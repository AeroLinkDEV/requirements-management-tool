import { expect, logicTest as test } from './isolated-client-test'
import type { Navaid } from '../src/fmsCdu/navData'
import { radioFixes } from '../src/fmsCdu/radioNavigation'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { CivilNavigation } from '../src/fmsCdu/civilNavigation'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { applySensorStimulus } from '../src/fmsCdu/sensorStimulus'
import { screenText } from '../src/fmsCdu/screen'
import type { RadioObservation } from '../src/fmsCdu/sensorPorts'

// Stage F plan F7 (M300 1-5, 12-19 to 12-20, 15-3): VOR/DME/TACAN from acknowledged, eligible tunings; M300 15-3's
// accuracy steps; reasonableness without the prior (coverage, and agreement between sources); VOR/DME/TCN STATUS.

const AT = { lat: 0, lon: 0 }, ALT = 3000, NOW = 10_000
const navaid = (ident: string, type: Navaid['type'], bearing: number, distance: number): Navaid => ({ kind: 'navaid', ident, type, name: 'Fixture',
  frequency: '115.00', elevation: { feet: 0, source: 'data', provenance: 'fixture' }, position: { lat: Math.cos(bearing * Math.PI / 180) * distance / 60, lon: Math.sin(bearing * Math.PI / 180) * distance / 60 }, ...(type === 'TACAN' ? { channel: '99X' } : {}) })
const observe = (stations: Navaid[], radialError: Record<string, number> = {}): RadioObservation[] => stations.map(s => ({ station: s,
  slantRangeNm: { at: NOW, sequence: 1, status: 'NORMAL', value: Math.hypot(s.position.lat * 60, s.position.lon * 60, ALT / 6076.12) },
  bearingTrue: { at: NOW, sequence: 1, status: 'NORMAL', value: (Math.atan2(-s.position.lon, -s.position.lat) * 180 / Math.PI + (radialError[s.ident] ?? 0) + 360) % 360 } }))
const vorFix = (observations: RadioObservation[]) => radioFixes(observations, AT, ALT, NOW).find(f => f.mode === 'VOR/DME')

test('F7: measured fixes follow the 95% accuracy model up to 7 NM and step to 1.5 NM beyond it', () => {
  const station: Navaid = { kind: 'navaid', ident: 'VOR', type: 'VORDME', name: 'Analytic range boundary', frequency: '115.00',
    position: { lat: 0, lon: 0 }, elevation: { feet: 3000, source: 'data', provenance: 'literal fixture' } }
  for (const [range, expected] of [[0.001, 0.6000285714285714], [3.5, 0.7], [7, 0.8], [7.000001, 1.5]]) {
    const observation: RadioObservation = { station, slantRangeNm: { at: NOW, sequence: 1, status: 'NORMAL', value: range },
      bearingTrue: { at: NOW, sequence: 1, status: 'NORMAL', value: 90 } }
    const fix = radioFixes([observation], { lat: 0, lon: 0 }, 3000, NOW).find(f => f.mode === 'VOR/DME')
    expect(fix).toBeDefined()
    expect(fix!.anp).toBeCloseTo(expected, 9)
  }
})

test('F7: an unreasonable radial is rejected: two VOR/DMEs that disagree are both left out; one that disagrees with DME/DME is rejected', () => {
  const two = [navaid('VA', 'VORDME', 90, 5), navaid('VB', 'VORDME', 0, 5)]
  expect(vorFix(observe(two))).toBeDefined()
  // A 30-degree radial error moves VA's fix 2.6 NM: more than both accuracies together; neither can be trusted alone.
  expect(vorFix(observe(two, { VA: 30 }))).toBeUndefined()
  const withDmeDme = [navaid('VA', 'VORDME', 90, 5), navaid('D1', 'DME', 0, 10), navaid('D2', 'DME', 120, 10), navaid('D3', 'DME', 240, 10)]
  const fixes = radioFixes(observe(withDmeDme, { VA: 30 }), AT, ALT, NOW)
  expect(fixes.find(f => f.mode === 'DME/DME')).toBeDefined()
  expect(fixes.find(f => f.mode === 'VOR/DME')).toBeUndefined()
  expect(fixes[0].rejected).toEqual(expect.arrayContaining([{ ident: 'VA', reason: 'VOR/DME position disagrees with another source' }]))
})

