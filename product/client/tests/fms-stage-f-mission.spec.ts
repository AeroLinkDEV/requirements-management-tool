import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { COPTER_PINS_CIFP_2609, COPTER_PINS_CIFP_2609_SHA256 } from '../src/fmsCdu/data/copterPinsCifp2609'
import { courseDeg, distanceNm } from '../src/fmsCdu/fmsModel'
import { NavDatabase } from '../src/fmsCdu/navData'
import { HELICOPTER_PROFILE, LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import {
  REAL_COASTAL_STATIONS, SYNTHETIC_COASTAL_STATIONS, SYNTHETIC_PROVENANCE, canonicalJson, fixtureHash, stageFMissionManifest,
} from '../src/fmsCdu/stageFMission'

// F15 of the Stage F plan, Astra's integration item 7: the Stage F acceptance mission's fixture. It is reproducible
// (the same declared inputs give the same fixture hash), it names its data files by SHA-256, and the coastal coverage
// the real CIFP extract lacks is synthetic: flagged, declared with a terrain elevation (F1's contract), and never
// found by a lookup in the CIFP data. The expected results are slots for the items that will compute them.
const cifp = () => parseArinc424(COPTER_PINS_CIFP_2609).data

test('the same declared inputs give the same manifest and fixture hash; a changed input changes the hash', async () => {
  const first = stageFMissionManifest(), second = stageFMissionManifest()
  expect(canonicalJson(second)).toBe(canonicalJson(first))
  const hash = await fixtureHash(first)
  expect(hash).toMatch(/^sha256-[0-9a-f]{64}$/)
  expect(await fixtureHash(second)).toBe(hash)
  // Canonical: the order the keys were written in does not matter.
  const reordered = Object.fromEntries(Object.entries(first).reverse()) as typeof first
  expect(await fixtureHash(reordered)).toBe(hash)
  // Independently: SHA-256 of the canonical text.
  expect(hash).toBe(`sha256-${createHash('sha256').update(canonicalJson(first), 'utf8').digest('hex')}`)
  // Any changed input is a different fixture: another profile, a fault moved by one second, a station moved.
  expect(await fixtureHash(stageFMissionManifest(LAB_AIRLINE_VNAV_PROFILE))).not.toBe(hash)
  const moved = structuredClone(first)
  moved.segments[0].faults[0].at += 1
  expect(await fixtureHash(moved)).not.toBe(hash)
  const shifted = structuredClone(first)
  shifted.facilities.synthetic[0].position.lat += 0.0001
  expect(await fixtureHash(shifted)).not.toBe(hash)
})

test('the manifest names the profile, the data files by SHA-256, the procedure, the clock and every fault', () => {
  const manifest = stageFMissionManifest()
  expect(manifest.profile).toEqual({ id: HELICOPTER_PROFILE.id, version: HELICOPTER_PROFILE.version, fingerprint: expect.stringMatching(/^fnv1a-[0-9a-f]{8}$/) })
  expect(manifest.navData.cycle).toBe('2609')
  expect(manifest.navData.files).toHaveLength(3)
  // Each file's hash is its text's own.
  expect(createHash('sha256').update(COPTER_PINS_CIFP_2609, 'latin1').digest('hex')).toBe(manifest.navData.files[0].sha256)
  expect(manifest.navData.files[0].sha256).toBe(COPTER_PINS_CIFP_2609_SHA256)
  for (const file of manifest.navData.files.slice(1))
    expect(createHash('sha256').update(readFileSync(file.name)).digest('hex'), file.name).toBe(file.sha256)
  expect(manifest.procedures.map(procedure => `${procedure.airport} ${procedure.ident}`)).toEqual(['87N R190', 'KIAG N28', 'PASD Q32'])
  // Each procedure is in its file.
  expect(cifp().procedures.some(procedure => procedure.airport === '87N' && procedure.ident === 'R190')).toBe(true)
  for (const variant of manifest.ndbVariants) {
    const { data } = parseArinc424(readFileSync(variant.file, 'latin1'), { airports: [variant.airport] })
    expect(data.procedures.some(procedure => procedure.airport === variant.airport && procedure.ident === variant.procedure), `${variant.airport} ${variant.procedure}`).toBe(true)
  }
  expect(manifest.clock).toEqual({ start: '2026-09-29T15:00:00Z', tickSeconds: 0.25 })
  expect(manifest.segments.map(segment => segment.id)).toEqual(['offshore', 'coastal'])
  // Offshore: no radio coverage; the start 10 NM south of 87N at 500 ft, as the 87N mission's.
  const [offshore, coastal] = manifest.segments
  expect(offshore.facilities).toEqual([])
  const site = new NavDatabase(cifp()).airport('87N')!.position
  expect(distanceNm(site, offshore.initialState.position)).toBeCloseTo(10, 2)
  expect(courseDeg(site, offshore.initialState.position)).toBeCloseTo(180, 0)
  expect(offshore.initialState.altitudeFt).toBe(500)
  expect(offshore.faults.map(fault => [fault.at, fault.fault])).toEqual([[120, 'GPS INTEGRITY'], [300, 'GPS POSITION'], [600, 'DVS'], [900, 'GPS RESTORED']])
  expect(coastal.faults.map(fault => [fault.at, fault.fault])).toEqual([[120, 'DME BIAS'], [300, 'DME STATIONS'], [480, 'DME DESELECT'], [600, 'NAV CONTROL'], [780, 'AUTO VOR'], [960, 'TACAN']])
  // Every fault time is inside its segment, and in order.
  for (const segment of manifest.segments)
    expect(segment.faults.map(fault => fault.at)).toEqual([...segment.faults.map(fault => fault.at)].sort((a, b) => a - b))
  expect(manifest.ndbVariants.map(variant => [variant.id, variant.faults.map(fault => `${fault.fault} ${fault.at} s after the ${fault.after}`)]))
    .toEqual([['kiag-n28', ['GPS INTEGRITY 0 s after the FAF']], ['pasd-q32', ['NDB OFF AIR 60 s after the FAF']]])
})

test('the synthetic stations are flagged and declared, with a terrain elevation, and absent from every CIFP lookup', () => {
  const data = cifp(), db = new NavDatabase(data)
  // Independent values: the Annex 10 pairing of each declared frequency, and the Terrarium ground read under each site.
  const declared = { QWHM: ['VORDME', '108.65', '23Y', 54], QFIS: ['DME', '109.25', '29Y', 6], QORP: ['DME', '112.05', '57Y', 14], QMTK: ['DME', '111.85', '55Y', 41], QBIX: ['TACAN', '115.70', '104X', 107] }
  expect(SYNTHETIC_COASTAL_STATIONS.map(station => station.ident)).toEqual(Object.keys(declared))
  for (const station of SYNTHETIC_COASTAL_STATIONS) {
    const [type, frequency, channel, feet] = declared[station.ident as keyof typeof declared]
    expect(station).toMatchObject({ type, frequency, channel, synthetic: { declaredFor: 'STAGE F MISSION' } })
    expect(station.name).toMatch(/SYNTHETIC$/)
    expect(station.elevation).toMatchObject({ feet, source: 'terrain' })
    expect(station.elevation.provenance.startsWith(SYNTHETIC_PROVENANCE)).toBe(true)
    expect(station.elevation.provenance).toMatch(/not an antenna/)
    expect(Object.isFrozen(station)).toBe(true)
    // Never in the real data: no CIFP entry of any kind has the ident, and no CIFP station uses the frequency.
    expect(db.find(station.ident)).toEqual([])
    expect(data.entries.filter(entry => entry.kind === 'navaid' && entry.frequency === station.frequency)).toEqual([])
  }
  // Nor in the mission's NDB fixture files.
  for (const variant of stageFMissionManifest().ndbVariants) {
    const fixture = new NavDatabase(parseArinc424(readFileSync(variant.file, 'latin1'), { airports: [variant.airport] }).data)
    for (const station of SYNTHETIC_COASTAL_STATIONS) expect(fixture.find(station.ident), `${station.ident} in ${variant.file}`).toEqual([])
  }
  // The real stations the coastal segment uses are the CIFP's own, unchanged.
  for (const ident of REAL_COASTAL_STATIONS) expect(db.find(ident).some(entry => entry.kind === 'navaid')).toBe(true)
  expect(db.find('HTO').find(entry => entry.kind === 'navaid')).toMatchObject({ type: 'VORTAC', elevation: { feet: 22, source: 'data' } })
  // The manifest labels them synthetic in its own words.
  const manifest = stageFMissionManifest()
  expect(manifest.facilities.real).toEqual(['HTO', 'CCC'])
  expect(manifest.facilities.synthetic.map(station => station.ident)).toEqual(Object.keys(declared))
  for (const station of manifest.facilities.synthetic) expect(station.elevation.provenance.startsWith(SYNTHETIC_PROVENANCE)).toBe(true)
})

test('from the coastal start the seven stations are in DME range and cross at usable angles: geometry for F6 and F7', () => {
  const db = new NavDatabase(cifp())
  const [, coastal] = stageFMissionManifest().segments
  const at = coastal.initialState.position
  const positions = coastal.facilities.map(ident => {
    const synthetic = SYNTHETIC_COASTAL_STATIONS.find(station => station.ident === ident)
    const real = db.find(ident).find(entry => entry.kind === 'navaid')
    return (synthetic ?? real)!.position
  })
  expect(positions).toHaveLength(7)
  // All within 60 NM (line of sight at 1,500 ft is about 48 NM to a station at sea level, more to a raised one: the
  // nearer six give F6's scan).
  const ranges = positions.map(position => distanceNm(at, position)).sort((a, b) => a - b)
  expect(ranges[5]).toBeLessThan(48)
  expect(ranges[6]).toBeLessThan(60)
  // Offshore, every station is on the land side, so the bearings cannot surround the aircraft. They still spread over
  // more than 120 degrees (the largest gap, the open sea, under 240), and some pair crosses within 30 degrees of a right
  // angle: DME/DME has a well-conditioned fix, not only near-parallel lines of position.
  const bearings = positions.map(position => courseDeg(at, position)).sort((a, b) => a - b)
  const gaps = bearings.map((bearing, i) => ((i + 1 < bearings.length ? bearings[i + 1] : bearings[0] + 360) - bearing))
  expect(Math.max(...gaps)).toBeLessThan(240)
  const crossing = (a: number, b: number) => { const d = Math.abs(a - b) % 180; return Math.min(d, 180 - d) }
  const best = Math.max(...bearings.flatMap((a, i) => bearings.slice(i + 1).map(b => crossing(a, b))))
  expect(best).toBeGreaterThan(60)
})

test('the expected results are slots, each naming the item that will compute it: a slot is never a pass', () => {
  const manifest = stageFMissionManifest()
  for (const segment of [...manifest.segments, ...manifest.ndbVariants]) {
    expect(segment.expected.length).toBeGreaterThan(0)
    for (const expected of segment.expected) {
      expect(expected).toMatchObject({ value: 'TODO', tolerance: 'TODO' })
      expect(expected.item).toMatch(/^F\d+$/)
    }
    // Each fault's item has a slot that checks it.
    for (const fault of segment.faults) expect(segment.expected.some(expected => expected.item === fault.item)).toBe(true)
  }
})
