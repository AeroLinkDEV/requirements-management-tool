import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { distanceNm, offset, type LatLon } from '../src/fmsCdu/fmsModel'
import { DEMO_NAV_DATA, pairedChannel, type Navaid } from '../src/fmsCdu/navData'
import { BenchRadioReceiver, horizontalRange, radioFixes } from '../src/fmsCdu/radioNavigation'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import type { RadioObservation } from '../src/fmsCdu/sensorPorts'

// Stage F1 of the Stage F plan: the navaid data the radio sensors need. DME-only stations are read at their DME's
// position; a station's elevation and a co-located DME's own position come from the data; TACAN stations are read
// (DEC-150: TACAN on); missing elevation is stated as assumed, never a silent zero, and a range solution carries that
// uncertainty. The records are FAA CIFP 2609 (public domain), as the bench's fixtures hold them.

const KBTV = readFileSync('tests/fixtures/cifp/kbtv-2609.pc', 'latin1')
const PINS = readFileSync('tests/fixtures/cifp/copter-pins-2609.pc', 'latin1')
const navaid = (text: string, ident: string) => parseArinc424(text).data.entries.find((e): e is Navaid => e.kind === 'navaid' && e.ident === ident)
const ftToNm = (feet: number) => feet / 6076.12

/** One record changed at columns a..b (1-based), padded to width. */
function withColumns(record: string, changes: [number, number, string][]) {
  let line = record
  for (const [a, b, text] of changes) line = line.slice(0, a - 1) + text.padEnd(b - a + 1).slice(0, b - a + 1) + line.slice(b)
  return line
}
const recordOf = (text: string, ident: string) => text.split(/\r?\n/).find(line => line.slice(4, 6) === 'D ' && line.slice(13, 17).trim() === ident)!

test('F1: a DME-only record is read with its own position and elevation (the KBTV ILS DME)', () => {
  const ibtv = navaid(KBTV, 'IBTV')!
  expect(ibtv).toBeDefined()
  expect(ibtv.type).toBe('DME')
  // N44°27'51.34" W073°08'26.91", 342 ft, from the record's DME fields (56-74, 80-84).
  expect(ibtv.position.lat).toBeCloseTo(44 + 27 / 60 + 51.34 / 3600, 7)
  expect(ibtv.position.lon).toBeCloseTo(-(73 + 8 / 60 + 26.91 / 3600), 7)
  expect(ibtv.elevation).toEqual({ feet: 342, source: 'data', provenance: 'ARINC 424 DME elevation (columns 80-84)' })
  // 110.30 MHz pairs with DME channel 40X.
  expect(ibtv).toMatchObject({ frequency: '110.30', channel: '40X' })
})

test('F1: VOR/DME and VORTAC stations carry their elevation, channel and the DME\'s own position when it differs', () => {
  expect(navaid(KBTV, 'BTV')).toMatchObject({ type: 'VORDME', elevation: { feet: 417, source: 'data' }, channel: '122X' })
  // HAMPTON, class VTHW: a VORTAC, 113.60 paired with TACAN channel 83X, 22 ft.
  expect(navaid(PINS, 'HTO')).toMatchObject({ type: 'VORTAC', elevation: { feet: 22, source: 'data' }, channel: '83X' })
  // COLTS NECK: the DME stands 0.01" of latitude from its VOR, and ranges are measured from it.
  const col = navaid(PINS, 'COL')!
  expect(col.dmePosition).toBeDefined()
  expect(col.dmePosition!.lat - col.position.lat).toBeCloseTo(0.01 / 3600, 9)
})

