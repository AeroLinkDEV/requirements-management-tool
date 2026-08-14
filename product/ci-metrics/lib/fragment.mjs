// Builds and validates one bounded CI metrics fragment.
//
// The fragment is CI-only telemetry. It deliberately carries no environment values, command output, cookies,
// headers, request bodies, or file contents. Validation is driven by the checked-in JSON Schema so the
// contract cannot drift from the code that consumes it.

import { validateAgainstSchema, schema } from './schema-validate.mjs'

export const SCHEMA_VERSION = 'aerolink-ci-fragment/v1'
export const MAX_FRAGMENT_BYTES = 256 * 1024

// Bare keyword scanning would reject legitimate AeroLink test/class names ("Password visibility test",
// "token refresh", "cookie consent"). These patterns only match credential-shaped values, which must never
// appear in telemetry regardless of the field that carries them.
const CREDENTIAL_VALUE_PATTERNS = [
  /\b(password|passwd|pwd|secret|api[_-]?key|authorization|connectionstring)\s*[:=]\s*\S+/i,
  /\bbearer\s+[A-Za-z0-9._~+/=-]{12,}/i,
  /begin (rsa |ec |openssh )?private key/i,
  /(host|server)\s*=\s*\S+.*(user\s*id|username|password)\s*=/i,
]

export function looksLikeCredential(value) {
  if (typeof value !== 'string') return false
  return CREDENTIAL_VALUE_PATTERNS.some((pattern) => pattern.test(value))
}

export function boundedString(value, maxLength, fallback = '') {
  if (typeof value !== 'string') return fallback
  return value.slice(0, maxLength)
}

export function optionalInt(value) {
  if (value === null || value === undefined || value === '') return null
  const number = Number(value)
  return Number.isInteger(number) && number >= 0 ? number : null
}

export function optionalBool(value) {
  if (value === null || value === undefined || value === '') return null
  if (value === true || value === 'true') return true
  if (value === false || value === 'false') return false
  return null
}

function trimList(items, max) {
  if (!Array.isArray(items)) return []
  return items.slice(0, max).map((item) => ({
    name: boundedString(item.name, 300),
    durationMs: optionalInt(item.durationMs),
    kind: item.kind === 'spec' ? 'spec' : 'class',
  }))
}

export function buildFragment({ run, job, timings, counts, slowest = [], flakyTests = [], flakyTitlesTruncated = false, cache, classification, missing = {}, result }) {
  const knownResults = ['success', 'failure', 'cancelled', 'skipped']
  const jobResult = knownResults.includes(result) ? result : knownResults.includes(job?.result) ? job.result : 'unavailable'
  const fragment = {
    schemaVersion: SCHEMA_VERSION,
    run: {
      id: optionalInt(run.id),
      attempt: optionalInt(run.attempt) ?? 1,
      event: boundedString(run.event, 50),
      sha: boundedString(run.sha, 40),
      tree: boundedString(run.tree, 40),
      ref: boundedString(run.ref, 200),
      pr: optionalInt(run.pr),
      baseSha: run.baseSha ? boundedString(run.baseSha, 40) : null,
      headSha: run.headSha ? boundedString(run.headSha, 40) : null,
      workflow: boundedString(run.workflow, 200),
      workflowRef: boundedString(run.workflowRef, 300),
      repository: boundedString(run.repository, 200),
    },
    job: {
      group: boundedString(job.group || job.id, 100),
      instance: boundedString(job.instance || job.group || job.id, 120),
      name: boundedString(job.name, 200),
      matrix: job.matrix ?? null,
      needs: Array.isArray(job.needs) ? job.needs.slice(0, 12).map((n) => boundedString(n, 100)) : [],
      result: jobResult,
    },
    timings: {
      jobStartMs: optionalInt(timings.jobStartMs),
      setupEndMs: optionalInt(timings.setupEndMs),
      testEndMs: optionalInt(timings.testEndMs),
      jobEndMs: optionalInt(timings.jobEndMs),
      setupMs: optionalInt(timings.setupMs),
      testMs: optionalInt(timings.testMs),
      uploadAndCleanupMs: optionalInt(timings.uploadAndCleanupMs),
      missing: sanitiseMissing(timings.missing),
    },
    counts: {
      expected: optionalInt(counts.expected),
      executed: optionalInt(counts.executed),
      passed: optionalInt(counts.passed),
      failed: optionalInt(counts.failed),
      skipped: optionalInt(counts.skipped),
      flaky: optionalInt(counts.flaky),
      source: counts.source ?? null,
      missing: counts.missing ? boundedString(counts.missing, 300) : null,
    },
    slowest: trimList(slowest, 50),
    flakyTests: (Array.isArray(flakyTests) ? flakyTests : []).slice(0, 20).map((t) => boundedString(t, 400)),
    flakyTitlesTruncated: flakyTitlesTruncated === true,
    cache: {
      nuget: cache.nuget ?? null,
      npm: cache.npm ?? null,
      chromium: cache.chromium ?? null,
      missing: sanitiseMissing(cache.missing),
    },
    classification: {
      docsOnly: optionalBool(classification.docsOnly),
      backend: optionalBool(classification.backend),
      client: optionalBool(classification.client),
      browser: optionalBool(classification.browser),
      postgresql: optionalBool(classification.postgresql),
      unavailable: classification.unavailable === true,
    },
    missing: sanitiseMissing(missing),
  }
  return sanitiseFragment(fragment)
}

