import { expect, logicTest as test } from './isolated-client-test'
import { CivilNavigation, type PositionMeasurement } from '../src/fmsCdu/civilNavigation'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator, legGeometry } from '../src/fmsCdu/flight'
import { distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { NAV_MODES, type NavMode } from '../src/fmsCdu/navigation'
import type { RadioFix } from '../src/fmsCdu/radioNavigation'
import { FMS_OUTPUT_TAGS } from '../src/fmsCdu/outputTags'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { MODE_HYSTERESIS_M, MODE_TRANSITIONS, transitionAlert } from '../src/fmsCdu/sensorTransitions'

// Stage F plan F3 (M300 1-3 to 1-5, 12-1): the civil mode selection. Integrity reversions are immediate; accuracy-based
// transitions use 95% statistics with 100 m of hysteresis, except VOR/DME with integrity to DME/DME with integrity.

const HERE = { lat: 45.0, lon: -63.0 }
const air = { at: 0, headingTrue: 90, tasKt: 100, altitudeFt: 1000 }
const gps = (position = HERE): PositionMeasurement =>
  ({ position, receiver: 1, accuracy95Nm: 0.02, hilNm: 0.1, northKt: 0, eastKt: 100 })
const fix = (mode: RadioFix['mode'], anp: number, position = offset(HERE, mode === 'DME/DME' ? 0 : 180, 0.3)): RadioFix =>
  ({ position, at: 0, anp, mode, dmes: mode === 'DME/DME' ? ['AAA', 'BBB'] : ['VVV'], vor: mode === 'VOR/DME' ? 'VVV' : null,
    assumedElevation: [], terrainElevation: [], rejected: [], accuracyBasis: 'laboratory', oldestAt: 0, priorResolved: false })
const equipped = () => new CivilNavigation(HERE, undefined, { kalman: true, dvs: true })
type Update = Parameters<CivilNavigation['update']>[0]
const input = (overrides: Partial<Update> = {}): Update =>
  ({ dt: 1, air, gps: null, uncertainGps: null, radio: null, radios: [], radioApproved: true, rnp: 2,
    apirs: { northMs2: 0, eastMs2: 0 }, dvs: null, waterCurrent: null, kalmanReady: false, ...overrides })
const metres = (m: number) => m / 1852

test('F3 regression: loss of VOR/DME integrity to DME/DME raises the receiver-failure alert despite nominal priority', () => {
  const nav = equipped()
  nav.update(input({ rnp: 0.5, radios: [fix('VOR/DME', 0.3)] }))
  const reverted = nav.update(input({ rnp: 0.5, radios: [fix('VOR/DME', 0.5), fix('DME/DME', 0.4)] }))
  expect(reverted.mode).toBe('DME/DME')
  expect(transitionAlert('VOR/DME', 'DME/DME', reverted.sensors, { vorDmeReceiversFailed: true })).toBe('VOR/DME NAV LOST')
  expect(transitionAlert('VOR/DME', 'DME/DME', reverted.sensors)).toBeNull()
})

test('F3 regression: a GPS-dependent best radio fix does not hide an independent integrity backup from NAIM', () => {
  const nav = equipped()
  nav.update(input({ gps: gps() }))
  const dependent = { ...fix('DME/DME', 0.2, HERE), priorResolved: true }
  const independent = fix('VOR/DME', 0.3, HERE)
  const result = nav.update(input({ rnp: 1, uncertainGps: { ...gps(offset(HERE, 0, 2)), hilNm: 1.5 },
    radios: [dependent, independent] }))
  // The independent VOR/DME comparison is 2.0 + 0.3 NM, outside RNP 1; step 2 cannot retain GPS.
  expect(result.mode).toBe('DME/DME')
  expect(result.sensors.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeCloseTo(2.3, 6)
})

test('F3 step 2: uncertain GPS stays ahead of KALMAN and DVS without a qualifying radio backup', () => {
  const nav = equipped()
  nav.update(input({ gps: gps(), kalmanReady: true }))
  const result = nav.update(input({ uncertainGps: { ...gps(), hilNm: 3 },
    kalmanReady: true, dvs: { northKt: 0, eastKt: 100 } }))
  expect(result.mode).toBe('GPS')
  expect(result.uncertain).toBe(true)
  for (const mode of ['KALMAN', 'DVS']) expect(result.sensors.find(sensor => sensor.mode === mode)).toMatchObject({ available: true, integrity: false })
})

test('F3: DME/DME to VOR/DME waits for 100 m of accuracy advantage; VOR/DME to DME/DME does not', () => {
  const nav = equipped()
  expect(nav.update(input({ radios: [fix('DME/DME', 0.5), fix('VOR/DME', 0.6)] })).mode).toBe('DME/DME')
  // VOR/DME 90 m better: DME/DME is kept.
  expect(nav.update(input({ radios: [fix('DME/DME', 0.5), fix('VOR/DME', 0.5 - metres(90))] })).mode).toBe('DME/DME')
  // 110 m better: the transition is made, with no alert (DME/DME is still usable).
  const moved = nav.update(input({ radios: [fix('DME/DME', 0.5), fix('VOR/DME', 0.5 - metres(110))] }))
  expect(moved.mode).toBe('VOR/DME')
  expect(transitionAlert('DME/DME', 'VOR/DME', moved.sensors)).toBeNull()
  // Back: DME/DME only 10 m better is enough (no hysteresis, M300 1-3).
  expect(nav.update(input({ radios: [fix('DME/DME', 0.5 - metres(120)), fix('VOR/DME', 0.5 - metres(110))] })).mode).toBe('DME/DME')
  expect(MODE_HYSTERESIS_M).toBe(100)
})

test('F3: an integrity loss reverts at once whatever the hysteresis', () => {
  const nav = equipped()
  const rnp = 0.5
  expect(nav.update(input({ rnp, radios: [fix('DME/DME', 0.45), fix('VOR/DME', 0.46)] })).mode).toBe('DME/DME')
  // DME/DME's 95% accuracy reaches the RNP: it has lost integrity, and VOR/DME (only 55 m better) takes over at once.
  const reverted = nav.update(input({ rnp, radios: [fix('DME/DME', 0.5), fix('VOR/DME', 0.5 - metres(55))] }))
  expect(reverted.mode).toBe('VOR/DME')
  expect(transitionAlert('DME/DME', 'VOR/DME', reverted.sensors)).toBe('DME/DME NAV LOST')
  // GPS with integrity outranks every radio mode; losing that integrity to a radio fix with it is immediate too.
  const gpsNav = equipped()
  gpsNav.update(input({ gps: gps(), radios: [fix('DME/DME', 0.3)] }))
  // 2 NM north of HERE against the fix 0.3 NM north (0.3 NM accuracy): the comparison is 1.7 + 0.3 = 2.0, not below RNP 2.
  const lost = gpsNav.update(input({ uncertainGps: { ...gps(offset(HERE, 0, 2)), hilNm: 2.5 }, radios: [fix('DME/DME', 0.3)] }))
  expect(lost.mode).toBe('DME/DME')
  expect(transitionAlert('GPS', 'DME/DME', lost.sensors)).toBe('GPS NAV LOST')
})

/** What makes each mode the best available to the estimator. KALMAN is calibrated at the start of every row. */
const SOURCE: Record<NavMode, Partial<Update>> = {
  GPS: { gps: gps() },
  'DME/DME': { radios: [fix('DME/DME', 0.3)] },
  'VOR/DME': { radios: [fix('VOR/DME', 0.5)] },
  KALMAN: { kalmanReady: true },
  DVS: { dvs: { northKt: 0, eastKt: 100 } },
  DR: {},
}

// Independent alert oracle from plan F3's loss rows; do not derive policy expectations from MODE_TRANSITIONS.
const LOSS_DESTINATIONS: Record<NavMode, NavMode[]> = {
  GPS: ['DME/DME', 'VOR/DME', 'KALMAN', 'DVS', 'DR'],
  'DME/DME': ['VOR/DME', 'KALMAN', 'DVS', 'DR'],
  'VOR/DME': ['DME/DME', 'KALMAN', 'DVS', 'DR'],
  KALMAN: ['DVS', 'DR'], DVS: ['DR'], DR: [],
}
// Independent position/accuracy policy from the approved plan, not from the exported table under test.
const EXPECTED_CONTINUITY: Record<NavMode, 'measured' | 'emulated INS' | 'continued'> = {
  GPS: 'measured', 'DME/DME': 'measured', 'VOR/DME': 'measured',
  KALMAN: 'emulated INS', DVS: 'continued', DR: 'continued',
}
const EXPECTED_HYSTERESIS: Record<string, number> = { 'DME/DME>VOR/DME': 100, 'VOR/DME>DME/DME': 0 }

test('F3: the transition table covers every ordered pair of equipped modes', () => {
  expect(MODE_TRANSITIONS).toHaveLength(NAV_MODES.length * (NAV_MODES.length - 1))
  const keys = new Set(MODE_TRANSITIONS.map(row => `${row.from}>${row.to}`))
  for (const from of NAV_MODES) for (const to of NAV_MODES) if (from !== to) expect(keys.has(`${from}>${to}`), `${from}>${to}`).toBe(true)
})

for (const row of MODE_TRANSITIONS) {
  test(`F3 transition ${row.from} to ${row.to}: ${row.trigger}`, () => {
    const nav = equipped()
    nav.update(input({ gps: gps() }))
    const before = nav.update(input(SOURCE[row.from]))
    expect(before.mode).toBe(row.from)
    const after = nav.update(input(SOURCE[row.to]))
    expect(after.mode).toBe(row.to)
    // Driven with each row's condition met (E-17's receiver failure for VOR/DME); without it the conditional message is absent.
    const expectedAlert = LOSS_DESTINATIONS[row.from].includes(row.to) ? `${row.from} NAV LOST` : null
    const expectedContinuity = EXPECTED_CONTINUITY[row.to]
    const expectedHysteresis = EXPECTED_HYSTERESIS[`${row.from}>${row.to}`] ?? 0
    expect(row.message).toBe(expectedAlert)
    expect(row.continuity).toBe(expectedContinuity)
    expect(row.hysteresisM).toBe(expectedHysteresis)
    expect(transitionAlert(row.from, row.to, after.sensors, { vorDmeReceiversFailed: true })).toBe(expectedAlert)
    if (row.messageCondition) expect(transitionAlert(row.from, row.to, after.sensors)).toBeNull()
    const step = distanceNm(before.position, after.position)
    if (expectedContinuity === 'measured') expect(distanceNm(after.position, SOURCE[row.to].gps?.position ?? SOURCE[row.to].radios![0].position)).toBeLessThan(1e-9)
    else if (expectedContinuity === 'continued') expect(Math.abs(step - 100 / 3600)).toBeLessThan(1e-4)
    else {
      // The emulated INS: calibrated at the first update's GPS (HERE, 100 kt east), then propagated.
      const elapsed = row.from === 'GPS' ? 1 : 2
      expect(distanceNm(after.position, offset(before.mode === 'GPS' ? before.position : HERE, 90, 100 * elapsed / 3600))).toBeLessThan(1e-3)
    }
    // Accuracy-based rows: with both radio modes present, the hysteresis decides, and nothing is lost so nothing is raised.
    if (['DME/DME', 'VOR/DME'].includes(row.from) && ['DME/DME', 'VOR/DME'].includes(row.to)) {
      const both = (better: number) => input({ radios: [fix(row.from as RadioFix['mode'], 0.5), fix(row.to as RadioFix['mode'], 0.5 - metres(better))] })
      const accuracy = equipped()
      expect(accuracy.update(input({ radios: [fix(row.from as RadioFix['mode'], 0.5)] })).mode).toBe(row.from)
      expect(accuracy.update(both(expectedHysteresis - 10)).mode).toBe(row.from)
      const switched = accuracy.update(both(expectedHysteresis + 10))
      expect(switched.mode).toBe(row.to)
      expect(transitionAlert(row.from, row.to, switched.sensors)).toBeNull()
    }
  })
}

test('F3: a station gained or lost in DME/DME moves the position, and the coupled roll command follows it', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  const fly = (seconds: number, until?: () => boolean) => { for (let t = 0; t < seconds; t++) { now += 1000; sim.step(1); if (until?.()) return } }
  fms.setCondition('gpsLost', true)
  fly(3600, () => fms.navState.mode === 'DME/DME' && fms.navState.dmes.length === 2)
  expect(fms.navState.mode).toBe('DME/DME')
  const used = [...fms.navState.dmes], before = { ...fms.position }
  const crossTrack = () => legGeometry(fms.activeLegStart, fms.coordinates(fms.route.legs[0].kind === 'wpt' ? fms.route.legs[0].ident : '')!, fms.position).crossTrack
  // Deselect a station in the fix: the solver takes another pair, and the sensed position steps (range biases differ).
  fms.setInhibited([used[0]])
  expect(fms.navState.dmes).not.toContain(used[0])
  const shift = distanceNm(before, fms.position)
  expect(shift * 1852).toBeGreaterThan(20)
  // The step is not smoothed: the guidance's cross-track is computed from the new sensed position at once (M300 12-16).
  fly(1)
  // Within the one second the aircraft moved since the guidance was computed (about 0.002 NM here).
  expect(Math.abs(sim.guidance.crossTrack - crossTrack())).toBeLessThan(0.005)
})

test('F3 step 3: nominal priority never overrides a better current accuracy (VOR/DME 0.30 in use keeps against DME/DME 0.40)', () => {
  const nav = equipped()
  expect(nav.update(input({ radios: [fix('VOR/DME', 0.3)] })).mode).toBe('VOR/DME')
  expect(nav.update(input({ radios: [fix('DME/DME', 0.4), fix('VOR/DME', 0.3)] })).mode).toBe('VOR/DME')
})

test('F3 step 2 precedes step 3: an uncertain GPS whose NAIM comparison is below the RNP is retained although the backup has integrity', () => {
  const nav = equipped()
  nav.update(input({ gps: gps() }))
  // 0.1 NM from the DME/DME fix of 0.3 NM accuracy: 0.4 < 1.0.
  const kept = nav.update(input({ rnp: 1, uncertainGps: { ...gps(offset(HERE, 90, 0.1)), hilNm: 1.5 }, radios: [{ ...fix('DME/DME', 0.3), position: HERE }] }))
  expect(kept.mode).toBe('GPS')
  expect(kept.uncertain).toBe(true)
  expect(kept.sensors.find(s => s.mode === 'DME/DME')!.integrity).toBe(true)
})

test('F3 annunciation: INT lights when GPS NAV LOST forces a reversion, stays lit on radio, and clears when GPS with integrity returns; crew deselection lights nothing', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  const bus = () => fmsOutputs(fms, sim).gpsIntegrityAnnunciation
  const step = (seconds: number) => { for (let t = 0; t < seconds; t++) { now += 1000; fms.updateNavigation(1) } }
  step(5)
  expect(bus()).toEqual({ value: false, status: 'NORMAL' })
  fms.setCondition('gpsIntegrity', true)
  step(2)
  expect(fms.navState.mode).toBe('GPS')
  expect(fms.navState.uncertain).toBe(true)
  expect(bus()).toEqual({ value: true, status: 'NORMAL' })
  fms.setCondition('gpsIntegrity', false)
  step(2)
  expect(bus()).toEqual({ value: false, status: 'NORMAL' })
  fms.setCondition('gpsLost', true)
  step(2)
  expect(fms.navState.mode).not.toBe('GPS')
  expect(fms.recallList.map(m => m.text)).toContain('GPS NAV LOST')
  expect(bus()).toEqual({ value: true, status: 'NORMAL' })
  step(10)
  expect(bus()).toEqual({ value: true, status: 'NORMAL' })
  fms.setCondition('gpsLost', false)
  step(2)
  expect(fms.navState.mode).toBe('GPS')
  expect(bus()).toEqual({ value: false, status: 'NORMAL' })
  // The crew selects GPS out: GPS NAV LOST, but no integrity annunciation.
  fms.press('INIT_REF'); fms.press('NEXT'); fms.press('LSK5R'); fms.press('LSK6R')
  fms.press('LSK3L'); fms.press('LSK3L'); fms.press('LSK3L')
  step(2)
  expect(fms.gpsNavSelected).toBe(false)
  expect(bus()).toEqual({ value: false, status: 'NORMAL' })
  expect(FMS_OUTPUT_TAGS.gpsIntegrityAnnunciation).toMatchObject({ kind: 'data', validity: 'word' })
  const tag = FMS_OUTPUT_TAGS.gpsIntegrityAnnunciation
  expect('provenance' in tag && tag.provenance).toMatch(/GPS.*integrity/i)
  fms.setCondition('fmsFail', true)
  expect(bus()).toEqual({ value: null, status: 'FAIL' })
})

