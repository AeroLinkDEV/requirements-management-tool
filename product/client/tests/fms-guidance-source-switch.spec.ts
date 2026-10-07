import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import type { FmsSide } from '../src/fmsCdu/crossTalk'
import { distanceNm, type LatLon } from '../src/fmsCdu/fmsModel'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { setUp87nOffshoreSar } from '../src/fmsCdu/heliDemo'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'

// #1508: changing the FMS guidance source in the low-speed regime (#1502 §19 item 2, decided by Sean on 6 Oct 2026),
// with the edge cases Q1-Q4 he decided on 7 Oct (#1546). One aircraft and one AFCS: the aircraft's motion carries on
// unchanged; the AFCS drops only what it coupled from the FMS (the MRK target, NAV on the hover join and the TDN-MRK
// leg), keeps the modes it flies on its own (TD/H's own deceleration, the crew's hover point), falls to ATT without
// continuous hover feedback, annunciates the change and is re-engaged by the crew against the new computer.
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
 * Slowed to hover speed and HOV engaged on the given computer, then flown for 60 s, by when it is closing the last
 * 20 m or so to its hover point at about 2 kt. `point`: where HOV was engaged, the crew's hover point. With
 * `ownReceivers`, the computers run INDEPENDENT, FMS 1 on GPS 1 and FMS 2 on GPS 2, so each measures the hover on its
 * own receiver.
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
  const point = { ...one.truePosition }
  fly(60)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  return { ...setup, point }
}

/**
 * The hover procedure over the sighting, activated and executed on FMS 1 (and so on FMS 2, in SYNC). FMS 2 is primed
 * first (selected for 1 s): a computer that has never guided has no AFCS words and refuses TDN, so without it FMS 2
 * would raise no transition request of its own.
 */
function hoverProcedure() {
  const setup = mission()
  setup.system.selectGuidance(2); setup.fly(1); setup.system.selectGuidance(1)
  setup.fly(1)
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) setup.one.press(key)
  expect(setup.two.hover.status).toBe('ACT')
  return setup
}

for (const [from, to] of [[1, 2], [2, 1]] as const) {
  test(`in HOV, FMS ${from} to FMS ${to}: the aircraft holds the crew's hover point, flown on, and TU is engaged on the new computer`, () => {
    const { system, one, fly, annunciated, point } = hovering(from)
    const before = { position: { ...one.truePosition }, tas: system.simulator.tas }
    expect(system.simulator.iasReliable).toBe(false)
    system.selectGuidance(to)
    expect(system.guidanceSide).toBe(to)
    // The next step flies the one aircraft on: its air velocity and air data state are the same aircraft's.
    fly(0.25)
    expect(Math.abs(system.simulator.tas - before.tas), 'air velocity continuous, kt').toBeLessThanOrEqual(STEP_KT)
    expect(system.simulator.iasReliable).toBe(false)
    // The one AFCS: still HOV on the crew's hover point (Q2), with nothing to re-engage.
    expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
    expect(system.simulator.hoverCaptured).toBe(true)
    expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('HOV point kept') })
    expect(annunciated().last.detail).not.toContain('re-engage')
    fly(30)
    expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
    expect(metres(one.truePosition, point), 'over the crew hover point, m').toBeLessThan(15)
    // Re-engagement on the new computer: TU departs on its feedback.
    expect(system.simulator.engageTransitionUp()).toBe(true)
    fly(90)
    // Through the departure to coordinated flight.
    expect(system.simulator.inLowSpeedRegime).toBe(false)
    expect(system.simulator.indicatedAirspeed).toBeGreaterThan(45)
  })
}

