import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { RNP_DEFAULTS } from '../src/fmsCdu/navigation'
import { HELICOPTER_PROFILE, LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { BenchRadioReceiver, solveRadio } from '../src/fmsCdu/radioNavigation'
import { BufferedSensorPort, type RadioObservation, type SensorFrame } from '../src/fmsCdu/sensorPorts'
import type { Navaid } from '../src/fmsCdu/navData'
import { CivilNavigation } from '../src/fmsCdu/civilNavigation'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Civil measured navigation: S300 uncertain GPS and heading/TAS/last-computed-wind DR, measured radios, phase RNP,
// sensor-port freshness and predictive RAIM. Radio noise, age/acquisition limits and uncertainty are bench policy.
const setup = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => {
    for (let t = 0; t < seconds; t += 1) {
      now += 1000
      sim.step(1)
      if (each?.()) return t
    }
    return seconds
  }
  return { unit, sim, fly }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : `CHAR_${ch}`)
}
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trimEnd()
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : leg?.kind === 'cond' ? `(${leg.path})` : null }

test('an external sensor mailbox refuses a receiver replay even when its air-data packet is newer', () => {
  const template = new ScriptedFms().navigationInputs!
  const port = new BufferedSensorPort()
  expect(port.publish(template)).toBe(true)
  const replay = structuredClone(template)
  replay.air.sequence += 1
  replay.air.at += 1000
  replay.gps[0].sequence -= 1
  replay.gps[0].at -= 1000
  expect(port.publish(replay)).toBe(false)
  expect(port.read()).toEqual(template)
})

test('radio AUTO acquisition restarts at the first sample in range after a loss', () => {
  const at = { lat: 45, lon: -75 }
  const station: Navaid = { kind: 'navaid', ident: 'TEST', type: 'VORDME', name: 'Test fixture', frequency: '115.00', position: offset(at, 90, 10) }
  const receiver = new BenchRadioReceiver()
  receiver.tune([station], 0)
  expect(receiver.sample(at, 3000, 3000)[0].slantRangeNm.status).toBe('NORMAL')
  receiver.sample(at, 3000, 4000, true)
  expect(receiver.sample(at, 3000, 5000)[0].slantRangeNm.status).toBe('NCD')
  expect(receiver.sample(at, 3000, 7000)[0].slantRangeNm.status).toBe('NCD')
  expect(receiver.sample(at, 3000, 8000)[0].slantRangeNm.status).toBe('NORMAL')
})

test('DME/DME and VOR/DME solve measured ranges and bearings rather than substitute a prior or truth position', () => {
  const at = { lat: 45, lon: -75 }, altitude = 3000, now = 10000
  const stations: Navaid[] = [90, 0, 225].map((course, i) => ({ kind: 'navaid', ident: `T${i}`, type: 'VORDME', name: 'Test fixture', frequency: '115.00', position: offset(at, course, 10) }))
  const observations: RadioObservation[] = stations.map(station => ({ station,
    slantRangeNm: { at: now, sequence: 1, status: 'NORMAL', value: Math.hypot(distanceNm(at, station.position), altitude / 6076.12) },
    bearingTrue: { at: now, sequence: 1, status: 'NORMAL', value: bearingDeg(station.position, at) } }))
  const prior = offset(at, 220, 2)
  const dme = solveRadio(observations, prior, altitude, now)!
  expect(dme.mode).toBe('DME/DME')
  expect(distanceNm(dme.position, at)).toBeLessThan(0.03)
  expect(distanceNm(dme.position, prior)).toBeGreaterThan(1.9)
  expect(solveRadio(observations, prior, altitude, now + 2001)).toBeNull()
  const vor = solveRadio(observations.slice(1), prior, altitude, now)!
  expect(vor.mode).toBe('VOR/DME')
  expect(distanceNm(vor.position, at)).toBeLessThan(0.01)
})

