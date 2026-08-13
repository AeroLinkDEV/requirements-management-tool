// Builds and validates one bounded CI metrics fragment.
//
// The fragment is CI-only telemetry. It deliberately carries no environment values, command output, cookies,
// headers, request bodies, or file contents; every field is a number, boolean, enum, or a bounded string
// that passed the secret scan below.

export const SCHEMA_VERSION = 'aerolink-ci-fragment/v1'
export const MAX_FRAGMENT_BYTES = 256 * 1024

const SECRET_PATTERNS = [
  /password/i,
  /passwd/i,
  /secret/i,
  /token/i,
  /authorization/i,
  /cookie/i,
  /connectionstrings?/i,
  /apikey/i,
  /privatekey/i,
  /begin (rsa |ec |openssh )?private key/i,
]

export function looksLikeSecret(value) {
  if (typeof value !== 'string') return false
  return SECRET_PATTERNS.some((pattern) => pattern.test(value))
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
    durationMs: optionalInt(item.durationMs) ?? 0,
    kind: item.kind === 'spec' ? 'spec' : 'class',
  }))
}

export function buildFragment({ run, job, timings, counts, slowest = [], flakyTests = [], cache, classification, missing = {}, result }) {
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
      id: boundedString(job.id, 100),
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
  const json = JSON.stringify(fragment)
  if (Buffer.byteLength(json, 'utf8') > MAX_FRAGMENT_BYTES) {
    // Bounded output is part of the contract. Drop the optional detail lists first; if still too large,
    // fail loudly so a schema/field change cannot silently ship unbounded telemetry.
    fragment.slowest = []
    fragment.flakyTests = []
    const retry = JSON.stringify(fragment)
    if (Buffer.byteLength(retry, 'utf8') > MAX_FRAGMENT_BYTES) {
      throw new Error('Metrics fragment exceeds the bounded size even without optional detail lists.')
    }
  }
  for (const [key, value] of Object.entries(flatten(fragment))) {
    if (looksLikeSecret(value)) {
      throw new Error(`Metrics fragment field "${key}" matches a secret pattern; refusing to publish it.`)
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

const REQUIRED_TOP_LEVEL = ['schemaVersion', 'run', 'job', 'timings', 'counts', 'cache', 'classification', 'missing']

export function validationErrors(fragment) {
  const errors = []
  if (fragment === null || typeof fragment !== 'object' || Array.isArray(fragment)) return ['Fragment is not an object.']
  for (const key of REQUIRED_TOP_LEVEL) if (!(key in fragment)) errors.push(`Missing top-level field "${key}".`)
  if (fragment.schemaVersion !== SCHEMA_VERSION) errors.push(`Unknown schema version "${fragment.schemaVersion}".`)
  if (!Number.isInteger(fragment.run?.id) || fragment.run.id < 1) errors.push('run.id must be a positive integer.')
  if (!/^[0-9a-f]{40}$/.test(fragment.run?.sha ?? '')) errors.push('run.sha must be a 40-character hex SHA.')
  if (!/^[0-9a-f]{40}$/.test(fragment.run?.tree ?? '')) errors.push('run.tree must be a 40-character hex SHA.')
  if (!['success', 'failure', 'cancelled', 'skipped', 'unavailable'].includes(fragment.job?.result)) errors.push('job.result is not a known outcome.')
  if (Buffer.byteLength(JSON.stringify(fragment), 'utf8') > MAX_FRAGMENT_BYTES) errors.push('Fragment exceeds the bounded size.')
  return errors
}

export function validateFragment(fragment) {
  const errors = validationErrors(fragment)
  if (errors.length > 0) throw new Error(`Invalid metrics fragment: ${errors.join('; ')}`)
  return true
}
