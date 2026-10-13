import { expect, logicTest as test } from './isolated-client-test'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { dualComposition, singleComposition, type LegacyPlant } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { ScenarioRunner, type Scenario, type ScenarioStep } from '../src/fmsCdu/scenario'

// #1517 I1a: the kernel's event order under the legacy plant adapter (#1502 D1 3.2, 3.8a; D5 7.5). Every expectation is
// read off the D5 7.5 paper timeline, Δ = 0.25 s = 2,500,000 units: INTEGRATE at F_j is tick j+1's clock move and
// step, so its units' clock reads F_{j+1} while kernel time is F_j; ACTION at F_{j+1} then runs the steps due by then.

const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const F = (j: number) => j * 2_500_000
const scenario = (steps: ScenarioStep[], maxSeconds = 10): Scenario => ({ id: 'phases', title: 'Phases', objective: '', maxSeconds, steps })
const alert = (text: string, when: ScenarioStep['when']): ScenarioStep => ({ when, action: { kind: 'alert', text } })
const expectAlert = (text: string, when: ScenarioStep['when']): ScenarioStep => ({ when, action: { kind: 'expectAlert', text } })

/** The real composition, watched from outside: what each INTEGRATE saw as it began and as it ended. */
function watched(composition: ReturnType<typeof singleComposition>) {
  const integrations: { kernelBefore: number; clockBefore: number; clockAfter: number; kernelAfter: number }[] = []
  let kernel: FmsKernel | null = null
  const plant: LegacyPlant = {
    clock: composition.plant.clock,
    units: composition.plant.units,
    integrate: () => {
      const kernelBefore = kernel!.now, clockBefore = composition.clock.time
      composition.plant.integrate()
      integrations.push({ kernelBefore, clockBefore, clockAfter: composition.clock.time, kernelAfter: kernel!.now })
    },
  }
  return { plant, integrations, attach: (k: FmsKernel) => { kernel = k; return k } }
}

// Owner: INTEGRATE at F_j runs exactly once per frame, with the units' clock one frame ahead of kernel time (the
// declared adapter exception); the instant closes before it, with both clocks at F_j; time is exact integer units.
test('each frame closes its instant, then runs one INTEGRATE whose clock reads the next instant while kernel time stays', () => {
  const composition = singleComposition(START)
  const { plant, integrations, attach } = watched(composition)
  const kernel = attach(new FmsKernel(plant))
  const closes: [number, number][] = []
  expect(kernel.advance(3, { onInstantClose: () => closes.push([kernel.now, composition.clock.time]) })).toBe(3)
  expect(closes).toEqual([[F(0), F(0)], [F(1), F(1)], [F(2), F(2)]])
  expect(integrations).toEqual([
    { kernelBefore: F(0), clockBefore: F(0), clockAfter: F(1), kernelAfter: F(0) },
    { kernelBefore: F(1), clockBefore: F(1), clockAfter: F(2), kernelAfter: F(1) },
    { kernelBefore: F(2), clockBefore: F(2), clockAfter: F(3), kernelAfter: F(2) },
  ])
  expect(kernel.now).toBe(7_500_000)
  expect(kernel.unitClockMs).toBe(START + 750)
  expect(composition.fms.now.getTime()).toBe(START + 750)
  expect(() => kernel.advance(0.5)).toThrow(RangeError)
  expect(kernel.now).toBe(7_500_000)
})

