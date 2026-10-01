import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { RADIO_MESSAGE_ROWS, RADIO_NAMES, SCAN_CHANNELS, RadioManagementSystem } from '../src/fmsCdu/radioManagement'
import { APPENDIX_E } from '../src/fmsCdu/appendixE'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import { bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { CivilNavigation } from '../src/fmsCdu/civilNavigation'
import { radioFixes, type RadioMotion } from '../src/fmsCdu/radioNavigation'
import type { RadioObservation, SensorFrame } from '../src/fmsCdu/sensorPorts'
import type { Navaid } from '../src/fmsCdu/navData'

// Stage F plan F8a (M300 13-1 to 13-26, 3-26, Appendix E), on the shared civil RMS of #1350: each radio device answers
// with feedback, its separate internal states (plan C3) decide what it reports and which Appendix E rows are met, and navigation measures only through what the radios report.
// The FMS is the only tuning source (DEC-150).

const T0 = Date.UTC(2026, 8, 30, 14)
const LATENCY = 0.25, TIMEOUT = 2

function rmsAt() {
  let now = T0
  const rms = new RadioManagementSystem(() => now, () => true, () => {}, LATENCY, TIMEOUT)
  return { rms, one: rms.port(1), two: rms.port(2), advance: (seconds: number) => { now += seconds * 1000; rms.tick() } }
}

test('F8a: a tune request stays pending until the radio acknowledges it, and the state is the acknowledged frequency', () => {
  const { one, advance } = rmsAt()
  one.tune('nav1', '115.80')
  expect(one.requests[0]).toMatchObject({ device: 'nav1', value: '115.80', status: 'PENDING' })
  expect(one.state.nav1).toBe('113.90')
  advance(LATENCY)
  expect(one.requests[0].status).toBe('ACK')
  expect(one.receiving('nav1')).toBe('115.80')
})

test('C3: a command-only timeout raises CONTROL LOST (E-13) on the requesting computer alone; the live bus keeps the measurements and no FAILED row is met', () => {
  const { rms, one, two, advance } = rmsAt()
  rms.setFaults('nav1', { controlPath: 'LOST' })
  expect(one.drainEvents()).toEqual([])
  two.tune('nav1', '115.80')
  advance(TIMEOUT - 0.5)
  expect(two.requests[0].status).toBe('PENDING')
  advance(0.5)
  expect(two.requests[0].status).toBe('TIMEOUT')
  expect(two.drainEvents()).toEqual([{ kind: 'alert', text: 'NAV1 CONTROL LOST', row: 'E-13' }])
  expect(one.drainEvents()).toEqual([])
  expect(one.receiving('nav1')).toBe('113.90')
  advance(5)
  expect(two.drainEvents()).toEqual([])
})

test('C3: an ADF receiver failure meets both E-2 (alert, with its configuration and inhibit) and E-21 (advisory) on both computers', () => {
  const { rms, one, two } = rmsAt()
  rms.setFaults('adf', { receiver: 'FAILED' })
  const expected = [
    { kind: 'alert', text: 'ADF1 CONTROL LOST', row: 'E-2', inhibit: 'polarOrRoll', configuredBy: 'adfControlLostAlert' },
    { kind: 'advisory', text: 'ADF1 FAILED', row: 'E-21' },
  ]
  expect(one.drainEvents()).toEqual(expected)
  expect(two.drainEvents()).toEqual(expected)
  expect(one.receiving('adf')).toBeNull()
  // Still met: nothing new. Cleared and met again: raised again.
  rms.setFaults('adf', { measurementBus: 'LOST' })
  expect(one.drainEvents()).toEqual([])
  rms.setFaults('adf', { receiver: 'NORMAL', measurementBus: 'NORMAL' })
  expect(one.receiving('adf')).toBe('0350')
  rms.setFaults('adf', { measurementBus: 'LOST' })
  expect(one.drainEvents().map(event => event.row)).toEqual(['E-2', 'E-21'])
})

test('C3: each FAILED advisory is raised from its own row: DME on its bus (E-23), NAV on its radio or bus (E-27)', () => {
  const { rms, one } = rmsAt()
  // A DME receiver failure stops its ranges, but E-23 names only the communication bus: no advisory.
  rms.setFaults('dme1', { receiver: 'FAILED' })
  expect(one.dmeReceiving('dme1')).toBe(false)
  expect(one.drainEvents()).toEqual([])
  rms.setFaults('dme1', { measurementBus: 'LOST' })
  expect(one.drainEvents()).toEqual([{ kind: 'advisory', text: 'DME1 FAILED', row: 'E-23' }])
  rms.setFaults('nav2', { receiver: 'FAILED' })
  expect(one.drainEvents()).toEqual([{ kind: 'advisory', text: 'NAV2 FAILED', row: 'E-27' }])
  expect(one.receiving('nav2')).toBeNull()
})

test('C3: a superseded command raises nothing, a rejected one is amber-worthy but no fault, and the #1350 device failure is a lost control path', () => {
  const { rms, one, two, advance } = rmsAt()
  one.tune('nav2', '112.80')
  two.tune('nav2', '113.20')
  expect(one.requests[0].status).toBe('SUPERSEDED')
  advance(LATENCY)
  expect(two.requests[0].status).toBe('ACK')
  rms.rejectNext('adf')
  one.tune('adf', '0400')
  advance(LATENCY)
  expect(one.requests[0].status).toBe('REJECTED')
  expect([...one.drainEvents(), ...two.drainEvents()]).toEqual([])
  rms.injectFailure('com1', true)
  expect(rms.faults('com1')).toMatchObject({ controlPath: 'LOST', measurementBus: 'NORMAL', receiver: 'NORMAL' })
  expect(one.receiving('com1')).toBe('121.500')
})

test('C3: a radio under TEST gives no navigation measurements until its result', () => {
  const { one, advance } = rmsAt()
  one.pressTest('nav1'); one.pressTest('nav1')
  expect(one.testState('nav1')).toBe('STARTED')
  expect(one.receiving('nav1')).toBeNull()
  advance(3)
  expect(one.testState('nav1')).toBe('PASS')
  expect(one.receiving('nav1')).toBe('113.90')
})

function standalone() {
  let now = T0
  const fms = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  return { fms, step }
}

test('F8a: with both DME transceivers failed there is no range at all: no DME/DME, no VOR/DME', () => {
  const { fms, step } = standalone()
  fms.setCondition('gpsLost', true)
  step(30)
  expect(['DME/DME', 'VOR/DME']).toContain(fms.navState.mode)
  fms.setRadioFaults('dme1', { measurementBus: 'LOST' })
  fms.setRadioFaults('dme2', { measurementBus: 'LOST' })
  step(30)
  // No radio mode remains (KALMAN or DVS may carry the position: F11).
  expect(['DME/DME', 'VOR/DME']).not.toContain(fms.navState.mode)
  expect(fms.lastAdvisories).toEqual(expect.arrayContaining(['DME1 FAILED', 'DME2 FAILED']))
  expect(fms.radioObservations().every(observation => observation.slantRangeNm.status !== 'NORMAL')).toBe(true)
})

test('F8a: NAV AUTO tunes the nearest VOR/DME; its bearing feeds VOR/DME under autoVorNavigation, and only in MAN without it (M300 12-19)', () => {
  const bearingOf = (fms: ScriptedFms, ident: string) => fms.radioObservations().find(observation => observation.station.ident === ident)?.bearingTrue.status
  const { fms, step } = standalone()
  step(5)
  const nearest = fms.nearestVorDme()!
  expect(fms.navRadioMode('nav1')).toBe('AUTO')
  expect(fms.radioReceiving('nav1')).toBe(nearest.frequency)
  expect(bearingOf(fms, nearest.ident)).toBe('NORMAL')
  let now = T0
  const options = { ...HELICOPTER_PROFILE.configuration.options, autoVorNavigation: { ...HELICOPTER_PROFILE.configuration.options.autoVorNavigation, configured: false } }
  const manual = new ScriptedFms(() => new Date(now), { profile: { ...HELICOPTER_PROFILE, configuration: { ...HELICOPTER_PROFILE.configuration, options } } })
  const run = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; manual.updateNavigation(1) } }
  run(5)
  expect(manual.navRadioMode('nav1')).toBe('AUTO')
  expect(manual.radioReceiving('nav1')).toBe(nearest.frequency)
  expect(bearingOf(manual, nearest.ident)).not.toBe('NORMAL')
  manual.setRadio('nav1', nearest.frequency)
  run(5)
  expect(manual.navRadioMode('nav1')).toBe('MAN')
  expect(bearingOf(manual, nearest.ident)).toBe('NORMAL')
})

