import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildFragment, validateFragment, validationErrors, looksLikeCredential, MAX_FRAGMENT_BYTES } from '../lib/fragment.mjs'

const base = {
  run: {
    id: 1234,
    attempt: 1,
    event: 'pull_request',
    sha: 'a'.repeat(40),
    tree: 'b'.repeat(40),
    ref: 'refs/pull/1/merge',
    pr: 1,
    workflow: 'Product quality gate',
    workflowRef: 'seanmccarthyns/requirements-management-tool/.github/workflows/ci.yml@refs/heads/main',
    repository: 'seanmccarthyns/requirements-management-tool',
  },
  job: { group: 'backend-api', instance: 'backend-api-1', name: 'API test suite (1/3)', needs: ['changes'], result: 'success', matrix: { shard: 1 } },
  timings: { jobStartMs: 1000, setupEndMs: 5000, testEndMs: 30000, jobEndMs: 31000, setupMs: 4000, testMs: 25000, postTestMs: 1000, missing: {} },
  counts: { expected: 160, executed: 159, passed: 158, failed: 1, skipped: 1, flaky: null, source: 'trx', missing: null },
  cache: { nuget: 'hit', npm: null, chromium: null, missing: { npm: 'no npm cache in this job' } },
  classification: { docsOnly: false, backend: true, client: false, browser: false, postgresql: false, unavailable: false },
  missing: {},
}

test('buildFragment produces a valid bounded fragment with group and instance identity', () => {
  const fragment = buildFragment(base)
  validateFragment(fragment)
  assert.equal(fragment.schemaVersion, 'aerolink-ci-fragment/v2')
  assert.equal(fragment.job.group, 'backend-api')
  assert.equal(fragment.job.instance, 'backend-api-1')
  assert.deepEqual(fragment.job.matrix, { shard: 1 })
})

test('instance identity falls back to the group when no explicit instance is supplied', () => {
  const fragment = buildFragment({ ...base, job: { ...base.job, instance: undefined } })
  assert.equal(fragment.job.instance, 'backend-api')
})

test('an unknown schema version is rejected by the schema-driven validator', () => {
  const fragment = buildFragment(base)
  fragment.schemaVersion = 'aerolink-ci-fragment/older'
  assert.ok(validationErrors(fragment).some((e) => e.includes('constant')))
})

test('the v2 timing field names the post-test interval, not the untracked upload/cleanup', () => {
  const fragment = buildFragment(base)
  assert.equal(fragment.timings.postTestMs, 1000)
  assert.equal('uploadAndCleanupMs' in fragment.timings, false)
  const stale = buildFragment(base)
  stale.timings.uploadAndCleanupMs = 1000
  assert.ok(validationErrors(stale).some((e) => e.includes('timings')))
})

test('node-junit counts must be internally consistent', () => {
  const junitBase = { ...base, counts: { expected: 10, executed: 9, passed: 9, failed: 0, skipped: 1, flaky: null, source: 'node-junit', missing: null } }
  const valid = buildFragment(junitBase)
  assert.equal(validationErrors(valid).length, 0)

  const bad = buildFragment({ ...junitBase, counts: { ...junitBase.counts, expected: 11, executed: 9, passed: 9, failed: 0, skipped: 1, flaky: null, source: 'node-junit', missing: null } })
  assert.ok(validationErrors(bad).some((e) => e.includes('expected must equal executed + skipped')))

  const flaky = buildFragment({ ...junitBase, counts: { ...junitBase.counts, flaky: 2, source: 'node-junit' } })
  assert.ok(validationErrors(flaky).some((e) => e.includes('flaky count is only valid for playwright-json')))
})

