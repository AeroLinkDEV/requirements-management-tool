import { frameSummary } from './stats.ts'

/**
 * Browser accounting revision 2 (#1557): completed frames, not interval count x selected rate.
 *
 * The paced bench admits one finite batch per 250 ms deadline, runs it in task slices of whole frames and discards
 * what a stop leaves. Its pacing timeline (User Timing measures named in protocol.browser.accounting.timeline)
 * reports, per task slice, the frames admitted, attempted and completed and the debt left, and per discard the frames
 * dropped. browserWindowAccounting turns one run's recorded timeline into a measured window's accounting, or refuses:
 * - every entry of the measured epoch is conserved in order (admitted = attempted + discarded + outstanding), from
 *   the epoch's first admission, so a lost, duplicated or reordered entry refuses rather than miscounting;
 * - the window [begin, end] is taken between tasks, so a slice lies wholly inside or outside it; one that straddles
 *   a boundary refuses. Frames admitted before `begin` and completed inside the window count (the window executed
 *   them), and the debt open at each boundary is reported;
 * - simulation cost (each slice's frame attempts) is kept apart from continuation cost: the yield gap between one
 *   slice and its batch's next, and the task tail after a slice (the microtask render the slice's publication
 *   causes). Long tasks, Long Animation Frames, rAF and Event Timing stay separately attributed in the run record.
 */
export const BROWSER_RUN_SCHEMA = 'aerolink.fms-perf-browser-run.v2'
export const HISTORICAL_BROWSER_RUN_SCHEMA = 'aerolink.fms-perf-browser-run.v1'
const FRAME_SECONDS = 0.25
// Measure end = startTime + duration in doubles; boundary comparisons tolerate that rounding, nothing more.
const EPSILON_MS = 1e-6

export type TimelineEntry = { kind: 'slice' | 'discard'; start: number; end: number; detail: Record<string, unknown> }
export type TimerTask = { start: number; end: number; tail: number | null }
export type WindowInput = { rate: number; begin: number; end: number; timeline: readonly TimelineEntry[]; tasks: readonly TimerTask[]; closure?: Record<string, any> }

export class AccountingRefusal extends Error {
  constructor(reason: string) { super(`FMS_BROWSER_ACCOUNTING_REFUSED: ${reason}`) }
}

type Slice = { kind: 'slice'; start: number; end: number; epoch: number; batch: number; admitted: number; attempted: number; completed: number; remaining: number }
type Discard = { kind: 'discard'; start: number; end: number; epoch: number; batch: number; discarded: number }
type Event = Slice | Discard

const whole = (value: unknown, name: string, minimum = 0) => {
  if (!Number.isSafeInteger(value) || (value as number) < minimum) throw new AccountingRefusal(`timeline ${name} ${String(value)} is not a whole number >= ${minimum}`)
  return value as number
}

function parse(entry: TimelineEntry, rate: number): Event {
  const detail = entry.detail ?? {}
  if (detail.schema !== 1) throw new AccountingRefusal(`timeline schema ${String(detail.schema)} is not 1`)
  if (detail.rate !== rate) throw new AccountingRefusal(`a timeline entry was paced at ${String(detail.rate)}x, not the selected ${rate}x`)
  if (!(Number.isFinite(entry.start) && Number.isFinite(entry.end) && entry.end >= entry.start)) throw new AccountingRefusal('a timeline entry has no valid span')
  const common = { start: entry.start, end: entry.end, epoch: whole(detail.epoch, 'epoch', 1), batch: whole(detail.batch, 'batch', 1) }
  if (entry.kind === 'discard') return { kind: 'discard', ...common, discarded: whole(detail.discarded, 'discarded', 1) }
  const slice: Slice = { kind: 'slice', ...common, admitted: whole(detail.admitted, 'admitted'), attempted: whole(detail.attempted, 'attempted', 1),
    completed: whole(detail.completed, 'completed'), remaining: whole(detail.remaining, 'remaining') }
  if (slice.admitted !== 0 && slice.admitted !== rate) throw new AccountingRefusal(`a batch of ${slice.admitted} frames was admitted at ${rate}x`)
  if (slice.completed > slice.attempted) throw new AccountingRefusal('a slice completed more frames than it attempted')
  return slice
}

