import { expect, logicTest as test } from './isolated-client-test'
import { startTickPacing, startKernelPacing, type TickSchedulingHost, type PacingProgress } from '../src/fmsCdu/benchPacing'
import { BenchPresentation } from '../src/fmsCdu/benchPresentation'
import { ScenarioRunner, runHeadless, type Scenario } from '../src/fmsCdu/scenario'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { singleComposition, dualComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { FRAME_UNITS } from '../src/fmsCdu/kernel/time'

// Monotonic platform ports; production alone admits work, owns debt/termination and chooses task deadlines.
class SchedulingHost implements TickSchedulingHost {
  wall = 0
  next = 1
  tasks = new Map<number, { due: number, callback: () => void }>()
  delays: number[] = []
  performance = { now: () => this.wall }
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
const epoch = Date.UTC(2026, 8, 27, 14)
const noFault = (error: unknown) => { throw error }

for (const rate of [1, 4, 16, 64]) test(`${rate}x retains every real whole kernel frame across slices`, () => {
  const host = new SchedulingHost(), composed = singleComposition(epoch), kernel = new FmsKernel(composed.plant)
  const instants: number[] = []
  const stop = startKernelPacing(rate, kernel, host, { onFault: noFault,
    onInstantClose: () => { instants.push(kernel.now); host.wall += 8 } })
  host.runNext()
  expect(kernel.now).toBe(Math.min(rate, 4) * FRAME_UNITS)
  for (let i = 0; kernel.now < rate * FRAME_UNITS && i < 32; i++) host.runNext()
  expect(kernel.now).toBe(rate * FRAME_UNITS)
  expect(kernel.unitClockMs).toBe(epoch + rate * 250)
  expect(instants).toEqual(Array.from({ length: rate }, (_, i) => i * FRAME_UNITS))
  expect(host.tasks.size).toBe(1)
  stop(); expect(host.tasks.size).toBe(0)
})

test('deadline admission starts due work when its batch finishes without an extra interval or idle slice', () => {
  const host = new SchedulingHost()
  let frames = 0
  const stop = startTickPacing(64, () => { frames++; host.wall += 8; return true }, host)
  host.runNext()
  expect(frames).toBe(4)
  expect(host.delays.slice(0, 2)).toEqual([250, 0])
  for (let i = 0; i < 15; i++) host.runNext()
  expect(frames).toBe(64)
  expect(host.wall).toBe(762)
  expect(host.delays.at(-1)).toBe(0)
  host.runNext()
  expect(frames).toBe(68)
  stop()
})

test('a long hidden-host delay admits one finite batch instead of queueing missed wall deadlines', () => {
  const host = new SchedulingHost()
  let frames = 0
  const stop = startTickPacing(64, () => { frames++; host.wall += 8; return true }, host)
  host.wall = 60_000; host.runNext()
  expect(frames).toBe(4)
  expect(host.tasks.size).toBe(1)
  stop(); expect(host.tasks.size).toBe(0)
})

test('an indivisible slow frame completes once before yielding', () => {
  const host = new SchedulingHost()
  let frames = 0
  const stop = startTickPacing(4, () => { frames++; host.wall += 100; return true }, host)
  host.runNext()
  expect(frames).toBe(1)
  expect(host.tasks.size).toBe(1)
  host.runNext(); expect(frames).toBe(2)
  stop()
})

test('pause and rate/session replacement cancel debt and stale continuations without rewinding the kernel', () => {
  const host = new SchedulingHost(), composed = singleComposition(epoch), kernel = new FmsKernel(composed.plant)
  const options = { onFault: noFault, onInstantClose: () => { host.wall += 8 } }
  const stop = startKernelPacing(64, kernel, host, options)
  host.runNext()
  const stale = [...host.tasks.values()][0].callback
  stop(); stop(); stale()
  expect(host.tasks.size).toBe(0)
  expect(kernel.now).toBe(4 * FRAME_UNITS)
  const resumed = startKernelPacing(4, kernel, host, options)
  host.runNext()
  expect(kernel.now).toBe(8 * FRAME_UNITS)
  resumed()
})

const finishScenario: Scenario = { id: 'pacing-finish', title: 'Pacing finish', objective: 'Terminal boundary', maxSeconds: 10,
  steps: [{ when: { kind: 'time', seconds: 0.75 }, action: { kind: 'alert', text: 'PACING END' } },
    { when: { kind: 'time', seconds: 0.75 }, action: { kind: 'expectLamp', lamp: 'MSG', lit: true } }] }

test('the dual composition settles the placed aircraft on both units before the first scenario input at t0', () => {
  const composed = dualComposition(epoch), [fms, peer] = composed.system.computers
  const atInput: { lat: number, lon: number, utc: number }[] = []
  const press = fms.press.bind(fms)
  fms.press = key => { atInput.push({ ...peer.truePosition, utc: peer.now.getTime() }); press(key) }
  const scenario: Scenario = { id: 'settled-t0', title: 'Settled t0', objective: 'Composition start ordering',
    start: '87n-offshore-sar', maxSeconds: 1, steps: [
      { when: { kind: 'start' }, action: { kind: 'keys', keys: ['INIT_REF'] } },
      { when: { kind: 'start' }, action: { kind: 'expectAircraft', altitude: 500, heightTolerance: 1 } },
    ] }
  let settlements = 0
  const runner = new ScenarioRunner(scenario, fms, undefined, composed.system.flights[0], () => {
    settlements++; composed.settleStart()
  })
  // Ignore the setup's EXEC; the last input is the runner's first t0 action, after setup and settlement.
  const firstAction = atInput.at(-1)!
  expect(firstAction.lat).toBeGreaterThan(40)
  expect(firstAction.lat).toBeLessThan(41)
  expect(firstAction.lon).toBeGreaterThan(-73)
  expect(firstAction.lon).toBeLessThan(-72)
  expect(settlements).toBe(1)
  expect(runner.results.map(result => [result.status, result.at])).toEqual([['done', 0], ['pass', 0]])
  expect(composed.clock.ms).toBe(epoch); expect(firstAction.utc).toBe(epoch)
  expect(peer.truePosition).toEqual(fms.truePosition)
})

test('a refused start neither settles the composition nor executes its first input', () => {
  const composed = singleComposition(epoch)
  let settlements = 0
  const scenario: Scenario = { id: 'refused-t0', title: 'Refused start', objective: '',
    start: '87n-offshore-sar', maxSeconds: 1,
    steps: [{ when: { kind: 'start' }, action: { kind: 'keys', keys: ['INIT_REF'] } }] }
  const runner = new ScenarioRunner(scenario, composed.fms, undefined, null, () => { settlements++; composed.settleStart() })
  expect(runner.problems).toEqual(['start state 87n-offshore-sar: the helicopter mission needs the flight simulation (autopilot selections)'])
  expect(runner.results.map(result => result.status)).toEqual(['pending'])
  expect(settlements).toBe(0); expect(composed.clock.ms).toBe(epoch)
})

test('runHeadless observes the successful placed start once before its constructor t0 check', () => {
  const observed: { lat: number, utc: number }[] = []
  const order: string[] = []
  const original = FlightSimulator.prototype.observe
  const originalPress = ScriptedFms.prototype.press
  ScriptedFms.prototype.press = function (key) {
    if (key === 'INIT_REF') order.push('t0 input')
    return originalPress.call(this, key)
  }
  FlightSimulator.prototype.observe = function (...args) {
    const result = original.apply(this, args)
    order.push('settled')
    observed.push({ lat: this.fms.truePosition.lat, utc: this.fms.now.getTime() })
    return result
  }
  try {
    const scenario: Scenario = { id: 'headless-settled-t0', title: 'Headless start', objective: '',
      start: '87n-offshore-sar', maxSeconds: 1,
      steps: [{ when: { kind: 'start' }, action: { kind: 'keys', keys: ['INIT_REF'] } },
        { when: { kind: 'start' }, action: { kind: 'expectAircraft', altitude: 500, heightTolerance: 1 } }] }
    const { runner, fms } = runHeadless(scenario, epoch)
    expect(observed).toHaveLength(1)
    expect(observed[0].lat).toBeGreaterThan(40.67); expect(observed[0].lat).toBeLessThan(40.69)
    expect(observed[0].utc).toBe(epoch); expect(fms.now.getTime()).toBe(epoch)
    expect(order).toEqual(['settled', 't0 input'])
    expect(runner.results.map(result => [result.status, result.at])).toEqual([['done', 0], ['pass', 0]])
  } finally { FlightSimulator.prototype.observe = original; ScriptedFms.prototype.press = originalPress }
})

test('the real production driver terminates at the kernel terminal frame across task yields', () => {
  const host = new SchedulingHost(), composed = singleComposition(epoch)
  const runner = new ScenarioRunner(finishScenario, composed.fms, undefined, composed.sim), kernel = new FmsKernel(composed.plant, runner)
  let ended = 0
  startKernelPacing(64, kernel, host, { onFault: noFault, onStopped: () => { ended++ }, onInstantClose: () => { host.wall += 20 } })
  for (let i = 0; host.tasks.size && i < 10; i++) host.runNext()
  expect(kernel.now).toBe(3 * FRAME_UNITS)
  expect(runner.elapsed).toBe(0.75)
  expect(runner.results.map(result => [result.status, result.at])).toEqual([['done', 0.75], ['pass', 0.75]])
  expect(ended).toBe(1)
  expect(host.tasks.size).toBe(0)
  expect(kernel.advance(1, { haltOnRunEnd: true })).toBe(0)
  expect(kernel.now).toBe(3 * FRAME_UNITS)
  // A subsequent deliberate free-flight call is distinct from the terminated logical run.
  expect(kernel.advance(1)).toBe(1)
  expect(kernel.now).toBe(4 * FRAME_UNITS)
})

test('already-finished construction is terminal without integration and deliberate Fly remains available', () => {
  const host = new SchedulingHost(), composed = singleComposition(epoch)
  const scenario: Scenario = { id: 'prefinished', title: 'Prefinished', objective: '', maxSeconds: 1,
    steps: [{ when: { kind: 'start' }, action: { kind: 'expectAircraft', minAltitude: 0 } }] }
  const runner = new ScenarioRunner(scenario, composed.fms, undefined, composed.sim), kernel = new FmsKernel(composed.plant, runner)
  expect(runner.finished).toBe(true)
  startKernelPacing(64, kernel, host, { onFault: noFault })
  host.runNext(); expect(kernel.now).toBe(0); expect(host.tasks.size).toBe(0)
  const stop = startKernelPacing(4, kernel, host, { haltOnRunEnd: false, onFault: noFault })
  host.runNext(); expect(kernel.now).toBe(4 * FRAME_UNITS); stop()
})

test('a throwing advance clears all admitted debt, cancels future tasks and propagates the failure', () => {
  const host = new SchedulingHost(), failure = new Error('INTEGRATE failed')
  let attempts = 0
  startTickPacing(64, () => { attempts++; throw failure }, host)
  expect(() => host.runNext()).toThrow('INTEGRATE failed')
  expect(attempts).toBe(1); expect(host.tasks.size).toBe(0)
  expect(host.runNext()).toBe(false); expect(attempts).toBe(1)
})

test('achieved progress excludes the retained fault instant and explicit recovery does not replay it', () => {
  const host = new SchedulingHost(), composed = singleComposition(epoch), progress: PacingProgress[] = []
  let attempts = 0, errors = 0
  const plant = { ...composed.plant, integrate: () => {
    attempts++; host.wall += 10
    if (attempts === 2) { composed.clock.advanceFrame(); throw new Error('retained fault') }
    composed.plant.integrate()
  } }
  const kernel = new FmsKernel(plant)
  startKernelPacing(64, kernel, host, { onProgress: value => progress.push(value), onFault: () => { errors++ } })
  host.runNext()
  expect(errors).toBe(1); expect(attempts).toBe(2)
  expect(kernel.faulted).toBe(true); expect(kernel.now).toBe(2 * FRAME_UNITS)
  expect(progress.at(-1)?.completedFrames).toBe(1)
  expect(progress.at(-1)?.achievedRate).toBeCloseTo(250 / 270, 12)
  expect(host.tasks.size).toBe(0)
  const resume = startKernelPacing(1, kernel, host, { onFault: noFault })
  host.runNext()
  expect(attempts).toBe(3); expect(kernel.now).toBe(3 * FRAME_UNITS); expect(kernel.faulted).toBe(false)
  resume()
})

test('achieved flying progress resets with a replacement owner and remains finite at short windows', () => {
  const host = new SchedulingHost(), composed = singleComposition(epoch), kernel = new FmsKernel(composed.plant), reports: PacingProgress[] = []
  const stop = startKernelPacing(4, kernel, host, { onFault: noFault, onProgress: r => reports.push(r) })
  host.runNext(); expect(reports[0]).toEqual({ completedFrames: 4, wallMilliseconds: 250, achievedRate: 4 })
  stop(); const reset = startKernelPacing(1, kernel, host, { onFault: noFault, onProgress: r => reports.push(r) })
  host.runNext(); expect(reports.at(-1)).toEqual({ completedFrames: 1, wallMilliseconds: 250, achievedRate: 1 })
  expect(reports.every(r => Number.isFinite(r.achievedRate))).toBe(true)
  reset()
})

test('current achieved rate follows a sustained load change instead of retaining the earlier fast run', () => {
  const host = new SchedulingHost(), composed = singleComposition(epoch), reports: PacingProgress[] = []
  let cost = 0
  const plant = { ...composed.plant, integrate: () => { composed.plant.integrate(); host.wall += cost } }
  const kernel = new FmsKernel(plant)
  const stop = startKernelPacing(64, kernel, host, { onFault: noFault, onProgress: r => reports.push(r) })
  for (let i = 0; host.wall < 4000 && i < 1000; i++) host.runNext()
  expect(reports.at(-1)?.achievedRate).toBeGreaterThan(60)
  cost = 80 // One whole frame now costs 80 ms: sustained capacity is 0.25 / 0.08 = 3.125x.
  for (let i = 0; host.wall < 8500 && i < 1000; i++) host.runNext()
  expect(reports.at(-1)?.achievedRate).toBeGreaterThan(2.8)
  expect(reports.at(-1)?.achievedRate).toBeLessThan(3.4)
  expect(reports.at(-1)?.completedFrames).toBeGreaterThan(1000)
  stop(); expect(host.tasks.size).toBe(0)
})

test('a progress wake expires old completed work without advancing a flying frame', () => {
  const host = new SchedulingHost(), composed = singleComposition(epoch), kernel = new FmsKernel(composed.plant)
  const reports: PacingProgress[] = []
  const stop = startKernelPacing(4, kernel, host, { onFault: noFault, onProgress: r => reports.push(r) })
  host.runNext()
  expect(reports.at(-1)?.achievedRate).toBe(4)
  const before = { instant: kernel.now, utc: kernel.unitClockMs, frames: reports.at(-1)?.completedFrames }
  // The oldest remaining host task is the independently scheduled progress wake, before the next admission.
  const [id, task] = [...host.tasks.entries()].sort((a, b) => a[0] - b[0])[0]
  host.tasks.delete(id); host.wall = 5000; task.callback()
  expect(reports.at(-1)?.achievedRate).toBe(0)
  expect({ instant: kernel.now, utc: kernel.unitClockMs, frames: reports.at(-1)?.completedFrames }).toEqual(before)
  stop(); expect(host.tasks.size).toBe(0)
})

for (const interval of [250, 1000]) test(`both presentation subscriptions coalesce frames for ${interval}ms while model revisions and control feedback remain immediate`, () => {
  const host = new SchedulingHost(), composed = dualComposition(epoch), kernel = new FmsKernel(composed.plant)
  const presentation = new BenchPresentation(composed.system.computers, host, interval)
  let bench = 0, panel = 0
  const offBench = presentation.subscribe(() => { bench++ }), offPanel = presentation.subscribe(() => { panel++ })
  const beforeVersion = presentation.revision(), beforeModel = composed.system.computers.map(f => f.revision())
  presentation.frame(() => kernel.advance(1))
  expect(presentation.revision()).toBe(beforeVersion)
  expect(composed.system.computers.every((f, i) => f.revision() > beforeModel[i])).toBe(true)
  expect(bench).toBe(0); expect(panel).toBe(0)
  expect(host.delays.at(-1)).toBe(interval)
  composed.system.computers[0].press('LEGS')
  expect(presentation.revision()).toBeGreaterThan(beforeVersion)
  expect(bench).toBeGreaterThan(0); expect(panel).toBe(bench)
  expect(host.tasks.size).toBe(0)
  presentation.frame(() => kernel.advance(1)); host.runNext()
  expect(panel).toBe(bench)
  // A completed frame at an already-due publication boundary is visible before returning, with no extra timer turn.
  const beforeDue = presentation.revision()
  host.wall += interval
  presentation.frame(() => kernel.advance(1))
  expect(presentation.revision()).toBeGreaterThan(beforeDue)
  expect(panel).toBe(bench); expect(host.tasks.size).toBe(0)
  offBench(); offPanel(); expect(host.tasks.size).toBe(0)
})
