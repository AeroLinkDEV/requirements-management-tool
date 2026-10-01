import { expect, logicTest as test } from './isolated-client-test'
import { CivilNavigation, type PositionMeasurement } from '../src/fmsCdu/civilNavigation'
import { anpText, bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import type { GpsBus, GpsReceiver } from '../src/fmsCdu/gps'
import { assessReceiver } from '../src/fmsCdu/gpsSensors'
import { radioFixes, type RadioFix } from '../src/fmsCdu/radioNavigation'
import { NAV_MODES } from '../src/fmsCdu/navigation'
import { NAV_OUTPUT_VOCABULARY } from '../src/fmsCdu/sensorState'
import type { RadioObservation } from '../src/fmsCdu/sensorPorts'
import type { Navaid } from '../src/fmsCdu/navData'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { accuracy95Isotropic } from '../src/fmsCdu/sensorState'
import { seededRandom } from '../src/fmsCdu/gnss'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Stage F plan F2: each sensor solution carries its availability, its 95% accuracy, its integrity bound (NP) and its
// phase eligibility separately, and "within the limit" is strictly less than (M300 1-3, 15-1 to 15-4).

const HERE = { lat: 45.5, lon: -73.6 }
const air = { at: 0, headingTrue: 90, tasKt: 100, altitudeFt: 2000 }
const gpsFix = (overrides: Partial<PositionMeasurement> = {}): PositionMeasurement =>
  ({ position: HERE, receiver: 1, accuracy95Nm: 0.05, hilNm: 0.2, northKt: 0, eastKt: 100, ...overrides })
const radioFix = (mode: RadioFix['mode'], anp: number): RadioFix =>
  ({ position: offset(HERE, 90, 0.1), at: 0, anp, mode, dmes: ['YUL', 'YMX'], vor: mode === 'VOR/DME' ? 'YUL' : null, priorResolved: false })
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
  const solution = update(new CivilNavigation(HERE), { uncertainGps: gpsFix({ hilNm: 1.4 }) })
  expect(solution.mode).toBe('GPS')
  expect(solution.selected).toMatchObject({ mode: 'GPS', available: true, integrity: false, integrityNm: 1.4 })
  // A receiver that flags its own integrity (273 INTEGRITY DETECTED) is uncertain even with a HIL under the limit:
  // the bound is reported as the receiver gave it, and it still carries no integrity.
  const flagged = update(new CivilNavigation(HERE), { uncertainGps: gpsFix({ hilNm: 0.5 }) })
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

test('F2: the selected candidate need not be the first weighed: a GPS rejected against a radio fix reverts to it (M300 1-4)', () => {
  // The estimator: an uncertain GPS 2 NM from an approved DME/DME fix fails the comparison, so the fix is navigated on
  // while GPS stays listed first. F3's hysteresis will make the same order common.
  const nav = new CivilNavigation(HERE)
  const rejected = update(nav, { uncertainGps: gpsFix({ position: offset(HERE, 0, 2), hilNm: 3 }), radio: radioFix('DME/DME', 0.4) })
  expect(rejected.sensors.map(sensor => sensor.mode)).toEqual(['GPS', 'DME/DME', 'DR'])
  expect(rejected.mode).toBe('DME/DME')
  expect(rejected.selected).toEqual(rejected.sensors[1])
  expect(rejected.sensors[0]).toMatchObject({ mode: 'GPS', available: true, integrity: false })
  // The FMS: both receivers flag their integrity and carry a position bias; the radio fix takes over, and
  // navPerformance reports that selected sensor, not the first candidate.
  let now = Date.UTC(2026, 8, 30, 14)
  const unit = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; unit.updateNavigation(1) } }
  step(60)
  for (const index of [0, 1]) {
    stimulusFor(unit).apply(index, { op: 'override', label: '130', kind: 'FORCE', amount: 3 })
    stimulusFor(unit).apply(index, { op: 'override', label: '110', kind: 'BIAS', amount: 0.05 })
  }
  step(20)
  expect(unit.navState.mode).not.toBe('GPS')
  expect(unit.sensorSolutions[0].mode).toBe('GPS')
  expect(unit.navPerformance.sensor.mode).toBe(unit.navState.mode)
  expect(unit.navPerformance.sensor).toEqual(unit.sensorSolutions.find(sensor => sensor.mode === unit.navState.mode))
})