/** One measured window's accounting from the recorded pacing timeline and timer tasks. Throws AccountingRefusal. */
export function browserWindowAccounting({ rate, begin, end, timeline, tasks, closure }: WindowInput) {
  if (!(Number.isFinite(begin) && Number.isFinite(end) && end > begin)) throw new AccountingRefusal('the measured window has no valid span')
  const events = timeline.map(entry => parse(entry, rate))
  const across = (event: Event, instant: number) => event.start < instant - EPSILON_MS && event.end > instant + EPSILON_MS
  if (events.some(event => across(event, begin) || across(event, end))) throw new AccountingRefusal('a slice straddles a window boundary; boundaries must be taken between tasks')
  const inside = (event: Event) => event.start >= begin - EPSILON_MS && event.end <= end + EPSILON_MS
  const epochs = [...new Set(events.filter(inside).map(event => event.epoch))]
  if (epochs.length !== 1) throw new AccountingRefusal(epochs.length
    ? `the window spans ${epochs.length} pacing epochs (a rate change, pause or restart)`
    : 'no pacing slice was observed in the window: the arm publishes no pacing timeline, or nothing was paced')

  // Conservation, in emission order, from the epoch's first admission.
  let debt = 0, batch = 0, last = -Infinity
  const after: { event: Event; debt: number; batch: number }[] = []
  for (const event of events.filter(candidate => candidate.epoch === epochs[0])) {
    if (event.end < last) throw new AccountingRefusal('the timeline is out of order')
    last = event.end
    if (event.kind === 'slice') {
      if (event.admitted) {
        if (debt !== 0 || event.batch !== batch + 1) throw new AccountingRefusal(after.length
          ? `batch ${event.batch} was admitted with ${debt} frames of batch ${batch} outstanding`
          : 'the timeline does not start at the epoch\'s first admission')
        batch = event.batch
      } else if (event.batch !== batch || debt === 0) throw new AccountingRefusal(`a continuation of batch ${event.batch} has no outstanding admitted frames`)
      debt += event.admitted - event.attempted
      if (debt < 0 || debt !== event.remaining) throw new AccountingRefusal(`batch ${event.batch} reports ${event.remaining} outstanding frames; the timeline conserves ${debt}`)
    } else {
      if (event.batch !== batch || event.discarded !== debt) throw new AccountingRefusal(`batch ${event.batch} discarded ${event.discarded} frames; the timeline conserves ${debt}`)
      debt = 0
    }
    after.push({ event, debt, batch })
  }
  // Rows are in time order: those before the window, those inside it, then those after it.
  const before = after.filter(row => !inside(row.event) && row.event.end <= begin + EPSILON_MS)
  const none = { debt: 0, batch: 0 }
  const open = { begin: before.at(-1) ?? none, end: after.filter(row => before.includes(row) || inside(row.event)).at(-1) ?? none }
  const measured = after.map(row => row.event).filter(inside)
  const slices = measured.filter((event): event is Slice => event.kind === 'slice')
  const sum = (list: readonly Slice[], key: 'admitted' | 'attempted' | 'completed') => list.reduce((total, slice) => total + slice[key], 0)
  const admitted = sum(slices, 'admitted'), attempted = sum(slices, 'attempted'), completed = sum(slices, 'completed')
  const discarded = measured.reduce((total, event) => total + (event.kind === 'discard' ? event.discarded : 0), 0)
  // Conservation sees a lost entry only through a later entry of the epoch. The window is closed when the epoch's
  // timeline continues past its end, or ends inside it with a discard (a stopped epoch emits nothing more).
  const later = after.filter(row => !before.includes(row) && !inside(row.event))
  if (closure) {
    const epochEvents = after.map(row => row.event), allSlices = epochEvents.filter((event): event is Slice => event.kind === 'slice')
    const totals = { admitted: sum(allSlices, 'admitted'), attempted: sum(allSlices, 'attempted'), completed: sum(allSlices, 'completed'),
      discarded: epochEvents.reduce((n, event) => n + (event.kind === 'discard' ? event.discarded : 0), 0) }
    if (closure.schema !== 1 || closure.kind !== 'run-end' || closure.epoch !== epochs[0] || closure.rate !== rate ||
        closure.sequence !== allSlices.length || closure.batch !== batch || closure.debt !== 0 || debt !== 0 ||
        !Number.isFinite(closure.monotonicMs) || closure.monotonicMs < last || closure.monotonicMs > end + EPSILON_MS ||
        later.length || Object.entries(totals).some(([key, value]) => closure[key] !== value) || totals.attempted !== totals.completed ||
        totals.admitted !== totals.attempted + totals.discarded)
      throw new AccountingRefusal('finite epoch close contradicts ordered conserved work or is not a successful-work run-end')
  }
  if (!closure && !later.length && after.at(-1)?.event.kind !== 'discard')
    throw new AccountingRefusal('no timeline entry of the epoch follows the window end, so entries lost before it cannot be detected')

  // Continuation cost. A yield gap is measured only between two slices of one batch, both inside the window; it is
  // the wall time from one slice's end to the next slice's start, so it contains the earlier task's tail.
  const gaps: number[] = []
  slices.forEach((slice, i) => { if (i > 0 && !slice.admitted && slices[i - 1].batch === slice.batch) gaps.push(slice.start - slices[i - 1].end) })
  const tails: number[] = [], taskDurations: number[] = []
  for (const slice of slices) {
    const task = tasks.find(candidate => candidate.start <= slice.start + EPSILON_MS && candidate.end >= slice.end - EPSILON_MS)
    if (!task || task.tail === null || task.tail < task.end) throw new AccountingRefusal('a pacing slice ran outside an observed timer task, or its task tail was not recorded')
    tails.push(task.tail - slice.end); taskDurations.push(task.tail - task.start)
  }
  const total = (list: readonly number[]) => list.reduce((a, b) => a + b, 0)
  const summary = (list: readonly number[]) => list.length ? { ...frameSummary(list), totalMs: total(list) } : { frames: 0, totalMs: 0 }
  const durations = (list: readonly Slice[]) => list.map(slice => slice.end - slice.start)
  const wallSeconds = (end - begin) / 1000, simSeconds = completed * FRAME_SECONDS
  const achievedRate = simSeconds / wallSeconds
  const carried = open.begin.debt ? slices.filter(slice => slice.batch === open.begin.batch) : []
  const warm = before.map(row => row.event).filter((event): event is Slice => event.kind === 'slice')
  return {
    selectedRate: rate, achievedRate, achievedFraction: achievedRate / rate, wallSeconds, simSeconds,
    window: { begin, end, epoch: epochs[0] },
    // The epoch's executed work before the window (the warm-up), from its first admission.
    beforeWindow: { completed: sum(warm, 'completed'), simSeconds: sum(warm, 'completed') * FRAME_SECONDS, slices: warm.length,
      wallSeconds: warm.length ? (begin - warm[0].start) / 1000 : 0 },
    frames: {
      admitted, attempted, completed, discarded, unfinishedAttempts: attempted - completed,
      carriedIn: { debt: open.begin.debt, batch: open.begin.debt ? open.begin.batch : null, attemptedInWindow: sum(carried, 'attempted'), completedInWindow: sum(carried, 'completed') },
      carriedOut: { debt: open.end.debt, batch: open.end.debt ? open.end.batch : null },
    },
    batchesAdmitted: slices.filter(slice => slice.admitted).length,
    simulation: { slices: summary(durations(slices)), admissionMs: total(durations(slices.filter(slice => slice.admitted))), continuationMs: total(durations(slices.filter(slice => !slice.admitted))) },
    continuation: { slices: slices.filter(slice => !slice.admitted).length, yieldGaps: summary(gaps), taskTails: summary(tails), pacingTasks: summary(taskDurations) },
  }
}
export type WindowAccounting = ReturnType<typeof browserWindowAccounting>

/** The recorder as the run sees it (page.evaluate calls in the browser owner). */
export type WindowProbe = { begin(): Promise<number>; completed(): Promise<number>; finish(): Promise<RecordedWindow>; timeline(): Promise<TimelineEntry[]> }

/**
 * The measured window's run loop. `step` is one interaction pair (it lets wall time pass). The window ends when it
 * holds `targetFrames` completed frames, or as soon as a step adds none (an arm without a timeline, a mission end,
 * a fault or a pause): the caller then accounts what was recorded, so the declared refusal fires instead of a
 * timeout. After the window ends, `settle` lets time pass, at most `settles` times, until a timeline entry follows the
 * end: the accounting needs one to detect entries lost before it.
 */
export async function measureWindow(probe: WindowProbe, targetFrames: number, step: () => Promise<void>, settle: () => Promise<void>, settles = 20) {
  await probe.begin()
  let completed = 0, steps = 0, stalled = false
  while (completed < targetFrames) {
    await step(); steps += 1
    const now = await probe.completed()
    if (now <= completed) { stalled = true; break }
    completed = now
  }
  const recorded = await probe.finish()
  for (let i = 0; i < settles && !recorded.timeline.some(entry => entry.start >= recorded.end); i++) {
    await settle()
    recorded.timeline = await probe.timeline()
  }
  return { recorded, steps, stalled }
}

type BrowserRecord = { index: number; status: string; arm: string; configuration?: string; rate?: number;
  startedAt?: string; sessionStartedAt?: string; seed?: any; armIdentity?: Record<string, any>; result?: Record<string, any> | null }
type BrowserSession = Record<string, any> & { endRecords: (Record<string, any> & { seedValid: boolean })[] }

