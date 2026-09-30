import { expect, logicTest as test } from './isolated-client-test'
import { CivilNavigation, type PositionMeasurement } from '../src/fmsCdu/civilNavigation'
import { offset } from '../src/fmsCdu/fmsModel'
import type { GpsBus, GpsReceiver } from '../src/fmsCdu/gps'
import { assessReceiver } from '../src/fmsCdu/gpsSensors'
import type { RadioFix } from '../src/fmsCdu/radioNavigation'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Stage F plan F2: each sensor solution carries its availability, its 95% accuracy, its integrity bound (NP) and its
// phase eligibility separately, and "within the limit" is strictly less than (M300 1-3, 15-1 to 15-4).

const HERE = { lat: 45.5, lon: -73.6 }
const air = { at: 0, headingTrue: 90, tasKt: 100, altitudeFt: 2000 }
const gpsFix = (overrides: Partial<PositionMeasurement> = {}): PositionMeasurement =>
  ({ position: HERE, receiver: 1, anp: 0.05, accuracy95Nm: 0.05, hilNm: 0.2, northKt: 0, eastKt: 100, ...overrides })
const radioFix = (mode: RadioFix['mode'], anp: number): RadioFix =>
  ({ position: offset(HERE, 90, 0.1), at: 0, anp, mode, dmes: ['YUL', 'YMX'], vor: mode === 'VOR/DME' ? 'YUL' : null })
const update = (nav: CivilNavigation, input: Partial<Parameters<CivilNavigation['update']>[0]>) =>
  nav.update({ dt: 1, air, gps: null, uncertainGps: null, radio: null, radioApproved: true, rnp: 1, ...input })

test('F2: a GPS HIL exactly equal to the alert limit is not integrity; just below it is (M300 1-3, strict less-than)', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const unit = new ScriptedFms(() => new Date(now))
  now += 60_000; unit.updateNavigation(60)
  const receiver = (unit as unknown as { gps: GpsReceiver[] }).gps[0]
  const withHil = (hil: number): GpsBus => { const bus = structuredClone(receiver.bus()!); bus['130'] = { ...bus['130'], value: hil, ssm: 'NORMAL' }; return bus }
  const equal = assessReceiver(withHil(1), 1)
  expect(equal.usable).toBe(false)
  expect(equal.reason).toBe('INTEGRITY')
  expect(assessReceiver(withHil(0.999), 1).usable).toBe(true)
})

test('F2: GPS carries its 95% accuracy (HFOM) and its integrity bound (HIL) separately, eligible in every phase', () => {
  const nav = new CivilNavigation(HERE)
  const solution = update(nav, { gps: gpsFix({ accuracy95Nm: 0.03, hilNm: 0.25 }) })
  expect(solution.selected).toMatchObject({ mode: 'GPS', available: true, accuracy95Nm: 0.03, integrityNm: 0.25, integrity: true, integrityBasis: 'NP' })
  expect(solution.selected.eligibility).toEqual({ 'EN ROUTE': true, TERMINAL: true, APPROACH: true })
  // The bound is judged against the active limit (RNP basis, DEC-150), strictly: 0.3 is not within 0.3.
  expect(update(new CivilNavigation(HERE), { gps: gpsFix({ hilNm: 0.3 }), rnp: 0.3 }).selected.integrity).toBe(false)
})

test('F2: an uncertain GPS is available without integrity, and says so', () => {
  const solution = update(new CivilNavigation(HERE), { uncertainGps: gpsFix({ hilNm: 1.4, anp: 1.4 }) })
  expect(solution.mode).toBe('GPS')
  expect(solution.selected).toMatchObject({ mode: 'GPS', available: true, integrity: false, integrityNm: 1.4 })
  // A receiver that flags its own integrity (273 INTEGRITY DETECTED) is uncertain even with a HIL under the limit:
  // the bound is reported as the receiver gave it, and it still carries no integrity.
  const flagged = update(new CivilNavigation(HERE), { uncertainGps: gpsFix({ hilNm: 0.5, anp: 0.5 }) })
  expect(flagged.selected).toMatchObject({ mode: 'GPS', integrityNm: 0.5, integrity: false })
})

test('F2: DME/DME and VOR/DME are available but never approach-eligible (M300 15-3)', () => {
  for (const mode of ['DME/DME', 'VOR/DME'] as const) {
    const solution = update(new CivilNavigation(HERE), { radio: radioFix(mode, 0.4) })
    expect(solution.mode, mode).toBe(mode)
    expect(solution.selected, mode).toMatchObject({ mode, available: true, accuracy95Nm: 0.4, integrityNm: null, integrityBasis: 'criteria' })
    expect(solution.selected.eligibility, mode).toEqual({ 'EN ROUTE': true, TERMINAL: true, APPROACH: false })
  }
})

test('F2: dead reckoning is available from air data but never has integrity, and is not approach-eligible', () => {
  const solution = update(new CivilNavigation(HERE), {})
  expect(solution.mode).toBe('DR')
  expect(solution.selected).toMatchObject({ mode: 'DR', available: true, integrity: false, integrityNm: null, integrityBasis: 'none' })
  expect(solution.selected.eligibility.APPROACH).toBe(false)
  expect(update(new CivilNavigation(HERE), { air: null }).selected.available).toBe(false)
})

test('F2: every candidate the update weighed is reported, the selected one among them, and navPerformance reads it', () => {
  const both = update(new CivilNavigation(HERE), { gps: gpsFix(), radio: radioFix('DME/DME', 0.4) })
  expect(both.sensors.map(sensor => sensor.mode)).toEqual(['GPS', 'DME/DME', 'DR'])
  expect(both.sensors.find(sensor => sensor.mode === both.mode)).toEqual(both.selected)
  let now = Date.UTC(2026, 8, 30, 14)
  const unit = new ScriptedFms(() => new Date(now))
  now += 60_000; unit.updateNavigation(60)
  expect(unit.navPerformance.sensor.mode).toBe(unit.navState.mode)
  expect(unit.sensorSolutions.some(sensor => sensor.mode === unit.navState.mode)).toBe(true)
})
