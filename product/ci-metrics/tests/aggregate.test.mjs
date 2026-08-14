import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { readFragments, aggregateFragments, criticalPath, renderMarkdown, MAX_FRAGMENTS } from '../lib/aggregate.mjs'
import { buildFragment, validateFragment } from '../lib/fragment.mjs'

const binDir = fileURLToPath(new URL('../bin/', import.meta.url))
const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))

const run = {
  id: 99,
  attempt: 1,
  event: 'pull_request',
  sha: 'c'.repeat(40),
  tree: 'd'.repeat(40),
  ref: 'refs/pull/9/merge',
  pr: 9,
  workflow: 'Product quality gate',
  workflowRef: 'x/.github/workflows/ci.yml@main',
  repository: 'owner/repo',
}

function fragment(group, instance, { needs = [], result = 'success', jobStartMs = 0, jobEndMs = 10000, setupEndMs = 2000, testEndMs = 9000, counts = null, cache = {}, classification = { docsOnly: false, backend: true, client: false, browser: false, postgresql: false, unavailable: false }, flakyTests = [] } = {}) {
  if (jobEndMs !== null && testEndMs !== null && jobEndMs <= testEndMs) testEndMs = jobEndMs - 1
  if (testEndMs !== null && setupEndMs !== null && setupEndMs >= testEndMs) setupEndMs = Math.max(jobStartMs ?? 0, testEndMs - 1)
  return buildFragment({
    run,
    job: { group, instance, name: `${group} (${instance})`, needs, result, matrix: null },
    timings: {
      jobStartMs: jobStartMs === null ? null : jobStartMs,
      setupEndMs: setupEndMs === null ? null : setupEndMs,
      testEndMs: testEndMs === null ? null : testEndMs,
      jobEndMs: jobEndMs === null ? null : jobEndMs,
      setupMs: setupEndMs !== null && jobStartMs !== null ? setupEndMs - jobStartMs : null,
      testMs: setupEndMs !== null && testEndMs !== null ? testEndMs - setupEndMs : null,
      postTestMs: testEndMs !== null && jobEndMs !== null ? jobEndMs - testEndMs : null,
      missing: {},
    },
    counts: counts ?? { expected: null, executed: null, passed: null, failed: null, skipped: null, flaky: null, source: null, missing: 'no structured output' },
    slowest: [],
    flakyTests,
    cache: { nuget: cache.nuget ?? null, npm: cache.npm ?? null, chromium: cache.chromium ?? null, missing: {} },
    classification,
    missing: {},
  })
}

const expectedMatrix = [
  { group: 'changes', instance: 'changes', needs: [] },
  { group: 'backend-api', instance: 'backend-api-1', needs: ['changes'] },
  { group: 'backend-api', instance: 'backend-api-2', needs: ['changes'] },
  { group: 'backend-api', instance: 'backend-api-3', needs: ['changes'] },
  { group: 'browser-pr', instance: 'browser-pr-1', needs: ['changes'] },
  { group: 'browser-pr', instance: 'browser-pr-2', needs: ['changes'] },
  { group: 'browser-pr', instance: 'browser-pr-3', needs: ['changes'] },
  { group: 'browser-pr', instance: 'browser-pr-4', needs: ['changes'] },
  { group: 'gate', instance: 'gate', needs: ['changes', 'backend-api', 'browser-pr'] },
]

test('the real matrix topology keeps every instance distinct and the gate waits on the slowest lane', () => {
  const fragments = [
    fragment('changes', 'changes', { jobStartMs: 0, jobEndMs: 1000 }),
    fragment('backend-api', 'backend-api-1', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 20000 }),
    fragment('backend-api', 'backend-api-2', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 12000 }),
    fragment('backend-api', 'backend-api-3', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 14000 }),
    fragment('browser-pr', 'browser-pr-1', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 30000 }),
    fragment('browser-pr', 'browser-pr-2', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 16000 }),
    fragment('browser-pr', 'browser-pr-3', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 18000 }),
    fragment('browser-pr', 'browser-pr-4', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 15000 }),
    fragment('gate', 'gate', { needs: ['changes', 'backend-api', 'browser-pr'], jobStartMs: 30000, jobEndMs: 31000 }),
  ]
  const merged = aggregateFragments({ fragments, runMeta: { expectedJobs: expectedMatrix, provenance: { mode: 'trusted', reason: 'test default-branch context' } } })
  assert.equal(merged.missing.length, 0)
  assert.equal(merged.jobs.length, 9)
  assert.equal(merged.criticalPath.job, 'gate')
  assert.equal(merged.criticalPath.durationMs, 31000)
  assert.deepEqual(merged.criticalPath.path, ['changes', 'browser-pr-1', 'gate'])
  assert.equal(merged.criticalPath.trustedTopology, true)
  assert.equal(merged.runIdentityTrusted, false)
})

test('an absent expected lane is missing data and makes the critical path unavailable', () => {
  const fragments = [
    fragment('changes', 'changes', { jobStartMs: 0, jobEndMs: 1000 }),
    fragment('backend-api', 'backend-api-1', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 20000 }),
    fragment('gate', 'gate', { needs: ['changes', 'backend-api', 'browser-pr'], jobStartMs: 20000, jobEndMs: 21000 }),
  ]
  const merged = aggregateFragments({ fragments, runMeta: { expectedJobs: expectedMatrix } })
  assert.ok(merged.missing.some((entry) => entry.job === 'browser-pr-1'))
  assert.ok(merged.missing.some((entry) => entry.job === 'backend-api-2'))
  assert.equal(merged.criticalPath.job, null)
  assert.equal(merged.criticalPath.durationMs, null)
  assert.match(merged.criticalPath.unavailableReason, /expected fragment absent/)
})

test('a null duration on the otherwise longest path makes the critical path unavailable', () => {
  const fragments = [
    fragment('changes', 'changes', { jobStartMs: 0, jobEndMs: 1000 }),
    fragment('backend-api', 'backend-api-1', { needs: ['changes'], jobStartMs: null, jobEndMs: null, setupEndMs: null, testEndMs: null }),
  ]
  const path = criticalPath({ fragments })
  assert.equal(path.durationMs, null)
  assert.match(path.unavailableReason, /duration unknown/)
})

test('a cycle yields an explicit unavailable reason rather than a number', () => {
  const a = fragment('a', 'a', { needs: ['b'] })
  const b = fragment('b', 'b', { needs: ['a'] })
  const path = criticalPath({ fragments: [a, b] })
  assert.equal(path.durationMs, null)
  assert.match(path.unavailableReason, /Cycle detected/)
})

