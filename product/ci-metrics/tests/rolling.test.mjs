import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  median, percentile, classifyRun, runDurationMs, jobGroupDurations, queueAndCancellation,
  flakeTrend, cacheTrend, rollingStats, detectRegressions, validateRunRecord, recordFormat, buildRollingReport, trackerBody,
  fullGatesPerMerge,
} from '../lib/rolling.mjs'

function record(overrides = {}) {
  return {
    schemaVersion: 'aerolink-ci-run/v2',
    run: {
      id: 1,
      attempt: 1,
      event: 'pull_request',
      sha: 'a'.repeat(40),
      tree: 'b'.repeat(40),
      ref: 'refs/pull/1/merge',
      pr: 1,
      baseSha: 'c'.repeat(40),
      headSha: 'd'.repeat(40),
      workflow: 'Product quality gate',
      workflowRef: 'repo/.github/workflows/ci.yml@refs/pull/1/merge',
      repository: 'owner/repo',
    },
    jobs: [
      { group: 'changes', instance: 'changes', sourceAttempt: 1, timings: { jobStartMs: 0, jobEndMs: 1000 } },
      { group: 'backend-api', instance: 'backend-api-1', sourceAttempt: 1, timings: { jobStartMs: 0, jobEndMs: 30000 } },
      { group: 'gate', instance: 'gate', sourceAttempt: 1, timings: { jobStartMs: 0, jobEndMs: 31000 } },
    ],
    criticalPath: { job: 'gate', durationMs: 31000, path: ['changes', 'backend-api-1', 'gate'], unavailableReason: null },
    counts: { expected: 100, executed: 100, passed: 100, failed: 0, skipped: 0, flaky: 0 },
    cache: { nuget: { hit: 1, miss: 0 }, npm: { hit: 0, miss: 0 }, chromium: { hit: 1, miss: 0 } },
    flakyTests: [],
    classifications: { docsOnly: 0, backend: 1, client: 0, browser: 0, postgresql: 0, unavailable: 0 },
    conclusion: 'success',
    ...overrides,
  }
}

test('median and percentile are deterministic nearest-rank statistics', () => {
  assert.equal(median([]), null)
  assert.equal(median([1, 2, 3, 4]), 2.5)
  assert.equal(median([5, 1, 3]), 3)
  assert.equal(percentile([1, 2, 3, 4], 95), 4)
  assert.equal(percentile([1, 2, 3, 4], 50), 2)
})

test('classifyRun distinguishes comparable categories', () => {
  assert.equal(classifyRun(record()), 'backend-only')
  assert.equal(classifyRun(record({ classifications: { docsOnly: 1, backend: 0, client: 0, browser: 0, postgresql: 0, unavailable: 0 } })), 'docs-only')
  assert.equal(classifyRun(record({ classifications: { docsOnly: 0, backend: 0, client: 1, browser: 0, postgresql: 0, unavailable: 0 } })), 'client-only')
  assert.equal(classifyRun(record({ classifications: { docsOnly: 0, backend: 0, client: 0, browser: 1, postgresql: 0, unavailable: 0 } })), 'browser-only')
  assert.equal(classifyRun(record({ classifications: { docsOnly: 0, backend: 0, client: 0, browser: 0, postgresql: 1, unavailable: 0 } })), 'postgresql-only')
  assert.equal(classifyRun(record({ classifications: { docsOnly: 0, backend: 1, client: 1, browser: 1, postgresql: 1, unavailable: 0 } })), 'mixed')
  assert.equal(classifyRun(record({ classifications: { docsOnly: 0, backend: 0, client: 0, browser: 0, postgresql: 0, unavailable: 1 } })), 'unclassified')
  assert.equal(classifyRun(record({ classifications: { docsOnly: 0, backend: 1, client: 1, browser: 1, postgresql: 1, unavailable: 1 } })), 'mixed')
  assert.equal(classifyRun(record({ run: { ...record().run, event: 'push' } })), 'push-main')
  assert.equal(classifyRun(record({ run: { ...record().run, event: 'schedule' } })), 'scheduled')
  assert.equal(classifyRun(record({ run: { ...record().run, event: 'workflow_dispatch' } })), 'manual')
})

test('runDurationMs and jobGroupDurations respect unavailable data', () => {
  assert.equal(runDurationMs(record()), 31000)
  assert.equal(runDurationMs(record({ criticalPath: { job: null, durationMs: null, unavailableReason: 'missing' } })), null)
  const groups = jobGroupDurations(record())
  assert.equal(groups.get('backend-api')[0], 30000)
})