test('F7: a range beyond line-of-sight coverage is rejected as unreasonable', () => {
  // At 3,000 ft the radio horizon is about 67 NM: a VOR/DME reporting 150 NM cannot be the station.
  const far = navaid('FAR', 'VORDME', 90, 150)
  const fixes = radioFixes(observe([far]), AT, ALT, NOW)
  expect(fixes.find(f => f.mode === 'VOR/DME')).toBeUndefined()
  expect(fixes).toEqual([])
})

function unit(autoVorNavigation = true) {
  let now = Date.UTC(2026, 8, 30, 14)
  const profile = { ...HELICOPTER_PROFILE, configuration: { ...HELICOPTER_PROFILE.configuration, options: { ...HELICOPTER_PROFILE.configuration.options,
    autoVorNavigation: { ...HELICOPTER_PROFILE.configuration.options.autoVorNavigation, configured: autoVorNavigation } } } }
  const fms = new ScriptedFms(() => new Date(now), { profile })
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  return { fms, step, lines: () => screenText(fms.screen()) }
}

test('F7: a pending, unacknowledged tuning gives no fix: the NAV reports its old station until the radio confirms', () => {
  const { fms, step } = unit()
  step(5)
  const before = fms.navStation('nav1')!
  const onNav2 = fms.navStation('nav2')?.ident
  const other = fms.vorDmeStations().find(station => station.ident !== before.ident && station.ident !== onNav2)
  expect(other).toBeDefined()
  fms.setRadio('nav1', other!.frequency)
  expect(fms.radioRequests[0]).toMatchObject({ device: 'nav1', status: 'PENDING' })
  expect(fms.navStation('nav1')!.ident).toBe(before.ident)
  fms.updateNavigation(0)
  expect(fms.radioObservations().find(o => o.station.ident === other!.ident)?.bearingTrue.status ?? 'NCD').not.toBe('NORMAL')
})

test('F7: VOR/DME/TCN STATUS shows each NAV and DME, the TACAN and the position, from NAV STATUS (M300 12-20)', () => {
  const { fms, step, lines } = unit()
  step(6)
  fms.open('NAV_STATUS')
  fms.press('LSK3L')
  expect(lines()[0]).toMatch(/^VOR\/DME\/TCN STATUS\s+1\/1$/)
  expect(lines()[2]).toMatch(/^VOR1 /)
  expect(lines()[3]).toMatch(/^DME1 /)
  expect(lines()[4]).toMatch(/^VOR2 /)
  expect(lines()[5]).toMatch(/^DME2 /)
  expect(lines()[7]).toMatch(/^TCN /)
  const station = fms.navStation('nav1')
  expect(station).toBeDefined()
  expect(lines()[2]).toContain(station!.ident)
  expect(lines()[6]).toBe('NAV1 AUTO      NAV2 AUTO')
  fms.setRadio('nav1', station!.frequency)
  expect(lines()[6]).toBe('NAV1 MAN       NAV2 AUTO')
  expect(lines()[12]).toMatch(/^<NAV STATUS/)
})


