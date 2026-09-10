import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { buildCurrentCountPlan, normalizeApiDiscovery, parseVstestList } from '../lib/api-packing-shadow.mjs'
import { API_OBSERVATION_ARTIFACT_SCHEMA, buildApiObservationRun, classDurationWeights, decodeXmlAttribute, normalizeApiObservationArtifact, reconcileApiObservationArtifact, resolveJobOrigins } from '../lib/api-observations.mjs'
import { buildFragment } from '../lib/fragment.mjs'

const commitSha = 'a'.repeat(40)
const treeSha = 'b'.repeat(40)
const discovery = normalizeApiDiscovery({
  schemaVersion: 'aerolink-api-discovery/v1',
  repository: 'AeroLinkDEV/requirements-management-tool',
  source: 'dotnet-list-tests',
  project: 'product/tests/AeroLink.Api.Tests/AeroLink.Api.Tests.csproj',
  commitSha,
  treeSha,
  tests: [
    'AeroLink.Api.Tests.AlphaTests.First_test',
    'AeroLink.Api.Tests.BetaTests.Second_test',
    'AeroLink.Api.Tests.GammaTests.Third_test',
    'AeroLink.Api.Tests.DeltaTests.Fourth_test',
  ],
})
const plan = buildCurrentCountPlan(discovery, 3)
const run = { id: 42, run_attempt: 2, name: 'Product quality gate', workflow_id: 7, workflow_ref: 'AeroLinkDEV/requirements-management-tool/.github/workflows/ci.yml@refs/heads/main', event: 'merge_group', status: 'completed', conclusion: 'success', head_sha: commitSha, repository: { full_name: 'AeroLinkDEV/requirements-management-tool' }, created_at: '2026-09-09T01:00:00Z', updated_at: '2026-09-09T01:10:00Z' }
const workflow = { id: 7, path: '.github/workflows/ci.yml' }

function trxFor(shard) {
  const tests = plan.shards.find((entry) => entry.shard === shard).tests
  const definitions = tests.map((name, index) => {
    const id = `id-${shard}-${index}`
    const dot = name.lastIndexOf('.')
    const className = name.slice(0, dot)
    const method = name.slice(dot + 1)
    return { id, className, method, name }
  })
  return `<?xml version="1.0"?><TestRun><ResultSummary><Counters total="${tests.length}" executed="${tests.length}" passed="${tests.length}" failed="0" notExecuted="0" /></ResultSummary><TestDefinitions>${definitions.map((entry) => `<UnitTest id="${entry.id}"><TestMethod className="${entry.className}" name="${entry.method}" /></UnitTest>`).join('')}</TestDefinitions><Results>${definitions.map((entry, index) => `<UnitTestResult testId="${entry.id}" testName="${entry.name}" outcome="Passed" duration="00:00:00.00${index + 1}" />`).join('')}</Results></TestRun>`
}

function artifactFor(shard, overrides = {}) {
  const selected = plan.shards.find((entry) => entry.shard === shard)
  return {
    schemaVersion: API_OBSERVATION_ARTIFACT_SCHEMA,
    repository: 'AeroLinkDEV/requirements-management-tool',
    run: { id: run.id, attempt: run.run_attempt, event: run.event, sha: commitSha, tree: treeSha, workflow: 'Product quality gate', workflowRef: 'AeroLinkDEV/requirements-management-tool/.github/workflows/ci.yml@refs/heads/main' },
    shard,
    shardCount: 3,
    discovery,
    plan,
    actual: { filter: selected.filter, expected: selected.caseCount },
    timing: { jobStartMs: 1000, setupEndMs: 2000, testEndMs: 6000, capturedAtMs: 7000 },
    toolchain: { runnerOs: 'Windows', image: 'windows-2025', dotnet: '10.0.x' },
    ...overrides,
  }
}

