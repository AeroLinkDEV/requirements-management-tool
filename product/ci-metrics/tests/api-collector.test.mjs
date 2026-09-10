import { test } from 'node:test'
import assert from 'node:assert/strict'
import { collectApiObservations } from '../bin/collect-api-observations.mjs'
import { renderApiObservationMarkdown } from '../lib/api-observations.mjs'

const repository = 'AeroLinkDEV/requirements-management-tool'
const root = `/repos/${repository}`
const commitSha = 'a'.repeat(40)
const treeSha = 'b'.repeat(40)
const workflowRef = `${repository}/.github/workflows/ci.yml@refs/heads/main`

function zip(entries) {
  const local = []
  const central = []
  let offset = 0
  for (const [name, contents] of entries) {
    const nameBytes = Buffer.from(name)
    const data = Buffer.from(contents)
    const header = Buffer.alloc(30 + nameBytes.length + data.length)
    header.writeUInt32LE(0x04034b50, 0)
    header.writeUInt16LE(20, 4)
    header.writeUInt16LE(0, 6)
    header.writeUInt16LE(0, 8)
    header.writeUInt32LE(0, 10)
    header.writeUInt32LE(0, 14)
    header.writeUInt32LE(data.length, 18)
    header.writeUInt32LE(data.length, 22)
    header.writeUInt16LE(nameBytes.length, 26)
    nameBytes.copy(header, 30)
    data.copy(header, 30 + nameBytes.length)
    local.push(header)
    const record = Buffer.alloc(46 + nameBytes.length)
    record.writeUInt32LE(0x02014b50, 0)
    record.writeUInt16LE(20, 4)
    record.writeUInt16LE(20, 6)
    record.writeUInt16LE(0, 8)
    record.writeUInt16LE(0, 10)
    record.writeUInt32LE(0, 12)
    record.writeUInt32LE(0, 16)
    record.writeUInt32LE(data.length, 20)
    record.writeUInt32LE(data.length, 24)
    record.writeUInt16LE(nameBytes.length, 28)
    record.writeUInt32LE(offset, 42)
    nameBytes.copy(record, 46)
    central.push(record)
    offset += header.length
  }
  const centralBytes = Buffer.concat(central)
  const end = Buffer.alloc(22)
  end.writeUInt32LE(0x06054b50, 0)
  end.writeUInt16LE(entries.length, 8)
  end.writeUInt16LE(entries.length, 10)
  end.writeUInt32LE(centralBytes.length, 12)
  end.writeUInt32LE(offset, 16)
  return Buffer.concat([...local, centralBytes, end])
}

function streamedBody(bytes) {
  let read = false
  return {
    getReader: () => ({
      read: async () => read ? { done: true, value: undefined } : (read = true, { done: false, value: bytes }),
      cancel: async () => {},
      releaseLock: () => {},
    }),
  }
}

function apiArtifact({ shard, attempt, plan, discovery }) {
  const selected = plan.shards.find((entry) => entry.shard === shard)
  return {
    schemaVersion: 'aerolink-api-observation-artifact/v1', repository,
    run: { id: 42, attempt, event: 'merge_group', sha: commitSha, tree: treeSha, workflow: 'Product quality gate', workflowRef },
    shard, shardCount: 3, discovery, plan,
    actual: { filter: selected.filter, expected: selected.caseCount },
    timing: { jobStartMs: 1_000, setupEndMs: 2_000, testEndMs: 6_000, capturedAtMs: 7_000 },
    toolchain: { runnerOs: 'Windows', image: 'windows-2025', dotnet: '10.0.x' },
    constraints: { collectionTopology: 'unknown-unobserved', reason: 'fixture' },
  }
}

function trxFor(plan, shard) {
  const tests = plan.shards.find((entry) => entry.shard === shard).tests
  const definitions = tests.map((name, index) => { const dot = name.lastIndexOf('.'); return { id: `t-${index}`, name, className: name.slice(0, dot), method: name.slice(dot + 1).split('(', 1)[0] } })
  return `<TestRun><ResultSummary><Counters total="${tests.length}" executed="${tests.length}" passed="${tests.length}" failed="0" notExecuted="0" /></ResultSummary><TestDefinitions>${definitions.map((entry) => `<UnitTest id="${entry.id}"><TestMethod className="${entry.className}" name="${entry.method}" /></UnitTest>`).join('')}</TestDefinitions><Results>${definitions.map((entry) => `<UnitTestResult testId="${entry.id}" testName="${entry.name}" outcome="Passed" duration="00:00:00.0010000" />`).join('')}</Results></TestRun>`
}

