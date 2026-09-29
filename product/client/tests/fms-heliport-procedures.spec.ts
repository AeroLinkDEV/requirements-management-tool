import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import type { Airport, Fix } from '../src/fmsCdu/navData'
import { joinTransition } from '../src/fmsCdu/procedures'
import { screenText } from '../src/fmsCdu/screen'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import type { CduFunction } from '../src/fmsCdu/variants'

// Stage C of the helicopter-first plan: heliport navigation data and procedure endpoints. The fixture is the FAA CIFP
// cycle 2609 (FAACIFP18, SHA-256 fbea2179...c76cad; ARINC 424-18, a US Government work in the public domain): the five
// Copter point-in-space approaches, 87N R190 (Southampton heliport, the v1 fixture), KJRA R210 (West 30th St heliport),
// KJFK R027, KLGA R250 and 2P2 R029, with their heliport or airport reference records, terminal waypoints, the enroute
// fixes and navaids they use, and their MSA records, extracted unchanged. The 87N chart facts are from AL-9013 (FAA),
// COPTER RNAV (GPS) 190, Orig-B, in d-TPP 2609.
const FIXTURE = readFileSync('tests/fixtures/cifp/copter-pins-2609.pc', 'latin1')
const parsed = () => parseArinc424(FIXTURE)
const procedure = (airport: string, ident: string) => parsed().data.procedures.find(p => p.airport === airport && p.ident === ident)
const entry = (ident: string) => parsed().data.entries.find(e => e.ident === ident)
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const typeText = (unit: ScriptedFms, text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }

/** An FMS with the fixture loaded and active, the route to a heliport or airport and the approach through a transition. */
function flying(dest: string, approach: string, transition?: string) {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 29, 14, 0, 0)))
  expect(unit.loadArinc424(FIXTURE, 'copter-pins-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  // The loaded cycle keeps the data's minimum sector altitudes.
  expect(unit.navdb.msa).toContainEqual(expect.objectContaining({ airport: '87N', centre: 'CRANN' }))
  unit.press('RTE')
  typeText(unit, dest)
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', approach, transition)
  unit.press('EXEC')
  return unit
}

test('C.1: heliport reference, terminal waypoint and MSA records are read from the heliport section', () => {
  const result = parsed()
  expect(result.invalid).toEqual([])
  expect(result.errors).toEqual([])
  // HA 87N: N40 50 46.52 W072 27 59.00, variation W014.0, elevation 5 ft, SOUTHAMPTON.
  const heliport = entry('87N') as Airport
  expect(heliport).toMatchObject({ kind: 'airport', heliport: true, name: 'SOUTHAMPTON', elevation: 5, magneticVariation: -14, runways: [] })
  expect(heliport.position.lat).toBeCloseTo(40 + 50 / 60 + 46.52 / 3600, 9)
  expect(heliport.position.lon).toBeCloseTo(-(72 + 27 / 60 + 59 / 3600), 9)
  expect((entry('KJRA') as Airport).heliport).toBe(true)
  // An airport-section airport is not a heliport.
  expect((entry('KJFK') as Airport).heliport).toBeUndefined()
  // HC CRANN: N40 51 42.40 W072 27 55.11.
  const crann = entry('CRANN') as Fix
  expect(crann.kind).toBe('fix')
  expect(crann.position.lat).toBeCloseTo(40 + 51 / 60 + 42.4 / 3600, 9)
  expect(crann.position.lon).toBeCloseTo(-(72 + 27 / 60 + 55.11 / 3600), 9)
  for (const ident of ['STAYS', 'TIDUE', 'JORBA', 'ERORE']) expect(entry(ident)?.kind).toBe('fix')
  // HS: MSA CRANN 25 NM, 1900 ft, one sector all round (180 to 180), magnetic: the chart's "MSA CRANN 25 NM 1900".
  expect(result.data.msa).toContainEqual({ airport: '87N', centre: 'CRANN', magnetic: true, sectors: [{ from: 180, to: 180, altitude: 1900, radiusNm: 25 }] })
  expect(result.data.msa?.find(m => m.airport === 'KLGA')).toMatchObject({ centre: 'WITKN', sectors: [{ altitude: 2900, radiusNm: 25 }] })
})

test('C.2, C.4, C.9: all five Copter point-in-space approaches import with their endpoints and no runway', () => {
  const expected = [
    ['87N', 'R190', 'CRANN', '560', 'HELIPORT', 'STAYS'],
    ['KJRA', 'R210', 'JORBA', '780', 'HELIPORT', 'ERORE'],
    ['KJFK', 'R027', 'HELOG', '500', 'AIRPORT', 'WERIN'],
    ['KLGA', 'R250', 'WITKN', '520', 'AIRPORT', 'NEUMN'],
    ['2P2', 'R029', 'OBIBE', '1160', 'AIRPORT', 'JILIP'],
  ] as const
  for (const [airport, ident, map, mda, site, faf] of expected) {
    const approach = procedure(airport, ident)!
    expect(approach, `${airport} ${ident}`).toBeDefined()
    // No runway threshold is manufactured: the procedure serves none, and its final ends at the MAP at its MDA.
    expect(approach).toMatchObject({ kind: 'APPROACH', approachType: 'RNAV', pointInSpace: true, runways: [], faf })
    expect(approach.legs.at(-1)).toMatchObject({ ident: map, altitude: mda, verticalAngleDeg: 0 })
    expect(approach.endpoint).toMatchObject({
      instrumentEnd: { fix: map, altitude: mda },
      landingSite: { kind: site, ident: airport, airport },
      // C.4: every one codes vertical angle 000 at the MAP, so none has a vertical path; flown LNAV.
      vertical: { kind: 'NONE' },
    })
    // Only 87N's chart has been read: the others' visual segment is unknown and unvalidated, never assumed.
    if (airport !== '87N') {
      expect(approach.endpoint!.visualSegment).toMatchObject({ kind: 'UNKNOWN', validated: false })
      expect(approach.notes).toBeUndefined()
    }
  }
  expect(parsed().data.procedures).toHaveLength(5)
})

test('C.2: 87N proceeds VFR from CRANN to the heliport, 197 degrees magnetic 0.9 NM, as the chart shows', () => {
  const endpoint = procedure('87N', 'R190')!.endpoint!
  expect(endpoint.visualSegment).toMatchObject({ kind: 'PROCEED VFR', validated: true, source: 'FAA AL-9013 COPTER RNAV (GPS) 190, Orig-B, d-TPP 2609' })
  // Independent geometry from the raw records: the heliport (HA) and CRANN (HC) are 0.933 NM apart at 183.0 true; the
  // heliport record's 14.0 W variation makes that 197.0 magnetic, the chart's "197 (0.9) from MAP".
  const from = { lat: 40 + 51 / 60 + 42.4 / 3600, lon: -(72 + 27 / 60 + 55.11 / 3600) }
  const to = { lat: 40 + 50 / 60 + 46.52 / 3600, lon: -(72 + 27 / 60 + 59 / 3600) }
  expect(distanceNm(from, to)).toBeCloseTo(0.933, 3)
  expect(bearingDeg(from, to)).toBeCloseTo(183.0, 1)
  expect(endpoint.visualSegment.bearingMag!).toBeCloseTo(197.0, 1)
  expect(endpoint.visualSegment.distanceNm!).toBeCloseTo(0.933, 3)
  expect(Math.round(endpoint.visualSegment.distanceNm! * 10) / 10).toBe(0.9)
})

test('87N R190 imports record by record: final, speed limits, the missed approach and its 4 NM hold', () => {
  const r190 = procedure('87N', 'R190')!
  // TIDUE IF at or above 1700, speed 070 (at or below); STAYS FAF at or above 1700; CRANN MAP at 560, speed 070, VPA 000.
  expect(r190.legs).toEqual([
    { ident: 'TIDUE', altitude: '1700A', speedLimit: { kt: 70, descriptor: 'AT OR BELOW' } },
    { ident: 'STAYS', altitude: '1700A', path: 'TF' },
    { ident: 'CRANN', altitude: '560', path: 'TF', speedLimit: { kt: 70, descriptor: 'AT OR BELOW' }, verticalAngleDeg: 0 },
  ])
  // C.5a: the CA on 190.0 magnetic (176 true) to 439, below the MDA; then DF BEADS with its right turn, at or above 2000,
  // speed 070; the HM at BEADS inbound 236.0 magnetic (222 true), right turns, 4 NM legs (not time), speed 090.
  const hm = { path: 'HM', inbound: 222, turn: 'RIGHT', legDistanceNm: 4, legTimeMin: null, exit: 'MANUAL', altitude: '2000A', speedLimit: { kt: 90, descriptor: 'AT OR BELOW' } }
  expect(r190.missed).toEqual([
    { path: 'CA', course: 176, altitude: 439 },
    { ident: 'BEADS', altitude: '2000A', path: 'DF', turnDirection: 'RIGHT', speedLimit: { kt: 70, descriptor: 'AT OR BELOW' }, hold: hm },
  ])
  // C.6 / C16: the missed approach hold keeps its leg distance and speed.
  expect(r190.missedHold).toEqual({ fix: 'BEADS', inbound: 222, turn: 'RIGHT', altitude: '2000A', legDistanceNm: 4, speedLimit: { kt: 90, descriptor: 'AT OR BELOW' } })
  // C.10: the chart notes travel with the procedure, displayed and not enforced.
  expect(r190.notes).toEqual(expect.arrayContaining(['Procedure NA at night.', 'Use Westhampton Beach altimeter setting.',
    'Procedure NA for arrival on CCC VOR/DME airway radials 057 CW 105.', 'Procedure NA for arrivals at HTO VORTAC on V46 eastbound.',
    'RNP APCH.', 'LNAV MDA 560-1.', 'Limit final and missed approach to 70K.']))
})

test('a hold whose fix is not the leg before it becomes a leg of its own, never attached to another fix', () => {
  // The HTO transition without its TF TIDUE record: the HF at TIDUE follows the IF at HTO.
  const text = FIXTURE.split('\n').filter(line => !/^SUSAH 87N K6FR190  AHTO   020/.test(line)).join('\n')
  const hto = parseArinc424(text).data.procedures.find(p => p.ident === 'R190')!.transitions.HTO
  expect(hto).toEqual([{ ident: 'HTO' }, { ident: 'TIDUE', altitude: '1700A', hold: expect.objectContaining({ path: 'HF', exit: 'ONCE' }) }])
})

test('C.6, C.7: each transition keeps its HF course reversal at TIDUE, 4 NM legs, exit once', () => {
  const r190 = procedure('87N', 'R190')!
  // The HF at TIDUE: inbound 190.0 magnetic (176 true), left turns, 4 NM legs, at or above 1700, left after one circuit.
  const hf = { path: 'HF', inbound: 176, turn: 'LEFT', legDistanceNm: 4, legTimeMin: null, exit: 'ONCE', altitude: '1700A' }
  expect(r190.transitions).toEqual({
    HTO: [{ ident: 'HTO' }, { ident: 'TIDUE', altitude: '1800A', path: 'TF', hold: hf }],
    CCC: [{ ident: 'CCC' }, { ident: 'TIDUE', altitude: '1700A', path: 'TF', hold: hf }],
  })
})

test('C.7: the transition joins the final by record role, TIDUE flown once with its HF, never collapsed by name', () => {
  const r190 = procedure('87N', 'R190')!
  const joined = joinTransition(r190.transitions.HTO, r190.legs)
  // HTO, then TIDUE reached on the TF at or above 1800 with the HF there, then the final from TIDUE: STAYS and CRANN.
  // The final's IF adds its 70 kt limit; its 1700 applies after the hold, which carries it.
  expect(joined.map(l => ('ident' in l ? l.ident : l.path))).toEqual(['HTO', 'TIDUE', 'STAYS', 'CRANN'])
  expect(joined[1]).toMatchObject({ ident: 'TIDUE', altitude: '1800A', hold: { path: 'HF', exit: 'ONCE', altitude: '1700A' }, speedLimit: { kt: 70 } })
  // A transition that does not end at the final's IF is not joined: its legs and the final's are kept whole.
  const apart = joinTransition([{ ident: 'HTO' }], r190.legs)
  expect(apart.map(l => ('ident' in l ? l.ident : l.path))).toEqual(['HTO', 'TIDUE', 'STAYS', 'CRANN'])
  expect(apart[1]).not.toHaveProperty('hold')
  // Only onto the final's IF: a final that begins with a TF to the same fix is a second leg to it, and is kept.
  const tf = joinTransition([{ ident: 'HTO' }, { ident: 'TIDUE', path: 'TF' }], [{ ident: 'TIDUE', path: 'TF' }, { ident: 'STAYS', path: 'TF' }])
  expect(tf.map(l => ('ident' in l ? l.ident : l.path))).toEqual(['HTO', 'TIDUE', 'TIDUE', 'STAYS'])
})

test('C.7: in the FMS the route flies TIDUE once with its HF; a direct-to TIDUE keeps the HF, a direct-to STAYS drops it', () => {
  const unit = flying('87N', 'R190', 'HTO')
  const legs = () => unit.activeRoute.legs
  const approach = legs().slice(legs().findIndex(l => l.kind === 'wpt' && l.ident === 'HTO'))
  expect(approach.map(l => (l.kind === 'wpt' ? l.ident : l.kind === 'cond' ? `(${l.path})` : '(disco)'))).toEqual(['HTO', 'TIDUE', 'STAYS', 'CRANN', '(CA)', 'BEADS'])
  const tidue = approach[1]
  expect(tidue).toMatchObject({ kind: 'wpt', ident: 'TIDUE', altitude: '1800A', hold: { path: 'HF', legDistanceNm: 4, exit: 'ONCE' }, speedLimit: { kt: 70 } })
  expect(approach[5]).toMatchObject({ kind: 'wpt', ident: 'BEADS', path: 'DF', hold: { path: 'HM', legDistanceNm: 4 }, speedLimit: { kt: 70 } })

  // Direct-to TIDUE replaces the legs before it and keeps the HF that follows.
  expect(unit.directTo('TIDUE')).toBeUndefined()
  unit.press('EXEC')
  expect(legs()[0]).toMatchObject({ kind: 'wpt', ident: 'TIDUE', hold: { path: 'HF', exit: 'ONCE' } })
  // Direct-to STAYS removes the HF, as a crew choice.
  unit.directTo('STAYS')
  unit.press('EXEC')
  expect(legs()[0]).toMatchObject({ kind: 'wpt', ident: 'STAYS' })
  expect(legs().some(l => l.kind === 'wpt' && l.hold?.path === 'HF')).toBe(false)
})

test('C.6: a missed approach arms the BEADS hold with its coded 4 NM legs and 90 kt, not 1 minute at 180 kt', () => {
  const unit = flying('87N', 'R190', 'HTO')
  expect(unit.goAround()).toBe(true)
  // The coded exit (HM: MANUAL) is kept; the S300 still leaves it after one racetrack (missed, MISSED-HOLD).
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'BEADS', turn: 'RIGHT', inbound: 222, legDistance: 4, legTime: null, speed: 90, exit: 'MANUAL', altitude: '2000A', missed: true })
})