test('F8a: a crew entry puts NAV in MAN; bearings come only from reported stations, and a stuck NAV raises CONTROL LOST', () => {
  const { fms, step } = standalone()
  step(5)
  const other = fms.vorDmeStations().find(station => station.ident !== fms.nearestVorDme()!.ident)!
  fms.setRadio('nav1', other.frequency)
  step(1)
  expect(fms.navRadioMode('nav1')).toBe('MAN')
  expect(fms.radioReceiving('nav1')).toBe(other.frequency)
  // In MAN the FMS's own AUTO tuning leaves the NAV where the crew put it.
  step(5)
  expect(fms.radioReceiving('nav1')).toBe(other.frequency)
  const reported = [fms.radioReceiving('nav1'), fms.radioReceiving('nav2')]
  for (const observation of fms.radioObservations().filter(entry => entry.bearingTrue.status === 'NORMAL')) {
    expect(reported).toContain(observation.station.frequency)
  }
  fms.setRadioFaults('nav1', { controlPath: 'LOST' })
  fms.setRadio('nav1', fms.nearestVorDme()!.frequency)
  step(3)
  expect(fms.radioReceiving('nav1')).toBe(other.frequency)
  expect(fms.recallList.map(message => message.text)).toContain('NAV1 CONTROL LOST')
})

