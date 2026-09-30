import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { LAB_AIRLINE_VNAV_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Guidance validity and mode authority (independent review findings R02 and R10, with the laboratory reversion
// agreed as Q-A2): on FMS failure managed guidance stops being flown, basic heading and altitude hold fly references
// latched at the failure, the aircraft keeps moving under its own dynamics, recovery resumes nothing by itself, and
// the vertical mode is the controller branch that commands the aircraft, not a reading of its vertical speed.
const setup = (profile?: AircraftProfile) => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now), { profile })
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); if (each?.()) return } }
  return { unit, sim, fly }
}
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }

test('on FMS failure the aircraft flies latched heading and altitude, keeps moving, and stops sequencing (R02)', () => {
  const { unit, sim, fly } = setup()
  fly(30)
  expect(sim.lateralMode).toBe('LNAV')
  const flownHeading = unit.heading, altitude = unit.altitude, leg = active(unit), start = unit.truePosition
  unit.setCondition('fmsFail', true)
  fly(1)
  expect(sim.lateralMode).toBe('HDG')
  expect(sim.guidance.mode).toBe('HDG')
  expect(sim.guidance.desiredTrack).toBeNull()
  expect(sim.verticalMode).toBe('ALT HOLD')
  // The heading the aircraft was flying is latched (not its track, which differs by the drift).
  expect(sim.selectedHeading).toBe(Math.round(flownHeading))
  expect(sim.altitudeHoldReference).toBe(Math.round(altitude))
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'FMS FAILURE', detail: expect.stringContaining(`ALT HOLD ${Math.round(altitude)} FT`) })
  // The references are latched: flying on does not re-latch them to the changing aircraft state.
  const heading = sim.selectedHeading
  fly(600)
  expect(sim.selectedHeading).toBe(heading)
  expect(sim.altitudeHoldReference).toBe(Math.round(altitude))
  expect(Math.abs(unit.altitude - Math.round(altitude))).toBeLessThan(20)
  // It kept flying (10 minutes at about 2 NM a minute) but did not sequence the route.
  expect(distanceNm(start, unit.truePosition)).toBeGreaterThan(15)
  expect(active(unit)).toBe(leg)
})

test('recovery resumes nothing by itself; LNAV and VNAV must be selected again (R02)', () => {
  const { unit, sim, fly } = setup(LAB_AIRLINE_VNAV_PROFILE)
  fly(30)
  unit.setCondition('fmsFail', true)
  fly(5)
  // Managed modes cannot be selected while the FMS has failed.
  sim.armLnav()
  expect(sim.lnavIsArmed).toBe(false)
  expect(sim.engageVnav()).toBe(false)
  unit.setCondition('fmsFail', false)
  fly(5)
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'FMS RECOVERED' })
  expect(sim.lateralMode).toBe('HDG')
  expect(sim.verticalMode).toBe('ALT HOLD')
  expect(sim.engageVnav()).toBe(true)
  fly(1)
  expect(sim.verticalMode).not.toBe('ALT HOLD')
  sim.armLnav()
  expect(sim.lnavIsArmed).toBe(true)
})

test('the vertical mode is the commanding branch: level in path mode and moving in altitude hold both occur (R10)', () => {
  // Below the descent path the aircraft holds its altitude in VNAV PTH: level, in a path mode.
  const { unit, sim, fly } = setup(LAB_AIRLINE_VNAV_PROFILE)
  fly(3 * 3600, () => unit.profile().descending)
  fly(30)
  unit.setAircraft({ altitude: unit.altitude - 800 })
  fly(5)
  expect(sim.verticalMode).toBe('VNAV PTH')
  expect(Math.abs(unit.verticalSpeed)).toBeLessThan(10)
  // A failure while descending: altitude hold captures its latched altitude with a transient, still ALT HOLD.
  const descending = setup(LAB_AIRLINE_VNAV_PROFILE)
  descending.unit.press('LEGS')
  // DES NOW descends at 1000 fpm to the planned altitude at the active fix, so it needs one below the aircraft: the
  // leg, at cruise, into the first descent constraint.
  descending.fly(3 * 3600, () => descending.unit.altitude > 4490 && descending.unit.profile().points[0].altitude! < 4000)
  descending.unit.vnav.desNow = true
  descending.fly(60, () => descending.unit.verticalSpeed <= -990)
  expect(descending.unit.verticalSpeed).toBeLessThanOrEqual(-990)
  descending.unit.setCondition('fmsFail', true)
  descending.fly(1)
  expect(descending.sim.verticalMode).toBe('ALT HOLD')
  expect(Math.abs(descending.unit.verticalSpeed)).toBeGreaterThan(1)
})

