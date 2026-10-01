import { expect, logicTest as test } from './isolated-client-test'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import type { Navaid } from '../src/fmsCdu/navData'
import { radioFixes, type DmeStationStatus } from '../src/fmsCdu/radioNavigation'
import { CivilNavigation } from '../src/fmsCdu/civilNavigation'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { RadioObservation } from '../src/fmsCdu/sensorPorts'

// Stage F plan F6 (M300 1-4, 12-16 to 12-18, 15-3): DME/DME from up to six scanned stations, each with its status;
// three inconsistent ranges leave the mode unavailable with no culprit; four or more isolate a station only on a unique
// hypothesis; the accuracy reproduces 15-3's typical figures; a deselected station is never used for DME/DME.

const AT = { lat: 0, lon: 0 }, ALT = 3000, NOW = 10_000
const station = (ident: string, bearing: number, distance = 10): Navaid => ({ kind: 'navaid', ident, type: 'DME', name: 'Fixture', frequency: '115.00',
  elevation: { feet: 0, source: 'data', provenance: 'fixture' }, position: { lat: Math.cos(bearing * Math.PI / 180) * distance / 60, lon: Math.sin(bearing * Math.PI / 180) * distance / 60 } })
const observe = (stations: Navaid[], bias: Record<string, number> = {}): RadioObservation[] => stations.map(s => ({ station: s,
  slantRangeNm: { at: NOW, sequence: 1, status: 'NORMAL', value: Math.hypot(s.position.lat * 60, s.position.lon * 60, ALT / 6076.12) + (bias[s.ident] ?? 0) },
  bearingTrue: { at: NOW, sequence: 1, status: 'NCD', value: null } }))
const solve = (observations: RadioObservation[], options = {}) => {
  const statuses: DmeStationStatus[] = []
  const fixes = radioFixes(observations, AT, ALT, NOW, undefined, options, statuses)
  return { fix: fixes.find(f => f.mode === 'DME/DME') ?? null, statuses }
}
const status = (statuses: DmeStationStatus[], ident: string) => statuses.find(s => s.ident === ident)

test('F6: the accuracy reproduces M300 15-3 at a right-angle crossing: 0.5 NM en route, 0.4 NM terminal', () => {
  const pair = [station('A', 0), station('B', 90), station('C', 225)]
  const enRoute = solve(observe(pair), { typical95Nm: 0.5 }).fix!
  const terminal = solve(observe(pair), { typical95Nm: 0.4 }).fix!
  // The best pair crosses at 90 degrees here; the residual of exact ranges is about the bench range bias (0.02 NM) or less.
  expect(enRoute.anp).toBeGreaterThanOrEqual(0.5)
  expect(enRoute.anp).toBeLessThan(0.56)
  expect(terminal.anp).toBeGreaterThanOrEqual(0.4)
  expect(terminal.anp).toBeLessThan(0.46)
})

test('F6: three consistent ranges are all used; three inconsistent ones leave DME/DME unavailable and name no culprit', () => {
  const three = [station('A', 0), station('B', 120), station('C', 240)]
  const good = solve(observe(three))
  expect(distanceNm(good.fix!.position, AT)).toBeLessThan(0.05)
  expect(good.statuses.map(s => s.status)).toEqual(['USED', 'USED', 'USED'])
  const bad = solve(observe(three, { B: 1.5 }))
  expect(bad.fix).toBeNull()
  // Every station carries the same reason: the check found disagreement, not its source.
  expect(new Set(bad.statuses.map(s => `${s.status} ${s.reason}`))).toEqual(new Set(['REJ ranges inconsistent; no unique station to exclude']))
})

