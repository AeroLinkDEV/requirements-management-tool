import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { bearingDeg as bearingTo, distanceNm, offset, type LatLon } from '../src/fmsCdu/fmsModel'
import { navaidComponent, NavDatabase, type Navaid, type ProcedureLeg } from '../src/fmsCdu/navData'
import { vhfFrequency } from '../src/fmsCdu/navPages'
import { HELICOPTER_PROFILE, LATER_SBAS_PROFILE } from '../src/fmsCdu/profile'
import { BenchRadioReceiver } from '../src/fmsCdu/radioNavigation'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Stage F16 (NDB approaches; DEC-150, M300 7-1): the NDB/DME fixture. The data is Sand Point, Alaska (PASD) from the FAA
// CIFP, cycle 2609 (ARINC 424-18, a US Government work in the public domain): its airport, runway, terminal waypoint and
// procedure records, and the enroute fixes and navaids its procedures use, extracted unchanged from FAACIFP18
// (CIFP_260903.zip; the file's SHA-256 is FBEA2179...6CAD). PASD Q32 is route type Q, an NDB/DME approach.
//
// Chart reconciliation, recorded for the fixture (Astra's check against dTPP 2609 06537N32.PDF, SHA-256 BF794EBB...FF3C,
// in her Stage F plan review): the title is NDB RWY 32 with DME required, so the Q coding is the NDB/DME fixture and the
// title is not an overlay; HBT NDB 390 kHz, DME channel 79 (113.2); HBT 0.4 at the MAP; the missed approach climbs to
// 1,800, then a climbing right turn to 4,300 direct HBT, and holds. The bench flies the database procedure on FMS
// guidance, and its assumptions are that the NDB is operating and the ADF raw data is monitored (AIM 1-2-3(c)).
const FIXTURE = readFileSync('tests/fixtures/cifp/pasd-2609.pc', 'latin1')
const RECORDS = FIXTURE.split('\n')
const loaded = () => parseArinc424(FIXTURE, { airports: ['PASD'] })
const q32 = () => loaded().data.procedures.find(p => p.airport === 'PASD' && p.ident === 'Q32')!

// Independent of the reader: positions from the records' text by hand (degrees, minutes, seconds and hundredths).
const dms = (deg: number, min: number, sec: number, negative: boolean) => (negative ? -1 : 1) * (deg + min / 60 + sec / 3600)
// SCANDB HBT: N55185640 W160310622, 390.0 kHz. SCAND HBT: DME only, N55185695 W160311019, 113.20 MHz, elevation 130 ft.
const HBT_NDB: LatLon = { lat: dms(55, 18, 56.4, false), lon: dms(160, 31, 6.22, true) }
const HBT_DME: LatLon = { lat: dms(55, 18, 56.95, false), lon: dms(160, 31, 10.19, true) }
// PASD's magnetic variation, E0110 on its airport record: 11.0 degrees east, added to a magnetic course to make it true.
const VARIATION = 11
const close = (a: LatLon | undefined, b: LatLon) => expect(a && distanceNm(a, b)).toBeLessThan(1e-6)

/** The Q32 records of one transition (blank for the final), as the file has them: columns 27-29, 30-34, 37-38, 48-49, 67-70, 71-74. */
const records = (transition: string) => RECORDS.filter(line => line.startsWith('SCANP PASDPAFQ32 ') && line.slice(20, 25).trim() === transition)
  .map(line => ({ sequence: line.slice(26, 29), fix: line.slice(29, 34).trim(), section: line.slice(36, 38), path: line.slice(47, 49), rho: line.slice(66, 70), course: line.slice(70, 74) }))
const identLegs = (legs: ProcedureLeg[]) => legs.filter((leg): leg is Extract<ProcedureLeg, { ident: string }> => 'ident' in leg)