test('LNAV with no leg to fly is lost to heading hold, and the loss is recorded (R10)', () => {
  const { unit, sim, fly } = setup()
  unit.press('LEGS')
  for (const ch of 'ELIBA') unit.press(`CHAR_${ch}`)
  unit.press('LSK1L')
  unit.press('EXEC')
  fly(3600, () => sim.lateralMode === 'HDG')
  expect(unit.activeRoute.legs[0]).toMatchObject({ kind: 'disco' })
  expect(sim.lateralMode).toBe('HDG')
  expect(sim.modeEvents.some(e => e.event === 'LNAV LOST')).toBe(true)
  // The FMA shows NAV lost in amber on the roll axis (B4.1).
  expect(sim.modeEvents.find(e => e.event === 'LNAV LOST')?.lost).toEqual([{ axis: 'roll', mode: 'NAV' }])
})


// Approach authority (finding R03, with the laboratory contract agreed as Q-A1): beyond the final approach fix only a
// captured approach mode descends toward the runway. Capture needs the approach armed, valid approach capability,
// LNAV engaged and the aircraft established on the final leg. Loss of integrity after capture drops the approach mode
// to a latched altitude hold, and restoring integrity does not re-capture by itself.
const onFinal = (unit: ScriptedFms) => active(unit) === 'RW24R'
const approachSetup = (arm: boolean, profile?: AircraftProfile) => {
  const run = setup(profile)
  run.unit.selectProcedure('APPROACH', 'R24R')
  run.unit.press('EXEC')
  if (arm) run.unit.armApproach(true)
  run.fly(3 * 3600, () => onFinal(run.unit))
  expect(onFinal(run.unit)).toBe(true)
  return run
}

test('armed with valid capability, the approach captures on final and descends on its path (R03)', () => {
  const { unit, sim, fly } = approachSetup(true)
  let captured = false, descended = false
  fly(600, () => {
    if (sim.approachMode === 'CAPTURED') captured = true
    if (sim.verticalMode === 'APPR' && unit.verticalSpeed < -300) descended = true
    return !onFinal(unit)
  })
  expect(captured).toBe(true)
  expect(descended).toBe(true)
  expect(sim.modeEvents.some(e => e.event === 'APPR CAPTURED')).toBe(true)
})

test('unarmed, the aircraft does not descend below the FAF altitude on final (R03)', () => {
  const { unit, sim, fly } = approachSetup(false)
  const faf = unit.fafAltitudeCorrected
  let lowest = Infinity
  fly(600, () => { lowest = Math.min(lowest, unit.altitude); expect(sim.approachMode).not.toBe('CAPTURED'); return !onFinal(unit) })
  expect(lowest).toBeGreaterThan(faf - 60)
})

test('without approach integrity the armed approach does not capture (R03)', () => {
  const run = setup()
  run.unit.selectProcedure('APPROACH', 'R24R')
  run.unit.press('EXEC')
  run.unit.armApproach(true)
  run.unit.setCondition('gpsIntegrity', true)
  run.fly(3 * 3600, () => onFinal(run.unit))
  const faf = run.unit.fafAltitudeCorrected
  let lowest = Infinity
  run.fly(600, () => { lowest = Math.min(lowest, run.unit.altitude); expect(run.sim.approachMode).not.toBe('CAPTURED'); return !onFinal(run.unit) })
  expect(lowest).toBeGreaterThan(faf - 60)
})

