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
import type { GpsReceiver } from '../src/fmsCdu/gps'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'
import { readFileSync } from 'node:fs'
import { MagvarModel } from '../src/fmsCdu/magvar'
import { WMM2025_DATABASE } from '../src/fmsCdu/wmm2025'
import { fmsOutputs } from '../src/fmsCdu/efis'

test('WMM2025 agrees with all independent NOAA field vectors at both epochs and ellipsoid heights', () => {
  const model = new MagvarModel()
  expect(model.valid).toBe(true)
  // Public-domain NOAA reference file; 0.11 nT / 0.011 deg laboratory comparison tolerance allows its rounding.
  const vectors = readFileSync('tests/fixtures/WMM2025_TEST_VALUES.txt', 'utf8').split(/\r?\n/).filter(line => line.trim() && !line.startsWith('#'))
  expect(vectors).toHaveLength(12)
  for (const line of vectors) {
    const [date, height, lat, lon, x, y, z, h, f, inclination, declination] = line.trim().split(/\s+/).map(Number)
    const year = Math.floor(date), start = Date.UTC(year, 0, 1)
    const clock = new Date(start + (date - year) * (Date.UTC(year + 1, 0, 1) - start))
    const result = model.field({ lat, lon }, height, clock)!
    for (const [actual, expected] of [[result.north, x], [result.east, y], [result.down, z], [result.horizontal, h], [result.total, f]]) expect(Math.abs(actual - expected), line).toBeLessThan(0.11)
    expect(Math.abs(result.inclination - inclination), line).toBeLessThan(0.011)
    expect(Math.abs(result.declination - declination), line).toBeLessThan(0.011)
  }
  for (const lat of [-90, 90]) expect(Object.values(model.field({ lat, lon: 0 }, 0, new Date(Date.UTC(2026, 0, 1)))!).every(Number.isFinite)).toBe(true)
  expect(model.field({ lat: NaN, lon: 0 }, 0, new Date())).toBeNull()
})

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

test('SETUP applies MAG/TRUE to CDU courses and angular entry, keeps true wind, and inhibits polar toggles (M300 3-9)', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2025, 0, 1)))
  unit.placeAircraft({ position: { lat: 0, lon: 120 }, altitude: 0, track: 100, heading: 100 }, 'NOAA equator reference')
  press(unit, 'INIT_REF', 'LSK5L')
  expect(lines(unit)[0]).toContain('SETUP')
  expect(lines(unit)[2]).toContain('MAG')
  expect(unit.angleText(100)).toBe('100°')
  // NOAA's equatorial reference D=-0.16: true = magnetic + D, with wraparound.
  expect(unit.angleFromEntry(100)).toBeCloseTo(99.84, 2)
  expect(unit.angleFromEntry(0)).toBeCloseTo(359.84, 2)
  unit.loadNavData({ cycle: { id: 'MAG-ENTRY', from: '', to: '' }, entries: [{ kind: 'fix', ident: 'REF01', position: { lat: 0, lon: 120 } }], airways: [], procedures: [] })
  unit.swapCycles()
  const pilot = unit.resolveWaypoint('REF01090/10')
  expect(typeof pilot).toBe('object')
  if (typeof pilot !== 'object') throw new Error('PBD refused')
  const target = unit.coordinates(pilot.ident)!
  expect(distanceNm({ lat: 0, lon: 120 }, target)).toBeCloseTo(10, 6)
  expect(bearingDeg({ lat: 0, lon: 120 }, target)).toBeCloseTo(89.84, 2)
  expect(unit.defineHold('REF01')).toBeUndefined()
  unit.open('HOLD'); enter(unit, '090', 'LSK3L')
  expect(unit.route.hold!.inbound).toBeCloseTo(89.84, 2)
  expect(lines(unit)[6]).toContain('090°')
  const output = fmsOutputs(unit, new FlightSimulator(unit))
  expect(output.angleReference).toBe('MAG')
  expect(output.magneticVariation.value).toBeCloseTo(-0.16, 2)
  unit.open('SETUP')
  const wind = { ...unit.wind }
  press(unit, 'LSK1L')
  expect(unit.angleReference).toBe('TRUE')
  expect(unit.angleText(100)).toBe('100T')
  expect(unit.angleFromEntry(100)).toBe(100)
  expect(unit.wind).toEqual(wind)
  press(unit, 'LSK1L')
  unit.placeAircraft({ position: { lat: 74, lon: 0 }, altitude: 0, track: 100 }, 'north polar boundary')
  expect(unit.angleReference).toBe('TRUE')
  expect(recalled(unit, 'USING TRUE REF')).toBe(true)
  press(unit, 'LSK1L')
  expect(unit.angleReference).toBe('TRUE')
  unit.placeAircraft({ position: { lat: 73, lon: 0 }, altitude: 0, track: 100 }, 'leave polar region')
  expect(unit.angleReference).toBe('TRUE')
  expect(recalled(unit, 'CHECK TRUE/MAG REF')).toBe(true)
  press(unit, 'LSK1L')
  expect(unit.angleReference).toBe('MAG')
})

