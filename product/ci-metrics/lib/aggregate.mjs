// Aggregates one workflow run's job fragments into a bounded merged record and a concise Markdown summary.
//
// Missing data is represented as missing with a reason. Nothing here ever interprets "no fragment" as a
// zero-duration success, and no value from a fragment is used as a command, path, expression, or script.

import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { validationErrors, MAX_FRAGMENT_BYTES } from './fragment.mjs'

export const RUN_SCHEMA_VERSION = 'aerolink-ci-run/v1'

export function readFragments(directory) {
  const entries = []
  let files = []
  try {
    files = readdirSync(directory)
  } catch (error) {
    return { fragments: [], missing: [{ job: 'fragments-directory', reason: `Could not read fragment directory: ${error.message}` }] }
  }
  for (const file of files.filter((name) => name.endsWith('.json')).sort()) {
    const path = join(directory, file)
    let parsed
    try {
      const content = readFileSync(path, 'utf8')
      if (Buffer.byteLength(content, 'utf8') > MAX_FRAGMENT_BYTES) throw new Error('Fragment file exceeds the bounded size.')
      parsed = JSON.parse(content)
    } catch (error) {
      entries.push({ file, error: error.message, fragment: null })
      continue
    }
    const errors = validationErrors(parsed)
    if (errors.length > 0) {
      entries.push({ file, error: errors.join('; '), fragment: null })
      continue
    }
    entries.push({ file, error: null, fragment: parsed })
  }
  const fragments = entries.filter((entry) => entry.fragment !== null).map((entry) => entry.fragment)
  const missing = entries.filter((entry) => entry.fragment === null).map((entry) => ({
    job: entry.file.replace(/\.json$/, ''),
    reason: entry.error,
  }))
  return { fragments, missing }
}

function totalDuration(fragment) {
  const start = fragment.timings.jobStartMs
  const end = fragment.timings.jobEndMs
  return start !== null && end !== null && end >= start ? end - start : null
}

export function criticalPath(fragments) {
  if (fragments.length === 0) return { job: null, durationMs: null, path: [], reason: 'No fragments were available.' }
  const byId = new Map(fragments.map((fragment) => [fragment.job.id, fragment]))
  const duration = new Map(fragments.map((fragment) => [fragment.job.id, totalDuration(fragment)]))
  const best = new Map(fragments.map((fragment) => [fragment.job.id, 0]))
  const parent = new Map()
  const missingDuration = []

  const visit = (id, seen) => {
    const fragment = byId.get(id)
    if (!fragment) return 0
    if (seen.has(id)) return 0 // cycle guard; fragments should never form one
    seen.add(id)
    let longest = duration.get(id) ?? 0
    if (duration.get(id) === null) missingDuration.push(id)
    for (const need of fragment.job.needs) {
      const childBest = visit(need, seen)
      if (childBest + (duration.get(id) ?? 0) > longest) {
        longest = childBest + (duration.get(id) ?? 0)
        parent.set(id, need)
      }
    }
    best.set(id, longest)
    seen.delete(id)
    return longest
  }

  let critical = null
  for (const fragment of fragments) {
    const candidate = visit(fragment.job.id, new Set())
    if (critical === null || candidate > critical.durationMs) critical = { id: fragment.job.id, durationMs: candidate }
  }

  const path = []
  const visited = new Set()
  let cursor = critical?.id
  while (cursor && !visited.has(cursor)) {
    visited.add(cursor)
    path.unshift(cursor)
    cursor = parent.get(cursor)
  }
  return {
    job: critical?.id ?? null,
    durationMs: critical?.durationMs ?? null,
    path,
    missingDuration: [...new Set(missingDuration)],
  }
}