test('integrity lost after capture drops to altitude hold; restoring it does not re-capture (R03)', () => {
  const { unit, sim, fly } = approachSetup(true)
  fly(600, () => sim.approachMode === 'CAPTURED' && unit.verticalSpeed < -300)
  expect(sim.approachMode).toBe('CAPTURED')
  unit.setCondition('gpsIntegrity', true)
  fly(2)
  expect(sim.approachMode).toBe('OFF')
  expect(sim.verticalMode).toBe('ALT HOLD')
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'APPR LOST' })
  const held = sim.altitudeHoldReference!
  unit.setCondition('gpsIntegrity', false)
  fly(20)
  expect(sim.approachMode).toBe('OFF')
  expect(Math.abs(unit.altitude - held)).toBeLessThan(40)
})

// The laboratory go-around and the approach exits (third review D02 and D03): an accepted TOGA leaves the approach the
// same way however the approach ended, and the commanded target is always the one the controlling authority flies.
const capturedApproach = (profile?: AircraftProfile) => {
  const run = approachSetup(true, profile)
  run.fly(600, () => run.sim.approachMode === 'CAPTURED' && run.unit.verticalSpeed < -300)
  expect(run.sim.approachMode).toBe('CAPTURED')
  return run
}

test('TOGA climbs on the missed approach whether the approach was captured or had lost its integrity (third review D02)', () => {
  // The degraded case first: it is the one that went wrong (the normal go-around already climbed).
  for (const lostIntegrity of [true, false]) {
    const { unit, sim, fly } = capturedApproach(LAB_AIRLINE_VNAV_PROFILE)
    if (lostIntegrity) {
      unit.setCondition('gpsIntegrity', true)
      fly(2)
      expect(sim.verticalMode).toBe('ALT HOLD')
      // In altitude hold the commanded target is the held altitude, not the planned one.
      expect(sim.guidance.targetAltitude).toBe(sim.altitudeHoldReference)
    }
    const from = unit.altitude
    expect(unit.goAround()).toBe(true)
    fly(60)
    const leg = unit.activeRoute.legs[0]
    expect(leg && leg.kind !== 'disco' ? leg.source : null).toBe('MISSED')
    expect(sim.altitudeHoldReference, `integrity lost: ${lostIntegrity}`).toBeNull()
    expect(sim.verticalMode).toBe('VNAV CLB')
    expect(sim.guidance.targetAltitude).toBe(3000)
    expect(unit.altitude - from).toBeGreaterThan(500)
    const goAround = sim.modeEvents.find(e => e.event === 'GO AROUND')!
    expect(goAround.detail).toContain('VNAV climbs on the missed approach')
    expect(goAround.detail.includes('released')).toBe(lostIntegrity)
  }
})

test('TOGA is refused while the FMS has failed, and the route is left as it was (third review D02)', () => {
  const { unit, sim, fly } = capturedApproach()
  unit.setCondition('fmsFail', true)
  fly(1)
  const legs = structuredClone(unit.activeRoute.legs)
  expect(unit.goAround()).toBe(false)
  expect(unit.activeRoute.legs).toEqual(legs)
  fly(2)
  expect(sim.modeEvents.some(e => e.event === 'GO AROUND')).toBe(false)
})

test('after capture, APPR pressed off or HDG SEL cancels the approach to an altitude hold at the altitude it had (third review D03)', () => {
  for (const how of ['APPR pressed off', 'HDG SEL']) {
    const { unit, sim, fly } = capturedApproach()
    if (how === 'HDG SEL') sim.selectHeading(240)
    else unit.armApproach(false)
    fly(2)
    expect(sim.approachMode, how).toBe('OFF')
    expect(sim.verticalMode).toBe('ALT HOLD')
    expect(unit.approachArmed).toBe(false)
    expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'APPR CANCELLED', detail: expect.stringContaining(how) })
    const held = sim.altitudeHoldReference!
    expect(sim.guidance.targetAltitude).toBe(held)
    fly(20)
    expect(Math.abs(unit.altitude - held)).toBeLessThan(40)
  }
})