test('F16: HBT is two records with one ident: the NDB the ADF tunes and the DME its ranges come from, each at its own position', () => {
  const { data, errors, invalid } = loaded()
  expect(invalid).toEqual([])
  const hbt = data.entries.filter(e => e.ident === 'HBT')
  expect(hbt.map(e => e.kind === 'navaid' && e.type).sort()).toEqual(['DME', 'NDB'])
  const ndb = navaidComponent(hbt, 'NDB')!, dme = navaidComponent(hbt, 'VHF')!
  expect(ndb).toMatchObject({ type: 'NDB', frequency: '390', name: 'BORLAND' })
  close(ndb.position, HBT_NDB)
  // The DME's channel is Annex 10's pairing for 113.20 MHz (112.30 to 117.95 are 70 to 126; 113.20 is 70 + 9 = 79X).
  expect(dme).toMatchObject({ type: 'DME', frequency: '113.20', channel: '79X', elevation: { feet: 130, source: 'data' } })
  close(dme.position, HBT_DME)
  // They are about 70 m apart: a consumer that takes the wrong one is wrong by that much, not by nothing.
  expect(distanceNm(HBT_NDB, HBT_DME)).toBeGreaterThan(0.03)
  expect(distanceNm(HBT_NDB, HBT_DME)).toBeLessThan(0.05)
  // PASD's two RNAV approaches with a non-runway MAP are F16's conventional non-runway MAP work, not this fixture's.
  expect(errors).toEqual(['PASD R14-Y: missed approach point is not a runway', 'PASD R14-Z: missed approach point is not a runway'])
})

test('F16: each consumer of HBT gets its own component, whichever record the database holds first', () => {
  const entries = loaded().data.entries.filter(e => e.ident === 'HBT')
  // Both orders: the file has the DME first; a database that held the NDB first must not change any answer.
  for (const order of [entries, [...entries].reverse()]) {
    expect(navaidComponent(order, 'NDB')?.frequency).toBe('390')
    expect(navaidComponent(order, 'VHF')?.frequency).toBe('113.20')
    // NAV STATUS shows a tuned DME's frequency: the VHF record's, never the NDB's 390.
    expect(vhfFrequency(order)).toBe('113.20')
  }
  // The DME range is measured from the DME. Over the DME antenna, level with it, the slant range is the receiver's
  // declared range bias alone; measured from the NDB it would be 0.04 NM more.
  const dme = navaidComponent(entries, 'VHF')!, receiver = new BenchRadioReceiver()
  receiver.tune([dme], 0)
  receiver.sample(HBT_DME, 130, 0)
  const [observation] = receiver.sample(HBT_DME, 130, 10_000)
  const bias = HELICOPTER_PROFILE.parameters.radioRangeBias.value
  expect(Math.abs(Math.abs(observation.slantRangeNm.value!) - bias)).toBeLessThan(1e-9)
})

test('F16: the FMS ranges on the HBT DME, never the NDB, and flies the procedure fix HBT to the coded NDB', () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now), {})
  expect(unit.loadArinc424(FIXTURE, 'pasd-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  // The automatic radio stations near Sand Point: HBT's DME, never its NDB.
  const internals = unit as unknown as { here: LatLon; autoRadioStations(): Navaid[] }
  internals.here = { ...HBT_NDB }
  const stations = internals.autoRadioStations().filter(s => s.ident === 'HBT')
  expect(stations.map(s => s.type)).toEqual(['DME'])
  close(stations[0].position, HBT_DME)
  // PASD Q32 via CUBPA in the active route: its HBT legs fly to the NDB the records code (section DB), not the DME.
  // The aircraft is 1 NM west of the station, nearer the DME (west of the NDB), when the route is built and flown: the
  // nearest HBT record is the DME, so only the coded fix gives the NDB.
  internals.here = offset(HBT_DME, 270, 1)
  expect(distanceNm(internals.here, HBT_DME)).toBeLessThan(distanceNm(internals.here, HBT_NDB))
  unit.press('RTE')
  for (const ch of 'PASD') unit.press(`CHAR_${ch}` as CduFunction)
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'Q32', 'CUBPA')
  unit.press('EXEC')
  now += 1000
  expect(unit.route.legs.some(leg => leg.kind === 'wpt' && leg.ident === 'HBT')).toBe(true)
  close(unit.coordinates('HBT'), HBT_NDB)
  const leg = unit.route.legs.find(l => l.kind === 'wpt' && l.ident === 'HBT')
  close(leg?.kind === 'wpt' ? leg.position : undefined, HBT_NDB)
})