test('stale external position and air data hold the last estimate and withdraw managed guidance', () => {
  let now = Date.UTC(2026, 8, 27, 14)
  const template = new ScriptedFms(() => new Date(now)).navigationInputs!
  template.radioHeight = { at: now, sequence: template.air.sequence, status: 'NORMAL', value: 75 }
  const port = new BufferedSensorPort()
  expect(port.publish(template)).toBe(true)
  const profile = structuredClone(HELICOPTER_PROFILE)
  profile.parameters.sensorMaxAge.value = 1.5
  const unit = new ScriptedFms(() => new Date(now), { sensors: port, profile })
  const output: { status: string; value: unknown }[] = []
  const sim = new FlightSimulator(unit, { write: frame => output.push(frame) })
  expect(unit.radioHeight).toEqual({ status: 'NORMAL', value: 75 })
  const lastFix = { ...unit.position }
  now += 1600
  sim.step(1)
  expect(unit.navState).toMatchObject({ mode: 'DR', airValid: false })
  expect(unit.radioHeight).toEqual({ status: 'NCD', value: null })
  expect(unit.validBaroAltitude).toBeNull()
  expect(distanceNm(unit.position, lastFix)).toBeLessThan(1e-8)
  expect(output.at(-1)?.status).toBe('NCD')
  expect(sim.modeEvents.some(event => event.event === 'LNAV LOST')).toBe(true)
  const fresh: SensorFrame = structuredClone(template)
  fresh.air.at = now
  fresh.air.sequence += 1
  fresh.air.value = { headingTrue: 90, tasKt: 120, altitudeFt: 3000 }
  expect(port.publish(fresh)).toBe(true)
  unit.updateNavigation(10)
  expect(unit.navState).toMatchObject({ mode: 'DR', airValid: true })
  expect(distanceNm(unit.position, lastFix)).toBeGreaterThan(0.3)
})

test('dead reckoning cannot read a changed truth position when every position sensor is unavailable', () => {
  const { unit } = setup()
  unit.setCondition('dmeOutage', true)
  unit.setCondition('gpsLost', true)
  const lastFix = { ...unit.position }
  // The plant can move or be repositioned independently of its failed sensors. No new measurement means no
  // position update at zero elapsed time; a truth-relative synthetic error would move the estimated fix 50 NM.
  unit.setAircraft({ position: offset(unit.truePosition, 180, 50) })
  unit.updateNavigation(0)
  expect(unit.navState.mode).toBe('DR')
  expect(distanceNm(unit.position, lastFix)).toBeLessThan(1e-8)
})

test('DR retains the wind computed from successive radio fixes when no GPS velocity is available', () => {
  const start = { lat: 45, lon: -75 }
  const navigation = new CivilNavigation(start)
  const input = { dt: 1, air: { headingTrue: 90, tasKt: 120, altitudeFt: 3000 }, gps: null, uncertainGps: null, radioApproved: true, rnp: 1 }
  navigation.update({ ...input, radio: { position: start, at: 1000, mode: 'DME/DME', anp: 0.2, dmes: ['A', 'B'], vor: null } })
  const next = offset(start, 90, 140 / 3600)
  navigation.update({ ...input, radio: { position: next, at: 2000, mode: 'DME/DME', anp: 0.2, dmes: ['A', 'B'], vor: null } })
  const dr = navigation.update({ ...input, dt: 60, radio: null })
  expect(dr.mode).toBe('DR')
  expect(distanceNm(next, dr.position)).toBeCloseTo(140 / 60, 2)
})

test('GPS-only integrity loss retains GPS2 position without granting hover authority or switching back on recovery', () => {
  const { unit, fly } = setup()
  unit.setCondition('dmeOutage', true)
  const stimulus = stimulusFor(unit)
  stimulus.apply(0, { op: 'fault', fault: 'RECEIVER', on: true })
  expect(unit.navState.gpsSource).toBe(2)
  unit.setCondition('gpsIntegrity', true)
  expect(unit.navState).toMatchObject({ mode: 'GPS', gpsSource: 2, uncertain: true })
  expect(unit.hoverFeedback).toBeNull()
  expect(recalled(unit, 'GPS NAV LOST')).toBe(false)
  unit.open('NAV_STATUS')
  expect(lines(unit).join('\n')).toContain('GPS2 UNCERTAIN')
  unit.setCondition('gpsIntegrity', false)
  stimulus.apply(0, { op: 'fault', fault: 'RECEIVER', on: false })
  fly(5)
  expect(unit.navState).toMatchObject({ mode: 'GPS', gpsSource: 2, uncertain: false })
  expect(unit.navSourceLog.map(entry => entry.source)).toEqual(['GPS2', 'GPS1'])
})

