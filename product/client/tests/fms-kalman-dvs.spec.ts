import { expect, logicTest as test } from './isolated-client-test'
import { CivilNavigation, KALMAN_COAST_S, type PositionMeasurement } from '../src/fmsCdu/civilNavigation'
import { distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Stage F plan F11 (DEC-150; M300 1-5, 12-20 to 12-24, 15-4): the AHRS/APIRS KALMAN mode coasts on the GPS-calibrated
// emulated INS for 2 minutes after GPS loss, then the Doppler (DVS) solution, without integrity and at the lowest
// priority, then dead reckoning. Neither KALMAN nor DVS is ever RNP- or approach-eligible.

const HERE = { lat: 45.0, lon: -63.0 }
const air = { at: 0, headingTrue: 90, tasKt: 100, altitudeFt: 1000 }
const gps = (position = HERE): PositionMeasurement =>
  ({ position, receiver: 1, accuracy95Nm: 0.02, hilNm: 0.1, northKt: 0, eastKt: 100 })
const equipped = () => new CivilNavigation(HERE, undefined, { kalman: true, dvs: true })
const input = (overrides: Partial<Parameters<CivilNavigation['update']>[0]> = {}) =>
  ({ dt: 1, air, gps: null, uncertainGps: null, radio: null, radioApproved: true, rnp: 2,
    apirs: { northMs2: 0, eastMs2: 0 }, dvs: { northKt: 0, eastKt: 100 }, waterCurrent: null, kalmanReady: true, ...overrides })

test('F11: after GPS loss the KALMAN mode coasts on the calibrated velocity for exactly 2 minutes, its 2-sigma growing', () => {
  const nav = equipped()
  let position = HERE
  for (let i = 0; i < 10; i++) { position = offset(position, 90, 100 / 3600); nav.update(input({ gps: gps(position) })) }
  const accuracies: number[] = []
  for (let t = 1; t <= KALMAN_COAST_S; t++) {
    const solution = nav.update(input())
    expect(solution.mode, `t=${t}`).toBe('KALMAN')
    accuracies.push(solution.selected.accuracy95Nm!)
  }
  expect(KALMAN_COAST_S).toBe(120)
  expect(accuracies.every((value, index) => index === 0 || value >= accuracies[index - 1])).toBe(true)
  // It flew on at 100 kt east: 2 minutes = 3.33 NM, with no acceleration measured.
  expect(distanceNm(nav.current.position, offset(position, 90, 100 * KALMAN_COAST_S / 3600))).toBeLessThan(0.01)
  // The coast has ended: the Doppler solution follows (the next mode, DEC-150), not KALMAN.
  expect(nav.update(input()).mode).toBe('DVS')
})

test('F11: an accelerometer bias walks the KALMAN position off quadratically, inside its declared 2-sigma', () => {
  const nav = equipped()
  for (let i = 0; i < 5; i++) nav.update(input({ gps: gps() , }))
  const start = nav.current.position
  let solution = nav.current
  for (let t = 1; t <= 120; t++) solution = nav.update(input({ apirs: { northMs2: 0.01, eastMs2: 0 } }))
  const truth = offset(start, 90, 100 * 120 / 3600)
  const error = distanceNm(solution.position, truth)
  // 0.5 * 0.01 m/s² * 120² s² = 72 m north.
  expect(error * 1852).toBeGreaterThan(60)
  expect(error * 1852).toBeLessThan(85)
  expect(solution.selected.accuracy95Nm!).toBeGreaterThan(error)
})

test('F11: KALMAN and DVS never have integrity and are never RNP- or approach-eligible (M300 15-4, 12-20)', () => {
  const nav = equipped()
  nav.update(input({ gps: gps() }))
  const kalman = nav.update(input()).selected
  expect(kalman).toMatchObject({ mode: 'KALMAN', integrity: false, integrityBasis: 'none' })
  expect(kalman.eligibility).toEqual({ 'EN ROUTE': false, TERMINAL: false, APPROACH: false })
  const dvs = nav.update(input({ apirs: null })).selected
  expect(dvs).toMatchObject({ mode: 'DVS', integrity: false, integrityBasis: 'none' })
  expect(dvs.eligibility).toEqual({ 'EN ROUTE': false, TERMINAL: false, APPROACH: false })
})

test('F11: a radio fix outranks KALMAN, and KALMAN outranks DVS; without APIRS readiness or calibration there is no KALMAN', () => {
  const radioFix = { position: HERE, at: 0, anp: 0.4, mode: 'DME/DME' as const, dmes: ['A', 'B'], vor: null }
  const nav = equipped()
  nav.update(input({ gps: gps() }))
  expect(nav.update(input({ radio: radioFix })).mode).toBe('DME/DME')
  expect(nav.update(input()).mode).toBe('KALMAN')
  // Never calibrated by GPS: no KALMAN.
  expect(equipped().update(input()).mode).toBe('DVS')
  // Not ready (the first minute after power-up, M300 12-24).
  const cold = equipped()
  cold.update(input({ gps: gps(), kalmanReady: false }))
  expect(cold.update(input({ kalmanReady: false })).mode).toBe('DVS')
})

test('F11: DVS integrates the Doppler ground velocity, and the crew water current corrects its drift over water', () => {
  // The Doppler measures velocity relative to a surface drifting 2 kt east; the aircraft flies 100 kt east over ground.
  const drifting = { northKt: 0, eastKt: 98 }
  const uncorrected = new CivilNavigation(HERE, undefined, { kalman: false, dvs: true })
  const corrected = new CivilNavigation(HERE, undefined, { kalman: false, dvs: true })
  for (let t = 0; t < 600; t++) {
    uncorrected.update(input({ apirs: null, dvs: drifting }))
    corrected.update(input({ apirs: null, dvs: drifting, waterCurrent: { northKt: 0, eastKt: 2 } }))
  }
  // Truth steps east each second as the estimator does (a constant-bearing path, not one great-circle leg).
  let truth = HERE
  for (let t = 0; t < 600; t++) truth = offset(truth, 90, 100 / 3600)
  expect(distanceNm(uncorrected.current.position, truth)).toBeGreaterThan(0.3)
  expect(distanceNm(corrected.current.position, truth)).toBeLessThan(0.01)
  expect(corrected.current.mode).toBe('DVS')
})

test('F11: offshore with GPS and radios lost, the FMS goes KALMAN for 2 minutes, then DVS, then DR when the DVS fails', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  step(90)
  expect(fms.navState.mode).toBe('GPS')
  fms.setCondition('gpsLost', true)
  fms.setCondition('dmeOutage', true)
  step(1)
  expect(fms.navState.mode).toBe('KALMAN')
  step(118)
  expect(fms.navState.mode).toBe('KALMAN')
  step(2)
  expect(fms.navState.mode).toBe('DVS')
  expect(fms.recallList.map(message => message.text)).toContain('KALMAN NAV LOST')
  expect(fms.navPerformance.sensor).toMatchObject({ mode: 'DVS', integrity: false })
  fms.setSensorHealth('DVS', 'FAIL')
  step(2)
  expect(fms.navState.mode).toBe('DR')
  expect(fms.recallList.map(message => message.text)).toContain('DVS NAV LOST')
})