test('malformed nested fields are rejected, not passed to aggregation', () => {
  const fragment = buildFragment(base)
  fragment.job.needs = null
  assert.ok(validationErrors(fragment).some((e) => e.includes('needs')))

  const badCounts = buildFragment(base)
  badCounts.counts.expected = 'not-a-number'
  assert.ok(validationErrors(badCounts).some((e) => e.includes('counts.expected')))

  const badCache = buildFragment(base)
  badCache.cache.nuget = 'partial'
  assert.ok(validationErrors(badCache).some((e) => e.includes('cache.nuget')))

  const badResult = buildFragment(base)
  badResult.job.result = 'mystery'
  assert.ok(validationErrors(badResult).some((e) => e.includes('job.result')))
})

test('unexpected nested fields are rejected', () => {
  const fragment = buildFragment(base)
  fragment.timings.surprise = 1
  assert.ok(validationErrors(fragment).some((e) => e.includes('timings')))
})

test('legitimate security-vocabulary test titles are retained', () => {
  const fragment = buildFragment({
    ...base,
    flakyTests: ['Password visibility test', 'token refresh keeps the session', 'cookie consent banner'],
    slowest: [{ name: 'AeroLink.Api.Tests.SecurityHardeningTests', durationMs: 500, kind: 'class' }],
  })
  assert.deepEqual(fragment.flakyTests, ['Password visibility test', 'token refresh keeps the session', 'cookie consent banner'])
  assert.equal(fragment.slowest[0].name, 'AeroLink.Api.Tests.SecurityHardeningTests')
})

test('credential-shaped values are refused wherever they appear', () => {
  assert.ok(looksLikeCredential('Password=hunter2'))
  assert.ok(looksLikeCredential('Authorization: Bearer abcdefghijklmnop'))
  assert.ok(looksLikeCredential('Host=db.example;User ID=sa;Password=x'))
  assert.ok(looksLikeCredential('BEGIN RSA PRIVATE KEY'))
  assert.ok(!looksLikeCredential('Password visibility test'))
  assert.ok(!looksLikeCredential('release approval'))
  assert.throws(() => buildFragment({ ...base, job: { ...base.job, name: 'job with Password=hunter2 in its name' } }))
  assert.throws(() => buildFragment({ ...base, flakyTests: ['Authorization: Bearer abcdefghijklmnop'] }))
})

test('slowest and flaky lists are bounded', () => {
  const fragment = buildFragment({
    ...base,
    slowest: Array.from({ length: 200 }, (_, i) => ({ name: `class-${i}`, durationMs: i, kind: 'class' })),
    flakyTests: Array.from({ length: 200 }, (_, i) => `flaky-${i}`),
  })
  assert.equal(fragment.slowest.length, 50)
  assert.equal(fragment.flakyTests.length, 20)
  assert.ok(Buffer.byteLength(JSON.stringify(fragment), 'utf8') <= MAX_FRAGMENT_BYTES)
})

test('an oversized fragment fails loudly instead of publishing truncated telemetry', () => {
  const huge = buildFragment({
    ...base,
    slowest: Array.from({ length: 50 }, (_, i) => ({ name: `class-${i}-${'x'.repeat(5000)}`, durationMs: i, kind: 'class' })),
  })
  assert.ok(Buffer.byteLength(JSON.stringify(huge), 'utf8') <= MAX_FRAGMENT_BYTES)
})

test('failed, cancelled, and skipped results are preserved as distinct outcomes', () => {
  for (const result of ['failure', 'cancelled', 'skipped']) {
    const fragment = buildFragment({ ...base, job: { ...base.job, result } })
    assert.equal(fragment.job.result, result)
  }
})

test('reversed or inconsistent timing markers are rejected at read time, never published as zero', () => {
  const reversed = buildFragment(base)
  reversed.timings.jobStartMs = 1000
  reversed.timings.setupEndMs = 500
  assert.ok(validationErrors(reversed).some((e) => e.includes('setupEndMs precedes jobStartMs')))

  const derivedMismatch = buildFragment(base)
  derivedMismatch.timings.setupMs = 999
  assert.ok(validationErrors(derivedMismatch).some((e) => e.includes('derived duration does not match')))

  const consistent = buildFragment(base)
  assert.equal(validationErrors(consistent).length, 0)
})
