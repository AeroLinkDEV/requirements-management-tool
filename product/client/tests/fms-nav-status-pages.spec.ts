import { expect, logicTest as test } from './isolated-client-test'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm, offset } from '../src/fmsCdu/fmsModel'
import type { Navaid } from '../src/fmsCdu/navData'
import { radioFixes, type DmeStationStatus } from '../src/fmsCdu/radioNavigation'
import type { RadioObservation, SensorFrame } from '../src/fmsCdu/sensorPorts'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'

// Stage F plan F9 (M300 5-26, 12-21 to 12-24, 17-2 to 17-3): NAV STATUS INDEX with prompts only for configured
// equipment, DESELECT and GPS DESELECT and what deselection does, and the KALMAN STATUS and DVS STATUS pages.

function unit(profile = HELICOPTER_PROFILE) {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now), { profile })
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  return { fms, step, lines: () => screenText(fms.screen()), enter: (text: string, key: Parameters<ScriptedFms['press']>[0]) => { fms.setScratch(text); fms.press(key) } }
}
const withoutOption = (option: string) => {
  const options = { ...HELICOPTER_PROFILE.configuration.options, [option]: { ...(HELICOPTER_PROFILE.configuration.options as Record<string, object>)[option], configured: false } }
  return { ...HELICOPTER_PROFILE, configuration: { ...HELICOPTER_PROFILE.configuration, options } } as typeof HELICOPTER_PROFILE
}

test('F9: INIT REF and PROGRESS lead to NAV STATUS INDEX, whose prompts exist only for configured equipment (M300 5-26)', () => {
  const { fms, lines } = unit()
  fms.press('INIT_REF'); fms.press('NEXT'); fms.press('LSK5R')
  expect(lines()[0]).toMatch(/^NAV STATUS INDEX\s+1\/1$/)
  expect(lines()[2]).toMatch(/^<PREDICT RAIM\s+GPS>$/)
  expect(lines()[4]).toMatch(/DME>$/)
  expect(lines()[6]).toMatch(/^<DVS\s+VOR\/DME\/TCN>$/)
  expect(lines()[10]).toMatch(/KALMAN>$/)
  expect(lines()[12]).toMatch(/^<INIT\/REF\s+DESELECT>$/)
  fms.press('PROG'); fms.press('LSK6R')
  expect(lines()[0]).toMatch(/^NAV STATUS INDEX/)
  // Without a Doppler or a KALMAN mode configured there is no prompt for them.
  const bare = unit(withoutOption('kalman'))
  bare.fms.open('NAV_STATUS_INDEX')
  expect(bare.lines()[10]).not.toMatch(/KALMAN/)
  const noDvs = unit(withoutOption('doppler'))
  noDvs.fms.open('NAV_STATUS_INDEX')
  expect(noDvs.lines()[6]).not.toMatch(/<DVS/)
  noDvs.fms.press('LSK3L')
  expect(noDvs.lines()[0]).toMatch(/^NAV STATUS INDEX/)
})

test('F9: DESELECT TAS stops the wind computation and leaves GPS navigation unaffected (M300 17-3)', () => {
  const { fms, step, lines } = unit()
  step(5)
  expect(fms.windComputed).toBe(true)
  const before = { ...fms.position }
  fms.open('DESELECT')
  expect(lines()[0]).toMatch(/^DESELECT\s+1\/1$/)
  expect(lines()[2]).toMatch(/^>VALID\s+GPS>$/)
  fms.press('LSK1L')
  expect(fms.deselectedInputs.has('TAS')).toBe(true)
  step(2)
  expect(lines()[2]).toMatch(/^>DESEL/)
  expect(fms.windComputed).toBe(false)
  expect(fms.navState.mode).toBe('GPS')
  expect(fms.position.lat).toBeCloseTo(before.lat, 2)
  fms.press('LSK1L')
  step(2)
  expect(fms.windComputed).toBe(true)
})