test('queueAndCancellation computes queue delay and cancelled consumption from API metadata', () => {
  const apiRun = {
    created_at: '2026-08-14T00:00:00Z',
    run_started_at: '2026-08-14T00:00:05Z',
    updated_at: '2026-08-14T00:20:00Z',
    conclusion: 'cancelled',
  }
  const apiJobs = [
    { started_at: '2026-08-14T00:00:06Z', completed_at: '2026-08-14T00:10:00Z', conclusion: 'cancelled' },
    { started_at: '2026-08-14T00:00:07Z', completed_at: '2026-08-14T00:12:00Z', conclusion: 'failure' },
    { started_at: '2026-08-14T00:00:08Z', completed_at: '2026-08-14T00:15:00Z', conclusion: 'success' },
  ]
  const result = queueAndCancellation(apiRun, apiJobs)
  assert.equal(result.queueDelayMs, 5000)
  assert.equal(result.cancelledJobs, 1)
  assert.equal(result.cancelledConsumedMs, 594_000)
  assert.equal(result.runConsumedMs, 1_200_000)
  assert.equal(result.conclusion, 'cancelled')
})

test('flakeTrend ranks repeated titles and cacheTrend totals hits', () => {
  const records = [
    record({ run: { ...record().run, id: 1 }, flakyTests: ['alpha'], cache: { nuget: { hit: 2, miss: 1 }, npm: { hit: 0, miss: 0 }, chromium: { hit: 1, miss: 0 } } }),
    record({ run: { ...record().run, id: 2 }, flakyTests: ['alpha', 'beta'], cache: { nuget: { hit: 0, miss: 1 }, npm: { hit: 0, miss: 0 }, chromium: { hit: 0, miss: 1 } } }),
  ]
  const flakes = flakeTrend(records)
  assert.equal(flakes.totalFlakyRuns, 2)
  assert.deepEqual(flakes.titles, [{ title: 'alpha', runs: 2 }, { title: 'beta', runs: 1 }])
  const cache = cacheTrend(records)
  assert.equal(cache.nuget.hit, 2)
  assert.equal(cache.nuget.miss, 2)
  assert.equal(cache.chromium.hit, 1)
  assert.equal(cache.chromium.miss, 1)
})

test('rollingStats groups like-for-like runs with median/p95', () => {
  const records = []
  for (let i = 0; i < 5; i += 1) {
    records.push(record({ run: { ...record().run, id: i + 1 }, criticalPath: { job: 'gate', durationMs: 30_000 + i * 10_000, unavailableReason: null } }))
  }
  records.push(record({ run: { ...record().run, id: 99, event: 'push' }, criticalPath: { job: 'gate', durationMs: 100_000, unavailableReason: null } }))
  const stats = rollingStats(records)
  const backendOnly = stats.find((group) => group.category === 'backend-only')
  assert.equal(backendOnly.runs, 5)
  assert.equal(backendOnly.criticalPath.median, 50_000)
  assert.equal(backendOnly.criticalPath.p95, 70_000)
  const pushMain = stats.find((group) => group.category === 'push-main')
  assert.equal(pushMain.runs, 1)
})

test('detectRegressions requires sustained evidence and never fires on noise', () => {
  assert.deepEqual(detectRegressions([record()], {}), [])
  const fast = Array.from({ length: 6 }, (_, i) => record({ criticalPath: { job: 'gate', durationMs: 30_000, unavailableReason: null }, run: { ...record().run, id: i + 1 } }))
  const slow = Array.from({ length: 6 }, (_, i) => record({ criticalPath: { job: 'gate', durationMs: 60_000 + i, unavailableReason: null }, run: { ...record().run, id: i + 20 } }))
  const regressions = detectRegressions([...fast, ...slow], { window: 6, minRuns: 3, ratio: 1.15, minDeltaMs: 10_000 })
  assert.ok(regressions.some((entry) => entry.metric === 'criticalPathMedian'))
  assert.deepEqual(detectRegressions([...fast, ...fast], { window: 6, minRuns: 3, ratio: 1.15, minDeltaMs: 10_000 }), [])
  const outlier = record({ criticalPath: { job: 'gate', durationMs: 240_000, unavailableReason: null }, run: { ...record().run, id: 999 } })
  const p95regressions = detectRegressions([...fast, ...fast, outlier], { window: 6, minRuns: 3, ratio: 1.15, minDeltaMs: 60_000 })
  assert.ok(p95regressions.some((entry) => entry.metric === 'criticalPathP95' && entry.previous === 30_000))
})