function jobFor(shard, attempt = 2) {
  return { id: 100 + shard, run_id: run.id, run_attempt: attempt, name: `API test suite (${shard}/3)`, status: 'completed', conclusion: 'success', head_sha: commitSha, started_at: '2026-09-09T01:01:00Z', completed_at: '2026-09-09T01:02:00Z', runner_os: 'Windows', runner_name: `runner-${shard}` }
}

function fragmentFor(shard, attempt = run.run_attempt) {
  const counts = plan.shards.find((entry) => entry.shard === shard).caseCount
  return buildFragment({
    run: { id: run.id, attempt, event: run.event, sha: commitSha, tree: treeSha, workflow: run.name, workflowRef: run.workflow_ref, repository: 'AeroLinkDEV/requirements-management-tool' },
    job: { group: 'backend-api', instance: `backend-api-${shard}`, name: jobFor(shard).name, needs: [], result: 'success' },
    timings: { jobStartMs: 1000, setupEndMs: 2000, testEndMs: 6000, jobEndMs: 7000, setupMs: 1000, testMs: 4000, postTestMs: 1000, missing: {} },
    counts: { expected: counts, executed: counts, passed: counts, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null },
    cache: { nuget: null, npm: null, chromium: null, missing: {} },
    classification: { docsOnly: false, backend: true, client: false, browser: false, postgresql: false, unavailable: false },
    result: 'success',
  })
}

test('artifact and TRX reconciliation retains exact identities and class durations', () => {
  const artifact = artifactFor(1)
  const normalized = normalizeApiObservationArtifact(artifact)
  assert.equal(normalized.discovery.digest, discovery.digest)
  const result = reconcileApiObservationArtifact({ artifact, trxText: trxFor(1), apiRun: run, apiJob: jobFor(1), treeSha, workflow })
  assert.equal(result.trx.totals.total, plan.shards[0].tests.length)
  assert.equal(result.trx.tests.length, result.classDurations.reduce((sum, entry) => sum + entry.tests, 0))
  assert.equal(result.timingComplete, true)
})

test('artifact reconciliation rejects a job whose head SHA differs from the run', () => {
  const mismatchedJob = { ...jobFor(1), head_sha: 'f'.repeat(40) }
  assert.throws(() => reconcileApiObservationArtifact({ artifact: artifactFor(1), trxText: trxFor(1), apiRun: run, apiJob: mismatchedJob, treeSha, workflow }), /job head SHA/)
})

test('TRX XML entities are decoded once before parameterized identity reconciliation', () => {
  const entityName = 'AeroLink.Api.Tests.AlphaTests.First_test(value: "one" & <two>)'
  const entityDiscovery = normalizeApiDiscovery({ ...discovery, tests: [entityName, ...discovery.tests.slice(1)] })
  const entityPlan = buildCurrentCountPlan(entityDiscovery, 3)
  const selected = entityPlan.shards.find((entry) => entry.tests.includes(entityName))
  const definitions = selected.tests.map((name, index) => { const dot=name.lastIndexOf('.'); return { id:`entity-${index}`, name, className:name.slice(0,dot), method:name.slice(dot+1).split('(', 1)[0] } })
  const encoded = (value) => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;')
  const trx = `<TestRun><ResultSummary><Counters total="${definitions.length}" executed="${definitions.length}" passed="${definitions.length}" failed="0" notExecuted="0" /></ResultSummary><TestDefinitions>${definitions.map((entry) => `<UnitTest id="${entry.id}"><TestMethod className="${entry.className}" name="${entry.method}" /></UnitTest>`).join('')}</TestDefinitions><Results>${definitions.map((entry) => `<UnitTestResult testId="${entry.id}" testName="${encoded(entry.name)}" outcome="Passed" duration="00:00:00.0010000" />`).join('')}</Results></TestRun>`
  const entityArtifact = artifactFor(selected.shard, { discovery: entityDiscovery, plan: entityPlan, shard: selected.shard, actual: { filter: selected.filter, expected: selected.caseCount } })
  const result = reconcileApiObservationArtifact({ artifact: entityArtifact, trxText: trx, apiRun: run, apiJob: jobFor(selected.shard), treeSha, workflow })
  assert.ok(result.trx.tests.some((test) => test.name === entityName))
  assert.equal(decodeXmlAttribute('&amp;quot;'), '&quot;')
})

