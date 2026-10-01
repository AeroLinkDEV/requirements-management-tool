import { expect, logicTest as test } from './isolated-client-test'
import { CivilNavigation, indicatedAirspeedKt, type PositionMeasurement } from '../src/fmsCdu/civilNavigation'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Stage F plan F10 (M300 1-5, Appendix E E-33; Astra's amendment): dead reckoning from the last position, heading, TAS
// and the last valid computed wind; FMS NAV IN DR on entry; a low-speed regime below the declared indicated airspeed in
// which the wind is frozen (M300 12-22) and DR is degraded; automatic recovery when a sensor returns.

const HERE = { lat: 45.0, lon: -63.0 }
const parameters = HELICOPTER_PROFILE.parameters
const gps = (position: { lat: number; lon: number }, northKt: number, eastKt: number): PositionMeasurement =>
  ({ position, receiver: 1, accuracy95Nm: 0.02, hilNm: 0.1, northKt, eastKt })
const input = (air: { headingTrue: number; tasKt: number; altitudeFt: number } | null, overrides: Partial<Parameters<CivilNavigation['update']>[0]> = {}) =>
  ({ dt: 1, air: air && { at: 0, ...air }, gps: null, uncertainGps: null, radio: null, radioApproved: true, rnp: 2, ...overrides })

test('F10: DR carries the last computed wind', () => {
  const nav = new CivilNavigation(HERE)
  // Heading 090 at 100 kt TAS, the GPS ground velocity 100 kt east and 20 kt north: the wind is 20 kt from the south.
  const air = { headingTrue: 90, tasKt: 100, altitudeFt: 1000 }
  const first = nav.update(input(air, { gps: gps(HERE, 20, 100) }))
  expect(first.mode).toBe('GPS')
  expect(first.windComputed).toBe(true)
  expect(nav.windEstimate.north).toBeCloseTo(20, 9)
  expect(nav.windEstimate.east).toBeCloseTo(0, 9)
  // GPS lost: DR from heading and TAS plus that wind. 60 s at 100 kt east and 20 kt north, worked independently.
  let solution = first
  for (let t = 0; t < 60; t++) solution = nav.update(input(air))
  expect(solution.mode).toBe('DR')
  const expected = offset(HERE, Math.atan2(100, 20) * 180 / Math.PI, Math.hypot(100, 20) * 60 / 3600)
  expect(distanceNm(solution.position, expected)).toBeLessThan(0.001)
  // Its accuracy grows from the declared input uncertainties: wind, TAS and heading (2 kt, 0.5 kt, 1 degree at 100 kt).
  const rate = Math.hypot(parameters.drWindUncertainty.value, parameters.drTasUncertainty.value, 100 * Math.sin(parameters.drHeadingUncertainty.value * Math.PI / 180))
  expect(solution.anp).toBeCloseTo(0.02 + rate * 60 / 3600, 9)
  expect(solution.lowSpeed).toBe(false)
})

test('F10: in the hover, DR is flagged degraded and the accuracy grows at the no-velocity rate', () => {
  const nav = new CivilNavigation(HERE)
  // A wind computed in forward flight: 15 kt from the west.
  nav.update(input({ headingTrue: 0, tasKt: 90, altitudeFt: 500 }, { gps: gps(HERE, 90, 15) }))
  expect(nav.windEstimate.east).toBeCloseTo(15, 9)
  // Slowed to 20 kt TAS: the low-speed regime. With GPS still valid, the wind is frozen, not recomputed.
  const slow = { headingTrue: 0, tasKt: 20, altitudeFt: 500 }
  const held = nav.update(input(slow, { gps: gps(HERE, 0, 0) }))
  expect(held.lowSpeed).toBe(true)
  expect(held.windComputed).toBe(false)
  expect(nav.windEstimate.east).toBeCloseTo(15, 9)
  // GPS lost in the hover: DR, degraded, its accuracy growing at the declared no-motion rate (10 NM/h), not from the
  // 2.1 kt of input uncertainties forward flight would use.
  let solution = held
  for (let t = 0; t < 360; t++) solution = nav.update(input(slow))
  expect(solution.mode).toBe('DR')
  expect(solution.lowSpeed).toBe(true)
  expect(solution.anp).toBeCloseTo(0.02 + parameters.drNoAirGrowth.value * 360 / 3600, 9)
  expect(parameters.drLowSpeedIas).toMatchObject({ value: 40, unit: 'kt', basis: 'lab' })
})

test('F10: the low-speed limit is indicated airspeed: the same TAS is slow high up and not at sea level', () => {
  // ICAO standard atmosphere density ratio at 10,000 ft is 0.7385: 45 kt TAS is 38.7 KIAS there, 45 KIAS at sea level.
  expect(indicatedAirspeedKt(45, 0)).toBeCloseTo(45, 6)
  expect(indicatedAirspeedKt(45, 10_000)).toBeCloseTo(45 * Math.sqrt(0.7385), 2)
  const at = (altitudeFt: number) => new CivilNavigation(HERE).update(input({ headingTrue: 0, tasKt: 45, altitudeFt })).lowSpeed
  expect(at(0)).toBe(false)
  expect(at(10_000)).toBe(true)
  // Exactly at the limit is not below it.
  expect(new CivilNavigation(HERE).update(input({ headingTrue: 0, tasKt: 40, altitudeFt: 0 })).lowSpeed).toBe(false)
})

test('F10: a restored GPS ends DR and the position step is announced', () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  unit.wind = { direction: 270, speed: 20 }
  fly(30)
  expect(unit.navState.mode).toBe('GPS')
  // Every position sensor lost: DR, with the 20 kt westerly it last computed.
  unit.setCondition('apirsFail', true); unit.setCondition('dvsFail', true)
  unit.setCondition('dmeOutage', true); unit.setCondition('gpsLost', true)
  fly(1)
  expect(unit.navState.mode).toBe('DR')
  // The wind turns to 30 kt from the east. DR still carries the westerly, so its position drifts about 50 kt from the
  // aircraft's: some 4 NM in five minutes.
  unit.wind = { direction: 90, speed: 30 }
  fly(300)
  expect(unit.navState.mode).toBe('DR')
  expect(distanceNm(unit.position, unit.truePosition)).toBeGreaterThan(2)
  // GPS returns: DR ends at once (M300 1-5), and the jump to the GPS position is announced.
  unit.setCondition('gpsLost', false)
  fly(2)
  expect(unit.navState.mode).toBe('GPS')
  expect(distanceNm(unit.position, unit.truePosition)).toBeLessThan(0.1)
  expect(unit.recallList.some(message => message.text === 'POSITION SHIFT')).toBe(true)
})

test('F10: entering DR shows FMS NAV IN DR, a status advisory: white, and never in MESSAGE RECALL', () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  fly(5)
  // Everything but the radios lost, then the radios: the last step into DR raises no alert of its own.
  unit.setCondition('apirsFail', true); unit.setCondition('dvsFail', true); unit.setCondition('gpsLost', true)
  fly(2)
  expect(unit.navState.mode).not.toBe('DR')
  const shown = () => (unit as unknown as { message: { text: string; alert: boolean } | null }).message
  while (shown()) unit.press('CLR')
  unit.setCondition('dmeOutage', true)
  fly(1)
  expect(unit.navState.mode).toBe('DR')
  expect(shown()).toEqual({ text: 'FMS NAV IN DR', alert: false })
  expect(unit.recallList.some(message => message.text === 'FMS NAV IN DR')).toBe(false)
})