test('F7: a prior-resolved DME mirror cannot veto an independent VOR or its NAIM comparison', () => {
  // Three facilities on the east/west axis admit north and south mirror positions five NM away.
  // Literal ranges are sqrt(10^2+5^2), 5 and sqrt(10^2+5^2); the centre VOR points north.
  const north = { lat: 5 / 60, lon: 0 }, south = { lat: -5 / 60, lon: 0 }
  const positions = [{ lat: 0, lon: -10 / 60 }, { lat: 0, lon: 0 }, { lat: 0, lon: 10 / 60 }]
  const observations: RadioObservation[] = positions.map((position, i) => ({
    station: { kind: 'navaid', ident: ['WEST', 'VOR', 'EAST'][i], type: i === 1 ? 'VORDME' : 'DME', name: 'Analytic mirror fixture',
      frequency: '115.00', position, elevation: { feet: 3000, source: 'data', provenance: 'literal analytic fixture' } },
    rangeIdentity: { receiver: 'dme1', channel: 2, frequency: '115.00', commandSequence: 0 },
    slantRangeNm: { at: NOW, sequence: 1, status: 'NORMAL', value: i === 1 ? 5 : Math.sqrt(125) },
    bearingTrue: { at: NOW, sequence: 1, status: i === 1 ? 'NORMAL' : 'NCD', value: i === 1 ? 0 : null } }))
  for (const prior of [south, north]) {
    const fixes = radioFixes(observations, prior, 3000, NOW)
    expect(fixes.find(f => f.mode === 'DME/DME')!.priorResolved).toBe(true)
    const independent = fixes.find(f => f.mode === 'VOR/DME')
    expect(independent).toBeDefined()
    expect(independent!.position.lat).toBeCloseTo(5 / 60, 3)
    expect(independent!.position.lon).toBeCloseTo(0, 5)
    const nav = new CivilNavigation(prior)
    const gps = { position: prior, receiver: 1 as const, accuracy95Nm: 0.05, hilNm: 0.1, northKt: 0, eastKt: 0, at: NOW }
    const air = { headingTrue: 0, tasKt: 100, altitudeFt: 3000 }
    nav.update({ dt: 0, now: NOW, air, airAt: NOW, gps, uncertainGps: null, radio: null, radioApproved: true, rnp: 1 })
    const result = nav.update({ dt: 0, now: NOW, air, airAt: NOW, gps: null, uncertainGps: { ...gps, hilNm: 10 },
      radio: null, radios: fixes, radioApproved: true, rnp: 1 })
    const compared = result.sensors.find(f => f.mode === 'GPS')!.naimComparisonNm
    if (prior === south) {
      expect(result.mode).toBe('DME/DME')
      expect(result.gpsDependent).toBe(true) // The more accurate mirror still inherits the prior; the backup does not.
      expect(result.sensors.find(f => f.mode === 'VOR/DME')!.gpsDependent).toBe(false)
      expect(compared).toBeGreaterThan(9.9)
      expect(compared).toBeLessThan(10.9)
    } else {
      expect(result.mode).toBe('GPS')
      expect(compared).toBeGreaterThan(0.7)
      expect(compared).toBeLessThan(0.8)
    }
  }
})