test('F3 (E-17): VOR/DME NAV LOST needs every VOR or DME receiver failed; a crew-deselected station loses the mode silently', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const setup = () => { const fms = new ScriptedFms(() => new Date(now)); fms.setCondition('apirsFail', true); fms.setCondition('dvsFail', true); fms.setCondition('gpsLost', true); return fms }
  const step = (fms: ScriptedFms, seconds: number) => { for (let t = 0; t < seconds; t++) { now += 1000; fms.updateNavigation(1) } }
  const deselected = setup()
  step(deselected, 3)
  expect(deselected.navState.mode).toBe('VOR/DME')
  deselected.setInhibited(['YOW', 'HWK'])
  step(deselected, 1)
  expect(deselected.navState.mode).toBe('DR')
  expect(deselected.recallList.map(m => m.text)).not.toContain('VOR/DME NAV LOST')
  const failed = setup()
  step(failed, 3)
  expect(failed.navState.mode).toBe('VOR/DME')
  failed.setRadioFaults('nav1', { receiver: 'FAILED' }); failed.setRadioFaults('nav2', { receiver: 'FAILED' })
  step(failed, 1)
  expect(failed.navState.mode).not.toBe('VOR/DME')
  expect(failed.recallList.map(m => m.text)).toContain('VOR/DME NAV LOST')
})