test('the consumed MAGVAR checksum withdraws navigation; age above five years is an advisory, and valid loading restores outputs (M300 E-20/E-26)', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 0, 1)))
  const sim = new FlightSimulator(unit)
  expect(fmsOutputs(unit, sim).failed).toBe(false)
  unit.setCondition('magvarCrc', true)
  expect(unit.magvar.valid).toBe(false)
  expect(fmsOutputs(unit, sim)).toMatchObject({ failed: true, desiredTrack: { status: 'FAIL', value: null } })
  expect(unit.lamps()).toEqual(new Set(['FAIL']))
  expect(recalled(unit, 'MAG VAR CRC FAILED')).toBe(true)
  expect(recalled(unit, 'SYSTEM FAILED')).toBe(true)
  expect(unit.loadMagvar({ format: 'unsupported' })).toBe(false)
  expect(unit.magvar.valid).toBe(false)
  expect(unit.loadMagvar(WMM2025_DATABASE)).toBe(true)
  expect(fmsOutputs(unit, sim).failed).toBe(false)
  for (const [time, expired] of [[Date.UTC(2030, 0, 1), false], [Date.UTC(2030, 0, 1) + 1, true]] as const) {
    const aged = new ScriptedFms(() => new Date(time))
    expect(recalled(aged, 'MAG VAR OUT OF DATE')).toBe(expired)
    expect(aged.hasCondition('fmsFail')).toBe(false)
    expect(aged.magneticField).not.toBeNull()
  }
})

test('cold/warm FMS power-up retains TAS/position, applies the ground wind rule, checks lamps, and requires an active leg in flight (M300 3-7/3-19/E-40)', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const truth = { ...unit.truePosition }, gps = unit.gps
  unit.planData.cruiseTas = 137
  unit.planData.cruiseWind = { direction: 110, speed: 18 }
  unit.powerOff()
  expect(unit.lamps().size).toBe(0)
  expect(fmsOutputs(unit, sim)).toMatchObject({ failed: true, lateralMode: 'HDG HOLD' })
  expect(sim.guidance).toMatchObject({ mode: 'HDG', desiredTrack: null })
  unit.powerOn('WARM', true)
  expect(unit.powerState).toBe('TEST')
  expect(unit.lamps()).toContain('EXEC')
  expect(unit.screen().flat().every(cell => cell.inverse && cell.color === 'white')).toBe(true)
  now += 5000; unit.tick()
  expect(unit.powerState).toBe('ON')
  expect(unit.planData.cruiseTas).toBe(137)
  expect(unit.planData.cruiseWind).toEqual({ direction: 110, speed: 18 })
  expect(unit.truePosition).toEqual(truth)
  expect(unit.gps).toBe(gps)
  expect(unit.needsActiveLeg).toBe(false)
  unit.powerOn('COLD', false); now += 5000; unit.tick()
  expect(unit.planData.cruiseWind.speed).toBe(18)
  expect(unit.needsActiveLeg).toBe(true)
  expect(recalled(unit, '!ENTER ACT WPT/LEG')).toBe(true)
  expect(fmsOutputs(unit, sim).desiredTrack).toMatchObject({ status: 'NCD', value: null })
  sim.step(0.25)
  expect(sim.guidance.desiredTrack).toBeNull()
  const target = unit.activeRoute.legs.find(leg => leg.kind === 'wpt')!
  expect(target.kind).toBe('wpt')
  if (target.kind !== 'wpt') throw new Error('Fixture has no active waypoint')
  expect(unit.directTo(target.ident)).toBeUndefined(); unit.press('EXEC')
  expect(unit.needsActiveLeg).toBe(false)
  unit.powerOn('COLD', true); now += 5000; unit.tick()
  expect(unit.planData.cruiseWind).toEqual({ direction: 0, speed: 0 })
  expect(unit.planData.cruiseTas).toBe(137)
  // With no GPS/radio source, startup retains the calculated position rather than leaking plant truth.
  unit.setCondition('gpsLost', true); unit.setCondition('dmeOutage', true)
  expect(unit.initializePosition({ lat: 41, lon: -71 })).toBe(true)
  unit.powerOff(); unit.powerOn('WARM', true); now += 5000; unit.tick()
  expect(unit.position).toEqual({ lat: 41, lon: -71 })
})