test('F8a: the RADIO page shows a frequency being tuned in inverse, then the acknowledged one (M300 13-3)', () => {
  const { fms, step } = standalone()
  fms.press('RADIO')
  fms.setScratch('118.30')
  fms.press('LSK1L')
  const line = fms.screen()[2]
  expect(screenText(fms.screen())[2]).toMatch(/^118\.300/)
  expect(JSON.stringify(line)).toMatch(/"inverse":true/)
  step(1)
  expect(fms.radioState.com1).toBe('118.300')
  expect(JSON.stringify(fms.screen()[2])).not.toMatch(/"inverse":true/)
})

test('F8a: two computers share the NAVs: FMS 1 auto-tunes them, and both navigate on what the shared radios report', () => {
  let now = T0
  const system = new DualFmsSystem(() => new Date(now))
  const [one, two] = system.computers
  for (let i = 0; i < 5; i++) { now += 1000; system.tick() }
  const nearest = one.nearestVorDme()!
  expect(one.radioState.nav1).toBe(nearest.frequency)
  expect(two.radioState.nav1).toBe(nearest.frequency)
  system.rms.setFaults('nav1', { receiver: 'FAILED' })
  for (let i = 0; i < 2; i++) { now += 1000; system.tick() }
  expect(one.lastAdvisories).toContain('NAV1 FAILED')
  expect(two.lastAdvisories).toContain('NAV1 FAILED')
})