test('C.5: procedure speed limits are imported from columns 100-102 with their descriptor, on fix and conditional legs', () => {
  // KJRA R210: IF JEDIL 090, FAF ERORE 070, the missed CA 070 (a conditional leg), DF JEDIL 090, HM JEDIL 090 coded "at".
  const r210 = procedure('KJRA', 'R210')!
  expect(r210.legs[0]).toMatchObject({ ident: 'JEDIL', speedLimit: { kt: 90, descriptor: 'AT OR BELOW' } })
  expect(r210.legs[1]).toMatchObject({ ident: 'ERORE', speedLimit: { kt: 70, descriptor: 'AT OR BELOW' } })
  expect(r210.legs[2]).not.toHaveProperty('speedLimit')
  expect(r210.missed![0]).toMatchObject({ path: 'CA', speedLimit: { kt: 70, descriptor: 'AT OR BELOW' } })
  expect(r210.missedHold).toMatchObject({ fix: 'JEDIL', legDistanceNm: 4, speedLimit: { kt: 90, descriptor: 'AT' } })
  // KLGA R250's missed approach codes a VA on 070.0 magnetic (058 true) to 1240 with a left turn and 070.
  expect(procedure('KLGA', 'R250')!.missed![1]).toMatchObject({ path: 'VA', course: expect.closeTo(58, 6), altitude: 1240, turnDirection: 'LEFT', speedLimit: { kt: 70 } })
})

