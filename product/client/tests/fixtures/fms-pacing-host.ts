import { PACING_TIMELINE, type TickSchedulingHost } from '../../src/fmsCdu/benchPacing'

/** Monotonic platform ports; production alone admits work, owns debt/termination and chooses task deadlines. */
export class SchedulingHost implements TickSchedulingHost {
  wall = 0
  next = 1
  tasks = new Map<number, { due: number, callback: () => void }>()
  delays: number[] = []
  performance: TickSchedulingHost['performance'] = { now: () => this.wall }
  setTimeout(callback: () => void, milliseconds: number) {
    const id = this.next++
    this.delays.push(milliseconds)
    this.tasks.set(id, { due: this.wall + milliseconds, callback })
    return id
  }
  clearTimeout(id: number) { this.tasks.delete(id) }
  runNext() {
    const first = [...this.tasks.entries()].sort((a, b) => a[1].due - b[1].due || a[0] - b[0])[0]
    if (!first) return false
    this.tasks.delete(first[0]); this.wall = Math.max(this.wall, first[1].due); first[1].callback()
    return true
  }
}

/**
 * A host with User Timing: it keeps the measures the pacing timeline (#1557) makes, in the shape the browser recorder
 * keeps them, and every timer task: `postMs` of synchronous work after the callback's slice, then `tailMs` of microtask work
 * after the callback, both in the same task.
 */
export class TimelineHost extends SchedulingHost {
  measures: { name: string, start: number, end: number, detail: Record<string, unknown> }[] = []
  cleared: string[] = []
  timerTasks: { start: number, end: number, tail: number }[] = []
  constructor(readonly tailMs = 0, readonly postMs = 0) {
    super()
    this.performance = {
      now: () => this.wall,
      measure: (name: string, options?: string | PerformanceMeasureOptions) => {
        const { start, end, detail } = options as { start: number, end: number, detail: Record<string, unknown> }
        this.measures.push({ name, start, end, detail: structuredClone(detail) })
        return undefined as unknown as PerformanceMeasure
      },
      clearMeasures: (name?: string) => { this.cleared.push(name ?? '') },
    }
  }
  override runNext() {
    const first = [...this.tasks.values()].sort((a, b) => a.due - b.due)[0]
    if (!first) return false
    const start = Math.max(this.wall, first.due)
    super.runNext()
    this.wall += this.postMs
    const end = this.wall
    this.wall += this.tailMs
    this.timerTasks.push({ start, end, tail: this.wall })
    return true
  }
  /** The recorded timeline as the browser recorder returns it. */
  timeline() {
    return this.measures.filter(({ name }) => name === PACING_TIMELINE.slice || name === PACING_TIMELINE.discard)
      .map(({ name, start, end, detail }) => ({ kind: name === PACING_TIMELINE.slice ? 'slice' as const : 'discard' as const, start, end, detail }))
  }
  entries() { return this.timeline().map(({ kind, start, end, detail }) => `${kind}@${start}-${end} ${JSON.stringify(detail)}`) }
}
