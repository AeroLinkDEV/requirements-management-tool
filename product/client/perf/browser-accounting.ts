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
export type WindowInput = { rate: number; begin: number; end: number; timeline: readonly TimelineEntry[]; tasks: readonly TimerTask[] }

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
export function browserWindowAccounting({ rate, begin, end, timeline, tasks }: WindowInput) {
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
  if (!later.length && after.at(-1)?.event.kind !== 'discard')
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

type BrowserRecord ={ index: number; status: string; arm: string; configuration?: string; rate?: number; armIdentity?: { commit?: string }; result?: Record<string, unknown> | null }

/** Passed records under accounting revision 2 are reported; every other browser record is retained as NOT_QUALIFIED. */
export function browserQualification(protocol: { browser?: { accounting?: { revision?: number } } }, records: readonly BrowserRecord[]) {
  const accepted = records.filter(record => record.status === 'passed' && record.result?.schema === BROWSER_RUN_SCHEMA)
  return {
    accountingIssue: '#1557', policyRevision: protocol.browser?.accounting?.revision ?? null, supportedAccountingSchemas: [BROWSER_RUN_SCHEMA],
    accepted,
    notQualified: records.filter(record => !accepted.includes(record)).map(record => ({
      index: record.index, status: record.status, arm: record.arm, commit: record.armIdentity?.commit ?? null,
      configuration: record.configuration ?? null, rate: record.rate ?? null, accountingSchema: record.result?.schema ?? null,
      qualification: 'NOT_QUALIFIED' as const,
      compatibility: record.result?.schema === HISTORICAL_BROWSER_RUN_SCHEMA ? 'historical-interval-inference' : record.status !== 'passed' ? 'failed-run' : 'unsupported-or-missing',
    })),
  }
}

/**
 * The page-side recorder, installed with context.addInitScript(installBrowserRecorder, names) before the app loads.
 * Serialized into the page, so it references nothing outside itself. It keeps the whole pacing timeline (conservation
 * needs every entry from the epoch's first admission) and, while measuring, every timer task with the end of the
 * microtask queued after it (the tail), rAF intervals, long tasks, Long Animation Frames and Event Timing entries.
 */
export function installBrowserRecorder(names: { slice: string; discard: string }) {
  if (window !== window.top) return
  type Entry = { kind: 'slice' | 'discard'; start: number; end: number; detail: Record<string, unknown> }
  const state = {
    measuring: false, begin: 0, end: 0, timeline: [] as Entry[], tasks: [] as { start: number; end: number; tail: number | null }[],
    lastFrame: -1, rafIntervals: [] as number[], longTasks: [] as { start: number; duration: number }[],
    loaf: [] as { start: number; duration: number; blocking: number }[],
    events: [] as { start: number; name: string; duration: number; processing: number; delay: number; interaction: number }[],
  }
  const take = (entry: PerformanceEntry) => {
    const kind = entry.name === names.slice ? 'slice' : entry.name === names.discard ? 'discard' : null
    if (kind) state.timeline.push({ kind, start: entry.startTime, end: entry.startTime + entry.duration, detail: (entry as PerformanceMeasure).detail as Record<string, unknown> })
  }
  const timeline = new PerformanceObserver(list => list.getEntries().forEach(take))
  timeline.observe({ type: 'measure', buffered: true })
  const nativeTimeout = window.setTimeout.bind(window)
  window.setTimeout = ((handler: TimerHandler, timeout?: number, ...args: unknown[]) => {
    if (typeof handler !== 'function') return nativeTimeout(handler, timeout, ...args)
    return nativeTimeout((...callArgs: unknown[]) => {
      if (!state.measuring) { (handler as (...a: unknown[]) => void)(...callArgs); return }
      const task = { start: performance.now(), end: 0, tail: null as number | null }
      state.tasks.push(task)
      try { (handler as (...a: unknown[]) => void)(...callArgs) } finally {
        task.end = performance.now()
        queueMicrotask(() => { task.tail = performance.now() })
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
  const observe = (type: string, onEntry: (entry: PerformanceEntry) => void, extra: Record<string, unknown> = {}) => {
    try { new PerformanceObserver(list => list.getEntries().forEach(onEntry)).observe({ type, buffered: true, ...extra } as PerformanceObserverInit) } catch { /* unsupported: recorded as absent */ }
  }
  observe('longtask', entry => state.longTasks.push({ start: entry.startTime, duration: entry.duration }))
  observe('long-animation-frame', entry => state.loaf.push({ start: entry.startTime, duration: entry.duration, blocking: (entry as unknown as { blockingDuration: number }).blockingDuration }))
  observe('event', entry => {
    const timing = entry as PerformanceEventTiming
    state.events.push({ start: timing.startTime, name: timing.name, duration: timing.duration, processing: timing.processingEnd - timing.processingStart,
      delay: timing.processingStart - timing.startTime, interaction: (timing as unknown as { interactionId: number }).interactionId })
  }, { durationThreshold: 16 })
  const flush = () => timeline.takeRecords().forEach(take)
  ;(window as unknown as { __fmsPerf: unknown }).__fmsPerf = {
    begin() { flush(); state.measuring = true; state.begin = performance.now(); state.tasks = []; state.rafIntervals = []; state.lastFrame = -1; return state.begin },
    /** Completed frames of slices observed since begin: the run loop's progress, not the accounting. */
    get completed() { flush(); return state.timeline.filter(entry => entry.kind === 'slice' && entry.start >= state.begin).reduce((total, entry) => total + Number(entry.detail.completed), 0) },
    /** The whole timeline so far; it keeps recording after finish(), so the entry that closes the window arrives. */
    get timeline() { flush(); return state.timeline },
    finish() {
      // The window ends here, between tasks; every measure queued before it is taken now.
      state.end = performance.now(); state.measuring = false; flush()
      const inside = <T extends { start: number }>(list: T[]) => list.filter(entry => entry.start >= state.begin && entry.start <= state.end)
      return { begin: state.begin, end: state.end, timeline: state.timeline, tasks: state.tasks, rafIntervals: state.rafIntervals,
        longTasks: inside(state.longTasks).map(entry => entry.duration), loaf: inside(state.loaf).map(({ duration, blocking }) => ({ duration, blocking })),
        events: inside(state.events).map(({ name, duration, processing, delay, interaction }) => ({ name, duration, processing, delay, interaction })) }
    },
  }
}
/** What the recorder's finish() returns. */
export type RecordedWindow = { begin: number; end: number; timeline: TimelineEntry[]; tasks: TimerTask[]; rafIntervals: number[]; longTasks: number[];
  loaf: { duration: number; blocking: number }[]; events: { name: string; duration: number; processing: number; delay: number; interaction: number }[] }