test('C.8: what is not supported stays unavailable with the reason', () => {
  const raw = FIXTURE.split('\n')
  // A heliport departure (HUDSN ONE at KJRA, the real record) is not read, and says so.
  const hd = 'SUSAH KJRAK6DHUDSN16YOMAN 010HUDSNK6EA0E       IF                                 + 00920     18000       HUDSN K6EA       771632502'
  expect(parseArinc424(`${FIXTURE}${hd}\n`).errors).toContain('KJRA HUDSN1: heliport departures (HD) are not read by this simulation')
  // A Copter approach whose landing site record is missing cannot be placed.
  const noKjfk = raw.filter(line => !/^SUSAP KJFKK6A/.test(line)).join('\n')
  expect(parseArinc424(noKjfk).errors).toContain('KJFK R027: point-in-space approach without its landing site record')
  // Nor one whose MAP fix is not in the data.
  const noCrann = raw.filter(line => !/^SUSAH 87N K6CCRANN/.test(line)).join('\n')
  expect(parseArinc424(noCrann).errors).toContain('87N R190: point-in-space approach whose missed approach point CRANN is not in the data')
  // An airport-section approach whose MAP is not a runway and which is not a Copter procedure stays refused: R027
  // renamed R27 reads as a runway approach ident.
  const renamed = raw.map(line => (/^SUSAP KJFKK6FR027 /.test(line) ? line.replace('FR027 ', 'FR27  ') : line)).join('\n')
  expect(parseArinc424(renamed).errors).toContain('KJFK R27: missed approach point is not a runway')
})

