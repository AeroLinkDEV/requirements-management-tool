import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Guidance validity and mode authority (independent review findings R02 and R10, with the laboratory reversion
// agreed as Q-A2): on FMS failure managed guidance stops being flown, basic heading and altitude hold fly references
// latched at the failure, the aircraft keeps moving under its own dynamics, recovery resumes nothing by itself, and
// the vertical mode is the controller branch that commands the aircraft, not a reading of its vertical speed.
const setup = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); if (each?.()) return } }
  return { unit, sim, fly }
}
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }

test('on FMS failure the aircraft flies latched heading and altitude, keeps moving, and stops sequencing (R02)', () => {
  const { unit, sim, fly } = setup()
  fly(30)
  expect(sim.lateralMode).toBe('LNAV')
  const track = unit.track, altitude = unit.altitude, leg = active(unit), start = unit.truePosition
  unit.setCondition('fmsFail', true)
  fly(1)
  expect(sim.lateralMode).toBe('HDG')
  expect(sim.guidance.mode).toBe('HDG')
  expect(sim.guidance.desiredTrack).toBeNull()
  expect(sim.verticalMode).toBe('ALT HOLD')
  expect(sim.selectedHeading).toBe(Math.round(track))
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
  const { unit, sim, fly } = setup()
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
  const { unit, sim, fly } = setup()
  fly(3 * 3600, () => unit.profile().descending)
  fly(30)
  unit.setAircraft({ altitude: unit.altitude - 800 })
  fly(5)
  expect(sim.verticalMode).toBe('VNAV PTH')
  expect(Math.abs(unit.verticalSpeed)).toBeLessThan(10)
  // A failure while descending: altitude hold captures its latched altitude with a transient, still ALT HOLD.
  const descending = setup()
  descending.unit.press('LEGS')
  descending.fly(1)
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
})


// Approach authority (finding R03, with the laboratory contract agreed as Q-A1): beyond the final approach fix only a
// captured approach mode descends toward the runway. Capture needs the approach armed, valid approach capability,
// LNAV engaged and the aircraft established on the final leg. Loss of integrity after capture drops the approach mode
// to a latched altitude hold, and restoring integrity does not re-capture by itself.
const onFinal = (unit: ScriptedFms) => active(unit) === 'RW24R'
const approachSetup = (arm: boolean) => {
  const run = setup()
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