test('artifact normalization accepts maintained count-desc filter order with unequal class sizes', () => {
  const orderedDiscovery = normalizeApiDiscovery({ ...discovery, tests: [
    'AeroLink.Api.Tests.AlphaTests.One', 'AeroLink.Api.Tests.AlphaTests.Two', 'AeroLink.Api.Tests.AlphaTests.Three',
    'AeroLink.Api.Tests.BetaTests.One', 'AeroLink.Api.Tests.GammaTests.One', 'AeroLink.Api.Tests.DeltaTests.One',
  ] })
  const orderedPlan = buildCurrentCountPlan(orderedDiscovery, 3)
  const selected = orderedPlan.shards[0]
  const normalized = normalizeApiObservationArtifact(artifactFor(selected.shard, {
    discovery: orderedDiscovery,
    plan: orderedPlan,
    actual: { filter: selected.filter, expected: selected.caseCount },
  }))
  assert.equal(normalized.actual.filter, selected.filter)
  assert.equal(normalized.plan.shards[selected.shard - 1].filter, selected.filter)
})

test('complete run records authenticated metadata separately from reconciled artifact assertions', () => {
  const firstPassRun = { ...run, run_attempt: 1 }
  const artifactResults = [1, 2, 3].map((shard) => ({ shard, artifact: artifactFor(shard, { run: { id: run.id, attempt: 1, event: run.event, sha: commitSha, tree: treeSha, workflow: 'Product quality gate', workflowRef: run.workflow_ref } }), trxText: trxFor(shard), job: jobFor(shard, 1) }))
  const jobs = [1, 2, 3].map((shard) => jobFor(shard, 1))
  jobs.push({ id: 99, run_id: run.id, run_attempt: 1, name: 'API test suite (1/3)', status: 'completed', conclusion: 'success' })
  const report = buildApiObservationRun({ apiRun: firstPassRun, workflow, workflowDefinition: { sha: 'c'.repeat(40) }, treeSha, artifactResults, latestJobs: jobs.slice(0, 3), allJobs: jobs, fragmentResults: [1, 2, 3].map((shard) => ({ shard, fragment: fragmentFor(shard, 1) })) })
  assert.equal(report.sourceMetadata.authenticated, true)
  assert.equal(report.artifactAssertions.authenticated, false)
  assert.equal(report.artifactAssertions.reconciled, true)
  assert.equal(report.artifactAssertions.fragmentsComplete, true)
  assert.equal(report.comparability.eligible, true)
  assert.deepEqual(report.attempts.effectiveJobIds, [101, 102, 103])
  assert.equal(report.attempts.ledger.find((entry) => entry.id === 99).effective, false)
  assert.equal(report.inventory.testCount, 4)
  assert.equal(classDurationWeights(report).size, 4)
})

test('attempt ledger resolves a copied partial-rerun job to its earlier execution', () => {
  const original = jobFor(1, 1)
  original.id = 901
  const copied = jobFor(1, 2)
  copied.id = 1901
  const attempts = resolveJobOrigins({ latestJobs: [copied], allJobs: [original, copied], runId: run.id, runAttempt: 2, attemptRuns: [
    { id: run.id, run_attempt: 1, run_started_at: '2026-09-09T01:00:00Z', updated_at: '2026-09-09T01:03:00Z' },
    { id: run.id, run_attempt: 2, run_started_at: '2026-09-09T01:00:30Z', updated_at: '2026-09-09T01:10:00Z' },
  ] })
  const effective = attempts.ledger.find((entry) => entry.id === copied.id)
  assert.equal(effective.effective, true)
  assert.equal(effective.originJobId, original.id)
  assert.equal(effective.originAttempt, 1)
  assert.equal(effective.origin, 'copied-from-earlier-attempt')
  assert.equal(attempts.attemptMetadata.complete, true)
  assert.deepEqual(attempts.unresolvedCopies, [])
})