test('airports limits a load to a named heliport, its procedure and the fixes it uses', () => {
  const limited = parseArinc424(FIXTURE, { airports: ['87N'] })
  const idents = limited.data.entries.map(e => e.ident)
  for (const ident of ['87N', 'CRANN', 'STAYS', 'TIDUE', 'BEADS', 'CCC', 'HTO']) expect(idents).toContain(ident)
  for (const ident of ['KJRA', 'JORBA', 'KLGA', 'COL']) expect(idents).not.toContain(ident)
  expect(limited.data.procedures.map(p => `${p.airport} ${p.ident}`)).toEqual(['87N R190'])
  expect(limited.data.msa?.map(m => m.airport)).toEqual(['87N'])
})

test('the CDU: a heliport is a destination, REF NAV DATA shows it and its approach, ARRIVALS lists RNAV 190', () => {
  const unit = flying('87N', 'R190', 'HTO')
  expect(unit.route.dest).toBe('87N')
  unit.press('INIT_REF')
  unit.press('LSK1R')
  typeText(unit, '87N')
  unit.press('LSK1L')
  expect(lines(unit)[2]).toMatch(/^87N\s+HELIPORT$/)
  expect(lines(unit)[9]).toMatch(/APPROACHES/)
  expect(lines(unit)[10]).toMatch(/^R190 VFR/)
  typeText(unit, 'KJRA')
  unit.press('LSK1L')
  expect(lines(unit)[10]).toMatch(/^R210 VIS UNKNOWN/)
  // A 3-character ident that is not an airport or heliport is still refused as a destination.
  unit.press('RTE')
  typeText(unit, 'HTO')
  unit.press('LSK1R')
  expect(unit.route.dest).toBe('87N')
  expect(lines(unit).join('\n')).toContain('INVALID ENTRY')
  unit.press('DEP_ARR')
  unit.press('LSK1R')
  expect(lines(unit).join('\n')).toContain('RNAV 190')
})