test('POS INIT permits RTC date/time entries without GPS time and refuses them with GPS, while sensor timestamps remain unchanged (M300 A-111)', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 30, 14)))
  unit.open('POS')
  enter(unit, '121530', 'LSK5L')
  expect(scratch(unit)).toBe('NOT ALLOWED')
  expect(unit.utcTime.toISOString()).toBe('2026-09-30T14:00:00.000Z')
  unit.press('CLR', { held: true }); unit.press('CLR', { held: true })
  unit.setCondition('gpsLost', true); unit.setCondition('dmeOutage', true)
  const etaBefore = unit.profile().points[0].eta!
  enter(unit, '121530', 'LSK5L')
  expect(unit.utcTime.toISOString()).toBe('2026-09-30T12:15:30.000Z')
  expect(unit.profile().points[0].eta).toBe(etaBefore - 6270000)
  unit.open('TIMER'); enter(unit, '1230', 'LSK1L')
  expect(unit.timer.alarmAt).toBe(Date.UTC(2026, 8, 30, 12, 30))
  unit.open('POS')
  enter(unit, 'OCT01/26', 'LSK5R')
  expect(unit.utcTime.toISOString()).toBe('2026-10-01T12:15:30.000Z')
  expect(unit.now.toISOString()).toBe('2026-09-30T14:00:00.000Z')
  enter(unit, 'FEB30/26', 'LSK5R')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  expect(unit.utcTime.toISOString()).toBe('2026-10-01T12:15:30.000Z')
  unit.setCondition('gpsLost', false)
  // A warm valid GPS resumes immediately in this fixture and its UTC reference supersedes the RTC entry.
  expect(unit.navState.mode).toBe('GPS')
  expect(unit.utcTime.toISOString()).toBe('2026-09-30T14:00:00.000Z')
})