test('attempt ledger resolves a copied job chain to the earliest physical execution', () => {
  const original = jobFor(1, 1)
  original.id = 2901
  const copiedSecond = jobFor(1, 2)
  copiedSecond.id = 2902
  const copiedThird = jobFor(1, 3)
  copiedThird.id = 2903
  const attempts = resolveJobOrigins({ latestJobs: [copiedThird], allJobs: [original, copiedSecond, copiedThird], runId: run.id, runAttempt: 3, attemptRuns: [
    { id: run.id, run_attempt: 1, run_started_at: '2026-09-09T01:00:00Z', updated_at: '2026-09-09T01:03:00Z' },
    { id: run.id, run_attempt: 2, run_started_at: '2026-09-09T01:00:30Z', updated_at: '2026-09-09T01:10:00Z' },
    { id: run.id, run_attempt: 3, run_started_at: '2026-09-09T01:00:45Z', updated_at: '2026-09-09T01:15:00Z' },
  ] })
  assert.equal(attempts.ledger.find((entry) => entry.id === copiedSecond.id).originJobId, original.id)
  assert.equal(attempts.ledger.find((entry) => entry.id === copiedThird.id).originJobId, original.id)
  assert.deepEqual(attempts.unresolvedCopies, [])
})

test('invalid timing on an unrelated skipped job remains diagnostic without excluding valid API evidence', () => {
  const firstPassRun = { ...run, run_attempt: 1 }
  const skipped = { id: 909, run_id: run.id, run_attempt: 1, name: 'Full browser journeys (${{ matrix.shard }}/${{ strategy.job-total }})', status: 'completed', conclusion: 'skipped', started_at: '2026-09-09T01:03:00Z', completed_at: '2026-09-09T01:02:59Z' }
  const jobs = [1, 2, 3].map((shard) => jobFor(shard, 1))
  const report = buildApiObservationRun({
    apiRun: firstPassRun,
    workflow,
    workflowDefinition: { sha: 'c'.repeat(40) },
    treeSha,
    artifactResults: [1, 2, 3].map((shard) => ({ shard, artifact: artifactFor(shard, { run: { id: run.id, attempt: 1, event: run.event, sha: commitSha, tree: treeSha, workflow: 'Product quality gate', workflowRef: run.workflow_ref } }), trxText: trxFor(shard), job: jobFor(shard, 1) })),
    latestJobs: [...jobs, skipped],
    allJobs: [...jobs, skipped],
    fragmentResults: [1, 2, 3].map((shard) => ({ shard, fragment: fragmentFor(shard, 1) })),
  })
  assert.equal(report.comparability.eligible, true)
  assert.ok(report.exclusions.some((entry) => entry.kind === 'job-diagnostic' && entry.reason.includes('909')))
  assert.ok(report.attempts.invalidTimings.some((entry) => entry.jobId === skipped.id && entry.name === skipped.name))
})

test('event roles keep branch dispatch diagnostics separate from main validation', () => {
  const branchRun = { ...run, run_attempt: 1, event: 'workflow_dispatch', head_branch: 'codex/942-api-observations', status: 'completed', conclusion: 'failure' }
  const report = buildApiObservationRun({ apiRun: branchRun, workflow, workflowDefinition: { sha: 'c'.repeat(40) }, treeSha, artifactResults: [], latestJobs: [], allJobs: [] })
  assert.equal(report.sourceMetadata.run.role, 'branch-dispatch-role-unverified')
})