test('F1: a TACAN-only station is read as TACAN at its DME position; a record naming no VOR, DME or TACAN is refused, saying why', () => {
  const base = recordOf(PINS, 'HTO')
  // No VOR (class " TH  ", VOR position blank): a TACAN, placed at its DME.
  const tacan = withColumns(base, [[14, 17, 'TCN'], [28, 32, ' TH'], [33, 51, '']])
  const read = parseArinc424(tacan).data.entries.find((e): e is Navaid => e.kind === 'navaid' && e.ident === 'TCN')!
  expect(read).toMatchObject({ type: 'TACAN', channel: '83X', elevation: { feet: 22, source: 'data' } })
  expect(distanceNm(read.position, navaid(PINS, 'HTO')!.position)).toBeLessThan(0.01)
  // A class with neither a VOR nor a DME: skipped, with the reason.
  const nothing = parseArinc424(withColumns(base, [[14, 17, 'NIL'], [28, 32, '  H']]))
  expect(nothing.data.entries.some(e => e.kind === 'navaid' && e.ident === 'NIL')).toBe(false)
  expect(nothing.errors.join('\n')).toMatch(/navaid class "H" names no VOR, DME or TACAN/)
})

test('F1: the loader refuses malformed DME records, stating the reason', () => {
  const base = recordOf(KBTV, 'IBTV')
  // A DME-only record without its DME position.
  const unplaced = parseArinc424(withColumns(base, [[56, 74, '']]))
  expect(unplaced.errors.join('\n')).toMatch(/DME-only record without a DME position/)
  expect(unplaced.data.entries.some(e => e.kind === 'navaid' && e.ident === 'IBTV')).toBe(false)
  // An impossible DME position (95° of latitude) condemns the file, like any impossible position.
  const impossible = parseArinc424(withColumns(base, [[56, 64, 'N95275134']]))
  expect(impossible.invalid.join('\n')).toMatch(/impossible DME position/)
  // An elevation out of range, likewise.
  const high = parseArinc424(withColumns(base, [[80, 84, '99999']]))
  expect(high.invalid.join('\n')).toMatch(/impossible DME elevation/)
})

test('F1: a record without an elevation is marked assumed, never a silent sea level; the invented demonstration stations stand on the ground under them', () => {
  const blank = parseArinc424(withColumns(recordOf(KBTV, 'BTV'), [[80, 84, '']]))
  const btv = blank.data.entries.find((e): e is Navaid => e.kind === 'navaid' && e.ident === 'BTV')!
  expect(btv.elevation).toEqual({ feet: 0, source: 'assumed', provenance: 'no elevation in the record' })
  // The invented stations: the Terrarium ground elevation at each invented site (z14, read 30 September 2026).
  const demo = Object.fromEntries(DEMO_NAV_DATA.entries.filter((e): e is Navaid => e.kind === 'navaid').map(n => [n.ident, n.elevation]))
  expect(demo).toMatchObject({ YOW: { feet: 433, source: 'terrain' }, YUL: { feet: 157, source: 'terrain' }, HWK: { feet: 153, source: 'terrain' }, RIG: { feet: 248, source: 'terrain' } })
  for (const [ident, elevation] of Object.entries(demo)) {
    expect(elevation.source, ident).toBe('terrain')
    // The provenance says what the figure is: the ground's, not the antenna's.
    expect(elevation.provenance, ident).toMatch(/Terrarium ground elevation.*not the antenna/)
  }
})

test('F1: the DME channel pairing follows ICAO Annex 10 (Vol I, Attachment C, Table A)', () => {
  const pairs: [number, string | null][] = [
    [108.0, '17X'], [108.05, '17Y'], [108.1, '18X'], [112.25, '59Y'], [112.3, '70X'], [113.6, '83X'], [117.95, '126Y'],
    [107.95, null], [118.0, null], [108.03, null],
  ]
  for (const [mhz, channel] of pairs) expect(pairedChannel(mhz), String(mhz)).toBe(channel)
})

/** A DME station for the range tests. */
const station = (ident: string, position: LatLon, feet: number, source: 'data' | 'terrain' | 'assumed' = 'data'): Navaid =>
  ({ kind: 'navaid', ident, type: 'DME', position, frequency: '110.30', name: ident, elevation: { feet, source, provenance: 'test' } })
const AT: LatLon = { lat: 44.5, lon: -73.2 }