test('F9: DME deselected takes DME/DME and VOR/DME both; VOR/DME/TCN deselected takes VOR/DME only (plan C3)', () => {
  const { fms, step } = unit()
  fms.setCondition('gpsLost', true)
  fms.setCondition('apirsFail', true); fms.setCondition('dvsFail', true)
  step(6)
  expect(['DME/DME', 'VOR/DME']).toContain(fms.navState.mode)
  fms.setDeselected('VOR/DME/TCN', true)
  step(2)
  expect(fms.lastRadioFixes.every(fix => fix.mode !== 'VOR/DME') || fms.navState.mode !== 'VOR/DME').toBe(true)
  expect(fms.navState.mode).not.toBe('VOR/DME')
  fms.setDeselected('VOR/DME/TCN', false)
  fms.setDeselected('DME', true)
  step(2)
  expect(['DME/DME', 'VOR/DME']).not.toContain(fms.navState.mode)
  fms.open('DESELECT')
  expect(screenText(fms.screen())[4]).toMatch(/DESEL<$/)
  fms.press('LSK2R')
  step(6)
  expect(['DME/DME', 'VOR/DME']).toContain(fms.navState.mode)
})

test('F9: KALMAN and DVS deselected are not navigated on; their lines read DESEL', () => {
  const { fms, step, lines } = unit()
  step(90)
  fms.setCondition('gpsLost', true); fms.setCondition('dmeOutage', true)
  step(1)
  expect(fms.navState.mode).toBe('KALMAN')
  fms.setDeselected('KALMAN', true)
  step(1)
  expect(fms.navState.mode).toBe('DVS')
  fms.setDeselected('DVS', true)
  step(1)
  expect(fms.navState.mode).toBe('DR')
  fms.open('DESELECT')
  expect(lines()[6]).toMatch(/^>DESEL/)
  expect(lines()[8]).toMatch(/^>DESEL/)
})

test('F9: GPS DESELECT deselects each receiver; both deselected selects GPS out (M300 17-3)', () => {
  const { fms, step, lines } = unit()
  step(3)
  fms.open('DESELECT'); fms.press('LSK1R')
  expect(lines()[0]).toMatch(/^GPS DESELECT\s+1\/1$/)
  expect(lines()[2]).toMatch(/VALID<$/)
  fms.press('LSK1R')
  expect(fms.gpsReceiverChoice).toBe('GPS2')
  expect(lines()[2]).toMatch(/DESEL<$/)
  fms.press('LSK2R')
  expect(fms.gpsNavSelected).toBe(false)
  fms.press('LSK1R'); fms.press('LSK2R')
  expect(fms.gpsNavSelected).toBe(true)
  expect(fms.gpsReceiverChoice).toBe('AUTO')
})

test('F9: KALMAN STATUS shows INI, then NAV with the emulated INS position, its 2 sigma in metres and both readiness flags (M300 12-24)', () => {
  const { fms, step, lines } = unit()
  step(90)
  fms.open('NAV_STATUS_INDEX'); fms.press('LSK5R')
  expect(lines()[0]).toMatch(/^KALMAN STATUS\s+1\/1$/)
  expect(lines()[10]).toMatch(/^YES\s+YES$/)
  fms.setCondition('gpsLost', true); fms.setCondition('dmeOutage', true)
  step(30)
  expect(lines()[2]).toMatch(/^NAV/)
  expect(lines()[4]).not.toMatch(/\*\*\*/)
  expect(lines()[8].trimEnd()).toMatch(/^\d+ M$/)
  expect(lines()[10]).toMatch(/^NO\s+YES$/)
  step(120)
  expect(lines()[2]).toMatch(/^INI/)
  expect(lines()[4]).toMatch(/\*\*\*/)
})

test('F9: DVS STATUS shows the Doppler velocities and mode; the crew water current applies only in SEA mode (M300 12-21 to 12-23)', () => {
  const { fms, step, lines, enter } = unit()
  step(3)
  fms.open('NAV_STATUS_INDEX'); fms.press('LSK3L')
  expect(lines()[0]).toMatch(/^DVS STATUS\s+1\/2$/)
  expect(lines()[2]).toMatch(/^VX [+-]\d+\.\d KTS/)
  expect(lines()[3]).toMatch(/^VY [+-]\d+\.\d KTS/)
  expect(lines()[4]).toMatch(/^VZ [+-]\d+ FT\/MIN/)
  expect(lines()[6]).toMatch(/^LAND/)
  fms.press('NEXT')
  expect(lines()[0]).toMatch(/^DVS STATUS\s+2\/2$/)
  enter('280/4.0', 'LSK2L')
  expect(lines()[4]).toMatch(/^280°\/4\.0 KTS/)
  expect(fms.waterCurrentEntry!.speedKt).toBeCloseTo(4, 6)
  enter('290/40', 'LSK1L')
  // The FMS computes the wind with GPS: no manual entry.
  expect(lines()[SCRATCHPAD_LINE].trim()).toMatch(/NOT ALLOWED|INVALID/)
  fms.press('CLR')
  fms.setDvsSurface('SEA')
  fms.press('PREV')
  expect(lines()[6]).toMatch(/^SEA/)
})