// Compare complete recorded identities, independent of object key order. Display prefixes never decide equality.
function canonical(value: any): any {
  if (Array.isArray(value)) return value.map(canonical)
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]))
  return value
}
const same = (a: unknown, b: unknown) => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b))
const text = (value: unknown) => typeof value === 'string' && value.length > 0
const hash = (value: unknown, length: number) => typeof value === 'string' && new RegExp(`^[0-9a-f]{${length}}$`).test(value)
const harnessComplete = (id: any) => id && hash(id.commit, 40) && hash(id.apiTree, 40) && hash(id.apiAssemblySha256, 64) && id.dirtyEntries === 0
const armComplete = (id: any) => id && ['commit', 'srcTree', 'lockBlob', 'viteBlob'].every(key => hash(id[key], 40)) && hash(id.distSha256, 64) && Number.isSafeInteger(id.distFiles) && id.distFiles > 0

/**
 * Revision-2 levels require an attributable run and complete consistent start/end receipts. An explicit session
 * link is emitted by new runs; older runs may bind only through one unambiguous recorded time interval. Missing
 * historical evidence stays NOT_QUALIFIED, never supplied from the reporting checkout's current protocol.
 */
export function browserQualification(_protocol: unknown, records: readonly BrowserRecord[], sessions: readonly BrowserSession[] = []) {
  const accepted: (BrowserRecord & { browserIdentity: Record<string, any> })[] = []
  const acceptedRecords = new Set<BrowserRecord>()
  const refusals = new Map<BrowserRecord, string>()
  for (const record of records) {
    if (record.status !== 'passed' || record.result?.schema !== BROWSER_RUN_SCHEMA) continue
    const at = Date.parse(record.startedAt ?? '')
    const matches = sessions.filter(session => record.sessionStartedAt !== undefined
      ? session.startedAt === record.sessionStartedAt
      : Number.isFinite(at) && at >= Date.parse(session.startedAt) && session.endRecords.some(end => at <= Date.parse(end.endedAt)))
    let reason: string | null = null
    const session = matches[0], end = session?.endRecords[0], result = record.result!
    if (matches.length !== 1) reason = 'the run has no unambiguous recorded browser session'
    else if (session.endRecords.length !== 1) reason = 'the session has no unique closing receipt'
    else if (!Number.isFinite(at) || !Number.isFinite(Date.parse(session.startedAt)) || !Number.isFinite(Date.parse(end.endedAt)) || at < Date.parse(session.startedAt) || at > Date.parse(end.endedAt)) reason = 'the run is outside its recorded session span'
    else if (session.dirtyOverride !== false || !harnessComplete(session.harness) || !harnessComplete(end.harnessAtEnd) || !same(session.harness, end.harnessAtEnd)) reason = 'the harness identity is missing, dirty or changed during the session'
    else if (!same(session.harness, end.harness) || !same(session.protocol, end.protocol) || !same(session.arms, end.arms) || !same(session.environment, end.environment)) reason = 'the closing receipt changes the declared harness, protocol, arms or environment'
    else if (!text(session.protocol?.schema) || session.protocol?.browser?.accounting?.revision !== 2 || session.protocol?.browser?.accounting?.schema !== BROWSER_RUN_SCHEMA || !text(session.protocol?.browser?.scenario) || !Number.isSafeInteger(session.protocol?.statistics?.iterations) || session.protocol.statistics.iterations < 1 || !Number.isSafeInteger(session.protocol?.statistics?.seed) || !(session.protocol?.statistics?.targetHalfWidthPp > 0) || !Number.isSafeInteger(session.protocol?.caps?.browserRunsPerCell) || session.protocol.caps.browserRunsPerCell < 1) reason = 'the session protocol or scoring identity is incomplete or unsupported'
    else if (!session.environment || !['node', 'v8', 'playwright'].every(key => text(session.environment[key])) || !['platform', 'release', 'arch'].every(key => text(session.environment.os?.[key])) || !text(session.environment.cpu?.model) || !Number.isSafeInteger(session.environment.cpu?.logicalCores) || session.environment.cpu.logicalCores < 1 || !(session.environment.memoryGiB > 0)) reason = 'the recorded runtime environment is incomplete'
    else if (!Array.isArray(session.arms) || session.arms.length !== 1 || session.arms[0].arm !== record.arm || !armComplete(record.armIdentity) || !same(session.arms[0], { arm: record.arm, ...record.armIdentity })) reason = 'the run does not carry its session\'s complete arm identity'
    else if (!same(end.servedAtEnd, { distSha256: record.armIdentity!.distSha256, distFiles: record.armIdentity!.distFiles })) reason = 'the served client identity is missing or changed at session end'
    else if (!end.seedValid || !same(record.seed, end.seed)) reason = 'the retained seed receipt is missing, changed or disagrees with the run'
    else if (!Number.isSafeInteger(record.rate) || record.rate! < 1 || !text(record.configuration) || result.accountingRevision !== 2 || result.scenario !== session.protocol.browser.scenario || result.rate !== record.rate || result.configuration !== record.configuration || !Array.isArray(session.cells) || !session.cells.some((cell: any) => cell?.rate === record.rate && cell?.configuration === record.configuration)) reason = 'the result does not match its recorded scenario, configuration or rate'
    else if (!text(result.browser?.version) || !text(result.browser?.renderer) || typeof result.browser?.headless !== 'boolean' || !['width', 'height', 'deviceScaleFactor'].every(key => Number.isFinite(result.browser?.viewport?.[key]) && result.browser.viewport[key] > 0) || !same(result.browser.viewport, session.protocol.browser.viewport)) reason = 'the measured browser runtime identity is incomplete or contradicts its protocol'
    if (reason) { refusals.set(record, reason); continue }
    acceptedRecords.add(record)
    accepted.push({ ...record, browserIdentity: canonical({ arm: record.arm, armIdentity: record.armIdentity,
      scenario: result.scenario, protocol: session.protocol, harness: session.harness, environment: session.environment, runtime: result.browser }) })
  }
  return {
    accountingIssue: '#1557', policyRevision: 2, supportedAccountingSchemas: [BROWSER_RUN_SCHEMA],
    accepted,
    notQualified: records.filter(record => !acceptedRecords.has(record)).map(record => ({
      index: record.index, status: record.status, arm: record.arm, commit: record.armIdentity?.commit ?? null,
      configuration: record.configuration ?? null, rate: record.rate ?? null, accountingSchema: record.result?.schema ?? null,
      qualification: 'NOT_QUALIFIED' as const,
      compatibility: record.result?.schema === HISTORICAL_BROWSER_RUN_SCHEMA ? 'historical-interval-inference' : record.status !== 'passed' ? 'failed-run' : refusals.has(record) ? 'session-evidence' : 'unsupported-or-missing',
      ...(refusals.has(record) ? { reason: refusals.get(record) } : {}),
    })),
  }
}

/**
 * The page-side recorder, installed with context.addInitScript(installBrowserRecorder, names) before the app loads.
 * Serialized into the page, so it references nothing outside itself. It keeps the whole pacing timeline (conservation
 * needs every entry from the epoch's first admission) and, while measuring, every timer task with the end of the
 * microtask queued after it (the tail), rAF intervals, long tasks, Long Animation Frames and Event Timing entries.
 */