test('F1: the slant range is measured over the height above the station, not above sea level', () => {
  // A DME on a 3,000 ft summit, the aircraft level with it 1 NM away: the slant range is the horizontal 1 NM.
  const summit = station('SUMT', offset(AT, 0, 1), 3000)
  const receiver = new BenchRadioReceiver()
  receiver.tune([summit], 0)
  receiver.sample(AT, 3000, 0)
  const [level] = receiver.sample(AT, 3000, 5000)
  expect(level.slantRangeNm.status).toBe('NORMAL')
  expect(Math.abs(level.slantRangeNm.value! - 1)).toBeLessThanOrEqual(0.02 + 1e-9)
  // At 6,000 ft, 3,000 ft above it: hypot(1 NM, 3,000 ft), not hypot(1 NM, 6,000 ft) as a sea-level station would give.
  const [above] = receiver.sample(AT, 6000, 5250)
  expect(Math.abs(above.slantRangeNm.value! - Math.hypot(1, ftToNm(3000)))).toBeLessThanOrEqual(0.02 + 1e-9)
  expect(Math.abs(above.slantRangeNm.value! - Math.hypot(1, ftToNm(6000)))).toBeGreaterThan(0.2)
})

/** Exact slant ranges (no bias) from a position to each station, as observations at time t. */
function observed(stations: Navaid[], from: LatLon, altitudeFt: number, t: number): RadioObservation[] {
  return stations.map((s, sequence) => ({
    station: s,
    slantRangeNm: { at: t, sequence, status: 'NORMAL' as const, value: Math.hypot(distanceNm(from, s.dmePosition ?? s.position), ftToNm(altitudeFt - s.elevation.feet)) },
    bearingTrue: { at: t, sequence, status: 'NCD' as const, value: null },
  }))
}

test('F1: DME/DME solves from DME-only stations with their elevations, and a DME\'s own position is where its range is measured from', () => {
  // Stations on high ground, close, the aircraft at 9,000 ft: the height above each station matters to the fix.
  const east = station('DMEA', offset(AT, 90, 6), 5000), north = station('DMEB', offset(AT, 0, 5), 3000), south = station('DMEC', offset(AT, 200, 7), 6000)
  const fix = radioFixes(observed([east, north, south], AT, 9000, 1000), offset(AT, 45, 0.5), 9000, 1000).find(fix => fix.mode === 'DME/DME')!
  expect(fix.mode).toBe('DME/DME')
  expect(distanceNm(fix.position, AT)).toBeLessThan(0.01)
  expect(fix.assumedElevation).toEqual([])
  // The same ranges solved as if every station were at sea level land well away: the elevation is what put it right.
  const seaLevel = [east, north, south].map(s => ({ ...s, elevation: { feet: 0, source: 'data' as const } }))
  const wrong = radioFixes(observed([east, north, south], AT, 9000, 1000).map((o, i) => ({ ...o, station: seaLevel[i] })), offset(AT, 45, 0.5), 9000, 1000).find(fix => fix.mode === 'DME/DME')
  expect(wrong === undefined || distanceNm(wrong.position, AT) > 0.05).toBe(true)
  // A co-located DME 0.3 NM from its VOR, toward the aircraft: ranges from the DME solve to the aircraft; from the VOR they would not.
  const vorPosition = offset(AT, 90, 6)
  const split: Navaid = { ...east, type: 'VORDME', position: vorPosition, dmePosition: offset(vorPosition, 270, 0.3) }
  const fromDme = radioFixes(observed([split, north, south], AT, 9000, 1000), offset(AT, 45, 0.5), 9000, 1000).find(fix => fix.mode === 'DME/DME')!
  expect(distanceNm(fromDme.position, AT)).toBeLessThan(0.01)
  // Had the range been taken from the VOR's position, the fix would be off by about the separation.
  const fromVor = radioFixes(observed([split, north, south], AT, 9000, 1000).map((o, i) => (i === 0 ? { ...o, station: { ...split, dmePosition: undefined } } : o)), offset(AT, 45, 0.5), 9000, 1000).find(fix => fix.mode === 'DME/DME')
  expect(fromVor === undefined || distanceNm(fromVor.position, AT) > 0.1).toBe(true)
})