test('F11: an FMS power cycle re-initializes KALMAN, unavailable for the first minute (M300 12-24)', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  step(90)
  fms.powerOff()
  now += 1000
  fms.powerOn('WARM', false)
  step(10)
  fms.setCondition('gpsLost', true)
  fms.setCondition('dmeOutage', true)
  step(1)
  expect(fms.navState.mode).not.toBe('KALMAN')
})

test('F11: only a GPS with integrity calibrates KALMAN; an uncertain GPS 1 NM off does not pull the emulated INS', () => {
  const nav = equipped()
  let truth = HERE
  for (let i = 0; i < 10; i++) { truth = offset(truth, 90, 100 / 3600); nav.update(input({ gps: gps(truth) })) }
  // Ten seconds on an uncertain GPS reporting 1 NM north of the aircraft (navigated on, but never calibrating KALMAN).
  for (let i = 0; i < 10; i++) {
    truth = offset(truth, 90, 100 / 3600)
    expect(nav.update(input({ uncertainGps: { ...gps(offset(truth, 0, 1)), hilNm: 1.4 } })).mode).toBe('GPS')
  }
  truth = offset(truth, 90, 100 / 3600)
  const coasting = nav.update(input())
  expect(coasting.mode).toBe('KALMAN')
  expect(distanceNm(coasting.position, truth)).toBeLessThan(0.05)
})