test('F16: PASD Q32 loads as NDB D with its PI reversal, recommended NDB/DME HBT and the 0.4 NM DME distance at the MAP, every leg matching its record', () => {
  const approach = q32()
  expect(approach).toMatchObject({ kind: 'APPROACH', approachType: 'NDB', dmeRequired: true, runways: ['RW32'], faf: 'JOTOK' })
  // The recommended navaid on the final is the NDB HBT (section DB), with the DME of its ident for the coded distances.
  expect(approach.recommendedNavaid).toMatchObject({ ident: 'HBT', type: 'NDB', frequency: '390', dme: { channel: '79X' } })
  close(approach.recommendedNavaid!.position, HBT_NDB)
  close(approach.recommendedNavaid!.dme!.position, HBT_DME)

  // The final, IF WONBA to the MAP RW32, leg by leg against the records (route type Q, sequences 010 to 030).
  const final = records('').filter(r => r.sequence <= '030')
  expect(final.map(r => `${r.path} ${r.fix}`)).toEqual(['IF WONBA', 'CF JOTOK', 'CF OTIPE', 'CF RW32'])
  const legs = identLegs(approach.legs)
  expect(legs.map(l => l.ident)).toEqual(final.map(r => r.fix))
  for (const [i, record] of final.entries()) if (record.path === 'CF') {
    expect(legs[i].path, record.fix).toBe('CF')
    expect(legs[i].course, record.fix).toBeCloseTo(Number(record.course) / 10 + VARIATION, 9)
  }
  expect(legs.map(l => l.altitude)).toEqual(['2900A', '2300A', '1620A', '59'])
  // The MAP is 0.4 NM from HBT (rho 0004 on the RW32 record); no other final leg codes a distance.
  expect(final.map(r => r.rho.trim())).toEqual(['', '', '', '0004'])
  expect(legs.map(l => l.navaidDistance ?? null)).toEqual([null, null, null, { ident: 'HBT', nm: 0.4 }])
  expect(legs.at(-1)!.overfly).toBe(true)

  // The missed approach: CA on 339.1 magnetic to 1,800, DF HBT at 4,300 and the HM at HBT (inbound 344.0, right turns,
  // 1.0 minute), at the NDB the records code.
  const missed = records('').filter(r => r.sequence > '030')
  expect(missed.map(r => `${r.path} ${r.fix}`)).toEqual(['CA ', 'DF HBT', 'HM HBT'])
  expect(missed.slice(1).map(r => r.section)).toEqual(['DB', 'DB'])
  expect(approach.missed).toHaveLength(2)
  expect(approach.missed![0]).toMatchObject({ path: 'CA', altitude: 1800 })
  expect(approach.missed![0].course).toBeCloseTo(Number(missed[0].course) / 10 + VARIATION, 9)
  const hbt = approach.missed![1] as Extract<ProcedureLeg, { ident: string }>
  expect(hbt).toMatchObject({ ident: 'HBT', path: 'DF', altitude: '4300A', turnDirection: 'RIGHT', hold: { path: 'HM', turn: 'RIGHT', legTimeMin: 1, altitude: '4300A' } })
  expect(hbt.hold!.inbound).toBeCloseTo(Number(RECORDS.find(l => l.startsWith('SCANP PASDPAFQ32   Q      060'))!.slice(70, 74)) / 10 + VARIATION, 9)
  close(hbt.position, HBT_NDB)
  expect(approach.missedHold).toMatchObject({ fix: 'HBT', inbound: 355, turn: 'RIGHT', altitude: '4300A' })

  // The four transitions: IF, TF HBT (the NDB), TF JOTOK, the PI at JOTOK and the CF inbound to WONBA.
  expect(Object.keys(approach.transitions).sort()).toEqual(['CUBPA', 'DUGAC', 'RAYMD', 'SAFKO'])
  for (const [name, transitionLegs] of Object.entries(approach.transitions)) {
    const coded = records(name)
    expect(coded.map(r => `${r.path} ${r.fix}`), name).toEqual([`IF ${name}`, 'TF HBT', 'TF JOTOK', 'PI JOTOK', 'CF WONBA'])
    expect(coded[1].section, name).toBe('DB')
    const flown = identLegs(transitionLegs)
    expect(flown.map(l => l.ident), name).toEqual([name, 'HBT', 'JOTOK', 'JOTPTR', 'JOTPTL', 'WONBA'])
    close(flown[1].position, HBT_NDB)
    // The PI: JOTOK the reference, two generated outbound legs, then the coded CF inbound (339.1 magnetic).
    expect(flown.map(l => l.procedureTurn?.role ?? null), name).toEqual([null, null, 'REFERENCE', 'OUTBOUND', 'OUTBOUND', 'INBOUND'])
    expect(flown[5].course, name).toBeCloseTo(Number(coded[4].course) / 10 + VARIATION, 9)
    // JOTOK is a terminal waypoint with one record: nothing to tell apart, so it carries no pinned position.
    expect(flown[2].position, name).toBeUndefined()
  }
})

