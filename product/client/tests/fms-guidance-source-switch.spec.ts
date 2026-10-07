import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import type { FmsSide } from '../src/fmsCdu/crossTalk'
import { distanceNm, type LatLon } from '../src/fmsCdu/fmsModel'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'

// #1508: changing the FMS guidance source in the low-speed regime (#1502 §19 item 2, decided by Sean on 6 Oct 2026).
// One aircraft and one AFCS: the aircraft's motion carries on unchanged; the AFCS cancels an FMS-coupled transition in
// progress (TDN to MRK, the hover join), holds its low-speed mode (HOV at the present position, or ATT without hover
// feedback), annunciates the change and is re-engaged by the crew against the new computer.
// Owner: dualFms.ts selectGuidance and flight.ts adoptAircraftMotion. The bench drives the same production modules.

const START = Date.UTC(2026, 8, 29, 15, 0, 0)
const metres = (a: LatLon, b: LatLon) => distanceNm(a, b) * 1852
/** The most the one aircraft's air velocity can change in a 0.25 s step, at the profile's acceleration limits (kt). */
const STEP_KT = Math.hypot(HELICOPTER_PROFILE.parameters.longitudinalAccel.value, HELICOPTER_PROFILE.parameters.lateralAccel.value) * 0.25 + 1e-9

/** The 87N offshore SAR start on a dual system, set up and copied to FMS 2 as the bench does it. */
function mission() {
  let now = START
  const system = new DualFmsSystem(() => new Date(now))
  const [one, two] = system.computers
  one.compute(() => {
    expect(setUp87nOffshoreSar(one, system.flights[0])).toEqual({ ready: true })
    one.dualOperation?.settingsChanged(); one.dualOperation?.finishEdit(true); two.observeAircraft(one)
  })
  const fly = (seconds: number) => { for (let i = 0; i < seconds * 4; i++) { now += 250; system.step(0.25) } }
  const flyUntil = (done: () => boolean, limit: number) => {
    for (let i = 0; i < limit * 4 && !done(); i++) { now += 250; system.step(0.25) }
    expect(done(), 'condition reached in time').toBe(true)
  }
  /** The last mode change, as the bench shows it, and what the FMA shows amber for the 5 s capture box. */
  const annunciated = () => ({ last: system.simulator.modeEvents.at(-1)!, degraded: system.simulator.axisDegraded(5) })
  return { system, one, two, fly, flyUntil, annunciated }
}

/**
 * Slowed to hover speed and HOV engaged on the given computer, then settled. With `ownReceivers`, the computers run
 * INDEPENDENT, FMS 1 on GPS 1 and FMS 2 on GPS 2, so each measures the hover on its own receiver.
 */
function hovering(side: FmsSide, ownReceivers = false) {
  const setup = mission()
  const { system, one, two, fly, flyUntil } = setup
  fly(1)
  if (ownReceivers) { system.setLinkAvailable(false); one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2') }
  system.selectGuidance(side)
  system.simulator.selectSpeed(20)
  flyUntil(() => system.simulator.tas < 26, 120)
  expect(system.simulator.engageHover()).toBe(true)
  fly(60)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  return setup
}

/**
 * The hover procedure over the sighting, activated and executed on FMS 1 (and so on FMS 2, in SYNC). FMS 2 receives the
 * one AFCS's words while FMS 1 guides (#1537), so it raises its own transition request at its TDN.
 */
function hoverProcedure() {
  const setup = mission()
  setup.fly(1)
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) setup.one.press(key)
  expect(setup.two.hover.status).toBe('ACT')
  return setup
}