// Plan C2 (Astra SF-02): simulation-time clocks, the 50 ms interruption boundary, and a coast that re-entry cannot renew.

test('C2: a power interruption of 51 ms re-initializes KALMAN; 49 and 50 ms do not (M300 12-24)', () => {
  for (const [ms, reinitialized] of [[49, false], [50, false], [51, true]] as const) {
    let now = Date.UTC(2026, 8, 30, 14)
    const fms = new ScriptedFms(() => new Date(now))
    const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
    step(90)
    fms.powerInterrupt(ms)
    fms.setCondition('gpsLost', true)
    fms.setCondition('dmeOutage', true)
    step(1)
    expect(fms.navState.mode === 'KALMAN', `${ms} ms`).toBe(!reinitialized)
  }
})

test('C2: leaving KALMAN for a radio fix and coming back does not renew the 2-minute coast', () => {
  const nav = equipped()
  nav.update(input({ gps: gps() }))
  const radioFix = { position: HERE, at: 0, anp: 0.4, mode: 'DME/DME' as const, dmes: ['A', 'B'], vor: null }
  for (let t = 1; t <= 60; t++) expect(nav.update(input()).mode).toBe('KALMAN')
  for (let t = 61; t <= 100; t++) expect(nav.update(input({ radio: radioFix })).mode).toBe('DME/DME')
  // Back to KALMAN at 101 s: only 19 s of coast remain, counted from the last GPS aiding.
  for (let t = 101; t <= KALMAN_COAST_S; t++) expect(nav.update(input()).mode, `t=${t}`).toBe('KALMAN')
  expect(nav.update(input()).mode).toBe('DVS')
})

test('C2 (DF-05): the KALMAN accuracy is 2.448 x the quadrature sum of the aiding position, aiding velocity and residual bias terms', () => {
  const nav = equipped()
  nav.update(input({ gps: gps() }))
  let solution = nav.current
  for (let t = 1; t <= 90; t++) solution = nav.update(input())
  // Independent: sigma0 = 0.02 / 2.448 NM; velocity 0.2 kt x 90 s; bias 0.5 x 0.02 m/s² x 90² s².
  const sigma = Math.hypot(0.02 / 2.4477, 0.2 * 90 / 3600, 0.5 * 0.02 * 90 * 90 / 1852)
  expect(solution.mode).toBe('KALMAN')
  expect(solution.anp!).toBeCloseTo(2.4477 * sigma, 4)
  expect(nav.kalmanTwoSigmaNm!).toBeCloseTo(2 * sigma, 4)
})

test('C2 (DF-05): an injected APIRS bias is a fault outside the nominal model; one chosen large enough exceeds the estimate', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  step(90)
  // 0.3 m/s² for 100 s: 0.5 x 0.3 x 100² = 1,500 m (0.81 NM), against a nominal 95% of about 0.5 NM at 100 s.
  fms.setApirsFaultBias(0.3, 0)
  fms.setCondition('gpsLost', true)
  fms.setCondition('dmeOutage', true)
  step(100)
  expect(fms.navState.mode).toBe('KALMAN')
  expect(distanceNm(fms.position, fms.truePosition)).toBeGreaterThan(fms.navState.anp!)
})

test('C1 provenance: KALMAN is GPS-aided and DVS keeps the GPS dependency of the position it started from', () => {
  const nav = equipped()
  nav.update(input({ gps: gps() }))
  const kalman = nav.update(input())
  expect(kalman.mode).toBe('KALMAN')
  expect(kalman.gpsDependent).toBe(true)
  expect(kalman.selected).toMatchObject({ gpsDependent: true, accuracyBasis: 'laboratory' })
  const dvs = nav.update(input({ apirs: null, dvs: { northKt: 0, eastKt: 100 } }))
  expect(dvs.mode).toBe('DVS')
  expect(dvs.gpsDependent).toBe(true)
  // Many epochs later, still dependent: integrating an independent velocity does not rejuvenate the start position.
  let later = dvs
  for (let t = 0; t < 300; t++) later = nav.update(input({ apirs: null, dvs: { northKt: 0, eastKt: 100 } }))
  expect(later.gpsDependent).toBe(true)
})