test('F16: an NDB/DME approach whose recommended navaid has no DME in the data is refused, with the reason', () => {
  // The same file without HBT's DME record: the Q approach cannot measure its coded distances.
  const withoutDme = RECORDS.filter(line => !line.startsWith('SCAND        HBT ')).join('\n')
  const { data, errors } = parseArinc424(withoutDme, { airports: ['PASD'] })
  expect(data.procedures.find(p => p.ident === 'Q32')).toBeUndefined()
  expect(errors).toContain('PASD Q32: NDB/DME approach without the DME of its recommended navaid HBT')
  expect(data.entries.filter(e => e.ident === 'HBT').map(e => e.kind === 'navaid' && e.type)).toEqual(['NDB'])
})

test('F16: a coded fix carries a position only when its ident names more than one record and its section picks one', () => {
  const db = new NavDatabase(loaded().data)
  expect(db.find('HBT')).toHaveLength(2)
  const approach = q32()
  const all = [...identLegs(approach.legs), ...identLegs(approach.missed ?? []), ...Object.values(approach.transitions).flatMap(identLegs)]
  // Generated PI waypoints carry their own positions; every other leg with one is an HBT leg, at the NDB.
  const pinned = all.filter(l => l.position && l.procedureTurn?.role !== 'OUTBOUND')
  expect(new Set(pinned.map(l => l.ident))).toEqual(new Set(['HBT']))
  for (const leg of pinned) close(leg.position, HBT_NDB)
})

test('F16: a terminal fix that shares its ident with a navaid flies to the fix, and a distance coded on any fix leg is kept', () => {
  // Laboratory variations of the file's own records, independent of the decoder. The terminal waypoint JOTOK is renamed
  // JOTO (an NDB ident has four columns, 14-17), and an NDB JOTO is added about 5 NM east of it: the procedures code
  // the fix with section PC, so every JOTO leg, the PI reference included, is the terminal waypoint. And the final's IF
  // WONBA (recommended navaid HBT) is given rho 0080: 8.0 NM from HBT.
  const set = (line: string, column: number, text: string) => line.slice(0, column - 1) + text + line.slice(column - 1 + text.length)
  const ndb = RECORDS.find(line => line.startsWith('SCANDB       HBT '))!
  const ifWonba = RECORDS.find(line => line.startsWith('SCANP PASDPAFQ32   Q      010'))!
  // CUBPA also loses its TF JOTOK, so its PI reference is a leg of its own rather than the TF it follows.
  const tfJotok = RECORDS.find(line => line.startsWith('SCANP PASDPAFQ32   ACUBPA 030'))!
  const lines = [...RECORDS.filter(line => line !== tfJotok).map(line => line === ifWonba ? set(line, 67, '0080') : line),
    set(set(ndb, 14, 'JOTO'), 33, 'N55122402W160203000')]
  const renamed = lines.join('\n').replaceAll('JOTOK', 'JOTO ')
  const { data } = parseArinc424(renamed, { airports: ['PASD'] })
  const approach = data.procedures.find(p => p.ident === 'Q32')!
  expect(data.entries.filter(e => e.ident === 'JOTO').map(e => e.kind).sort()).toEqual(['fix', 'navaid'])
  const terminal = data.entries.find(e => e.ident === 'JOTO' && e.kind === 'fix')!.position
  const legs = [...identLegs(approach.legs), ...Object.values(approach.transitions).flatMap(identLegs)].filter(l => l.ident === 'JOTO')
  expect(legs.filter(l => l.procedureTurn?.role === 'REFERENCE')).toHaveLength(4)
  expect(identLegs(approach.transitions.CUBPA).map(l => l.ident).slice(0, 3)).toEqual(['CUBPA', 'HBT', 'JOTO'])
  for (const leg of legs) close(leg.position, terminal)
  expect(identLegs(approach.legs)[0]).toMatchObject({ ident: 'WONBA', navaidDistance: { ident: 'HBT', nm: 8 } })
})

