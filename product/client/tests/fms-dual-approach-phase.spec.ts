import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { MISSION_87N_OFFSHORE_SAR } from '../src/fmsCdu/heliDemo'
import { START_STATES } from '../src/fmsCdu/kbtvDemo'
import { TICK_SECONDS, advanceTicks } from '../src/fmsCdu/scenario'

// #1511 owner: in a synchronized dual installation both computers fly one approach in one phase of flight. The approach
// is armed by one aircraft switch (M300 7-8) and each computer enters the approach phase 2 NM before the FAF with
// approach integrity predicted (M300 7-10), so the 30 s phase-disagreement rule (M300 3-25) never fires on a normal
// approach. tests/fms-tactical-maint.spec.ts owns the rule itself firing on a real disagreement.
test('a synchronized dual installation enters the approach phase on both computers and stays synchronized', () => {
  let now = Date.parse(MISSION_87N_OFFSHORE_SAR.startTime!)
  const system = new DualFmsSystem(() => new Date(now))
  const [one, two] = system.computers
  // The bench's start-state composition (FmsCduTestBench.tsx): set up FMS 1 and its flight, then copy to FMS 2.
  expect(one.compute(() => {
    const outcome = START_STATES['87n-rnav190-final'].setUp(one, system.flights[0])
    one.dualOperation?.settingsChanged(); one.dualOperation?.finishEdit(true); two.observeAircraft(one)
    return outcome
  })).toEqual({ ready: true })
  expect(system.mode).toBe('SYNC')

  const phases: { at: number; one: string; two: string; mode: string }[] = []
  let approachAt: number | null = null
  for (let tick = 1; tick * TICK_SECONDS <= 240; tick += 1) {
    advanceTicks(1, ms => { now += ms }, system, null)
    const at = tick * TICK_SECONDS
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