// Owner: v1 steps run in ACTION, at F_0 when the runner is constructed, and at the first frame at or after their time,
// after the INTEGRATE that leads into it; a check sees the steps before it at the same instant.
test('steps run in ACTION: at F_0 on construction, then in each frame after its step, checks seeing earlier steps', () => {
  const composition = singleComposition(START)
  const runner = new ScenarioRunner(scenario([
    alert('ZERO', { kind: 'start' }), expectAlert('ZERO', { kind: 'start' }),
    alert('HALF', { kind: 'time', seconds: 0.5 }), expectAlert('HALF', { kind: 'after', seconds: 0 }),
    alert('LATE', { kind: 'time', seconds: 0.6 }),
  ]), composition.fms, undefined, composition.sim)
  const kernel = new FmsKernel(composition.plant, runner)
  // F_0's ACTION ran in the constructor: the kernel rests at R_0 with the first two steps done.
  expect(runner.results.slice(0, 2)).toEqual([{ status: 'done', at: 0 }, { status: 'pass', at: 0, actual: 'ZERO' }])
  expect(runner.results[2]).toEqual({ status: 'pending' })
  kernel.advance(1)
  expect(runner.results[2]).toEqual({ status: 'pending' })
  kernel.advance(1)
  expect(runner.results.slice(2, 4)).toEqual([{ status: 'done', at: 0.5 }, { status: 'pass', at: 0.5, actual: 'HALF' }])
  // Due at 0.6, between frames: it runs in the ACTION at F_3 = 0.75.
  kernel.advance(1)
  expect(runner.results[4]).toEqual({ status: 'done', at: 0.75 })
})

/** What INTEGRATE produces: where the aircraft is and how it flies. */
const flown = ({ fms, sim }: ReturnType<typeof singleComposition>) => ({ position: fms.truePosition, track: fms.track, bank: sim.bankAngle })

// Owner: the ACTION rule at R_k. Steps due at F_k run in its poll; an input made at the resting point R_k then executes
// in the still-open ACTION at F_k: after the poll (a check at F_k does not see it), and before F_k's INTEGRATE, which
// flies the selection (against a control run with no selection, and one that selects only after that INTEGRATE).
test('an input at the resting point comes after the steps due at that instant and is flown by its INTEGRATE', () => {
  const drive = (selectAt: number | null) => {
    const composition = singleComposition(START)
    const runner = new ScenarioRunner(scenario([
      { when: { kind: 'time', seconds: 0.5 }, action: { kind: 'expectNoAlert', text: 'INPUT' } },
      expectAlert('INPUT', { kind: 'after', seconds: 0.25 }),
    ]), composition.fms, undefined, composition.sim)
    const kernel = new FmsKernel(composition.plant, runner)
    for (let j = 0; j <= 3; j += 1) {
      if (j === selectAt) {
        // R_j: the bench's controls act here, at kernel time F_j.
        expect(kernel.now).toBe(F(j))
        composition.fms.raiseAlert('INPUT')
        composition.sim.selectHeading((composition.fms.track + 90) % 360)
      }
      if (j < 3) kernel.advance(1)
    }
    return { results: runner.results, flown: flown(composition) }
  }
  const atR2 = drive(2), control = drive(null), atR3 = drive(3)
  expect(atR2.results).toEqual([{ status: 'pass', at: 0.5, actual: 'not raised' }, { status: 'pass', at: 0.75, actual: 'INPUT' }])
  // At R_3 the selection made at R_2 has been flown for [F_2, F_3]. One made at R_3 itself has not: selecting moves
  // nothing until the INTEGRATE that follows it.
  expect(atR2.flown).not.toEqual(control.flown)
  expect(atR3.flown).toEqual(control.flown)
})

// Owner: the run-end halt. A run that finishes in an ACTION halts that call there: no INTEGRATE follows at F_end, and
// the remaining frames are not run. Flying on afterwards is a later call's choice, as on the bench.
test('a run that ends in an ACTION halts the call there, with no INTEGRATE after the end', () => {
  const composition = singleComposition(START)
  const { plant, integrations, attach } = watched(composition)
  const runner = new ScenarioRunner(scenario([alert('END', { kind: 'time', seconds: 1 })]), composition.fms, undefined, composition.sim)
  const kernel = attach(new FmsKernel(plant, runner))
  expect(kernel.advance(40)).toBe(4)
  expect(runner.finished).toBe(true)
  expect(runner.endedAfter).toBe(1)
  expect(integrations).toHaveLength(4)
  expect(kernel.now).toBe(F(4))
  expect(kernel.unitClockMs).toBe(START + 1000)
  expect(kernel.advance(2)).toBe(2)
  expect(integrations).toHaveLength(6)
})