// The two conventional NDB fixtures, from the same CIFP 2609 file. KIAG N28 (Niagara Falls): NDB runway 28 with the MAP
// RW28, the NDB IA its FAF and recommended navaid, an HF course reversal on the EHMAN transition, and the missed
// approach CA, DF and HM at IA. KBKT NDB-A (Blackstone): a circling approach whose MAP is the fix CFBNK, the NDB BKT its
// FAF and recommended navaid. Magnetic variation from each airport record: KIAG W0100 and KBKT W0090.
const KIAG = readFileSync('tests/fixtures/cifp/kiag-2609.pc', 'latin1')
const KBKT = readFileSync('tests/fixtures/cifp/kbkt-2609.pc', 'latin1')
const legRecord = (text: string, prefix: string) => {
  const line = text.split('\n').find(l => l.startsWith(prefix))!
  return { course: Number(line.slice(70, 74)) / 10, time: line.slice(74, 78) }
}

test('F16: KIAG N28 loads with its prefix, legs, HF reversal and missed approach', () => {
  const { data } = parseArinc424(KIAG, { airports: ['KIAG'] })
  const n28 = data.procedures.find(p => p.ident === 'N28')!
  const variation = -10
  expect(n28).toMatchObject({ approachType: 'NDB', runways: ['RW28'], faf: 'IA', endpoint: { identification: { basis: 'RUNWAY' }, vertical: { kind: 'VPA', angleDeg: 3.1 } } })
  expect(n28.dmeRequired).toBeUndefined()
  // SUSADB IA: N43063280 W078501788, 329 kHz. An NDB approach has no DME of its navaid.
  expect(n28.recommendedNavaid).toMatchObject({ ident: 'IA', type: 'NDB', frequency: '329' })
  expect(n28.recommendedNavaid!.dme).toBeUndefined()
  close(n28.recommendedNavaid!.position, { lat: dms(43, 6, 32.8, false), lon: dms(78, 50, 17.88, true) })
  // The final: IF IA at 2,000, then CF RW28 on 280.4 magnetic at 643, flown over.
  const final = identLegs(n28.legs)
  expect(final.map(l => [l.ident, l.altitude])).toEqual([['IA', '2000A'], ['RW28', '643']])
  expect(final[1].course).toBeCloseTo(legRecord(KIAG, 'SUSAP KIAGK6FN28   N      030').course + variation, 9)
  // EHMAN: IF EHMAN, TF IA at 2,300 with the HF reversal (inbound 280.1, right turns, 1.0 minute, at 2,200), then CF
  // IA inbound at 2,000.
  const ehman = identLegs(n28.transitions.EHMAN)
  expect(ehman.map(l => l.ident)).toEqual(['EHMAN', 'IA', 'IA'])
  const hf = legRecord(KIAG, 'SUSAP KIAGK6FN28   AEHMAN 030')
  expect(hf.time).toBe('T010')
  expect(ehman[1]).toMatchObject({ path: 'TF', altitude: '2300A', hold: { path: 'HF', turn: 'RIGHT', legTimeMin: 1, exit: 'ONCE', altitude: '2200A' } })
  expect(ehman[1].hold!.inbound).toBeCloseTo(hf.course + variation, 9)
  expect(ehman[2]).toMatchObject({ path: 'CF', altitude: '2000A' })
  expect(ehman[2].course).toBeCloseTo(legRecord(KIAG, 'SUSAP KIAGK6FN28   AEHMAN 040').course + variation, 9)
  // The missed approach: CA on 294.0 magnetic to 1,600, then DF IA at 3,200 and the HM there.
  expect(n28.missed![0]).toMatchObject({ path: 'CA', altitude: 1600 })
  expect(n28.missed![0].course).toBeCloseTo(legRecord(KIAG, 'SUSAP KIAGK6FN28   N      040').course + variation, 9)
  expect(n28.missed![1]).toMatchObject({ ident: 'IA', path: 'DF', altitude: '3200A', hold: { path: 'HM', turn: 'RIGHT', legTimeMin: 1 } })
})

