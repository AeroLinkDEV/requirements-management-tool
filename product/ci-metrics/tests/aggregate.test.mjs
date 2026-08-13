import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, writeFileSync, rmSync, mkdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { readFragments, aggregateFragments, criticalPath } from '../lib/aggregate.mjs'
import { buildFragment } from '../lib/fragment.mjs'

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

function fragment(jobId, { needs = [], result = 'success', jobStartMs = 0, jobEndMs = 10000, setupEndMs = 2000, testEndMs = 9000, counts = null, cache = {}, classification = { docsOnly: false, backend: true, client: false, browser: false, postgresql: false, unavailable: false }, flakyTests = [] } = {}) {
  return buildFragment({
    run,
    job: { id: jobId, name: jobId, needs, result },
    timings: { jobStartMs, setupEndMs, testEndMs, jobEndMs, setupMs: setupEndMs - jobStartMs, testMs: testEndMs - setupEndMs, uploadAndCleanupMs: jobEndMs - testEndMs, missing: {} },
    counts: counts ?? { expected: null, executed: null, passed: null, failed: null, skipped: null, flaky: null, source: null, missing: 'no structured output' },
    slowest: [],
    flakyTests,
    cache: { nuget: cache.nuget ?? null, npm: cache.npm ?? null, chromium: cache.chromium ?? null, missing: {} },
    classification,
    missing: {},
  })
}

test('valid fragments aggregate and the critical path follows the dependency DAG', () => {
  const fragments = [
    fragment('changes', { jobStartMs: 0, jobEndMs: 1000 }),
    fragment('backend-api', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 26000 }),
    fragment('client', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 8000 }),
    fragment('gate', { needs: ['backend-api', 'client'], jobStartMs: 26000, jobEndMs: 27000 }),
  ]
  const merged = aggregateFragments({ fragments })
  assert.equal(merged.jobs.length, 4)
  assert.equal(merged.criticalPath.job, 'gate')
  assert.equal(merged.criticalPath.durationMs, 27000)
  assert.deepEqual(merged.criticalPath.path, ['changes', 'backend-api', 'gate'])
})

test('a job without duration does not claim zero on the critical path', () => {
  const fragments = [fragment('changes', { jobStartMs: 0, jobEndMs: 1000 }), fragment('backend-api', { needs: ['changes'], jobStartMs: null, jobEndMs: null, setupEndMs: null, testEndMs: null })]
  const merged = aggregateFragments({ fragments })
  assert.ok(merged.criticalPath.missingDuration.includes('backend-api'))
  assert.equal(merged.criticalPath.durationMs, 1000)
})

