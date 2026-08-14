// Reads the job's timing markers, structured test outputs, and GitHub context, then writes one bounded
// fragment. Runs with `if: always()` so failures and cancellations are still observable.

import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import { execFileSync } from 'node:child_process'
import { buildFragment, validateFragment } from '../lib/fragment.mjs'
import { parseTrx, classDurations } from '../lib/trx.mjs'
import { parsePlaywrightJson, specDurations } from '../lib/playwright.mjs'

function env(name) {
  return process.env[name] ?? ''
}

function optional(name) {
  const value = env(name)
  return value === '' ? null : value
}

function readTimings() {
  const timingFile = env('METRICS_TIMING_FILE')
  if (!timingFile || !existsSync(timingFile)) {
    return { markers: [], reason: 'METRICS_TIMING_FILE was not set or the timing file was not produced.' }
  }
  try {
    const markers = readFileSync(timingFile, 'utf8').split('\n').filter(Boolean).map((line) => JSON.parse(line))
    return { markers, reason: null }
  } catch (error) {
    return { markers: [], reason: `Timing file could not be parsed: ${error.message}` }
  }
}

function readTreeSha() {
  try {
    return execFileSync('git', ['rev-parse', 'HEAD^{tree}'], { encoding: 'utf8', cwd: env('GITHUB_WORKSPACE') || undefined }).trim()
  } catch {
    return null
  }
}

function readEventContext() {
  const eventPath = env('GITHUB_EVENT_PATH')
  if (!eventPath || !existsSync(eventPath)) return { pr: null, baseSha: null, headSha: null }
  try {
    const event = JSON.parse(readFileSync(eventPath, 'utf8'))
    return {
      pr: event.pull_request?.number ?? null,
      baseSha: event.pull_request?.base?.sha ?? null,
      headSha: event.pull_request?.head?.sha ?? null,
    }
  } catch {
    return { pr: null, baseSha: null, headSha: null }
  }
}

function parseCounts() {
  const source = env('METRICS_COUNTS_SOURCE')
  if (source === 'trx') {
    const path = env('METRICS_TRX_PATH')
    if (!path || !existsSync(path)) return { counts: null, reason: 'METRICS_TRX_PATH was set but the TRX file is missing.' }
    try {
      const { totals, tests } = parseTrx(readFileSync(path, 'utf8'))
      return {
        counts: {
          expected: totals.total,
          executed: totals.executed,
          passed: totals.passed,
          failed: totals.failed,
          skipped: totals.skipped,
          flaky: null,
          source: 'trx',
          missing: null,
        },
        slowest: classDurations(tests),
        flakyTests: [],
      }
    } catch (error) {
      return { counts: null, reason: `TRX parse failed: ${error.message}` }
    }
  }
  if (source === 'playwright-json') {
    const path = env('METRICS_PLAYWRIGHT_JSON_PATH')
    if (!path || !existsSync(path)) return { counts: null, reason: 'METRICS_PLAYWRIGHT_JSON_PATH was set but the report file is missing.' }
    try {
      const parsed = parsePlaywrightJson(readFileSync(path, 'utf8'))
      // Planned/executed/passed semantics: a skipped test is planned but never executed; a retry-pass is a
      // final pass and is counted in flaky as well as in the final-pass total.
      return {
        counts: {
          expected: parsed.totals.planned,
          executed: parsed.totals.executed,
          passed: parsed.totals.passed,
          failed: parsed.totals.unexpected,
          skipped: parsed.totals.skipped,
          flaky: parsed.totals.flaky,
          source: 'playwright-json',
          missing: null,
        },
        slowest: specDurations(parsed.tests),
        flakyTests: parsed.flakyTitles,
      }
    } catch (error) {
      return { counts: null, reason: `Playwright JSON parse failed: ${error.message}` }
    }
  }
  return { counts: null, reason: source ? `Unknown METRICS_COUNTS_SOURCE "${source}".` : 'This job has no structured test output.' }
}