test('F16: KBKT NDB-A loads with a conventional non-runway MAP, not as PinS, and is flown to the MAP and into the missed approach', () => {
  // Its MELIA and NUTTS transitions end in a PI at BKT that the final's IF follows, with no CF: the decoder refuses that
  // reversal today (as it does every PI coded so in the CIFP), so the transitions are left out here and the refusal is
  // pinned below. The final and missed approach are the fixture.
  expect(parseArinc424(KBKT, { airports: ['KBKT'] }).errors).toEqual(['KBKT NDB-A: transition MELIA: PI at BKT ends the transition, and the final does not begin IF, then a CF to BKT'])
  const finalOnly = KBKT.split('\n').filter(line => !/^SUSAP KBKTK6FNDB-A A/.test(line)).join('\n')
  const { data, errors } = parseArinc424(finalOnly, { airports: ['KBKT'] })
  expect(errors).toEqual([])
  const ndbA = data.procedures.find(p => p.ident === 'NDB-A')!
  expect(ndbA).toMatchObject({ approachType: 'NDB', runways: [], faf: 'BKT', recommendedNavaid: { ident: 'BKT', type: 'NDB', frequency: '326' } })
  expect(ndbA.pointInSpace).toBeUndefined()
  expect(ndbA.endpoint).toMatchObject({
    instrumentEnd: { fix: 'CFBNK', altitude: '437' }, landingSite: { kind: 'AIRPORT', ident: 'KBKT' },
    visualSegment: { kind: 'UNKNOWN', validated: false }, identification: { basis: 'CONVENTIONAL NON-RUNWAY MAP' },
  })
  const final = identLegs(ndbA.legs)
  expect(final.map(l => l.ident)).toEqual(['BKT', 'CFBNK'])
  expect(final[1].course).toBeCloseTo(legRecord(KBKT, 'SUSAP KBKTK6FNDB-A N      030').course - 9, 9)
  expect(ndbA.missed!.map(l => ('ident' in l ? `${l.path} ${l.ident}` : l.path))).toEqual(['CA', 'DF BKT'])

  // Flown: the instrument end is the MAP CFBNK, there is no runway and no point-in-space continuation, and the
  // missed approach follows the MAP in the route.
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now), {})
  expect(unit.loadArinc424(finalOnly, 'kbkt-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.press('RTE')
  for (const ch of 'KBKT') unit.press(`CHAR_${ch}` as CduFunction)
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'NDB-A')
  unit.press('EXEC')
  now += 1000
  expect(unit.instrumentEnd).toBe('CFBNK')
  expect(unit.finalRunway).toBeNull()
  expect(unit.pinsContinuation).toBeNull()
  const route = unit.route.legs.flatMap(leg => (leg.kind === 'wpt' ? [`${leg.ident}/${leg.source ?? ''}`] : []))
  const map = route.indexOf('CFBNK/APPR')
  expect(map).toBeGreaterThan(0)
  expect(route.slice(map + 1)).toContain('BKT/MISSED')
  // ARRIVALS lists it by its ident, NDB-A: it serves no runway to name it by.
  unit.press('DEP_ARR')
  unit.press('LSK1R')
  expect(screenText(unit.screen()).join('\n')).toContain('NDB-A')

  // An RNAV approach whose MAP is not a runway stays refused (PASD R14-Y and R14-Z, in the first test).
})