test('F9: station deselection removes one station and keeps DME/DME (M300 12-18)', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  fms.setCondition('apirsFail', true); fms.setCondition('dvsFail', true)
  fms.setCondition('gpsLost', true)
  const dmeDme = () => fms.lastRadioFixes.find(fix => fix.mode === 'DME/DME')
  const fly = (seconds: number, until?: () => boolean) => { for (let i = 0; i < seconds; i++) { now += 1000; sim.step(1); if (until?.()) return } }
  fly(3600, () => dmeDme() !== undefined)
  // The bench route's roster has one usable crossing, so removing a station there ends DME/DME by geometry. What this
  // proves at the FMS is that a station deselection is not the DME sensor's: the station leaves, the DESELECT line stays.
  const removed = dmeDme()!.dmes[0]
  fms.setDmeDeselected([removed])
  fly(5)
  expect(fms.lastRadioFixes.every(fix => !fix.dmes.includes(removed))).toBe(true)
  expect(fms.deselectedInputs.has('DME')).toBe(false)
  expect(fms.inputState('DME')).not.toBe('DESEL')
  // With four stations at good crossings, removing one used station keeps DME/DME on the others.
  const at = { lat: 45, lon: -75 }, t = 10_000
  const stations: Navaid[] = [0, 90, 180, 270].map((course, i) => ({ kind: 'navaid', ident: `S${i}`, type: 'DME', name: 'Fixture', frequency: '115.00',
    elevation: { feet: 0, source: 'data', provenance: 'fixture' }, position: offset(at, course, 10) }))
  const observations: RadioObservation[] = stations.map(station => ({ station,
    slantRangeNm: { at: t, sequence: 1, status: 'NORMAL', value: Math.hypot(distanceNm(at, station.position), 3000 / 6076.12) },
    bearingTrue: { at: t, sequence: 1, status: 'NCD', value: null } }))
  const before = radioFixes(observations, at, 3000, t).find(fix => fix.mode === 'DME/DME')!
  const statuses: DmeStationStatus[] = []
  const after = radioFixes(observations, at, 3000, t, undefined, { dmeDeselected: new Set([before.dmes[0]]) }, statuses).find(fix => fix.mode === 'DME/DME')
  expect(after).toBeDefined()
  expect(after!.dmes).not.toContain(before.dmes[0])
  expect(distanceNm(after!.position, at)).toBeLessThan(0.05)
})