function fragmentFor({ shard, attempt, plan }) {
  const count = plan.shards.find((entry) => entry.shard === shard).caseCount
  return {
    schemaVersion: 'aerolink-ci-fragment/v2',
    run: { id: 42, attempt, event: 'merge_group', sha: commitSha, tree: treeSha, ref: 'refs/heads/main', pr: null, baseSha: null, headSha: null, workflow: 'Product quality gate', workflowRef, repository },
    job: { group: 'backend-api', instance: `backend-api-${shard}`, name: `API test suite (${shard}/3)`, matrix: { shard }, needs: [], result: 'success' },
    timings: { jobStartMs: 1_000, setupEndMs: 2_000, testEndMs: 6_000, jobEndMs: 7_000, setupMs: 1_000, testMs: 4_000, postTestMs: 1_000, missing: {} },
    counts: { expected: count, executed: count, passed: count, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null },
    slowest: [], flakyTests: [], flakyTitlesTruncated: false, flakyTitlesUnavailable: false,
    cache: { nuget: null, npm: null, chromium: null, missing: {} },
    classification: { docsOnly: false, backend: true, client: false, browser: false, postgresql: false, unavailable: false },
    missing: {},
  }
}

test('collector selects copied artifacts from the resolved originating attempt', async () => {
  const tests = [
    'AeroLink.Api.Tests.AlphaTests.One', 'AeroLink.Api.Tests.BetaTests.One', 'AeroLink.Api.Tests.GammaTests.One',
    'AeroLink.Api.Tests.DeltaTests.One', 'AeroLink.Api.Tests.EpsilonTests.One', 'AeroLink.Api.Tests.ZetaTests.One',
  ]
  const discovery = { schemaVersion: 'aerolink-api-discovery/v1', repository, source: 'dotnet-list-tests', project: 'product/tests/AeroLink.Api.Tests/AeroLink.Api.Tests.csproj', commitSha, treeSha, tests }
  const { normalizeApiDiscovery, buildCurrentCountPlan } = await import('../lib/api-packing-shadow.mjs')
  const normalized = normalizeApiDiscovery(discovery)
  const plan = buildCurrentCountPlan(normalized, 3)
  const oldJob = { id: 501, run_id: 42, run_attempt: 1, name: 'API test suite (1/3)', status: 'completed', conclusion: 'success', head_sha: commitSha, started_at: '2026-09-09T01:01:00Z', completed_at: '2026-09-09T01:02:00Z', steps: [] }
  const copiedJob = { ...oldJob, id: 1501, run_attempt: 2 }
  const jobs = [copiedJob, { id: 502, run_id: 42, run_attempt: 2, name: 'API test suite (2/3)', status: 'completed', conclusion: 'success', head_sha: commitSha, started_at: '2026-09-09T01:01:00Z', completed_at: '2026-09-09T01:02:00Z', steps: [] }, { id: 503, run_id: 42, run_attempt: 2, name: 'API test suite (3/3)', status: 'completed', conclusion: 'success', head_sha: commitSha, started_at: '2026-09-09T01:01:00Z', completed_at: '2026-09-09T01:02:00Z', steps: [] }]
  const allJobs = [oldJob, ...jobs]
  const artifacts = []
  const zipById = new Map()
  for (const shard of [1, 2, 3]) {
    const attempt = shard === 1 ? 1 : 2
    const artifactId = 100 + shard
    const fragmentId = 200 + shard
    artifacts.push({ id: artifactId, name: `api-observations-${shard}-${attempt}`, expired: false, workflow_run: { id: 42 } })
    artifacts.push({ id: fragmentId, name: `ci-metrics-fragment-backend-api-${shard}-${attempt}`, expired: false, workflow_run: { id: 42 } })
    zipById.set(artifactId, zip([['api-observation.json', JSON.stringify(apiArtifact({ shard, attempt, plan, discovery: normalized }))], ['shard.trx', trxFor(plan, shard)]]))
    zipById.set(fragmentId, zip([[`fragment-backend-api-${shard}.json`, JSON.stringify(fragmentFor({ shard, attempt, plan }))]]))
  }
  const run = { id: 42, run_attempt: 2, name: 'Product quality gate', path: '.github/workflows/ci.yml', workflow_id: 7, event: 'merge_group', head_branch: 'main', status: 'completed', conclusion: 'success', head_sha: commitSha, repository: { full_name: repository }, created_at: '2026-09-09T01:00:00Z', updated_at: '2026-09-09T01:10:00Z' }
  const request = async (path) => {
    if (path === `${root}/actions/workflows/ci.yml`) return { id: 7, name: 'Product quality gate', path: '.github/workflows/ci.yml' }
    if (path.includes('/actions/workflows/ci.yml/runs?')) return { total_count: 1, workflow_runs: [run] }
    if (path === `${root}/actions/runs/42`) return run
    if (path === `${root}/actions/runs/42/attempts/1`) return { id: 42, run_attempt: 1, run_started_at: '2026-09-09T01:00:00Z', updated_at: '2026-09-09T01:03:00Z' }
    if (path === `${root}/actions/runs/42/attempts/2`) return { id: 42, run_attempt: 2, run_started_at: '2026-09-09T01:00:30Z', updated_at: '2026-09-09T01:10:00Z' }
    if (path === `${root}/git/commits/${commitSha}`) return { sha: commitSha, tree: { sha: treeSha } }
    if (path === `${root}/contents/.github/workflows/ci.yml?ref=${commitSha}`) return { type: 'file', path: '.github/workflows/ci.yml', sha: 'c'.repeat(40) }
    if (path.includes('/jobs?filter=latest')) return { total_count: jobs.length, jobs }
    if (path.includes('/jobs?filter=all')) return { total_count: allJobs.length, jobs: allJobs }
    if (path.includes('/artifacts?')) return { total_count: artifacts.length, artifacts }
    throw new Error(`unexpected API path ${path}`)
  }
  const fetchImpl = async (url) => {
    const match = /\/artifacts\/(\d+)\/zip$/.exec(new URL(url).pathname)
    if (match) return { status: 302, ok: false, headers: { get: (name) => name === 'location' ? `https://objects.example.test/${match[1]}.zip` : null } }
    const artifactId = Number(/\/(\d+)\.zip$/.exec(new URL(url).pathname)?.[1])
    const body = zipById.get(artifactId)
    return { status: 200, ok: true, headers: { get: () => null }, body: streamedBody(body) }
  }
  const report = await collectApiObservations({ token: 'token', repository, request, fetchImpl, window: 1 })
  assert.equal(report.runs.length, 1)
  assert.equal(report.collector.mode, 'injected-read-only-fixture')
  assert.equal(report.collector.sourceAuthenticated, false)
  assert.equal(report.runs[0].sourceMetadata.authenticated, false)
  assert.equal(report.runs[0].sourceMetadata.workflow.workflowRef, workflowRef)
  assert.equal(report.runs[0].sourceMetadata.workflow.workflowRefBasis, 'fixed-repository-workflow-path-and-authenticated-head-branch')
  const markdown = renderApiObservationMarkdown(report)
  assert.match(markdown, /Run dispositions/)
  assert.match(markdown, /Recovered or rerun execution/)
  assert.match(markdown, /retained non-comparable runs: 1/)
  assert.equal(report.runs[0].comparability.eligible, false)
  assert.equal(report.runs[0].comparability.sampleKind, 'recovered-or-rerun')
  const shard = report.runs[0].shards.find((entry) => entry.shard === 1)
  assert.equal(shard.job.id, copiedJob.id)
  assert.equal(shard.originJob.id, oldJob.id)
  assert.equal(shard.trx.tests.length, shard.expected)
  assert.equal(report.runs[0].artifactAssertions.fragmentsComplete, true)
})
