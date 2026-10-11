import { expect, logicTest as test } from './isolated-client-test'
import { MISSION_87N_OFFSHORE_SAR } from '../src/fmsCdu/heliDemo'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { dualComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { START_STATES } from '../src/fmsCdu/kbtvDemo'
import { ScenarioRunner, TICK_SECONDS } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'

// #1511 owner: in a synchronized dual installation both computers fly one approach in one phase of flight. Each
// computer enters the approach phase 2 NM before the FAF when the approach is armed and integrity is predicted
// (M300 7-10), so the 30 s phase-disagreement rule (M300 3-25) does not fire on a normal approach when both receivers
// agree. M300 7-8 says the arm comes automatically or from "an instrument panel-mounted switch", depending on the
// installation; one panel switch wired to both computers is a bench installation assumption. A single receiver's
// integrity loss is #1538. tests/fms-tactical-maint.spec.ts owns the rule itself firing on a real disagreement.

/** The bench's start-state composition (FmsCduTestBench.tsx): set up FMS 1 and its flight, then copy to FMS 2. */
function approachSetup() {
  const { system, plant } = dualComposition(Date.parse(MISSION_87N_OFFSHORE_SAR.startTime!))
  const [one, two] = system.computers
  expect(one.compute(() => {
    const outcome = START_STATES['87n-rnav190-final'].setUp(one, system.flights[0])
    one.dualOperation?.settingsChanged(); one.dualOperation?.finishEdit(true); two.observeAircraft(one)
    return outcome
  })).toEqual({ ready: true })
  expect(system.mode).toBe('SYNC')
  const kernel = new FmsKernel(plant)
  const tick = () => { kernel.advance(1) }
  return { system, one, two, tick }
}

test('a synchronized dual installation enters the approach phase on both computers and stays synchronized', () => {
  const { system, one, two, tick } = approachSetup()
  const phases: { at: number; one: string; two: string; mode: string }[] = []
  let approachAt: number | null = null
  for (let n = 1; n * TICK_SECONDS <= 240; n += 1) {
    tick()
    const at = n * TICK_SECONDS
    phases.push({ at, one: one.localFlightPhase, two: two.localFlightPhase, mode: system.mode })
    if (approachAt === null && one.localFlightPhase === 'APPROACH') approachAt = at
    // Well past the 30 s rule once the selected computer is in the approach phase.
    if (approachAt !== null && at - approachAt > 45) break
  }

  // Positive control: the selected computer did reach the approach phase, so the rule had its chance to fire.
  expect(approachAt).not.toBeNull()
  expect(two.approachArmed).toBe(true)
  expect(phases.filter(sample => sample.mode !== 'SYNC')).toEqual([])
  expect(one.faultLog.filter(fault => fault.text.startsWith('X-SIDE SYNC LOST'))).toEqual([])
  expect(phases.at(-1)).toMatchObject({ one: 'APPROACH', two: 'APPROACH', mode: 'SYNC' })
})

// A guidance-source change requires re-arming (FlightSimulator.adoptAircraftMotion): the shared arm must follow that
// disarm to the other computer rather than re-arm the new one from the old, and arming again reaches both.
test('a guidance-source change disarms both computers in SYNC and a later arm on the new guidance computer arms both', () => {
  const { system, one, two, tick } = approachSetup()
  tick()
  expect([one.approachArmed, two.approachArmed]).toEqual([true, true])

  system.selectGuidance(2)
  expect(system.guidanceSide).toBe(2)
  expect(two.approachArmed).toBe(false)
  tick()
  expect(one.approachArmed).toBe(false)

  const modes: string[] = []
  for (let n = 1; n * TICK_SECONDS <= 40; n += 1) { tick(); modes.push(system.mode) }
  expect(modes.filter(mode => mode !== 'SYNC')).toEqual([])

  expect(two.armApproach(true)).toBe(true)
  tick()
  expect([one.approachArmed, two.approachArmed]).toEqual([true, true])
  expect(system.mode).toBe('SYNC')
})

// #1567 primary owner: the named armed start must not leave a false ARM APPROACH in either computer's history.
// The existing phase owner above observes the settled phase, not the earlier approach-monitoring/arm order.
// Use the actual runner constructor (including its t0 poll), without pre-arming the peer, calling its observer,
// acknowledging messages, or introducing a test-only settlement callback. Pass the actual dualComposition
// settleStart to the production constructor: setup, zero-time dual settlement, then its original t0 poll.
const STARTUP_SCENARIOS = ['87n-d-early-toga', '87n-e-direct-on-final', '87n-f-proceed-vfr'] as const

function namedApproachStartup(id: typeof STARTUP_SCENARIOS[number], side: 1 | 2) {
  const scenario = SCENARIO_LIBRARY.find(entry => entry.id === id)
  expect(scenario).toBeDefined()
  expect(scenario!.start).toBe('87n-rnav190-final')
  expect(scenario!.startTime).toBe('2026-09-29T15:00:00Z')
  const { system, plant, settleStart } = dualComposition(Date.parse(scenario!.startTime!))
  system.selectGuidance(side)
  const guiding = system.computers[side - 1]
  const snapshot = () => system.computers.map(unit => ({
    utc: unit.now.toISOString(),
    armed: unit.approachArmed,
    // recallList is a live array: keep actual historical samples by value.
    recall: structuredClone(unit.recallList),
    scratch: screenText(unit.screen())[SCRATCHPAD_LINE].trimEnd(),
    msg: unit.lamps().has('MSG'),
  }))
  const before = snapshot()
  const runner = new ScenarioRunner(scenario!, guiding, undefined, system.flights[side - 1], settleStart)
  const afterStart = snapshot()
  const kernel = new FmsKernel(plant, runner)
  expect(kernel.advance(1)).toBe(1)
  const firstFrame = snapshot()
  expect(kernel.advance(1)).toBe(1)
  const secondFrame = snapshot()
  return { system, guiding, runner, kernel, snapshot, samples: [before, afterStart, firstFrame, secondFrame] }
}

for (const id of STARTUP_SCENARIOS) for (const side of [1, 2] as const) {
  test(`${id}: FMS${side}-guided armed startup has no false ARM APPROACH at t0 or either first frame`, () => {
    const { system, guiding, runner, samples } = namedApproachStartup(id, side)
    expect(system.guidanceSide).toBe(side)
    expect(system.mode).toBe('SYNC')
    expect(runner.problems).toEqual([])
    expect(runner.startedAt.toISOString()).toBe('2026-09-29T15:00:00.000Z')
    expect(runner.elapsed).toBe(0.5)
    for (const unit of system.computers) {
      expect(unit.activeRoute.approach).toEqual({ ident: 'R190', transition: 'HTO' })
      expect(unit.activeRoute.legs[0]).toMatchObject({ kind: 'wpt', ident: 'STAYS' })
      expect(unit.finalApproachFix).toBe('STAYS')
    }
    expect(guiding.distanceToFaf).toBeGreaterThan(2.5)
    expect(guiding.distanceToFaf).toBeLessThan(3.5)
    expect(samples[1][side - 1].armed).toBe(true)
    expect(samples[3].map(unit => unit.armed)).toEqual([true, true])

    const times = ['2026-09-29T15:00:00.000Z', '2026-09-29T15:00:00.000Z',
      '2026-09-29T15:00:00.250Z', '2026-09-29T15:00:00.500Z']
    for (const [index, sample] of samples.entries()) {
      expect(sample.map(unit => unit.utc)).toEqual([times[index], times[index]])
      expect(sample.map(unit => unit.recall.filter(message => message.text === 'ARM APPROACH').length),
        `startup boundary ${index}: ${JSON.stringify(sample)}`).toEqual([0, 0])
      expect(sample.map(unit => unit.scratch === 'ARM APPROACH')).toEqual([false, false])
    }
  })
}

for (const side of [1, 2] as const) {
  test(`FMS${side}-guided genuine unarmed approach still alerts both computers after shared-panel propagation`, () => {
    const { system, guiding, kernel, snapshot } = namedApproachStartup('87n-d-early-toga', side)
    expect(system.mode).toBe('SYNC')
    expect(system.computers.map(unit => unit.approachArmed)).toEqual([true, true])
    const before = snapshot().map(unit => unit.recall.filter(message => message.text === 'ARM APPROACH').length)
    expect(guiding.armApproach(false)).toBe(false)
    expect(kernel.advance(2)).toBe(2)
    const unarmed = snapshot()
    expect(unarmed.map(unit => unit.armed)).toEqual([false, false])
    // Genuine warning availability is this control's contract. Exact cross-side episode cardinality belongs to
    // #1564; the pre-episode fb7 implementation can independently append another local occurrence.
    for (const [index, unit] of unarmed.entries()) {
      expect(unit.recall.filter(message => message.text === 'ARM APPROACH').length - before[index])
        .toBeGreaterThanOrEqual(1)
    }
    expect(unarmed.map(unit => unit.msg)).toEqual([true, true])

    expect(guiding.armApproach(true)).toBe(true)
    expect(kernel.advance(2)).toBe(2)
    const rearmed = snapshot()
    expect(rearmed.map(unit => unit.armed)).toEqual([true, true])
    expect(rearmed.map(unit => unit.recall.filter(message => message.text === 'ARM APPROACH').length))
      .toEqual(unarmed.map(unit => unit.recall.filter(message => message.text === 'ARM APPROACH').length))
  })

  test(`FMS${side}-guided INDEPENDENT operation preserves the existing shared physical-panel arm boundary`, () => {
    const { system, guiding, kernel } = namedApproachStartup('87n-d-early-toga', side)
    const peer = system.computers[2 - side]
    guiding.setCondition('independent', true)
    expect(system.mode).toBe('INDEPENDENT')
    expect(guiding.approachArmed).toBe(true)
    expect(peer.armApproach(false)).toBe(false)
    // The peer's local choice is transient: actual observe/refreshGuidance already adopts the one physical panel.
    expect([guiding.approachArmed, peer.approachArmed]).toEqual([true, false])
    expect(kernel.advance(1)).toBe(1)
    expect(system.mode).toBe('INDEPENDENT')
    expect(system.computers.map(unit => unit.approachArmed)).toEqual([true, true])
    expect(guiding.armApproach(false)).toBe(false)
    expect(kernel.advance(1)).toBe(1)
    expect(system.computers.map(unit => unit.approachArmed)).toEqual([false, false])
  })
}
