import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  median, percentile, classifyRun, runDurationMs, jobGroupDurations, queueAndCancellation,
  flakeTrend, cacheTrend, rollingStats, detectRegressions, validateRunRecord, recordFormat, buildRollingReport, trackerBody, decideTrackerAction,
  fullGatesPerMerge, FULL_GATE_WINDOW_DAYS, MAX_RECORDS,
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
  legacy.jobs = [
    null,
    { group: 'x', instance: 42, timings: 'bad' },
    { group: 'x', instance: 'no-timings' },
    { group: 'x', instance: 'non-numeric', timings: { jobStartMs: 'a', jobEndMs: 5 } },
    { group: 'x', instance: 'reversed', timings: { jobStartMs: 10, jobEndMs: 5 } },
  ]
  const errors = validateRunRecord(legacy)
  assert.ok(errors.some((error) => /not an object/.test(error)))
  assert.ok(errors.some((error) => /invalid instance/.test(error)))
  assert.ok(errors.some((error) => /no timings object/.test(error)))
  assert.ok(errors.some((error) => /non-integer or negative timing endpoints/.test(error)))
  assert.ok(errors.some((error) => /reversed timing endpoints/.test(error)))
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

test('the Markdown regression section retains the comparable category', () => {
  const report = buildRollingReport({
    records: [record()],
    regressions: [{ metric: 'criticalPathMedian', category: 'mixed', current: 900_000, previous: 700_000, threshold: 805_000, runs: 8 }],
  })
  assert.match(report.markdown, /mixed: criticalPathMedian/)
})