test('missing and malformed fragments are reported as missing, never as zero', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  try {
    writeFileSync(join(directory, 'valid.json'), JSON.stringify(fragment('changes', { jobStartMs: 0, jobEndMs: 1000 })))
    writeFileSync(join(directory, 'malformed.json'), '{not json')
    writeFileSync(join(directory, 'wrong-schema.json'), JSON.stringify({ ...fragment('client'), schemaVersion: 'aerolink-ci-fragment/old' }))
    const { fragments, missing } = readFragments(directory)
    assert.equal(fragments.length, 1)
    assert.equal(missing.length, 2)
    assert.ok(missing.some((entry) => entry.job === 'malformed'))
    assert.ok(missing.some((entry) => entry.job === 'wrong-schema'))
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
    assert.deepEqual(missing, [])
    const merged = aggregateFragments({ fragments, missing })
    assert.equal(merged.criticalPath.job, null)
    assert.equal(merged.jobs.length, 0)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('expected/actual count mismatch is carried into the merged record', () => {
  const fragments = [fragment('backend-api', { counts: { expected: 160, executed: 159, passed: 158, failed: 1, skipped: 0, flaky: null, source: 'trx', missing: null } })]
  const merged = aggregateFragments({ fragments })
  assert.equal(merged.counts.expected, 160)
  assert.equal(merged.counts.executed, 159)
  assert.notEqual(merged.counts.expected, merged.counts.executed)
})

test('flaky titles from all fragments are unioned and bounded', () => {
  const fragments = [
    fragment('browser-1', { flakyTests: ['alpha spec'], counts: { expected: 40, executed: 40, passed: 39, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null } }),
    fragment('browser-2', { flakyTests: ['alpha spec', 'beta spec'], counts: { expected: 40, executed: 40, passed: 39, failed: 0, skipped: 0, flaky: 1, source: 'playwright-json', missing: null } }),
  ]
  const merged = aggregateFragments({ fragments })
  assert.deepEqual(merged.flakyTests, ['alpha spec', 'beta spec'])
  assert.equal(merged.counts.flaky, 2)
})

test('comparable-run grouping counts classification flags per fragment', () => {
  const fragments = [
    fragment('backend-api', { classification: { docsOnly: false, backend: true, client: false, browser: false, postgresql: false, unavailable: false } }),
    fragment('browser-1', { classification: { docsOnly: false, backend: false, client: false, browser: true, postgresql: false, unavailable: false } }),
    fragment('changes', { classification: { docsOnly: true, backend: false, client: false, browser: false, postgresql: false, unavailable: false } }),
  ]
  const merged = aggregateFragments({ fragments })
  assert.equal(merged.classifications.backend, 1)
  assert.equal(merged.classifications.browser, 1)
  assert.equal(merged.classifications.docsOnly, 1)
})

test('runMeta queue delay is reported when supplied and unavailable otherwise', () => {
  const fragments = [fragment('changes')]
  assert.equal(aggregateFragments({ fragments }).queue.delayMs, null)
  assert.match(aggregateFragments({ fragments }).queue.unavailableReason, /rolling collector/)
  assert.equal(aggregateFragments({ fragments, runMeta: { queueDelayMs: 12000 } }).queue.delayMs, 12000)
})

test('criticalPath guards against a cycle without hanging', () => {
  const a = fragment('a', { needs: ['b'] })
  const b = fragment('b', { needs: ['a'] })
  const path = criticalPath([a, b])
  assert.ok(Array.isArray(path.path))
})

test('readFragments tolerates a missing directory', () => {
  const { fragments, missing } = readFragments(join(tmpdir(), 'does-not-exist-ci-metrics'))
  assert.equal(fragments.length, 0)
  assert.equal(missing.length, 1)
  assert.match(missing[0].reason, /Could not read fragment directory/)
})

test('aggregate output is bounded and contains no secret-like values', () => {
  const fragments = [fragment('changes'), fragment('backend-api', { needs: ['changes'] })]
  const merged = aggregateFragments({ fragments })
  const json = JSON.stringify(merged)
  assert.ok(Buffer.byteLength(json, 'utf8') < 256 * 1024)
  assert.ok(!/password|secret|authorization/i.test(json))
})

test('a failed or cancelled job result is preserved in the merged record', () => {
  const fragments = [fragment('backend-api', { result: 'failure' }), fragment('browser-1', { result: 'cancelled' })]
  const merged = aggregateFragments({ fragments })
  assert.equal(merged.jobs.find((job) => job.id === 'backend-api').result, 'failure')
  assert.equal(merged.jobs.find((job) => job.id === 'browser-1').result, 'cancelled')
})

test('writeFragment parity: a fixture directory plus a valid run-meta produces a full run record', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-metrics-'))
  const output = mkdtempSync(join(tmpdir(), 'ci-metrics-out-'))
  try {
    writeFileSync(join(directory, 'changes.json'), JSON.stringify(fragment('changes', { jobStartMs: 0, jobEndMs: 1000 })))
    writeFileSync(join(directory, 'backend-api.json'), JSON.stringify(fragment('backend-api', { needs: ['changes'], jobStartMs: 1000, jobEndMs: 26000 })))
    const runMetaPath = join(output, 'run-meta.json')
    writeFileSync(runMetaPath, JSON.stringify({ queueDelayMs: 5000 }))
    const { spawnSync } = await import('node:child_process')
    const result = spawnSync(process.execPath, ['bin/aggregate.mjs', directory, output, runMetaPath], { encoding: 'utf8' })
    assert.equal(result.status, 0, result.stderr)
    const merged = JSON.parse(await import('node:fs').then((fs) => fs.readFileSync(join(output, 'run-metrics.json'), 'utf8')))
    assert.equal(merged.schemaVersion, 'aerolink-ci-run/v1')
    assert.equal(merged.queue.delayMs, 5000)
    assert.equal(merged.criticalPath.job, 'backend-api')
    const markdown = await import('node:fs').then((fs) => fs.readFileSync(join(output, 'run-metrics.md'), 'utf8'))
    assert.match(markdown, /Critical path/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
    rmSync(output, { recursive: true, force: true })
  }
})