test('F6: four ranges with one biased station isolate it on a unique hypothesis; two biased stations leave the mode unavailable', () => {
  const four = [station('A', 0, 9), station('B', 90, 12), station('C', 180, 9), station('D', 270, 12)]
  const one = solve(observe(four, { C: 3 }))
  expect(one.fix).not.toBeNull()
  expect(distanceNm(one.fix!.position, AT)).toBeLessThan(0.05)
  expect(status(one.statuses, 'C')).toEqual({ ident: 'C', status: 'REJ', reason: 'range inconsistent with the others (isolated)' })
  for (const ident of ['A', 'B', 'D']) expect(status(one.statuses, ident)!.status).toBe('USED')
  const two = solve(observe(four, { B: 3, C: 3 }))
  expect(two.fix).toBeNull()
  expect(two.statuses.every(s => s.reason === 'ranges inconsistent; no unique station to exclude')).toBe(true)
})

test('F6: four ranges whose geometry admits more than one plausible exclusion leave the mode unavailable, with no culprit', () => {
  // Here excluding the biased C, or excluding A, both leave a consistent set: the geometry cannot tell which is wrong.
  const four = [station('A', 0, 9), station('B', 100, 12), station('C', 200, 9), station('D', 300, 12)]
  const result = solve(observe(four, { C: 1.5 }))
  expect(result.fix).toBeNull()
  expect(result.statuses.every(s => s.status === 'REJ' && s.reason === 'ranges inconsistent; no unique station to exclude')).toBe(true)
})

test('F6: stations whose every pairing fails the crossing-angle check are REJ for geometry, not inconsistent', () => {
  // All three lie along one line through the aircraft: every crossing angle is near 0 or 180 degrees.
  const line = [station('A', 0, 10), station('B', 180, 10), station('C', 0, 20)]
  const result = solve(observe(line))
  expect(result.fix).toBeNull()
  expect(result.statuses.every(s => s.status === 'REJ' && s.reason === 'geometry')).toBe(true)
})

test('F6: a deselected station is never used for DME/DME', () => {
  const four = [station('A', 0), station('B', 90), station('C', 180, 12), station('D', 270, 8)]
  const result = solve(observe(four), { dmeDeselected: new Set(['B']) })
  expect(result.fix!.dmes).not.toContain('B')
  expect(result.statuses.map(s => s.ident)).not.toContain('B')
})

function unit() {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  const lines = () => screenText(fms.screen())
  return { fms, step, lines }
}

test('F6: DME STATUS lists the scanned stations with status, frequency and range, the scan control and the DME/DME position (M300 12-17)', () => {
  const { fms, step, lines } = unit()
  const six = [station('A', 0), station('B', 90), station('C', 180, 12), station('D', 270, 8), station('E', 45), station('F', 225)]
    .map((entry, i) => ({ ...entry, frequency: `11${i}.00` }))
  expect(fms.loadNavData({ cycle: { id: 'SIX-LITERAL', from: '2026-09-01', to: '2026-10-28' }, entries: six, airways: [], procedures: [] })).toEqual({ loaded: 'SIX-LITERAL' })
  fms.swapCycles()
  fms.setAircraft({ position: AT, altitude: ALT })
  step(8)
  fms.open('NAV_STATUS')
  fms.press('LSK5R')
  expect(lines()[0]).toMatch(/^DME STATUS\s+1\/1$/)
  const roster = fms.radioPort!.scanRoster()
  expect(roster.map(entry => entry.ident).sort()).toEqual(['A', 'B', 'C', 'D', 'E', 'F'])
  expect(fms.dmeStatus).toHaveLength(6)
  expect(fms.dmeStatus.map(entry => entry.status)).toEqual(['', '', '', '', '', ''])
  for (const [i, entry] of roster.entries()) expect(lines()[2 + i]).toMatch(new RegExp(`^${entry.ident}\\s`))
  expect(lines()[8]).toMatch(/SCANNING CTRL ACTIVE/)
  expect(lines()[12]).toMatch(/^<NAV STATUS\s+DME DESEL>$/)
  // A station the radios do not answer for shows N/A.
  fms.setRadioFaults('dme1', { measurementBus: 'LOST' }); fms.setRadioFaults('dme2', { measurementBus: 'LOST' })
  step(5)
  expect(fms.dmeStatus.every(entry => entry.status === 'N/A')).toBe(true)
  expect(lines()[8]).toMatch(/SCANNING CTRL ACTIVE/) // Reception loss is not control-path loss.
  for (const receiver of ['dme1', 'dme2'] as const) fms.setRadioFaults(receiver, { measurementBus: 'NORMAL', controlPath: 'LOST' })
  fms.updateNavigation(0)
  expect(lines()[8]).toMatch(/SCANNING CTRL LOST/)
  expect(fms.radioPort!.dmeReceiving('dme1')).toBe(true)
})

