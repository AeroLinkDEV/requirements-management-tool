// Authenticated API observation records for the #942 investigation.
//
// GitHub metadata and runner-produced artifacts have different trust boundaries.  This module keeps the
// distinction explicit, parses only the small set of fields needed for scheduling evidence, and refuses to
// derive a complete inventory from a partial or contradictory shard set.

import { looksLikeCredential, validateFragment } from './fragment.mjs'
import { parseTrx, classDurations } from './trx.mjs'
import { buildCurrentCountPlan, normalizeApiDiscovery } from './api-packing-shadow.mjs'

export const API_OBSERVATION_ARTIFACT_SCHEMA = 'aerolink-api-observation-artifact/v1'
export const API_OBSERVATION_REPORT_SCHEMA = 'aerolink-api-observations/v1'
export const API_REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
export const API_WORKFLOW_PATH = '.github/workflows/ci.yml'
export const API_WORKFLOW_NAME = 'Product quality gate'
export const API_SHARD_COUNT = 3
export const MAX_OBSERVATION_RUNS = 40
export const MAX_RESULT_ROWS = 30_000
export const MAX_EXCLUSIONS = 200

const SHA40 = /^[0-9a-f]{40}$/i
const SHA64 = /^[0-9a-f]{64}$/i
const MAX_TIMESTAMP_MS = 10_000_000_000_000

// One maintained API test displays two deliberately harmless password arguments in its fully-qualified
// identity. Preserve that identity for exact inventory/TRX reconciliation while continuing to reject every
// other credential-shaped value before it reaches an observation artifact.
const SAFE_PARAMETERIZED_TEST_IDENTITIES = new Set([
  'AeroLink.Api.Tests.TestChangeRequestReviewWorkflowTests.Missing_or_incorrect_password_refuses_signature_without_any_partial_transition(password: null)',
  'AeroLink.Api.Tests.TestChangeRequestReviewWorkflowTests.Missing_or_incorrect_password_refuses_signature_without_any_partial_transition(password: "not-the-current-password")',
])

export function looksLikeObservationCredential(value) {
  return looksLikeCredential(value) && !SAFE_PARAMETERIZED_TEST_IDENTITIES.has(value)
}