test('C3: the computer applies E-2 configuration and inhibits: no ADF CONTROL LOST above 20 degrees of roll, in the polar area or unconfigured; E-21 still raised', () => {
  const raised = (fms: ScriptedFms) => fms.recallList.map(message => message.text)
  const banked = standalone()
  banked.step(2)
  banked.fms.setAircraft({ bank: 25 })
  banked.fms.setRadioFaults('adf', { receiver: 'FAILED' })
  banked.step(1)
  expect(raised(banked.fms)).not.toContain('ADF1 CONTROL LOST')
  expect(banked.fms.lastAdvisories).toContain('ADF1 FAILED')
  const level = standalone()
  level.step(2)
  level.fms.setRadioFaults('adf', { receiver: 'FAILED' })
  level.step(1)
  expect(raised(level.fms)).toContain('ADF1 CONTROL LOST')
  let now = T0
  const options = { ...HELICOPTER_PROFILE.configuration.options, adfControlLostAlert: { ...HELICOPTER_PROFILE.configuration.options.adfControlLostAlert, configured: false } }
  const unconfigured = new ScriptedFms(() => new Date(now), { profile: { ...HELICOPTER_PROFILE, configuration: { ...HELICOPTER_PROFILE.configuration, options } } })
  now += 2000; unconfigured.updateNavigation(2)
  unconfigured.setRadioFaults('adf', { receiver: 'FAILED' })
  now += 1000; unconfigured.updateNavigation(1)
  expect(raised(unconfigured)).not.toContain('ADF1 CONTROL LOST')
  expect(unconfigured.lastAdvisories).toContain('ADF1 FAILED')
})

test('C3: a healthy ADF tuned to nothing in range shows no bearing and raises no fault message', () => {
  const { fms, step } = standalone()
  fms.setRadio('adf', '1750')
  step(3)
  expect(fms.adfBearing('adf')).toBeNull()
  expect(fms.recallList.map(message => message.text).filter(text => /ADF/.test(text))).toEqual([])
  expect(fms.lastAdvisories.filter(text => /ADF/.test(text))).toEqual([])
})

test('C3: each radio message row names the Appendix E page the shared catalogue gives for its message', () => {
  for (const row of RADIO_MESSAGE_ROWS) for (const device of row.devices) {
    const text = row.text(RADIO_NAMES[device])
    const source = APPENDIX_E[text]
    // Alerts are in the catalogue; the FAILED maintenance advisories are raised outside the alert library.
    if (row.kind === 'alert') expect(source, text).toEqual({ page: row.row })
    else if (source) expect(source, text).toEqual({ page: row.row })
  }
  // The command-timeout rows: NAV E-13, ADF E-2.
  expect(APPENDIX_E['NAV1 CONTROL LOST']).toEqual({ page: 'E-13' })
  expect(APPENDIX_E['ADF2 CONTROL LOST']).toEqual({ page: 'E-2' })
})

const roster = ['A', 'B', 'C', 'D', 'E', 'F'].map((ident, i) => ({ ident, frequency: `11${i}.00` }))

test('C3: a six-station roster is spread over the four scan channels; two channels alternate two stations each dwell', () => {
  let now = T0 - (T0 % 4000)
  const rms = new RadioManagementSystem(() => now, () => true, () => {}, 0.25, 2)
  rms.setScanRoster(roster, 2)
  expect(SCAN_CHANNELS).toHaveLength(4)
  const onAir = () => rms.scanning().map(entry => `${entry.device}/${entry.channel}:${entry.ident}`).sort()
  expect(onAir()).toEqual(['dme1/2:A', 'dme1/3:C', 'dme2/2:B', 'dme2/3:D'])
  now += 2000
  expect(onAir()).toEqual(['dme1/2:E', 'dme1/3:C', 'dme2/2:F', 'dme2/3:D'])
  // HOLD keeps channel 1 on the held frequency and does not touch the scan channels.
  rms.setDmeHold('dme2', true)
  expect(onAir()).toEqual(['dme1/2:E', 'dme1/3:C', 'dme2/2:F', 'dme2/3:D'])
  // At most six, and no station twice.
  rms.setScanRoster([...roster, { ident: 'G', frequency: '117.00' }, { ident: 'A', frequency: '110.00' }], 2)
  expect(rms.scanRoster().map(station => station.ident)).toEqual(['A', 'B', 'C', 'D', 'E', 'F'])
})