for (const [from, to] of [[1, 2], [2, 1]] as const) {
  test(`in HOV, FMS ${from} to FMS ${to}: the aircraft holds where it is, flown on, and TU is engaged on the new computer`, () => {
    const { system, one, fly, annunciated } = hovering(from)
    const before = { position: { ...one.truePosition }, tas: system.simulator.tas }
    expect(system.simulator.iasReliable).toBe(false)
    system.selectGuidance(to)
    expect(system.guidanceSide).toBe(to)
    // The next step flies the one aircraft on: its air velocity and air data state are the same aircraft's.
    fly(0.25)
    expect(Math.abs(system.simulator.tas - before.tas), 'air velocity continuous, kt').toBeLessThanOrEqual(STEP_KT)
    expect(system.simulator.iasReliable).toBe(false)
    // The one AFCS: still HOV, re-datumed at the present position, with nothing to re-engage.
    expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
    expect(system.simulator.hoverCaptured).toBe(true)
    expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('HOV at the present position') })
    expect(annunciated().last.detail).not.toContain('re-engage')
    fly(30)
    expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
    expect(metres(one.truePosition, before.position), 'held over the same spot, m').toBeLessThan(15)
    // Re-engagement on the new computer: TU departs on its feedback.
    expect(system.simulator.engageTransitionUp()).toBe(true)
    fly(90)
    // Through the departure to coordinated flight.
    expect(system.simulator.inLowSpeedRegime).toBe(false)
    expect(system.simulator.indicatedAirspeed).toBeGreaterThan(45)
  })
}

test('the TDN transition flying TD/H to MRK is cancelled on a source change: HOV where it is, annunciated, MRK not flown to', () => {
  const { system, one, fly, flyUntil, annunciated } = hoverProcedure()
  const mrk = one.hover.active!.mark.position
  flyUntil(() => system.simulator.axisModes.pitch === 'TD/H', 400)
  fly(5)
  const transition = system.simulator.transitionInProgress
  expect(transition).not.toBeNull()
  const at = { ...one.truePosition }, toMrk = metres(at, mrk)
  system.selectGuidance(2)
  expect(system.simulator.transitionInProgress).toBeNull()
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  const switched = system.simulator.modeEvents.length
  expect(system.simulator.modeEvents.at(-2)).toMatchObject({ event: 'TD/H CANCELLED', detail: expect.stringContaining('HOV where it is') })
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringMatching(/TD\/H to MRK cancelled, HOV at the present position.*re-engage on the new computer/) })
  expect(annunciated().degraded.pitch).toContain('TD/H')
  fly(180)
  // HOV recovered to where the transition was cancelled; it never resumed the transition to MRK.
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  expect(metres(one.truePosition, at), 'holding where it was cancelled, m').toBeLessThan(50)
  expect(metres(one.truePosition, mrk)).toBeGreaterThan(toMrk - 100)
  expect(system.simulator.modeEvents.slice(switched).map(e => e.event)).not.toContain('TD/H')
  // Re-engagement on the new computer: the crew's TD/H stops and holds where it stops.
  expect(system.simulator.engageTransitionDownToHover()).toBe(true)
  fly(60)
  expect(system.simulator.axisModes.pitch).toBe('HOV')
})

test('TD with TD/H to MRK armed is disarmed on a source change: TD goes on to the gate, and TD/H does not follow', () => {
  const { system, flyUntil, fly, annunciated } = hoverProcedure()
  flyUntil(() => system.simulator.axisArmed.pitch.includes('TD/H'), 400)
  fly(3)
  expect(system.simulator.axisModes.collective).toBe('TD')
  system.selectGuidance(2)
  expect(system.simulator.axisArmed.pitch).toEqual([])
  expect(system.simulator.transitionInProgress).toBeNull()
  expect(system.simulator.axisModes.collective).toBe('TD')
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('TD/H to MRK disarmed') })
  expect(annunciated().degraded.pitch).toContain('TD/H')
  const switched = system.simulator.modeEvents.length
  fly(120)
  expect(system.simulator.axisModes.collective).toBe('RHT')
  expect(system.simulator.inLowSpeedRegime).toBe(false)
  expect(system.simulator.modeEvents.slice(switched).map(e => e.event)).not.toContain('TD/H')
})