test('predictive RAIM uses the destination MAP and seven ETA intervals; PRN exclusion affects predictions alone', () => {
  let now = Date.UTC(2026, 8, 27, 14)
  const unit = new ScriptedFms(() => new Date(now))
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  unit.open('PREDICT_RAIM')
  expect(unit.predictiveRaim.ident).toBe('CYUL')
  expect(unit.predictiveRaim.eta).toBe(unit.profile().points.find(point => point.ident === 'RW24R')!.eta)
  expect(unit.predictedRaim).toEqual([])
  now += 1000
  unit.updateNavigation(0)
  const live = structuredClone(unit.navigationInputs!.gps)
  const prediction = unit.predictedRaim
  expect(prediction).toHaveLength(7)
  expect(prediction.map(row => (row.at - unit.predictiveRaim.eta!) / 60000)).toEqual([-15, -10, -5, 0, 5, 10, 15])
  expect(prediction.some(row => row.phase !== 'NONE' && row.phase !== '****')).toBe(true)
  unit.open('SAT_DESELECT')
  enter(unit, '33', 'LSK1L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  for (let prn = 1; prn <= 32; prn++) unit.deselectRaimSatellite(prn, true)
  now += 1000
  expect(unit.predictedRaim.every(row => row.phase === 'NONE')).toBe(true)
  expect(unit.navigationInputs!.gps).toEqual(live)
  expect(unit.predictRaimAt('HWK')).toBe(true)
  expect(unit.predictiveRaim.eta).toBeNull()
  expect(unit.predictRaimEta('2460')).toBe(false)
  expect(unit.predictRaimEta('1430Z')).toBe(true)
  now += 1000
  expect(unit.predictedRaim).toHaveLength(7)
  for (let prn = 1; prn <= 32; prn++) unit.deselectRaimSatellite(prn, false)
  expect(stimulusFor(unit).apply(0, { op: 'fault', fault: 'STOP_TRANSMITTING', on: true })).toBe(true)
  expect(stimulusFor(unit).apply(1, { op: 'fault', fault: 'STOP_TRANSMITTING', on: true })).toBe(true)
  now += 1000
  expect(unit.predictedRaim.every(row => row.phase === '****')).toBe(true)
})

test('civil NAV STATUS reports available navigation without claiming an unconfigured IRS', () => {
  const { unit } = setup()
  unit.open('NAV_STATUS')
  expect(lines(unit).join('\n')).not.toMatch(/\bIRS\b/)
  expect(lines(unit).join('\n')).toContain('GPS')
  unit.setCondition('dmeOutage', true)
  unit.setCondition('gpsLost', true)
  expect(unit.navState.mode).toBe('DR')
  expect(lines(unit).join('\n')).toContain('DR')
  expect(lines(unit).join('\n')).not.toMatch(/\bIRS\b/)
})

test('GPS loss reverts to radio updating automatically as stations come into range', () => {
  const { unit, fly } = setup()
  unit.setCondition('gpsLost', true)
  expect(unit.navState.mode).toBe('VOR/DME')
  const modes = new Set<string>()
  fly(3600, () => { modes.add(unit.navState.mode); return active(unit) === 'TOLGU' })
  expect(modes.has('DME/DME')).toBe(true)
  expect(modes.has('DR')).toBe(false)
})

test('dead reckoning uses measured motion and grows its uncertainty independently of aircraft truth', () => {
  const { unit, fly } = setup()
  fly(20)
  const initialAnp = unit.navState.anp
  // DME first, then GPS: the aircraft goes straight from a GPS fix to dead reckoning.
  unit.setCondition('dmeOutage', true)
  unit.setCondition('gpsLost', true)
  expect(unit.navState.mode).toBe('DR')
  expect(unit.lamps().has('POS')).toBe(true)
  fly(30 * 60)
  // Perfect heading/TAS and a steady last-valid wind remain close to the plant. An uncertainty allowance still grows;
  // it is not computed by secretly comparing the estimate with truth, nor an invented inertial drift.
  const error = distanceNm(unit.truePosition, unit.position)
  expect(error).toBeLessThan(0.1)
  expect(unit.navState.anp).toBeGreaterThan(initialAnp + 1)
})

test('when GPS returns after dead reckoning the position jumps back, and the FMS reports POSITION SHIFT', () => {
  const { unit, fly } = setup()
  // DME first, then GPS: the aircraft goes straight from a GPS fix to dead reckoning.
  unit.setCondition('dmeOutage', true)
  unit.setCondition('gpsLost', true)
  // A changed wind cannot be computed without a valid position/velocity source: DR keeps its last measured wind.
  unit.wind.speed += 6
  fly(30 * 60)
  expect(recalled(unit, 'POSITION SHIFT')).toBe(false)
  unit.setCondition('gpsLost', false)
  expect(recalled(unit, 'POSITION SHIFT')).toBe(true)
  expect(distanceNm(unit.truePosition, unit.position)).toBeLessThan(0.05)
  expect(unit.lamps().has('POS')).toBe(false)
})

test('the aircraft flies the FMS position, so in dead reckoning it really is off the route', () => {
  const { unit, fly } = setup()
  // DME first, then GPS: the aircraft goes straight from a GPS fix to dead reckoning.
  unit.setCondition('dmeOutage', true)
  unit.setCondition('gpsLost', true)
  unit.wind.speed += 6
  let offAtRdg = 0
  fly(3600, () => {
    if (active(unit) === 'TOLGU') { offAtRdg = distanceNm(unit.truePosition, unit.coordinates('RDG')!); return true }
  })
  // Sequenced where the FMS believed RDG was: really a few tenths of a mile away.
  expect(offAtRdg).toBeGreaterThan(0.3)
})

test('RNP defaults by phase: terminal near the airports, en route between them, approach on the approach', () => {
  const unit = new ScriptedFms()
  expect(unit.flightPhase).toBe('TERMINAL')
  expect(unit.requiredRnp).toBe(RNP_DEFAULTS.TERMINAL.rnp)
  unit.sequence()
  unit.sequence()
  unit.sequence()
  expect(unit.flightPhase).toBe('EN ROUTE')
  press(unit, 'PROG')
  expect(lines(unit)[9]).toMatch(/RNP\/ANP EN ROUTE/)
  // ANP in GPS mode is the receiver's HFOM with a 0.02 NM floor (GPS phase 3a); navigating on SBAS, HFOM is a few thousandths.
  expect(lines(unit)[10]).toMatch(/^2\.00\/0\.02NM/)
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  unit.sequence()
  expect(unit.flightPhase).toBe('APPROACH')
  expect(unit.requiredRnp).toBe(0.3)
})

test('ANP above RNP raises CHECK ANP only after the time to alert for the phase', () => {
  const { unit, fly } = setup()
  press(unit, 'PROG')
  // 0.01 NM is below the 0.02 NM ANP floor, so it is below ANP whatever the satellite geometry (GPS phase 3a).
  enter(unit, '.01', 'LSK5L')
  expect(lines(unit)[10]).toMatch(/^0\.01\/0\.02NM/)
  expect(lines(unit)[9]).toMatch(/MANUAL/)
  expect(unit.lamps().has('RNP')).toBe(true)
  // Terminal phase: 60 seconds.
  fly(50)
  expect(recalled(unit, 'CHECK ANP')).toBe(false)
  fly(15)
  expect(recalled(unit, 'CHECK ANP')).toBe(true)
  press(unit, 'CLR', 'CLR', 'LSK5L')
  expect(unit.requiredRnp).toBe(1)
  expect(unit.lamps().has('RNP')).toBe(false)
})

test('a manual RNP larger than the phase default asks the crew to VERIFY RNP VALUE', () => {
  const unit = new ScriptedFms()
  press(unit, 'PROG')
  enter(unit, '3.0', 'LSK5L')
  expect(scratch(unit)).toBe('VERIFY RNP VALUE')
  expect(unit.requiredRnp).toBe(3)
  press(unit, 'CLR', 'CLR', 'CLR', 'CLR')
  enter(unit, 'X', 'LSK5L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
})

test('loss of GPS integrity: GPS POS UNCERTAIN, larger ANP, and no RNAV approach guidance', () => {
  const unit = new ScriptedFms()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  // The receivers report the approach selected (156) once the FMS has sent its FAS block, at the next navigation update;
  // until then it is not selected, and an unselected approach may not be flown (the GPS review's GPS-06).
  expect(unit.approachType).toBe('NO APPR')
  unit.updateNavigation(0)
  // The level the GPS reports (305, GPS phase 3b): outside the 30 NM approach region SBAS NAV supports LNAV/VNAV; LPV
  // comes only inside it, in SBAS PA.
  expect(unit.approachType).toBe('LNAV/VNAV')
  unit.setCondition('gpsIntegrity', true)
  expect(scratch(unit)).toBe('GPS POS UNCERTAIN')
  // The independent radio comparison is adequate here (M300 1-7). The position is retained as uncertain, without
  // granting approach or hover authority to either integrity-rejected receiver.
  expect(unit.navState.mode).toBe('GPS')
  expect(unit.navState.uncertain).toBe(true)
  expect(unit.navState.anp).toBeGreaterThan(0.3)
  expect(unit.approachType).toBe('NO APPR')
  for (let i = 0; i < 3; i += 1) unit.sequence()
  unit.updateNavigation(0)
  expect(unit.flightPhase).toBe('APPROACH')
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
  press(unit, 'INIT_REF', 'NEXT', 'LSK5R')
  // The condition leaves each receiver five satellites (one degree of freedom: detection without exclusion).
  expect(lines(unit)[6]).toMatch(/5 SAT NO RAIM$/)
})

test('NAV OPTIONS inhibits a navaid from updating, and GPS can be selected out', () => {
  const { unit, fly } = setup()
  press(unit, 'INIT_REF', 'NEXT', 'LSK5R')
  expect(lines(unit)[0]).toMatch(/^NAV STATUS/)
  expect(lines(unit)[2]).toMatch(/^GPS/)
  unit.press('LSK6R')
  expect(lines(unit)[0]).toMatch(/^NAV OPTIONS/)
  // GPS NAV steps AUTO, GPS1, GPS2, OFF (GPS phase 3a): the third press selects GPS out.
  press(unit, 'LSK3L', 'LSK3L', 'LSK3L')
  expect(unit.gpsNavSelected).toBe(false)
  expect(unit.navState.mode).toBe('VOR/DME')
  expect(recalled(unit, 'GPS NAV LOST')).toBe(true)
  unit.press('CLR')
  enter(unit, 'YOW', 'LSK1L')
  // YOW excluded, the FMS updates from HWK; both excluded, nothing is left to update from.
  expect(unit.navState.vor).toBe('HWK')
  enter(unit, 'HWK', 'LSK1R')
  expect(unit.navState.mode).toBe('DR')
  expect(unit.lamps().has('POS')).toBe(true)
  press(unit, 'LSK6L')
  expect(lines(unit)[10]).toMatch(/^YOW HWK/)
  press(unit, 'LSK6R', 'CLR', 'LSK1L', 'CLR', 'LSK1L')
  expect(unit.inhibitedNavaids).toEqual([])
  expect(unit.navState.mode).toBe('DR')
  fly(4)
  expect(unit.navState.mode).toBe('VOR/DME')
})

test('the NPA annunciator follows a non-precision approach, not an ILS', () => {
  const unit = new ScriptedFms()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  // Jump through MUN, RDG, TOLGU, DEMEL, ALNIT and ULIDA to the FAF.
  for (let i = 0; i < 6; i += 1) unit.sequence()
  expect(active(unit)).toBe('FERDI')
  expect(unit.lamps().has('NPA')).toBe(true)
  const ils = new ScriptedFms()
  ils.selectProcedure('APPROACH', 'I24R')
  ils.press('EXEC')
  for (let i = 0; i < 6; i += 1) ils.sequence()
  expect(active(ils)).toBe('FERDI')
  expect(ils.lamps().has('NPA')).toBe(false)
})

test('ARM APPROACH is asked for within 2 NM of the final approach fix when the approach is not armed', () => {
  const { unit, fly } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  fly(3 * 3600, () => active(unit) === 'RW24R' || recalled(unit, 'ARM APPROACH'))
  expect(recalled(unit, 'ARM APPROACH')).toBe(true)
  expect(distanceNm(unit.position, unit.coordinates('FERDI')!)).toBeLessThanOrEqual(2.05)

  const armed = setup()
  armed.unit.selectProcedure('APPROACH', 'R24R')
  armed.unit.press('EXEC')
  armed.unit.armApproach()
  armed.fly(3 * 3600, () => active(armed.unit) === 'RW24R')
  expect(recalled(armed.unit, 'ARM APPROACH')).toBe(false)
})

test('TOGA before the MAP (helicopter): guidance continues to the MAP, which then sequences the missed approach; its hold armed (M300 7-16, R2-03)', () => {
  const unit = new ScriptedFms()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  for (let i = 0; i < 6; i += 1) unit.sequence()
  expect(active(unit)).toBe('FERDI')
  unit.armApproach(true)
  expect(unit.goAround()).toBe(true)
  // The lateral path is kept: still to FERDI, then the runway (the MAP); the approach is disarmed.
  expect(active(unit)).toBe('FERDI')
  expect(unit.approachArmed).toBe(false)
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'UL502', status: 'ARMED' })
  while (active(unit) !== 'RW24R') unit.sequence()
  unit.sequence()
  expect(active(unit)).toBe('(CA)')
  expect(unit.goAround()).toBe(false)
})

test('TOGA on the approach (laboratory airline profile) drops the rest of it and flies the missed approach, its hold armed', () => {
  const unit = new ScriptedFms(undefined, { profile: LAB_AIRLINE_VNAV_PROFILE })
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  for (let i = 0; i < 6; i += 1) unit.sequence()
  expect(active(unit)).toBe('FERDI')
  expect(unit.goAround()).toBe(true)
  expect(active(unit)).toBe('(CA)')
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'UL502', status: 'ARMED' })
  expect(unit.goAround()).toBe(false)
})
