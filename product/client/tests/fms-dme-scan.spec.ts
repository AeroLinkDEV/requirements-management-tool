import { expect, logicTest as test } from './isolated-client-test'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { RadioManagementSystem, SCAN_CHANNELS } from '../src/fmsCdu/radioManagement'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// Stage F plan C3 (M300 12-16; R3-01): two three-channel DMEs scan a roster of up to six stations on the scan channels
// whose DME measures; each station's newest range is cached with its own measurement time, and a fix built from cached
// ranges widens its accuracy by the motion over its oldest range's age, bounded from air data only.

const T0 = Date.UTC(2026, 8, 30, 14)
const parameters = HELICOPTER_PROFILE.parameters
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

/** Flown along the route with GPS lost until DME/DME solves, then DME2's bus lost: DME1's two scan channels rotate. */
function scanning() {
  let now = T0
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  fms.setCondition('apirsFail', true); fms.setCondition('dvsFail', true)
  fms.setCondition('gpsLost', true)
  const step = (seconds: number, until?: () => boolean) => { for (let i = 0; i < seconds; i++) { now += 1000; sim.step(1); if (until?.()) return } }
  step(3600, () => fms.lastRadioFixes.some(entry => entry.mode === 'DME/DME'))
  expect(fms.radioPort!.scanRoster().length).toBeGreaterThanOrEqual(3)
  fms.setRadioFaults('dme2', { measurementBus: 'LOST' })
  return { fms, step, now: () => now }
}

test('C3: with fewer channels than stations a station keeps its cached range after its channel moves on, with its own time and age limit', () => {
  const { fms, step, now } = scanning()
  let older = false
  for (let t = 0; t < 12; t++) {
    step(1)
    for (const observation of fms.cachedRanges.values()) {
      const age = now() - observation.slantRangeNm.at
      expect(age).toBeLessThanOrEqual(parameters.dmeRangeCacheAge.value * 1000)
      if (age > 0 && fms.radioPort!.scanning().every(entry => entry.ident !== observation.station.ident)) older = true
    }
  }
  expect(older).toBe(true)
})

test('C3: repropagation never renews a range: a fix keeps its oldest range time, and its accuracy carries the motion over that age', () => {
  const { fms, step, now } = scanning()
  let checked = 0
  for (let t = 0; t < 12; t++) {
    step(1)
    const fix = fms.lastRadioFixes.find(entry => entry.mode === 'DME/DME')
    if (!fix) continue
    const age = (now() - fix.oldestAt) / 1000
    expect(fix.oldestAt).toBeLessThanOrEqual(now())
    expect(age).toBeLessThanOrEqual(parameters.dmeRangeCacheAge.value)
    // The motion term: the age at TAS plus the declared wind; the geometry model adds at least 0.25 NM besides.
    expect(fix.anp).toBeGreaterThanOrEqual(0.25 + age / 3600 * (fms.trueAirspeed! + parameters.rangeMotionWindAllowance.value) - 1e-9)
    if (age > 0) checked++
  }
  expect(checked).toBeGreaterThan(0)
})

test('C3: a DME whose bus is lost loses its cached ranges at once; a deselected station loses its range', () => {
  const { fms, step } = scanning()
  step(6)
  expect(fms.cachedRanges.size).toBeGreaterThan(0)
  fms.setRadioFaults('dme1', { measurementBus: 'LOST' })
  step(1)
  expect(fms.cachedRanges.size).toBe(0)
  fms.setRadioFaults('dme1', { measurementBus: 'NORMAL' })
  step(6)
  const station = [...fms.cachedRanges.keys()][0]
  expect(station).toBeDefined()
  fms.setInhibited([station])
  step(1)
  expect(fms.cachedRanges.has(station)).toBe(false)
})

test('C3 (R3-01): a GPS-only change cannot move an independent radio fix', () => {
  let now = T0
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  fms.setCondition('apirsFail', true); fms.setCondition('dvsFail', true)
  const step = (seconds: number, until?: () => boolean) => { for (let i = 0; i < seconds; i++) { now += 1000; sim.step(1); if (until?.()) return } }
  step(3600, () => fms.lastRadioFixes.some(entry => entry.mode === 'DME/DME'))
  const fix = () => fms.lastRadioFixes.find(entry => entry.mode === 'DME/DME')
  const before = fix()!
  // Bias both receivers' latitude words by one minute: the GPS moves 1 NM north; the radio fix does not follow it.
  for (const index of [0, 1]) stimulusFor(fms).apply(index, { op: 'override', label: '110', kind: 'BIAS', amount: 1 / 60 })
  step(1)
  const after = fix()
  expect(after).toBeDefined()
  expect(distanceNm(after!.position, fms.truePosition)).toBeLessThan(0.5)
  expect(distanceNm(after!.position, before.position)).toBeLessThan(0.2)
})