test('F1: a range corrected with an assumed elevation is named in the solution, whose accuracy widens by the height uncertainty', () => {
  const places = [offset(AT, 90, 6), offset(AT, 0, 5), offset(AT, 200, 7)]
  const known = places.map((p, i) => station(`DME${'ABC'[i]}`, p, 0))
  const assumed = places.map((p, i) => station(`DME${'ABC'[i]}`, p, 0, 'assumed'))
  const altitude = 9000
  const exact = radioFixes(observed(known, AT, altitude, 1000), AT, altitude, 1000).find(fix => fix.mode === 'DME/DME')!
  const unsure = radioFixes(observed(assumed, AT, altitude, 1000), AT, altitude, 1000).find(fix => fix.mode === 'DME/DME')!
  // The same place (the assumed 0 ft happens to be right here), but not the same claim about it. Pairs are ranked by their
  // accuracy, the elevation allowance included (plan C3), so the allowance may favour another pair: both are within the
  // range bias of each other.
  expect(distanceNm(unsure.position, exact.position)).toBeLessThan(0.05)
  expect(exact.assumedElevation).toEqual([])
  expect(unsure.assumedElevation).toEqual(unsure.dmes)
  // Each range is uncertain by |height| / range × the profile's assumed-elevation uncertainty: the ANP grows by at least one.
  expect(HELICOPTER_PROFILE.parameters.assumedNavaidElevationUncertainty).toMatchObject({ value: 1000, unit: 'ft', basis: 'lab' })
  const height = ftToNm(altitude)
  const perRange = unsure.dmes.map(ident => {
    const p = places['ABC'.indexOf(ident.slice(3))]
    return (height / distanceNm(AT, p)) * ftToNm(HELICOPTER_PROFILE.parameters.assumedNavaidElevationUncertainty.value)
  })
  expect(unsure.anp - exact.anp).toBeGreaterThan(Math.max(...perRange) * 0.99)
  // A terrain elevation (the ground at an invented site) widens it too, by its own, smaller allowance.
  const grounded = places.map((p, i) => station(`DME${'ABC'[i]}`, p, 0, 'terrain'))
  const terrainFix = radioFixes(observed(grounded, AT, altitude, 1000), AT, altitude, 1000).find(fix => fix.mode === 'DME/DME')!
  expect(terrainFix.terrainElevation).toEqual(terrainFix.dmes)
  expect(terrainFix.assumedElevation).toEqual([])
  expect(HELICOPTER_PROFILE.parameters.terrainNavaidElevationUncertainty).toMatchObject({ value: 100, unit: 'ft', basis: 'lab' })
  expect(terrainFix.anp).toBeGreaterThan(exact.anp)
  expect(terrainFix.anp).toBeLessThan(unsure.anp)
  // Every radio fix is a laboratory estimate (Stage F plan C1): its accuracy is a declared model, not a 95 percent bound.
  for (const fix of [exact, unsure, terrainFix]) expect(fix.accuracyBasis).toBe('laboratory')
})

test('F1: a TACAN\'s range serves DME/DME like any DME (DEC-150: TACAN on)', () => {
  const places = [offset(AT, 90, 6), offset(AT, 0, 5), offset(AT, 200, 7)]
  const stations: Navaid[] = places.map((p, i) => ({ ...station(`TAC${i}`, p, 500), type: 'TACAN' as const, channel: '83X' }))
  const fix = radioFixes(observed(stations, AT, 5000, 1000), offset(AT, 45, 0.5), 5000, 1000).find(fix => fix.mode === 'DME/DME')
  expect(fix).not.toBeNull()
  expect(fix!.mode).toBe('DME/DME')
  expect(fix!.dmes.every(ident => ident.startsWith('TAC'))).toBe(true)
  expect(distanceNm(fix!.position, AT)).toBeLessThan(0.01)
})

