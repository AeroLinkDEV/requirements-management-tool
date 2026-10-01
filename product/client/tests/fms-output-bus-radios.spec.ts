import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { BUS_RADIOS, fmsOutputs, type Word } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { FMS_OUTPUT_TAGS } from '../src/fmsCdu/outputTags'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { NAV_OUTPUT_VOCABULARY } from '../src/fmsCdu/sensorState'

// Stage F F13 part 2 (F13, C3 and C4 of the Stage F plan): the radios and the KALMAN and DVS sources reach the exhaustive
// output bus. Per radio: what it reports it is on, this computer's last command status, and its separate control-path,
// measurement-bus and receiver states. Per measurement: the station and the radial, range or bearing. Each is a word: FAIL
// from a failed FMS or where the radio cannot give it (bus lost, receiver failed), NCD where there is simply nothing.

const bench = () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const step = (seconds: number) => { for (let i = 0; i < seconds; i += 1) { now += 1000; unit.tick(); unit.updateNavigation(1) } }
  return { unit, step, bus: () => fmsOutputs(unit, sim) }
}
const consistent = (label: string, word: Word<unknown>) => {
  expect(['NORMAL', 'NCD', 'FAIL'], label).toContain(word.status)
  expect(word.value === null, label).toBe(word.status !== 'NORMAL')
}