test('MISSED-HOLD: the BEADS hold is flown for one racetrack and left at the fix; the route ends, and NAV gives way to HDG (D-H, M300 7-16)', () => {
  const unit = flying('87N', 'R190', 'HTO')
  const sim = new FlightSimulator(unit)
  expect(unit.goAround()).toBe(true)
  const beads = unit.coordinates('BEADS')!
  // Established on the inbound course 3 NM before BEADS at 2000 ft: a direct entry.
  expect(unit.directTo('BEADS')).toBeUndefined()
  unit.press('EXEC')
  unit.placeAircraft({ position: offset(beads, 222 + 180, 3), track: 222, altitude: 2000 }, 'test: inbound to BEADS')
  const fly = (seconds: number, until: () => boolean) => { for (let t = 0; t < seconds; t++) { sim.step(1); if (until()) return t } return seconds }
  expect(fly(600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')).toBeLessThan(600)
  expect(unit.holdEntryFlown).toBe('DIRECT')
  // One racetrack: two 4 NM legs and two half turns at about 90 kt is roughly seven minutes; then out at the fix.
  const circuit = fly(1200, () => unit.activeRoute.hold === undefined)
  expect(circuit).toBeGreaterThan(300)
  expect(circuit).toBeLessThan(720)
  expect(distanceNm(unit.position, beads)).toBeLessThan(0.3)
  fly(10, () => false)
  expect(unit.recallList.some(m => m.text === 'END OF ROUTE')).toBe(true)
  expect(sim.lateralMode).toBe('HDG')
})

test('an AT TGT ALT hold (HA) is left at the first fix crossing once the target altitude is reached (D-H, M300 10-10)', () => {
  const unit = flying('87N', 'R190', 'HTO')
  const sim = new FlightSimulator(unit)
  const tidue = unit.coordinates('TIDUE')!
  expect(unit.directTo('TIDUE')).toBeUndefined()
  unit.press('EXEC')
  unit.placeAircraft({ position: offset(tidue, 300, 3), track: 120, altitude: 1500 }, 'test: toward TIDUE below the target')
  const fly = (seconds: number, until: () => boolean) => { for (let t = 0; t < seconds; t++) { sim.step(1); if (until()) return t } return seconds }
  fly(600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  expect(unit.activeRoute.hold!.fix).toBe('TIDUE')
  unit.changeHold(h => { h.exit = 'AT TGT ALT'; h.altitude = '5000A' })
  unit.press('EXEC')
  // A crossing is a return to within 0.2 NM of TIDUE after being more than a mile away.
  let crossings = 0, away = false, belowAtFirst = 0
  const crossed = () => {
    const d = distanceNm(unit.position, tidue)
    if (d > 1) away = true
    if (away && d < 0.2) { away = false; crossings++; return true }
    return false
  }
  // The first crossing below 5000 ft: the hold goes on.
  fly(1800, () => crossed())
  expect(crossings).toBe(1)
  belowAtFirst = unit.altitude
  expect(belowAtFirst).toBeLessThan(4900)
  fly(30, () => false)
  expect(unit.activeRoute.hold?.status).toBe('IN PROGRESS')
  // Climbing to 5000: out at the next crossing.
  sim.selectAltitude(5000)
  sim.engageVerticalSpeed(800)
  const t = fly(1800, () => { crossed(); return unit.activeRoute.hold === undefined })
  expect(t).toBeLessThan(1800)
  expect(crossings).toBe(2)
  expect(unit.altitude).toBeGreaterThan(4900)
})

/** Direct to a hold fix from `nm` NM on `bearing` (from the fix), flying toward it; counts the fix crossings. */
function holdFrom(fix: string, bearing: number, nm: number, missed = false) {
  const unit = flying('87N', 'R190', 'HTO')
  const sim = new FlightSimulator(unit)
  if (missed) expect(unit.goAround()).toBe(true)
  const at = unit.coordinates(fix)!
  unit.placeAircraft({ position: offset(at, bearing, nm), track: (bearing + 180) % 360, altitude: 2000 }, 'test: toward the hold fix')
  expect(unit.directTo(fix)).toBeUndefined()
  unit.press('EXEC')
  let crossings = 0, away = true
  const fly = (seconds: number, until: () => boolean) => {
    for (let t = 0; t < seconds; t++) {
      sim.step(1)
      const d = distanceNm(unit.position, at)
      if (away && d < 0.1) { crossings++; away = false }
      if (d > 0.5) away = true
      if (until()) return t
    }
    return seconds
  }
  // How many whole racetracks the hold flew after its entry, as the engineering record states at the exit.
  const exited = () => sim.modeEvents.find(e => e.event === 'HOLD EXITED')?.detail ?? 'not exited'
  return { unit, sim, fly, crossings: () => crossings, exited }
}

for (const [entry, bearing] of [['PARALLEL', 120], ['TEARDROP', 200]] as const) {
  test(`an HF (EXIT TYPE ONCE) after a ${entry.toLowerCase()} entry is left where the entry ends, not a racetrack later (D-H)`, () => {
    const { unit, fly, exited } = holdFrom('TIDUE', bearing, 4)
    fly(600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
    expect(unit.holdEntryFlown).toBe(entry)
    const t = fly(1800, () => unit.activeRoute.hold === undefined)
    expect(t).toBeLessThan(1800)
    // Out at the fix passage that ends the entry: no whole racetrack flown.
    expect(exited()).toBe('TIDUE: 0 whole racetracks after the entry (EXIT TYPE ONCE)')
  })
}

test('the missed-approach hold after a non-direct entry still flies one whole racetrack before it is left (MISSED-HOLD)', () => {
  const { unit, fly, crossings, exited } = holdFrom('BEADS', 198, 4, true)
  fly(600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  expect(unit.holdEntryFlown).not.toBe('DIRECT')
  fly(2400, () => unit.activeRoute.hold === undefined)
  expect(unit.activeRoute.hold).toBeUndefined()
  // The entry start, the entry end, and the crossing after one racetrack.
  expect(crossings()).toBe(3)
  expect(exited()).toBe('BEADS: 1 whole racetrack after the entry (EXIT TYPE MANUAL, missed approach)')
})

test('RESUME HOLD converts the exit to MANUAL: an HF resumed is held on (D-H)', () => {
  const { unit, fly, crossings } = holdFrom('TIDUE', 176 + 180, 4)
  fly(600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')
  expect(unit.activeRoute.hold!.exit).toBe('ONCE')
  unit.press('HOLD')
  unit.press('LSK5R')
  unit.press('EXEC')
  expect(unit.activeRoute.hold!.status).toBe('EXIT ARMED')
  unit.press('LSK5R')
  unit.press('EXEC')
  expect(unit.activeRoute.hold).toMatchObject({ status: 'IN PROGRESS', exit: 'MANUAL' })
  fly(1500, () => false)
  expect(unit.activeRoute.hold?.status).toBe('IN PROGRESS')
  expect(crossings()).toBeGreaterThanOrEqual(3)
})

test('UNABLE HOLD at the first fix passage: no hold guidance, NAV gives way to a latched HDG, the fix is not sequenced (D-H, F8)', () => {
  const { unit, sim, fly } = holdFrom('TIDUE', 176 + 180, 4)
  fly(30, () => false)
  expect(sim.lateralMode).toBe('LNAV')
  unit.wind.direction = 0
  unit.wind.speed = Math.ceil(sim.tas) + 5
  fly(600, () => unit.recallList.some(m => m.text === 'UNABLE HOLD'))
  expect(unit.recallList.some(m => m.text === 'UNABLE HOLD')).toBe(true)
  fly(2, () => false)
  expect(sim.lateralMode).toBe('HDG')
  expect(sim.headingHeld).toBe(true)
  expect(sim.guidance.mode).not.toBe('HOLD')
  const leg = unit.activeRoute.legs[0]
  expect(leg?.kind === 'wpt' && leg.ident).toBe('TIDUE')
})