// Q1 (#1546): the AFCS flies the deceleration (802p 11-18); the source change takes away only the FMS's MRK target.
test('Q1: TD/H to MRK at the gate keeps its own deceleration across a source change: no faster than 1.25 kt/s, HOV where it stops, MRK dropped', () => {
  const { system, one, fly, flyUntil, annunciated } = hoverProcedure()
  const mrk = one.hover.active!.mark.position
  flyUntil(() => system.simulator.axisModes.pitch === 'TD/H', 400)
  fly(1)
  // Still in the gate segment, at about the gate speed.
  expect(system.simulator.axisModes.collective).toBe('RHT')
  expect(system.simulator.transitionInProgress).not.toBeNull()
  expect(one.groundSpeed).toBeGreaterThan(50)
  system.selectGuidance(2)
  expect(system.simulator.transitionInProgress).toBeNull()
  expect(system.simulator.axisModes).toEqual({ collective: 'TD/H', pitch: 'TD/H', roll: 'TD/H' })
  expect(system.simulator.modeEvents.at(-2)).toMatchObject({ event: 'MRK DECOUPLED' })
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringMatching(/TD\/H to MRK decoupled, HOV where it stops.*re-engage on the new computer/) })
  // TD/H itself is still engaged: nothing amber on pitch or roll.
  expect(annunciated().degraded).toMatchObject({ pitch: [], roll: [] })
  const switched = system.simulator.modeEvents.length
  let previous = one.groundSpeed, fastest = 0
  // Every second until the aircraft has stopped, whatever mode flies it.
  for (let s = 0; s < 240 && (system.simulator.axisModes.pitch === 'TD/H' || one.groundSpeed > 1); s++) {
    fly(1)
    fastest = Math.max(fastest, previous - one.groundSpeed); previous = one.groundSpeed
  }
  // TD/H's own law: 0.75 kt/s nominal, never more than its 1.25 kt/s bound (the HOV re-datum braked at 2 kt/s).
  expect(fastest, 'deceleration, kt/s').toBeLessThanOrEqual(1.25)
  expect(system.simulator.axisModes.pitch).toBe('HOV')
  const stopped = { ...one.truePosition }
  expect(system.simulator.modeEvents.slice(switched)).toContainEqual(expect.objectContaining({ event: 'HOV', detail: 'holding where it stopped' }))
  fly(60)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  expect(metres(one.truePosition, stopped), 'overshoot past the stop, m').toBeLessThan(10)
  // MRK is no longer the target: it stopped short of it on the nominal deceleration and stays there.
  expect(metres(one.truePosition, mrk), 'from MRK, m').toBeGreaterThan(300)
  expect(system.simulator.hoverCaptured).toBe(true)
})

test("Q1 across receivers at the gate: a receiver offset within the continuity limit keeps TD/H; the aircraft's own motion is not taken for a jump", () => {
  const { system, one, two, fly, flyUntil } = hoverProcedure()
  flyUntil(() => system.simulator.axisModes.pitch === 'TD/H', 400)
  system.setLinkAvailable(false); one.selectGpsReceiver('GPS1'); two.selectGpsReceiver('GPS2')
  stimulusFor(one).setSpoof(1, { northM: -4, driftEastMps: 0 })
  fly(1)
  expect(system.simulator.axisModes.collective).toBe('RHT')
  expect(two.hoverFeedback?.source).toBe(2)
  // At the gate speed the aircraft covers about 8 m in a 0.25 s step: more than the limit less the 4 m offset.
  expect(one.groundSpeed * 0.25 * 1852 / 3600).toBeGreaterThan(HELICOPTER_PROFILE.parameters.hoverTransferPosition.value - 4)
  system.selectGuidance(2)
  expect(system.simulator.axisModes).toEqual({ collective: 'TD/H', pitch: 'TD/H', roll: 'TD/H' })
  expect(system.simulator.modeEvents.map(e => e.event)).not.toContain('HOV LOST')
})

// Q3 (#1546): the TDN-MRK leg is part of the transition, so NAV on it goes as NAV on the hover join does.
test('TD with TD/H to MRK armed is disarmed on a source change: TD goes on to the gate, NAV on the TDN-MRK leg gives way to HDG HOLD (Q3)', () => {
  const { system, one, flyUntil, fly, annunciated } = hoverProcedure()
  flyUntil(() => system.simulator.axisArmed.pitch.includes('TD/H'), 400)
  fly(3)
  expect(system.simulator.axisModes).toMatchObject({ collective: 'TD', roll: 'NAV' })
  expect(one.activeRoute.legs[0]).toMatchObject({ ident: 'MRK' })
  system.selectGuidance(2)
  expect(system.simulator.axisArmed.pitch).toEqual([])
  expect(system.simulator.transitionInProgress).toBeNull()
  expect(system.simulator.axisModes).toMatchObject({ collective: 'TD', roll: 'HDG' })
  expect(system.simulator.modeEvents.at(-2)).toMatchObject({ event: 'NAV CANCELLED', detail: expect.stringContaining('the TDN-MRK leg is part of the transition: HDG HOLD') })
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringMatching(/TD\/H to MRK disarmed; TDN-MRK leg cancelled, HDG HOLD; re-engage on the new computer/) })
  expect(annunciated().degraded).toMatchObject({ pitch: ['TD/H'], roll: ['NAV', 'TD/H'] })
  const switched = system.simulator.modeEvents.length, heading = one.heading
  fly(120)
  expect(system.simulator.axisModes).toMatchObject({ collective: 'RHT', roll: 'HDG' })
  expect(system.simulator.inLowSpeedRegime).toBe(false)
  expect(Math.abs(((one.heading - heading + 540) % 360) - 180), 'heading held, °').toBeLessThan(2)
  // Neither TD/H nor NAV comes back by itself, so MRK is never overflown under NAV to an LNAV LOST.
  expect(system.simulator.modeEvents.slice(switched).map(e => e.event)).not.toContain('TD/H')
  expect(system.simulator.modeEvents.slice(switched).map(e => e.event)).not.toContain('LNAV LOST')
})

