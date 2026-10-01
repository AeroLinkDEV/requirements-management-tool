import { expect, logicTest as test } from './isolated-client-test'
import { fmsOutputs, type FmsOutputs, type WordStatus } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import type { GpsReceiver } from '../src/fmsCdu/gps'
import { ANP_FLOOR_NM } from '../src/fmsCdu/gpsSensors'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { FMS_OUTPUT_TAGS } from '../src/fmsCdu/outputTags'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { NAV_OUTPUT_VOCABULARY } from '../src/fmsCdu/sensorState'

// Stage F F13 part 1 (F13 and C4 of the Stage F plan; Astra SF-07): the C1 values F2 computes reach the exhaustive output
// bus, each as its own word with provenance and validity: FAIL from a failed FMS, NCD for a value the selected solution
// does not have. Radio health, tuned stations, TACAN and DVS follow in part 2, after F8 and F11.

/** C4's performance vocabulary, and the bus word that carries each item (rnp and phase were already on the bus). */
const PERFORMANCE_WORDS: Record<(typeof NAV_OUTPUT_VOCABULARY.performance)[number], keyof FmsOutputs> = {
  accuracy95Nm: 'accuracy95Nm', accuracyBasis: 'accuracyBasis', integrityBoundNm: 'integrityBoundNm', integrityValid: 'integrityValid',
  uncertain: 'positionUncertain', naimComparisonNm: 'naimComparisonNm', gpsDependent: 'gpsDependent', rnp: 'rnp', phase: 'phase',
}
const C1_WORDS = ['accuracy95Nm', 'accuracyBasis', 'gpsDependent', 'integrityBoundNm', 'integrityBasis', 'integrityValid', 'positionUncertain', 'naimComparisonNm'] as const

const bench = () => {
  let now = Date.UTC(2026, 8, 30, 14)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const step = (seconds: number) => { for (let i = 0; i < seconds; i += 1) { now += 1000; unit.updateNavigation(1) } }
  const receiverBus = () => (unit as unknown as { gps: GpsReceiver[] }).gps[0].bus()!
  return { unit, sim, step, receiverBus, bus: () => fmsOutputs(unit, sim) }
}

test('every Stage F value is in the exhaustive table with provenance', () => {
  // Every C4 performance item has a bus word, and every word a tag naming where it comes from.
  for (const item of NAV_OUTPUT_VOCABULARY.performance) {
    const tag = FMS_OUTPUT_TAGS[PERFORMANCE_WORDS[item]]
    expect(tag, item).toBeDefined()
    expect(tag.kind === 'data' || tag.kind === 'annunciation', item).toBe(true)
    expect('provenance' in tag && tag.provenance.length > 10, item).toBe(true)
  }
  // The C1 values are data words, each with its own validity; the NAIM comparison says it is laboratory.
  for (const key of C1_WORDS) expect(FMS_OUTPUT_TAGS[key], key).toMatchObject({ kind: 'data', validity: 'word' })
  expect(FMS_OUTPUT_TAGS.naimComparisonNm).toMatchObject({ provenance: expect.stringMatching(/laboratory/) })
  expect(FMS_OUTPUT_TAGS.naimComparisonNm).toMatchObject({ provenance: expect.stringMatching(/never integrity/) })
  // Each word carries a status beside its value.
  const { step, bus } = bench()
  step(60)
  for (const key of C1_WORDS) {
    const word = bus()[key]
    expect(Object.keys(word).sort(), key).toEqual(['status', 'value'])
    expect(['NORMAL', 'NCD', 'FAIL'], key).toContain(word.status)
    // A value only with NORMAL; NCD and FAIL carry none.
    expect(word.value === null, key).toBe(word.status !== 'NORMAL')
  }
})