export function installBrowserRecorder(names: { slice: string; discard: string; clock?: string; close?: string; successFrame?: string; frozen?: string }) {
  if (window !== window.top) return
  type Entry = { kind: 'slice' | 'discard'; start: number; end: number; detail: Record<string, unknown> }
  const state = {
    w3: false, frames: [] as Record<string, any>[], frozen: [] as Record<string, any>[], availability:{} as Record<string,string>,
    finite: null as null | { target: number; error: string | null; ended: boolean }, closes: [] as Record<string, any>[],
    measuring: false, begin: 0, end: 0, timeline: [] as Entry[], tasks: [] as { start: number; end: number; tail: number | null }[],
    lastFrame: -1, rafIntervals: [] as number[], longTasks: [] as { start: number; duration: number }[],
    loaf: [] as { start: number; duration: number; blocking: number }[],
    events: [] as { start: number; name: string; duration: number; processing: number; delay: number; interaction: number; targetKey: string | null; targetIndex: number }[],
    clocks: [] as Record<string, any>[], boundaries: [] as Record<string, any>[],
    inputs: [] as Record<string, unknown>[],
  }
  const take = (entry: PerformanceEntry) => {
    const kind = entry.name === names.slice ? 'slice' : entry.name === names.discard ? 'discard' : null
    if (kind) state.timeline.push({ kind, start: entry.startTime, end: entry.startTime + entry.duration, detail: (entry as PerformanceMeasure).detail as Record<string, unknown> })
    if (entry.name === names.close) state.closes.push((entry as PerformanceMeasure).detail)
    if (entry.name === names.clock) state.clocks.push((entry as PerformanceMeasure).detail)
    if (entry.name === names.successFrame) state.frames.push((entry as PerformanceMeasure).detail)
    if (entry.name === names.frozen) state.frozen.push((entry as PerformanceMeasure).detail)
  }
  const timeline = new PerformanceObserver(list => list.getEntries().forEach(take))
  timeline.observe({ type: 'measure', buffered: true })
  const nativeTimeout = window.setTimeout.bind(window)
  window.setTimeout = ((handler: TimerHandler, timeout?: number, ...args: unknown[]) => {
    if (typeof handler !== 'function') return nativeTimeout(handler, timeout, ...args)
    return nativeTimeout((...callArgs: unknown[]) => {
      if (!state.measuring && !state.finite) { (handler as (...a: unknown[]) => void)(...callArgs); return }
      const task = { start: performance.now(), end: 0, tail: null as number | null }
      if (state.measuring) state.tasks.push(task)
      try { (handler as (...a: unknown[]) => void)(...callArgs) } finally {
        task.end = performance.now()
        queueMicrotask(() => { task.tail = performance.now(); if (state.finite) finiteBoundary() })
      }
    }, timeout, ...args)
  }) as typeof window.setTimeout
  const nativeRaf = window.requestAnimationFrame.bind(window)
  window.requestAnimationFrame = callback => nativeRaf(time => {
    if (state.measuring && time !== state.lastFrame) {
      if (state.lastFrame >= 0) state.rafIntervals.push(time - state.lastFrame)
      state.lastFrame = time
    }
    callback(time)
  })
  const observers: { observer: PerformanceObserver; take: (entry: PerformanceEntry) => void }[] = []
  const observe = (type: string, onEntry: (entry: PerformanceEntry) => void, extra: Record<string, unknown> = {}) => {
    try {
      const observer = new PerformanceObserver(list => list.getEntries().forEach(onEntry))
      observer.observe({ type, buffered: true, ...extra } as PerformanceObserverInit)
      observers.push({ observer, take: onEntry })
      state.availability[type]='SUPPORTED'
    } catch { state.availability[type]='UNAVAILABLE' }
  }
  observe('longtask', entry => state.longTasks.push({ start: entry.startTime, duration: entry.duration }))
  observe('long-animation-frame', entry => state.loaf.push({ start: entry.startTime, duration: entry.duration, blocking: (entry as unknown as { blockingDuration: number }).blockingDuration }))
  observe('event', entry => {
    const timing = entry as PerformanceEventTiming
    state.events.push({ start: timing.startTime, name: timing.name, duration: timing.duration, processing: timing.processingEnd - timing.processingStart,
      delay: timing.processingStart - timing.startTime, interaction: (timing as unknown as { interactionId: number }).interactionId,
      targetKey: timing.target instanceof Element ? timing.target.closest('[data-fn]')?.getAttribute('data-fn') ?? null : null,
      targetIndex: timing.target instanceof Element ? Array.from(document.querySelectorAll(state.w3 ? '[data-fn]' : '[data-fn="BRT"]')).indexOf(timing.target.closest('[data-fn]')!) : -1 })
  }, { durationThreshold: 16 })
  // Event Timing and other observers may have queued records before their callback is delivered. Drain all of them.
  const flush = () => { timeline.takeRecords().forEach(take); observers.forEach(({ observer, take }) => observer.takeRecords().forEach(take)) }
  const boundary = (label: string) => {
    flush()
    const receipt = { label, utcMs: Date.now(), monotonicMs: performance.now(), timeOrigin: performance.timeOrigin,
      // A direct sample from the most recently completed slice, with its actual observation time retained. It is
      // not relabelled as an observation taken at this later page-task boundary.
      trace: state.clocks.at(-1) ?? null }
    state.boundaries.push(receipt)
    return receipt
  }
  const finiteBoundary = () => {
    const finite = state.finite
    if (!finite || finite.ended) return
    flush()
    const clock = state.clocks.at(-1), done = clock?.exit?.completed ?? 0
    if (!state.measuring) {
      if (state.closes.length) finite.error = 'epoch ended before the scored window'
      else if (done > finite.target) finite.error = 'warmup overshot its exact completed-frame boundary'
      else if (done === finite.target) {
        if (!clock || clock.exit.unitClockMs - state.clocks[0].entry.unitClockMs !== 64000) finite.error = 'warmup direct clock does not span 64000ms'
        else {
          const warm = boundary('warmupEnd')
          // Both labels name this one real between-task observation, not two asynchronously sampled instants.
          state.boundaries.push({ ...warm, label: 'scoredStart' })
          state.begin = warm.monotonicMs; state.measuring = true; state.tasks = []; state.rafIntervals = []; state.lastFrame = -1
        }
      }
    }
    if (state.closes.length && state.measuring) {
      state.end = boundary('scoredEnd').monotonicMs; state.measuring = false; finite.ended = true
    }
    if (finite.error) finite.ended = true
  }
  document.addEventListener('click', event => {
    const key = event.target instanceof Element ? event.target.closest('[data-fn]') : null
    if (!key || (!state.w3 && key.getAttribute('data-fn') !== 'BRT')) return
    const pending = state.inputs.at(-1)
    if (!pending || pending.eventTime !== undefined) return
    Object.assign(pending, { utcMs: Date.now(), observedAt: performance.now(), eventTime: event.timeStamp,
      trusted: event.isTrusted, key: key.getAttribute('data-fn'), target: Array.from(document.querySelectorAll(state.w3 ? '[data-fn]' : '[data-fn="BRT"]')).indexOf(key),
      before: state.w3 ? key.closest('.fmsCdu')?.querySelector('.fmsCduScreen')?.getAttribute('aria-label') ?? null : key.closest('.fmsCdu')?.getAttribute('style') ?? null,
      ...(state.w3 ? { completedAtDispatch: state.frames.at(-1)?.ordinal ?? 0 } : {}) })
  }, true)
  ;(window as unknown as { __fmsPerf: unknown }).__fmsPerf = {
    boundary,
    armW3() {
      state.w3 = true; state.measuring = true; state.tasks = []; state.frames = []; state.frozen = []
      state.timeline = []; state.clocks = []; state.closes = []; state.inputs = []; state.events = []; state.boundaries = []
    },
    w3Snapshot() {
      flush()
      return { timeline: state.timeline, clocks: state.clocks, closes: state.closes, frames: state.frames, frozen: state.frozen,
        tasks: state.tasks, inputs: state.inputs, events: state.events, longTasks: state.longTasks, loaf: state.loaf,availability:state.availability }
    },
    armFinite() {
      // Called before the public Run action: retain only the fresh scenario's epoch, not an earlier Fly.
      state.timeline = []; state.clocks = []; state.closes = []; state.boundaries = []; state.inputs = []
      state.finite = { target: 256, error: null, ended: false }
    },
    get finiteStatus() { flush(); return { started: state.begin > 0, ended: state.finite?.ended ?? false, error: state.finite?.error ?? null } },
    keyDispatch(expectedKey?: string) { state.inputs.push({ dispatchStart: performance.now(), ...(state.w3 ? { expectedKey } : {}) }) },
    begin() { const receipt = boundary('scoredStart'); state.measuring = true; state.begin = receipt.monotonicMs; state.tasks = []; state.rafIntervals = []; state.lastFrame = -1; return state.begin },
    keyOutcome() {
      const press = state.inputs.at(-1)
      if (press) {
        press.dispatchEnd = performance.now()
        const key = Array.from(document.querySelectorAll(state.w3 ? '[data-fn]' : '[data-fn="BRT"]'))[Number(press.target)]
        press.after = state.w3 ? key?.closest('.fmsCdu')?.querySelector('.fmsCduScreen')?.getAttribute('aria-label') ?? null : key?.closest('.fmsCdu')?.getAttribute('style') ?? null
        if(state.w3) press.outcomeObservedAt=performance.now()
      }
    },
    /** Completed frames of slices observed since begin: the run loop's progress, not the accounting. */
    get completed() { flush(); return state.timeline.filter(entry => entry.kind === 'slice' && entry.start >= state.begin).reduce((total, entry) => total + Number(entry.detail.completed), 0) },
    /** The whole timeline so far; it keeps recording after finish(), so the entry that closes the window arrives. */
    get timeline() { flush(); return state.timeline },
    drain() { flush(); return state.events.filter(entry => entry.start >= state.begin && entry.start <= state.end) },
    finish() {
      // The window ends here, between tasks; every measure queued before it is taken now.
      if (!state.finite) { state.end = boundary('scoredEnd').monotonicMs; state.measuring = false }
      else if (state.finite.error || !state.finite.ended || !state.begin) {
        state.finite.error ??= 'finite epoch has not closed'
        if (!state.end) state.end = boundary('scoredEnd').monotonicMs
        state.measuring = false
      }
      flush()
      const inside = <T extends { start: number }>(list: T[]) => list.filter(entry => entry.start >= state.begin && entry.start <= state.end)
      return { begin: state.begin, end: state.end, timeline: state.timeline, tasks: state.tasks, rafIntervals: state.rafIntervals,
        longTasks: inside(state.longTasks).map(entry => entry.duration), loaf: inside(state.loaf).map(({ duration, blocking }) => ({ duration, blocking })),
        events: inside(state.events), capture: { boundaries: state.boundaries, clocks: state.clocks, inputs: state.inputs }, ...(state.finite ? { closes: state.closes } : {}) }
    },
  }
}
/** What the recorder's finish() returns. */
export type RecordedWindow = { closes?: Record<string, any>[]; begin: number; end: number; timeline: TimelineEntry[]; tasks: TimerTask[]; rafIntervals: number[]; longTasks: number[];
  loaf: { duration: number; blocking: number }[]; events: { start?: number; targetKey?: string | null; targetIndex?: number; name: string; duration: number; processing: number; delay: number; interaction: number }[];
  capture?: { boundaries: Record<string, any>[]; clocks: Record<string, any>[]; inputs: Record<string, any>[] } }