function main() {
  const timings = readTimings()
  const markers = new Map(timings.markers.map((marker) => [marker.name, marker.at]))
  const jobStartMs = markers.get('job-start') ?? null
  let setupEndMs = markers.get('setup-end') ?? null
  let testEndMs = markers.get('test-end') ?? null
  let jobEndMs = Date.now()

  const timingMissing = {}
  if (timings.reason) timingMissing.markers = timings.reason
  if (jobStartMs === null) timingMissing.jobStartMs = 'job-start marker missing; timings unavailable.'
  if (setupEndMs === null) timingMissing.setupEndMs = 'setup-end marker missing; setup/build duration unavailable.'
  if (testEndMs === null) timingMissing.testEndMs = 'test-end marker missing; test duration unavailable.'

  let setupMs = null
  let testMs = null
  let uploadAndCleanupMs = null
  if (setupEndMs !== null && jobStartMs !== null && setupEndMs < jobStartMs) {
    timingMissing.setupEndMs = 'setup-end marker precedes job-start; duration treated as unavailable.'
    setupEndMs = null
  } else if (setupEndMs !== null && jobStartMs !== null) {
    setupMs = setupEndMs - jobStartMs
  }
  if (testEndMs !== null && setupEndMs !== null && testEndMs < setupEndMs) {
    timingMissing.testEndMs = 'test-end marker precedes setup-end; duration treated as unavailable.'
    testEndMs = null
  } else if (testEndMs !== null && setupEndMs !== null) {
    testMs = testEndMs - setupEndMs
  }
  if (jobEndMs !== null && testEndMs !== null && jobEndMs < testEndMs) {
    timingMissing.jobEndMs = 'job-end marker precedes test-end; duration treated as unavailable.'
    jobEndMs = null
  } else if (jobEndMs !== null && testEndMs !== null) {
    uploadAndCleanupMs = jobEndMs - testEndMs
  }

  const parsed = parseCounts()
  const counts = parsed.counts ?? { expected: null, executed: null, passed: null, failed: null, skipped: null, flaky: null, source: null, missing: null }
  if (parsed.reason) counts.missing = parsed.reason

  const event = readEventContext()
  const tree = readTreeSha()
  const runMissing = {}
  if (!tree) runMissing.tree = 'git rev-parse HEAD^{tree} failed; exact tested tree unavailable.'
  if (!env('GITHUB_RUN_ID')) runMissing.runId = 'GITHUB_RUN_ID is not set.'

  const classificationUnavailable = !['true', 'false'].includes(env('METRICS_CLASS_DOCS_ONLY'))
  const classification = {
    docsOnly: env('METRICS_CLASS_DOCS_ONLY') === 'true' ? true : env('METRICS_CLASS_DOCS_ONLY') === 'false' ? false : null,
    backend: env('METRICS_CLASS_BACKEND') === 'true' ? true : env('METRICS_CLASS_BACKEND') === 'false' ? false : null,
    client: env('METRICS_CLASS_CLIENT') === 'true' ? true : env('METRICS_CLASS_CLIENT') === 'false' ? false : null,
    browser: env('METRICS_CLASS_BROWSER') === 'true' ? true : env('METRICS_CLASS_BROWSER') === 'false' ? false : null,
    postgresql: env('METRICS_CLASS_POSTGRESQL') === 'true' ? true : env('METRICS_CLASS_POSTGRESQL') === 'false' ? false : null,
    unavailable: classificationUnavailable,
  }

  const cacheMissing = {}
  const cache = {
    nuget: optional('METRICS_CACHE_NUGET'),
    npm: optional('METRICS_CACHE_NPM'),
    chromium: optional('METRICS_CACHE_CHROMIUM'),
    missing: cacheMissing,
  }
  if (cache.nuget === null) cacheMissing.nuget = 'Cache step output not wired for this job.'
  if (cache.npm === null) cacheMissing.npm = 'Cache step output not wired for this job.'
  if (cache.chromium === null) cacheMissing.chromium = 'Cache step output not wired for this job.'

  let matrix = null
  if (optional('METRICS_MATRIX')) {
    try {
      matrix = JSON.parse(env('METRICS_MATRIX'))
    } catch {
      matrix = null
    }
  }
  const group = env('METRICS_JOB_GROUP') || env('METRICS_JOB_ID') || env('GITHUB_JOB')
  const explicitInstance = optional('METRICS_JOB_INSTANCE')
  const instance = explicitInstance
    || (matrix?.shard !== undefined ? `${group}-${matrix.shard}` : group)

  const fragment = buildFragment({
    run: {
      id: env('GITHUB_RUN_ID'),
      attempt: env('GITHUB_RUN_ATTEMPT'),
      event: env('GITHUB_EVENT_NAME'),
      sha: env('GITHUB_SHA'),
      tree,
      ref: env('GITHUB_REF'),
      pr: event.pr,
      baseSha: event.baseSha,
      headSha: event.headSha,
      workflow: env('GITHUB_WORKFLOW'),
      workflowRef: env('GITHUB_WORKFLOW_REF'),
      repository: env('GITHUB_REPOSITORY'),
    },
    job: {
      group,
      instance,
      name: env('METRICS_JOB_NAME') || env('GITHUB_JOB'),
      matrix,
      needs: optional('METRICS_NEEDS') ? env('METRICS_NEEDS').split(',').map((n) => n.trim()).filter(Boolean) : [],
      result: env('METRICS_JOB_RESULT') || 'unavailable',
    },
    timings: {
      jobStartMs,
      setupEndMs,
      testEndMs,
      jobEndMs,
      setupMs,
      testMs,
      uploadAndCleanupMs,
      missing: timingMissing,
    },
    counts,
    slowest: parsed.slowest ?? [],
    flakyTests: parsed.flakyTests ?? [],
    cache,
    classification,
    missing: runMissing,
  })

  try {
    validateFragment(fragment)
  } catch (error) {
    console.error(`[ci-metrics] Fragment failed validation and was not published: ${error.message}`)
    process.exit(0)
  }

  const outputPath = env('METRICS_FRAGMENT_PATH')
  if (!outputPath) {
    console.error('[ci-metrics] METRICS_FRAGMENT_PATH is not set; fragment was not written.')
    process.exit(0)
  }
  try {
    mkdirSync(dirname(outputPath), { recursive: true })
    writeFileSync(outputPath, `${JSON.stringify(fragment, null, 2)}\n`, 'utf8')
    console.log(`[ci-metrics] Wrote fragment for ${fragment.job.name} (result ${fragment.job.result}).`)
  } catch (error) {
    console.error(`[ci-metrics] Could not write fragment: ${error.message}`)
  }
  process.exit(0)
}

main()