test('manual QNH accepts the published S300 limits through VNAV and POS without a runway, and cancels only on departure terminal exit (M300 A-128)', () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const source = new ScriptedFms(() => new Date(now))
  const departure = source.navdb.airport('CYOW')!
  source.placeAircraft({ position: departure.position, altitude: 1500, track: 90 }, 'QNH source fixture')
  let frame = source.navigationInputs!
  frame.air.value = { ...frame.air.value!, baroCorrected: false, pressureAltitudeFt: 1500 }
  const port = new BufferedSensorPort(); expect(port.publish(frame)).toBe(true)
  const unit = new ScriptedFms(() => new Date(now), { sensors: port })
  unit.selectProcedure('APPROACH', 'R24R'); unit.press('EXEC'); unit.open('VNAV')
  unit.press('CLR') // Acknowledge the external-position acquisition alert before making the crew entry.
  // The old 945..1050 / 28..31 limits reject 915 here: retain that pre-fix failure.
  for (const [entry, expected] of [['915', '915'], ['1083', '1083'], ['2700', '27.00'], ['3200', '32.00']]) {
    unit.setScratch(entry); unit.press('LSK6L')
    expect(unit.vnav.qnh, entry).toBe(expected)
  }
  for (const entry of ['914', '1084', '2699', '3201']) {
    unit.setScratch(entry); unit.press('LSK6L')
    expect(scratch(unit), entry).toBe('INVALID ENTRY')
    expect(unit.vnav.qnh).toBe('32.00')
    unit.press('CLR', { held: true }); unit.press('CLR', { held: true })
  }
  unit.modify(route => { route.approach = undefined; route.legs = [] }); unit.press('EXEC')
  unit.open('POS'); unit.press('NEXT')
  expect(lines(unit)[0]).toContain('2/2')
  expect(lines(unit)[9]).toContain('ALT (CORR)')
  unit.setScratch('1013'); unit.press('LSK6L')
  expect(unit.advisoryBaroAltitude).toBeCloseTo(1493.25, 6) // 1500 + (1013 - 1013.25)*27; laboratory air-data policy.
  expect(unit.qnhUnits).toBe('MB')
  unit.press('LSK6L'); expect(unit.qnhUnits).toBe('INHG')
  expect(lines(unit)[12]).toContain('29.91')
  const publishAt = (distance: number, altitude: number, corrected = false) => {
    source.placeAircraft({ position: offset(departure.position, 90, distance), altitude, track: 90 }, 'QNH departure boundary')
    now += 1000; source.updateNavigation(0); frame = source.navigationInputs!
    frame.air.value = { ...frame.air.value!, baroCorrected: corrected, pressureAltitudeFt: altitude }
    expect(port.publish(frame)).toBe(true); unit.refreshSensorInput()
  }
  publishAt(33, 1500); expect(unit.vnav.qnh).toBe('1013')
  publishAt(33.01, 1500); expect(unit.vnav.qnh).toBeNull()
  unit.press('CLR') // Acknowledge POSITION SHIFT from the independent external-frame relocation.
  unit.setScratch('2992'); unit.press('LSK6L')
  expect(unit.vnav.qnh).toBe('29.92')
  publishAt(34, 1500); expect(unit.vnav.qnh).toBe('29.92') // Arrival entry is not repeatedly cancelled because origin is behind.
  publishAt(0, departure.elevation + 15990)
  publishAt(0, departure.elevation + 16010); expect(unit.vnav.qnh).toBeNull()
  publishAt(0, 1500, true)
  expect(unit.manualQnhAvailable).toBe(false)
  expect(lines(unit)[11]).not.toContain('QNH SET')
  unit.press('CLR')
  unit.setScratch('1013'); unit.press('LSK6L')
  expect(scratch(unit)).toBe('NOT ALLOWED')
})

test('S300 after-FAF integrity-only cancellation waits 300 seconds, while HDOP above four cancels immediately (M300 7-12)', () => {
  const prepared = () => {
    let now = Date.UTC(2026, 8, 29, 14)
    const unit = new ScriptedFms(() => new Date(now))
    unit.selectProcedure('APPROACH', 'R24R'); unit.press('EXEC')
    unit.directTo('FERDI'); unit.press('EXEC')
    unit.placeAircraft({ position: offset(unit.coordinates('FERDI')!, 237, 1), altitude: 1500, track: 237 }, 'S300 integrity timer fixture')
    unit.armApproach(); unit.updateNavigation(0)
    expect(unit.nonPrecisionApproach).toBe(true)
    unit.arrive(); unit.updateNavigation(0)
    expect(unit.onFinalSegment).toBe(true)
    const receivers = (unit as unknown as { gps: readonly GpsReceiver[] }).gps
    const lose = (hdop: number) => {
      for (const rx of receivers) { rx.override('130', { kind: 'FORCE', value: 1, ssm: 'NORMAL' }); rx.override('101', { kind: 'FORCE', value: hdop, ssm: 'NORMAL' }) }
      unit.updateNavigation(0)
    }
    return { unit, lose, advance: (seconds: number) => { now += seconds * 1000; unit.updateNavigation(0) } }
  }
  const delayed = prepared(); delayed.lose(1)
  expect(delayed.unit.navState.uncertain).toBe(true)
  expect(delayed.unit.approachType).toBe('LNAV')
  expect(delayed.unit.nonPrecisionApproach).toBe(true)
  delayed.advance(299)
  expect(delayed.unit.nonPrecisionApproach).toBe(true)
  expect(recalled(delayed.unit, 'NO APPR INTEGRITY')).toBe(false)
  delayed.advance(1)
  expect(delayed.unit.approachType).toBe('NO APPR')
  expect(delayed.unit.nonPrecisionApproach).toBe(false)
  expect(recalled(delayed.unit, 'NO APPR INTEGRITY')).toBe(true)
  const immediate = prepared(); immediate.lose(4.01)
  expect(immediate.unit.nonPrecisionApproach).toBe(false)
  expect(immediate.unit.approachType).toBe('NO APPR')
  expect(recalled(immediate.unit, 'NO APPR INTEGRITY')).toBe(true)
})

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