// Plan C1 (Astra SF-01): accuracy, integrity bound and the laboratory NAIM comparison are separate values.

test('C1: a retained uncertain GPS keeps the receiver HIL as its bound; the NAIM comparison is its own value, and ANP is the HFOM', () => {
  // RNP 0.5 (crew entry): a 0.1 NM GPS bias against a DME/DME fix of 0.3 NM accuracy compares at 0.4 NM, below 0.5.
  const retained = update(new CivilNavigation(HERE), { rnp: 0.5, uncertainGps: gpsFix({ position: offset(HERE, 90, 0.2), accuracy95Nm: 0.04, hilNm: 0.9 }),
    radio: { ...radioFix('DME/DME', 0.3), position: offset(HERE, 90, 0.1) } })
  expect(retained.mode).toBe('GPS')
  expect(retained.uncertain).toBe(true)
  expect(retained.selected).toMatchObject({ integrityNm: 0.9, integrity: false })
  expect(retained.selected.naimComparisonNm).toBeCloseTo(0.4, 6)
  expect(retained.anp).toBe(0.04)
})

test('C1: the NAIM comparison retains an uncertain GPS only strictly below the limit (0.4 retain, 0.7 revert, equal reverts)', () => {
  const judged = (bias: number, rnp = 0.5) => update(new CivilNavigation(HERE), { rnp,
    uncertainGps: gpsFix({ position: offset(HERE, 90, bias), hilNm: 0.9 }), radio: { ...radioFix('DME/DME', 0.3), position: HERE } }).mode
  // 0.1 + 0.3 = 0.4 < 0.5: retained. 0.4 + 0.3 = 0.7: reverted.
  expect(judged(0.1)).toBe('GPS')
  expect(judged(0.4)).toBe('DME/DME')
  // The boundary: a limit equal to the comparison (the separation as the geometry gives it, plus 0.3) is not below it.
  const separation = distanceNm(offset(HERE, 90, 0.2), HERE)
  expect(Math.abs(separation - 0.2)).toBeLessThan(1e-9)
  expect(judged(0.2, separation + 0.3)).toBe('DME/DME')
  expect(judged(0.2, separation + 0.3 + 1e-9)).toBe('GPS')
})

test('C1: without a 95% accuracy the ANP is unavailable: dashes, and counted as exceeding the RNP', () => {
  const solution = update(new CivilNavigation(HERE), { gps: gpsFix({ accuracy95Nm: null }) })
  expect(solution.mode).toBe('GPS')
  expect(solution.anp).toBeNull()
  expect(anpText(solution.anp)).toBe('----')
  expect(anpText(0.05)).toBe('0.05')
})

// Plan C1 (Astra's sign-off): GPS dependency is transitive. A fix the prior had to disambiguate, a wind computed from GPS,
// and dead reckoning from a GPS position all carry it; such a solution is never the GPS-independent NAIM backup.

const at = { lat: 45, lon: -75 }, altitudeFt = 3000, now = 10_000
const observe = (courses: number[], truth = at): RadioObservation[] => courses.map((course, i) => {
  const station: Navaid = { kind: 'navaid', ident: `T${i}`, type: 'VORDME', name: 'Test fixture', frequency: '115.00', elevation: { feet: 0, source: 'data', provenance: 'test fixture' }, position: offset(at, course, 10) }
  return { station, slantRangeNm: { at: now, sequence: 1, status: 'NORMAL', value: Math.hypot(distanceNm(truth, station.position), altitudeFt / 6076.12) },
    bearingTrue: { at: now, sequence: 1, status: 'NORMAL', value: bearingDeg(station.position, truth) } }
})