test('SF-08: a height allowance is carried through the slant-range geometry exactly, and costs more the nearer the aircraft is to overhead', () => {
  // Independent values: slant 5 NM, height 1 NM, allowance 0.1 NM. The range is sqrt(24) NM; the height could be 0.9
  // or 1.1 NM, so the range sqrt(24.19) or sqrt(23.79) NM; the allowance is the larger departure.
  const far = horizontalRange(5, 1, 0.1)
  expect(far).toEqual({ ok: true, rangeNm: Math.sqrt(24), allowanceNm: Math.max(Math.sqrt(24) - Math.sqrt(23.79), Math.sqrt(24.19) - Math.sqrt(24)) })
  // Below the aircraft or above it: the same by symmetry of the height.
  expect(horizontalRange(5, -1, 0.1)).toEqual(far)
  // The same 0.1 NM allowance near overhead: slant 1.2 NM over 1 NM of height, range sqrt(0.44) NM, and the range at
  // 1.1 NM of height is sqrt(0.23) NM: about 0.18 NM, eighteen times the far case's 0.02 NM.
  const near = horizontalRange(1.2, 1, 0.1)
  expect(near.ok).toBe(true)
  if (near.ok) {
    expect(near.rangeNm).toBeCloseTo(Math.sqrt(0.44), 12)
    expect(near.allowanceNm).toBeCloseTo(Math.sqrt(0.44) - Math.sqrt(0.23), 12)
    if (far.ok) expect(near.allowanceNm / far.allowanceNm).toBeGreaterThan(8)
  }
  // An exact elevation (no allowance): no widening.
  expect(horizontalRange(5, 1, 0)).toEqual({ ok: true, rangeNm: Math.sqrt(24), allowanceNm: 0 })
})

test('SF-08: impossible or undetermined geometry is refused with the reason, never clipped to a range', () => {
  // The slant range shorter than, or equal to, the height to the station: no horizontal range exists.
  expect(horizontalRange(0.9, 1, 0)).toEqual({ ok: false, reason: 'slant range no longer than the height to the station: impossible geometry' })
  expect(horizontalRange(1, 1, 0)).toEqual({ ok: false, reason: 'slant range no longer than the height to the station: impossible geometry' })
  // Longer than the height, but within the height allowance of it: the horizontal range could be anything from zero.
  expect(horizontalRange(1.05, 1, 0.1)).toEqual({ ok: false, reason: 'near overhead: the station elevation allowance could explain the whole slant range' })
  // Exactly at the limit (slant = height + allowance) is refused too: a zero range is not a measurement.
  expect(horizontalRange(1.1, 1, 0.1)).toEqual({ ok: false, reason: 'near overhead: the station elevation allowance could explain the whole slant range' })
  // Values that are not numbers, or negative.
  for (const [slant, height, allowance] of [[Number.NaN, 1, 0], [5, Number.POSITIVE_INFINITY, 0], [-5, 1, 0], [5, 1, -0.1]]) {
    expect(horizontalRange(slant, height, allowance)).toEqual({ ok: false, reason: 'slant range or height not valid' })
  }
})

test('SF-08: in a fix, a station whose range is refused is left out and named with the reason; the rest solve', () => {
  // Three good stations and a fourth almost directly below the aircraft with an assumed elevation: its range is refused.
  const good = [station('DMEA', offset(AT, 90, 6), 1000), station('DMEB', offset(AT, 0, 5), 1000), station('DMEC', offset(AT, 200, 7), 1000)]
  const below = station('DMEX', offset(AT, 45, 0.05), 0, 'assumed')
  const fix = radioFixes(observed([...good, below], AT, 1500, 1000), offset(AT, 45, 0.5), 1500, 1000).find(fix => fix.mode === 'DME/DME')!
  expect(fix).not.toBeNull()
  expect(fix.dmes).not.toContain('DMEX')
  expect(fix.rejected).toEqual([{ ident: 'DMEX', reason: 'near overhead: the station elevation allowance could explain the whole slant range' }])
  expect(distanceNm(fix.position, AT)).toBeLessThan(0.01)
})