// F16 guidance and authority (plan DF-01; M300 7-1, 7-12, C.3.1). The S300 approach authority covers every non-ILS
// approach, NDB included: an NDB approach flown on FMS guidance with GPS has the approach phase and RNP 0.3, and an
// integrity-only loss after the FAF is continued for 300 s and then cancelled, as on an RNAV final. KIAG N28: the FAF
// is the NDB IA, the MAP RW28 on 270.4 true.
const ndbFinal = () => {
  let now = Date.UTC(2026, 8, 29, 14)
  const unit = new ScriptedFms(() => new Date(now))
  expect(unit.loadArinc424(KIAG, 'kiag-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.modify(route => { route.dest = 'KIAG'; route.legs = [] }); unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'N28'); unit.press('EXEC')
  unit.directTo('IA'); unit.press('EXEC')
  unit.placeAircraft({ position: offset(unit.coordinates('IA')!, 90, 1), altitude: 2000, track: 270 }, 'KIAG N28 one mile before IA')
  unit.armApproach(); unit.updateNavigation(0)
  const receivers = (unit as unknown as { gps: readonly { override(label: string, value: unknown): void }[] }).gps
  const lose = (hdop: number) => {
    for (const rx of receivers) { rx.override('130', { kind: 'FORCE', value: 1, ssm: 'NORMAL' }); rx.override('101', { kind: 'FORCE', value: hdop, ssm: 'NORMAL' }) }
    unit.updateNavigation(0)
  }
  return { unit, lose, advance: (seconds: number) => { now += seconds * 1000; unit.updateNavigation(0) } }
}
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)

test('F16: an NDB approach flown on FMS guidance with GPS has the approach phase, RNP 0.3 and its CDI full scale', () => {
  const { unit } = ndbFinal()
  expect(unit.flightPhase).toBe('APPROACH')
  expect(unit.nonPrecisionApproach).toBe(true)
  expect(unit.requiredRnp).toBe(0.3)
  expect(unit.approachType).toBe('LNAV')
})

test('F16: GPS integrity lost after the FAF on an NDB final: guidance continues for 300 s, then NO APPR INTEGRITY and NAV withdrawn; MISSED APPR restores terminal guidance', () => {
  const { unit, lose, advance } = ndbFinal()
  unit.arrive(); unit.updateNavigation(0)
  expect(unit.onFinalSegment).toBe(true)
  lose(1)
  advance(299)
  expect(unit.nonPrecisionApproach).toBe(true)
  expect(unit.approachSteeringValid).toBe(true)
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(false)
  advance(1)
  // Cancelled (M300 7-12; C.3.1): the alert, the approach phase ended, and no valid approach steering, so the AFCS
  // reverts to HDG and NAV is withdrawn.
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
  expect(unit.nonPrecisionApproach).toBe(false)
  expect(unit.approachType).toBe('NO APPR')
  expect(unit.approachSteeringValid).toBe(false)
  // The crew's MISSED APPR restores terminal guidance on the missed approach; nothing restores approach permission.
  expect(unit.requestMissedApproach()).toBe(true)
  expect(unit.approachSteeringValid).toBe(true)
  expect(unit.flightPhase).toBe('TERMINAL')
})

test('F16: an invalid GPS position on the NDB final cancels at once', () => {
  const { unit, lose } = ndbFinal()
  unit.arrive(); unit.updateNavigation(0)
  // HDOP above 4 is not an integrity-only loss: no 300 s continuation (M300 7-12).
  lose(4.01)
  expect(unit.nonPrecisionApproach).toBe(false)
  expect(unit.approachSteeringValid).toBe(false)
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
})

test('F16: outside the S300 the NDB approach keeps the existing RNAV-only approach phase (DF-01 decides the S300 only)', () => {
  // The later SBAS profile's approach authority for NDB approaches is not decided by the plan: it keeps the behaviour it
  // had, with no approach phase on an NDB final. A deliberate boundary, open for a decision.
  let now = Date.UTC(2026, 8, 29, 14)
  const unit = new ScriptedFms(() => new Date(now), { profile: LATER_SBAS_PROFILE })
  unit.loadArinc424(KIAG, 'kiag-2609.pc'); unit.swapCycles()
  unit.modify(route => { route.dest = 'KIAG'; route.legs = [] }); unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'N28'); unit.press('EXEC')
  unit.directTo('IA'); unit.press('EXEC')
  unit.placeAircraft({ position: offset(unit.coordinates('IA')!, 90, 1), altitude: 2000, track: 270 }, 'KIAG N28 one mile before IA')
  unit.armApproach(); unit.updateNavigation(0)
  unit.arrive(); now += 1000; unit.updateNavigation(0)
  expect(unit.nonPrecisionApproach).toBe(false)
})

