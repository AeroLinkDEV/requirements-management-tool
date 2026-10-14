import { expect, logicTest as test } from './isolated-client-test'
import { execFileSync, spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { runInNewContext } from 'node:vm'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { startKernelPacing } from '../src/fmsCdu/benchPacing'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { singleComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { ScenarioRunner } from '../src/fmsCdu/scenario'
import { BROWSER_RUN_SCHEMA, browserWindowAccounting, captureEvidence, finiteMissionEvidence, installBrowserRecorder, measureWindow, type WindowProbe } from '../perf/browser-accounting'
import { TimelineHost } from './fixtures/fms-pacing-host'
import { browserCapturePlan, captureDisposition, coldPayload, copyColdPayload, digest, CAPTURE_GUARDIAN } from '../perf/browser-capture'
import { abbaOrder, blockZeroCheck, failedRunRule, headlessSchedule, headlessVerdict, holm, judgeFamily, median, medianLevel, mulberry32, nearestRank, permutationTest, plannedRuns, relativeEffect, scoredRuns, type RunValue } from '../perf/stats'

// The performance harness (product/client/perf, #1510) turns runs into the intervals every D10 budget decision rests
// on. These vectors are derived by hand from the definitions, not from the code: nearest-rank percentiles, the ABBA
// order, the exact bootstrap of a three-run sample, blocking, the within-block permutation test (protocol revision 2)
// and its calibration under a null with block effects, and Holm's step-down adjustment.

// Browser accounting revision 2 (#1557). The timelines below are produced by the real bench pacing driver on a
// deterministic host (fixtures/fms-pacing-host.ts), not written by hand; the expected counts are derived by hand from
// the frame costs. These are functional accounting proofs, not performance measurements.
const pacingEpoch = Date.UTC(2026, 8, 27, 14)
function paced(rate: number, frameMs: number, options: { tailMs?: number, postMs?: number, otherMs?: number } = {}) {
  const host = new TimelineHost(options.tailMs ?? 0, options.postMs ?? 0), composed = singleComposition(pacingEpoch), kernel = new FmsKernel(composed.plant)
  const stop = startKernelPacing(rate, kernel, host, { onFault: error => { throw error }, onInstantClose: () => { host.wall += frameMs } })
  // `otherMs` of other main-thread work (rendering, input) runs between every two timer tasks.
  const run = (tasks: number) => { for (let i = 0; i < tasks; i++) { host.runNext(); host.wall += options.otherMs ?? 0 } return host.wall }
  const window = (begin: number, end: number) => browserWindowAccounting({ rate, begin, end, timeline: host.timeline(), tasks: host.timerTasks })
  return { host, kernel, stop, run, window }
}

test('a window counts the completed frames of batches straddling both boundaries and separates continuation cost', () => {
  // 64x, 8 ms frames: a slice is four frames (32 ms), a batch sixteen slices. Each task does 1 ms of synchronous work
  // after its slice and has a 2 ms microtask tail, and 5 ms of other work follows it: a task cycle is 40 ms, and
  // batches run back to back (each outlasts its 250 ms deadline).
  const { run, window } = paced(64, 8, { postMs: 1, tailMs: 2, otherMs: 5 })
  const begin = run(10) // after 10 slices of batch 1: 40 frames done, 24 outstanding
  const end = run(30)   // 6 more slices of batch 1, all 16 of batch 2, 8 of batch 3
  run(1)                // the entry that closes the window
  expect([begin, end]).toEqual([250 + 10 * 40, 250 + 40 * 40])
  const accounting = window(begin, end)
  expect(accounting.frames).toEqual({ admitted: 128, attempted: 120, completed: 120, discarded: 0, unfinishedAttempts: 0,
    carriedIn: { debt: 24, batch: 1, attemptedInWindow: 24, completedInWindow: 24 }, carriedOut: { debt: 32, batch: 3 } })
  expect(accounting.batchesAdmitted).toBe(2)
  expect([accounting.simSeconds, accounting.wallSeconds, accounting.achievedRate, accounting.achievedFraction]).toEqual([30, 1.2, 25, 25 / 64])
  expect(accounting.window).toEqual({ begin: 650, end: 1850, epoch: accounting.window.epoch })
  // The warm-up: the epoch's 10 slices before the window, 40 frames over the 0.4 s from its first admission.
  expect(accounting.beforeWindow).toEqual({ completed: 40, simSeconds: 10, slices: 10, wallSeconds: 0.4 })
  // Simulation: 30 slices of 32 ms. Continuation: 27 yield gaps of 8 ms (post + tail + other work) between slices of
  // one batch inside the window (not across an admission, nor from a slice before the window); 30 task tails of
  // 3 ms after the slice (post + microtask tail), of tasks lasting 35 ms.
  expect(accounting.simulation).toEqual({ slices: { frames: 30, p50: 32, p95: 32, p99: 32, max: 32, totalMs: 960 }, admissionMs: 64, continuationMs: 896 })
  expect(accounting.continuation).toEqual({ slices: 28, yieldGaps: { frames: 27, p50: 8, p95: 8, p99: 8, max: 8, totalMs: 216 },
    taskTails: { frames: 30, p50: 3, p95: 3, p99: 3, max: 3, totalMs: 90 }, pacingTasks: { frames: 30, p50: 35, p95: 35, p99: 35, max: 35, totalMs: 1050 } })
})

test('a busy window that admits nothing counts the continuation frames it executed, not intervals x rate', () => {
  // 64x, 100 ms frames: each task completes one indivisible frame, so a batch takes 6.4 s. Over the 1 s window the
  // interval inference of revision 1 would have claimed four intervals x 64 / 4 = 64 s; ten frames, 2.5 s, ran.
  const { run, window } = paced(64, 100)
  const begin = run(3), end = run(10)
  run(1)
  const accounting = window(begin, end)
  expect(accounting.frames).toEqual({ admitted: 0, attempted: 10, completed: 10, discarded: 0, unfinishedAttempts: 0,
    carriedIn: { debt: 61, batch: 1, attemptedInWindow: 10, completedInWindow: 10 }, carriedOut: { debt: 51, batch: 1 } })
  expect(accounting.batchesAdmitted).toBe(0)
  expect([accounting.simSeconds, accounting.wallSeconds, accounting.achievedRate]).toEqual([2.5, 1, 2.5])
})

test('a pause inside the window discards the batch remainder: 8 admitted, 5 completed, 3 discarded', () => {
  const { run, window, stop, host } = paced(8, 40)
  const begin = host.wall
  run(5); stop()
  // The discard ends the epoch, so it closes the window too.
  const accounting = window(begin, host.wall)
  expect(accounting.frames).toEqual({ admitted: 8, attempted: 5, completed: 5, discarded: 3, unfinishedAttempts: 0,
    carriedIn: { debt: 0, batch: null, attemptedInWindow: 0, completedInWindow: 0 }, carriedOut: { debt: 0, batch: null } })
})

test('accounting refuses a timeline it cannot conserve or close, a straddled boundary, a second epoch or unattributed slices', () => {
  const { host, run, kernel } = paced(64, 8)
  const begin = run(10), end = run(30)
  run(1)
  const timeline = host.timeline(), tasks = host.timerTasks
  const refused = (input: Partial<Parameters<typeof browserWindowAccounting>[0]>) =>
    () => browserWindowAccounting({ rate: 64, begin, end, timeline, tasks, ...input })
  expect(refused({})).not.toThrow()
  expect(refused({ timeline: timeline.filter((_, i) => i !== 20) })).toThrow(/FMS_BROWSER_ACCOUNTING_REFUSED: batch 2 reports/)
  expect(refused({ timeline: [...timeline.slice(0, 21), timeline[20], ...timeline.slice(21)] })).toThrow(/FMS_BROWSER_ACCOUNTING_REFUSED/)
  expect(refused({ timeline: timeline.slice(1) })).toThrow(/does not start at the epoch's first admission|no outstanding admitted frames/)
  // The last slice inside the window is lost: the entry after the window exposes it (120 would read as 116).
  expect(refused({ timeline: timeline.filter((_, i) => i !== 39) })).toThrow(/batch 3 reports/)
  // Nothing follows the window end, so a loss at its end would be invisible.
  expect(refused({ timeline: timeline.slice(0, 40) })).toThrow(/no timeline entry of the epoch follows the window end/)
  expect(refused({ begin: timeline[10].start + 1 })).toThrow(/straddles a window boundary/)
  expect(refused({ timeline: [] })).toThrow(/no pacing slice was observed in the window/)
  expect(refused({ tasks: [] })).toThrow(/outside an observed timer task/)
  expect(refused({ rate: 16 })).toThrow(/paced at 64x, not the selected 16x/)
  // A pause and a new Fly (a second pacing epoch) inside the window.
  const second = new TimelineHost()
  second.wall = end
  const stop = startKernelPacing(64, kernel, second, { onFault: error => { throw error } })
  second.runNext(); stop()
  expect(() => browserWindowAccounting({ rate: 64, begin, end: second.wall, timeline: [...timeline, ...second.timeline()], tasks: [...tasks, ...second.timerTasks] }))
    .toThrow(/the window spans 2 pacing epochs/)
})

// The browser owner's run loop (perf/browser.perf.ts) over the recorder; here the recorder is the deterministic host.
function probeOf(host: TimelineHost): WindowProbe {
  let begin = 0, end = 0
  const timeline = () => host.timeline()
  return {
    begin: async () => (begin = host.wall),
    completed: async () => timeline().filter(entry => entry.kind === 'slice' && entry.start >= begin).reduce((total, entry) => total + Number(entry.detail.completed), 0),
    finish: async () => { end = host.wall; return { begin, end, timeline: timeline(), tasks: host.timerTasks, rafIntervals: [], longTasks: [], loaf: [], events: [] } },
    timeline: async () => timeline(),
  }
}

test('the window loop ends at the target, waits for the entry that closes the window, and accounts it', async () => {
  const { host, run } = paced(64, 8)
  run(10)
  let settles = 0
  const { recorded, steps, stalled } = await measureWindow(probeOf(host), 100, async () => { run(10) }, async () => { settles++; run(1) })
  // Ten slices (40 frames) per step: three steps reach 100; the window then ends between tasks and one task closes it.
  expect([steps, stalled, settles]).toEqual([3, false, 1])
  const accounting = browserWindowAccounting({ rate: 64, begin: recorded.begin, end: recorded.end, timeline: recorded.timeline, tasks: recorded.tasks })
  expect(accounting.frames.completed).toBe(120)
})

test('a window that stops gaining frames ends at once and is accounted, so a missing timeline refuses instead of timing out', async () => {
  // An arm without the timeline: nothing is ever recorded.
  let steps = 0, settles = 0
  const absent: WindowProbe = { begin: async () => 0, completed: async () => 0, timeline: async () => [],
    finish: async () => ({ begin: 0, end: 6000, timeline: [], tasks: [], rafIntervals: [], longTasks: [], loaf: [], events: [] }) }
  const empty = await measureWindow(absent, 9600, async () => { steps++ }, async () => { settles++ })
  expect([empty.steps, empty.stalled, steps, settles]).toEqual([1, true, 1, 20])
  expect(() => browserWindowAccounting({ rate: 64, begin: 0, end: 6000, timeline: empty.recorded.timeline, tasks: [] })).toThrow(/no pacing slice was observed/)

  // A pause during the window: the step after it adds nothing; the discard closes the window and is reported.
  const { host, run, stop } = paced(64, 8)
  run(3)
  let step = 0
  const paused = await measureWindow(probeOf(host), 9600, async () => { if (step++ === 0) run(5); else stop() }, async () => { run(1) })
  expect([paused.steps, paused.stalled]).toEqual([2, true])
  const accounting = browserWindowAccounting({ rate: 64, begin: paused.recorded.begin, end: paused.recorded.end, timeline: paused.recorded.timeline, tasks: paused.recorded.tasks })
  expect(accounting.frames).toMatchObject({ completed: 20, discarded: 32, carriedOut: { debt: 0 } })
})

const host = { quietBefore: { quiet: true } }
const reportProtocol = JSON.parse(readFileSync('perf/protocol.json', 'utf8'))
const reportHarness = { commit: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(), apiTree: 'b'.repeat(40), apiAssemblySha256: 'f'.repeat(64), dirtyEntries: 0 }
const browserRecord = (index: number, rate: number, completed: number, wallSeconds: number, extra: Record<string, unknown> = {}) => ({
  index, kind: 'browser', status: 'passed', block: index, arm: 'B', configuration: 'view-off-svs-off', rate, host,
  armIdentity: { commit: 'a'.repeat(40), srcTree: 'b'.repeat(40), lockBlob: 'b'.repeat(40), viteBlob: 'c'.repeat(40), distSha256: 'd'.repeat(64), distFiles: 2 },
  result: { schema: 'aerolink.fms-perf-browser-run.v2', accountingRevision: 2, scenario: reportProtocol.browser.scenario, rate, configuration: 'view-off-svs-off', windowSimSeconds: completed / 4, wallSeconds, throughput: completed / 4 / wallSeconds,
    accounting: { achievedRate: completed / 4 / wallSeconds, achievedFraction: completed / 4 / wallSeconds / rate,
      simulation: { slices: { p50: 20, p95: 30, p99: 31 } }, continuation: { yieldGaps: { p95: 4 }, taskTails: { p95: 3 } } },
    longTasks: { count: 0 }, raf: null, interaction: { entries: [], resolutionStepMs: null }, heapUsedBytesAfterGc: 2 ** 20,
    browser: { version: 'fixture', headless: true, renderer: 'fixture', viewport: { width: 1440, height: 900, deviceScaleFactor: 1 } } },
  ...extra,
})

// Independent synthetic receipts at the real CLI boundary. Values and expected levels are hand-defined, not
// produced by qualification. Session start precedes seeding; the end and each run carry its retained seed receipt.
function browserSession(out: string, id: number, rows: ReturnType<typeof browserRecord>[], protocol = reportProtocol, harness = reportHarness) {
  const startedAt = `2026-10-13T12:${String(id).padStart(2, '0')}:00.000Z`
  const seedFile = `seed-${id}.json`, seedBytes = '{"fixture":"independent seed"}'
  const seed = { file: seedFile, sha256: createHash('sha256').update(seedBytes).digest('hex') }
  writeFileSync(join(out, seedFile), seedBytes)
  const start = { kind: 'browser', startedAt, harness, dirtyOverride: false, protocol,
    environment: { node: '24-fixture', v8: 'fixture', playwright: '1-fixture', os: { platform: 'fixture', release: 'fixture', arch: 'x64' }, cpu: { model: 'fixture', logicalCores: 4 }, memoryGiB: 8, powerPlan: null, environmentVariableNames: [] },
    arms: [{ arm: 'B', ...rows[0].armIdentity }], rounds: 2, cells: [{ configuration: rows[0].configuration, rate: rows[0].rate }] }
  const end = { ...start, endedAt: `2026-10-13T12:${String(id).padStart(2, '0')}:59.000Z`, harnessAtEnd: harness,
    servedAtEnd: { distSha256: rows[0].armIdentity.distSha256, distFiles: rows[0].armIdentity.distFiles }, seed }
  const linked = rows.map(row => ({ ...row, sessionStartedAt: startedAt, startedAt: `2026-10-13T12:${String(id).padStart(2, '0')}:30.000Z`, seed }))
  return { start, end, rows: linked }
}

function writeBrowserSession(out: string, id: number, receipt: { start: object; end: object }) {
  writeFileSync(join(out, `session-${id}.json`), JSON.stringify(receipt.start))
  writeFileSync(join(out, `session-end-${id}.json`), JSON.stringify(receipt.end))
}

test('the report scores completed-frame browser records beside the selected rate and keeps older records NOT_QUALIFIED', ({ browserName: _browserName }, testInfo) => {
  const out = testInfo.outputPath('mixed-records')
  mkdirSync(out, { recursive: true })
  const historical = { schema: 'aerolink.fms-perf-browser-run.v1', windowSimSeconds: 336, throughput: 42, callbacks: { p50: 1, p95: 2, p99: 3 } }
  const rows = [
    // 64x selected: 8,800 and 9,000 completed frames over 40 s achieve 55x and 56.25x.
    browserRecord(0, 64, 8800, 40), browserRecord(1, 64, 9000, 40),
    { ...browserRecord(2, 64, 0, 1), result: historical },
    { ...browserRecord(3, 64, 0, 1), status: 'failed', result: null },
    // Another build of arm B in the same --out (another served dist): its own cell, never pooled. 6,400 frames, 40x.
    browserRecord(5, 64, 6400, 40, { armIdentity: { commit: 'a'.repeat(40), srcTree: 'b'.repeat(40), lockBlob: 'b'.repeat(40), viteBlob: 'c'.repeat(40), distSha256: 'e'.repeat(64), distFiles: 2 } }),
    { index: 4, kind: 'headless', status: 'passed', block: 0, arm: 'B', workload: 'W1', topology: 'single', host,
      result: { p50: 1, p95: 2, p99: 3, throughput: 250, heapUsedBytesAfterGc: 1024, sync: null } },
  ]
  const first = browserSession(out, 0, rows.slice(0, 4) as ReturnType<typeof browserRecord>[])
  const second = browserSession(out, 1, [rows[4]] as ReturnType<typeof browserRecord>[])
  writeBrowserSession(out, 0, first); writeBrowserSession(out, 1, second)
  const input = [...first.rows, ...second.rows, rows[5]].map(row => JSON.stringify(row)).join('\n') + '\n'
  writeFileSync(join(out, 'runs.jsonl'), input)
  const child = spawnSync(process.execPath, ['perf/run-perf.ts', 'report', '--out', out], { cwd: process.cwd(), encoding: 'utf8' })
  expect(child.status, child.stderr).toBe(0)
  const report = JSON.parse(readFileSync(join(out, 'report.json'), 'utf8'))
  expect(report.browserQualification).toMatchObject({ accountingIssue: '#1557', policyRevision: 2, accepted: [0, 1, 5],
    supportedAccountingSchemas: ['aerolink.fms-perf-browser-run.v2'], notQualified: [
      { index: 2, qualification: 'NOT_QUALIFIED', compatibility: 'historical-interval-inference' },
      { index: 3, qualification: 'NOT_QUALIFIED', compatibility: 'failed-run' }] })
  expect(report.browser).toHaveLength(2)
  // Two runs: the median of 55 and 56.25 is 55.625; achieved / selected 55.625 / 64.
  expect(report.browser[0].levels.throughput.estimate).toBeCloseTo(55.625, 12)
  expect(report.browser[0].levels['achieved / selected'].estimate).toBeCloseTo(55.625 / 64, 12)
  expect(report.browser[1]).toMatchObject({ arm: `B@${'a'.repeat(12)}/${'b'.repeat(12)}/${'e'.repeat(12)}`, runs: 1 })
  expect(report.browser[1].levels.throughput.estimate).toBe(40)
  const prose = readFileSync(join(out, 'report.md'), 'utf8')
  expect(prose).toContain('## Browser matrix (accounting revision 2, completed frames')
  expect(prose).toContain(`| B@${'a'.repeat(12)}/${'b'.repeat(12)}/${'d'.repeat(12)} | view-off-svs-off | 64x | 2 | 55.6 [55.0, 56.3]`)
  expect(prose).toContain(`| B@${'a'.repeat(12)}/${'b'.repeat(12)}/${'e'.repeat(12)} | view-off-svs-off | 64x | 1 | 40.0 [40.0, 40.0]`)
  expect(prose).toContain('No browser budget verdict is made under this revision')
  expect(prose).toContain('- historical-interval-inference: 1 (runs 2)')
  expect(prose).toContain('- failed-run: 1 (runs 3)')
  expect(prose).toContain('| W1 | single | 1 | 1.000')
  expect(readFileSync(join(out, 'runs.jsonl'), 'utf8')).toBe(input)
})

test('browser report cells retain actual scenario, protocol, runtime, harness and full build identities', ({ browserName: _browserName }, testInfo) => {
  const out = testInfo.outputPath('browser-cohorts')
  mkdirSync(out, { recursive: true })
  const normal = browserRecord(0, 64, 1600, 40) // 10x; its identical repeat combines, each other cohort stays 50x.
  const otherScenario = 'kbtv-rnav15-advisory'
  const scenario = browserRecord(2, 64, 8000, 40)
  scenario.result.scenario = otherScenario
  const runtime = browserRecord(3, 64, 8000, 40)
  runtime.result.browser = { version: 'other-chromium', headless: false, renderer: 'other-renderer', viewport: { width: 1280, height: 720, deviceScaleFactor: 2 } }
  const lock = browserRecord(6, 64, 8000, 40)
  lock.armIdentity.lockBlob = 'e'.repeat(40)
  // Equal display prefixes do not establish equal immutable hashes.
  const prefix = browserRecord(7, 64, 8000, 40)
  prefix.armIdentity.distSha256 = 'd'.repeat(12) + 'e'.repeat(52)
  const receipts = [
    browserSession(out, 0, [normal, browserRecord(1, 64, 1600, 40)]),
    browserSession(out, 1, [scenario], { ...reportProtocol, browser: { ...reportProtocol.browser, scenario: otherScenario } }),
    browserSession(out, 2, [runtime], { ...reportProtocol, browser: { ...reportProtocol.browser, viewport: runtime.result.browser.viewport } }),
    browserSession(out, 3, [browserRecord(4, 64, 8000, 40)], { ...reportProtocol, statistics: { ...reportProtocol.statistics, seed: 99 } }),
    browserSession(out, 4, [browserRecord(5, 64, 8000, 40)], reportProtocol, { ...reportHarness, apiAssemblySha256: 'e'.repeat(64) }),
    browserSession(out, 5, [lock]), browserSession(out, 6, [prefix]),
  ]
  receipts.forEach((receipt, id) => writeBrowserSession(out, id, receipt))
  writeFileSync(join(out, 'runs.jsonl'), receipts.flatMap(receipt => receipt.rows).map(row => JSON.stringify(row)).join('\n') + '\n')
  const child = spawnSync(process.execPath, ['perf/run-perf.ts', 'report', '--out', out], { cwd: process.cwd(), encoding: 'utf8' })
  expect(child.status, child.stderr).toBe(0)
  const report = JSON.parse(readFileSync(join(out, 'report.json'), 'utf8'))
  expect(report.browserQualification.accepted).toEqual([0, 1, 2, 3, 4, 5, 6, 7])
  expect(report.browser.map((cell: any) => [cell.runs, cell.levels.throughput.estimate])).toEqual([[2, 10], [1, 50], [1, 50], [1, 50], [1, 50], [1, 50], [1, 50]])
  expect(report.browser[1].identity.scenario).toBe(otherScenario)
  expect(report.browser[2].identity.runtime).toMatchObject({ version: 'other-chromium', headless: false })
  expect(report.browser[3].identity.protocol.statistics.seed).toBe(99)
  expect(report.browser[4].identity.harness.apiAssemblySha256).toBe('e'.repeat(64))
  const prose = readFileSync(join(out, 'report.md'), 'utf8')
  expect(prose).toContain(otherScenario)
  expect(prose).toContain('other-chromium, headed, renderer: other-renderer, viewport 1280x720 at DPR 2')
})

test('browser report requires an unambiguous complete clean session with matching source and retained seed', ({ browserName: _browserName }, testInfo) => {
  const out = testInfo.outputPath('browser-session-evidence')
  mkdirSync(out, { recursive: true })
  const variants = ['valid', 'missing-session', 'missing-end', 'changed-dist', 'changed-harness', 'dirty-smoke', 'changed-seed', 'missing-seed-file', 'incomplete-identity', 'duplicate-end', 'wrong-run-arm', 'changed-protocol', 'wrong-session', 'duplicate-session', 'wrong-run-seed', 'incomplete-runtime', 'incomplete-protocol']
  const rows = variants.map((variant, id) => {
    const receipt = browserSession(out, id, [browserRecord(id, 64, 1600, 40)])
    const row = receipt.rows[0]
    if (variant === 'changed-dist') receipt.end.servedAtEnd.distSha256 = 'e'.repeat(64)
    if (variant === 'changed-harness') receipt.end.harnessAtEnd = { ...reportHarness, commit: 'e'.repeat(40) }
    if (variant === 'dirty-smoke') receipt.start.dirtyOverride = true
    if (variant === 'changed-seed') receipt.end.seed = { ...receipt.end.seed, sha256: 'e'.repeat(64) }
    if (variant === 'missing-seed-file') { receipt.end.seed = { ...receipt.end.seed, file: 'absent-seed.json' }; row.seed = receipt.end.seed }
    if (variant === 'incomplete-identity') receipt.start.harness = { ...reportHarness, apiAssemblySha256: '' }
    if (variant === 'wrong-run-arm') row.armIdentity = { ...row.armIdentity, lockBlob: 'e'.repeat(40) }
    if (variant === 'changed-protocol') receipt.end.protocol = { ...reportProtocol, browser: { ...reportProtocol.browser, scenario: 'other-scenario' } }
    if (variant === 'wrong-session') row.sessionStartedAt = 'unknown-session'
    if (variant === 'wrong-run-seed') row.seed = { ...row.seed, sha256: 'e'.repeat(64) }
    if (variant === 'incomplete-runtime') row.result.browser.version = ''
    if (variant === 'incomplete-protocol') {
      receipt.start.protocol = { ...reportProtocol, statistics: { ...reportProtocol.statistics, iterations: undefined } }
      receipt.end.protocol = receipt.start.protocol
    }
    if (variant !== 'missing-session') writeFileSync(join(out, `session-${id}.json`), JSON.stringify(receipt.start))
    if (variant !== 'missing-end') writeFileSync(join(out, `session-end-${id}.json`), JSON.stringify(receipt.end))
    if (variant === 'duplicate-end') writeFileSync(join(out, 'session-end-100.json'), JSON.stringify(receipt.end))
    if (variant === 'duplicate-session') writeFileSync(join(out, 'session-100.json'), JSON.stringify(receipt.start))
    return row
  })
  const input = rows.map(row => JSON.stringify(row)).join('\n') + '\n'
  writeFileSync(join(out, 'runs.jsonl'), input)
  const child = spawnSync(process.execPath, ['perf/run-perf.ts', 'report', '--out', out], { cwd: process.cwd(), encoding: 'utf8' })
  expect(child.status, child.stderr).toBe(0)
  const report = JSON.parse(readFileSync(join(out, 'report.json'), 'utf8'))
  expect(report.browserQualification.accepted).toEqual([0])
  expect(report.browserQualification.notQualified.map((row: any) => row.index)).toEqual(variants.slice(1).map((_, i) => i + 1))
  expect(report.browserQualification.notQualified.every((row: any) => row.compatibility === 'session-evidence' && row.reason)).toBe(true)
  expect(report.browser[0]).toMatchObject({ runs: 1, levels: { throughput: { estimate: 10 } } })
  expect(readFileSync(join(out, 'runs.jsonl'), 'utf8')).toBe(input)
})

test('the headless report titles a set by its arms: A/A when both carry one client identity, otherwise A/B', ({ browserName: _browserName }, testInfo) => {
  const same = { srcTree: 'b'.repeat(40), lockBlob: 'l'.repeat(40), viteBlob: 'v'.repeat(40) }
  const titled = (name: string, b: Record<string, string>) => {
    const out = testInfo.outputPath(name)
    mkdirSync(out, { recursive: true })
    const runs = [0, 1].flatMap(block => ['A', 'B', 'B', 'A'].map((arm, slot) => ({ index: block * 4 + slot, kind: 'headless', status: 'passed', block, slot, arm,
      armIdentity: arm === 'A' ? same : b, workload: 'W1', topology: 'single', host,
      result: { p50: 1 + slot, p95: 2, p99: 3, throughput: 250, heapUsedBytesAfterGc: 1024, sync: null } })))
    writeFileSync(join(out, 'runs.jsonl'), runs.map(row => JSON.stringify(row)).join('\n') + '\n')
    const child = spawnSync(process.execPath, ['perf/run-perf.ts', 'report', '--out', out], { cwd: process.cwd(), encoding: 'utf8' })
    expect(child.status, child.stderr).toBe(0)
    return { prose: readFileSync(join(out, 'report.md'), 'utf8'), json: JSON.parse(readFileSync(join(out, 'report.json'), 'utf8')) }
  }
  const aa = titled('aa', same)
  expect(aa.prose).toContain('A/A result: **not judged')
  expect(aa.json.headless.comparison).toBe('A/A')
  const ab = titled('ab', { ...same, srcTree: 't'.repeat(40) })
  expect(ab.prose).not.toContain('A/A result')
  expect(ab.prose).toContain('A/B result: **not judged')
  expect(ab.json.headless.comparison).toBe('A/B')
})

test('an A/B family reads as differences found or none significant; an A/A keeps its verdict', () => {
  const judged = (verdict: 'failed' | 'passed' | 'refused', reason = '') => ({ verdict, reason, significant: verdict === 'failed' ? 3 : 0 })
  expect(headlessVerdict('A/B', judged('failed'))).toEqual({ kind: 'A/B', title: 'A/B result', verdict: 'differences found' })
  expect(headlessVerdict('A/B', judged('passed'))).toEqual({ kind: 'A/B', title: 'A/B result', verdict: 'no significant difference' })
  expect(headlessVerdict('A/B', judged('refused', 'too few blocks'))).toMatchObject({ verdict: 'refused: too few blocks' })
  expect(headlessVerdict('A/A', judged('failed'))).toEqual({ kind: 'A/A', title: 'A/A result', verdict: 'failed' })
  expect(headlessVerdict('arms unidentified', judged('passed')).title).toBe('Arm comparison (arm identities not recorded, so neither A/A nor A/B)')
})

test('the protocol declares the browser run schema the owner writes', () => {
  expect(JSON.parse(readFileSync('perf/protocol.json', 'utf8')).browser.accounting.schema).toBe(BROWSER_RUN_SCHEMA)
})

test('nearest-rank percentiles take the smallest value with at least p% at or below it', () => {
  // ceil(p/100 * n): p5 of 5 -> rank 1, p30 -> 2, p40 -> 2, p50 -> 3, p100 -> 5.
  const five = [50, 15, 40, 20, 35]
  expect([5, 30, 40, 50, 100].map(p => nearestRank(five, p))).toEqual([15, 20, 20, 35, 50])
  // Twenty values 1..20 in a scrambled order: p50 -> rank 10, p95 -> rank 19, p99 -> rank 20.
  const twenty = [7, 19, 3, 12, 20, 1, 16, 9, 5, 14, 2, 18, 11, 6, 13, 4, 17, 8, 15, 10]
  expect([50, 95, 99].map(p => nearestRank(twenty, p))).toEqual([10, 19, 20])
  expect(() => nearestRank(twenty, 0)).toThrow()
  expect(median([3, 1, 2])).toBe(2)
  expect(median([4, 1, 3, 2])).toBe(2.5)
})

test('runs are ordered in ABBA blocks', () => {
  expect(abbaOrder(2).join('')).toBe('ABBAABBA')
})

test('a priming pass, arm A then arm B, runs every workload x topology once before block 0 (protocol revision 3)', () => {
  // The cold first run of a session is always arm A, the base of an A/B; without priming it biases B/A - 1 toward B
  // faster (#1536). One slot per arm (slots -2 and -1, so no slot number is shared with a scored slot), topology order
  // alternating as in the scored blocks, no block.
  const show = (slots: ReturnType<typeof headlessSchedule>) =>
    slots.map(s => `${s.primed ? 'P' : s.block}:${s.slot}:${s.arm}:${s.runs.map(r => `${r.position}${r.workload}${r.topology[0]}`).join(',')}`)
  expect(show(headlessSchedule(2, ['W1', 'W3'], ['single', 'dual']))).toEqual([
    'P:-2:A:0W1s,1W1d,2W3s,3W3d', 'P:-1:B:0W1d,1W1s,2W3d,3W3s',
    '0:0:A:0W1s,1W1d,2W3s,3W3d', '0:1:B:0W1d,1W1s,2W3d,3W3s', '0:2:B:0W1s,1W1d,2W3s,3W3d', '0:3:A:0W1d,1W1s,2W3d,3W3s',
    '1:4:A:0W1s,1W1d,2W3s,3W3d', '1:5:B:0W1d,1W1s,2W3d,3W3s', '1:6:B:0W1s,1W1d,2W3s,3W3d', '1:7:A:0W1d,1W1s,2W3d,3W3s',
  ])
  expect(headlessSchedule(1, ['W1'], ['single', 'dual']).filter(s => s.primed).every(s => s.block === null)).toBe(true)
})

// Two ABBA blocks of one cell after a priming pass. The priming runs of both arms are recorded; the arm A one passed,
// the arm B one failed. In block 1 an arm A run failed.
const cell = { kind: 'headless', workload: 'W1', topology: 'single' }
const recorded = [
  { ...cell, primed: true, block: null, arm: 'A', status: 'passed', id: 'pA' },
  { ...cell, primed: true, block: null, arm: 'B', status: 'failed', id: 'pB' },
  ...(['A', 'B', 'B', 'A'] as const).map((arm, i) => ({ ...cell, primed: false, block: 0, arm, status: 'passed', id: `0${i}` })),
  ...(['A', 'B', 'B', 'A'] as const).map((arm, i) => ({ ...cell, primed: false, block: 1, arm, status: i === 3 ? 'failed' : 'passed', id: `1${i}` })),
]
const ofCell = (entry: { workload?: string; topology?: string }) => entry.workload === 'W1' && entry.topology === 'single'

test('priming runs are never analysed: they are not scored runs and never rows of a cell', () => {
  expect(scoredRuns(recorded).map(e => e.id)).toEqual(['00', '01', '02', '03', '10', '11', '12', '13'])
  // Only block 0 is analysed: block 1 lost its failed run, and the passed arm A priming run is not a block's run (also
  // when no failed priming run is beside it).
  expect(failedRunRule(recorded, ofCell).rows.map(e => e.id)).toEqual(['00', '01', '02', '03'])
  expect(failedRunRule(recorded.filter(e => e.id !== 'pB'), ofCell).rows.map(e => e.id)).toEqual(['00', '01', '02', '03'])
})

test('a failed priming run does not trigger the failed-run rule', () => {
  // The failed arm B priming run excludes nothing; block 1 is excluded for its own failed scored run only.
  expect(failedRunRule(recorded, ofCell).excludedBlocks).toEqual([1])
  expect(failedRunRule(recorded.slice(0, 6), ofCell)).toMatchObject({ excludedBlocks: [], rows: recorded.slice(2, 6) })
})

test('the block-0 check sets block 0\'s B/A - 1 against the RMS of the later blocks\' (descriptive)', () => {
  // Block 0: A 10, 10 and B 8, 8, so d0 = -0.2. Block 1: B 11, 11, so +0.1. Block 2: B 7, 7, so -0.3. RMS of the later
  // blocks: sqrt((0.01 + 0.09) / 2) = sqrt(0.05), about 0.2236; their mean |d| would be 0.2 and their mean d -0.1.
  // Block 3 has arm A only and gives no d.
  const block = (n: number, b: number): RunValue[] => [{ block: n, arm: 'A', value: 10 }, { block: n, arm: 'B', value: b }, { block: n, arm: 'B', value: b }, { block: n, arm: 'A', value: 10 }]
  const check = blockZeroCheck([...block(0, 8), ...block(1, 11), ...block(2, 7), { block: 3, arm: 'A', value: 10 }])
  expect(check.d0).toBeCloseTo(-0.2, 12)
  expect(check.rmsLater).toBeCloseTo(Math.sqrt(0.05), 12)
  expect(check.laterBlocks).toBe(2)
  expect(blockZeroCheck(block(1, 11))).toMatchObject({ d0: null, laterBlocks: 1 })
  expect(blockZeroCheck(block(0, 8))).toMatchObject({ rmsLater: null, laterBlocks: 0 })
})

test('the exact bootstrap of a three-run sample matches its hand enumeration', () => {
  // Arm A runs 1, 2, 4; arm B runs 3, 3, 3; one block. A group of k = 3 runs gives k - 1 = 2 draws, so each arm has
  // 3^2 = 9 equally likely ordered resamples, 81 together. The median of two draws is their mean:
  //   (1,1) 1; (1,2) (2,1) 1.5; (2,2) 2; (1,4) (4,1) 2.5; (2,4) (4,2) 3; (4,4) 4.
  // B's median is always 3, so B/A - 1 = 3/m - 1 takes, over 81: -0.25 (9), 0 (18), 0.2 (18), 0.5 (9), 1 (18), 2 (9).
  // Nearest rank: 2.5% of 81 is rank 3 (-0.25); 97.5% is rank 79, past 72, so 2. At or below zero: 27; at or above:
  // 72. The observed medians are 2 and 3: estimate 0.5; half-width (2 + 0.25) / 2 = 112.5 pp.
  const runs: RunValue[] = [
    ...[1, 2, 4].map(value => ({ block: 0, arm: 'A' as const, value })),
    ...[3, 3, 3].map(value => ({ block: 0, arm: 'B' as const, value })),
  ]
  const effect = relativeEffect(runs, { iterations: 1000, seed: 1 })
  expect(effect).toMatchObject({ exact: true, replicates: 81, estimate: 0.5, low: -0.25, high: 2 })
  expect(effect.halfWidthPp).toBeCloseTo(112.5, 9)
  // One arm alone: the 9 resamples of {1, 2, 4} have medians 1, 1.5, 1.5, 2, 2.5, 2.5, 3, 3, 4; 2.5% is rank 1 (1) and
  // 97.5% is rank 9 (4). Half-width (4 - 1) / 2 = 1.5, which is 75% of the median 2.
  expect(medianLevel([1, 2, 4], { iterations: 1000, seed: 1 }))
    .toMatchObject({ exact: true, replicates: 9, estimate: 2, low: 1, high: 4, halfWidthPct: 75 })
  // Two runs (one per browser round): 2^1 = 2 resamples, medians 10 and 30, around the median 20: half-width 50%.
  expect(medianLevel([10, 30], { iterations: 1000, seed: 1 })).toMatchObject({ replicates: 2, estimate: 20, low: 10, high: 30, halfWidthPct: 50 })
})

test('a two-run group contributes one draw (k - 1) to each bootstrap resample', () => {
  // Block 0: A 10, 12; B 11, 13. With k - 1 = 1 draw per group there are 2 x 2 = 4 resamples, B/A - 1 =
  // 11/10 - 1 = 0.1, 13/10 - 1 = 0.3, 11/12 - 1 = -1/12, 13/12 - 1 = 1/12. Nearest rank over 4: 2.5% is rank 1, 97.5% rank 4.
  // Drawing k = 2 per group instead would give 16 resamples, adding the pair means 11 and 12.
  const runs: RunValue[] = [
    { block: 0, arm: 'A', value: 10 }, { block: 0, arm: 'B', value: 11 }, { block: 0, arm: 'B', value: 13 }, { block: 0, arm: 'A', value: 12 },
  ]
  const effect = relativeEffect(runs, { iterations: 1000, seed: 1 })
  expect(effect).toMatchObject({ exact: true, replicates: 4 })
  expect(effect.low).toBeCloseTo(-1 / 12, 12)
  expect(effect.high).toBeCloseTo(0.3, 12)
})

test('the bootstrap resamples runs within their ABBA block: a drift between blocks adds no width here', () => {
  // Block 0 ran on a quiet host (A 1, 1; B 2, 2), block 1 on a slower one (A 3, 3; B 6, 6). Within each block every
  // draw is that block's value, so every replicate is median(2, 6) / median(1, 3) - 1 = 4 / 2 - 1 = 1.
  // Pooling the blocks before resampling would let a replicate draw A = {1, 1} against B = {6, 6}.
  const runs: RunValue[] = [
    { block: 0, arm: 'A', value: 1 }, { block: 0, arm: 'B', value: 2 }, { block: 0, arm: 'B', value: 2 }, { block: 0, arm: 'A', value: 1 },
    { block: 1, arm: 'A', value: 3 }, { block: 1, arm: 'B', value: 6 }, { block: 1, arm: 'B', value: 6 }, { block: 1, arm: 'A', value: 3 },
  ]
  // 4 groups of 2 runs, one draw each: 2^4 = 16 distinct resamples. 10 iterations draw them; 1000 enumerate them.
  for (const [iterations, exact] of [[10, false], [1000, true]] as const) {
    const effect = relativeEffect(runs, { iterations, seed: 20261006 })
    expect(effect).toMatchObject({ exact, estimate: 1, low: 1, high: 1, halfWidthPp: 0 })
    expect(effect.replicates).toBe(exact ? 16 : 10)
  }
})

test('the drawn bootstrap agrees with the exact enumeration it samples', () => {
  // Six blocks; each arm's two runs in a block differ, so every one of the 2^12 = 4096 resamples (one draw from each
  // of 12 two-run groups) is equally likely and the exact interval is known. 4000 drawn resamples (fewer than 4096,
  // so the drawn path) must land on the same interval within Monte Carlo error. A draw that always took the first
  // value collapses the interval to a point; k draws per group in the drawn path narrows it by about a third.
  const runs: RunValue[] = []
  for (let block = 0; block < 6; block += 1) {
    runs.push({ block, arm: 'A', value: 100 + 3 * block }, { block, arm: 'A', value: 108 + 3 * block })
    runs.push({ block, arm: 'B', value: 101 + 3 * block }, { block, arm: 'B', value: 113 + 3 * block })
  }
  const exact = relativeEffect(runs, { iterations: 5000, seed: 7 })
  const drawn = relativeEffect(runs, { iterations: 4000, seed: 7 })
  expect([exact.exact, exact.replicates, drawn.exact, drawn.replicates]).toEqual([true, 4096, false, 4000])
  expect(drawn.estimate).toBe(exact.estimate)
  expect(Math.abs(drawn.low - exact.low)).toBeLessThan(0.15 * (exact.high - exact.low))
  expect(Math.abs(drawn.high - exact.high)).toBeLessThan(0.15 * (exact.high - exact.low))
  expect(drawn.halfWidthPp / exact.halfWidthPp).toBeGreaterThan(0.85)
  expect(drawn.halfWidthPp / exact.halfWidthPp).toBeLessThan(1.15)
})

test('the within-block permutation test matches its hand enumeration and states its smallest attainable p', () => {
  // One block, A 1, 2 and B 3, 4. The 6 ways to label two of the four runs A, with |ln(median B / median A)|:
  // {1,2} ln(3.5/1.5); {3,4} the same, negated; {1,3} and {2,4} ln 1.5; {1,4} and {2,3} 0. Two of six reach the
  // observed {1,2}: p = 1/3.
  const one: RunValue[] = [{ block: 0, arm: 'A', value: 1 }, { block: 0, arm: 'B', value: 3 }, { block: 0, arm: 'B', value: 4 }, { block: 0, arm: 'A', value: 2 }]
  expect(permutationTest(one, { permutations: 1000, seed: 1 })).toMatchObject({ exact: true, permutations: 6, p: 1 / 3, estimate: 3.5 / 1.5 - 1 })
  // Two such blocks: 36 labellings; observed A = {1, 2, 1, 2} (median 1.5) against B = {3, 4, 3, 4} (3.5). With A = {1, 2}
  // in one block, the other block's A of {1, 2} or {1, 3} keeps 1.5 against 3.5 (A {1, 1, 2, 3}, B {2, 3, 4, 4}), while
  // {1, 4} gives B a median of 3 and the rest raise A's: 3 labellings, and their 3 mirror images: p = 6/36. The design's
  // floor is the observed labelling and its mirror alone: 2/36.
  const two = [...one, ...one.map(run => ({ ...run, block: 1 }))]
  const pair = permutationTest(two, { permutations: 1000, seed: 1 })
  expect(pair).toMatchObject({ exact: true, permutations: 36, smallestP: 2 / 36 })
  expect(pair.p).toBeCloseTo(6 / 36, 12)
  // The declared family: 40 tests at alpha 0.05 need p below 0.05 / 40 = 0.00125 to reject at all. Ten ABBA blocks
  // (6^10 labellings) drawn R = 19999 times reach 1 / 20000; an arm flip per block (two runs, 2^10 = 1024 labellings)
  // cannot go below 2 / 1024, so it could never reject.
  const abba: RunValue[] = [], flip: RunValue[] = []
  for (let block = 0; block < 10; block += 1) {
    ;(['A', 'B', 'B', 'A'] as const).forEach((arm, i) => abba.push({ block, arm, value: 10 + block + i }))
    flip.push({ block, arm: 'A', value: 10 + block }, { block, arm: 'B', value: 11 + block })
  }
  expect(permutationTest(abba, { permutations: 19999, seed: 1 }).smallestP).toBe(1 / 20000)
  // A drawn p is (c + 1) / (R + 1), so it is a whole number of 1 / 20000 steps and never 0, even when, as here (A at
  // 1, 2 and B at 100, 101 in every block), almost no drawn labelling reaches the observed one.
  const extreme: RunValue[] = []
  for (let block = 0; block < 10; block += 1)
    extreme.push({ block, arm: 'A', value: 1 }, { block, arm: 'B', value: 100 }, { block, arm: 'B', value: 101 }, { block, arm: 'A', value: 2 })
  const drawn = permutationTest(extreme, { permutations: 19999, seed: 1 })
  expect(drawn).toMatchObject({ exact: false, permutations: 19999 })
  expect(drawn.p).toBeGreaterThanOrEqual(1 / 20000)
  expect(drawn.p).toBeLessThan(0.00125)
  expect(drawn.p * 20000).toBeCloseTo(Math.round(drawn.p * 20000), 9)
  expect(permutationTest(flip, { permutations: 19999, seed: 1 })).toMatchObject({ exact: true, smallestP: 2 / 1024 })
  expect(() => permutationTest([{ block: 0, arm: 'A', value: 1 }, { block: 0, arm: 'A', value: 2 }], { permutations: 10, seed: 1 })).toThrow()
  // An unbalanced block (A 1, 2; B 3) has 3 labellings and no mirror labelling, so its floor is 1/3, not 2/3.
  const unbalanced: RunValue[] = [{ block: 0, arm: 'A', value: 1 }, { block: 0, arm: 'A', value: 2 }, { block: 0, arm: 'B', value: 3 }]
  expect(permutationTest(unbalanced, { permutations: 100, seed: 1 })).toMatchObject({ exact: true, permutations: 3, smallestP: 1 / 3 })
})

test('an A/A family is judged only when complete, clean and under the statistics committed with it', () => {
  // Synthetic session records and p-values only: no measured data is scored here.
  const statistics = { revision: 2, test: 'within-block permutation', permutations: 19999 }
  const recorded = { permutations: 19999, test: 'within-block permutation', revision: 2 }
  const commit = 'c'.repeat(40)
  // One headless W1 session that ran the declared priming pass: A single, A dual (slot -2), then B dual, B single (-1).
  const priming = [
    { primed: true, slot: -2, arm: 'A', position: 0, workload: 'W1', topology: 'single' }, { primed: true, slot: -2, arm: 'A', position: 1, workload: 'W1', topology: 'dual' },
    { primed: true, slot: -1, arm: 'B', position: 0, workload: 'W1', topology: 'dual' }, { primed: true, slot: -1, arm: 'B', position: 1, workload: 'W1', topology: 'single' },
  ]
  const primedRuns = [0, 1, 2, 3].map(index => ({ index, status: 'passed' }))
  const session = {
    kind: 'headless', workloads: ['W1'], priming, primedRuns,
    protocol: { statistics: recorded }, committedStatistics: recorded, harness: { commit, dirtyEntries: 0 }, end: { commit, dirtyEntries: 0 },
  }
  const base = { pilot: false, sessions: [session], statistics, topologies: ['single', 'dual'], alpha: 0.05, declaredFamilySize: 40 }
  const of = (n: number, p: number) => Array.from({ length: n }, () => p)
  // Complete (40 of 40), a reachable floor (1/20000 < 0.05/40): judged; one Holm p at alpha fails it.
  expect(judgeFamily({ ...base, adjusted: of(40, 1), smallestP: of(40, 1 / 20000) }).verdict).toBe('passed')
  expect(judgeFamily({ ...base, adjusted: [0.05, ...of(39, 1)], smallestP: of(40, 1 / 20000) })).toMatchObject({ verdict: 'failed', significant: 1 })
  // Refused, never passed: one ABBA block's floor (1/3); an empty family (the failed-run rule can empty it); 8 of 40.
  expect(judgeFamily({ ...base, adjusted: of(40, 1), smallestP: of(40, 1 / 3) }).verdict).toBe('refused')
  expect(judgeFamily({ ...base, adjusted: [], smallestP: [] }).verdict).toBe('refused')
  expect(judgeFamily({ ...base, adjusted: of(8, 1), smallestP: of(8, 1 / 20000) }).verdict).toBe('refused')
  // Not judged: another statistics block (revision 1), the same test with a different R, a declaration other than the
  // one committed at the session's harness commit, no session, the smoke override, a dirty start, no end record, an
  // end at another commit or on a dirty tree.
  const judgedWith = (sessions: object[]) => judgeFamily({ ...base, sessions, adjusted: of(40, 1), smallestP: of(40, 1 / 20000) }).verdict
  const otherR = { ...recorded, permutations: 9999 }
  expect(judgedWith([session, { ...session, protocol: { statistics: { iterations: 10000, seed: 20261006 } } }])).toBe('not judged')
  expect(judgedWith([{ ...session, protocol: { statistics: otherR }, committedStatistics: otherR }])).toBe('not judged')
  expect(judgedWith([{ ...session, committedStatistics: otherR }])).toBe('not judged')
  expect(judgedWith([{ ...session, committedStatistics: null }])).toBe('not judged')
  expect(judgedWith([])).toBe('not judged')
  expect(judgedWith([{ ...session, dirtyOverride: true }])).toBe('not judged')
  expect(judgedWith([{ ...session, harness: { commit, dirtyEntries: 2 } }])).toBe('not judged')
  expect(judgedWith([{ ...session, end: null }])).toBe('not judged')
  expect(judgedWith([{ ...session, end: { commit: 'd'.repeat(40), dirtyEntries: 0 } }])).toBe('not judged')
  expect(judgedWith([{ ...session, end: { commit, dirtyEntries: 1 } }])).toBe('not judged')
  // Revision 3. Not judged: no priming record, priming in another order (B first), a priming run left out of the
  // record, an end record missing a priming run or listing none, a failed priming run, and two headless sessions in one
  // run set (each numbers its blocks from 0, so the analysis would merge them). A browser session beside the one
  // headless session is no second headless session.
  expect(judgedWith([{ ...session, priming: undefined }])).toBe('not judged')
  expect(judgedWith([{ ...session, priming: [...priming.slice(2), ...priming.slice(0, 2)] }])).toBe('not judged')
  expect(judgedWith([{ ...session, priming: priming.slice(1) }])).toBe('not judged')
  expect(judgedWith([{ ...session, primedRuns: primedRuns.slice(1) }])).toBe('not judged')
  expect(judgedWith([{ ...session, primedRuns: null }])).toBe('not judged')
  expect(judgedWith([{ ...session, primedRuns: [...primedRuns.slice(0, 3), { index: 3, status: 'failed' }] }])).toBe('not judged')
  expect(judgedWith([session, { ...session }])).toBe('not judged')
  expect(judgedWith([session, { ...session, kind: 'browser', priming: undefined, primedRuns: null }])).toBe('passed')
  // An end record listing more priming runs than the declared pass is not that pass. A browser session that recorded
  // other statistics (its committed block being the one in force, so only the recorded-statistics rule can see it) is
  // not judged: the single-headless-session rule does not count it.
  expect(judgedWith([{ ...session, primedRuns: [...primedRuns, { index: 4, status: 'passed' }] }])).toBe('not judged')
  const browserOtherR = { ...session, kind: 'browser', priming: undefined, primedRuns: null, protocol: { statistics: otherR } }
  expect(judgedWith([session, browserOtherR])).toBe('not judged')
  expect(judgeFamily({ ...base, pilot: true, adjusted: of(40, 0), smallestP: of(40, 1 / 20000) }).verdict).toBe('pilot')
})

test('under a null with block effects the permutation test rejects at its nominal rate', () => {
  // 400 synthetic A/A sets of ten ABBA blocks: each block has its own level (the host drifting between blocks, up to
  // 50% of the noise-free value) and every run independent noise, with no arm effect. At alpha 0.05 and R = 99 the
  // rejection rate must stay within 3.5 binomial standard deviations (0.011 for 400 sets) of 0.05. This checks gross
  // validity at 0.05 only, not the far tail where Holm's alpha / 40 sits; the tail rests on the exact construction.
  const random = mulberry32(20261006)
  let rejected = 0
  const sets = 400
  for (let n = 0; n < sets; n += 1) {
    const runs: RunValue[] = []
    for (let block = 0; block < 10; block += 1) {
      const level = 1 + 0.5 * random()
      for (const arm of ['A', 'B', 'B', 'A'] as const) runs.push({ block, arm, value: level * (1 + 0.1 * (random() + random() + random() - 1.5)) })
    }
    if (permutationTest(runs, { permutations: 99, seed: n + 1 }).p <= 0.05) rejected += 1
  }
  expect(Math.abs(rejected / sets - 0.05)).toBeLessThan(3.5 * Math.sqrt((0.05 * 0.95) / sets))
})

test('a drawn bootstrap is reproduced by its recorded seed', () => {
  const runs: RunValue[] = []
  for (let block = 0; block < 6; block += 1)
    for (const [i, arm] of (['A', 'B', 'B', 'A'] as const).entries()) runs.push({ block, arm, value: 10 + ((block * 7 + i * 3) % 5) })
  const first = relativeEffect(runs, { iterations: 2000, seed: 42 })
  expect(first.exact).toBe(false)
  expect(relativeEffect(runs, { iterations: 2000, seed: 42 })).toEqual(first)
})

test('Holm adjusts a family step-down and keeps the input order', () => {
  // Sorted 0.005, 0.01, 0.03, 0.04 times 4, 3, 2, 1 = 0.02, 0.03, 0.06, 0.04; monotone: 0.02, 0.03, 0.06, 0.06.
  const adjusted = holm([0.01, 0.04, 0.03, 0.005])
  ;[0.03, 0.06, 0.06, 0.02].forEach((value, i) => expect(adjusted[i]).toBeCloseTo(value, 12))
  // Capped at 1: 0.5 x 2 = 1, then 0.6 x 1 = 0.6 is raised to the running 1.
  expect(holm([0.6, 0.5])).toEqual([1, 1])
})

test('the pilot plans an even number of runs per arm between the pilot and the cap', () => {
  // n = ceil(n0 (h / target)^2): 4 x 4 = 16; 4 x 9 = 36 capped at 20; 4 x 0.25 = 1 raised to the pilot's 4;
  // 4 x 2.25 = 9 rounded up to 10 so each ABBA block stays whole.
  expect(plannedRuns({ halfWidthPp: 2, runsPerArm: 4 }, 1, 20)).toBe(16)
  expect(plannedRuns({ halfWidthPp: 3, runsPerArm: 4 }, 1, 20)).toBe(20)
  expect(plannedRuns({ halfWidthPp: 0.5, runsPerArm: 4 }, 1, 20)).toBe(4)
  expect(plannedRuns({ halfWidthPp: 1.5, runsPerArm: 4 }, 1, 20)).toBe(10)
})

// #1542 capture-only contracts. Literal schedule arithmetic is independent of the allocator. File fixtures prove
// byte binding, not SQLite consistency. Platform/event/closure fixtures prove admission, not native execution.
test('paired browser CLI uses the declared Williams rows and balances arms and both rates at every position', () => {
  const child = spawnSync(process.execPath, ['perf/run-perf.ts', 'browser-plan'], { encoding: 'utf8' })
  expect(child.status, child.stderr).toBe(0)
  const plan = JSON.parse(child.stdout)
  const rows = [
    [0, 1, 7, 2, 6, 3, 5, 4], [1, 2, 0, 3, 7, 4, 6, 5],
    [2, 3, 1, 4, 0, 5, 7, 6], [3, 4, 2, 5, 1, 6, 0, 7],
    [4, 5, 3, 6, 2, 7, 1, 0], [5, 6, 4, 7, 3, 0, 2, 1],
    [6, 7, 5, 0, 4, 1, 3, 2], [7, 0, 6, 1, 5, 2, 4, 3],
  ]
  expect(plan.slots).toHaveLength(128)
  expect(plan.inference).toBe('NOT_CALIBRATED')
  for (let cycle = 0; cycle < 2; cycle++) for (let row = 0; row < 8; row++) {
    const slots = plan.slots.slice(cycle * 64 + row * 8, cycle * 64 + row * 8 + 8)
    expect(slots.map((slot: any) => slot.cell)).toEqual(rows[row])
    expect(slots[0].rate).toBe(row % 2 ? 64 : 1)
    const declaredArms = ['A', 'B', 'B', 'A', 'A', 'B', 'B', 'A', 'B', 'A', 'A', 'B', 'B', 'A', 'A', 'B']
    expect(slots.map((slot: any) => slot.arm)).toEqual(Array(8).fill(declaredArms[cycle * 8 + row]))
  }
  for (let cell = 0; cell < 8; cell++) for (let position = 0; position < 8; position++) {
    expect(plan.slots.filter((slot: any) => slot.cell === cell && slot.position === position).map((slot: any) => slot.arm).sort()).toEqual(['A', 'B'])
  }
  for (let position = 0; position < 8; position++) expect(plan.slots.filter((slot: any) => slot.position === position && slot.rate === 64)).toHaveLength(8)
})

test('cold browser inputs preserve exact DB sidecar and evidence bytes, refuse changed input and retain empty directories', ({ browserName: _browserName }, info) => {
  const source = info.outputPath('cold-source'), copy = info.outputPath('cold-copy')
  mkdirSync(join(source, 'evidence', 'empty'), { recursive: true })
  mkdirSync(copy)
  writeFileSync(join(source, 'aerolink.db'), 'literal closed fixture bytes')
  writeFileSync(join(source, 'aerolink.db-wal'), 'literal retained WAL bytes')
  writeFileSync(join(source, 'evidence', 'controlled.bin'), new Uint8Array([0, 4, 255]))
  const expected = coldPayload(source)
  expect(expected.map(entry => entry.path)).toEqual(['aerolink.db', 'aerolink.db-wal', 'evidence', 'evidence/controlled.bin', 'evidence/empty'])
  copyColdPayload(source, copy, expected)
  expect(readFileSync(join(copy, 'evidence/controlled.bin'))).toEqual(Buffer.from([0, 4, 255]))
  expect(coldPayload(copy)).toEqual(expected)
  writeFileSync(join(source, 'aerolink.db-wal'), 'different retained WAL bytes')
  expect(() => copyColdPayload(source, info.outputPath('must-not-copy'), expected)).toThrow('cold source bytes changed')
})

test('complete paired capture stays uncalibrated; contradictory direct clocks, lineage, source, seed or closure refuse', ({ browserName: _browserName }, info) => {
  const plan = browserCapturePlan(), protocol = JSON.parse(readFileSync('perf/protocol.json', 'utf8'))
  const arm = { commit: '1'.repeat(40), srcTree: '2'.repeat(40), lockBlob: '3'.repeat(40), viteBlob: '4'.repeat(40), distSha256: '5'.repeat(64), distFiles: 2 }
  const cohort = { protocol, runtime: { node: '24.18.0', v8: 'fixture-v8', playwright: '1.61.1', os: { platform: 'win32', release: 'literal-os', arch: 'x64' }, cpu: { model: 'literal-cpu', logicalCores: 16 }, memoryGiB: 32 },
    harness: { commit: '6'.repeat(40), apiTree: '7'.repeat(40), apiAssemblySha256: '8'.repeat(64), dirtyEntries: 0 } }
  const out = info.outputPath('paired-report'), archive = join(out, 'cold/payload')
  mkdirSync(join(archive, 'evidence'), { recursive: true })
  writeFileSync(join(archive, 'aerolink.db'), 'literal already-closed payload fixture')
  writeFileSync(join(out, 'cold/seed.json'), '{"seed":"literal"}')
  writeFileSync(join(out, 'cold/closure.json'), JSON.stringify({ guardianSha256: CAPTURE_GUARDIAN, status: 'EXITED', exitCode: 0, jobCount: 0, handlesClosed: true, forced: false,
    producerBirth100ns: '134360000000000000', producerImage: 'literal-seed-producer.exe', producerArgv: 'literal-seed-fixture', closedAt: '2026-10-14T00:00:00Z',
    apiTree: cohort.harness.apiTree, apiAssemblySha256: cohort.harness.apiAssemblySha256 }))
  const bytesHash = (name: string) => createHash('sha256').update(readFileSync(join(out, name))).digest('hex')
  const cold = { payloadSha256: digest(coldPayload(archive)), seedSha256: bytesHash('cold/seed.json'), closureSha256: bytesHash('cold/closure.json') }
  const session = { id: 'literal-session', plan, cohort, cold, arms: { A: arm, B: arm }, startedAt: '2026-10-14T00:00:00Z', monotonicStart: 100, deadline: 14_400_100 }
  const closure = { guardianSha256: CAPTURE_GUARDIAN, status: 'EXITED', exitCode: 0, jobCount: 0, handlesClosed: true, forced: false,
    producerBirth100ns: '134360000000000000', producerImage: 'literal-node.exe', producerArgv: 'literal-capture-cli', closedAt: '2026-10-14T02:40:01Z',
    exitedAt: '2026-10-14T02:40:00.500Z',
    // The outer owner's monotonic origin deliberately differs from the producer's100ms origin.
    wholeOwner: { startedAt: '2026-10-14T00:00:00Z', monotonicStartMs: 1000, measurementDeadlineMs: 14_401_000,
      cleanupDeadlineMs: 14_701_000, producerExitedMs: 9_601_500, closedMs: 9_602_000 },
    sessionId: session.id, sourceCommit: cohort.harness.commit }
  const end = { ...session, closure, stopped: null, endedAt: '2026-10-14T02:40:00Z', monotonicEnd: 9_600_100 }
  let nextMonotonic = 100
  const rows = plan.slots.map(slot => {
    // Independent fixed-work receipt fixture: 60 warmup batches, then 240 one-frame batches at1x or168
    // 64-frame batches at64x (42wallseconds/10,752frames, the seven6-second input steps' overshoot).
    const batches = slot.rate === 1 ? 240 : 168, steps = slot.rate === 1 ? 10 : 7
    const timeline: any[] = [], clocks: any[] = [], tasks: any[] = []
    for (let batch = 1; batch <= 60 + batches + 1; batch++) {
      const start = batch * 250, end = start + 1, before = (batch - 1) * slot.rate, after = batch * slot.rate
      const sample = (monotonicMs: number, completed: number) => ({ utcMs: pacingEpoch + monotonicMs, monotonicMs, timeOrigin: pacingEpoch,
        kernelTime: completed * 2_500_000, unitClockMs: pacingEpoch + completed * 250, completed })
      timeline.push({ kind: 'slice', start, end, detail: { schema: 1, epoch: 1, rate: slot.rate, batch, admitted: slot.rate, attempted: slot.rate, completed: slot.rate, remaining: 0 } })
      clocks.push({ schema: 1, epoch: 1, sequence: batch, rate: slot.rate, batch, entry: sample(start, before), exit: sample(end, after) })
      tasks.push({ start, end, tail: end })
    }
    const begin = 15_001, end = (60 + batches) * 250 + 1
    const boundary = (label: string, at: number) => ({ label, utcMs: pacingEpoch + at, monotonicMs: at, timeOrigin: pacingEpoch,
      trace: clocks.filter(clock => clock.exit.monotonicMs <= at).at(-1) })
    const inputs = Array.from({ length: steps * 2 }, (_, index) => {
      const offset = Math.floor(index / 2) * 6000 + index % 2 * 1000
      return { key: 'BRT', target: 0, trusted: true, eventTime: begin + offset + 0.2, dispatchStart: begin + offset + 0.1, dispatchEnd: begin + offset + 0.3, before: 'brightness:1', after: 'brightness:1' }
    })
    const rawObservation = { begin, end, timeline, tasks, rafIntervals: [], longTasks: [], loaf: [],
      events: inputs.map((input, index) => ({ start: input.eventTime, name: 'click', targetKey: 'BRT', targetIndex: 0, duration: 16, processing: 1, delay: 0, interaction: index + 1 })),
      capture: { clocks, boundaries: [boundary('warmupEnd', begin), boundary('scoredStart', begin), boundary('scoredEnd', end)], inputs } }
    const accounting = browserWindowAccounting({ rate: slot.rate, ...rawObservation })
    expect(accounting.frames.completed).toBe(slot.rate === 1 ? 240 : 10_752)
    const monotonicStartedAt = nextMonotonic + 5000
    const monotonicEndedAt = monotonicStartedAt + (slot.rate === 1 ? 76_000 : 58_000)
    nextMonotonic = monotonicEndedAt
    return { status: 'passed', startedAt: new Date(Date.UTC(2026, 9, 14) + monotonicStartedAt - 100).toISOString(), monotonicStartedAt, monotonicEndedAt, lineage: { session: session.id, manifest: digest(plan), ...slot }, cohort, cold, copySha256: cold.payloadSha256, copiedInputs: coldPayload(archive),
      armIdentity: arm, servedAtEnd: arm, host: { quietBefore: { quiet: true, waitedSeconds: 5, samples: [{ utc: '2026-10-14T00:00:29Z', monotonicMs: 1000, busyPercent: 2 }] }, busyPercentDuringRun: 20 },
      result: { accounting, rawObservation, scenario: plan.scenario, rate: slot.rate, configuration: slot.configuration,
        window: { targetFrames: slot.rate === 1 ? 240 : 9600, stalled: false, steps }, interaction: { presses: steps * 2 }, refusal: null, scenarioFinishedInWindow: false,
        browser: { version: 'literal-chromium', renderer: 'literal-renderer', headless: true, viewport: protocol.browser.viewport } } }
  })
  expect(captureDisposition(plan, session, rows, end)).toMatchObject({ status: 'CAPTURE_COMPLETE', qualification: 'NOT_CALIBRATED', inference: 'DISABLED', reasons: [] })
  const orderedSamples = structuredClone(rows)
  orderedSamples[0].result.rawObservation.capture.clocks[60].entry.monotonicMs += 0.25
  orderedSamples[0].result.rawObservation.capture.clocks[60].exit.monotonicMs += 0.25
  // Actual ENTRY follows slice start and EXIT follows the recorded slice end; equality is not required.
  expect(captureDisposition(plan, session, orderedSamples, end).qualification).toBe('NOT_CALIBRATED')
  const reject = (mutate: (value: any) => void, label = 'contradictory capture') => {
    const value = structuredClone({ session, rows, end }); mutate(value)
    expect.soft(captureDisposition(plan, value.session, value.rows, value.end).qualification, label).toBe('NOT_QUALIFIED')
  }
  const changeClocks = (value: any, change: (clocks: any[]) => void) => {
    const capture = value.rows[0].result.rawObservation.capture
    change(capture.clocks)
    // Keep page receipts coherent with the changed direct stream: rejection must come from pacing attribution.
    for (const boundary of capture.boundaries) boundary.trace = structuredClone(capture.clocks.filter((clock: any) => clock.exit.monotonicMs <= boundary.monotonicMs).at(-1))
  }
  reject(value => changeClocks(value, clocks => clocks.forEach(clock => clock.epoch = 2)), 'coherent wrong epoch must refuse')
  reject(value => changeClocks(value, clocks => clocks.forEach(clock => clock.rate = 64)), 'coherent wrong rate must refuse')
  reject(value => changeClocks(value, clocks => clocks.forEach(clock => clock.batch += 10)), 'coherent wrong batch must refuse')
  reject(value => changeClocks(value, clocks => {
    // Move one successful frame from the second scored slice into the first. Totals, continuity and clock deltas
    // still agree, but the independently declared one-frame timeline slices do not.
    for (const sample of [clocks[60].exit, clocks[61].entry]) {
      sample.completed += 1; sample.kernelTime += 2_500_000; sample.unitClockMs += 250
    }
  }), 'coherent wrong per-slice completed work must refuse')
  const capEdge = structuredClone(end)
  Object.assign(capEdge.closure.wholeOwner, { producerExitedMs: 14_401_000, closedMs: 14_701_000 })
  Object.assign(capEdge.closure, { exitedAt: '2026-10-14T04:00:00Z', closedAt: '2026-10-14T04:05:00Z' })
  expect(captureDisposition(plan, session, rows, capEdge)).toMatchObject({ status: 'CAPTURE_COMPLETE', qualification: 'NOT_CALIBRATED' })
  reject(value => value.end.closure.closedAt = '2026-10-15T00:00:00Z', 'late UTC closure must refuse')
  reject(value => value.end.closure.wholeOwner.closedMs = 14_701_001, 'one millisecond beyond cleanup must refuse')
  reject(value => delete value.end.closure.wholeOwner, 'missing original owner budget must refuse')
  reject(value => value.end.closure.wholeOwner.cleanupDeadlineMs = 14_701_001, 'supplied budget cannot extend cleanup')
  reject(value => value.end.closure.wholeOwner.measurementDeadlineMs = 14_401_001, 'supplied budget cannot extend measurement')
  reject(value => value.end.closure.wholeOwner.producerExitedMs = 14_401_001, 'early end marker cannot hide final observations or I/O after deadline')
  reject(value => value.rows.pop())
  reject(value => value.rows[1].monotonicStartedAt = value.rows[0].monotonicStartedAt)
  reject(value => value.rows[0].monotonicEndedAt = value.rows[0].monotonicStartedAt + 1000)
  reject(value => value.rows[1] = value.rows[0])
  reject(value => value.rows[0].copySha256 = 'c'.repeat(64))
  reject(value => value.end.closure.forced = true)
  reject(value => value.end.closure.sessionId = 'different-session')
  reject(value => value.end.closure.closedAt = '2026-10-13T23:59:59Z')
  reject(value => value.rows[0].cohort.harness.dirtyEntries = 1)
  reject(value => value.rows[0].servedAtEnd = { ...arm, distSha256: 'd'.repeat(64) })
  reject(value => value.rows[0].result.rawObservation.capture.clocks[0].exit.unitClockMs += 250)
  reject(value => value.rows[0].result.rawObservation.events = [])
  reject(value => value.rows[0].result.rawObservation.capture.inputs[0].trusted = false)
  writeFileSync(join(out, 'paired-session.json'), JSON.stringify(session))
  writeFileSync(join(out, 'paired-end.json'), JSON.stringify(end))
  writeFileSync(join(out, 'runs.jsonl'), rows.map(row => JSON.stringify(row)).join('\n') + '\n')
  const report = () => {
    const child = spawnSync(process.execPath, ['perf/run-perf.ts', 'browser-capture-report', '--out', out], { encoding: 'utf8' })
    expect(child.status, child.stderr).toBe(0)
    return JSON.parse(readFileSync(join(out, 'paired-report.json'), 'utf8'))
  }
  expect(report()).toMatchObject({ status: 'CAPTURE_COMPLETE', qualification: 'NOT_CALIBRATED' })
  const lateEnd = structuredClone(end)
  lateEnd.closure.wholeOwner.producerExitedMs = 14_401_001
  writeFileSync(join(out, 'paired-end.json'), JSON.stringify(lateEnd))
  expect.soft(report().qualification, 'actual report CLI refuses late final work despite an in-bound producer end marker').toBe('NOT_QUALIFIED')
  writeFileSync(join(out, 'paired-end.json'), JSON.stringify(end))
  writeFileSync(join(archive, 'aerolink.db'), 'mutated cold payload fixture')
  expect(report()).toMatchObject({ status: 'CAPTURE_PARTIAL', qualification: 'NOT_QUALIFIED', reasons: ['retained cold archive, seed or writer-closure bytes are missing or changed'] })
})
test('the actual recorder drains queued EventTiming before sealing its scored window', () => {
  // A platform queue control: records are pending, and the observer callback has not fired. This exercises the
  // real init-script boundary, not a copy of its flush predicate; it makes no native latency/paint claim.
  const observers: { type: string; pending: unknown[] }[] = []
  class Observer {
    type = ''; pending: unknown[] = []
    constructor(_callback: unknown) { observers.push(this) }
    observe(options: { type: string }) { this.type = options.type }
    takeRecords() { const records = this.pending; this.pending = []; return records }
  }
  let clock = 0
  const page: any = { setTimeout: () => 1, requestAnimationFrame: () => 1 }
  page.top = page
  runInNewContext(`(${installBrowserRecorder.toString()})({slice:'slice',discard:'discard'})`, {
    window: page, performance: { now: () => ++clock, timeOrigin: 1234 }, PerformanceObserver: Observer,
    document: { addEventListener() {}, querySelectorAll: () => [] }, Element: class {}, Date, queueMicrotask,
  })
  const begin = page.__fmsPerf.begin()
  observers.find(observer => observer.type === 'event')!.pending.push({ startTime: begin + 0.5, name: 'click', duration: 16, processingStart: begin + 0.5, processingEnd: begin + 1, interactionId: 2 })
  expect(page.__fmsPerf.finish().events).toMatchObject([{ name: 'click', duration: 16, interaction: 2 }])
})

test('paired browser plan refuses finite single-arm routes instead of silently substituting 87N', () => {
  const child = spawnSync(process.execPath, ['perf/run-perf.ts', 'browser-plan', '--workloads', 'W1'], { encoding: 'utf8' })
  expect(child.status, child.stdout).not.toBe(0)
  expect(child.stderr).toContain('paired browser planning/capture supports W2 only')
})

test('the route CLI declares the actual five mission extents, hosts and topology limits', () => {
  const child = spawnSync(process.execPath, ['perf/run-perf.ts', 'route-plan'], { encoding: 'utf8' })
  expect(child.status, child.stderr).toBe(0)
  const plan = JSON.parse(child.stdout)
  expect(plan.schema).toBe('aerolink.fms-perf-route-plan.v1')
  expect(plan.inference).toBe('NOT_CALIBRATED')
  expect(plan.routes).toHaveLength(25)
  const headless = plan.routes.filter((route: any) => route.host === 'headless' && route.topology === 'single')
  expect(headless.map((route: any) => [route.workload, route.scenario, route.source.kind, route.scored.maxTotalSimSeconds, route.warmup.unit, route.warmup.value, route.scored.kind, route.scored.fixedMeasuredSimSeconds ?? null])).toEqual([
    ['W1', 'kbtv-rnav15-advisory', 'scenario-library', 900, 'simulation-seconds', 60, 'scenario-end-after-warmup', null],
    ['W2', '87n-offshore-sar', 'heli-mission', 5400, 'simulation-seconds', 60, 'scenario-end-after-warmup', null],
    ['W3', 'perf-w3-dual-sync', 'inline-definition', 1800, 'simulation-seconds', 60, 'fixed-total-after-warmup', 1740],
    ['W4a', 'gps1-fde-then-gps2', 'scenario-library', 3600, 'simulation-seconds', 60, 'scenario-end-after-warmup', null],
    ['W4b', 'dead-reckoning', 'scenario-library', 900, 'simulation-seconds', 60, 'scenario-end-after-warmup', null],
  ])
  expect(headless.every((route: any) => route.status === 'IMPLEMENTED' && route.pacing === 'unpaced' && route.metrics.includes('frame-duration-percentiles'))).toBe(true)
  const ui = plan.routes.filter((route: any) => route.host === 'browser-paced')
  expect(ui.map((route: any) => [route.workload, route.topology, route.status])).toEqual([
    ['W1', 'dual', 'IMPLEMENTED'], ['W2', 'dual', 'IMPLEMENTED'], ['W3', 'dual', 'NOT_IMPLEMENTED'], ['W4a', 'dual', 'IMPLEMENTED'], ['W4b', 'dual', 'IMPLEMENTED'],
  ])
  expect(ui[1]).toMatchObject({ warmup: { unit: 'wall-seconds', value: 15 }, scored: { kind: 'completed-work-by-rate', simSecondsByRate: { '1': 60, '64': 2400 } }, terminal: 'still-running-no-stall-no-discard' })
  expect(ui.filter((route: any) => ['W1', 'W4a', 'W4b'].includes(route.workload)).map((route: any) =>
    [route.id, route.warmup, route.scored, route.scenarioStartUtc, route.executor])).toEqual([
    ['W1/browser-paced/dual/v2', { unit: 'completed-frames', value: 256, simMilliseconds: 64000 }, { kind: 'successful-scenario-end-after-warmup', maxTotalSimSeconds: 900 }, '2026-09-27T14:00:00.000Z', 'single-arm-browser'],
    ['W4a/browser-paced/dual/v2', { unit: 'completed-frames', value: 256, simMilliseconds: 64000 }, { kind: 'successful-scenario-end-after-warmup', maxTotalSimSeconds: 3600 }, '2026-09-27T14:00:00.000Z', 'single-arm-browser'],
    ['W4b/browser-paced/dual/v2', { unit: 'completed-frames', value: 256, simMilliseconds: 64000 }, { kind: 'successful-scenario-end-after-warmup', maxTotalSimSeconds: 900 }, '2026-09-27T14:00:00.000Z', 'single-arm-browser'],
  ])
  expect(ui.find((route: any) => route.workload === 'W3')).toMatchObject({ warmup: null, scored: null, terminal: null })
  const unpaced = plan.routes.filter((route: any) => route.host === 'browser-unpaced')
  expect(unpaced).toHaveLength(10)
  expect(unpaced.every((route: any) => route.status === 'NOT_IMPLEMENTED' && route.pacing === 'unpaced' && route.warmup === null && route.scored === null)).toBe(true)
  expect(plan.routes.find((route: any) => route.workload === 'W3' && route.host === 'headless' && route.topology === 'single').role).toBe('topology-comparator')
  expect(plan.routes.find((route: any) => route.workload === 'W3' && route.host === 'headless' && route.topology === 'dual').role).toBe('dual-SYNC-residency')
})

test('execution route refusal precedes output, arm and child setup', ({ browserName: _browserName }, info) => {
  for (const [command, workload, topology, message] of [
    ['browser-capture', 'W1', 'dual', 'paired browser planning/capture supports W2 only'],
    ['browser-capture', 'W3', 'dual', 'route W3/browser-paced/dual is NOT_IMPLEMENTED'],
    ['browser', 'W2', 'single', 'topology single is not available for browser-paced'],
    ['headless', 'UNKNOWN', 'dual', 'unknown workload UNKNOWN'],
  ]) {
    const output = info.outputPath(`${command}-${workload}-${topology}`)
    const child = spawnSync(process.execPath, ['perf/run-perf.ts', command, '--workloads', workload, ...(command === 'headless' ? [] : ['--topology', topology]), '--out', output], { encoding: 'utf8' })
    expect(child.status).not.toBe(0)
    expect(child.stderr).toContain(message)
    expect(existsSync(output), 'unsupported work refuses before any output/seed/child launch').toBe(false)
  }
})

// Independent literal finite record: four complete 64-frame warmup batches, then three completed frames and
// the actual remaining 61-frame discard. This is a reader control, not an authentic browser/native receipt.
function finiteRecord() {
  const timeline: any[] = [], clocks: any[] = [], tasks: any[] = []
  const origin = Date.parse('2026-09-27T14:00:00.000Z')
  let done = 0
  for (const [index, count] of [64, 64, 64, 64, 3].entries()) {
    const start = (index + 1) * 250, end = start + 2
    const sample = (n: number, mono: number) => ({ utcMs: 10000 + mono, monotonicMs: mono, timeOrigin: 10000,
      kernelTime: n * 2500000, unitClockMs: origin + n * 250, completed: n })
    timeline.push({ kind: 'slice', start, end, detail: { schema: 1, epoch: 7, rate: 64, batch: index + 1,
      admitted: 64, attempted: count, completed: count, remaining: 64 - count } })
    clocks.push({ schema: 1, epoch: 7, rate: 64, sequence: index + 1, batch: index + 1, entry: sample(done, start), exit: sample(done + count, end) })
    tasks.push({ start, end, tail: end + 1 }); done += count
  }
  timeline.push({ kind: 'discard', start: 1252, end: 1252, detail: { schema: 1, epoch: 7, rate: 64, batch: 5, discarded: 61 } })
  const close = { schema: 1, epoch: 7, rate: 64, sequence: 5, batch: 5, kind: 'run-end', admitted: 320, attempted: 259,
    completed: 259, discarded: 61, debt: 0, monotonicMs: 1252, utcMs: 11252, timeOrigin: 10000, lastClock: clocks[4] }
  const boundary = (label: string, monotonicMs: number, trace: any) => ({ label, monotonicMs, utcMs: 10000 + monotonicMs, timeOrigin: 10000, trace })
  return { begin: 1003, end: 1253, timeline, tasks, closes: [close], rafIntervals: [], longTasks: [], loaf: [], events: [],
    capture: { clocks, boundaries: [boundary('warmupEnd', 1003, clocks[3]), boundary('scoredStart', 1003, clocks[3]), boundary('scoredEnd', 1253, clocks[4])], inputs: [] } }
}
const finiteExpected = { scenario: 'literal-perf-finite-v2', digest: 'fnv1a-12345678', startUtc: '2026-09-27T14:00:00.000Z',
  profile: 'cma9000-s300-heli-civil', steps: 2, statuses: ['DONE', 'PASS'] as ('DONE' | 'PASS')[], maxSimSeconds: 900, gps: { model: 'literal-model/1', constellation: 1, receivers: [101, 202] } }
const finiteReport = `# FMS Test Bench run: Independent literal
**Result: PASS**
- Scenario: literal-perf-finite-v2, fnv1a-12345678
- Started: 2026-09-27T14:00:00.000Z; ended after 1 min 4.75 s of simulated time
- Aircraft profile: cma9000-s300-heli-civil
- GPS: model literal-model/1; constellation seed 1; receiver seeds 101 and 202; retained sky
| 1 | At start, action. | 0 s | DONE |  |
| 2 | Then check. | 1 min 4.75 s | PASS | observed |
`
test('finite mission admission conserves actual closed work and requires the independent complete public PASS report', () => {
  const record = finiteRecord()
  const account = (r = record) => browserWindowAccounting({ rate: 64, ...r, closure: r.closes[0] })
  expect(captureEvidence(record, account()).boundaryStatus).toBe('OBSERVED')
  expect(finiteMissionEvidence(record, account(), finiteReport, finiteExpected)).toMatchObject({ status: 'OBSERVED', totalFrames: 259, scoredFrames: 3, endedSimSeconds: 64.75, qualification: 'NOT_CALIBRATED', inference: 'DISABLED' })
  for (const changed of [
    finiteReport.replace('Result: PASS', 'Result: FAIL'), `${finiteReport}**Result: FAIL**\n`, finiteReport.replace('| PASS |', '| NOT REACHED |'),
    finiteReport.replace('4.75 s of', '4.5 s of'), finiteReport.replace('14:00:00.000Z', '14:00:01.000Z'),
    finiteReport.replace('101 and 202', '101 and 303'), finiteReport.replace('literal-perf-finite-v2,', 'wrong-scenario,'),
    finiteReport.replace('cma9000-s300-heli-civil', 'wrong-profile'),
  ]) expect(() => finiteMissionEvidence(record, account(), changed, finiteExpected)).toThrow(/public finite mission report/)
  for (const key of ['epoch', 'sequence', 'completed', 'batch', 'discarded']) {
    const changed = structuredClone(record); (changed.closes[0] as any)[key]++
    expect(() => account(changed), key).toThrow(/finite epoch close/)
  }
  const dropped = structuredClone(record); dropped.timeline.splice(4, 1)
  expect(() => account(dropped)).toThrow(/discarded|finite epoch close/)
  const straddled = structuredClone(record); straddled.begin = 1001
  expect(() => account(straddled)).toThrow(/straddles/)
  const early = structuredClone(record); early.capture.boundaries[0].trace.exit.completed = 255
  expect(() => finiteMissionEvidence(early, account(), finiteReport, finiteExpected)).toThrow(/direct clock|warmup/)
  const zero = structuredClone(record); zero.begin = zero.end
  expect(() => account(zero)).toThrow(/no valid span/)
  const stopped = structuredClone(record); stopped.closes[0].kind = 'cancelled'
  expect(() => account(stopped)).toThrow(/finite epoch close/)
  const missing = structuredClone(record); missing.closes = []
  expect(() => finiteMissionEvidence(missing, account(), finiteReport, finiteExpected)).toThrow(/unique actual epoch close/)
  const wrongLast = structuredClone(record); wrongLast.closes[0].lastClock.sequence++
  expect(() => finiteMissionEvidence(wrongLast, account(), finiteReport, finiteExpected)).toThrow(/direct clock|last actual clock/)
})

test('a zero-debt 1x END needs its actual close; an END label alone cannot hide a lost final slice', () => {
  const timeline = [1, 2, 3].map(batch => ({ kind: 'slice' as const, start: batch * 250, end: batch * 250 + 1,
    detail: { schema: 1, epoch: 8, rate: 1, batch, admitted: 1, attempted: 1, completed: 1, remaining: 0 } }))
  const tasks = timeline.map(slice => ({ start: slice.start, end: slice.end, tail: slice.end + 1 }))
  const close = { schema: 1, epoch: 8, rate: 1, sequence: 3, batch: 3, kind: 'run-end', admitted: 3, attempted: 3, completed: 3, discarded: 0, debt: 0, monotonicMs: 751 }
  const input = { rate: 1, begin: 0, end: 752, timeline, tasks }
  expect(() => browserWindowAccounting(input)).toThrow(/no timeline entry/)
  expect(browserWindowAccounting({ ...input, closure: close }).frames.completed).toBe(3)
  expect(() => browserWindowAccounting({ ...input, timeline: timeline.slice(0, 2), closure: close })).toThrow(/finite epoch close/)
  expect(() => browserWindowAccounting({ ...input, closure: { ...close, kind: 'fault' } })).toThrow(/finite epoch close/)
})

// Real producer -> actual serialized recorder on deterministic platform queues. No native latency/capture claim.
function finiteRecorder(endSeconds: number, frameCost = 8, loseWarmupClock = false) {
  const pending: Record<string, any>[] = [], microtasks: (() => void)[] = []
  class Observer {
    type = ''
    constructor(_callback: unknown) {}
    observe(options: { type: string }) { this.type = options.type }
    takeRecords() { return this.type === 'measure' ? pending.splice(0) : [] }
  }
  const host = new TimelineHost()
  host.performance = { ...host.performance, timeOrigin: 10000, measure: (name, options) => {
    const { start, end, detail } = options as PerformanceMeasureOptions & { start: number; end: number }
    if (!(loseWarmupClock && name === 'aerolink.fms-bench.pacing-clock' && (detail as any).exit.completed === 256))
      pending.push({ name, startTime: start, duration: end - start, detail })
    return undefined as unknown as PerformanceMeasure
  } }
  const schedule = host.setTimeout.bind(host)
  const page: any = { setTimeout: schedule, requestAnimationFrame: () => 1 }; page.top = page
  runInNewContext(`(${installBrowserRecorder.toString()})({slice:'aerolink.fms-bench.pacing-slice',discard:'aerolink.fms-bench.pacing-discard',clock:'aerolink.fms-bench.pacing-clock',close:'aerolink.fms-bench.pacing-close'})`, {
    window: page, performance: host.performance, PerformanceObserver: Observer,
    document: { addEventListener() {}, querySelectorAll: () => [] }, Element: class {}, Date,
    queueMicrotask: (fn: () => void) => microtasks.push(fn),
  })
  host.setTimeout = (fn, ms) => page.setTimeout(fn, ms)
  const composed = singleComposition(pacingEpoch)
  const runner = new ScenarioRunner({ id: 'finite-recorder', title: 'Finite recorder', objective: '', maxSeconds: 100,
    steps: [{ when: { kind: 'time', seconds: endSeconds }, action: { kind: 'expectAircraft', minAltitude: 0 } }] }, composed.fms, undefined, composed.sim)
  const kernel = new FmsKernel(composed.plant, runner)
  page.__fmsPerf.armFinite()
  startKernelPacing(64, kernel, host, { onFault: error => { throw error }, onInstantClose: () => { host.wall += frameCost } })
  while (host.runNext()) { while (microtasks.length) microtasks.shift()!() }
  return { recorder: page.__fmsPerf, kernel, runner }
}
test('the real recorder latches exactly256 completed warmup frames at a task tail and refuses overshoot or early END', () => {
  const valid = finiteRecorder(64.75)
  expect(valid.runner.outcome).toBe('passed')
  const record = valid.recorder.finish()
  expect(valid.recorder.finiteStatus).toEqual({ started: true, ended: true, error: null })
  expect(record.capture.boundaries.map((b: any) => [b.label, b.trace.exit.completed])).toEqual([
    ['warmupEnd', 256], ['scoredStart', 256], ['scoredEnd', 259],
  ])
  expect(record.capture.boundaries[0].monotonicMs).toBe(record.capture.boundaries[1].monotonicMs)
  expect(browserWindowAccounting({ rate: 64, ...record, closure: record.closes[0] }).frames.completed).toBe(3)
  expect(finiteRecorder(63).recorder.finiteStatus).toMatchObject({ ended: true, error: 'epoch ended before the scored window' })
  // Losing the actual frame256 clock must not let a later261-frame observation stand in for the exact boundary.
  expect(finiteRecorder(80, 7, true).recorder.finiteStatus).toMatchObject({ ended: true, error: 'warmup overshot its exact completed-frame boundary' })
})