test('C3: a DME under TEST or with its bus lost hands its stations to the other DME\'s channels, which dwell on three each', () => {
  let now = T0 - (T0 % 6000)
  const rms = new RadioManagementSystem(() => now, () => true, () => {}, 0.25, 2)
  rms.setScanRoster(roster, 2)
  rms.pressTest('dme1'); rms.pressTest('dme1')
  const seen = new Set<string>()
  for (let dwell = 0; dwell < 3; dwell++) {
    const on = rms.scanning()
    expect(on.map(entry => entry.device)).toEqual(['dme2', 'dme2'])
    on.forEach(entry => seen.add(entry.ident))
    now += 2000
  }
  expect([...seen].sort()).toEqual(['A', 'B', 'C', 'D', 'E', 'F'])
})


// Authoring gate (R3-01): epoch alignment, immutable original identity/expiry and compensation dependency are one
// radio integration contract. The recovered tests asserted only an accuracy widening and could pass without motion.
// Literal eastward displacement, stale-source and exact expiry controls fail those regressions; production solvers
// and the measured-input estimator boundary need no test-only getter.
const CACHE_AT = { lat: 0, lon: 0 }
const cacheParameters = HELICOPTER_PROFILE.parameters
function rangesAt(at: number): RadioObservation[] {
  return [90, 0, 225].map((course, index) => {
    const station: Navaid = { kind: 'navaid', type: 'DME', ident: `C${index}`, name: 'Range fixture', frequency: `11${index}.00`,
      position: offset(CACHE_AT, course, 10), elevation: { feet: 0, source: 'data', provenance: 'fixture survey' } }
    return { station, rangeIdentity: { receiver: 'dme1', channel: index === 0 ? 2 : 3, frequency: station.frequency, commandSequence: 7 },
      slantRangeNm: { at, sequence: 9, status: 'NORMAL', value: 10 }, bearingTrue: { at, sequence: 9, status: 'NCD', value: null } }
  })
}
const cachedFix = (observations: RadioObservation[], now: number, motion: RadioMotion | null) =>
  radioFixes(observations, CACHE_AT, 0, now, cacheParameters, { rangeMaxAgeS: 4, motion }).find(fix => fix.mode === 'DME/DME')

test('C3: compensation aligns ranges to the new epoch without renewing observation identity, age or expiry', () => {
  const observations = rangesAt(T0), original = structuredClone(observations)
  const motion: RadioMotion = { source: 'DVS', at: T0 + 1000, northKt: 0, eastKt: 600, gpsDependent: false }
  const fix = cachedFix(observations, T0 + 1000, motion)!
  expect(fix.position.lat).toBeCloseTo(0, 3)
  expect(fix.position.lon).toBeCloseTo(1 / 360, 3) // 600 kt for 1 s = 1/6 NM east = 1/360 degree.
  expect(fix.at).toBe(T0 + 1000)
  expect(fix.oldestAt).toBe(T0)
  expect(fix.observations).toEqual(original)
  expect(observations).toEqual(original)
  expect(fix.motion).toEqual(motion)
  expect(fix.naimEligible).toBe(true)
  const atExpiry = cachedFix(observations, T0 + 4000, { ...motion, at: T0 + 4000 })!
  expect(atExpiry.oldestAt).toBe(T0)
  expect(cachedFix(observations, T0 + 4001, { ...motion, at: T0 + 4001 })).toBeUndefined()
  // Source time is its own clock; a fresh fix epoch must not refresh it.
  const staleMotion = cachedFix(observations, T0 + 3001, { ...motion, at: T0 })!
  expect(staleMotion.motion).toBeNull()
  expect(staleMotion.naimEligible).toBe(false)
})