// F16 raw data (plan C3; M300 13-23, 13-24): the ADF is the crew's. Loading an NDB approach requests its recommended
// NDB in ADF1's standby for the crew to swap in on the ADF page; the bearing is raw data only, never a position source.
const adfLine = (unit: ScriptedFms) => screenText(unit.screen()).join('\n')
const relativeTo = (unit: ScriptedFms, target: LatLon) => {
  const d = (bearingTo(unit.truePosition, target) - unit.heading + 360) % 360
  return d > 180 ? d - 360 : d
}

test('F16: loading the NDB approach requests the recommended NDB on the ADF; the crew swaps it in on the ADF page', () => {
  const { unit, advance } = ndbFinal()
  // KIAG N28's recommended navaid is the NDB IA, 329 kHz: requested in ADF1's standby, the active frequency untouched.
  expect(unit.radioState.adfStby).toBe('0329')
  expect(unit.radioState.adf).toBe('0350')
  expect(unit.adfRelativeBearing('adf')).toBeNull()
  unit.open('ADF_RADIO')
  expect(adfLine(unit)).toContain('STBY 0329')
  // LSK 1L with the scratchpad empty swaps standby and active (M300 13-23); the radio acknowledges, then IA is received.
  unit.press('LSK1L')
  advance(1)
  expect(unit.radioState.adf).toBe('0329')
  expect(unit.radioState.adfStby).toBe('0350')
  const bearing = unit.adfRelativeBearing('adf')
  expect(bearing).not.toBeNull()
  expect(Math.abs(bearing! - relativeTo(unit, unit.coordinates('IA')!))).toBeLessThan(1e-6)
})

test('F16: a frequency entered on the ADF page goes to standby, not active (M300 13-23)', () => {
  const { unit } = ndbFinal()
  unit.open('ADF_RADIO')
  for (const ch of '400') unit.press(`CHAR_${ch}` as CduFunction)
  unit.press('LSK1L')
  expect(unit.radioState.adfStby).toBe('0400')
  expect(unit.radioState.adf).toBe('0350')
})

test('F16: the NDB off the air flags the bearing, with no fault alert or advisory; back on the air it returns', () => {
  const { unit, advance } = ndbFinal()
  unit.open('ADF_RADIO'); unit.press('LSK1L'); advance(1)
  expect(unit.adfRelativeBearing('adf')).not.toBeNull()
  unit.setNdbOffAir('IA', true)
  advance(1)
  expect(unit.adfRelativeBearing('adf')).toBeNull()
  // A healthy ADF with nothing to receive meets no Appendix E row (plan C3).
  expect(recalled(unit, 'ADF1 CONTROL LOST')).toBe(false)
  expect(unit.lastAdvisories).not.toContain('ADF1 FAILED')
  unit.setNdbOffAir('IA', false)
  advance(1)
  expect(unit.adfRelativeBearing('adf')).not.toBeNull()
})

test('F16: an ADF receiver failure flags the bearing and raises ADF CONTROL LOST (configured) and ADF FAILED', () => {
  const { unit, advance } = ndbFinal()
  unit.open('ADF_RADIO'); unit.press('LSK1L'); advance(1)
  expect(unit.adfRelativeBearing('adf')).not.toBeNull()
  unit.setRadioFaults('adf', { receiver: 'FAILED' })
  advance(1)
  expect(unit.adfRelativeBearing('adf')).toBeNull()
  expect(recalled(unit, 'ADF1 CONTROL LOST')).toBe(true)
  expect(unit.lastAdvisories).toContain('ADF1 FAILED')
  // Raw data only: the navigation solution does not change with the ADF.
  expect(unit.navState.mode).toBe('GPS')
})