test('an originating-attempt artifact remains bound to its origin while retaining the effective job identity', () => {
  const origin = jobFor(1, 1)
  origin.id = 801
  const effective = jobFor(1, 2)
  effective.id = 1801
  const oldArtifact = artifactFor(1, { run: { id: run.id, attempt: 1, event: run.event, sha: commitSha, tree: treeSha, workflow: run.name, workflowRef: run.workflow_ref } })
  const result = reconcileApiObservationArtifact({ artifact: oldArtifact, trxText: trxFor(1), apiRun: run, apiJob: origin, effectiveApiJob: effective, treeSha, workflow })
  assert.equal(result.job.id, effective.id)
  assert.equal(result.originJob.id, origin.id)
  assert.equal(result.artifact.run.attempt, 1)
})

test('tampered filter and incomplete shards remain exclusions and cannot become comparable', () => {
  const bad = artifactFor(1, { actual: { filter: 'FullyQualifiedName~AeroLink.Api.Tests.AlphaTests.', expected: 1 } })
  const report = buildApiObservationRun({
    apiRun: run,
    workflow,
    workflowDefinition: { sha: 'c'.repeat(40) },
    treeSha,
    artifactResults: [{ shard: 1, artifact: bad, trxText: trxFor(1), job: jobFor(1) }],
    latestJobs: [jobFor(1)],
    allJobs: [jobFor(1)],
  })
  assert.equal(report.comparability.eligible, false)
  assert.ok(report.exclusions.some((entry) => entry.kind === 'artifact'))
  assert.ok(report.exclusions.some((entry) => entry.kind === 'shard' && entry.shard === 2))
})

test('CI producer writes a complete inventory artifact from the maintained list-tests and partition inputs', () => {
  const output = join(tmpdir(), `aerolink-api-observation-writer-${Date.now()}-${Math.random().toString(16).slice(2)}`)
  const listed = join(process.cwd(), 'product/ci-metrics/tests/fixtures/api-vstest-list.txt')
  const parsedDiscovery = normalizeApiDiscovery({
    schemaVersion: 'aerolink-api-discovery/v1', repository: 'AeroLinkDEV/requirements-management-tool', source: 'dotnet-list-tests',
    project: 'product/tests/AeroLink.Api.Tests/AeroLink.Api.Tests.csproj', commitSha: 'f'.repeat(40), treeSha: '1'.repeat(40), tests: parseVstestList(readFileSync(listed, 'utf8')),
  })
  const shardPlan = buildCurrentCountPlan(parsedDiscovery, 3).shards[0]
  const partitionFile = join(output, 'partition.txt')
  mkdirSync(output, { recursive: true })
  writeFileSync(partitionFile, `${shardPlan.caseCount}\n${shardPlan.filter}\n`, 'utf8')
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: process.cwd(), encoding: 'utf8' }).trim()
  execFileSync(process.execPath, ['product/ci-metrics/bin/write-api-observation.mjs', listed, partitionFile, join(output, 'missing.trx'), output], {
    cwd: process.cwd(), encoding: 'utf8',
    env: { ...process.env, GITHUB_REPOSITORY: 'AeroLinkDEV/requirements-management-tool', GITHUB_SHA: head, GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '1', GITHUB_EVENT_NAME: 'merge_group', GITHUB_WORKFLOW: 'Product quality gate', GITHUB_WORKFLOW_REF: 'AeroLinkDEV/requirements-management-tool/.github/workflows/ci.yml@refs/heads/main', API_OBSERVATION_SHARD: '1' },
  })
  const artifact = JSON.parse(readFileSync(join(output, 'api-observation.json'), 'utf8'))
  assert.equal(artifact.schemaVersion, API_OBSERVATION_ARTIFACT_SCHEMA)
  assert.equal(artifact.discovery.tests.length, parsedDiscovery.tests.length)
  assert.equal(artifact.actual.filter, shardPlan.filter)
})