// Q4 (#1546): the crew gets a fresh MRK coupling by running TDN again on the new computer.
test('Q4: after the source change, a fresh hover procedure on the new computer re-couples to its new MRK through TDN', () => {
  const { system, one, two, flyUntil, fly } = hoverProcedure()
  const oldMrk = one.hover.active!.mark.position
  flyUntil(() => system.simulator.axisArmed.pitch.includes('TD/H'), 400)
  fly(3)
  system.selectGuidance(2)
  expect(system.simulator.axisModes.roll).toBe('HDG')
  fly(5)
  for (const key of ['TACT', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC'] as const) two.press(key)
  expect(two.hover.status).toBe('ACT')
  const mrk = two.hover.active!.mark.position
  expect(metres(mrk, oldMrk), 'a new MRK, m').toBeGreaterThan(500)
  const switched = system.simulator.modeEvents.length
  system.simulator.armLnav()
  flyUntil(() => system.simulator.transitionInProgress !== null, 600)
  expect(system.simulator.modeEvents.slice(switched).map(e => e.event)).toContain('TRANSITION REQUEST')
  flyUntil(() => system.simulator.axisModes.pitch === 'HOV', 600)
  fly(30)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  expect(system.simulator.hoverCaptured).toBe(true)
  expect(metres(one.truePosition, mrk), 'at the new MRK, m').toBeLessThan(10)
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

test('across receivers within the continuity limit, HOV keeps its point on the new receiver and is not judged again', () => {
  const { system, one, two, fly, annunciated, point } = hovering(1, true)
  expect(two.hoverFeedback?.source).toBe(2)
  system.selectGuidance(2)
  expect(annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringContaining('HOV point kept') })
  fly(30)
  expect(system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
  expect(system.simulator.modeEvents.map(e => e.event)).not.toContain('HOV LOST')
  expect(metres(one.truePosition, point), 'over the crew hover point, m').toBeLessThan(15)
})

// Q2 (#1546): the hover point is the crew's, not the FMS's. A crew HOV still settling toward it keeps it across the
// change, shifted only by the offset between the computers, so the aircraft ends where an unswitched twin does.
for (const offsetM of [0, 6]) {
  test(`Q2: a crew HOV still settling keeps its hover point in the physical world across a source change (receiver offset ${offsetM} m)`, () => {
    const settling = () => {
      const run = mission()
      run.fly(1)
      // With an offset: INDEPENDENT, FMS 1 on GPS 1 and FMS 2 on GPS 2, GPS 2 reading north of the truth, within the
      // continuity limit.
      if (offsetM) { run.system.setLinkAvailable(false); run.one.selectGpsReceiver('GPS1'); run.two.selectGpsReceiver('GPS2'); stimulusFor(run.one).setSpoof(1, { northM: offsetM, driftEastMps: 0 }) }
      run.system.simulator.selectSpeed(20)
      run.flyUntil(() => run.system.simulator.tas < 26, 120)
      expect(run.system.simulator.engageHover()).toBe(true)
      run.fly(4)
      return run
    }
    const [switched, twin] = [settling(), settling()]
    const at = { ...switched.one.truePosition }
    expect(switched.one.groundSpeed, 'still settling, kt').toBeGreaterThan(10)
    if (offsetM) expect(switched.two.hoverFeedback?.source).toBe(2)
    switched.system.selectGuidance(2)
    expect(switched.system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
    expect(switched.annunciated().last).toMatchObject({ event: 'FMS SOURCE CHANGED', detail: expect.stringMatching(offsetM ? /HOV point kept, shifted (5|6)\.\d m for the new computer/ : /HOV point kept$/) })
    for (const run of [switched, twin]) run.fly(120)
    expect(switched.system.simulator.axisModes).toEqual({ collective: 'RHT', pitch: 'HOV', roll: 'HOV' })
    // The case discriminates: the hover point is far from where the aircraft was at the change.
    expect(metres(twin.one.truePosition, at), 'twin hover point from the switch position, m').toBeGreaterThan(30)
    expect(metres(switched.one.truePosition, twin.one.truePosition), 'switched against unswitched hover point, m').toBeLessThan(1)
  })
}

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
