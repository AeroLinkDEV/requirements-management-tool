import { expect, logicTest as test } from './isolated-client-test'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import type { SensorFrame } from '../src/fmsCdu/sensorPorts'
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

test('F9: the POS INIT 2/2 table lists each equipped mode with its status, distance and accuracy (M300 12-27 layout)', () => {
  const { fms, step, lines } = unit()
  step(90)
  fms.open('POS'); fms.press('NEXT')
  expect(lines()[0]).toMatch(/^POS INIT\s+2\/2$/)
  expect(lines()[1]).toMatch(/^ FMS POS\s+GPS $/)
  expect(lines()[3]).toBe('MODE    STS DIS BRG  ACC')
  const table = lines().slice(4, 9)
  expect(table.map(line => line.slice(0, 8).trim())).toEqual(['GPS', 'DME/DME', 'VORDMTC', 'KALMAN', 'DVS'])
  expect(table.every(line => line.length === 24)).toBe(true)
  const gps = fms.sensorSolutions.find(sensor => sensor.mode === 'GPS')!
  expect(table[0]).toMatch(/^GPS     NAV 0\.00----\d\.\d\d$/)
  expect(Number(table[0].slice(20))).toBeCloseTo(gps.accuracy95Nm!, 2)
  // The KALMAN position follows the FMS position while GPS aids it; DVS has no position of its own.
  expect(table[3]).toMatch(/^KALMAN  NAV \d\.\d\d(----|\d{3}[°T])\d\.\d\d$/)
  expect(table[4]).toMatch(/^DVS     NAV --------(\d\.\d\d|----)$/)
  fms.setDeselected('KALMAN', true)
  fms.open('DESELECT'); fms.open('POS'); fms.press('NEXT')
  expect(lines()[7]).toBe('KALMAN  DSEL------------')
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
  expect(screenText(adapter.screen())[6]).toBe('VORDMTC NAV 3.00360T0.66')
  // Actual receiver words supply the page accuracy; rounded width boundaries must not alter source values.
  for (const [accuracy, printed] of [[9.999, '10.0'], [10, '10.0'], [99.999, ' 100'], [100, ' 100'],
    [null, '----'], [NaN, '----'], [Infinity, '----'], [10000, '****']] as const) {
    for (const word of frame.gps) {
      word.sequence++
      word.value!['247'] = { ssm: accuracy === null ? 'NCD' : 'NORMAL', value: accuracy }
    }
    adapter.updateNavigation(0)
    const row = screenText(adapter.screen())[4]
    expect(row).toHaveLength(24)
    expect(row.slice(20)).toBe(printed)
    expect(adapter.navigationInputs!.gps[0].value!['247'].value).toBe(accuracy)
  }
  // Without a KALMAN mode configured there is no KALMAN line.
  const bare = unit(withoutOption('kalman'))
  bare.step(3); bare.fms.open('POS'); bare.fms.press('NEXT')
  expect(bare.lines().slice(4, 9).map(line => line.slice(0, 8).trim())).toEqual(['GPS', 'DME/DME', 'VORDMTC', 'DVS', ''])
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

// F9 selection is also a motion-source predicate: cached radio geometry must not use selected-out air words.
test('F9: TAS and HDG deselection remove AIR_WIND cache compensation in the same tick and restoration recovers it', () => {
  for (const selectedOut of ['TAS', 'HDG'] as const) {
    let now = Date.UTC(2026, 8, 30, 14)
    const seed = new ScriptedFms(() => new Date(now))
    seed.setAircraft({ position: { lat: 45.5, lon: -74.9 }, altitude: 6000 })
    for (let i = 0; i < 5; i++) { now += 1000; seed.updateNavigation(1) }
    const measuredAt = now, frame: SensorFrame = structuredClone(seed.navigationInputs!)
    frame.air.value = { headingTrue: 0, tasKt: 100, altitudeFt: 6000 }
    frame.dvs = { at: now, sequence: 1, status: 'FAIL', value: { alongKt: 100, acrossKt: 0 } }
    const arrivals = frame.radios.filter(observation => observation.slantRangeNm.status === 'NORMAL')
    frame.radios = []
    const fms = new ScriptedFms(() => new Date(now), { sensors: { read: () => structuredClone(frame) } })
    frame.radios = arrivals.flatMap(observation => {
      const port = fms.radioPort!
      const paired = (['dme1', 'dme2'] as const).find(receiver => fms.dmeStation(receiver)?.ident === observation.station.ident)
      const scan = port.scanning().find(on => on.ident === observation.station.ident)
      const identity = paired ? port.dmeTuning(paired, 1) : scan ? port.dmeTuning(scan.device, scan.channel) : null
      return identity ? [{ ...observation, rangeIdentity: identity, bearingTrue: { ...observation.bearingTrue, status: 'NCD' as const, value: null } }] : []
    })
    expect(frame.radios).toHaveLength(3)
    const admitted = structuredClone(frame.radios)
    fms.updateNavigation(0)
    now += 1000; frame.air.at = now; frame.air.sequence++
    for (const word of frame.gps) { word.at = now; word.sequence++ }
    frame.radios = [] // Nothing remeasures the geometry or supplies radio-derived velocity.
    fms.updateNavigation(1)
    const fix = () => fms.lastRadioFixes.find(candidate => candidate.mode === 'DME/DME')!
    expect(fix().motion).toMatchObject({ source: 'AIR_WIND', gpsDependent: true })
    const alignedAccuracy = fix().anp
    fms.open('DESELECT'); fms.press(selectedOut === 'TAS' ? 'LSK1L' : 'LSK2L')
    expect(fms.deselectedInputs.has(selectedOut)).toBe(true)
    expect(fix().motion).toBeNull()
    expect(Number.isFinite(fix().anp)).toBe(true)
    expect(fix().naimEligible).toBe(false)
    expect(fix().oldestAt).toBe(measuredAt)
    // The declared LAB allowance adds 600/3600 NM without TAS, or 100/3600 with TAS but no heading; not a physical bound.
    expect(fix().anp).toBeCloseTo(alignedAccuracy + (selectedOut === 'TAS' ? 600 : 100) / 3600, 2)
    const heldPosition = { ...fix().position }, heldAccuracy = fix().anp
    frame.air.value![selectedOut === 'TAS' ? 'tasKt' : 'headingTrue'] = selectedOut === 'TAS' ? 500 : 180
    frame.air.sequence++; fms.updateNavigation(0)
    expect(fix().position).toEqual(heldPosition)
    expect(fix().anp).toBe(heldAccuracy)
    const originalHil = frame.gps.map(word => structuredClone(word.value!['130']))
    for (const word of frame.gps) { word.sequence++; word.value!['130'] = { ssm: 'NORMAL', value: 5 } }
    fms.updateNavigation(0)
    expect(fms.navState.mode).toBe('GPS')
    expect(fms.navState.uncertain).toBe(true)
    expect(fms.sensorSolutions.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeNull()
    frame.gps.forEach((word, index) => { word.sequence++; word.value!['130'] = originalHil[index] })
    frame.air.value![selectedOut === 'TAS' ? 'tasKt' : 'headingTrue'] = selectedOut === 'TAS' ? 100 : 0
    frame.air.sequence++
    fms.press(selectedOut === 'TAS' ? 'LSK1L' : 'LSK2L')
    expect(fix().motion).toMatchObject({ source: 'AIR_WIND', gpsDependent: true })
    expect(fix().oldestAt).toBe(measuredAt)
    fms.press(selectedOut === 'TAS' ? 'LSK1L' : 'LSK2L')
    frame.radios = admitted.map(observation => ({ ...observation,
      slantRangeNm: { ...observation.slantRangeNm, at: now, sequence: observation.slantRangeNm.sequence + 1 },
      bearingTrue: { ...observation.bearingTrue, status: 'NCD' as const, value: null } }))
    fms.updateNavigation(0)
    // Genuinely renewed zero-age radio ranges require no air motion and retain independent eligibility.
    expect(fix().oldestAt).toBe(now)
    expect(fix().motion).toBeNull()
    expect(fix().naimEligible).toBe(true)
    expect(fix().accuracyBasis).toBe('laboratory')
    if (selectedOut === 'TAS') {
      frame.radios = []; now += 1000; frame.air.at = now; frame.air.sequence++
      frame.dvs = { at: now, sequence: 2, status: 'NORMAL', value: { alongKt: 100, acrossKt: 0 } }
      fms.updateNavigation(1)
      expect(fix().motion).toMatchObject({ source: 'RADIO', gpsDependent: false })
      now += 1001; frame.air.at = now; frame.air.sequence++
      frame.dvs.at = now; frame.dvs.sequence++
      fms.updateNavigation(1.001)
      expect(fix().motion).toMatchObject({ source: 'DVS', gpsDependent: false })
      expect(fix().naimEligible).toBe(true)
    }
  }
})