export function aggregateFragments({ fragments, missing = [], runMeta = null }) {
  const byJob = new Map(fragments.map((fragment) => [fragment.job.id, fragment]))
  const path = criticalPath(fragments)
  const cache = { nuget: { hit: 0, miss: 0 }, npm: { hit: 0, miss: 0 }, chromium: { hit: 0, miss: 0 } }
  const flakyTests = []
  const countSummary = { expected: null, executed: null, passed: null, failed: null, skipped: null, flaky: null, sourcedJobs: 0 }
  for (const fragment of fragments) {
    for (const kind of ['nuget', 'npm', 'chromium']) {
      if (fragment.cache[kind] === 'hit') cache[kind].hit += 1
      if (fragment.cache[kind] === 'miss') cache[kind].miss += 1
    }
    for (const title of fragment.flakyTests ?? []) if (!flakyTests.includes(title)) flakyTests.push(title)
    if (fragment.counts.source) {
      countSummary.sourcedJobs += 1
      for (const key of ['expected', 'executed', 'passed', 'failed', 'skipped', 'flaky']) {
        if (fragment.counts[key] !== null) countSummary[key] = (countSummary[key] ?? 0) + fragment.counts[key]
      }
    }
  }

  const queueDelayMs = runMeta?.queueDelayMs ?? null
  const run = fragments[0]?.run ?? null
  const merged = {
    schemaVersion: RUN_SCHEMA_VERSION,
    run: run
      ? {
          id: run.id,
          attempt: run.attempt,
          event: run.event,
          sha: run.sha,
          tree: run.tree,
          ref: run.ref,
          pr: run.pr,
          workflow: run.workflow,
          repository: run.repository,
        }
      : null,
    jobs: [...byJob.values()].map((fragment) => ({
      id: fragment.job.id,
      name: fragment.job.name,
      matrix: fragment.job.matrix,
      needs: fragment.job.needs,
      result: fragment.job.result,
      timings: fragment.timings,
      counts: fragment.counts,
      cache: fragment.cache,
      classification: fragment.classification,
    })),
    criticalPath: path,
    queue: {
      delayMs: queueDelayMs,
      unavailableReason: queueDelayMs === null && runMeta === null
        ? 'GitHub API queue timing is collected by the rolling collector (phase B).'
        : queueDelayMs === null ? 'runMeta did not include queueDelayMs.' : null,
    },
    counts: countSummary,
    cache,
    flakyTests: flakyTests.slice(0, 20),
    missing: missing.map((entry) => ({ job: entry.job, reason: entry.reason })),
    classifications: {
      docsOnly: fragments.filter((f) => f.classification?.docsOnly === true).length,
      backend: fragments.filter((f) => f.classification?.backend === true).length,
      client: fragments.filter((f) => f.classification?.client === true).length,
      browser: fragments.filter((f) => f.classification?.browser === true).length,
      postgresql: fragments.filter((f) => f.classification?.postgresql === true).length,
      unavailable: fragments.filter((f) => f.classification?.unavailable === true).length,
    },
  }
  return merged
}

export function renderMarkdown(merged) {
  const lines = []
  lines.push('# CI run metrics')
  lines.push('')
  lines.push(`- Run: ${merged.run ? `#${merged.run.id} (attempt ${merged.run.attempt}, ${merged.run.event})` : 'unavailable'}`)
  lines.push(`- Repository: ${merged.run?.repository ?? 'unavailable'}`)
  lines.push(`- Tested tree: ${merged.run?.tree ? `\`${merged.run.tree}\`` : 'unavailable'}`)
  lines.push(`- Fragments: ${merged.jobs.length} valid, ${merged.missing.length} missing/unreadable`)
  const critical = merged.criticalPath
  lines.push(`- Critical path: ${critical.job ?? 'unavailable'} (${critical.durationMs === null ? 'unknown duration' : `${Math.round(critical.durationMs / 1000)}s`})`)
  lines.push('')
  lines.push('## Jobs')
  lines.push('')
  lines.push('| Job | Result | Total | Setup | Test | Upload/cleanup | Counts |')
  lines.push('|---|---|---|---|---|---|---|')
  for (const job of merged.jobs) {
    const seconds = (ms) => (ms === null ? '—' : `${(ms / 1000).toFixed(1)}s`)
    const counts = job.counts.source ? `${job.counts.executed ?? '?'}/${job.counts.expected ?? '?'}` : '—'
    lines.push(`| ${job.name} | ${job.result} | ${seconds(job.timings.jobStartMs !== null && job.timings.jobEndMs !== null ? job.timings.jobEndMs - job.timings.jobStartMs : null)} | ${seconds(job.timings.setupMs)} | ${seconds(job.timings.testMs)} | ${seconds(job.timings.uploadAndCleanupMs)} | ${counts} |`)
  }
  lines.push('')
  if (merged.missing.length > 0) {
    lines.push('## Missing data')
    lines.push('')
    for (const entry of merged.missing) lines.push(`- ${entry.job}: ${entry.reason}`)
    lines.push('')
  }
  if (merged.queue.unavailableReason) {
    lines.push(`## Queue timing\n\n${merged.queue.unavailableReason}\n`)
  }
  if (merged.flakyTests.length > 0) {
    lines.push('## Flaky / retried browser tests')
    lines.push('')
    for (const title of merged.flakyTests) lines.push(`- ${title}`)
    lines.push('')
  }
  if (merged.cache.nuget.hit + merged.cache.nuget.miss + merged.cache.npm.hit + merged.cache.npm.miss + merged.cache.chromium.hit + merged.cache.chromium.miss > 0) {
    lines.push(`## Caches\n\n- NuGet: ${merged.cache.nuget.hit} hit / ${merged.cache.nuget.miss} miss\n- npm: ${merged.cache.npm.hit} hit / ${merged.cache.npm.miss} miss\n- Chromium: ${merged.cache.chromium.hit} hit / ${merged.cache.chromium.miss} miss\n`)
  }
  return lines.join('\n')
}
