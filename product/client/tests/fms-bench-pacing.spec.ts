import { expect, logicTest as test } from './isolated-client-test'
import { readFileSync } from 'node:fs'
import { startTickPacing, startKernelPacing, PACING_TIMELINE, type PacingProgress } from '../src/fmsCdu/benchPacing'
import { BenchPresentation } from '../src/fmsCdu/benchPresentation'
import { ScenarioRunner, runHeadless, type Scenario } from '../src/fmsCdu/scenario'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { singleComposition, dualComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { FRAME_UNITS } from '../src/fmsCdu/kernel/time'
import { SchedulingHost, TimelineHost } from './fixtures/fms-pacing-host'

const epoch = Date.UTC(2026, 8, 27, 14)
const noFault = (error: unknown) => { throw error }

test('profiling observes direct kernel and unit clocks at slice entry and exit, including incomplete work', () => {
  const host = new TimelineHost(), composed = singleComposition(epoch)
  let attempts = 0
  const kernel = new FmsKernel({ ...composed.plant, integrate: () => {
    attempts++; host.wall += 10
    if (attempts === 2) { composed.clock.advanceFrame(); throw new Error('retained observation fault') }
    composed.plant.integrate()
  } })
  startKernelPacing(64, kernel, host, { onFault: () => {}, observeFrame: () => ({ sync: 'SYNC' }) })
  host.runNext()
  const observation = host.measures.find(entry => entry.name === 'aerolink.fms-bench.pacing-clock')
  // The failed second INTEGRATE advances both clocks but never completes a frame. These literals are not
  // reconstructed from the timeline or the successful-completion count under test.
  expect(observation?.detail.entry).toMatchObject({ kernelTime: 0, unitClockMs: epoch, completed: 0 })
  expect(observation?.detail.exit).toMatchObject({ kernelTime: 5_000_000, unitClockMs: epoch + 500, completed: 1 })
  expect(observation?.detail).toMatchObject({ schema: 1, sequence: 1, rate: 64 })
  const entry = observation!.detail.entry as { utcMs: number; monotonicMs: number; timeOrigin: number | null }
  const exit = observation!.detail.exit as typeof entry
  expect([entry.monotonicMs, exit.monotonicMs]).toEqual([250, 270])
  expect([entry.utcMs, exit.utcMs].every(Number.isFinite)).toBe(true) // UTC observations are retained, not assumed monotonic.
  expect(entry.timeOrigin).toBeNull() // This deterministic host has no browser timeOrigin.
  expect(kernel.faulted).toBe(true)
  expect(host.measures.filter(entry => entry.name === 'aerolink.fms-bench.pacing-success-frame').map(row => row.detail.ordinal)).toEqual([1])
  expect(host.measures.at(-1)?.detail).toMatchObject({ kind: 'fault', admitted: 64, attempted: 2, completed: 1, discarded: 62, debt: 0 })
})

test('successful-frame profiling is after real completion, keeps slice order and cannot control work', () => {
  const run = (observe: boolean) => {
    const host = new TimelineHost(), composed = singleComposition(epoch), kernel = new FmsKernel(composed.plant)
    const instants: number[] = []
    const stop = startKernelPacing(64,kernel,host,{onFault:noFault,onInstantClose:()=>{instants.push(kernel.now);host.wall+=8},
      ...(observe?{observeFrame:(ordinal:number)=>{if(ordinal===2)throw new Error('lost observation');return {sync:'SYNC'}}}:{})})
    for(let i=0;i<16;i++)host.runNext()
    stop()
    return {host,kernel,instants,aircraft:JSON.stringify(composed.fms.aircraft)}
  }
  const off=run(false),on=run(true)
  expect(on.instants).toEqual(off.instants)
  expect(on.aircraft).toBe(off.aircraft)
  expect(on.kernel.now).toBe(160_000_000)
  expect(on.kernel.unitClockMs).toBe(epoch+16000)
  const work = (host: TimelineHost) => host.timeline().map(({detail,...row})=>{const {epoch:_epoch,...counts}=detail;return {...row,detail:counts}})
  expect(work(on.host)).toEqual(work(off.host)) // distinct Fly epochs are identities, not scheduling differences
  const frames=on.host.measures.filter(row=>row.name==='aerolink.fms-bench.pacing-success-frame')
  expect(frames).toHaveLength(63)
  expect(frames[0].detail).toMatchObject({ordinal:1,sequence:1,batch:1,entry:{kernelTime:0,completed:0},frame:{kernelTime:2_500_000,unitClockMs:epoch+250,completed:1}})
  expect(frames.at(-1)?.detail).toMatchObject({ordinal:64,sequence:16,frame:{kernelTime:160_000_000,completed:64}})
  expect(on.host.measures.at(-1)?.detail).toMatchObject({kind:'cancelled',completed:64,admitted:64,attempted:64,discarded:0,debt:0})
})

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

for (const placed of [
  { start: '87n-offshore-sar' as const, altitude: 500, firstTas: 119.5 },
  { start: 'kbtv-rnav15' as const, altitude: 3200, firstTas: 120.5 },
]) for (const topology of ['single', 'dual FMS1', 'dual FMS2'] as const) {
  const title = placed.start === '87n-offshore-sar' && topology === 'dual FMS1'
    ? 'the dual composition settles the placed aircraft on both units before the first scenario input at t0'
    : `the composition settles placed physical TAS before t0 and the first complete frame (${placed.start}, ${topology})`
  test(title, () => {
    const single = topology === 'single' ? singleComposition(epoch) : null
    const dual = single ? null : dualComposition(epoch)
    const side = topology === 'dual FMS2' ? 2 : 1
    dual?.system.selectGuidance(side)
    const fms = single?.fms ?? dual!.system.computers[side - 1]
    const peer = single?.fms ?? dual!.system.computers[2 - side]
    const units = single ? [single.fms] : dual!.system.computers
    const flights = single ? [single.sim] : dual!.system.flights
    const sim = single?.sim ?? dual!.system.simulator
    const plant = single?.plant ?? dual!.plant
    const settleStart = single?.settleStart ?? dual!.settleStart
    const clock = single?.clock ?? dual!.clock
    const atInput: { lat: number, lon: number, utc: number, physicalTas: number[], controllerTas: number[] }[] = []
    const press = fms.press.bind(fms)
    fms.press = key => {
      atInput.push({ ...peer.truePosition, utc: peer.now.getTime(),
        physicalTas: units.map(unit => unit.physicalTrueAirspeed),
        controllerTas: flights.map(flight => flight.tas) })
      press(key)
    }
    const scenario: Scenario = { id: 'settled-t0', title: 'Settled t0', objective: 'Composition start ordering',
      start: placed.start, maxSeconds: 1, steps: [
        { when: { kind: 'start' }, action: { kind: 'keys', keys: ['INIT_REF'] } },
        { when: { kind: 'start' }, action: { kind: 'expectAircraft', altitude: placed.altitude, heightTolerance: 1 } },
      ] }
    let settlements = 0
    const runner = new ScenarioRunner(scenario, fms, undefined, sim, () => {
      settlements++; settleStart()
    })
    // Ignore setup EXEC inputs: this is the first t0 input, after placement and zero-time settlement.
    const firstAction = atInput.at(-1)!
    expect(firstAction.lat).toBeGreaterThan(placed.start === '87n-offshore-sar' ? 40 : 43)
    expect(firstAction.lat).toBeLessThan(placed.start === '87n-offshore-sar' ? 41 : 46)
    expect(firstAction.lon).toBeGreaterThan(placed.start === '87n-offshore-sar' ? -73 : -74)
    expect(firstAction.lon).toBeLessThan(placed.start === '87n-offshore-sar' ? -72 : -71)
    expect(settlements).toBe(1)
    expect(runner.results.map(result => [result.status, result.at])).toEqual([['done', 0], ['pass', 0]])
    expect(clock.ms).toBe(epoch); expect(firstAction.utc).toBe(epoch)
    expect(peer.truePosition).toEqual(fms.truePosition)
    // Both named placements retain the independent 120 kt physical TAS, distinct from the later IAS command.
    expect(firstAction.physicalTas).toEqual(single ? [120] : [120, 120])
    for (const tas of firstAction.controllerTas) expect(tas).toBeCloseTo(120, 8)
    for (const unit of units) {
      expect(unit.navigationInputs?.air).toMatchObject({ status: 'NORMAL', value: { tasValid: true } })
      expect(unit.navigationInputs?.air.value?.tasKt).toBeCloseTo(120, 8)
    }
    // One complete 250 ms frame applies the existing 2 kt/s command law, not a constructor-seed discontinuity.
    // SAR's 100 KIAS command decreases TAS by 0.5; KBTV's 120 KIAS at 3200 ft increases it by 0.5.
    const kernel = new FmsKernel(plant, runner)
    expect(kernel.advance(1)).toBe(1)
    expect(kernel.unitClockMs).toBe(epoch + 250)
    for (const flight of flights) expect(flight.tas).toBeCloseTo(placed.firstTas, 8)
    for (const unit of units) expect(unit.physicalTrueAirspeed).toBeCloseTo(placed.firstTas, 8)
  })
}
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

// The pacing timeline (#1557) is how the browser performance harness counts executed simulation; interval count x
// selected rate overstated it. A host with User Timing receives one measure per slice and per discarded remainder.
// The harness reads the measures by the names its protocol declares; a rename on either side fails here.
test('the bench publishes its pacing timeline under the names the performance protocol declares', () => {
  const protocol = JSON.parse(readFileSync('perf/protocol.json', 'utf8'))
  expect(protocol.browser.accounting.timeline).toEqual(PACING_TIMELINE)
})
const slice = (epoch: number, rate: number, batch: number, admitted: number, attempted: number, completed: number, remaining: number) =>
  JSON.stringify({ schema: 1, epoch, rate, batch, admitted, attempted, completed, remaining })
const discard = (epoch: number, rate: number, batch: number, discarded: number) => JSON.stringify({ schema: 1, epoch, rate, batch, discarded })

test('the pacing timeline reports admitted, completed and discarded frames of a paused batch: 8 admitted, 5 completed, 3 discarded', () => {
  const host = new TimelineHost(), composed = singleComposition(epoch), kernel = new FmsKernel(composed.plant)
  // An indivisible 40 ms frame exceeds the 32 ms slice target, so each task completes one frame and yields.
  const stop = startKernelPacing(8, kernel, host, { onFault: noFault, onInstantClose: () => { host.wall += 40 } })
  for (let i = 0; i < 5; i++) host.runNext()
  stop()
  const [first] = host.measures, e = Number(first.detail.epoch)
  expect(host.entries()).toEqual([
    `slice@250-290 ${slice(e, 8, 1, 8, 1, 1, 7)}`, `slice@290-330 ${slice(e, 8, 1, 0, 1, 1, 6)}`, `slice@330-370 ${slice(e, 8, 1, 0, 1, 1, 5)}`,
    `slice@370-410 ${slice(e, 8, 1, 0, 1, 1, 4)}`, `slice@410-450 ${slice(e, 8, 1, 0, 1, 1, 3)}`, `discard@450-450 ${discard(e, 8, 1, 3)}`])
  expect(kernel.now).toBe(5 * FRAME_UNITS)
  expect(host.measures.at(-1)?.detail).toMatchObject({ kind: 'cancelled', admitted: 8, attempted: 5, completed: 5, discarded: 3, debt: 0 })
  // Every measure is cleared from the performance entry buffer as it is made; observers still receive it.
  expect(host.cleared).toEqual(host.measures.map(measure => measure.name))
  // A later Fly is a new epoch whose batches number from 1 again.
  const resumed = startKernelPacing(8, kernel, host, { onFault: noFault, onInstantClose: () => { host.wall += 40 } })
  host.runNext(); resumed()
  expect(host.entries().slice(6)).toEqual([`slice@700-740 ${slice(e + 1, 8, 1, 8, 1, 1, 7)}`, `discard@740-740 ${discard(e + 1, 8, 1, 7)}`])
})

test('the pacing timeline counts the terminal frame as completed and a faulted attempt as attempted only', () => {
  const ended = new TimelineHost(), composed = singleComposition(epoch)
  const runner = new ScenarioRunner(finishScenario, composed.fms, undefined, composed.sim), kernel = new FmsKernel(composed.plant, runner)
  startKernelPacing(64, kernel, ended, { onFault: noFault, onInstantClose: () => { ended.wall += 20 } })
  for (let i = 0; ended.tasks.size && i < 10; i++) ended.runNext()
  const e = Number(ended.measures[0].detail.epoch)
  // The run ends at 0.75 s: three completed frames; the batch's other 61 frames are discarded, never attempted.
  expect(ended.entries()).toEqual([`slice@250-290 ${slice(e, 64, 1, 64, 2, 2, 62)}`, `slice@290-310 ${slice(e, 64, 1, 0, 1, 1, 61)}`, `discard@310-310 ${discard(e, 64, 1, 61)}`])

  const faulted = new TimelineHost(), plantComposition = singleComposition(epoch)
  let attempts = 0
  const plant = { ...plantComposition.plant, integrate: () => {
    attempts++; faulted.wall += 10
    if (attempts === 2) { plantComposition.clock.advanceFrame(); throw new Error('retained fault') }
    plantComposition.plant.integrate()
  } }
  startKernelPacing(64, new FmsKernel(plant), faulted, { onFault: () => {} })
  faulted.runNext()
  const f = Number(faulted.measures[0].detail.epoch)
  expect(faulted.entries()).toEqual([`slice@250-270 ${slice(f, 64, 1, 64, 2, 1, 62)}`, `discard@270-270 ${discard(f, 64, 1, 62)}`])
})

for (const rate of [1, 64]) test(`finite ${rate}x terminal publishes an actual close after its final slice and real discard without another frame`, () => {
  const host = new TimelineHost(), composed = singleComposition(epoch)
  const runner = new ScenarioRunner(finishScenario, composed.fms, undefined, composed.sim)
  const kernel = new FmsKernel(composed.plant, runner)
  const stop = startKernelPacing(rate, kernel, host, { onFault: noFault })
  while (host.runNext()) { /* actual public driver reaches the three-frame scenario END */ }
  stop(); stop()
  expect(kernel.now).toBe(3 * FRAME_UNITS)
  expect(runner.outcome).toBe('passed')
  const closes = host.measures.filter(entry => entry.name === 'aerolink.fms-bench.pacing-close')
  expect(closes).toHaveLength(1)
  const close = closes[0]
  expect(host.measures.at(-1)).toBe(close)
  expect(close.detail).toMatchObject({ schema: 1, epoch: host.measures[0].detail.epoch, rate,
    sequence: rate === 1 ? 3 : 1, batch: rate === 1 ? 3 : 1, kind: 'run-end',
    admitted: rate === 1 ? 3 : 64, attempted: 3, completed: 3, discarded: rate === 1 ? 0 : 61, debt: 0 })
  const clocks = host.measures.filter(entry => entry.name === 'aerolink.fms-bench.pacing-clock')
  expect(close.detail.lastClock).toEqual(clocks.at(-1)!.detail)
  expect(close.start).toBeGreaterThanOrEqual(clocks.at(-1)!.end)
  expect(host.tasks.size).toBe(0)
})