test('a failure after TOGA is accepted keeps its hold, whatever the order within a step, and recovery keeps it (fourth review E01)', () => {
  // TOGA accepted, then the FMS fails before the next step: the failure's hold survives the go-around.
  {
    const { unit, sim, fly } = capturedApproach(LAB_AIRLINE_VNAV_PROFILE)
    expect(unit.goAround()).toBe(true)
    unit.setCondition('fmsFail', true)
    fly(1)
    const held = sim.altitudeHoldReference
    expect(held, 'TOGA then failure in one step').not.toBeNull()
    expect(sim.verticalMode).toBe('ALT HOLD')
    expect(sim.guidance.targetAltitude).toBe(held)
    const goAround = sim.modeEvents.find(e => e.event === 'GO AROUND')!
    expect(goAround.detail).toContain('not flown: FMS failed')
    expect(goAround.detail).not.toContain('released')
    // Recovery without reselecting LNAV and VNAV keeps the basic hold.
    unit.setCondition('fmsFail', false)
    fly(10)
    expect(sim.altitudeHoldReference).toBe(held)
    expect(sim.verticalMode).toBe('ALT HOLD')
  }
  // TOGA flown for a step, then the failure: the failure latches its hold as usual.
  {
    const { unit, sim, fly } = capturedApproach(LAB_AIRLINE_VNAV_PROFILE)
    expect(unit.goAround()).toBe(true)
    fly(1)
    expect(sim.altitudeHoldReference).toBeNull()
    unit.setCondition('fmsFail', true)
    fly(1)
    expect(sim.altitudeHoldReference).not.toBeNull()
    expect(sim.verticalMode).toBe('ALT HOLD')
  }
})

test('on the first step after an approach ends, the published target is the hold it flies (fourth review E02)', () => {
  for (const how of ['integrity lost', 'APPR pressed off', 'HDG SEL']) {
    const { unit, sim, fly } = capturedApproach()
    if (how === 'integrity lost') unit.setCondition('gpsIntegrity', true)
    else if (how === 'APPR pressed off') unit.armApproach(false)
    else sim.selectHeading(240)
    fly(1)
    const held = sim.altitudeHoldReference
    expect(held, how).not.toBeNull()
    expect(sim.verticalMode, how).toBe('ALT HOLD')
    expect(sim.guidance.targetAltitude, how).toBe(held)
    // Stable on the next step too.
    fly(1)
    expect(sim.guidance.targetAltitude, how).toBe(held)
  }
})

test('under the helicopter profile TOGA climbs in GA to the preselected altitude and captures it; VS and ALT are the crew modes (Stage B3)', () => {
  const { unit, sim, fly } = capturedApproach()
  // The approach captures at about 3,000 ft here: preselect the missed approach altitude above it.
  sim.selectAltitude(4000)
  const from = unit.altitude
  expect(unit.goAround()).toBe(true)
  fly(5)
  expect(sim.verticalMode).toBe('GA')
  expect(unit.verticalSpeed).toBeGreaterThan(700)
  fly(240, () => sim.verticalMode === 'ALT HOLD')
  expect(sim.verticalMode).toBe('ALT HOLD')
  expect(Math.abs(unit.altitude - 4000)).toBeLessThan(30)
  expect(unit.altitude).toBeGreaterThan(from)
  expect(sim.modeEvents.map(e => e.event)).toEqual(expect.arrayContaining(['GO AROUND', 'ALT CAPTURED']))
  // VS toward a lower preselection, then capture there; VNAV is not a helicopter mode.
  sim.selectAltitude(3500)
  expect(sim.engageVerticalSpeed(-500)).toBe(true)
  fly(3)
  expect(sim.verticalMode).toBe('VS')
  fly(200, () => sim.verticalMode === 'ALT HOLD')
  expect(Math.abs(unit.altitude - 3500)).toBeLessThan(30)
  expect(sim.engageVnav()).toBe(false)
})