test('validateRunRecord rejects untrusted or credential-shaped records', () => {
  assert.deepEqual(validateRunRecord(record()), [])
  assert.ok(validateRunRecord({ ...record(), schemaVersion: 'old' }).some((error) => /Unsupported/.test(error)))
  assert.ok(validateRunRecord({ ...record(), run: { ...record().run, sha: 'short' } }).some((error) => /run.sha/.test(error)))
  const noSourceAttempt = record()
  noSourceAttempt.jobs = noSourceAttempt.jobs.map((job) => ({ ...job, sourceAttempt: undefined }))
  assert.ok(validateRunRecord(noSourceAttempt).some((error) => /sourceAttempt/.test(error)))
  const withSecret = record()
  withSecret.jobs[0].instance = 'Password=hunter2'
  assert.ok(validateRunRecord(withSecret).some((error) => /credential-value/.test(error)))
})

test('v1-legacy records are accepted without fabricated source attempts', () => {
  const legacy = record()
  legacy.schemaVersion = 'aerolink-ci-run/v1'
  legacy.jobs = legacy.jobs.map((job) => ({ group: job.group, instance: job.instance, timings: job.timings }))
  assert.deepEqual(validateRunRecord(legacy), [])
  assert.equal(recordFormat(legacy), 'v1-legacy')
  assert.equal(recordFormat(record()), 'v2')
  assert.equal(recordFormat({}), 'unknown')
})

test('malformed legacy jobs are rejected, not passed to aggregation', () => {
  const legacy = record()
  legacy.schemaVersion = 'aerolink-ci-run/v1'
  legacy.jobs = [null, { group: 'x', instance: 42, timings: 'bad' }]
  const errors = validateRunRecord(legacy)
  assert.ok(errors.some((error) => /not an object/.test(error)))
  assert.ok(errors.some((error) => /invalid instance/.test(error)))
  assert.ok(errors.some((error) => /no timings object/.test(error)))
  assert.equal(jobGroupDurations({ jobs: [null, { group: 'a', instance: 'a', timings: { jobStartMs: 0, jobEndMs: 1 } }] }).get('a')[0], 1)
})

test('buildRollingReport produces bounded JSON and Markdown', () => {
  const withTiming = record()
  withTiming.apiTiming = { queueDelayMs: 5000, cancelledConsumedMs: 120_000, cancelledJobs: 2, runConsumedMs: 900_000, conclusion: 'success' }
  const report = buildRollingReport({ records: [withTiming, record({ run: { ...record().run, id: 2 }, flakyTests: ['alpha'] })], regressions: [] })
  assert.equal(report.schemaVersion, 'aerolink-ci-rolling/v1')
  assert.equal(report.records.length, 2)
  assert.equal(report.records[0].apiTiming.queueDelayMs, 5000)
  assert.equal(report.queueAndCancellation.queueDelayMedianMs, 5000)
  assert.equal(report.queueAndCancellation.cancelledJobs, 2)
  assert.equal(report.queueAndCancellation.cancelledConsumedMs, 120_000)
  assert.match(report.markdown, /Comparable groups/)
  assert.match(report.markdown, /Queue and cancellation/)
  assert.match(report.markdown, /Queue delay median: 5s/)
  assert.ok(Buffer.byteLength(JSON.stringify(report), 'utf8') <= 512 * 1024)
})

test('trackerBody is single-issue and never fabricates regressions', () => {
  const clean = trackerBody({ generatedAt: '2026-08-14T00:00:00Z', regressions: [] })
  assert.match(clean, /No sustained regressions/)
  const hot = trackerBody({
    generatedAt: '2026-08-14T00:00:00Z',
    regressions: [{ metric: 'criticalPathMedian', current: 900_000, previous: 700_000, threshold: 805_000, runs: 8 }],
  })
  assert.match(hot, /criticalPathMedian/)
  assert.match(hot, /current 900s vs previous 700s/)
})

test('fullGatesPerMerge counts the PR gate plus successful post-merge push gates', () => {
  const mergedPrs = [
    { number: 571, merge_commit_sha: 'a'.repeat(40), merged_at: '2026-08-14T07:10:42Z' },
    { number: 572, merge_commit_sha: '2e5acb25ff4bbaab811773cecbafabc00fd2c7af', merged_at: '2026-08-14T13:40:00Z' },
  ]
  const runs = [
    { head_sha: '2e5acb25ff4bbaab811773cecbafabc00fd2c7af', conclusion: 'success' },
    { head_sha: '2e5acb25ff4bbaab811773cecbafabc00fd2c7af', conclusion: 'failure' },
    { head_sha: '2e5acb25ff4bbaab811773cecbafabc00fd2c7af', conclusion: 'success' },
    { head_sha: 'x'.repeat(40), conclusion: 'success' },
  ]
  const result = fullGatesPerMerge(mergedPrs, runs)
  const pr572 = result.find((entry) => entry.pr === 572)
  assert.equal(pr572.gates, 3)
  const pr571 = result.find((entry) => entry.pr === 571)
  assert.equal(pr571.gates, 1)
})