test('the full-gate headline sums the current run/attempt fields and never emits NaN', () => {
  const report = buildRollingReport({
    records: [record()],
    fullGates: [
      { pr: 572, mergedAt: '2026-08-14T13:43:31Z', runs: 10, attempts: 13, prRuns: 9, postMergeRuns: 1 },
      { pr: 571, mergedAt: '2026-08-14T07:10:42Z', runs: 10, attempts: 10, prRuns: 9, postMergeRuns: 1 },
    ],
  })
  assert.doesNotMatch(report.markdown, /NaN/)
  // Scope is stated in the line's own terms. Labelling an all-history figure "(window)" alongside window
  // statistics is what made 703 runs across 200 merges read as a window total and produced a false defect
  // report; the distribution leads because the totals are not the actionable part.
  assert.match(report.markdown, /Full gates per merged PR \(2 merge\(s\) from the last 30 days, newest 200 kept\)/)
  assert.match(report.markdown, /median 10, p95 10, max 10 \(20 runs \/ 23 attempts in total\)/)
  assert.match(report.markdown, /PR #572 \(merged 2026-08-14\): 10 full gate run\(s\) \/ 13 attempt\(s\) \(9 pre-merge, 1 post-merge\)/)
})

test('the full-gate headline reports a distribution, not just a mean-shaped total', () => {
  // A long tail is the finding: on real data the median is 2 and the maximum 34, and a bare total hides that
  // entirely. One merge costing 34 full gates is the rebase treadmill made countable.
  const report = buildRollingReport({
    records: [record()],
    fullGates: [
      { pr: 1, mergedAt: '2026-08-14T00:00:00Z', runs: 2, attempts: 2, prRuns: 1, postMergeRuns: 1 },
      { pr: 2, mergedAt: '2026-08-14T00:00:00Z', runs: 2, attempts: 2, prRuns: 1, postMergeRuns: 1 },
      { pr: 3, mergedAt: '2026-08-14T00:00:00Z', runs: 34, attempts: 34, prRuns: 33, postMergeRuns: 1 },
    ],
  })
  assert.match(report.markdown, /median 2, p95 34, max 34 \(38 runs \/ 38 attempts in total\)/)
  assert.doesNotMatch(report.markdown, /\(window\): 38/)
})

test('the full-gate quantiles are this module\'s, not a second set that disagrees', () => {
  // An ad-hoc floor-based pick returned the upper middle of an even sample and a different p95 rank than the
  // exported helpers. Two statistics in one file answering the same question differently is a defect waiting
  // to be quoted, so these lock the line to `median` and `percentile`.
  const gate = (pr, runs) => ({ pr, mergedAt: '2026-08-14T00:00:00Z', runs, attempts: runs, prRuns: runs - 1, postMergeRuns: 1 })

  // Even sample: median averages the middle pair. `[1, 10]` is 5.5, not 10.
  const even = buildRollingReport({ records: [record()], fullGates: [gate(1, 1), gate(2, 10)] })
  assert.match(even.markdown, /median 5\.5,/)
  assert.equal(median([1, 10]), 5.5)

  // Twenty samples, values 1..20: nearest-rank p95 is ceil(0.95 * 20) = rank 19, so the 19th value. The
  // discarded floor-based pick selected the 20th. Writing this assertion is how I confirmed the review was
  // right about the rank — my first attempt asserted 20, which is the wrong answer the old code gave.
  const twenty = Array.from({ length: 20 }, (_, index) => gate(index + 1, index + 1))
  const report = buildRollingReport({ records: [record()], fullGates: twenty })
  const expected = percentile(twenty.map((entry) => entry.runs), 95)
  assert.equal(expected, 19)
  assert.match(report.markdown, new RegExp(`p95 ${expected}, max 20`))
})

test('the full-gate scope reports the bounds the collector actually applied', () => {
  // Two wrong labels preceded this one: "(window)", which described the run window this figure does not
  // belong to, and "all merges seen", which described no bound at all while the collector filters to the
  // last 30 days and keeps the newest 200. The scope now travels with the data.
  const gates = [{ pr: 1, mergedAt: '2026-08-14T00:00:00Z', runs: 2, attempts: 2, prRuns: 1, postMergeRuns: 1 }]
  const supplied = buildRollingReport({
    records: [record()],
    fullGates: gates,
    fullGateScope: { windowDays: 7, cap: 50 },
  })
  assert.match(supplied.markdown, /1 merge\(s\) from the last 7 days, newest 50 kept/)
  assert.doesNotMatch(supplied.markdown, /all .* merges seen/)
  assert.doesNotMatch(supplied.markdown, /per merged PR \(window\)/)

  // Absent metadata falls back to the module's own constants rather than inventing a scope.
  const fallback = buildRollingReport({ records: [record()], fullGates: gates })
  assert.match(fallback.markdown, new RegExp(`from the last ${FULL_GATE_WINDOW_DAYS} days, newest ${MAX_RECORDS} kept`))
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

test('the tracker corrects a cleared regression instead of leaving a stale claim', () => {
  // The defect this covers: trackerBody has always rendered the clean case (asserted directly above),
  // but the caller returned early on zero regressions and never looked for an existing tracker — so the
  // clean body was unreachable and #587 asserted a regression for hours after it cleared. The library
  // was tested; the decision that reaches it was not.
  const cleared = decideTrackerAction({ regressions: [], trackerExists: true })
  assert.equal(cleared.action, 'update')
  assert.match(cleared.reason, /stale claim|clear/i)

  // The protection that must survive: nothing to report and nothing to correct touches nothing.
  // Creating an issue to announce there is no issue is the spam the early return was guarding against.
  const quiet = decideTrackerAction({ regressions: [], trackerExists: false })
  assert.equal(quiet.action, 'none')

  // And the detection path is unchanged in both directions.
  const regressions = [{ metric: 'criticalPathP95', current: 761_000, previous: 661_000, threshold: 761_000, runs: 8 }]
  assert.equal(decideTrackerAction({ regressions, trackerExists: true }).action, 'update')
  assert.equal(decideTrackerAction({ regressions, trackerExists: false }).action, 'create')

  // Defaults must not invent work: an empty call is the quiet case, not a create.
  assert.equal(decideTrackerAction().action, 'none')
  assert.equal(decideTrackerAction({ regressions: null, trackerExists: true }).action, 'update')
})

test('fullGatesPerMerge attributes every quality-gate run and attempt to its merged PR', () => {
  const mergedPrs = [
    {
      number: 572,
      head: { ref: 'deepseek/567-ci-metrics-instrumentation' },
      created_at: '2026-08-14T07:00:00Z',
      merged_at: '2026-08-14T13:40:00Z',
      merge_commit_sha: '2e5acb25ff4bbaab811773cecbafabc00fd2c7af',
    },
  ]
  const runs = [
    { event: 'pull_request', head_branch: 'deepseek/567-ci-metrics-instrumentation', created_at: '2026-08-14T07:22:35Z', run_attempt: 1 },
    { event: 'pull_request', head_branch: 'deepseek/567-ci-metrics-instrumentation', created_at: '2026-08-14T07:36:51Z', run_attempt: 1 },
    { event: 'pull_request', head_branch: 'deepseek/567-ci-metrics-instrumentation', created_at: '2026-08-14T07:52:08Z', run_attempt: 1 },
    { event: 'pull_request', head_branch: 'deepseek/567-ci-metrics-instrumentation', created_at: '2026-08-14T09:38:14Z', run_attempt: 1 },
    { event: 'pull_request', head_branch: 'deepseek/567-ci-metrics-instrumentation', created_at: '2026-08-14T10:04:19Z', run_attempt: 1 },
    { event: 'pull_request', head_branch: 'deepseek/567-ci-metrics-instrumentation', created_at: '2026-08-14T11:40:09Z', run_attempt: 2 },
    { event: 'pull_request', head_branch: 'deepseek/567-ci-metrics-instrumentation', created_at: '2026-08-14T12:44:27Z', run_attempt: 2 },
    { event: 'pull_request', head_branch: 'other-branch', created_at: '2026-08-14T08:00:00Z', run_attempt: 1 },
    { event: 'pull_request', head_branch: 'deepseek/567-ci-metrics-instrumentation', created_at: '2026-08-20T00:00:00Z', run_attempt: 1 },
    { event: 'push', head_sha: '2e5acb25ff4bbaab811773cecbafabc00fd2c7af', created_at: '2026-08-14T13:43:34Z', run_attempt: 1 },
    { event: 'push', head_sha: 'x'.repeat(40), created_at: '2026-08-14T13:50:00Z', run_attempt: 1 },
  ]
  const result = fullGatesPerMerge(mergedPrs, runs)
  const pr572 = result.find((entry) => entry.pr === 572)
  assert.equal(pr572.prRuns, 7)
  assert.equal(pr572.postMergeRuns, 1)
  assert.equal(pr572.runs, 8)
  assert.equal(pr572.attempts, 10)
})