/** Cross-check direct observations against conserved work; missing evidence is never filled from rate x callbacks. */
export function captureEvidence(recorded: RecordedWindow, accounting: WindowAccounting | null) {
  const reasons: string[] = [], capture = recorded.capture
  const finite = (value: unknown) => typeof value === 'number' && Number.isFinite(value)
  if (!capture || !accounting) return { boundaryStatus: 'INCOMPLETE', eventTimingStatus: 'INCOMPLETE', reasons: ['no direct capture or conserved accounting'] }
  const { clocks, boundaries, inputs } = capture
  const slices = recorded.timeline.filter(entry => entry.kind === 'slice' && entry.detail.epoch === accounting.window.epoch)
  // finish() snapshots clocks before the later conservation-closing timeline entry is collected. Every slice
  // through the scored end still needs its own direct observation; a later closing slice need not have one.
  if (clocks.length > slices.length || clocks.filter(clock => clock.exit?.monotonicMs <= recorded.end + EPSILON_MS).length !==
      slices.filter(slice => slice.end <= recorded.end + EPSILON_MS).length) reasons.push('direct observations do not cover the conserved pacing slices')
  let completed = 0
  let prior: any = null
  for (const [index, clock] of clocks.entries()) {
    const { entry, exit } = clock
    const slice = slices[index]
    if (!slice || !entry || !exit || clock.epoch !== accounting.window.epoch || clock.rate !== accounting.selectedRate || clock.batch !== slice.detail.batch ||
        entry.completed !== completed || exit.completed - entry.completed !== slice.detail.completed ||
        entry.monotonicMs < slice.start - EPSILON_MS || entry.monotonicMs > slice.end + EPSILON_MS || exit.monotonicMs < slice.end - EPSILON_MS)
      reasons.push('direct clock identity or completed work contradicts its conserved pacing slice')
    completed += Number(slice?.detail.completed ?? 0)
    if (clock.schema !== 1 || clock.sequence !== (prior?.sequence ?? 0) + 1 || (prior && clock.epoch !== prior.epoch) ||
      ![entry?.utcMs, entry?.monotonicMs, entry?.timeOrigin, entry?.kernelTime, entry?.unitClockMs, entry?.completed,
        exit?.utcMs, exit?.monotonicMs, exit?.timeOrigin, exit?.kernelTime, exit?.unitClockMs, exit?.completed].every(finite) ||
      exit.monotonicMs < entry.monotonicMs || exit.completed < entry.completed ||
      exit.kernelTime - entry.kernelTime !== (exit.completed - entry.completed) * 2_500_000 ||
      exit.unitClockMs - entry.unitClockMs !== (exit.completed - entry.completed) * 250 ||
      entry.timeOrigin !== exit.timeOrigin || (prior && (entry.kernelTime !== prior.exit.kernelTime || entry.unitClockMs !== prior.exit.unitClockMs || entry.completed !== prior.exit.completed)))
      reasons.push('direct clock/work sequence is missing, discontinuous or includes incomplete work')
    prior = clock
  }
  if (!clocks.length || boundaries.length !== 3 || boundaries.map(boundary => boundary.label).join(',') !== 'warmupEnd,scoredStart,scoredEnd') reasons.push('warmup/scored boundary receipts are missing')
  for (const boundary of boundaries) {
    const latest = clocks.filter(clock => clock.exit?.monotonicMs <= boundary.monotonicMs).at(-1)
    if (![boundary.utcMs, boundary.monotonicMs, boundary.timeOrigin].every(finite) || !latest || JSON.stringify(latest) !== JSON.stringify(boundary.trace) || boundary.timeOrigin !== latest.exit.timeOrigin)
      reasons.push('page boundary is not bound to its actual latest direct observation')
  }
  const [warm, start, end] = boundaries
  if (!warm || !start || !end || warm.monotonicMs > start.monotonicMs || start.monotonicMs !== recorded.begin || end.monotonicMs !== recorded.end ||
      end.trace?.exit?.completed - start.trace?.exit?.completed !== accounting.frames.completed ||
      end.trace?.exit?.unitClockMs - start.trace?.exit?.unitClockMs !== accounting.simSeconds * 1000)
    reasons.push('boundary completed work contradicts conserved timeline')
  const eventReasons: string[] = []
  if (!inputs.length) eventReasons.push('no native BRT input observed')
  const interactions = inputs.map((input): Record<string, any> => ({ ...input, entries: recorded.events.filter(event => event.name === 'click' && event.targetKey === 'BRT' && event.targetIndex === input.target && event.interaction > 0 &&
    finite(event.start) && event.start! >= input.dispatchStart && event.start! <= input.dispatchEnd) }))
  for (const input of interactions) if (!input.trusted || input.key !== 'BRT' || input.target !== 0 || !finite(input.eventTime) ||
      !finite(input.dispatchStart) || !finite(input.dispatchEnd) || input.eventTime < input.dispatchStart || input.eventTime > input.dispatchEnd ||
      typeof input.before !== 'string' || typeof input.after !== 'string' || input.entries.length !== 1)
    eventReasons.push('native key, observed display effect or attributable EventTiming entry is missing')
  return { boundaryStatus: reasons.length ? 'INCOMPLETE' : 'OBSERVED', eventTimingStatus: eventReasons.length ? 'INCOMPLETE' : 'OBSERVED',
    reasons: [...reasons, ...eventReasons], interactions, metric: 'EventTiming click-to-next-paint proxy; exact CDU pixel repaint unproven' }
}