test('C1: three DMEs on one line leave a mirror ambiguity the prior resolves; spread stations do not', () => {
  // Stations 10 NM north, here, and 10 NM south of a point 3 NM west of the aircraft: both mirror points fit every range.
  const west = offset(at, 270, 3)
  const collinear: RadioObservation[] = [0, 180, 0].map((course, i) => {
    const station: Navaid = { kind: 'navaid', ident: `L${i}`, type: 'VORDME', name: 'Line', frequency: '115.00', elevation: { feet: 0, source: 'data', provenance: 'test fixture' }, position: i === 2 ? west : offset(west, course, 10) }
    return { station, slantRangeNm: { at: now, sequence: 1, status: 'NORMAL', value: Math.hypot(distanceNm(at, station.position), altitudeFt / 6076.12) },
      bearingTrue: { at: now, sequence: 1, status: 'NORMAL', value: bearingDeg(station.position, at) } }
  })
  const ambiguous = radioFixes(collinear, offset(at, 90, 0.5), altitudeFt, now).find(fix => fix.mode === 'DME/DME')!
  expect(ambiguous.mode).toBe('DME/DME')
  expect(distanceNm(ambiguous.position, at)).toBeLessThan(0.05)
  expect(ambiguous.priorResolved).toBe(true)
  // With the prior on the other side, the same ranges give the mirror point: the prior decided.
  expect(distanceNm(radioFixes(collinear, offset(west, 270, 3.5), altitudeFt, now).find(fix => fix.mode === 'DME/DME')!.position, at)).toBeGreaterThan(5)
  const spread = radioFixes(observe([90, 0, 225]), offset(at, 220, 2), altitudeFt, now).find(fix => fix.mode === 'DME/DME')!
  expect(spread.priorResolved).toBe(false)
})

test('C1: a radio fix disambiguated by a GPS-derived prior is GPS-dependent and is not a NAIM backup; an independent one is', () => {
  const air2 = { at: 0, headingTrue: 90, tasKt: 100, altitudeFt }
  const fix = (priorResolved: boolean): RadioFix => ({ position: offset(at, 90, 0.05), at: now, anp: 0.3, mode: 'DME/DME', dmes: ['A', 'B'], vor: null, priorResolved })
  const uncertain = { position: offset(at, 0, 2), accuracy95Nm: 0.05, hilNm: 3, receiver: 1 as const, northKt: 0, eastKt: 100 }
  const run = (priorResolved: boolean) => {
    const nav = new CivilNavigation(at)
    // GPS with integrity first: the estimate (the solver's prior) now derives from GPS.
    nav.update({ dt: 1, air: air2, gps: { ...uncertain, position: at, hilNm: 0.2 }, uncertainGps: null, radio: null, radioApproved: true, rnp: 1 })
    return nav.update({ dt: 1, air: air2, gps: null, uncertainGps: uncertain, radio: fix(priorResolved), radioApproved: true, rnp: 1 })
  }
  // Independent: 2 NM + 0.3 = 2.3 >= 1, so the uncertain GPS is rejected for the fix.
  const independent = run(false)
  expect(independent.mode).toBe('DME/DME')
  expect(independent.gpsDependent).toBe(false)
  expect(independent.sensors[0].naimComparisonNm).toBeCloseTo(2.3, 1)
  // Dependent: no qualifying backup, so no comparison; the uncertain GPS is retained (resolver step 2).
  const dependent = run(true)
  expect(dependent.sensors.find(s => s.mode === 'DME/DME')!.gpsDependent).toBe(true)
  expect(dependent.sensors[0].naimComparisonNm).toBeNull()
  expect(dependent.mode).toBe('GPS')
  expect(dependent.uncertain).toBe(true)
})