function sanitiseMissing(missing) {
  if (!missing || typeof missing !== 'object') return {}
  const clean = {}
  for (const [key, reason] of Object.entries(missing)) {
    clean[boundedString(key, 100)] = boundedString(reason, 300)
  }
  return clean
}

function sanitiseFragment(fragment) {
  let json = JSON.stringify(fragment)
  if (Buffer.byteLength(json, 'utf8') > MAX_FRAGMENT_BYTES) {
    fragment.slowest = []
    if (fragment.flakyTests.length > 0) fragment.flakyTitlesTruncated = true
    fragment.flakyTests = []
    json = JSON.stringify(fragment)
    if (Buffer.byteLength(json, 'utf8') > MAX_FRAGMENT_BYTES) {
      throw new Error('Metrics fragment exceeds the bounded size even without optional detail lists.')
    }
  }
  for (const [key, value] of Object.entries(flatten(fragment))) {
    if (looksLikeCredential(value)) {
      throw new Error(`Metrics fragment field "${key}" matches a credential-value pattern; refusing to publish it.`)
    }
  }
  return fragment
}

function flatten(value, prefix = '', out = {}) {
  if (value === null || typeof value !== 'object') {
    if (value !== undefined) out[prefix || '(root)'] = value
    return out
  }
  for (const [key, child] of Object.entries(value)) flatten(child, prefix ? `${prefix}.${key}` : key, out)
  return out
}

export function validationErrors(fragment) {
  const errors = []
  if (fragment === null || typeof fragment !== 'object' || Array.isArray(fragment)) return ['Fragment is not an object.']
  errors.push(...validateAgainstSchema(fragment, schema))
  // The builder refuses credential-shaped values, but fragments are read back from untrusted artifacts.
  // Re-apply the same guard when validating anything that came from disk.
  for (const [key, value] of Object.entries(flatten(fragment))) {
    if (looksLikeCredential(value)) errors.push(`Field "${key}" matches a credential-value pattern; refusing to publish it.`)
  }
  const timings = fragment.timings ?? {}
  if (timings.jobStartMs !== null && timings.setupEndMs !== null && timings.setupEndMs < timings.jobStartMs) {
    errors.push('timings: setupEndMs precedes jobStartMs.')
  }
  if (timings.setupEndMs !== null && timings.testEndMs !== null && timings.testEndMs < timings.setupEndMs) {
    errors.push('timings: testEndMs precedes setupEndMs.')
  }
  if (timings.testEndMs !== null && timings.jobEndMs !== null && timings.jobEndMs < timings.testEndMs) {
    errors.push('timings: jobEndMs precedes testEndMs.')
  }
  const derived = [
    [timings.setupMs, timings.setupEndMs, timings.jobStartMs],
    [timings.testMs, timings.testEndMs, timings.setupEndMs],
    [timings.uploadAndCleanupMs, timings.jobEndMs, timings.testEndMs],
  ]
  for (const [value, later, earlier] of derived) {
    if (value !== null && (later === null || earlier === null || value !== later - earlier)) {
      errors.push('timings: a derived duration does not match its raw markers.')
    }
  }
  const counts = fragment.counts ?? {}
  if (counts.source === 'playwright-json') {
    const required = [counts.expected, counts.executed, counts.passed, counts.failed, counts.skipped, counts.flaky]
    if (required.some((value) => value === null)) {
      errors.push('counts: playwright-json requires expected/executed/passed/failed/skipped/flaky.')
    } else {
      if (counts.expected !== counts.executed + counts.skipped) errors.push('counts: expected must equal executed + skipped for playwright-json.')
      if (counts.executed !== counts.passed + counts.failed) errors.push('counts: executed must equal passed + failed for playwright-json.')
      if (counts.flaky > counts.passed) errors.push('counts: flaky cannot exceed passed for playwright-json.')
      const hasTitles = Array.isArray(fragment.flakyTests) && fragment.flakyTests.length > 0
      const reason = counts.missing ?? ''
      if (counts.flaky > 0 && !hasTitles && !/(title|flaky|detail|spec)/i.test(reason)) {
        errors.push('counts: a Playwright flaky count requires flaky title evidence or an explicit unavailable/truncated reason.')
      }
    }
  }
  if (counts.source === 'trx') {
    const required = [counts.expected, counts.executed, counts.passed, counts.failed, counts.skipped]
    if (required.some((value) => value === null)) {
      errors.push('counts: TRX requires expected/executed/passed/failed/skipped.')
    } else {
      if (counts.executed + counts.skipped > counts.expected) errors.push('counts: executed + skipped cannot exceed expected for TRX.')
      if (counts.passed + counts.failed > counts.executed) errors.push('counts: passed + failed cannot exceed executed for TRX.')
    }
  }
  if (Buffer.byteLength(JSON.stringify(fragment), 'utf8') > MAX_FRAGMENT_BYTES) errors.push('Fragment exceeds the bounded size.')
  return errors
}

export function validateFragment(fragment) {
  const errors = validationErrors(fragment)
  if (errors.length > 0) throw new Error(`Invalid metrics fragment: ${errors.join('; ')}`)
  return true
}