export type FiniteMissionIdentity = { scenario: string; digest: string; startUtc: string; /** Input profile ID; the public report names its complete resolved identity below. */ profile: string; profileSummary: string; steps: number; statuses: ('DONE' | 'PASS')[];
  maxSimSeconds: number; gps: { model: string; constellation: number; receivers: number[] } }
/** Admission of the public downloaded report, separately from the driver's non-PASS terminal observation. */
export function finiteMissionEvidence(recorded: RecordedWindow, accounting: WindowAccounting, report: string, expected: FiniteMissionIdentity) {
  const fail = (reason: string): never => { throw new AccountingRefusal(reason) }
  const close = recorded.closes?.length === 1 ? recorded.closes[0] : fail('finite mission has no unique actual epoch close')
  const clocks = recorded.capture?.clocks ?? [], first = clocks[0], last = clocks.at(-1)
  const boundaries = recorded.capture?.boundaries ?? [], [warm, start, end] = boundaries
  const observation = captureEvidence(recorded, accounting)
  if (observation.boundaryStatus !== 'OBSERVED') fail('finite direct clock/boundary evidence is incomplete')
  if (close.kind !== 'run-end' || !last || !same(close.lastClock, last) || close.sequence !== last.sequence ||
      close.monotonicMs < last.exit.monotonicMs || close.monotonicMs > recorded.end || close.timeOrigin !== last.exit.timeOrigin ||
      !Number.isFinite(close.utcMs) || close.completed !== last.exit.completed) fail('finite close does not bind the last actual clock')
  if (!first || first.entry.completed !== 0 || first.entry.unitClockMs !== Date.parse(expected.startUtc) ||
      accounting.beforeWindow.completed !== 256 || warm?.trace?.exit?.completed !== 256 ||
      warm.monotonicMs !== start?.monotonicMs || start?.trace?.exit?.unitClockMs - first.entry.unitClockMs !== 64000 ||
      end?.trace?.exit?.completed !== close.completed || accounting.frames.completed <= 0 || accounting.frames.unfinishedAttempts !== 0 ||
      accounting.frames.carriedOut.debt !== 0 || close.completed !== 256 + accounting.frames.completed)
    fail('finite warmup or scored work contradicts the actual frame boundary')
  const started = report.match(/^- Started: (\S+); ended after (?:(\d+) min(?: (\d+(?:\.\d+)?) s)?|(\d+(?:\.\d+)?) s) of simulated time$/m)
  const endedSeconds = started ? Number(started[2] ?? 0) * 60 + Number(started[3] ?? started[4] ?? 0) : NaN
  const rows = [...report.matchAll(/^\| (\d+) \| .* \| ([^|]*) \| (DONE|PASS) \| .* \|$/gm)]
  const lines = report.split('\n')
  const unique = (prefix: string, value: string) => lines.filter(line => line.startsWith(prefix)).length === 1 && lines.includes(value)
  const gps = `- GPS: model ${expected.gps.model}; constellation seed ${expected.gps.constellation}; receiver seeds ${expected.gps.receivers.join(' and ')};`
  if (!unique('**Result: ', '**Result: PASS**') ||
      !unique('- Scenario: ', `- Scenario: ${expected.scenario}, ${expected.digest}`) || started?.[1] !== expected.startUtc || lines.filter(line => line.startsWith('- Started: ')).length !== 1 ||
      typeof expected.profileSummary !== 'string' || expected.profileSummary.split(' ')[0] !== expected.profile ||
      !unique('- Aircraft profile: ', `- Aircraft profile: ${expected.profileSummary}`) || lines.filter(line => line.startsWith('- GPS: ')).length !== 1 || !lines.some(line => line.startsWith(gps)) ||
      expected.statuses.length !== expected.steps || rows.length !== expected.steps || rows.some((row, index) => Number(row[1]) !== index + 1 || row[3] !== expected.statuses[index]) || !rows.some(row => row[3] === 'PASS') ||
      !Number.isFinite(endedSeconds) || endedSeconds <= 64 || endedSeconds > expected.maxSimSeconds || endedSeconds * 4 !== close.completed)
    fail('public finite mission report is not complete PASS with the exact input, UTC, profile, seeds and end frame')
  return { status: 'OBSERVED', inference: 'DISABLED', qualification: 'NOT_CALIBRATED', totalFrames: close.completed,
    scoredFrames: accounting.frames.completed, endedSimSeconds: endedSeconds, close, expected, report }
}