test('the hover join is cancelled on a source change: HDG HOLD, annunciated, until NAV is re-engaged on the new computer', () => {
  const { system, one, fly, annunciated } = hoverProcedure()
  fly(20)
  expect(one.activeRoute.legs[0]).toMatchObject({ ident: 'JN' })
  expect(system.simulator.axisModes.roll).toBe('NAV')
  system.selectGuidance(2)
  expect(system.simulator.axisModes.roll).toBe('HDG')
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('hover join cancelled, HDG HOLD') })
  expect(annunciated().degraded.roll).toContain('NAV')
  const heading = one.heading
  fly(20)
  expect(system.simulator.axisModes.roll).toBe('HDG')
  expect(Math.abs(((one.heading - heading + 540) % 360) - 180)).toBeLessThan(2)
  // Re-engaged on the new computer, NAV flies its join on to JN.
  system.simulator.armLnav()
  for (let i = 0; i < 600 && one.activeRoute.legs[0]?.kind === 'wpt' && (one.activeRoute.legs[0] as { ident: string }).ident === 'JN'; i++) fly(1)
  expect(one.activeRoute.legs[0]).toMatchObject({ ident: 'TDN' })
})

test('HOV without hover feedback from the new computer becomes ATT at the change, annunciated; HOV is re-engaged when feedback returns', () => {
  const { system, one, fly, flyUntil, annunciated } = hovering(2)
  const tas = system.simulator.tas
  one.setCondition('gpsLost', true)
  expect(one.hoverFeedback).toBeNull()
  system.selectGuidance(1)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'ATT', roll: 'ATT' })
  expect(system.simulator.modeEvents.at(-2)).toMatchObject({ event: 'HOV LOST', detail: expect.stringContaining('FMS source changed') })
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('HOV LOST, ATT; re-engage on the new computer') })
  expect(annunciated().degraded).toMatchObject({ pitch: ['HOV'], roll: ['HOV'] })
  fly(0.25)
  expect(Math.abs(system.simulator.tas - tas), 'ATT holds the air-velocity command, kt').toBeLessThanOrEqual(STEP_KT)
  one.setCondition('gpsLost', false)
  flyUntil(() => one.hoverFeedback !== null, 120)
  fly(5)
  // No automatic re-engagement: the crew engages HOV again on the new computer.
  expect(system.simulator.axisModes.pitch).toBe('ATT')
  expect(system.simulator.engageHover()).toBe(true)
  fly(30)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
})

test('ATT after the feedback was lost stays ATT across a source change, on the same air velocity', () => {
  const { system, one, fly, flyUntil, annunciated } = hovering(1)
  one.setCondition('gpsLost', true)
  flyUntil(() => system.simulator.axisModes.pitch === 'ATT', 10)
  fly(2)
  const tas = system.simulator.tas, groundSpeed = one.groundSpeed
  system.selectGuidance(2)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'ATT', roll: 'ATT' })
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED' })
  fly(0.25)
  expect(Math.abs(system.simulator.tas - tas)).toBeLessThanOrEqual(STEP_KT)
  expect(Math.abs(one.groundSpeed - groundSpeed)).toBeLessThanOrEqual(STEP_KT)
  fly(30)
  expect(system.simulator.axisModes.pitch).toBe('ATT')
})

// D4 6.2a (R3-02) at the change: the new computer's feedback is judged once, against the AFCS's last sample.
test('across receivers, feedback beyond the continuity limit is lost at the change: ATT, annunciated, never HOV first', () => {
  const { system, two, fly, annunciated } = hovering(1, true)
  stimulusFor(system.computers[0]).setSpoof(1, { northM: 29.5, driftEastMps: 0 })
  fly(0.25)
  expect(two.hoverFeedback?.source).toBe(2)
  system.selectGuidance(2)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'ATT', roll: 'ATT' })
  expect(system.simulator.modeEvents.at(-2)).toMatchObject({ event: 'HOV LOST', detail: expect.stringMatching(/FMS source changed: GPS2 position \d+\.\d m from the last sample/) })
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('HOV LOST, ATT; re-engage on the new computer') })
  fly(5)
  expect(system.simulator.axisModes.pitch).toBe('ATT')
})

test('across receivers within the continuity limit, HOV re-datums on the new receiver and is not judged again', () => {
  const { system, one, two, fly, annunciated } = hovering(1, true)
  expect(two.hoverFeedback?.source).toBe(2)
  const at = { ...one.truePosition }
  system.selectGuidance(2)
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('HOV at the present position') })
  fly(30)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  expect(system.simulator.modeEvents.map(e => e.event)).not.toContain('HOV LOST')
  expect(metres(one.truePosition, at)).toBeLessThan(15)
})