test('F6: DME DESELECT takes up to 25 stations, five a page, and CLR removes one; a deselected station leaves the scan (M300 12-18)', () => {
  const { fms, step, lines } = unit()
  step(3)
  fms.open('DME_STATUS')
  fms.press('LSK6R')
  expect(lines()[0]).toMatch(/^DME DESELECT\s+1\/1$/)
  const scanned = fms.radioPort!.scanRoster()[0].ident
  fms.setScratch('NOTANAV'); fms.press('LSK1L')
  expect(lines()[SCRATCHPAD_LINE].trim()).toMatch(/INVALID ENTRY|NOT IN DATA BASE/)
  fms.press('CLR')
  fms.setScratch(scanned); fms.press('LSK1L')
  expect(fms.dmeDeselectedStations).toEqual([scanned])
  expect(lines()[2]).toMatch(new RegExp(`^${scanned}\\s+\\d+\\.\\d+ MHZ\\s*$`))
  step(2)
  expect(fms.radioPort!.scanRoster().map(s => s.ident)).not.toContain(scanned)
  expect(fms.lastRadioFixes.find(f => f.mode === 'DME/DME')?.dmes ?? []).not.toContain(scanned)
  fms.press('CLR'); fms.press('LSK1L')
  expect(fms.dmeDeselectedStations).toEqual([])
  // The list holds 25 at most.
  const many = Array.from({ length: 30 }, (_, i) => `D${String(i).padStart(2, '0')}`)
  fms.setDmeDeselected(many)
  expect(fms.dmeDeselectedStations).toHaveLength(25)
  expect(lines()[0]).toMatch(/^DME DESELECT\s+1\/5$/)
})

test('F6: all six consistency witnesses retain their original epochs and limit an independent NAIM backup', () => {
  const six = [station('A', 0), station('B', 90), station('C', 180, 12), station('D', 270, 8), station('E', 45), station('F', 225)]
  const observations = observe(six).map((observation, index) => ({ ...observation,
    rangeIdentity: { receiver: 'dme1' as const, channel: 2 as const, frequency: observation.station.frequency, commandSequence: 9 },
    slantRangeNm: { ...observation.slantRangeNm, at: index === 5 ? NOW - 4000 : NOW } }))
  const original = structuredClone(observations)
  const fixes = radioFixes(observations, AT, ALT, NOW, undefined, { rangeMaxAgeS: 6,
    motion: { source: 'DVS', at: NOW, northKt: 0, eastKt: 0, gpsDependent: false } })
  const fix = fixes.find(candidate => candidate.mode === 'DME/DME')!
  expect(fix).toBeDefined()
  expect(fix.dmes).not.toContain('F') // F checks consistency even though the best pair did not range on it.
  expect(fix.observations).toEqual(original)
  expect(fix.oldestAt).toBe(NOW - 4000)
  expect(fix.priorResolved).toBe(false)
  const compare = (now: number) => new CivilNavigation(AT).update({ dt: 0, now, naimMaxAgeS: 4, air: null,
    gps: null, uncertainGps: { position: AT, accuracy95Nm: 0.05, hilNm: 5, receiver: 1, northKt: null, eastKt: null },
    radios: fixes, radio: null, radioApproved: true, rnp: 1 })
  expect(compare(NOW).sensors.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeGreaterThan(0.5)
  expect(compare(NOW).sensors.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeLessThan(0.7)
  expect(compare(NOW + 1).sensors.find(sensor => sensor.mode === 'GPS')!.naimComparisonNm).toBeNull()
  expect(observations).toEqual(original)
})