/** W3 is a separate estimator. It never changes the between-task W2/finite readers above. */
export const W3_SCHEMA = 'aerolink.fms-perf-w3-prefix.v1'
export type W3Observation = { identity: Record<string, unknown>; setupReport: string; timeline: TimelineEntry[];
  frames: Record<string, any>[]; clocks: Record<string, any>[]; closes: Record<string, any>[]; tasks: TimerTask[];
  frozen: Record<string, any>[]; inputs: Record<string, any>[]; events: Record<string, any>[];
  longTasks: { start: number; duration: number }[]; loaf: { start: number; duration: number; blocking: number }[];availability:Record<string,string> }
export function w3SetupEvidence(report: string, expected: FiniteMissionIdentity) {
  const lines = report.split('\n'), unique = (prefix: string, line: string) => lines.filter(value => value.startsWith(prefix)).length === 1 && lines.includes(line)
  const rows = [...report.matchAll(/^\| (\d+) \| .* \| ([^|]*) \| (DONE|PASS) \| .* \|$/gm)]
  if (!unique('**Result: ', '**Result: PASS**') || !unique('- Scenario: ', `- Scenario: ${expected.scenario}, ${expected.digest}`) ||
      !unique('- Started: ', `- Started: ${expected.startUtc}; ended after 0 s of simulated time`) ||
      !unique('- Aircraft profile: ', `- Aircraft profile: ${expected.profileSummary}`) || expected.profileSummary.split(' ')[0] !== expected.profile ||
      lines.filter(line => line.startsWith('- GPS: ')).length !== 1 || !lines.some(line => line.startsWith(`- GPS: model ${expected.gps.model}; constellation seed ${expected.gps.constellation}; receiver seeds ${expected.gps.receivers.join(' and ')};`)) ||
      rows.length !== expected.steps || rows.some((row, i) => Number(row[1]) !== i + 1 || row[3] !== expected.statuses[i]) || !rows.some(row => row[3] === 'PASS'))
    throw new AccountingRefusal('W3 requires the exact complete public t0 setup PASS; this is not continuation END')
  return { status: 'OBSERVED_SETUP_ONLY', endedAfter: 0, expected, report }
}