// The bus owner proves real receiver-to-word mapping; the rendered owner separately proves needle/flag wiring.
// Independent CIFP2609 oracle: SCANDB HBT N55185640 W160310622, 390.0 kHz. Due south on the same meridian,
// the true bearing is north; with heading east (90), relative bearing is 270. No production bearing helper.
test('F16 ADF bus carries HBT raw bearing, NCD off air and FAIL for each receiver or measurement-bus failure', () => {
  const { unit, step, bus } = bench()
  expect(unit.loadArinc424(readFileSync('tests/fixtures/cifp/pasd-2609.pc', 'latin1'), 'pasd-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.setAircraft({ position: { lat: 55 + 18 / 60 + 56.4 / 3600 - 0.02, lon: -(160 + 31 / 60 + 6.22 / 3600) }, heading: 90, track: 180 })
  unit.setRadio('adf', '0390'); unit.setRadio('adf2', '0390'); step(2)
  for (const device of ['adf', 'adf2'] as const) {
    expect(bus().radioMeasurements[device].adfBearing.status).toBe('NORMAL')
    expect(bus().radioMeasurements[device].adfBearing.value).toBeCloseTo(270, 7)
  }
  unit.setNdbOffAir('HBT', true); step(1)
  for (const device of ['adf', 'adf2'] as const) expect(bus().radioMeasurements[device].adfBearing).toEqual({ value: null, status: 'NCD' })
  expect(unit.lastAdvisories.filter(text => /ADF/.test(text))).toEqual([])
  unit.setNdbOffAir('HBT', false); step(1)
  for (const device of ['adf', 'adf2'] as const) {
    for (const fault of [{ receiver: 'FAILED' as const }, { measurementBus: 'LOST' as const }]) {
      unit.setRadioFaults(device, fault); step(1)
      expect(bus().radioMeasurements[device].adfBearing).toEqual({ value: null, status: 'FAIL' })
      const other = device === 'adf' ? 'adf2' : 'adf'
      expect(bus().radioMeasurements[other].adfBearing).toEqual({ value: 270, status: 'NORMAL' })
      unit.setRadioFaults(device, { receiver: 'NORMAL', measurementBus: 'NORMAL' }); step(1)
      expect(bus().radioMeasurements[device].adfBearing).toEqual({ value: 270, status: 'NORMAL' })
    }
  }
})

test('every radio and source word is on the exhaustive bus with provenance, and each is a word with its own validity', () => {
  for (const key of ['radios', 'radioMeasurements'] as const) expect(FMS_OUTPUT_TAGS[key], key).toMatchObject({ kind: 'data', validity: 'word per field' })
  for (const key of ['kalman', 'dvs'] as const) expect(FMS_OUTPUT_TAGS[key], key).toMatchObject({ kind: 'data', validity: 'word' })
  const { step, bus } = bench()
  step(60)
  const words = bus()
  // C4's radio vocabulary: each item is a field of a radio's words or of a measurement.
  const fields = new Set([...Object.values(words.radios).flatMap(Object.keys), ...Object.values(words.radioMeasurements).flatMap(Object.keys)])
  for (const item of NAV_OUTPUT_VOCABULARY.radios) {
    if (item === 'controlPath' || item === 'measurementBus' || item === 'receiver' || item === 'activeFrequency' || item === 'commandStatus' || item === 'stationIdent'
      || item === 'vorRadial' || item === 'dmeDistance' || item === 'adfBearing' || item === 'tacanBearing' || item === 'tacanDistance') expect(fields.has(item), item).toBe(true)
  }
  expect(Object.keys(words.radios).sort()).toEqual([...BUS_RADIOS].sort())
  for (const [device, radio] of Object.entries(words.radios)) for (const [name, word] of Object.entries(radio)) consistent(`${device}.${name}`, word)
  for (const [device, measured] of Object.entries(words.radioMeasurements)) for (const [name, word] of Object.entries(measured)) consistent(`${device}.${name}`, word as Word<unknown>)
  consistent('kalman', words.kalman)
  consistent('dvs', words.dvs)
})

test('healthy radios: the frequency each reports, its health states, and NAV and DME measurements checked against the geometry', () => {
  const { unit, step, bus } = bench()
  step(60)
  const words = bus()
  // NAV1 in AUTO on the nearest VOR/DME (M300 13-21); the TACAN on its channel.
  expect(words.radios.nav1.activeFrequency).toEqual({ value: unit.radioPort!.state.nav1, status: 'NORMAL' })
  expect(words.radios.tacan.activeFrequency).toEqual({ value: unit.radioPort!.state.tacan, status: 'NORMAL' })
  for (const device of BUS_RADIOS) {
    expect(words.radios[device].controlPath, device).toEqual({ value: 'NORMAL', status: 'NORMAL' })
    expect(words.radios[device].measurementBus, device).toEqual({ value: 'NORMAL', status: 'NORMAL' })
    expect(words.radios[device].receiver, device).toEqual({ value: 'NORMAL', status: 'NORMAL' })
  }
  // The station NAV1 measures, and its radial within the compass.
  const station = unit.navStation('nav1')!
  expect(words.radioMeasurements.nav1.stationIdent).toEqual({ value: station.ident, status: 'NORMAL' })
  expect(words.radioMeasurements.nav1.vorRadial.status).toBe('NORMAL')
  expect(words.radioMeasurements.nav1.vorRadial.value!).toBeGreaterThanOrEqual(0)
  expect(words.radioMeasurements.nav1.vorRadial.value!).toBeLessThan(360)
  // DME1's slant range, against the geometry: the horizontal distance to its station and the height above it.
  const dme = unit.dmeStation('dme1')!
  const ground = distanceNm(unit.truePosition, dme.dmePosition ?? dme.position), height = (unit.physicalAltitude - dme.elevation.feet) / 6076.12
  expect(words.radioMeasurements.dme1.stationIdent).toEqual({ value: dme.ident, status: 'NORMAL' })
  expect(words.radioMeasurements.dme1.dmeDistance.value!).toBeCloseTo(Math.hypot(ground, height), 1)
  // Nothing tuned to an NDB here: the ADF bearing is no computed data, not a failure. The TACAN is not measured yet.
  expect(words.radioMeasurements.adf.adfBearing).toEqual({ value: null, status: 'NCD' })
  expect(words.radioMeasurements.tacan).toEqual({ tacanBearing: { value: null, status: 'NCD' }, tacanDistance: { value: null, status: 'NCD' } })
  // No crew command yet (AUTO tuning issues none): no command status.
  expect(words.radios.nav1.commandStatus).toEqual({ value: null, status: 'NCD' })
})

test('a lost measurement bus fails what the radio reports and measures, while its health words say why; the other radio is untouched', () => {
  const { unit, step, bus } = bench()
  step(60)
  unit.setRadioFaults('nav1', { measurementBus: 'LOST' })
  step(2)
  const words = bus()
  expect(words.radios.nav1.measurementBus).toEqual({ value: 'LOST', status: 'NORMAL' })
  expect(words.radios.nav1.controlPath).toEqual({ value: 'NORMAL', status: 'NORMAL' })
  expect(words.radios.nav1.activeFrequency).toEqual({ value: null, status: 'FAIL' })
  expect(words.radioMeasurements.nav1).toEqual({ stationIdent: { value: null, status: 'FAIL' }, vorRadial: { value: null, status: 'FAIL' } })
  expect(words.radios.nav2.activeFrequency.status).toBe('NORMAL')
  // A failed receiver fails it the same way.
  unit.setRadioFaults('nav2', { receiver: 'FAILED' })
  step(2)
  expect(bus().radios.nav2.receiver).toEqual({ value: 'FAILED', status: 'NORMAL' })
  expect(bus().radioMeasurements.nav2.vorRadial).toEqual({ value: null, status: 'FAIL' })
})

test('a lost control path times a command out; reception continues, so the reported frequency stays valid', () => {
  const { unit, step, bus } = bench()
  step(60)
  const before = unit.radioPort!.state.nav1
  unit.setRadioFaults('nav1', { controlPath: 'LOST' })
  unit.setRadio('nav1', '115.20')
  step(1)
  expect(bus().radios.nav1.commandStatus).toEqual({ value: 'PENDING', status: 'NORMAL' })
  step(5)
  const words = bus()
  expect(words.radios.nav1.commandStatus).toEqual({ value: 'TIMEOUT', status: 'NORMAL' })
  expect(words.radios.nav1.controlPath).toEqual({ value: 'LOST', status: 'NORMAL' })
  expect(words.radios.nav1.activeFrequency).toEqual({ value: before, status: 'NORMAL' })
})

test('KALMAN and DVS are words of their own, selected or not; a failed FMS sends FAIL on every radio and source word', () => {
  const { unit, step, bus } = bench()
  step(120)
  const words = bus()
  const kalman = unit.sensorSolutions.find(solution => solution.mode === 'KALMAN')!
  const dvs = unit.sensorSolutions.find(solution => solution.mode === 'DVS')!
  expect(words.kalman).toEqual({ value: { available: kalman.available, accuracy95Nm: kalman.accuracy95Nm }, status: 'NORMAL' })
  expect(words.dvs).toEqual({ value: { available: dvs.available, accuracy95Nm: dvs.accuracy95Nm }, status: 'NORMAL' })
  unit.setCondition('fmsFail', true)
  const failed = bus()
  expect(failed.kalman.status).toBe('FAIL')
  expect(failed.dvs.status).toBe('FAIL')
  for (const device of BUS_RADIOS) for (const [name, word] of Object.entries(failed.radios[device])) expect(word, `${device}.${name}`).toEqual({ value: null, status: 'FAIL' })
  for (const [device, measured] of Object.entries(failed.radioMeasurements)) for (const [name, word] of Object.entries(measured)) expect(word, `${device}.${name}`).toEqual({ value: null, status: 'FAIL' })
})