test('a TD/H to MRK cancelled across receivers beyond the continuity limit goes to ATT, not HOV', () => {
  const { system, one, two, fly, flyUntil, annunciated } = hoverProcedure()
  flyUntil(() => system.simulator.axisModes.pitch === 'TD/H', 400)
  system.setLinkAvailable(false); one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
  stimulusFor(one).setSpoof(1, { northM: 29.5, driftEastMps: 0 })
  fly(1)
  expect(system.simulator.axisModes.pitch).toBe('TD/H')
  expect(two.hoverFeedback?.source).toBe(2)
  system.selectGuidance(2)
  expect(system.simulator.axisModes.pitch).toBe('ATT')
  expect(system.simulator.modeEvents.at(-2)).toMatchObject({ event: 'TD/H CANCELLED', detail: expect.stringContaining('ATT: GPS2 position') })
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('TD/H to MRK cancelled, HOV LOST, ATT') })
})

test('TU with its lateral hold lost keeps LVL lost across a source change: no automatic re-engagement on the new feedback', () => {
  const { system, one, two, fly, flyUntil } = hovering(1, true)
  expect(system.simulator.engageTransitionUp()).toBe(true)
  stimulusFor(one).setFault(0, 'receiver', true)
  flyUntil(() => system.simulator.axisModes.roll === 'ATT', 5)
  expect(two.hoverFeedback).not.toBeNull()
  system.selectGuidance(2)
  fly(2)
  expect(system.simulator.axisModes).toMatchObject({ pitch: 'TU', roll: 'ATT' })
})

test('the one aircraft flies on unchanged: a switched run matches an unswitched twin when the FMS air data disagrees with it', () => {
  const runs = [mission(), mission()]
  for (const run of runs) {
    run.fly(10)
    // The FMS's own TAS (from its wind) now differs from the aircraft's: the attitude lost, the real wind changed.
    run.one.setCondition('apirsFail', true); run.system.tick()
    Object.assign(run.one.wind, { direction: 100, speed: 45 }); run.system.tick()
  }
  const [switched, twin] = runs
  expect(Math.abs((switched.one.trueAirspeed ?? 0) - switched.system.simulator.tas)).toBeGreaterThan(20)
  switched.system.selectGuidance(2)
  for (const seconds of [0.25, 5]) {
    for (const run of runs) run.fly(seconds)
    expect(Math.abs(switched.system.simulator.tas - twin.system.simulator.tas), 'TAS, kt').toBeLessThan(1e-6)
    expect(Math.abs(switched.system.simulator.bankAngle - twin.system.simulator.bankAngle), 'bank, °').toBeLessThan(1e-6)
    expect(metres(switched.one.truePosition, twin.one.truePosition), 'position, m').toBeLessThan(0.01)
  }
})

test('a transition request the new computer raised in the step before the change is not engaged by it', () => {
  const { system, one, two, fly, flyUntil } = hoverProcedure()
  flyUntil(() => two.hover.request > 0, 400)
  expect(one.hover.request).toBe(0)
  expect(system.simulator.transitionInProgress).toBeNull()
  system.selectGuidance(2)
  const switched = system.simulator.modeEvents.length
  fly(20)
  expect(system.simulator.modeEvents.slice(switched).map(e => e.event)).not.toContain('TRANSITION REQUEST')
  expect(system.simulator.axisModes.collective).not.toBe('TD')
})

test('NAV armed toward the hover join is disarmed on a source change, as an armed approach is', () => {
  const { system, one, fly, annunciated } = hoverProcedure()
  fly(20)
  expect(one.activeRoute.legs[0]).toMatchObject({ ident: 'JN' })
  system.simulator.selectHeading(one.heading)
  system.simulator.armLnav()
  expect(system.simulator.lnavIsArmed).toBe(true)
  system.selectGuidance(2)
  expect(system.simulator.lnavIsArmed).toBe(false)
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('NAV to the hover join disarmed') })
  fly(30)
  expect(system.simulator.axisModes.roll).toBe('HDG')
})