/** Fixed successful-frame prefix plus predeclared WHOLE timer/slice cohorts; crossings are never prorated. */
export function w3PrefixAccounting(raw: W3Observation, expected: FiniteMissionIdentity, identity: Record<string, unknown>) {
  const refuse = (reason: string): never => { throw new AccountingRefusal(`W3 ${reason}`) }
  const id=identity as Record<string,any>
  if (!same(raw.identity, identity) || !hash(id.original?.commit,40) || !hash(id.overlaySha256,64) || !hash(id.effectiveSourceSha256,64) ||
      !hash(id.harness?.commit,40) || id.build?.sourceSha256!==id.effectiveSourceSha256 || id.build?.harnessCommit!==id.harness.commit || !id.manifestSha256 || !id.build?.dist?.length)
    refuse('arm/overlay/input identity changed or missing')
  const setup = w3SetupEvidence(raw.setupReport, expected), frames = raw.frames, close = raw.closes.length === 1 ? raw.closes[0] : refuse('unique epoch close missing')
  if (frames.length < 7200) refuse('successful prefix is incomplete')
  const initial = frames[0].entry, epoch = frames[0].epoch, rate = frames[0].rate
  const configuration=typeof id.configuration==='string'?id.configuration.match(/^view-(on|off)-svs-(on|off)$/):null
  if(id.rate!==rate || !configuration)refuse('actual rate/configuration disagrees with the selected paired cell')
  if (!initial || ![initial.utcMs,initial.monotonicMs,initial.timeOrigin].every(Number.isFinite) || initial.completed !== 0 || initial.kernelTime !== 0 || initial.unitClockMs !== Date.parse(expected.startUtc)) refuse('first actual Fly ENTRY is not F0 at the fixed UTC')
  const events = raw.timeline.map(row => parse(row, rate)); let debt = 0, batch = 0, completed = 0, admitted = 0, attempted = 0, discarded = 0, sequence = 0, previousEnd = -Infinity
  const slices: { event: Slice; first: number; last: number }[] = []
  for (const event of events) {
    if (event.epoch !== epoch || event.end < previousEnd) refuse('epoch/order mismatch')
    previousEnd = event.end
    if (event.kind === 'discard') {
      if (event.batch !== batch || event.discarded !== debt) refuse('discard does not close actual debt')
      discarded += event.discarded; debt = 0; continue
    }
    if (event.admitted ? debt !== 0 || event.batch !== batch + 1 : debt === 0 || event.batch !== batch) refuse('admission/continuation is not conserved')
    if (event.admitted) batch = event.batch
    admitted += event.admitted; attempted += event.attempted; debt += event.admitted - event.attempted
    if (debt < 0 || debt !== event.remaining || event.completed !== event.attempted) refuse('missing/faulted frame or inconsistent debt')
    sequence++; const first = completed + 1; completed += event.completed
    slices.push({ event, first, last: completed })
    const clock = raw.clocks[sequence - 1]
    if (!clock || clock.sequence !== sequence || clock.epoch !== epoch || clock.batch !== batch || clock.rate !== rate ||
        clock.entry.completed !== first - 1 || clock.exit.completed !== completed || clock.exit.kernelTime - clock.entry.kernelTime !== event.completed * 2_500_000 ||
        clock.exit.unitClockMs - clock.entry.unitClockMs !== event.completed * 250 || !same(clock.entry,frames[first-1]?.entry) ||
        clock.exit.kernelTime!==frames[completed-1]?.frame.kernelTime || clock.exit.unitClockMs!==frames[completed-1]?.frame.unitClockMs || clock.exit.monotonicMs<frames[completed-1]?.frame.monotonicMs) refuse('slice/direct clock coverage disagrees')
    for (let ordinal = first; ordinal <= completed; ordinal++) {
      const f = frames[ordinal - 1], stamp = f?.frame
      if (!f || f.schema !== 1 || f.epoch !== epoch || f.rate !== rate || f.sequence !== sequence || f.batch !== batch || f.ordinal !== ordinal ||
          !stamp || stamp.completed !== ordinal || stamp.kernelTime !== ordinal * 2_500_000 || stamp.unitClockMs !== initial.unitClockMs + ordinal * 250 ||
          !Number.isFinite(stamp.utcMs) || !Number.isFinite(stamp.monotonicMs) || stamp.monotonicMs < event.start || stamp.monotonicMs > event.end ||
          !same(f.entry,clock.entry) || stamp.timeOrigin !== initial.timeOrigin || (ordinal > 1 && stamp.monotonicMs < frames[ordinal - 2].frame.monotonicMs) ||
          !Array.isArray(f.state?.unitClocksMs) || f.state.unitClocksMs.length !== 2 || f.state.unitClocksMs.some((ms: number) => ms !== stamp.unitClockMs)) refuse('successful-frame identity/clock/slice coverage disagrees')
      if (ordinal <= 7200 && f.state.sync !== 'SYNC') refuse('dual SYNC was missing or dropped within the prefix')
    }
  }
  if (completed !== frames.length || raw.clocks.length !== sequence || close.schema !== 1 || close.kind !== 'cancelled' || close.epoch !== epoch || close.rate !== rate ||
      close.sequence !== sequence || close.batch !== batch || close.admitted !== admitted || close.attempted !== attempted || close.completed !== completed || close.discarded !== discarded || close.debt !== 0 || debt !== 0 ||
      !Number.isFinite(close.monotonicMs) || close.monotonicMs < previousEnd || !same(close.lastClock, raw.clocks.at(-1))) refuse('final Pause close does not bind all real work, including zero-debt final slices')
  const begin = frames[239].frame.monotonicMs, end = frames[7199].frame.monotonicMs
  if (!(end > begin) || !frames[239].state.boundary || !frames[7199].state.boundary) refuse('actual F240/F7200 boundary observations missing')
  for(const boundary of [frames[239].state.boundary,frames[7199].state.boundary])if(boundary.outsideView!==(configuration![1]==='on'?'ready':'off') || boundary.svs!==configuration![2])refuse('actual boundary view/SVS disagrees with the selected paired cell')
  let frozenKernel=close.lastClock.exit.kernelTime,frozenClock=close.lastClock.exit.unitClockMs,frozenAt=close.monotonicMs
  for(const row of raw.frozen) {
    if(row.schema!==1 || row.completed!==1 || !Number.isFinite(row.utcMs) || !Number.isFinite(row.monotonicMs) || row.monotonicMs<frozenAt ||
      row.kernelTime!==frozenKernel+2_500_000 || row.unitClockMs!==frozenClock+250)refuse('recorded frozen-clock work is missing, reordered or contradictory')
    frozenKernel=row.kernelTime;frozenClock=row.unitClockMs;frozenAt=row.monotonicMs
  }
  const relation = (start: number, finish: number) => start >= begin && finish <= end ? 'contained' : finish < begin || start > end ? 'outside' : 'crossing'
  const exposure = (first: number, last: number) => Math.max(0, Math.min(7200,last) - Math.max(241,first) + 1)
  const timerRows: any[] = [], sliceRows: any[] = [], tails: any[] = [], gaps: any[] = []
  for (const [i,row] of slices.entries()) {
    const s = row.event, matched = raw.tasks.filter(task => task.start <= s.start && task.end >= s.end)
    if (matched.length !== 1) refuse('slice has no unique real timer handler')
    const task = matched[0]
    if (task.tail === null || !Number.isFinite(task.tail) || task.tail < task.end || task.end < task.start) refuse('timer/tail observation incomplete')
    const tail=task.tail as number
    const common = { first: row.first, last: row.last, attempted: s.attempted, completed: s.completed, scoredFrameExposure: exposure(row.first,row.last), batch:s.batch }
    timerRows.push({ ...common, start: task.start, end: task.end, durationMs: task.end-task.start, relation: relation(task.start,task.end) })
    sliceRows.push({ ...common, start: s.start, end:s.end, durationMs:s.end-s.start, relation:relation(s.start,s.end) })
    tails.push({ ...common, start:task.end, end:tail, durationMs:tail-task.end, timerPlusTailMs:tail-task.start, relation:relation(task.end,tail) })
    if(i && !s.admitted && slices[i-1].event.batch===s.batch) gaps.push({start:slices[i-1].event.end,end:s.start,durationMs:s.start-slices[i-1].event.end,relation:relation(slices[i-1].event.end,s.start)})
  }
  const cohort = (rows: any[]) => {const contained=rows.filter(row=>row.relation==='contained');return {unit:'whole-observation',raw:rows,contained:contained.length,summary:contained.length?frameSummary(contained.map(row=>row.durationMs)):null,
    representedScoredFrames:contained.reduce((n,row)=>n+row.scoredFrameExposure,0),excludedScoredFrames:6960-contained.reduce((n,row)=>n+row.scoredFrameExposure,0)}}
  const interactions = raw.inputs.map(input => {
    const title=input.expectedKey==='PROG'?'PROGRESS':input.expectedKey==='LEGS'?'RTE 1 LEGS':null
    const entries=raw.events.filter(event=>event.name==='click' && event.targetKey===input.key && event.targetIndex===input.target && Math.abs(event.start-input.eventTime)<=1)
    const outcome=input.trusted===true && input.key===input.expectedKey && title && typeof input.before==='string' && typeof input.after==='string' &&
      input.after.split('\n')[0].includes(title) && !input.before.split('\n')[0].includes(title) && Number.isFinite(input.outcomeObservedAt) ? 'CDU_OUTCOME_AFTER_PAINT_OBSERVED':'UNAVAILABLE'
    const entry=entries[0],native=entries.length===1&&entry.interaction>0&&Number.isFinite(entry.start)&&Number.isFinite(entry.duration)&&entry.duration>=16&&entry.duration%8===0&&entry.processing>=0&&entry.delay>=0
    return {...input,entries,outcome,eventTiming:outcome!=='UNAVAILABLE'&&native?'NATIVE_NEXT_PAINT_ENTRY_CORRELATED':'CENSORED_OR_UNAVAILABLE'}
  })
  return {schema:W3_SCHEMA,status:'OBSERVED',qualification:'NOT_CALIBRATED',inference:'DISABLED',setup,totalFrames:7200,warmupFrames:240,scoredFrames:6960,begin,end,
    primaryTimerHandlers:cohort(timerRows),simulationSlices:cohort(sliceRows),taskTails:tails,yieldGaps:gaps,
    boundaryCounters:{warmup:frames[239].state.boundary,end:frames[7199].state.boundary},sync:{total:7200,scored:6960,firstDrop:null},
    overrun:{completed:completed-7200,frames:frames.slice(7200),close,frozen:raw.frozen},
    availability:raw.availability,longTasks:raw.longTasks.map(row=>({...row,relation:relation(row.start,row.start+row.duration)})),loaf:raw.loaf.map(row=>({...row,relation:relation(row.start,row.start+row.duration)})),
    interactions,eventTiming:{thresholdMs:16,roundingMs:8,missing:'CENSORED_OR_UNAVAILABLE_NOT_ZERO',exactLatencyP95:'UNAVAILABLE_UNTIL_COMPLETE_CENSORING_ADMISSION'},
    heap:{retainedF7200:'UNAVAILABLE',laterPostGc:'CONTEXTUAL_ONLY',clockCoverage:'FROZEN_TIMELINE_ONLY_NO_TERMINAL_CLOCK_GETTER',reason:'ordinary Pause freezes flight but does not stop existing clock advances'},workerBacklog:'NOT_APPLICABLE_BEFORE_I3'}
}