test('a missing dependency group makes the critical path unavailable', () => {
  const fragments = [fragment('a', 'a', { needs: ['does-not-exist'] })]
  const path = criticalPath({ fragments })
  assert.match(path.unavailableReason, /Dependency group "does-not-exist"/)
})

test('duplicate instance identity is contradictory topology', () => {
  const fragments = [
    fragment('backend-api', 'backend-api-1'),
    fragment('backend-api', 'backend-api-1'),
  ]
  const path = criticalPath({ fragments })
  assert.match(path.unavailableReason, /Duplicate job instance/)
})

test('duplicate instances contaminate no derived aggregate, not merely the critical path', () => {
  const one = fragment('backend-api', 'backend-api-1', { counts: { expected: 100, executed: 100, passed: 100, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
  const two = fragment('backend-api', 'backend-api-1', { counts: { expected: 100, executed: 100, passed: 100, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
  two.cache.nuget = 'hit'
  const merged = aggregateFragments({ fragments: [one, two], runMeta: { expectedJobs: [{ group: 'backend-api', instance: 'backend-api-1', needs: [] }] } })
  assert.equal(merged.jobs.length, 0)
  assert.equal(merged.counts.expected, null)
  assert.equal(merged.cache.nuget.hit, 0)
  assert.equal(merged.criticalPath.job, null)
  assert.match(merged.criticalPath.unavailableReason, /Duplicate job instance identity/)
  assert.ok(merged.missing.some((entry) => /Duplicate job instance identity/.test(entry.reason)))
})

test('contradictory counters in a serialized fragment are rejected at read time', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    const crafted = fragment('browser-pr', 'browser-pr-1', { counts: { expected: 100, executed: 1, passed: 99, failed: 0, skipped: 0, flaky: 50, source: 'playwright-json', missing: null } })
    writeFileSync(join(directory, 'crafted.json'), JSON.stringify(crafted))
    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 0)
    assert.ok(missing.some((entry) => entry.job === 'crafted' && /expected must equal executed \+ skipped/.test(entry.reason)))
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('with trusted expectedRun, run-inconsistent fragments are excluded from every derived aggregate regardless of input order', () => {
  const valid = fragment('changes', 'changes', { counts: { expected: 100, executed: 100, passed: 100, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
  const foreign = fragment('backend-api', 'backend-api-1', { counts: { expected: 900, executed: 900, passed: 900, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
  foreign.run.id = 9999
  foreign.cache.nuget = 'hit'
  foreign.flakyTests = ['foreign flaky']
  const runMeta = { expectedJobs: [
    { group: 'changes', instance: 'changes', needs: [] },
    { group: 'backend-api', instance: 'backend-api-1', needs: ['changes'] },
  ], expectedRun: run, provenance: { mode: 'trusted', reason: 'test default-branch context' } }
  for (const fragments of [[valid, foreign], [foreign, valid]]) {
    const merged = aggregateFragments({ fragments, runMeta })
    assert.equal(merged.jobs.length, 1)
    assert.equal(merged.jobs[0].instance, 'changes')
    assert.equal(merged.counts.expected, 100)
    assert.equal(merged.counts.executed, 100)
    assert.equal(merged.cache.nuget.hit, 0)
    assert.deepEqual(merged.flakyTests, [])
    assert.ok(merged.missing.some((entry) => entry.job === 'backend-api-1' && /Run identity does not match/.test(entry.reason)))
    assert.equal(merged.runIdentityTrusted, true)
  }
})

test('without expectedRun, conflicting identities make the aggregate unavailable regardless of input order', () => {
  const valid = fragment('changes', 'changes', { counts: { expected: 100, executed: 100, passed: 100, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
  const foreign = fragment('backend-api', 'backend-api-1', { counts: { expected: 900, executed: 900, passed: 900, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
  foreign.run.id = 9999
  for (const fragments of [[valid, foreign], [foreign, valid]]) {
    const merged = aggregateFragments({ fragments })
    assert.equal(merged.jobs.length, 0)
    assert.equal(merged.counts.executed, null)
    assert.equal(merged.criticalPath.job, null)
    assert.match(merged.criticalPath.unavailableReason, /identities conflict/)
    assert.equal(merged.runIdentityTrusted, false)
    assert.ok(merged.missing.length >= 2)
  }
})

test('without expectedRun, a fully consistent fragment set is aggregated but labelled untrusted', () => {
  const merged = aggregateFragments({ fragments: [fragment('changes', 'changes'), fragment('backend-api', 'backend-api-1', { needs: ['changes'] })] })
  assert.equal(merged.jobs.length, 2)
  assert.equal(merged.runIdentityTrusted, false)
  assert.equal(merged.criticalPath.trustedTopology, false)
  assert.equal(merged.criticalPath.job, 'backend-api-1')
})

test('a fragment that does not match the expected run identity is excluded from jobs and counts', () => {
  const mismatched = fragment('changes', 'changes')
  mismatched.run.tree = 'e'.repeat(40)
  const merged = aggregateFragments({
    fragments: [mismatched],
    runMeta: {
      expectedJobs: [{ group: 'changes', instance: 'changes', needs: [] }],
      expectedRun: { ...run, tree: 'd'.repeat(40) },
    },
  })
  assert.equal(merged.jobs.length, 0)
  assert.ok(merged.missing.some((entry) => /Run identity does not match/.test(entry.reason)))
  assert.equal(merged.criticalPath.job, null)
})

test('trusted expected-jobs topology wins even when a fragment omits or invents dependencies', () => {
  const trusted = [
    { group: 'a', instance: 'a', needs: [] },
    { group: 'b', instance: 'b', needs: ['a'] },
  ]
  // The b fragment claims no dependencies, but the trusted topology says b depends on a.
  const omitted = [
    fragment('a', 'a', { jobStartMs: 0, jobEndMs: 1000 }),
    fragment('b', 'b', { needs: [], jobStartMs: 1000, jobEndMs: 12000 }),
  ]
  const merged = aggregateFragments({ fragments: omitted, runMeta: { expectedJobs: trusted, provenance: { mode: 'trusted', reason: 'test default-branch context' } } })
  assert.equal(merged.criticalPath.job, 'b')
  assert.equal(merged.criticalPath.durationMs, 12000)
  assert.deepEqual(merged.criticalPath.path, ['a', 'b'])
  assert.deepEqual(merged.criticalPath.topologyDisagreements, ['b'])

  // A fragment that claims a dependency the trusted topology does not contain is a contradiction.
  const invented = [
    fragment('a', 'a', { jobStartMs: 0, jobEndMs: 1000 }),
    fragment('b', 'b', { needs: ['does-not-exist'], jobStartMs: 1000, jobEndMs: 12000 }),
  ]
  const conflicted = aggregateFragments({ fragments: invented, runMeta: { expectedJobs: trusted, provenance: { mode: 'trusted', reason: 'test default-branch context' } } })
  assert.equal(conflicted.criticalPath.job, 'b')
  assert.deepEqual(conflicted.criticalPath.path, ['a', 'b'])
  assert.deepEqual(conflicted.criticalPath.topologyDisagreements, ['b'])
})

test('credential-shaped content inside a serialized fragment is rejected at read time, not republished', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    const crafted = fragment('changes', 'changes')
    crafted.job.matrix = { injected: 'Authorization: Bearer abcdefghijklmnop' }
    writeFileSync(join(directory, 'crafted.json'), JSON.stringify(crafted))
    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 0)
    assert.equal(missing.length, 1)
    assert.match(missing[0].reason, /credential-value pattern/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('matrix coordinates are bounded: nine keys, oversized keys/values, and non-scalars are rejected', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    const accepted = fragment('changes', 'changes')
    accepted.job.matrix = Object.fromEntries(Array.from({ length: 8 }, (_, i) => [`k${i}`, i]))
    writeFileSync(join(directory, 'eight.json'), JSON.stringify(accepted))
    assert.equal(readFragments(directory).fragments.length, 1)

    const nine = fragment('changes', 'changes')
    nine.job.matrix = Object.fromEntries(Array.from({ length: 9 }, (_, i) => [`k${i}`, i]))
    writeFileSync(join(directory, 'nine.json'), JSON.stringify(nine))
    const nineResult = readFragments(directory)
    assert.equal(nineResult.fragments.length, 1)
    assert.ok(nineResult.missing.some((entry) => entry.job === 'nine' && /maxProperties/.test(entry.reason)))

    const longKey = fragment('changes', 'changes')
    longKey.job.matrix = { ['x'.repeat(101)]: 'value' }
    writeFileSync(join(directory, 'long-key.json'), JSON.stringify(longKey))
    const longKeyResult = readFragments(directory)
    assert.ok(longKeyResult.missing.some((entry) => entry.job === 'long-key' && /maxKeyLength/.test(entry.reason)))

    const nonScalar = fragment('changes', 'changes')
    nonScalar.job.matrix = { nested: { a: 1 } }
    writeFileSync(join(directory, 'non-scalar.json'), JSON.stringify(nonScalar))
    const nonScalarResult = readFragments(directory)
    assert.ok(nonScalarResult.missing.some((entry) => entry.job === 'non-scalar' && /expected type/.test(entry.reason)))
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('unknown run properties are rejected by the closed run schema at read time', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    const crafted = fragment('changes', 'changes')
    crafted.run.unexpected = 'accepted'
    writeFileSync(join(directory, 'crafted.json'), JSON.stringify(crafted))
    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 0)
    assert.match(missing[0].reason, /unexpected property "unexpected"/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('expected-job metadata with duplicate or malformed entries is rejected', () => {
  const fragments = [fragment('changes', 'changes')]
  const merged = aggregateFragments({
    fragments,
    runMeta: { expectedJobs: [
      { group: 'changes', instance: 'changes', needs: [] },
      { group: 'changes', instance: 'changes', needs: [] },
    ] },
  })
  assert.match(merged.criticalPath.unavailableReason, /Duplicate expected job instance/)
})

test('missing and malformed fragments are reported as missing, never as zero', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    writeFileSync(join(directory, 'valid.json'), JSON.stringify(fragment('changes', 'changes', { jobStartMs: 0, jobEndMs: 1000 })))
    writeFileSync(join(directory, 'malformed.json'), '{not json')
    writeFileSync(join(directory, 'bad-nested.json'), JSON.stringify({ ...fragment('client', 'client'), job: { ...fragment('client', 'client').job, needs: null } }))
    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 1)
    assert.equal(missing.length, 2)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('an oversized fragment file is rejected as missing with a reason', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    writeFileSync(join(directory, 'huge.json'), 'x'.repeat(300 * 1024))
    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 0)
    assert.equal(missing.length, 1)
    assert.match(missing[0].reason, /bounded size/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('an empty fragment directory is missing data, not a successful zero-duration run', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 0)
    const merged = aggregateFragments({ fragments, missing })
    assert.equal(merged.criticalPath.job, null)
    assert.equal(merged.jobs.length, 0)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('expected/actual count mismatch is carried into the merged record', () => {
  const fragments = [fragment('backend-api', 'backend-api-1', { counts: { expected: 160, executed: 159, passed: 158, failed: 1, skipped: 0, flaky: null, source: 'trx', missing: null } })]
  const merged = aggregateFragments({ fragments })
  assert.equal(merged.counts.expected, 160)
  assert.equal(merged.counts.executed, 159)
})

test('flaky titles from all fragments are unioned and bounded', () => {
  const fragments = [
    fragment('browser-pr', 'browser-pr-1', { flakyTests: ['alpha spec'], counts: { expected: 40, executed: 40, passed: 40, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null } }),
    fragment('browser-pr', 'browser-pr-2', { flakyTests: ['alpha spec', 'beta spec'], counts: { expected: 40, executed: 40, passed: 40, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null } }),
  ]
  const merged = aggregateFragments({ fragments })
  assert.deepEqual(merged.flakyTests, ['alpha spec', 'beta spec'])
  assert.equal(merged.counts.flaky, 2)
})

test('runMeta queue delay is reported when supplied and unavailable otherwise', () => {
  const fragments = [fragment('changes', 'changes')]
  assert.equal(aggregateFragments({ fragments }).queue.delayMs, null)
  assert.match(aggregateFragments({ fragments }).queue.unavailableReason, /rolling collector/)
  assert.equal(aggregateFragments({ fragments, runMeta: { queueDelayMs: 12000 } }).queue.delayMs, 12000)
})

test('Markdown output escapes pipes, line breaks, and HTML from untrusted names', () => {
  const tricky = fragment('backend-api', 'backend-api-1', { needs: ['x|y'] })
  tricky.job.name = 'API | suite\n<script>alert(1)</script>'
  tricky.missing['reason <b>'] = 'line\nbreak & value'
  const merged = aggregateFragments({ fragments: [tricky] })
  const markdown = renderMarkdown(merged)
  assert.ok(!markdown.includes('<script>'))
  assert.ok(markdown.includes('&lt;script&gt;'))
  assert.ok(markdown.includes('\\|'))
})

test('merged output and missing lists are bounded', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    for (let i = 0; i < MAX_FRAGMENTS + 10; i++) {
      writeFileSync(join(directory, `fragment-${i}.json`), JSON.stringify(fragment('backend-api', `backend-api-${i}`)))
    }
    const { fragments, missing, truncated } = readFragments(directory)
    assert.equal(fragments.length, MAX_FRAGMENTS)
    assert.equal(truncated, true)
    assert.equal(missing.length, 0)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('aggregate output is bounded and contains no credential-shaped values', () => {
  const fragments = [fragment('changes', 'changes'), fragment('backend-api', 'backend-api-1', { needs: ['changes'] })]
  const merged = aggregateFragments({ fragments })
  const json = JSON.stringify(merged)
  assert.ok(Buffer.byteLength(json, 'utf8') < 512 * 1024)
  assert.ok(!/password\s*=|bearer\s+[A-Za-z0-9._~+/=-]{12,}/i.test(json))
})

test('a failed or cancelled job result is preserved in the merged record', () => {
  const fragments = [fragment('backend-api', 'backend-api-1', { result: 'failure' }), fragment('browser-pr', 'browser-pr-1', { result: 'cancelled' })]
  const merged = aggregateFragments({ fragments })
  assert.equal(merged.jobs.find((job) => job.instance === 'backend-api-1').result, 'failure')
  assert.equal(merged.jobs.find((job) => job.instance === 'browser-pr-1').result, 'cancelled')
})

test('CLI parity: expected-jobs metadata drives absent detection and bounded output', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  const output = mkdtempSync(join(tmpdir(), 'ci-metrics-out-'))
  try {
    writeFileSync(join(directory, 'changes.json'), JSON.stringify(fragment('changes', 'changes', { jobStartMs: 0, jobEndMs: 1000 })))
    writeFileSync(join(directory, 'backend-api-1.json'), JSON.stringify(fragment('backend-api', 'backend-api-1', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 26000 })))
    const runMetaPath = join(output, 'run-meta.json')
    writeFileSync(runMetaPath, JSON.stringify({ queueDelayMs: 5000, expectedJobs: expectedMatrix, provenance: { mode: 'trusted', reason: 'test default-branch context' } }))
    const { spawnSync } = await import('node:child_process')
    // The entry points must work from any working directory (CI runs the suite from the repository root),
    // so resolve the bin path from the module and execute with a neutral cwd.
    const result = spawnSync(process.execPath, [join(binDir, 'aggregate.mjs'), directory, output, runMetaPath], { encoding: 'utf8', cwd: output })
    assert.equal(result.status, 0, result.stderr)
    const merged = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(output, 'run-metrics.json'), 'utf8')))
    assert.equal(merged.schemaVersion, 'aerolink-ci-run/v2')
    assert.equal(merged.queue.delayMs, 5000)
    assert.ok(merged.missing.some((entry) => entry.job === 'backend-api-2'))
    assert.equal(merged.criticalPath.job, null)
    const markdown = await import('node:fs').then((fs) => fs.readFileSync(join(output, 'run-metrics.md'), 'utf8'))
    assert.match(markdown, /Critical path: unavailable/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
    rmSync(output, { recursive: true, force: true })
  }
})

test('CLI integration: mark.mjs and write-fragment.mjs produce a valid fragment from a clean working directory', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-cli-'))
  try {
    const { spawnSync } = await import('node:child_process')
    const env = {
      ...process.env,
      METRICS_TIMING_FILE: join(directory, 'timing.json'),
      METRICS_FRAGMENT_PATH: join(directory, 'fragment.json'),
      METRICS_JOB_ID: 'backend-api',
      METRICS_JOB_NAME: 'API test suite (1/3)',
      METRICS_JOB_GROUP: 'backend-api',
      METRICS_JOB_INSTANCE: 'backend-api-1',
      METRICS_NEEDS: 'changes',
      METRICS_JOB_RESULT: 'success',
      METRICS_MATRIX: '{"shard":1}',
      METRICS_COUNTS_SOURCE: 'trx',
      METRICS_TRX_PATH: fileURLToPath(new URL('./fixtures/trx-failure.trx', import.meta.url)),
      METRICS_CLASS_DOCS_ONLY: 'false',
      METRICS_CLASS_BACKEND: 'true',
      METRICS_CACHE_NUGET: 'hit',
      GITHUB_RUN_ID: '781',
      GITHUB_RUN_ATTEMPT: '1',
      GITHUB_EVENT_NAME: 'pull_request',
      GITHUB_SHA: 'a'.repeat(40),
      GITHUB_REF: 'refs/pull/9/merge',
      GITHUB_WORKFLOW: 'Product quality gate',
      GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/heads/main',
      GITHUB_REPOSITORY: 'owner/repo',
      GITHUB_JOB: 'backend-api',
      GITHUB_WORKSPACE: repoRoot,
    }
    for (const name of ['job-start', 'setup-end', 'test-end']) {
      const marked = spawnSync(process.execPath, [join(binDir, 'mark.mjs'), name], { encoding: 'utf8', cwd: directory, env })
      assert.equal(marked.status, 0, marked.stderr)
    }
    const written = spawnSync(process.execPath, [join(binDir, 'write-fragment.mjs')], { encoding: 'utf8', cwd: directory, env })
    assert.equal(written.status, 0, written.stderr)
    const fragment = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(directory, 'fragment.json'), 'utf8')))
    validateFragment(fragment)
    assert.equal(fragment.job.group, 'backend-api')
    assert.equal(fragment.job.instance, 'backend-api-1')
    assert.equal(fragment.counts.expected, 4)
    assert.equal(fragment.counts.executed, 4)
    assert.equal(fragment.timings.testMs > 0, true)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('CLI integration: a malformed structured report becomes an explicit counts.missing reason, not a crash', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-cli-'))
  try {
    const { spawnSync } = await import('node:child_process')
    const malformedTrx = join(directory, 'bad.trx')
    writeFileSync(malformedTrx, '<TestRun><ResultSummary><Counters total="-1" /></ResultSummary></TestRun>')
    const env = {
      ...process.env,
      METRICS_TIMING_FILE: join(directory, 'timing.json'),
      METRICS_FRAGMENT_PATH: join(directory, 'fragment.json'),
      METRICS_JOB_ID: 'backend-api',
      METRICS_JOB_NAME: 'API test suite (1/3)',
      METRICS_JOB_GROUP: 'backend-api',
      METRICS_JOB_INSTANCE: 'backend-api-1',
      METRICS_NEEDS: 'changes',
      METRICS_JOB_RESULT: 'success',
      METRICS_COUNTS_SOURCE: 'trx',
      METRICS_TRX_PATH: malformedTrx,
      METRICS_CACHE_NUGET: 'true',
      GITHUB_RUN_ID: '782',
      GITHUB_RUN_ATTEMPT: '1',
      GITHUB_EVENT_NAME: 'pull_request',
      GITHUB_SHA: 'a'.repeat(40),
      GITHUB_REF: 'refs/pull/10/merge',
      GITHUB_WORKFLOW: 'Product quality gate',
      GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/heads/main',
      GITHUB_REPOSITORY: 'owner/repo',
      GITHUB_JOB: 'backend-api',
      GITHUB_WORKSPACE: repoRoot,
    }
    for (const name of ['job-start', 'setup-end', 'test-end']) {
      const marked = spawnSync(process.execPath, [join(binDir, 'mark.mjs'), name], { encoding: 'utf8', cwd: directory, env })
      assert.equal(marked.status, 0, marked.stderr)
    }
    const written = spawnSync(process.execPath, [join(binDir, 'write-fragment.mjs')], { encoding: 'utf8', cwd: directory, env })
    assert.equal(written.status, 0, written.stderr)
    const fragment = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(directory, 'fragment.json'), 'utf8')))
    validateFragment(fragment)
    assert.equal(fragment.counts.source, null)
    assert.equal(fragment.counts.executed, null)
    assert.match(fragment.counts.missing, /TRX parse failed/)
    assert.equal(fragment.cache.nuget, 'hit')
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('CLI integration: reversed timing markers become null durations with missing reasons, never zero', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-cli-'))
  try {
    const { spawnSync } = await import('node:child_process')
    const later = 2000
    const earlier = 1000
    writeFileSync(join(directory, 'timing.json'),
      `${JSON.stringify({ name: 'job-start', at: later })}\n${JSON.stringify({ name: 'setup-end', at: earlier })}\n`)
    const env = {
      ...process.env,
      METRICS_TIMING_FILE: join(directory, 'timing.json'),
      METRICS_FRAGMENT_PATH: join(directory, 'fragment.json'),
      METRICS_JOB_ID: 'backend-api',
      METRICS_JOB_NAME: 'API test suite (1/3)',
      METRICS_JOB_GROUP: 'backend-api',
      METRICS_JOB_INSTANCE: 'backend-api-1',
      METRICS_NEEDS: 'changes',
      METRICS_JOB_RESULT: 'success',
      GITHUB_RUN_ID: '783',
      GITHUB_RUN_ATTEMPT: '1',
      GITHUB_EVENT_NAME: 'pull_request',
      GITHUB_SHA: 'a'.repeat(40),
      GITHUB_REF: 'refs/pull/11/merge',
      GITHUB_WORKFLOW: 'Product quality gate',
      GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/heads/main',
      GITHUB_REPOSITORY: 'owner/repo',
      GITHUB_JOB: 'backend-api',
      GITHUB_WORKSPACE: repoRoot,
    }
    const written = spawnSync(process.execPath, [join(binDir, 'write-fragment.mjs')], { encoding: 'utf8', cwd: directory, env })
    assert.equal(written.status, 0, written.stderr)
    const fragment = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(directory, 'fragment.json'), 'utf8')))
    validateFragment(fragment)
    assert.equal(fragment.timings.setupMs, null)
    assert.equal(fragment.timings.setupEndMs, null)
    assert.match(fragment.timings.missing.setupEndMs, /precedes/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('CLI integration: playwright-json counts follow planned/executed/passed semantics for a clean+flaky+skipped mixture', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-cli-'))
  try {
    const { spawnSync } = await import('node:child_process')
    const tests = []
    for (let i = 0; i < 44; i++) tests.push({ title: `clean-${i}`, projectName: 'chromium', results: [{ status: 'passed', duration: 100 }], retries: 0 })
    tests.push({ title: 'flaky picker', projectName: 'chromium', results: [{ status: 'failed', duration: 100 }, { status: 'passed', duration: 200 }], retries: 1 })
    tests.push({ title: 'skipped capture', projectName: 'chromium', results: [{ status: 'skipped', duration: 0 }], retries: 0 })
    const report = {
      stats: { expected: 44, unexpected: 0, flaky: 1, skipped: 1 },
      suites: [{ title: 's', specs: [{ title: 'x', file: 'x.spec.ts', tests }], suites: [] }],
    }
    const reportPath = join(directory, 'report.json')
    writeFileSync(reportPath, JSON.stringify(report))
    const env = {
      ...process.env,
      METRICS_TIMING_FILE: join(directory, 'timing.json'),
      METRICS_FRAGMENT_PATH: join(directory, 'fragment.json'),
      METRICS_JOB_ID: 'browser-pr',
      METRICS_JOB_NAME: 'Browser journeys (1/4)',
      METRICS_JOB_GROUP: 'browser-pr',
      METRICS_JOB_INSTANCE: 'browser-pr-1',
      METRICS_NEEDS: 'changes',
      METRICS_JOB_RESULT: 'success',
      METRICS_COUNTS_SOURCE: 'playwright-json',
      METRICS_PLAYWRIGHT_JSON_PATH: reportPath,
      GITHUB_RUN_ID: '784',
      GITHUB_RUN_ATTEMPT: '1',
      GITHUB_EVENT_NAME: 'pull_request',
      GITHUB_SHA: 'a'.repeat(40),
      GITHUB_REF: 'refs/pull/12/merge',
      GITHUB_WORKFLOW: 'Product quality gate',
      GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/heads/main',
      GITHUB_REPOSITORY: 'owner/repo',
      GITHUB_JOB: 'browser-pr',
      GITHUB_WORKSPACE: repoRoot,
    }
    for (const name of ['job-start', 'setup-end', 'test-end']) {
      const marked = spawnSync(process.execPath, [join(binDir, 'mark.mjs'), name], { encoding: 'utf8', cwd: directory, env })
      assert.equal(marked.status, 0, marked.stderr)
    }
    const written = spawnSync(process.execPath, [join(binDir, 'write-fragment.mjs')], { encoding: 'utf8', cwd: directory, env })
    assert.equal(written.status, 0, written.stderr)
    const fragment = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(directory, 'fragment.json'), 'utf8')))
    validateFragment(fragment)
    assert.deepEqual(
      { expected: fragment.counts.expected, executed: fragment.counts.executed, passed: fragment.counts.passed, failed: fragment.counts.failed, skipped: fragment.counts.skipped, flaky: fragment.counts.flaky },
      { expected: 46, executed: 45, passed: 45, failed: 0, skipped: 1, flaky: 1 })
    assert.deepEqual(fragment.flakyTests, ['flaky picker'])
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('CLI integration: a stats-only flaky report makes missing title/spec detail explicit, not silent', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-cli-'))
  try {
    const { spawnSync } = await import('node:child_process')
    const reportPath = join(directory, 'report.json')
    writeFileSync(reportPath, JSON.stringify({ stats: { expected: 0, unexpected: 0, flaky: 1, skipped: 0 }, errors: [] }))
    const env = {
      ...process.env,
      METRICS_TIMING_FILE: join(directory, 'timing.json'),
      METRICS_FRAGMENT_PATH: join(directory, 'fragment.json'),
      METRICS_JOB_ID: 'browser-pr',
      METRICS_JOB_NAME: 'Browser journeys (1/4)',
      METRICS_JOB_GROUP: 'browser-pr',
      METRICS_JOB_INSTANCE: 'browser-pr-1',
      METRICS_NEEDS: 'changes',
      METRICS_JOB_RESULT: 'success',
      METRICS_COUNTS_SOURCE: 'playwright-json',
      METRICS_PLAYWRIGHT_JSON_PATH: reportPath,
      GITHUB_RUN_ID: '785',
      GITHUB_RUN_ATTEMPT: '1',
      GITHUB_EVENT_NAME: 'pull_request',
      GITHUB_SHA: 'a'.repeat(40),
      GITHUB_REF: 'refs/pull/13/merge',
      GITHUB_WORKFLOW: 'Product quality gate',
      GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/heads/main',
      GITHUB_REPOSITORY: 'owner/repo',
      GITHUB_JOB: 'browser-pr',
      GITHUB_WORKSPACE: repoRoot,
    }
    for (const name of ['job-start', 'setup-end', 'test-end']) {
      const marked = spawnSync(process.execPath, [join(binDir, 'mark.mjs'), name], { encoding: 'utf8', cwd: directory, env })
      assert.equal(marked.status, 0, marked.stderr)
    }
    const written = spawnSync(process.execPath, [join(binDir, 'write-fragment.mjs')], { encoding: 'utf8', cwd: directory, env })
    assert.equal(written.status, 0, written.stderr)
    const fragment = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(directory, 'fragment.json'), 'utf8')))
    validateFragment(fragment)
    assert.equal(fragment.counts.flaky, 1)
    assert.deepEqual(fragment.flakyTests, [])
    assert.equal(fragment.flakyTitlesUnavailable, true)
    assert.match(fragment.counts.missing, /suites hierarchy|flaky titles/i)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('a serialized flaky=1 fragment with no title evidence and no reason is rejected at read time', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    const crafted = fragment('browser-pr', 'browser-pr-1', { counts: { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null } })
    writeFileSync(join(directory, 'crafted.json'), JSON.stringify(crafted))
    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 0)
    assert.ok(missing.some((entry) => entry.job === 'crafted' && /flaky title count \(0\) does not match the flaky count \(1\)/.test(entry.reason)))

    // With an explicit unavailable reason the fragment is accepted as honest degraded data.
    const honest = fragment('browser-pr', 'browser-pr-1', { counts: { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: 'Report has no suites hierarchy; flaky titles unavailable.' } })
    honest.flakyTitlesUnavailable = true
    writeFileSync(join(directory, 'honest.json'), JSON.stringify(honest))
    const honestResult = readFragments(directory)
    assert.equal(honestResult.fragments.length, 1)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('flaky-title evidence is structural: partial, bogus-reason, and inconsistent-flag fragments are rejected', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    const cases = [
      {
        name: 'partial',
        mutate: (f) => {
          f.counts = { expected: 2, executed: 2, passed: 2, failed: 0, skipped: 0, flaky: 2, source: 'playwright-json', missing: null }
          f.flakyTests = ['only one']
        },
        pattern: /flaky title count \(1\) does not match the flaky count \(2\)/,
      },
      {
        name: 'bogus-reason',
        mutate: (f) => {
          f.counts = { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: 'Flaky title is present.' }
          f.flakyTests = []
        },
        pattern: /flaky title count \(0\) does not match the flaky count \(1\)/,
      },
      {
        name: 'inconsistent-truncation',
        mutate: (f) => {
          f.counts = { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null }
          f.flakyTests = []
          f.flakyTitlesTruncated = true
        },
        pattern: /requires flaky > 20/,
      },
      {
        name: 'unavailable-with-titles',
        mutate: (f) => {
          f.counts = { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: 'unavailable' }
          f.flakyTests = ['a title']
          f.flakyTitlesUnavailable = true
        },
        pattern: /unavailable flaky titles must have zero retained titles/,
      },
      {
        name: 'unavailable-without-reason',
        mutate: (f) => {
          f.counts = { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null }
          f.flakyTests = []
          f.flakyTitlesUnavailable = true
        },
        pattern: /require a bounded reason/,
      },
      {
        name: 'truncated-without-reason',
        mutate: (f) => {
          f.counts = { expected: 25, executed: 25, passed: 25, failed: 0, skipped: 0, flaky: 25, source: 'playwright-json', missing: null }
          f.flakyTests = Array.from({ length: 20 }, (_, i) => `t-${i}`)
          f.flakyTitlesTruncated = true
        },
        pattern: /require a bounded reason/,
      },
      {
        name: 'truncated-at-20',
        mutate: (f) => {
          f.counts = { expected: 20, executed: 20, passed: 20, failed: 0, skipped: 0, flaky: 20, source: 'playwright-json', missing: 'truncated' }
          f.flakyTests = Array.from({ length: 20 }, (_, i) => `t-${i}`)
          f.flakyTitlesTruncated = true
        },
        pattern: /requires flaky > 20/,
      },
      {
        name: 'truncated-19-titles',
        mutate: (f) => {
          f.counts = { expected: 25, executed: 25, passed: 25, failed: 0, skipped: 0, flaky: 25, source: 'playwright-json', missing: 'truncated' }
          f.flakyTests = Array.from({ length: 19 }, (_, i) => `t-${i}`)
          f.flakyTitlesTruncated = true
        },
        pattern: /must retain exactly 20/,
      },
      {
        name: 'zero-flaky-unavailable',
        mutate: (f) => {
          f.counts = { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 0, source: 'playwright-json', missing: 'unavailable' }
          f.flakyTests = []
          f.flakyTitlesUnavailable = true
        },
        pattern: /requires flaky > 0/,
      },
      {
        name: 'zero-flaky-truncated',
        mutate: (f) => {
          f.counts = { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 0, source: 'playwright-json', missing: 'truncated' }
          f.flakyTests = []
          f.flakyTitlesTruncated = true
        },
        pattern: /requires flaky > 20/,
      },
    ]
    for (const entry of cases) {
      const crafted = fragment('browser-pr', 'browser-pr-1')
      entry.mutate(crafted)
      writeFileSync(join(directory, `${entry.name}.json`), JSON.stringify(crafted))
    }
    const complete = fragment('browser-pr', 'browser-pr-1', { counts: { expected: 2, executed: 2, passed: 2, failed: 0, skipped: 0, flaky: 2, source: 'playwright-json', missing: null } })
    complete.flakyTests = ['alpha', 'beta']
    writeFileSync(join(directory, 'complete.json'), JSON.stringify(complete))

    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 1)
    assert.equal(fragments[0].flakyTests.length, 2)
    for (const entry of cases) {
      assert.ok(missing.some((item) => item.job === entry.name && entry.pattern.test(item.reason)), `${entry.name} should be rejected`)
    }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('non-Playwright fragments cannot carry flaky titles or title-state flags', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    const forged = fragment('backend-api', 'backend-api-1', { counts: { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
    forged.flakyTests = ['forged flaky title']
    writeFileSync(join(directory, 'forged.json'), JSON.stringify(forged))

    const flagged = fragment('changes', 'changes')
    flagged.flakyTitlesUnavailable = true
    writeFileSync(join(directory, 'flagged.json'), JSON.stringify(flagged))

    const withFlakyCount = fragment('backend-api', 'backend-api-1', { counts: { expected: 1, executed: 1, passed: 1, failed: 0, skipped: 0, flaky: 3, source: 'trx', missing: null } })
    writeFileSync(join(directory, 'with-flaky-count.json'), JSON.stringify(withFlakyCount))

    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 0)
    assert.equal(missing.length, 3)
    assert.ok(missing.some((entry) => entry.job === 'forged' && /flaky titles are only valid for playwright-json/.test(entry.reason)))
    assert.ok(missing.some((entry) => entry.job === 'flagged' && /flakyTitlesUnavailable is only valid for playwright-json/.test(entry.reason)))
    assert.ok(missing.some((entry) => entry.job === 'with-flaky-count' && /flaky count is only valid for playwright-json/.test(entry.reason)))
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('aggregation and Markdown expose per-job unavailable/truncated flaky-title evidence', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-cli-'))
  const output = mkdtempSync(join(tmpdir(), 'ci-metrics-out-'))
  try {
    const { spawnSync } = await import('node:child_process')
    const reportPath = join(directory, 'report.json')
    writeFileSync(reportPath, JSON.stringify({ stats: { expected: 0, unexpected: 0, flaky: 2, skipped: 0 }, errors: [] }))
    const env = {
      ...process.env,
      METRICS_TIMING_FILE: join(directory, 'timing.json'),
      METRICS_FRAGMENT_PATH: join(directory, 'fragment.json'),
      METRICS_JOB_ID: 'browser-pr',
      METRICS_JOB_NAME: 'Browser journeys (1/4)',
      METRICS_JOB_GROUP: 'browser-pr',
      METRICS_JOB_INSTANCE: 'browser-pr-1',
      METRICS_NEEDS: 'changes',
      METRICS_JOB_RESULT: 'success',
      METRICS_COUNTS_SOURCE: 'playwright-json',
      METRICS_PLAYWRIGHT_JSON_PATH: reportPath,
      GITHUB_RUN_ID: '787',
      GITHUB_RUN_ATTEMPT: '1',
      GITHUB_EVENT_NAME: 'pull_request',
      GITHUB_SHA: 'a'.repeat(40),
      GITHUB_REF: 'refs/pull/15/merge',
      GITHUB_WORKFLOW: 'Product quality gate',
      GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/heads/main',
      GITHUB_REPOSITORY: 'owner/repo',
      GITHUB_JOB: 'browser-pr',
      GITHUB_WORKSPACE: repoRoot,
    }
    for (const name of ['job-start', 'setup-end', 'test-end']) {
      const marked = spawnSync(process.execPath, [join(binDir, 'mark.mjs'), name], { encoding: 'utf8', cwd: directory, env })
      assert.equal(marked.status, 0, marked.stderr)
    }
    const written = spawnSync(process.execPath, [join(binDir, 'write-fragment.mjs')], { encoding: 'utf8', cwd: directory, env })
    assert.equal(written.status, 0, written.stderr)

    const meta = join(output, 'run-meta.json')
    writeFileSync(meta, JSON.stringify({ expectedJobs: [{ group: 'browser-pr', instance: 'browser-pr-1', needs: [] }] }))
    const aggregated = spawnSync(process.execPath, [join(binDir, 'aggregate.mjs'), directory, output, meta], { encoding: 'utf8', cwd: output })
    assert.equal(aggregated.status, 0, aggregated.stderr)
    const merged = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(output, 'run-metrics.json'), 'utf8')))
    assert.deepEqual(merged.flakyTitleEvidence.unavailable, ['browser-pr-1'])
    assert.equal(merged.jobs[0].flakyTitlesUnavailable, true)
    assert.equal(merged.jobs[0].flakyTitlesTruncated, false)
    assert.match(merged.jobs[0].flakyTitleMissingReason, /suites hierarchy/)
    const markdown = await import('node:fs').then((fs) => fs.readFileSync(join(output, 'run-metrics.md'), 'utf8'))
    assert.match(markdown, /exact flaky titles unavailable/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
    rmSync(output, { recursive: true, force: true })
  }
})

test('more than twenty flaky titles are truncated explicitly, not silently', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-cli-'))
  try {
    const { spawnSync } = await import('node:child_process')
    const tests = Array.from({ length: 25 }, (_, i) => ({
      title: `flaky-${i}`,
      projectName: 'chromium',
      results: [{ status: 'failed', duration: 100 }, { status: 'passed', duration: 200 }],
      retries: 1,
    }))
    const report = { stats: { expected: 0, unexpected: 0, flaky: 25, skipped: 0 }, suites: [{ title: 's', specs: [{ title: 'x', file: 'x.spec.ts', tests }], suites: [] }] }
    const reportPath = join(directory, 'report.json')
    writeFileSync(reportPath, JSON.stringify(report))
    const env = {
      ...process.env,
      METRICS_TIMING_FILE: join(directory, 'timing.json'),
      METRICS_FRAGMENT_PATH: join(directory, 'fragment.json'),
      METRICS_JOB_ID: 'browser-pr',
      METRICS_JOB_NAME: 'Browser journeys (1/4)',
      METRICS_JOB_GROUP: 'browser-pr',
      METRICS_JOB_INSTANCE: 'browser-pr-1',
      METRICS_NEEDS: 'changes',
      METRICS_JOB_RESULT: 'success',
      METRICS_COUNTS_SOURCE: 'playwright-json',
      METRICS_PLAYWRIGHT_JSON_PATH: reportPath,
      GITHUB_RUN_ID: '786',
      GITHUB_RUN_ATTEMPT: '1',
      GITHUB_EVENT_NAME: 'pull_request',
      GITHUB_SHA: 'a'.repeat(40),
      GITHUB_REF: 'refs/pull/14/merge',
      GITHUB_WORKFLOW: 'Product quality gate',
      GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/heads/main',
      GITHUB_REPOSITORY: 'owner/repo',
      GITHUB_JOB: 'browser-pr',
      GITHUB_WORKSPACE: repoRoot,
    }
    for (const name of ['job-start', 'setup-end', 'test-end']) {
      const marked = spawnSync(process.execPath, [join(binDir, 'mark.mjs'), name], { encoding: 'utf8', cwd: directory, env })
      assert.equal(marked.status, 0, marked.stderr)
    }
    const written = spawnSync(process.execPath, [join(binDir, 'write-fragment.mjs')], { encoding: 'utf8', cwd: directory, env })
    assert.equal(written.status, 0, written.stderr)
    const fragment = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(directory, 'fragment.json'), 'utf8')))
    validateFragment(fragment)
    assert.equal(fragment.flakyTests.length, 20)
    assert.equal(fragment.flakyTitlesTruncated, true)
    assert.match(fragment.counts.missing, /truncated to 20 of 25/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('PR-controlled expectedJobs are used for the graph but labelled shadow until trusted validation', () => {
  const fragments = [
    fragment('changes', 'changes', { jobStartMs: 0, jobEndMs: 1000 }),
    fragment('backend-api', 'backend-api-1', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 20000 }),
    fragment('gate', 'gate', { needs: ['changes', 'backend-api'], jobStartMs: 20000, jobEndMs: 21000 }),
  ]
  const runMeta = {
    expectedJobs: [
      { group: 'changes', instance: 'changes', needs: [] },
      { group: 'backend-api', instance: 'backend-api-1', needs: ['changes'] },
      { group: 'gate', instance: 'gate', needs: ['changes', 'backend-api'] },
    ],
    expectedRun: run,
    provenance: { mode: 'shadow', reason: 'Same-workflow checkout is PR-controlled.' },
    skippedJobs: [{ group: 'browser-pr', instance: 'browser-pr-1', reason: 'browser classification is false' }],
  }
  const merged = aggregateFragments({ fragments, runMeta })
  assert.equal(merged.runIdentityTrusted, false)
  assert.equal(merged.provenance.mode, 'shadow')
  assert.equal(merged.criticalPath.trustedTopology, false)
  assert.equal(merged.criticalPath.expectedTopology, true)
  assert.equal(merged.criticalPath.job, 'gate')
  assert.deepEqual(merged.skipped, [{ group: 'browser-pr', instance: 'browser-pr-1', reason: 'browser classification is false' }])
  const markdown = renderMarkdown(merged)
  assert.match(markdown, /Deliberately skipped jobs: 1/)
  assert.match(markdown, /browser-pr-1: browser classification is false/)
  assert.match(markdown, /shadow/)
})

test('shadow expected-job metadata still wins over fragment claims and records disagreements', () => {
  const fragments = [
    fragment('a', 'a', { jobStartMs: 0, jobEndMs: 1000 }),
    fragment('b', 'b', { needs: ['does-not-exist'], jobStartMs: 1000, jobEndMs: 12000 }),
  ]
  const merged = aggregateFragments({
    fragments,
    runMeta: {
      expectedJobs: [
        { group: 'a', instance: 'a', needs: [] },
        { group: 'b', instance: 'b', needs: ['a'] },
      ],
      provenance: { mode: 'shadow', reason: 'PR-controlled checkout' },
    },
  })
  assert.equal(merged.criticalPath.job, 'b')
  assert.deepEqual(merged.criticalPath.path, ['a', 'b'])
  assert.deepEqual(merged.criticalPath.topologyDisagreements, ['b'])
  assert.equal(merged.criticalPath.trustedTopology, false)
  assert.equal(merged.criticalPath.expectedTopology, true)
})

test('missing test families are modelled separately from the sourced totals', () => {
  const fragments = [
    fragment('backend-api', 'backend-api-1', { needs: ['changes'], counts: { expected: 100, executed: 100, passed: 100, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } }),
    fragment('script-contracts', 'script-contracts', { needs: ['changes'], counts: { expected: null, executed: null, passed: null, failed: null, skipped: null, flaky: null, source: null, missing: 'This family has no structured test output.' } }),
  ]
  const runMeta = {
    expectedJobs: [
      { group: 'backend-api', instance: 'backend-api-1', needs: ['changes'] },
      { group: 'script-contracts', instance: 'script-contracts', needs: ['changes'] },
    ],
    provenance: { mode: 'trusted', reason: 'test default-branch context' },
  }
  const merged = aggregateFragments({ fragments, runMeta })
  assert.equal(merged.counts.expected, 100)
  assert.equal(merged.countsModel.totalIsPartial, true)
  assert.deepEqual(merged.countsModel.missingFamilies, [{ instance: 'script-contracts', reason: 'This family has no structured test output.' }])
  const markdown = renderMarkdown(merged)
  assert.match(markdown, /sourced families with structured output only/)
  assert.match(markdown, /Families without structured counts/)
  assert.match(markdown, /script-contracts/)
})

test('postTestMs is the interval between test-end and writer, never called upload/cleanup', () => {
  const merged = aggregateFragments({ fragments: [fragment('changes', 'changes', { jobStartMs: 0, setupEndMs: 2000, testEndMs: 9000, jobEndMs: 10000 })] })
  assert.equal(merged.jobs[0].timings.postTestMs, 1000)
  assert.equal('uploadAndCleanupMs' in merged.jobs[0].timings, false)
  const markdown = renderMarkdown(merged)
  assert.match(markdown, /\| After test \|/)
  assert.doesNotMatch(markdown, /Upload\/cleanup/)
})