test('F9: the POS INIT 2/2 table lists each equipped mode with its status, distance and accuracy (M300 12-27 layout)', () => {
  const { fms, step, lines } = unit()
  step(90)
  fms.open('POS'); fms.press('NEXT')
  expect(lines()[0]).toMatch(/^POS INIT\s+2\/2$/)
  expect(lines()[1]).toMatch(/^ FMS POS\s+GPS $/)
  expect(lines()[3]).toBe('MODE   STS DIS BRG ACCUR')
  const table = lines().slice(4, 9)
  expect(table.map(line => line.slice(0, 7).trim())).toEqual(['GPS', 'DME/DME', 'VORDMTC', 'KALMAN', 'DVS'])
  expect(table.every(line => line.length === 24)).toBe(true)
  const gps = fms.sensorSolutions.find(sensor => sensor.mode === 'GPS')!
  expect(table[0]).toMatch(/^GPS    NAV 0\.00---- \d\.\d\d$/)
  expect(Number(table[0].slice(19))).toBeCloseTo(gps.accuracy95Nm!, 2)
  // The KALMAN position follows the FMS position while GPS aids it; DVS has no position of its own.
  expect(table[3]).toMatch(/^KALMAN NAV \d\.\d\d(----|\d{3}[°T]) \d\.\d\d$/)
  expect(table[4]).toMatch(/^DVS    NAV --------( \d\.\d\d| ----)$/)
  fms.setDeselected('KALMAN', true)
  fms.open('DESELECT'); fms.open('POS'); fms.press('NEXT')
  expect(lines()[7]).toBe('KALMAN DSEL-------- ----')
  // An admitted adapter range is two NM south of a station five NM north of the selected GPS position.
  // The independent page oracle is therefore 3.00 NM due north, not a value computed with the production geometry.
  const now = Date.UTC(2026, 8, 30, 14)
  const seed = new ScriptedFms(() => new Date(now))
  const frame: SensorFrame = structuredClone(seed.navigationInputs!)
  for (const word of frame.gps) {
    word.value!['110'] = { ssm: 'NORMAL', value: 45.5 }; word.value!['120'] = { ssm: 'NORMAL', value: 0 }
    word.value!['111'] = { ssm: 'NORMAL', value: -74.9 }; word.value!['121'] = { ssm: 'NORMAL', value: 0 }
  }
  frame.air.value!.altitudeFt = 6000
  frame.radios = []
  const adapter = new ScriptedFms(() => new Date(now), { sensors: { read: () => structuredClone(frame) } })
  const receiver = (['dme1', 'dme2'] as const).find(device => adapter.dmeStation(device)?.ident === 'HWK')
  expect(receiver).toBeDefined()
  frame.radios = [{ station: { kind: 'navaid', ident: 'HWK', type: 'VORDME', name: 'Literal north fixture', frequency: '115.20',
    position: { lat: 45.5 + 5 * 180 / (3440.065 * Math.PI), lon: -74.9 }, elevation: { feet: 6000, source: 'data', provenance: 'fixture survey' } },
    rangeIdentity: adapter.radioPort!.dmeTuning(receiver!, 1)!,
    slantRangeNm: { at: now, sequence: 2, status: 'NORMAL', value: 2 }, bearingTrue: { at: now, sequence: 2, status: 'NORMAL', value: 180 } }]
  adapter.updateNavigation(0)
  expect(adapter.navState.mode).toBe('GPS')
  adapter.toggleAngleReference(); adapter.open('POS'); adapter.press('NEXT')
  expect(screenText(adapter.screen())[6]).toBe('VORDMTCNAV 3.00360T 0.66')
  // Without a KALMAN mode configured there is no KALMAN line.
  const bare = unit(withoutOption('kalman'))
  bare.step(3); bare.fms.open('POS'); bare.fms.press('NEXT')
  expect(bare.lines().slice(4, 9).map(line => line.slice(0, 7).trim())).toEqual(['GPS', 'DME/DME', 'VORDMTC', 'DVS', ''])
})

test('F9: the crew water current moves the DVS solution in SEA mode only (M300 12-22, 12-23)', () => {
  const drift = (surface: 'LAND' | 'SEA', current: boolean) => {
    const { fms, step } = unit()
    step(90)
    fms.setCondition('gpsLost', true); fms.setCondition('dmeOutage', true)
    fms.setDeselected('KALMAN', true)
    fms.setDvsSurface(surface)
    if (current) fms.setWaterCurrent(90, 10)
    step(1)
    expect(fms.navState.mode).toBe('DVS')
    step(360)
    return { ...fms.position }
  }
  const still = drift('LAND', false)
  const land = drift('LAND', true)
  const sea = drift('SEA', true)
  expect(distanceNm(land, still)).toBeLessThan(1e-6)
  // Six minutes of a 10 kt current toward east is 1 NM.
  expect(distanceNm(sea, still)).toBeGreaterThan(0.9)
  expect(distanceNm(sea, still)).toBeLessThan(1.1)
})


test('F9: GPS DESELECT shows acquisition after actual receiver loss and returns VALID on recovery', () => {
  const { fms, step, lines } = unit()
  step(5)
  fms.open('GPS_DESELECT')
  expect(lines()[2]).toMatch(/VALID/)
  expect(lines()[4]).toMatch(/VALID/)
  fms.setCondition('gpsLost', true)
  expect(lines()[2]).toMatch(/ACQ/)
  expect(lines()[4]).toMatch(/ACQ/)
  fms.press('LSK1R')
  expect(lines()[2]).toMatch(/DESEL/)
  fms.setCondition('gpsLost', false); step(5)
  expect(lines()[2]).toMatch(/DESEL/)
  expect(lines()[4]).toMatch(/VALID/)
  fms.press('LSK1R')
  expect(lines()[2]).toMatch(/VALID/)
})