test('accuracy and integrity bound are separate words: the receiver HFOM and HIL, each its own value', () => {
  const { unit, step, receiverBus, bus } = bench()
  step(60)
  // GPS en route: the accuracy is the receiver's HFOM (label 247) under the ANP floor, the bound its HIL (label 130).
  const words = bus()
  expect(words.navMode).toBe('GPS')
  expect(words.accuracy95Nm).toEqual({ value: Math.max(ANP_FLOOR_NM, receiverBus()['247'].value), status: 'NORMAL' })
  expect(words.accuracyBasis).toEqual({ value: 'receiver', status: 'NORMAL' })
  expect(words.integrityBoundNm).toEqual({ value: receiverBus()['130'].value, status: 'NORMAL' })
  expect(words.integrityBasis).toEqual({ value: 'NP', status: 'NORMAL' })
  expect(words.integrityValid).toEqual({ value: true, status: 'NORMAL' })
  expect(words.gpsDependent).toEqual({ value: true, status: 'NORMAL' })
  // A HIL forced to 0.6 NM on both receivers moves the bound, never the accuracy.
  for (const index of [0, 1]) stimulusFor(unit).apply(index, { op: 'override', label: '130', kind: 'FORCE', amount: 0.6 })
  step(5)
  expect(bus().integrityBoundNm).toEqual({ value: 0.6, status: 'NORMAL' })
  expect(bus().accuracy95Nm).toEqual(words.accuracy95Nm)
  // The bench's forced ANP (TEST) changes the anp annunciation, not the sensor's accuracy word.
  unit.setCondition('rnpExceeded', true)
  expect(bus().anp).toBeGreaterThan(1)
  expect(bus().accuracy95Nm).toEqual(words.accuracy95Nm)
})

test('an uncertain GPS is kept without integrity; the NAIM comparison is its own laboratory word, never the bound', () => {
  const { unit, step, bus } = bench()
  step(60)
  // HIL 3 NM, over the en route limit of 2: GPS POS UNCERTAIN, the position kept and judged against the radio fix.
  for (const index of [0, 1]) stimulusFor(unit).apply(index, { op: 'override', label: '130', kind: 'FORCE', amount: 3 })
  step(20)
  const words = bus()
  expect(words.navMode).toBe('GPS')
  expect(words.positionUncertain).toEqual({ value: true, status: 'NORMAL' })
  expect(words.integrityValid).toEqual({ value: false, status: 'NORMAL' })
  expect(words.integrityBoundNm).toEqual({ value: 3, status: 'NORMAL' })
  // Retained only strictly below the limit, and never integrity.
  expect(words.naimComparisonNm.status).toBe('NORMAL')
  expect(words.naimComparisonNm.value!).toBeLessThan(2)
  expect(words.naimComparisonNm.value).not.toBe(words.integrityBoundNm.value)
})

test('a radio solution: a laboratory accuracy, no GPS dependency, integrity by criteria and no bound (NCD)', () => {
  const { unit, step, bus } = bench()
  step(60)
  // The uncertain GPS biased 0.05 NM away fails the comparison: the FMS reverts to the radio fix (M300 1-4).
  for (const index of [0, 1]) {
    stimulusFor(unit).apply(index, { op: 'override', label: '130', kind: 'FORCE', amount: 3 })
    stimulusFor(unit).apply(index, { op: 'override', label: '110', kind: 'BIAS', amount: 0.05 })
  }
  step(40)
  const words = bus()
  expect(['DME/DME', 'VOR/DME']).toContain(words.navMode)
  expect(words.accuracy95Nm.status).toBe('NORMAL')
  expect(words.accuracyBasis).toEqual({ value: 'laboratory', status: 'NORMAL' })
  expect(words.gpsDependent).toEqual({ value: false, status: 'NORMAL' })
  expect(words.integrityBasis).toEqual({ value: 'criteria', status: 'NORMAL' })
  expect(words.integrityBoundNm).toEqual({ value: null, status: 'NCD' })
  expect(words.naimComparisonNm).toEqual({ value: null, status: 'NCD' })
  expect(words.positionUncertain).toEqual({ value: false, status: 'NORMAL' })
})

test('a failed FMS sends FAIL on every navigation word', () => {
  const { unit, step, bus } = bench()
  step(60)
  unit.setCondition('fmsFail', true)
  const words = bus()
  for (const key of C1_WORDS) expect(words[key], key).toEqual({ value: null, status: 'FAIL' satisfies WordStatus })
})