// A literal two-station dataset drives the real bench receiver, RMS request/ACK and navigation pipeline.
test('F7: native TACAN reception follows acknowledged tuning and preserves a crew-selected station', () => {
 for (const autoEligible of [false, true]) {
  const { fms, step, lines } = unit(autoEligible)
  const stations: Navaid[] = [
    { kind: 'navaid', ident: 'T1', type: 'TACAN', name: 'North fixture', frequency: '115.00', channel: '99X', position: { lat: 5 / 60, lon: 0 }, elevation: { feet: 0, source: 'data', provenance: 'fixture' } },
    { kind: 'navaid', ident: 'T2', type: 'TACAN', name: 'East fixture', frequency: '114.00', channel: '88X', position: { lat: 0, lon: 10 / 60 }, elevation: { feet: 0, source: 'data', provenance: 'fixture' } },
  ]
  expect(fms.loadNavData({ cycle: { id: 'TACAN-LITERAL', from: '2026-09-01', to: '2026-10-28' }, entries: stations, airways: [], procedures: [] })).toEqual({ loaded: 'TACAN-LITERAL' })
  fms.swapCycles(); fms.setAircraft({ position: { lat: 0, lon: 0 }, altitude: 3000 })
  step(5)
  expect(fms.tacanStation()!.ident).toBe('T1')
  if (autoEligible) {
    // Five NM north is true bearing-to 000; this fixed fixture's WMM2025 declination rounds to -4°, so MAG is004.
    // The station-to-aircraft radial would incorrectly print184. The status page reports magnetic bearing.
    fms.open('VOR_DME_STATUS')
    expect(lines()[7].trimEnd()).toBe('TCN  T1  99X 004°/5NM')
    // The T1 native fixture carries the declared -0.02 NM laboratory receiver bias.
    expect(fms.tacanBearingAndRange()!.rangeNm).toBeCloseTo(Math.hypot(5 * Math.PI / 10800 * 3440.065, 3000 / 6076.12) - 0.02, 5)
  }
  expect(fms.radioObservations().find(o => o.station.ident === 'T1')!.bearingTrue.status).toBe(autoEligible ? 'NORMAL' : 'NCD')
  expect(fms.lastRadioFixes.some(fix => fix.mode === 'VOR/DME')).toBe(autoEligible)
  fms.open('VOR_DME_STATUS')
  expect(lines()[8].trim()).toBe('TCN AUTO')
  fms.setRadio('tacan', '88X')
  expect(fms.radioRequests.find(r => r.device === 'tacan')).toMatchObject({ value: '88X', status: 'PENDING' })
  fms.updateNavigation(0)
  expect(fms.tacanStation()!.ident).toBe('T1')
  step(5)
  expect(fms.tacanStation()!.ident).toBe('T2')
  expect(fms.radioRequests.find(r => r.device === 'tacan')).toMatchObject({ value: '88X', status: 'ACK' })
  expect(fms.radioObservations().find(o => o.station.ident === 'T2')!.bearingTrue.status).toBe('NORMAL')
  expect(lines()[8].trim()).toBe('TCN MAN')
  const nativeFix = fms.lastRadioFixes.find(fix => fix.mode === 'VOR/DME')!
  expect(nativeFix.vor).toBe('T2')
  expect(Math.abs(nativeFix.position.lat)).toBeLessThan(0.002)
  expect(Math.abs(nativeFix.position.lon)).toBeLessThan(0.002)
  applySensorStimulus(fms, { kind: 'stationFault', ident: 'T2', component: 'DME', reportedIdent: 'BAD' })
  fms.refreshSensorInput()
  const contradicted = fms.radioObservations().find(observation => observation.station.ident === 'T2')!
  expect(contradicted.reportedDmeIdent).toMatchObject({ status: 'NORMAL', value: 'BAD' })
  expect(contradicted.bearingTrue.status).toBe('NORMAL')
  expect(contradicted.slantRangeNm.status).toBe('NORMAL')
  expect(fms.lastRadioFixes.some(fix => fix.vor === 'T2')).toBe(false)
  expect(fms.tacanBearingAndRange()).toBeNull()
  expect(lines()[7].trimEnd()).toBe('TCN  T2  88X')
  applySensorStimulus(fms, { kind: 'stationFault', ident: 'T2', component: 'DME', reportedIdent: null })
  fms.refreshSensorInput()
  expect(fms.radioObservations().find(observation => observation.station.ident === 'T2')!.reportedDmeIdent).toMatchObject({ status: 'NORMAL', value: 'T2' })
  expect(fms.tacanBearingAndRange()).not.toBeNull()
  expect(fms.lastRadioFixes.some(fix => fix.vor === 'T2')).toBe(true)
  fms.setRadioFaults('tacan', { receiver: 'FAILED' }); fms.updateNavigation(0)
  expect(fms.tacanStation()).toBeUndefined()
  expect(fms.tacanBearingAndRange()).toBeNull()
  fms.setRadioFaults('tacan', { receiver: 'NORMAL' }); step(5)
  expect(fms.tacanStation()!.ident).toBe('T2')
  expect(fms.radioObservations().find(o => o.station.ident === 'T2')!.bearingTrue.status).toBe('NORMAL')
 }
})
