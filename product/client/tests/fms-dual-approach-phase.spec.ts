import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { MISSION_87N_OFFSHORE_SAR } from '../src/fmsCdu/heliDemo'
import { START_STATES } from '../src/fmsCdu/kbtvDemo'
import { TICK_SECONDS, advanceTicks } from '../src/fmsCdu/scenario'

// #1511 owner: in a synchronized dual installation both computers fly one approach in one phase of flight. Each
// computer enters the approach phase 2 NM before the FAF when the approach is armed and integrity is predicted
// (M300 7-10), so the 30 s phase-disagreement rule (M300 3-25) does not fire on a normal approach when both receivers
// agree. M300 7-8 says the arm comes automatically or from "an instrument panel-mounted switch", depending on the
// installation; one panel switch wired to both computers is a bench installation assumption. A single receiver's
// integrity loss is #1538. tests/fms-tactical-maint.spec.ts owns the rule itself firing on a real disagreement.

/** The bench's start-state composition (FmsCduTestBench.tsx): set up FMS 1 and its flight, then copy to FMS 2. */
function approachSetup() {
  let now = Date.parse(MISSION_87N_OFFSHORE_SAR.startTime!)
  const system = new DualFmsSystem(() => new Date(now))
  const [one, two] = system.computers
  expect(one.compute(() => {
    const outcome = START_STATES['87n-rnav190-final'].setUp(one, system.flights[0])
    one.dualOperation?.settingsChanged(); one.dualOperation?.finishEdit(true); two.observeAircraft(one)
    return outcome
  })).toEqual({ ready: true })
  expect(system.mode).toBe('SYNC')
  const tick = () => advanceTicks(1, ms => { now += ms }, system, null)
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
