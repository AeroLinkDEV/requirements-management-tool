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
      uploadAndCleanupMs: testEndMs !== null && jobEndMs !== null ? jobEndMs - testEndMs : null,
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
  const merged = aggregateFragments({ fragments, runMeta: { expectedJobs: expectedMatrix } })
  assert.equal(merged.missing.length, 0)
  assert.equal(merged.jobs.length, 9)
  assert.equal(merged.criticalPath.job, 'gate')
  assert.equal(merged.criticalPath.durationMs, 31000)
  assert.deepEqual(merged.criticalPath.path, ['changes', 'browser-pr-1', 'gate'])
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

test('run-inconsistent fragments are excluded from every derived aggregate, not merely flagged', () => {
  const valid = fragment('changes', 'changes', { counts: { expected: 100, executed: 100, passed: 100, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
  const foreign = fragment('backend-api', 'backend-api-1', { counts: { expected: 900, executed: 900, passed: 900, failed: 0, skipped: 0, flaky: null, source: 'trx', missing: null } })
  foreign.run.id = 9999
  foreign.cache.nuget = 'hit'
  foreign.flakyTests = ['foreign flaky']
  const merged = aggregateFragments({ fragments: [valid, foreign] })
  assert.equal(merged.jobs.length, 1)
  assert.equal(merged.jobs[0].instance, 'changes')
  assert.equal(merged.counts.expected, 100)
  assert.equal(merged.counts.executed, 100)
  assert.equal(merged.cache.nuget.hit, 0)
  assert.deepEqual(merged.flakyTests, [])
  assert.ok(merged.missing.some((entry) => entry.job === 'backend-api-1' && /Run identity does not match/.test(entry.reason)))
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
    fragment('browser-pr', 'browser-pr-1', { flakyTests: ['alpha spec'], counts: { expected: 40, executed: 40, passed: 39, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null } }),
    fragment('browser-pr', 'browser-pr-2', { flakyTests: ['alpha spec', 'beta spec'], counts: { expected: 40, executed: 40, passed: 39, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null } }),
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
    writeFileSync(runMetaPath, JSON.stringify({ queueDelayMs: 5000, expectedJobs: expectedMatrix }))
    const { spawnSync } = await import('node:child_process')
    // The entry points must work from any working directory (CI runs the suite from the repository root),
    // so resolve the bin path from the module and execute with a neutral cwd.
    const result = spawnSync(process.execPath, [join(binDir, 'aggregate.mjs'), directory, output, runMetaPath], { encoding: 'utf8', cwd: output })
    assert.equal(result.status, 0, result.stderr)
    const merged = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(output, 'run-metrics.json'), 'utf8')))
    assert.equal(merged.schemaVersion, 'aerolink-ci-run/v1')
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
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