test('C1: dead reckoning keeps the GPS dependency of its start and of a GPS-computed wind; a crew position entry or an independent radio wind clears it', () => {
  const air2 = { at: 0, headingTrue: 90, tasKt: 100, altitudeFt }
  const gpsFixed = { position: at, accuracy95Nm: 0.05, hilNm: 0.2, receiver: 1 as const, northKt: 10, eastKt: 100 }
  const base = { dt: 1, air: air2, uncertainGps: null, radioApproved: true, rnp: 1 }
  const nav = new CivilNavigation(at)
  nav.update({ ...base, gps: gpsFixed, radio: null })
  const dr = nav.update({ ...base, gps: null, radio: null })
  expect(dr.mode).toBe('DR')
  expect(dr.gpsDependent).toBe(true)
  // Many seconds later the epoch is new, the dependency is not.
  let later = dr
  for (let i = 0; i < 60; i++) later = nav.update({ ...base, gps: null, radio: null })
  expect(later.gpsDependent).toBe(true)
  expect(later.selected).toMatchObject({ gpsDependent: true, accuracyBasis: 'laboratory' })
  // A GPS without velocity words computes no wind, yet DR from its position still depends on it.
  const noWind = new CivilNavigation(at)
  noWind.update({ ...base, gps: { ...gpsFixed, northKt: null, eastKt: null }, radio: null })
  expect(noWind.update({ ...base, gps: null, radio: null }).gpsDependent).toBe(true)
  // With no GPS-computed wind, a crew position entry (SET POS) makes the dead reckoning independent of GPS.
  noWind.initialize(offset(at, 90, 2))
  expect(noWind.update({ ...base, gps: null, radio: null }).gpsDependent).toBe(false)
  // A crew SET POS gives an independent position, but the GPS-computed wind still makes DR dependent.
  nav.initialize(offset(at, 90, 2))
  expect(nav.update({ ...base, gps: null, radio: null }).gpsDependent).toBe(true)
  // Two independent radio fixes compute a new wind; DR after them is independent.
  const radio = (seconds: number): RadioFix => ({ position: offset(at, 90, 100 * seconds / 3600), at: seconds * 1000, anp: 0.3, mode: 'DME/DME', dmes: ['A', 'B'], vor: null, priorResolved: false })
  const indep = new CivilNavigation(at)
  indep.update({ ...base, gps: gpsFixed, radio: null })
  indep.update({ ...base, gps: null, radio: radio(1) })
  const windFromRadio = indep.update({ ...base, gps: null, radio: radio(2) })
  expect(windFromRadio.windComputed).toBe(true)
  expect(indep.update({ ...base, gps: null, radio: null }).gpsDependent).toBe(false)
})

test('C1: the GPS accuracy basis is the receiver figure; radio and DR are laboratory estimates', () => {
  const nav = new CivilNavigation(HERE)
  expect(update(nav, { gps: gpsFix() }).selected).toMatchObject({ accuracyBasis: 'receiver', gpsDependent: true })
  expect(update(new CivilNavigation(HERE), { radio: radioFix('DME/DME', 0.4) }).selected).toMatchObject({ accuracyBasis: 'laboratory', gpsDependent: false })
  expect(update(new CivilNavigation(HERE), { gps: gpsFix({ accuracy95Nm: null }) }).selected.accuracyBasis).toBeNull()
})

test('C4: the output vocabulary names every navigation mode and each separated C1 value', () => {
  for (const mode of NAV_MODES) expect(NAV_OUTPUT_VOCABULARY.modes as readonly string[], mode).toContain(mode)
  // Every value a sensor solution carries is in the vocabulary, so F13 cannot leave one out (the NavMode field excepted).
  const solution = update(new CivilNavigation(HERE), { gps: gpsFix() }).selected
  const named = new Set<string>([...NAV_OUTPUT_VOCABULARY.performance, 'integrityNm', 'integrity'])
  for (const key of Object.keys(solution).filter(key => !['mode', 'available', 'integrityBasis', 'eligibility'].includes(key))) expect(named.has(key), key).toBe(true)
  expect(NAV_OUTPUT_VOCABULARY.performance).toContain('integrityBoundNm')
  expect(NAV_OUTPUT_VOCABULARY.performance).toContain('accuracyBasis')
})

test('C1 (DF-05): 2.448 sigma is the 95% radius of an isotropic independent error, and not of a correlated one', () => {
  // Explanatory Monte Carlo with a seeded normal draw; the regression evidence is the KALMAN propagation test.
  const next = seededRandom(7), normal = () => Math.sqrt(-2 * Math.log(1 - next())) * Math.cos(2 * Math.PI * next())
  const radius95 = (draw: () => [number, number]) => { const r = Array.from({ length: 40000 }, () => Math.hypot(...draw())).sort((a, b) => a - b); return r[Math.floor(0.95 * r.length)] }
  expect(radius95(() => [normal(), normal()])).toBeCloseTo(2.448, 1)
  expect(accuracy95Isotropic(1)).toBeCloseTo(2.4477, 4)
  // X = Y = Z: both marginal sigmas are 1, yet the 95% radius is sqrt(2) x 1.960 = 2.772 > 2.448.
  const correlated = radius95(() => { const z = normal(); return [z, z] })
  expect(correlated).toBeGreaterThan(2.7)
  expect(correlated).toBeLessThan(2.85)
})