test('C3: dependent motion can navigate but cannot supply a GPS-independent NAIM comparison; accuracy never clears dependency', () => {
  const observations = rangesAt(T0), now = T0 + 1000
  const source: RadioMotion = { source: 'AIR_WIND', at: now, northKt: 0, eastKt: 600, gpsDependent: true }
  const dependent = cachedFix(observations, now, source)!
  const independent = cachedFix(observations, now, { ...source, source: 'DVS', gpsDependent: false })!
  const check = (radio: typeof dependent) => {
    const nav = new CivilNavigation(CACHE_AT)
    return nav.update({ dt: 0, air: null, gps: null, uncertainGps: { position: offset(CACHE_AT, 0, 3), accuracy95Nm: 0.02,
      hilNm: 2, receiver: 1, northKt: null, eastKt: null }, radios: [radio], radio: null, radioApproved: true, rnp: 1, now, naimMaxAgeS: 4 })
  }
  const rejected = check(dependent)
  expect(rejected.mode).toBe('GPS')
  expect(rejected.sensors.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeNull()
  expect(rejected.sensors.find(sensor => sensor.mode === 'DME/DME')!.gpsDependent).toBe(true)
  const compared = check(independent)
  expect(compared.mode).toBe('DME/DME')
  expect(compared.sensors.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeGreaterThan(3)
  expect(compared.gpsDependent).toBe(false)
  const staleComparison = check({ ...independent, oldestAt: now - 4001 })
  expect(staleComparison.mode).toBe('GPS')
  expect(staleComparison.sensors.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeNull()
})

test('C3: legacy receiver words without identity remain fresh-only and never qualify for NAIM', () => {
  const observations = rangesAt(T0).map(({ rangeIdentity: _identity, ...observation }) => observation)
  const fresh = cachedFix(observations, T0, null)!
  expect(fresh).toBeDefined()
  expect(fresh.naimEligible).toBe(false)
})

function measuredCache() {
  let now = T0
  const seed = new ScriptedFms(() => new Date(now))
  seed.setAircraft({ position: { lat: 45.5, lon: -74.9 }, altitude: 6000 })
  for (let i = 0; i < 5; i++) { now += 1000; seed.updateNavigation(1) }
  let frame: SensorFrame = structuredClone(seed.navigationInputs!)
  const range = frame.radios.find(observation => observation.slantRangeNm.status === 'NORMAL' && observation.bearingTrue.status === 'NORMAL')!
  expect(range).toBeDefined()
  const healthyGps = structuredClone(frame.gps)
  frame.radios = frame.radios.filter(observation => observation.slantRangeNm.status === 'NORMAL')
    .map(observation => ({ ...observation, rangeIdentity: { ...observation.rangeIdentity!, receiver: 'dme1', channel: 2 } }))
  const missingGps = frame.gps.map(word => ({ ...word, status: 'NCD' as const, value: null }))
  frame.gps = [missingGps[0], missingGps[1]]
  frame.air.value = { headingTrue: 90, tasKt: 600, altitudeFt: frame.air.value!.altitudeFt }
  frame.dvs = { at: now, sequence: 1, status: 'NORMAL', value: { alongKt: 600, acrossKt: 0 } }
  const unit = new ScriptedFms(() => new Date(now), { sensors: { read: () => structuredClone(frame) } })
  const step = (milliseconds: number, dvs = true) => {
    now += milliseconds
    frame.air.at = now; frame.air.sequence++
    frame.dvs = { ...frame.dvs!, at: now, sequence: frame.dvs!.sequence + 1, status: dvs ? 'NORMAL' : 'FAIL' }
    frame.radios = []
    unit.updateNavigation(milliseconds / 1000)
  }
  return { unit, step, range: frame.radios[0], frame, healthyGps, now: () => now }
}

test('C3: RMS station cache keeps ranges after the channels move on, uses independent DVS plus crew current, and expires without renewed ranges', () => {
  const { unit, step } = measuredCache()
  expect(unit.navState.mode).toBe('DME/DME')
  const start = unit.position
  unit.setWaterCurrent(90, 60)
  step(1000)
  expect(unit.navState.mode).toBe('DME/DME')
  expect(distanceNm(start, unit.position)).toBeCloseTo(11 / 60, 2) // (600 + 60) kt for 1 second.
  expect(unit.sensorSolutions.find(sensor => sensor.mode === 'DME/DME')!.gpsDependent).toBe(false)
  step(5000)
  expect(unit.navState.mode).toBe('DME/DME')
  step(1)
  expect(unit.navState.mode).not.toBe('DME/DME')
})

test('C3: old arrivals and legacy identity-less ranges cannot populate the cache; TEST, bus loss and station deselection invalidate it', () => {
  for (const invalidate of ['bus', 'test', 'station', 'identity', 'arrival'] as const) {
    const { unit, step, range, frame } = measuredCache()
    expect(unit.navState.mode).toBe('DME/DME')
    if (invalidate === 'bus') unit.setRadioFaults(range.rangeIdentity!.receiver, { measurementBus: 'LOST' })
    if (invalidate === 'test') { unit.radioPort!.pressTest(range.rangeIdentity!.receiver); unit.radioPort!.pressTest(range.rangeIdentity!.receiver) }
    if (invalidate === 'station') unit.setInhibited([range.station.ident])
    if (invalidate === 'identity' || invalidate === 'arrival') {
      const fresh = structuredClone(frame)
      fresh.radios = [{ ...range, ...(invalidate === 'identity' ? { rangeIdentity: undefined }
        : { slantRangeNm: { ...range.slantRangeNm, at: range.slantRangeNm.at - 2001 } }) }]
      let candidateNow = fresh.air.at
      const candidate = new ScriptedFms(() => new Date(candidateNow), { sensors: { read: () => fresh } })
      fresh.radios = []
      candidateNow += 1000; fresh.air.at = candidateNow
      candidate.updateNavigation(1)
      expect(candidate.navState.mode).not.toBe('VOR/DME')
      continue
    }
    step(1000)
    expect(unit.navState.mode).not.toBe('DME/DME')
  }
})


test('C3: successive independent radio observations take priority over a conflicting DVS velocity', () => {
  const { unit, frame, step, now } = measuredCache()
  const measured = unit.position
  const ranges = structuredClone(frame.radios)
  const arrive = (position: typeof measured) => {
    frame.radios = ranges.map(observation => ({ ...observation,
      slantRangeNm: { ...observation.slantRangeNm, at: now(), sequence: observation.slantRangeNm.sequence + 1,
        value: Math.hypot(distanceNm(position, observation.station.dmePosition ?? observation.station.position),
          (frame.air.value!.altitudeFt - observation.station.elevation.feet) / 6076.12) },
      bearingTrue: { ...observation.bearingTrue, at: now(), sequence: observation.bearingTrue.sequence + 1,
        value: bearingDeg(observation.station.position, position) } }))
    unit.updateNavigation(0)
  }
  // Two independently generated, unbiassed observations move 0.1 NM east in one second (360 kt).
  step(1000); arrive(measured)
  step(1000); arrive(offset(measured, 90, 0.1))
  const radioAtSecond = unit.position
  step(1000)
  expect(unit.navState.mode).toBe('DME/DME')
  expect(distanceNm(radioAtSecond, unit.position)).toBeCloseTo(0.1, 2)
  expect(unit.sensorSolutions.find(sensor => sensor.mode === 'DME/DME')!.gpsDependent).toBe(false)
})

test('C3: GPS-derived last wind tags cached radio motion dependent, so GPS-only changes never establish an independent NAIM backup', () => {
  const { unit, frame, healthyGps, step } = measuredCache()
  frame.radios = []
  frame.dvs = { ...frame.dvs!, status: 'FAIL', value: null }
  frame.gps = healthyGps
  unit.updateNavigation(0)
  expect(unit.navState.mode).toBe('GPS')
  step(1000, false)
  expect(unit.sensorSolutions.find(sensor => sensor.mode === 'DME/DME')!.gpsDependent).toBe(true)
  // Bias GPS alone by 3 NM north, then reject its integrity. Old radio evidence cannot assess its own aiding source.
  const biased = frame.gps.map(word => {
    const bus = structuredClone(word.value!)
    bus['110'] = { ...bus['110'], value: bus['110'].value! + 3 / 60 }
    bus['130'] = { ...bus['130'], value: 10 }
    return { ...word, value: bus }
  })
  frame.gps = [biased[0], biased[1]]
  step(1000, false)
  expect(unit.navState.mode).toBe('GPS')
  expect(unit.navState.uncertain).toBe(true)
  expect(unit.sensorSolutions.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeNull()
  expect(unit.sensorSolutions.find(sensor => sensor.mode === 'DME/DME')!.gpsDependent).toBe(true)
})


// Raw frame words must pass the same arrival/receiver admission as cached ranges. Keeping those words present
// catches the bypass that cache-only invalidation tests cannot see.
for (const rejected of ['stale arrival', 'receiver bus loss'] as const) test(`C3: raw admission rejects ${rejected}, with a fresh healthy positive control`, () => {
  const { frame } = measuredCache()
  frame.dvs = { ...frame.dvs!, status: 'FAIL', value: null }
  const healthy = new ScriptedFms(() => new Date(frame.air.at), { sensors: { read: () => frame } })
  expect(healthy.navState.mode).toBe('DME/DME')
  if (rejected === 'stale arrival') {
    const older = structuredClone(frame)
    older.radios = older.radios.map(observation => ({ ...observation,
      slantRangeNm: { ...observation.slantRangeNm, at: observation.slantRangeNm.at - 2001 },
      bearingTrue: { ...observation.bearingTrue, at: observation.bearingTrue.at - 2001 } }))
    const stale = new ScriptedFms(() => new Date(older.air.at), { sensors: { read: () => older } })
    expect(stale.navState.mode).toBe('DR')
  } else {
    for (const receiver of ['dme1', 'dme2'] as const) healthy.setRadioFaults(receiver, { measurementBus: 'LOST' })
    healthy.updateNavigation(0)
    expect(healthy.navState.mode).toBe('DR')
    expect(healthy.sensorSolutions.some(sensor => sensor.mode === 'DME/DME')).toBe(false)
    for (const receiver of ['dme1', 'dme2'] as const) healthy.setRadioFaults(receiver, { measurementBus: 'NORMAL' })
    healthy.updateNavigation(0)
    expect(healthy.navState.mode).toBe('DME/DME')
  }
})


function radioWind() {
  const nav = new CivilNavigation(CACHE_AT)
  const radioInput = { dt: 1, air: { headingTrue: 90, tasKt: 100, altitudeFt: 0 }, gps: null, uncertainGps: null,
    radioApproved: true, rnp: 2 }
  nav.update({ ...radioInput, now: T0, radio: cachedFix(rangesAt(T0), T0, null)! })
  const at = T0 + 1000, position = offset(CACHE_AT, 90, 0.1)
  const next = rangesAt(at).map(observation => ({ ...observation,
    slantRangeNm: { ...observation.slantRangeNm, value: distanceNm(position, observation.station.position) } }))
  nav.update({ ...radioInput, now: at, radio: cachedFix(next, at, null)! })
  expect(nav.current.windComputed).toBe(true)
  expect(nav.measuredWind).toMatchObject({ at, gpsDependent: false })
  return nav
}

for (const adopted of ['peer GPS', 'manual'] as const) test(`C3: ${adopted} operational wind adoption clears the earlier radio wind qualification`, () => {
  const nav = radioWind()
  const original = nav.measuredWind!
  const solution = { ...nav.current, gpsDependent: adopted === 'peer GPS' }
  // accept is the common production boundary for receiveSystemNavigation and applyEnteredWind.
  nav.accept(solution, { north: 250, east: 0 })
  expect(nav.windEstimate).toEqual({ north: 250, east: 0 })
  expect(nav.measuredWind).toBeNull()
  expect(original).toMatchObject({ at: T0 + 1000, gpsDependent: false })
})

test('C3: recomputing an uncompensated cached epoch cannot manufacture fresh independent wind', () => {
  const nav = new CivilNavigation(CACHE_AT), observations = rangesAt(T0)
  const input = { dt: 1, air: { headingTrue: 90, tasKt: 100, altitudeFt: 0 }, gps: null, uncertainGps: null,
    radioApproved: true, rnp: 2 }
  nav.update({ ...input, now: T0, radio: cachedFix(observations, T0, null)! })
  const result = nav.update({ ...input, now: T0 + 1000, radio: cachedFix(observations, T0 + 1000, null)! })
  expect(result.mode).toBe('DME/DME')
  expect(result.windComputed).toBe(false)
  expect(nav.measuredWind).toBeNull()
  radioWind() // Fresh independent fixes still compute a source-qualified wind.
})