// Owner: an INTEGRATE that throws (#1508 is the known case). Kernel time follows the units' clock to F_{j+1}, the poll
// at F_{j+1} is skipped and stays skipped, the kernel is faulted, an input then executes in that open ACTION without a
// poll and is flown by the next INTEGRATE (against a control run with the same fault and no input), and the next call
// starts with that INTEGRATE.
test('an INTEGRATE that throws leaves the kernel faulted at the next instant, its poll skipped, an input there flown next', () => {
  const drive = (input: boolean) => {
    const composition = singleComposition(START)
    const { sim, fms } = composition
    const step = sim.step.bind(sim)
    let steps = 0
    sim.step = dt => { steps += 1; if (steps === 3) throw new Error('guidance source switch'); step(dt) }
    const runner = new ScenarioRunner(scenario([
      alert('DUE', { kind: 'time', seconds: 0.75 }), expectAlert('INPUT', { kind: 'after', seconds: 0 }),
    ]), fms, undefined, sim)
    const kernel = new FmsKernel(composition.plant, runner)
    kernel.advance(2)
    expect(kernel.faulted).toBe(false)
    expect(() => kernel.advance(5)).toThrow('guidance source switch')
    // The step began at F_2 and moved the units' clock to F_3 before it threw.
    expect(kernel.faulted).toBe(true)
    expect(kernel.now).toBe(F(3))
    expect(kernel.unitClockMs).toBe(START + 750)
    // The poll at F_3 did not run: the step due at 0.75 is still pending.
    expect(runner.results[0]).toEqual({ status: 'pending' })
    const atFault = flown(composition)
    if (input) {
      // An input executes in F_3's open ACTION: it causes no poll and moves nothing by itself.
      fms.raiseAlert('INPUT')
      sim.selectHeading((fms.track + 90) % 360)
      expect(runner.results[0]).toEqual({ status: 'pending' })
      expect(flown(composition)).toEqual(atFault)
    }
    // The next call starts with INTEGRATE at F_3; the poll resumes at F_4, where the skipped step finally runs.
    expect(kernel.advance(1)).toBe(1)
    expect(kernel.faulted).toBe(false)
    expect(kernel.now).toBe(F(4))
    expect(steps).toBe(4)
    return { results: runner.results, flown: flown(composition) }
  }
  const withInput = drive(true), control = drive(false)
  expect(withInput.results).toEqual([{ status: 'done', at: 1 }, { status: 'pass', at: 1, actual: 'INPUT' }])
  expect(control.results[0]).toEqual({ status: 'done', at: 1 })
  // The INTEGRATE at F_3 flew the selection made at the faulted resting point.
  expect(withInput.flown).not.toEqual(control.flown)
})

// Owner: a flight freeze INTEGRATE holds the aircraft while the clock moves a frame; a composition without a freeze
// refuses it before anything moves.
test('a flight freeze moves the clock a frame at a time and holds the aircraft; the single composition refuses it', () => {
  const { system, plant, clock } = dualComposition(START)
  const kernel = new FmsKernel(plant)
  const where = () => system.computers.map(fms => fms.truePosition)
  const before = where()
  kernel.submit({ kind: 'ios.flightFreeze', on: true }, { kind: 'ios', id: 'test', surface: 'main' })
  expect(kernel.advance(4)).toBe(4)
  expect(where()).toEqual(before)
  expect(clock.ms).toBe(START + 1000)
  kernel.submit({ kind: 'ios.flightFreeze', on: false }, { kind: 'ios', id: 'test', surface: 'main' })
  kernel.advance(1)
  expect(where()).not.toEqual(before)

  const single = singleComposition(START)
  const refusing = new FmsKernel(single.plant)
  refusing.submit({ kind: 'ios.flightFreeze', on: true }, { kind: 'ios', id: 'test', surface: 'main' })
  expect(refusing.journal.at(-1)!.outcome).toEqual({ status: 'refused', reason: 'this composition has no flight freeze' })
  expect(refusing.flightFreeze).toBe(false)
  expect(refusing.advance(1)).toBe(1)
  expect(single.clock.time).toBe(2_500_000)
  expect(refusing.faulted).toBe(false)
})