test('the FMS reads barometric altitude as the air-data word carries it, to the foot (ARINC 429 label 203), so a settled capture reaches its altitude', () => {
  const now = Date.UTC(2026, 8, 27, 14)
  // The default bench (no sensor port): the truth, to the foot.
  const unit = new ScriptedFms(() => new Date(now))
  const at = (altitude: number) => { unit.placeAircraft({ position: unit.truePosition, track: 0, altitude }, 'test: altitude'); return unit.validBaroAltitude }
  expect(at(1999.4)).toBe(1999)
  expect(at(1999.5)).toBe(2000)
  expect(at(2000.49)).toBe(2000)
  // Through a sensor port: the air-data word, to the foot.
  const template = new ScriptedFms(() => new Date(now)).navigationInputs!
  template.air.value = { ...template.air.value!, altitudeFt: 1999.6 }
  const port = new BufferedSensorPort()
  expect(port.publish(template)).toBe(true)
  const ported = new ScriptedFms(() => new Date(now), { sensors: port })
  ported.updateNavigation(0)
  expect(ported.validBaroAltitude).toBe(2000)
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

test('S300 phase boundaries use airport-relative altitude and separate arrival and departure radii', () => {
  const unit = new ScriptedFms(() => new Date('2026-09-29T14:00:00Z'))
  const origin = unit.navdb.airport(unit.activeRoute.origin)!
  const dest = unit.navdb.airport(unit.activeRoute.dest)!
  unit.placeAircraft({ position: offset(origin.position, 270, 32.9), altitude: origin.elevation + 15999, tas: 0 })
  expect(unit.flightPhase).toBe('TERMINAL')
  unit.placeAircraft({ position: offset(origin.position, 270, 32.9), altitude: origin.elevation + 16000, tas: 0 })
  expect(unit.flightPhase).toBe('EN ROUTE')
  unit.placeAircraft({ position: offset(origin.position, 270, 33.1), altitude: 2000, tas: 0 })
  expect(unit.flightPhase).toBe('EN ROUTE')
  unit.placeAircraft({ position: offset(dest.position, 90, 29.9), altitude: dest.elevation + 14999, tas: 0 })
  expect(unit.flightPhase).toBe('TERMINAL')
  unit.placeAircraft({ position: offset(dest.position, 90, 29.9), altitude: dest.elevation + 15000, tas: 0 })
  expect(unit.flightPhase).toBe('EN ROUTE')
})

test('RNP defaults by phase: a loaded approach does not grant approach phase', () => {
  const unit = new ScriptedFms(() => new Date('2026-09-29T14:00:00Z'))
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
  expect(unit.flightPhase).toBe('TERMINAL')
  expect(unit.requiredRnp).toBe(1)
  unit.directTo('FERDI')
  unit.press('EXEC')
  const faf = unit.coordinates('FERDI')!
  unit.placeAircraft({ position: offset(faf, 57, 3.1), altitude: 2000, track: 237, tas: 90 })
  unit.armApproach(true)
  expect(screenText(unit.screen())[SCRATCHPAD_LINE]).not.toContain('HSI SCALE TO CHANGE')
  unit.placeAircraft({ position: offset(faf, 57, 2.9), altitude: 2000, track: 237, tas: 90 })
  expect(screenText(unit.screen())[SCRATCHPAD_LINE]).toContain('HSI SCALE TO CHANGE')
  unit.armApproach(false); unit.updateNavigation(0)
  expect(recalled(unit, 'ARM APPROACH')).toBe(true)
  unit.placeAircraft({ position: offset(faf, 57, 2.1), altitude: 2000, track: 237, tas: 90 })
  unit.armApproach(true)
  expect(unit.flightPhase).toBe('TERMINAL')
  unit.placeAircraft({ position: offset(faf, 57, 1.9), altitude: 2000, track: 237, tas: 90 })
  expect(unit.flightPhase).toBe('APPROACH')
  expect(unit.requiredRnp).toBe(0.3)
  for (let prn = 1; prn <= 32; prn += 1) unit.deselectRaimSatellite(prn, true)
  unit.updateNavigation(0)
  expect(unit.flightPhase).toBe('TERMINAL')
  expect(unit.lamps().has('NPA')).toBe(false)
  // M300 7-10: EXIT HOLD is permission to leave at the next crossing, not passage of the FAF.
  for (let prn = 1; prn <= 32; prn += 1) unit.deselectRaimSatellite(prn, false)
  unit.defineHold('FERDI'); unit.press('EXEC')
  unit.sequence(); unit.updateNavigation(0)
  expect(unit.activeRoute.hold?.status).toBe('IN PROGRESS')
  expect(unit.flightPhase).toBe('TERMINAL')
  unit.changeHold(hold => { hold.status = 'EXIT ARMED' }); unit.press('EXEC')
  unit.updateNavigation(0)
  expect(unit.flightPhase).toBe('TERMINAL')
  expect(unit.lamps().has('NPA')).toBe(false)
  unit.arrive(); unit.updateNavigation(0)
  expect(unit.activeRoute.legs[0]).toMatchObject({ ident: 'RW24R' })
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
  // S300 uses the executed route and measured GPS integrity; later-software FAS acceptance is a separate profile.
  expect(unit.approachType).toBe('LNAV')
  unit.updateNavigation(0)
  expect(unit.approachType).toBe('LNAV')
  unit.setCondition('gpsIntegrity', true)
  expect(scratch(unit)).toBe('GPS POS UNCERTAIN')
  // The independent radio comparison is adequate here (M300 1-7). The position is retained as uncertain, without
  // granting approach or hover authority to either integrity-rejected receiver.
  expect(unit.navState.mode).toBe('GPS')
  expect(unit.navState.uncertain).toBe(true)
  expect(unit.navState.anp).toBeGreaterThan(0.3)
  expect(unit.approachType).toBe('NO APPR')
  for (let i = 0; i < 3; i += 1) unit.sequence()
  unit.directTo('FERDI'); unit.press('EXEC')
  unit.placeAircraft({ position: offset(unit.coordinates('FERDI')!, 251, 1.9), altitude: 2000, track: 71 }, 'test: denied approach before FAF')
  unit.armApproach()
  unit.updateNavigation(0)
  expect(unit.flightPhase).toBe('TERMINAL')
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
  unit.placeAircraft({ position: offset(unit.coordinates('FERDI')!, 251, 1.9), altitude: 2000, track: 71 }, 'test: armed non-precision approach before FAF')
  unit.armApproach(); unit.updateNavigation(0)
  expect(unit.lamps().has('NPA')).toBe(true)
  const ils = new ScriptedFms()
  ils.selectProcedure('APPROACH', 'I24R')
  ils.press('EXEC')
  for (let i = 0; i < 6; i += 1) ils.sequence()
  expect(active(ils)).toBe('FERDI')
  expect(ils.lamps().has('NPA')).toBe(false)
})

test('ARM APPROACH is asked for within 3 NM of the final approach fix when the approach is not armed (M300 7-10)', () => {
  const { unit, fly } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  fly(3 * 3600, () => active(unit) === 'RW24R' || recalled(unit, 'ARM APPROACH'))
  expect(recalled(unit, 'ARM APPROACH')).toBe(true)
  expect(distanceNm(unit.position, unit.coordinates('FERDI')!)).toBeLessThanOrEqual(3.05)
  expect(distanceNm(unit.position, unit.coordinates('FERDI')!)).toBeGreaterThan(2)

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

test('E1: the wind is computed only with valid air data and a measured ground velocity (GPS velocity or two radio fixes)', () => {
  const start = { lat: 45, lon: -75 }
  const air = { headingTrue: 90, tasKt: 120, altitudeFt: 3000 }
  const gps = { position: start, anp: 0.05, receiver: 1 as const, northKt: 0, eastKt: 140 }
  const base = { dt: 1, uncertainGps: null, radio: null, radioApproved: true, rnp: 1 }
  const navigation = new CivilNavigation(start)
  expect(navigation.current.windComputed).toBe(false)
  expect(navigation.update({ ...base, air, gps }).windComputed).toBe(true)
  // No velocity words, no air data, or air data out of range: the position may be measured, the wind is not.
  expect(navigation.update({ ...base, air, gps: { ...gps, northKt: null } }).windComputed).toBe(false)
  expect(navigation.update({ ...base, air: null, gps }).windComputed).toBe(false)
  expect(navigation.update({ ...base, air: { ...air, tasKt: Number.NaN }, gps }).windComputed).toBe(false)
  // Dead reckoning: never.
  expect(navigation.update({ ...base, air, gps: null }).windComputed).toBe(false)
  // Radio fixes: the first gives no velocity, the second within the allowed gap does.
  const fix = (position: typeof start, at: number) => ({ position, at, mode: 'DME/DME' as const, anp: 0.2, dmes: ['A', 'B'], vor: null })
  expect(navigation.update({ ...base, air, gps: null, radio: fix(start, 1000) }).windComputed).toBe(false)
  expect(navigation.update({ ...base, air, gps: null, radio: fix(offset(start, 90, 140 / 3600), 2000) }).windComputed).toBe(true)
})

test('E1: PROGRESS 1/4 takes a manual wind only while the FMS cannot compute one; it drives the predictions and never the air mass', () => {
  const { unit, fly } = setup()
  fly(5)
  expect(unit.windComputed).toBe(true)
  expect(unit.systemWind).toEqual(unit.wind)
  unit.press('PROG')
  const windRow = 6
  expect(lines(unit)[5]).toMatch(/^TRUE WIND/)
  expect(unit.screen()[windRow][1].size).toBe('medium')
  // Computed: medium font, and no manual entry.
  enter(unit, '090/30', 'LSK3L')
  expect(scratch(unit)).toBe('NOT ALLOWED')
  expect(unit.manualWindEntered).toBe(false)
  // No GPS and no DME: dead reckoning, and no wind can be computed. The last computed wind is carried, whatever the air
  // mass does next.
  unit.setCondition('gpsLost', true)
  unit.setCondition('dmeOutage', true)
  fly(5)
  expect(unit.navState.mode).toBe('DR')
  expect(unit.windComputed).toBe(false)
  const carried = { ...unit.systemWind }
  expect(carried).toEqual({ direction: unit.wind.direction, speed: unit.wind.speed })
  Object.assign(unit.wind, { direction: 180, speed: 40 })
  fly(2)
  expect(unit.systemWind).toEqual(carried)
  unit.press('PROG')
  expect(unit.screen()[windRow][1].size).toBe('large')
  // The crew's wind: shown large, used by the predictions, and the air mass untouched.
  const before = unit.profile().points[0].eta!
  for (let i = 0; i < 12 && scratch(unit) !== ''; i += 1) unit.press('CLR')
  enter(unit, '360/60', 'LSK3L')
  expect(unit.manualWindEntered).toBe(true)
  expect(unit.systemWind).toEqual({ direction: 0, speed: 60 })
  expect(unit.wind).toEqual({ direction: 180, speed: 40 })
  expect(lines(unit)[windRow]).toMatch(/^ 000°\/ 60KT/)
  expect(unit.profile().points[0].eta).not.toBe(before)
  const leg = unit.profile().points[0]
  const course = unit.legGeometry(unit.activeRoute)[0]!.course
  expect((leg.eta! - unit.now.getTime()) / 3_600_000).toBeCloseTo(leg.distance! / unit.groundSpeedOn(course), 6)
  // DELETE: back to the carried wind.
  for (let i = 0; i < 12 && scratch(unit) !== 'DELETE'; i += 1) unit.press('CLR')
  unit.press('LSK3L')
  expect(unit.manualWindEntered).toBe(false)
  expect(unit.systemWind).toEqual(carried)
  // A manual wind again, then the sensors return: the computed wind replaces it.
  enter(unit, '360/60', 'LSK3L')
  unit.setCondition('gpsLost', false)
  unit.setCondition('dmeOutage', false)
  fly(20, () => unit.windComputed)
  expect(unit.windComputed).toBe(true)
  expect(unit.manualWindEntered).toBe(false)
  expect(unit.systemWind).toEqual({ direction: 180, speed: 40 })
})