function object(value, label) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label} must be an object.`)
  return value
}

function boundedText(value, label, max = 300, rejectCredential = true) {
  if (typeof value !== 'string' || value.length === 0 || value.length > max || /[\r\n]/.test(value)) throw new Error(`${label} must be a bounded non-empty string.`)
  if (rejectCredential && looksLikeCredential(value)) throw new Error(`${label} contains a credential-shaped value.`)
  return value
}

function boundedString(value, label, max = 300) {
  return boundedText(value, label, max, true)
}

/** Decode one XML attribute layer emitted by the maintained TRX parser. */
export function decodeXmlAttribute(value, label = 'XML attribute') {
  if (typeof value !== 'string') throw new Error(`${label} must be text.`)
  const entities = [...value.matchAll(/&[^;\r\n]*;/g)].map((match) => match[0])
  let invalid = false
  for (const entity of entities) {
    const named = new Set(['&quot;', '&apos;', '&amp;', '&lt;', '&gt;'])
    if (entity.length > 82) { invalid = true; continue }
    if (named.has(entity.toLowerCase())) continue
    const hex = /^&#x([0-9a-f]+);$/i.exec(entity)
    const decimal = /^&#(\d+);$/.exec(entity)
    const codePoint = Number.parseInt(hex?.[1] ?? decimal?.[1] ?? '', hex ? 16 : 10)
    if (!hex && !decimal || !Number.isSafeInteger(codePoint) || codePoint === 0 || codePoint > 0x10ffff || (codePoint >= 0xd800 && codePoint <= 0xdfff)) invalid = true
  }
  const decoded = value.replace(/&(?:quot|apos|amp|lt|gt|#x[0-9a-f]+|#\d+);/gi, (entity) => {
    const named = { '&quot;': '"', '&apos;': "'", '&amp;': '&', '&lt;': '<', '&gt;': '>' }[entity.toLowerCase()]
    if (named !== undefined) return named
    const hex = /^&#x([0-9a-f]+);$/i.exec(entity)
    const decimal = /^&#(\d+);$/.exec(entity)
    const codePoint = Number.parseInt(hex?.[1] ?? decimal?.[1] ?? '', hex ? 16 : 10)
    if (!Number.isSafeInteger(codePoint) || codePoint === 0 || codePoint > 0x10ffff || (codePoint >= 0xd800 && codePoint <= 0xdfff)) {
      invalid = true
      return ''
    }
    return String.fromCodePoint(codePoint)
  })
  if (invalid) throw new Error(`${label} contains an unsupported XML entity.`)
  return decoded
}

function decodedBoundedString(value, label, max = 300) {
  // Check the credential shape after decoding so XML entities cannot evade the guard, while allowing the
  // two reviewed harmless password display values used by the maintained parameterized test identities.
  const decoded = decodeXmlAttribute(boundedText(value, label, max, false), label)
  if (decoded.length === 0 || decoded.length > max || /[\r\n]/.test(decoded) || looksLikeObservationCredential(decoded)) throw new Error(`${label} contains an invalid decoded value.`)
  return decoded
}

function optionalString(value, label, max = 300) {
  if (value === null || value === undefined || value === '') return null
  return boundedString(value, label, max)
}

function sha(value, label, length = 40) {
  const expression = length === 40 ? SHA40 : SHA64
  if (typeof value !== 'string' || !expression.test(value)) throw new Error(`${label} must be a ${length}-character hexadecimal SHA.`)
  return value.toLowerCase()
}

function positiveInt(value, label, max = Number.MAX_SAFE_INTEGER) {
  if (!Number.isSafeInteger(value) || value < 1 || value > max) throw new Error(`${label} must be a safe positive integer.`)
  return value
}

function nonNegativeInt(value, label, max = Number.MAX_SAFE_INTEGER) {
  if (!Number.isSafeInteger(value) || value < 0 || value > max) throw new Error(`${label} must be a bounded non-negative integer.`)
  return value
}

function optionalTimestampMs(value, label) {
  if (value === null || value === undefined) return null
  return nonNegativeInt(value, label, MAX_TIMESTAMP_MS)
}

function sortedUnique(values, label, max = MAX_RESULT_ROWS) {
  if (!Array.isArray(values) || values.length > max) throw new Error(`${label} must be an array of at most ${max} entries.`)
  const seen = new Set()
  for (const value of values) {
    if (typeof value !== 'string' || value.length === 0 || value.length > 1_000 || /[\r\n]/.test(value)) throw new Error(`${label} contains an invalid identity.`)
    if (looksLikeObservationCredential(value)) throw new Error(`${label} contains a credential-shaped identity.`)
    if (seen.has(value)) throw new Error(`${label} contains duplicate identity '${value}'.`)
    seen.add(value)
  }
  // Match the maintained plan's code-unit ordering (`<`), rather than locale-sensitive
  // ordering. Parameterized display names can contain punctuation/case where localeCompare
  // would reorder an otherwise exact current partition.
  return [...seen].sort((a, b) => a < b ? -1 : a > b ? 1 : 0)
}

function validateTiming(timing) {
  const value = timing === null || timing === undefined ? {} : object(timing, 'artifact.timing')
  const result = {
    jobStartMs: optionalTimestampMs(value.jobStartMs, 'artifact.timing.jobStartMs'),
    setupEndMs: optionalTimestampMs(value.setupEndMs, 'artifact.timing.setupEndMs'),
    testEndMs: optionalTimestampMs(value.testEndMs, 'artifact.timing.testEndMs'),
    capturedAtMs: optionalTimestampMs(value.capturedAtMs, 'artifact.timing.capturedAtMs'),
  }
  const missing = []
  if (result.jobStartMs === null) missing.push('job-start marker missing')
  if (result.setupEndMs === null) missing.push('setup-end marker missing')
  if (result.testEndMs === null) missing.push('test-end marker missing')
  if (result.capturedAtMs === null) missing.push('capture timestamp missing')
  if (result.jobStartMs !== null && result.setupEndMs !== null && result.setupEndMs < result.jobStartMs) missing.push('setup-end precedes job-start')
  if (result.setupEndMs !== null && result.testEndMs !== null && result.testEndMs < result.setupEndMs) missing.push('test-end precedes setup-end')
  if (result.testEndMs !== null && result.capturedAtMs !== null && result.capturedAtMs < result.testEndMs) missing.push('capture precedes test-end')
  return { ...result, missing }
}

function validateToolchain(raw) {
  const value = raw === null || raw === undefined ? {} : object(raw, 'artifact.toolchain')
  return {
    runnerOs: optionalString(value.runnerOs, 'artifact.toolchain.runnerOs', 80),
    image: optionalString(value.image, 'artifact.toolchain.image', 160),
    dotnet: optionalString(value.dotnet, 'artifact.toolchain.dotnet', 80),
  }
}

function validateRuntimeConstraints(raw) {
  const value = raw === null || raw === undefined ? {} : object(raw, 'artifact.constraints')
  const collectionTopology = value.collectionTopology ?? 'unknown-unobserved'
  if (collectionTopology !== 'unknown-unobserved') throw new Error('artifact.constraints.collectionTopology must remain unknown-unobserved for VSTest observations.')
  return {
    collectionTopology,
    reason: optionalString(value.reason, 'artifact.constraints.reason', 400) ?? 'VSTest output does not expose xUnit collection or fixture topology; no grouping claim is made.',
  }
}

function shardPlanShape(plan, shard) {
  if (!plan || typeof plan !== 'object' || !Array.isArray(plan.shards)) throw new Error('artifact.plan must contain a shard array.')
  if (plan.algorithm !== 'current-count-greedy' || plan.shardCount !== API_SHARD_COUNT || plan.shards.length !== API_SHARD_COUNT) {
    throw new Error('artifact.plan must be the current three-shard count plan.')
  }
  const selected = plan.shards.find((entry) => entry?.shard === shard)
  if (!selected || !Array.isArray(selected.classes) || !Array.isArray(selected.tests)) throw new Error(`artifact.plan has no valid shard ${shard}.`)
  const classes = sortedUnique(selected.classes, `artifact.plan.shard[${shard}].classes`, 10_000)
  const tests = sortedUnique(selected.tests, `artifact.plan.shard[${shard}].tests`)
  const filter = boundedString(selected.filter, `artifact.plan.shard[${shard}].filter`, 20_000)
  const filterClasses = filter.split('|').map((part) => {
    const prefix = 'FullyQualifiedName~'
    if (!part.startsWith(prefix) || !part.endsWith('.') || part.length <= prefix.length + 1) throw new Error(`artifact.plan shard ${shard} contains an invalid class filter.`)
    return part.slice(prefix.length, -1)
  })
  if (selected.caseCount !== tests.length || filterClasses.length !== classes.length || sortedUnique(filterClasses, `artifact.plan.shard[${shard}].filter classes`, 10_000).some((name, index) => name !== classes[index])) {
    throw new Error(`artifact.plan shard ${shard} has inconsistent count or filter.`)
  }
  return { shard: positiveInt(shard, 'artifact.shard', API_SHARD_COUNT), classes, tests, caseCount: tests.length, filter }
}

/** Validate the runner-produced artifact envelope, without asserting it is authenticated. */
export function normalizeApiObservationArtifact(input) {
  const value = object(input, 'artifact')
  if (value.schemaVersion !== API_OBSERVATION_ARTIFACT_SCHEMA) throw new Error(`artifact.schemaVersion must be ${API_OBSERVATION_ARTIFACT_SCHEMA}.`)
  if (value.repository !== API_REPOSITORY) throw new Error(`artifact.repository must be ${API_REPOSITORY}.`)
  const run = object(value.run, 'artifact.run')
  const runIdentity = {
    id: positiveInt(run.id, 'artifact.run.id'),
    attempt: positiveInt(run.attempt, 'artifact.run.attempt', 1000),
    event: boundedString(run.event, 'artifact.run.event', 50),
    sha: sha(run.sha, 'artifact.run.sha'),
    tree: sha(run.tree, 'artifact.run.tree'),
    workflow: boundedString(run.workflow, 'artifact.run.workflow', 200),
    workflowRef: boundedString(run.workflowRef, 'artifact.run.workflowRef', 300),
  }
  const shard = positiveInt(value.shard, 'artifact.shard', API_SHARD_COUNT)
  const shardCount = positiveInt(value.shardCount, 'artifact.shardCount', 16)
  if (shardCount !== API_SHARD_COUNT) throw new Error(`artifact.shardCount must be ${API_SHARD_COUNT}.`)
  const discovery = normalizeApiDiscovery(value.discovery)
  if (discovery.commitSha !== runIdentity.sha || discovery.treeSha !== runIdentity.tree) throw new Error('artifact.discovery identity does not match artifact.run.')
  const computedPlan = buildCurrentCountPlan(discovery, API_SHARD_COUNT)
  const selected = shardPlanShape(value.plan, shard)
  const expected = computedPlan.shards.find((entry) => entry.shard === shard)
  if (selected.caseCount !== expected.caseCount || selected.filter !== expected.filter || selected.tests.join('\n') !== expected.tests.join('\n')) {
    throw new Error(`artifact.plan shard ${shard} does not reproduce the current CI partition.`)
  }
  const actual = object(value.actual, 'artifact.actual')
  const actualFilter = boundedString(actual.filter, 'artifact.actual.filter', 20_000)
  const actualExpected = nonNegativeInt(actual.expected, 'artifact.actual.expected', MAX_RESULT_ROWS)
  if (actualFilter !== expected.filter || actualExpected !== expected.caseCount) throw new Error(`artifact.actual shard ${shard} does not match the current CI filter.`)
  return {
    schemaVersion: API_OBSERVATION_ARTIFACT_SCHEMA,
    repository: API_REPOSITORY,
    run: runIdentity,
    shard,
    shardCount,
    discovery,
    plan: computedPlan,
    actual: { filter: actualFilter, expected: actualExpected },
    timing: validateTiming(value.timing),
    toolchain: validateToolchain(value.toolchain),
    constraints: validateRuntimeConstraints(value.constraints),
  }
}

function jobIdentity(job) {
  const value = object(job, 'GitHub job')
  const id = positiveInt(value.id, 'GitHub job.id')
  const runId = positiveInt(value.run_id, 'GitHub job.run_id')
  const runAttempt = positiveInt(value.run_attempt ?? 1, 'GitHub job.run_attempt', 1000)
  return {
    id,
    runId,
    runAttempt,
    name: boundedString(value.name, `GitHub job ${id}.name`, 300),
    headSha: optionalString(value.head_sha, `GitHub job ${id}.head_sha`, 80),
    status: optionalString(value.status, `GitHub job ${id}.status`, 40),
    conclusion: optionalString(value.conclusion, `GitHub job ${id}.conclusion`, 40),
    startedAt: optionalString(value.started_at, `GitHub job ${id}.started_at`, 80),
    completedAt: optionalString(value.completed_at, `GitHub job ${id}.completed_at`, 80),
    runnerOs: optionalString(value.runner_os, `GitHub job ${id}.runner_os`, 80),
    runnerName: optionalString(value.runner_name, `GitHub job ${id}.runner_name`, 160),
    runnerGroup: optionalString(value.runner_group_name, `GitHub job ${id}.runner_group_name`, 160),
  }
}

function parseIsoTimestamp(value) {
  if (typeof value !== 'string' || value.length === 0) return null
  const parsed = Date.parse(value)
  return Number.isFinite(parsed) ? parsed : null
}

function normalizedAttemptMetadata(raw, runId) {
  const value = object(raw, 'GitHub workflow attempt')
  const id = positiveInt(value.id, 'GitHub workflow attempt.id')
  const attempt = positiveInt(value.run_attempt, 'GitHub workflow attempt.run_attempt', 1000)
  if (id !== runId) throw new Error(`GitHub workflow attempt ${attempt} belongs to another run.`)
  const startedAt = optionalString(value.run_started_at, `GitHub workflow attempt ${attempt}.run_started_at`, 80)
  const updatedAt = optionalString(value.updated_at, `GitHub workflow attempt ${attempt}.updated_at`, 80)
  const startMs = parseIsoTimestamp(startedAt)
  const endMs = parseIsoTimestamp(updatedAt)
  if (startMs === null || endMs === null || endMs < startMs) throw new Error(`GitHub workflow attempt ${attempt} has missing or reversed timestamps.`)
  return { id, attempt, startedAt, updatedAt, startMs, endMs, status: optionalString(value.status, `GitHub workflow attempt ${attempt}.status`, 40), conclusion: optionalString(value.conclusion, `GitHub workflow attempt ${attempt}.conclusion`, 40) }
}

function normalizeAttemptMetadata(attemptRuns, runId, runAttempt) {
  if (attemptRuns === undefined) return { supplied: false, complete: false, records: [], errors: [] }
  if (!Array.isArray(attemptRuns) || attemptRuns.length > 1000) return { supplied: true, complete: false, records: [], errors: ['Attempt metadata is not a bounded array.'] }
  const records = []
  const errors = []
  const seen = new Set()
  for (const raw of attemptRuns) {
    try {
      const record = normalizedAttemptMetadata(raw, runId)
      if (record.attempt > runAttempt) throw new Error(`Attempt ${record.attempt} is newer than the authenticated current attempt ${runAttempt}.`)
      if (seen.has(record.attempt)) throw new Error(`Attempt metadata repeats attempt ${record.attempt}.`)
      seen.add(record.attempt)
      records.push(record)
    } catch (error) {
      errors.push(String(error.message).slice(0, 300))
    }
  }
  for (let attempt = 1; attempt <= runAttempt; attempt += 1) if (!seen.has(attempt)) errors.push(`Authenticated attempt metadata is missing attempt ${attempt}.`)
  return { supplied: true, complete: errors.length === 0, records: records.sort((a, b) => a.attempt - b.attempt), errors }
}

function expectedFragmentResult(job) {
  if (job.conclusion === 'success') return 'success'
  if (job.conclusion === 'failure') return 'failure'
  if (job.conclusion === 'cancelled') return 'cancelled'
  if (job.conclusion === 'skipped') return 'skipped'
  return 'unavailable'
}

function reconcileApiFragment({ fragment, apiRun, apiJob, treeSha, shard, trx }) {
  try {
    validateFragment(fragment)
  } catch (error) {
    throw new Error(`Metrics fragment could not be validated: ${error.message}`)
  }
  const value = object(fragment, 'metrics fragment')
  if (value.schemaVersion !== 'aerolink-ci-fragment/v2') throw new Error('Metrics fragment schema is not the maintained v2 contract.')
  const fragmentRun = object(value.run, 'metrics fragment.run')
  if (fragmentRun.id !== apiRun.id || fragmentRun.attempt !== apiJob.runAttempt || fragmentRun.event !== apiRun.event || fragmentRun.sha !== apiRun.head_sha || fragmentRun.tree !== treeSha || fragmentRun.repository !== API_REPOSITORY) {
    throw new Error('Metrics fragment run identity does not match authenticated GitHub metadata.')
  }
  if (fragmentRun.workflow !== API_WORKFLOW_NAME || fragmentRun.workflowRef !== apiRun.workflow_ref) {
    throw new Error('Metrics fragment workflow identity does not match the authenticated run.')
  }
  const fragmentJob = object(value.job, 'metrics fragment.job')
  if (fragmentJob.group !== 'backend-api' || fragmentJob.instance !== `backend-api-${shard}` || fragmentJob.name !== apiJob.name) {
    throw new Error(`Metrics fragment job identity does not match API shard ${shard}.`)
  }
  if (fragmentJob.result !== expectedFragmentResult(apiJob)) throw new Error('Metrics fragment job outcome does not match authenticated GitHub job metadata.')
  const counts = object(value.counts, 'metrics fragment.counts')
  const expectedCounts = {
    expected: trx.totals.total,
    executed: trx.totals.executed,
    passed: trx.totals.passed,
    failed: trx.totals.failed,
    skipped: trx.totals.skipped,
  }
  for (const field of Object.keys(expectedCounts)) if (counts[field] !== expectedCounts[field]) throw new Error(`Metrics fragment ${field} count does not match reconciled TRX.`)
  return value
}

function sameNames(actual, expected, label) {
  const actualSorted = sortedUnique(actual, label)
  const expectedSorted = sortedUnique(expected, `${label} expected`)
  if (actualSorted.length !== expectedSorted.length || actualSorted.some((name, index) => name !== expectedSorted[index])) {
    const missing = expectedSorted.filter((name) => !actualSorted.includes(name)).slice(0, 20)
    const extra = actualSorted.filter((name) => !expectedSorted.includes(name)).slice(0, 20)
    throw new Error(`${label} does not reconcile (missing=${missing.join(',') || 'none'}; extra=${extra.join(',') || 'none'}).`)
  }
}

function normalizedResults(trx) {
  if (!trx || !Array.isArray(trx.tests) || trx.tests.length > MAX_RESULT_ROWS) throw new Error('TRX result rows are missing or exceed the bounded limit.')
  const tests = trx.tests.map((test, index) => ({
    className: decodedBoundedString(test.className, `TRX result ${index}.className`, 300),
    name: decodedBoundedString(test.name, `TRX result ${index}.name`, 1_000),
    outcome: decodedBoundedString(test.outcome, `TRX result ${index}.outcome`, 40),
    durationMs: test.durationMs === null ? null : nonNegativeInt(test.durationMs, `TRX result ${index}.durationMs`, 86_400_000 * 7),
  }))
  sameNames(tests.map((test) => test.name), tests.map((test) => test.name), 'TRX result identities')
  return tests.sort((a, b) => a.name.localeCompare(b.name))
}

/** Reconcile one artifact and its TRX against authenticated run/job metadata. */
export function reconcileApiObservationArtifact({ artifact, trxText, apiRun, apiJob, effectiveApiJob = null, treeSha, workflow }) {
  const normalized = normalizeApiObservationArtifact(artifact)
  const run = object(apiRun, 'GitHub workflow run')
  const repository = run.repository?.full_name ?? run.repository
  if (repository !== API_REPOSITORY) throw new Error('GitHub workflow run belongs to another repository.')
  if (run.id !== normalized.run.id || normalized.run.attempt > (run.run_attempt ?? 1) || run.event !== normalized.run.event || run.head_sha !== normalized.run.sha || normalized.run.tree !== treeSha) {
    throw new Error('Artifact run identity does not match authenticated GitHub metadata.')
  }
  if (run.name !== API_WORKFLOW_NAME || normalized.run.workflow !== API_WORKFLOW_NAME) throw new Error('Artifact workflow identity is not Product quality gate.')
  if (typeof run.workflow_ref !== 'string' || normalized.run.workflowRef !== run.workflow_ref) throw new Error('Artifact workflow reference does not match authenticated GitHub metadata.')
  const job = jobIdentity(apiJob)
  if (job.runId !== normalized.run.id || job.runAttempt !== normalized.run.attempt || !/^API test suite \(\d+\/3\)$/.test(job.name)) {
    throw new Error('Artifact job identity does not match an API shard job.')
  }
  if (job.headSha === null || job.headSha.toLowerCase() !== run.head_sha.toLowerCase()) throw new Error('Artifact origin job head SHA does not match the authenticated workflow run.')
  const effectiveJob = effectiveApiJob ? jobIdentity(effectiveApiJob) : job
  if (effectiveJob.runId !== job.runId || effectiveJob.name !== job.name || effectiveJob.runAttempt < job.runAttempt) throw new Error('Artifact origin does not bind to the effective API job.')
  if (effectiveJob.headSha === null || effectiveJob.headSha.toLowerCase() !== run.head_sha.toLowerCase()) throw new Error('Artifact effective job head SHA does not match the authenticated workflow run.')
  const jobShard = Number(/^API test suite \((\d+)\/3\)$/.exec(effectiveJob.name)?.[1] ?? 0)
  if (jobShard !== normalized.shard) throw new Error(`Artifact shard ${normalized.shard} does not match job shard ${jobShard}.`)
  if (workflow?.id !== undefined && workflow.id !== run.workflow_id) throw new Error('Workflow run does not belong to the requested workflow definition.')
  let trx
  try {
    trx = parseTrx(trxText)
  } catch (error) {
    throw new Error(`TRX could not be parsed or reconciled: ${error.message}`)
  }
  const results = normalizedResults(trx)
  const expected = normalized.plan.shards.find((entry) => entry.shard === normalized.shard)
  sameNames(results.map((test) => test.name), expected.tests, `API shard ${normalized.shard} TRX identities`)
  const classes = new Set(expected.classes)
  if (results.some((test) => !classes.has(test.className))) throw new Error(`API shard ${normalized.shard} TRX contains a result outside its planned classes.`)
  const expectedClassByTest = new Map(normalized.discovery.classes.flatMap((entry) => entry.tests.map((name) => [name, entry.className])))
  if (results.some((test) => expectedClassByTest.get(test.name) !== test.className)) throw new Error(`API shard ${normalized.shard} TRX class mapping does not match the discovered inventory.`)
  const durations = classDurations(results).map((entry) => ({ className: entry.name, durationMs: entry.durationMs, tests: entry.tests }))
  const timingComplete = results.every((test) => test.durationMs !== null) && normalized.timing.missing.length === 0
  return {
    shard: normalized.shard,
    artifact: normalized,
    job: effectiveJob,
    originJob: job,
    trx: { totals: trx.totals, tests: results },
    outcomes: {
      passed: results.filter((test) => test.outcome === 'Passed').length,
      failed: results.filter((test) => test.outcome === 'Failed').length,
      skipped: results.filter((test) => test.outcome === 'NotExecuted').length,
    },
    classDurations: durations,
    timingComplete,
  }
}

function jobOriginSignature(raw) {
  const value = object(raw, 'GitHub job')
  if (Array.isArray(value.steps) && value.steps.length > 100) throw new Error('GitHub job contains more than 100 steps; origin identity is not safely bounded.')
  const steps = Array.isArray(value.steps)
    ? value.steps.map((step, index) => {
      const item = object(step, `GitHub job step ${index}`)
      return {
        number: Number.isSafeInteger(item.number) ? item.number : null,
        name: optionalString(item.name, `GitHub job step ${index}.name`, 300),
        status: optionalString(item.status, `GitHub job step ${index}.status`, 40),
        conclusion: optionalString(item.conclusion, `GitHub job step ${index}.conclusion`, 40),
        startedAt: optionalString(item.started_at, `GitHub job step ${index}.started_at`, 80),
        completedAt: optionalString(item.completed_at, `GitHub job step ${index}.completed_at`, 80),
      }
    })
    : null
  return JSON.stringify({
    runId: value.run_id ?? null,
    name: value.name ?? null,
    headSha: value.head_sha ?? null,
    conclusion: value.conclusion ?? null,
    startedAt: value.started_at ?? null,
    completedAt: value.completed_at ?? null,
    steps,
  })
}

function jobTimingStatus(job) {
  const startMs = parseIsoTimestamp(job.startedAt)
  const endMs = parseIsoTimestamp(job.completedAt)
  if (job.startedAt !== null && startMs === null) return { valid: false, reason: 'Job started_at is not a valid timestamp.' }
  if (job.completedAt !== null && endMs === null) return { valid: false, reason: 'Job completed_at is not a valid timestamp.' }
  if (startMs !== null && endMs !== null && endMs < startMs) return { valid: false, reason: 'Job completed_at precedes started_at.' }
  return { valid: true, startMs, endMs }
}

function fitsAttempt(job, attempt) {
  const timing = jobTimingStatus(job)
  if (!timing.valid || timing.startMs === null || timing.endMs === null) return false
  return timing.startMs >= attempt.startMs && timing.endMs <= attempt.endMs
}

/** Resolve GitHub's copied partial-rerun jobs to their actual originating execution. */
export function resolveJobOrigins({ latestJobs = [], allJobs = [], runId, runAttempt, attemptRuns = undefined }) {
  const attemptMetadata = normalizeAttemptMetadata(attemptRuns, runId, runAttempt)
  const attemptByNumber = new Map(attemptMetadata.records.map((entry) => [entry.attempt, entry]))
  const attemptMetadataUsable = !attemptMetadata.supplied || attemptMetadata.complete
  const latest = latestJobs.map((raw) => ({ raw, job: jobIdentity(raw), signature: jobOriginSignature(raw) }))
  const all = allJobs.map((raw) => ({ raw, job: jobIdentity(raw), signature: jobOriginSignature(raw) }))
  const invalidTimings = [...latest, ...all].filter((entry, index, values) => {
    const timing = jobTimingStatus(entry.job)
    return !timing.valid && values.findIndex((candidate) => candidate.job.id === entry.job.id) === index
  }).map((entry) => ({ jobId: entry.job.id, name: entry.job.name, reason: jobTimingStatus(entry.job).reason }))
  const byId = new Map()
  const duplicateIds = []
  for (const entry of all) {
    if (entry.job.runId !== runId) continue
    if (byId.has(entry.job.id)) duplicateIds.push(entry.job.id)
    else byId.set(entry.job.id, entry)
  }
  const unresolvedCopies = []
  const resolveEntry = (entry) => {
    const direct = byId.get(entry.job.id)
    const matches = all.filter((candidate) => {
      if (!attemptMetadataUsable) return false
      if (candidate.signature !== entry.signature) return false
      const attempt = attemptByNumber.get(candidate.job.runAttempt)
      return attempt === undefined || fitsAttempt(candidate.job, attempt)
    })
    // A partial rerun can return a copied job with a new id and run_attempt while
    // retaining the exact execution timestamps and step outcomes from an earlier
    // attempt. Prefer the unique earliest execution. This also resolves copied
    // jobs carried through more than two attempts to their first physical run.
    const earlierMatches = matches.filter((candidate) => candidate.job.id !== entry.job.id && candidate.job.runAttempt < entry.job.runAttempt)
    const earliestAttempt = Math.min(...earlierMatches.map((candidate) => candidate.job.runAttempt))
    const earliestMatches = Number.isFinite(earliestAttempt) ? earlierMatches.filter((candidate) => candidate.job.runAttempt === earliestAttempt) : []
    const directAttempt = direct ? attemptByNumber.get(direct.job.runAttempt) : undefined
    const directUsable = attemptMetadataUsable && direct && (!directAttempt || fitsAttempt(direct.job, directAttempt)) ? direct : null
    let origin = earliestMatches.length === 1 ? earliestMatches[0] : directUsable ?? (matches.length === 1 ? matches[0] : null)
    if (earliestMatches.length > 1) origin = null
    if (!origin && matches.length > 1) {
      unresolvedCopies.push({ jobId: entry.job.id, name: entry.job.name, reason: 'Copied job matched multiple possible originating executions.' })
    } else if (!origin) {
      unresolvedCopies.push({ jobId: entry.job.id, name: entry.job.name, reason: 'Effective job had no uniquely matching originating execution.' })
    }
    return { ...entry, origin }
  }
  const resolvedLatest = latest.map(resolveEntry)
  const resolvedAll = all.map(resolveEntry)
  const effectiveIds = new Set(latest.map((entry) => entry.job.id))
  const seen = new Set()
  const ledger = []
  for (const entry of all) {
    const job = entry.job
    if (job.runId !== runId) continue
    if (seen.has(job.id)) {
      continue
    }
    seen.add(job.id)
    const effective = effectiveIds.has(job.id)
    const resolvedEntry = resolvedAll.find((candidate) => candidate.job.id === job.id)
    const originJob = resolvedEntry?.origin?.job ?? entry.job
    ledger.push({ ...job, effective, originJobId: originJob.id, originAttempt: originJob.runAttempt, origin: originJob.id !== job.id ? 'copied-from-earlier-attempt' : job.runAttempt < runAttempt ? 'earlier-attempt' : 'current-attempt' })
  }
  for (const entry of resolvedLatest) {
    if (seen.has(entry.job.id)) continue
    const originJob = entry.origin?.job ?? entry.job
    ledger.push({ ...entry.job, effective: true, originJobId: originJob.id, originAttempt: originJob.runAttempt, origin: originJob.id !== entry.job.id ? 'copied-from-earlier-attempt' : entry.job.runAttempt < runAttempt ? 'earlier-attempt' : 'current-attempt' })
  }
  return {
    currentAttempt: runAttempt,
    effectiveJobIds: latest.map((entry) => entry.job.id).sort((a, b) => a - b),
    ledger: ledger.sort((a, b) => a.id - b.id),
    duplicateIds,
    unresolvedCopies,
    invalidTimings,
    attemptMetadata: {
      supplied: attemptMetadata.supplied,
      complete: attemptMetadata.complete,
      records: attemptMetadata.records.map((entry) => ({ id: entry.id, attempt: entry.attempt, startedAt: entry.startedAt, updatedAt: entry.updatedAt, status: entry.status, conclusion: entry.conclusion })),
      errors: attemptMetadata.errors,
    },
  }
}

function eventRole(run) {
  const event = run.event
  const ref = String(run.head_branch ?? run.ref ?? '')
  const main = ref === 'main' || ref === 'refs/heads/main' || ref.endsWith('/refs/heads/main')
  return event === 'pull_request' ? 'pr-validation'
    : event === 'merge_group' ? 'merge-queue-validation'
      : event === 'push' ? (main ? 'post-merge-validation' : 'branch-push-role-unverified')
        : event === 'schedule' ? 'scheduled-diagnostic'
          : event === 'workflow_dispatch' ? (main ? 'main-manual-diagnostic' : 'branch-dispatch-role-unverified') : 'unknown'
}

/** Combine all API shard observations for one run; authentication is explicit and defaults to unverified. */
export function buildApiObservationRun({ apiRun, workflow, workflowDefinition, treeSha, artifactResults, latestJobs, allJobs, attemptRuns = undefined, fragmentResults = undefined, sourceAuthenticated = false }) {
  const run = object(apiRun, 'GitHub workflow run')
  const runId = positiveInt(run.id, 'GitHub workflow run.id')
  const runAttempt = positiveInt(run.run_attempt ?? 1, 'GitHub workflow run.run_attempt', 1000)
  const exclusions = []
  const reconciled = []
  for (const entry of Array.isArray(artifactResults) ? artifactResults : []) {
    try {
      if (entry.error) throw new Error(entry.error)
      const result = reconcileApiObservationArtifact({ artifact: entry.artifact, trxText: entry.trxText, apiRun: run, apiJob: entry.originJob ?? entry.job, effectiveApiJob: entry.job, treeSha, workflow })
      if (reconciled.some((other) => other.shard === result.shard)) throw new Error(`Duplicate observation for API shard ${result.shard}.`)
      reconciled.push(result)
    } catch (error) {
      exclusions.push({ kind: 'artifact', shard: entry.shard ?? null, artifactId: entry.artifact?.id ?? null, reason: String(error.message).slice(0, 300) })
    }
  }
  reconciled.sort((a, b) => a.shard - b.shard)
  const byShard = new Map(reconciled.map((entry) => [entry.shard, entry]))
  for (let shard = 1; shard <= API_SHARD_COUNT; shard += 1) if (!byShard.has(shard)) exclusions.push({ kind: 'shard', shard, reason: 'No reconciled API observation artifact for this shard.' })
  const fragmentsRequired = Array.isArray(fragmentResults)
  const reconciledFragments = []
  if (fragmentsRequired) {
    for (const entry of fragmentResults) {
      try {
        if (entry.error) throw new Error(entry.error)
        const shard = positiveInt(entry.shard, 'metrics fragment shard', API_SHARD_COUNT)
        const artifactResult = byShard.get(shard)
        if (!artifactResult) throw new Error(`Metrics fragment for shard ${shard} has no reconciled API artifact.`)
        const fragment = reconcileApiFragment({ fragment: entry.fragment, apiRun: run, apiJob: artifactResult.originJob ?? artifactResult.job, treeSha, shard, trx: artifactResult.trx })
        if (reconciledFragments.some((other) => other.shard === shard)) throw new Error(`Duplicate metrics fragment for API shard ${shard}.`)
        reconciledFragments.push({ shard, fragment })
      } catch (error) {
        exclusions.push({ kind: 'fragment', shard: entry.shard ?? null, reason: String(error.message).slice(0, 300) })
      }
    }
    reconciledFragments.sort((a, b) => a.shard - b.shard)
    const fragmentByShard = new Map(reconciledFragments.map((entry) => [entry.shard, entry.fragment]))
    for (let shard = 1; shard <= API_SHARD_COUNT; shard += 1) {
      const artifactResult = byShard.get(shard)
      const fragment = fragmentByShard.get(shard)
      if (!fragment) exclusions.push({ kind: 'fragment', shard, reason: 'No validated metrics fragment for this API shard.' })
      else artifactResult.fragment = fragment
    }
  }
  const fragmentsComplete = !fragmentsRequired || (reconciledFragments.length === API_SHARD_COUNT && !exclusions.some((entry) => entry.kind === 'fragment'))
  let inventory = null
  let inventoryError = null
  if (reconciled.length > 0) {
    try {
      inventory = reconciled[0].artifact.discovery
      for (const entry of reconciled.slice(1)) {
        if (entry.artifact.discovery.digest !== inventory.digest) throw new Error('Shard discovery digests disagree.')
        sameNames(entry.artifact.discovery.tests, inventory.tests, 'Shard discovery inventories')
      }
      const assigned = reconciled.flatMap((entry) => entry.trx.tests.map((test) => test.name))
      sameNames(assigned, inventory.tests, 'Complete API result inventory')
    } catch (error) {
      inventoryError = String(error.message).slice(0, 300)
      exclusions.push({ kind: 'inventory', reason: inventoryError })
      inventory = null
    }
  }
  const attempts = resolveJobOrigins({ latestJobs, allJobs, runId, runAttempt, attemptRuns })
  const apiJobName = (name) => /^API test suite \(\d+\/3\)$/.test(name)
  const relevantJobProblems = (entries) => entries.filter((entry) => apiJobName(entry.name ?? ''))
  const relevantUnresolved = relevantJobProblems(attempts.unresolvedCopies)
  const relevantInvalidTimings = relevantJobProblems(attempts.invalidTimings)
  // Keep every job diagnostic in the report, while only API shard jobs can make
  // API observation evidence incomplete. Skipped unrelated jobs can have stale
  // or reversed timestamps in GitHub's metadata and must not hide three valid
  // API origins.
  if (attempts.duplicateIds.length > 0) exclusions.push({ kind: 'jobs', reason: `Duplicate job identities in attempt ledger: ${attempts.duplicateIds.join(', ')}.` })
  if (relevantUnresolved.length > 0) exclusions.push({ kind: 'jobs', reason: `Unresolved copied API job origins: ${relevantUnresolved.map((entry) => entry.jobId).join(', ')}.` })
  if (relevantInvalidTimings.length > 0) exclusions.push({ kind: 'jobs', reason: `Invalid API job timing metadata: ${relevantInvalidTimings.map((entry) => entry.jobId).join(', ')}.` })
  if (attempts.unresolvedCopies.length > relevantUnresolved.length) exclusions.push({ kind: 'job-diagnostic', reason: `Unresolved non-API job origins retained in attempt ledger: ${attempts.unresolvedCopies.filter((entry) => !apiJobName(entry.name ?? '')).map((entry) => entry.jobId).join(', ')}.` })
  if (attempts.invalidTimings.length > relevantInvalidTimings.length) exclusions.push({ kind: 'job-diagnostic', reason: `Invalid non-API job timing metadata retained in attempt ledger: ${attempts.invalidTimings.filter((entry) => !apiJobName(entry.name ?? '')).map((entry) => entry.jobId).join(', ')}.` })
  if (attempts.attemptMetadata.errors.length > 0) exclusions.push({ kind: 'jobs', reason: `Incomplete authenticated attempt metadata: ${attempts.attemptMetadata.errors.slice(0, 5).join(' ')}` })
  const complete = reconciled.length === API_SHARD_COUNT && inventory !== null && fragmentsComplete && exclusions.every((entry) => entry.kind !== 'inventory' && entry.kind !== 'jobs' && entry.kind !== 'fragment')
  const allSucceeded = reconciled.every((entry) => entry.job.conclusion === 'success') && run.conclusion === 'success'
  const timingComplete = reconciled.every((entry) => entry.timingComplete)
  const comparabilityReasons = []
  if (!complete) comparabilityReasons.push('API shard artifacts or complete inventory reconciliation is unavailable.')
  if (!allSucceeded) comparabilityReasons.push('Run or shard outcome was not successful.')
  if (!timingComplete) comparabilityReasons.push('One or more TRX rows have unknown duration.')
  if (run.status !== 'completed') comparabilityReasons.push('Run is not completed.')
  if (runAttempt !== 1) comparabilityReasons.push('Recovered or rerun execution is retained for audit and weights, but is not an ordinary first-pass performance sample.')
  const metadata = {
    authenticated: sourceAuthenticated === true,
    repository: API_REPOSITORY,
    workflow: {
      id: workflow?.id ?? run.workflow_id ?? null,
      name: run.name ?? null,
      path: workflow?.path ?? run.path ?? API_WORKFLOW_PATH,
      definitionRevision: workflowDefinition?.sha ?? null,
    },
    run: {
      id: runId,
      attempt: runAttempt,
      event: boundedString(run.event, 'GitHub workflow run.event', 50),
      role: eventRole(run),
      status: optionalString(run.status, 'GitHub workflow run.status', 40),
      conclusion: optionalString(run.conclusion, 'GitHub workflow run.conclusion', 40),
      commitSha: sha(run.head_sha, 'GitHub workflow run.head_sha'),
      treeSha: sha(treeSha, 'GitHub commit tree SHA'),
      ref: optionalString(run.head_branch ?? run.ref, 'GitHub workflow run.ref', 300),
      createdAt: optionalString(run.created_at, 'GitHub workflow run.created_at', 80),
      startedAt: optionalString(run.run_started_at, 'GitHub workflow run.run_started_at', 80),
      completedAt: optionalString(run.updated_at, 'GitHub workflow run.updated_at', 80),
    },
  }
  return {
    schemaVersion: API_OBSERVATION_REPORT_SCHEMA,
    repository: API_REPOSITORY,
    sourceMetadata: metadata,
    artifactAssertions: {
      authenticated: false,
      reconciled: complete,
      inventoryDigest: inventory?.digest ?? null,
      artifactCount: artifactResults?.length ?? 0,
      reconciledShardCount: reconciled.length,
      fragmentsRequired,
      reconciledFragmentCount: reconciledFragments.length,
      fragmentsComplete,
    },
    comparability: {
      eligible: complete && allSucceeded && timingComplete && run.status === 'completed' && runAttempt === 1,
      sampleKind: runAttempt === 1 ? 'ordinary-first-pass' : 'recovered-or-rerun',
      reasons: comparabilityReasons,
      adoptionAuthority: 'absent',
    },
    inventory: inventory ? {
      digest: inventory.digest,
      tests: inventory.tests,
      classes: inventory.classes.map((entry) => ({ className: entry.className, testCount: entry.testCount })),
      testCount: inventory.tests.length,
      classCount: inventory.classes.length,
    } : null,
    shards: reconciled.map((entry) => ({
      shard: entry.shard,
      job: entry.job,
      originJob: entry.originJob ?? null,
      filter: entry.artifact.actual.filter,
      expected: entry.artifact.actual.expected,
      timing: entry.artifact.timing,
      toolchain: entry.artifact.toolchain,
      constraints: entry.artifact.constraints,
      trx: entry.trx,
      outcomes: entry.outcomes,
      classDurations: entry.classDurations,
      timingComplete: entry.timingComplete,
      fragment: entry.fragment ?? null,
    })),
    attempts,
    exclusions: exclusions.slice(0, MAX_EXCLUSIONS),
    inventoryError,
  }
}

export function classDurationWeights(run) {
  const value = object(run, 'API observation run')
  const map = new Map()
  const entries = Array.isArray(value.classDurations)
    ? value.classDurations
    : (Array.isArray(value.shards) ? value.shards.flatMap((shard) => Array.isArray(shard.classDurations) ? shard.classDurations : []) : [])
  for (const entry of entries) {
    if (typeof entry.className !== 'string' || map.has(entry.className)) continue
    map.set(entry.className, entry.durationMs === null ? null : entry.durationMs)
  }
  return map
}

function markdown(value) {
  return String(value ?? '—').replace(/[|\r\n]/g, (char) => char === '|' ? '\\|' : ' ')
}

export function renderApiObservationMarkdown(report) {
  const retainedNonComparable = report.runs.filter((run) => run.comparability?.eligible !== true).length
  const lines = ['# Authenticated API observations', '', `- Repository: \`${markdown(report.repository)}\`; runs retained: ${report.runs.length}; collection exclusions: ${report.exclusions.length}; retained non-comparable runs: ${retainedNonComparable}`, '- GitHub metadata is authenticated read-only API data. Artifact contents are runner assertions reconciled against that metadata; they do not become authenticated source claims.', '- Adoption authority: **absent**; ordinary API selection remains the current three-shard count plan.', '']
  lines.push('| Run | Attempt | Event role | Source commit | Inventory | Comparable |')
  lines.push('|---:|---:|---|---|---:|---|')
  for (const run of report.runs) {
    const metadata = run.sourceMetadata?.run ?? {}
    lines.push(`| ${metadata.id ?? '—'} | ${metadata.attempt ?? '—'} | ${markdown(metadata.role)} | \`${markdown(metadata.commitSha)}\` | ${run.inventory?.testCount ?? '—'} / ${markdown(run.artifactAssertions?.inventoryDigest)} | ${run.comparability?.eligible === true ? 'yes' : 'no'} |`)
  }
  lines.push('')
  lines.push('## Run dispositions')
  lines.push('')
  for (const run of report.runs) {
    const metadata = run.sourceMetadata?.run ?? {}
    const reasons = [
      ...(Array.isArray(run.comparability?.reasons) ? run.comparability.reasons : []),
      ...(Array.isArray(run.exclusions) ? run.exclusions.map((entry) => entry.reason) : []),
    ].filter(Boolean)
    lines.push(`### Run ${metadata.id ?? 'unknown'} attempt ${metadata.attempt ?? 'unknown'}`)
    lines.push('')
    lines.push(`- Comparable: **${run.comparability?.eligible === true}**; artifact reconciliation: **${run.artifactAssertions?.reconciled === true}**.`)
    if (reasons.length === 0) lines.push('- No reconciliation or comparability refusal reasons.')
    else for (const reason of reasons.slice(0, MAX_EXCLUSIONS)) lines.push(`- ${markdown(reason)}`)
    lines.push('')
  }
  lines.push('## Exclusions')
  lines.push('')
  if (report.exclusions.length === 0) lines.push('None.')
  else for (const exclusion of report.exclusions.slice(0, MAX_EXCLUSIONS)) lines.push(`- ${markdown(exclusion.reason ?? 'excluded artifact')}`)
  lines.push('')
  return lines.join('\n')
}
