import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import type { GpsReceiver } from '../src/fmsCdu/gps'
import type { Airport } from '../src/fmsCdu/navData'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Real navigation data: the FAA's Coded Instrument Flight Procedures (CIFP, ARINC 424-18, a US Government work in the
// public domain). The fixture is Burlington, Vermont (KBTV) from cycle 2609: its airport, runway, terminal waypoint,
// approach and path point records, and the enroute fixes and navaids its procedures use, extracted unchanged from
// FAACIFP18. The reader turns magnetic runway bearings true with the airport's variation (Astra's GPS review, Q5),
// builds the RNAV approaches, and keeps each one's published FAS data block, which the GPS then flies as published.
const FIXTURE = readFileSync('tests/fixtures/cifp/kbtv-2609.pc', 'latin1')
const parsed = () => parseArinc424(FIXTURE)
const kbtv = () => parsed().data.entries.find((e): e is Airport => e.kind === 'airport' && e.ident === 'KBTV')!
const approach = (ident: string) => parsed().data.procedures.find(p => p.airport === 'KBTV' && p.ident === ident)

// Laboratory 132-column records, independent of the decoder: positions are specified in ARINC coordinates,
// and path fields use the published 424-17/18 columns (true T replaces the course's tenths digit).
function labRecord(fields: [number, string][]) {
  const row = Array<string>(132).fill(' ')
  for (const [column, value] of fields) for (const [i, ch] of [...value].entries()) row[column - 1 + i] = ch
  return row.join('')
}
const labFix = (ident: string, latitude: string, longitude: string) => labRecord([[1, 'SUSAEA'], [7, 'ENRT'], [14, ident.padEnd(5)], [20, 'K6'], [22, '0'], [33, latitude], [42, longitude]])
const labLeg = (seq: string, fix: string, path: string, fields: [number, string][] = []) =>
  labRecord([[1, 'SUSAP KBTVK6F'], [14, 'R99'], [20, 'R'], [27, seq], [30, fix.padEnd(5)], [35, 'K6PC0'], [48, path], ...fields])
const labAirport = FIXTURE.split('\n').filter(line => line[4] === 'P' && (line[12] === 'A' || line[12] === 'G')).join('\n')

test('imported PI is the S300 two-outbound construction followed by the coded inbound, with protected generated fixes', () => {
  const text = [labAirport, labFix('PTREF', 'N44000000', 'W073000000'),
    labLeg('010', 'PTREF', 'PI', [[20, 'V'], [44, 'R'], [71, '000T'], [75, '0100']]),
    labLeg('020', 'PTREF', 'CF', [[20, 'V'], [71, '180T']]),
    labLeg('030', 'RW15', 'TF', [[20, 'V'], [43, 'M']])].join('\n')
  const result = parseArinc424(text), pi = result.data.procedures.find(p => p.ident === 'R99')!
  expect(result.errors).toEqual([])
  expect(pi.approachType).toBe('VOR')
  expect(pi.legs.map(l => 'ident' in l ? l.ident : l.path)).toEqual(['PTREF', 'PTRPTR', 'PTRPTL', 'PTREF', 'RW15'])
  const first = pi.legs[1], second = pi.legs[2]
  if (!('ident' in first) || !('ident' in second) || !first.position || !second.position) throw Error('PI outbound positions missing')
  expect(distanceNm({ lat: 44, lon: -73 }, first.position)).toBeCloseTo(3, 5)
  expect(distanceNm(first.position, second.position)).toBeCloseTo(2.25, 5)
  expect(bearingDeg(first.position, second.position)).toBeCloseTo(45, 5)
  expect(pi.legs[3]).toMatchObject({ path: 'CF', course: 180, turnDirection: 'LEFT', procedureTurn: { role: 'INBOUND' } })
  const unit = new ScriptedFms()
  unit.loadArinc424(text, 'lab PI'); unit.swapCycles()
  unit.modify(route => { route.dest = 'KBTV'; route.legs = [] }); unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'R99'); unit.press('EXEC')
  unit.placeAircraft({ position: offset({ lat: 44, lon: -73 }, 180, 0.1), track: 0, altitude: 3000 }, 'lab PI inbound initial pose')
  unit.wind = { direction: 0, speed: 0 }
  const sim = new FlightSimulator(unit)
  let inboundLeft = false, firstNorth = false, secondNortheast = false
  for (let i = 0; i < 1200; i += 1) {
    sim.step(1)
    const leg = unit.activeRoute.legs[0]
    if (leg?.kind !== 'wpt') break
    if (leg.ident === 'PTRPTR' && unit.truePosition.lat > 44.04) firstNorth = true
    if (leg.ident === 'PTRPTL' && unit.truePosition.lon > -72.975) secondNortheast = true
    if (leg.procedureTurn?.role === 'INBOUND' && sim.bankAngle < -5) inboundLeft = true
    if (leg.ident === 'RW15') break
  }
  expect(firstNorth && secondNortheast && inboundLeft).toBe(true)
  expect(unit.activeRoute.legs[0]).toMatchObject({ ident: 'RW15' })
  // Reload the unchanged route for the separate crew deletion check.
  unit.selectProcedure('APPROACH', 'R99'); unit.press('EXEC')
  unit.press('LEGS')
  // Reference plus the two private outbound fixes are one procedure-turn deletion; its inbound CF stays.
  for (let i = 0; i < 5 && !screenText(unit.screen())[SCRATCHPAD_LINE].includes('DELETE'); i += 1) unit.press('CLR')
  expect(screenText(unit.screen())[SCRATCHPAD_LINE]).toContain('DELETE')
  unit.press('LSK1L')
  expect(unit.route.legs.filter(l => l.kind === 'wpt' && l.procedureTurn?.role === 'OUTBOUND')).toEqual([])
  expect(unit.route.legs.find(l => l.kind === 'wpt' && l.ident === 'PTREF')).toMatchObject({ path: 'CF', course: 180 })
})

test('RF and AF import use their actual centre and radius fields and fly the coded quarter-circle, refusing bad geometry', () => {
  for (const path of ['RF', 'AF'] as const) {
    const text = [labAirport, labFix('START', 'N44010000', 'W073000000'), labFix('CENTR', 'N44000000', 'W073000000'), labFix('ARCND', 'N44000000', 'W072583659'),
      labLeg('010', 'START', 'IF', [[20, path === 'AF' ? 'D' : 'R']]),
      labLeg('020', 'ARCND', path, [[20, path === 'AF' ? 'D' : 'R'], [44, 'R'], ...(path === 'RF' ? [[57, '001000'], [107, 'CENTR']] : [[51, 'CENT'], [67, '0010']]) as [number, string][]]),
      labLeg('030', 'RW15', 'TF', [[20, path === 'AF' ? 'D' : 'R'], [43, 'M']])].join('\n').replaceAll('CENTR', path === 'AF' ? 'CENT ' : 'CENTR')
    const result = parseArinc424(text), procedure = result.data.procedures.find(p => p.ident === 'R99')!
    expect(result.errors).toEqual([])
    expect(procedure.approachType).toBe(path === 'AF' ? 'VOR' : 'RNAV')
    expect(procedure.legs[1]).toMatchObject({ path, arc: { centre: { lat: 44, lon: -73 }, turn: 'R' } })
    expect(parseArinc424(text.replace(path === 'RF' ? '001000' : '0010', path === 'RF' ? '002000' : '0020')).errors).toContain(`KBTV R99: ${path} endpoint disagrees with coded radius`)
    let now = Date.UTC(2026, 8, 29, 14)
    const unit = new ScriptedFms(() => new Date(now))
    unit.loadArinc424(text, `lab ${path}`); unit.swapCycles()
    unit.modify(route => { route.dest = 'KBTV'; route.legs = [] }); unit.press('EXEC')
    unit.selectProcedure('APPROACH', 'R99'); unit.press('EXEC')
    unit.placeAircraft({ position: { lat: 44 + 1 / 60, lon: -73 }, track: 90, altitude: 3000 }, 'lab arc tangent initial pose')
    unit.sequence()
    const sim = new FlightSimulator(unit); unit.wind = { direction: 0, speed: 0 }
    const radii: number[] = []
    for (let i = 0; i < 600; i += 1) {
      now += 1000; sim.step(1)
      const leg = unit.activeRoute.legs[0]
      if (leg?.kind === 'wpt' && leg.ident === 'ARCND' && sim.guidance.distanceToGo! < 1.3) radii.push(distanceNm({ lat: 44, lon: -73 }, unit.truePosition))
      if (leg?.kind === 'wpt' && leg.ident === 'RW15') break
    }
    expect(unit.activeRoute.legs[0]).toMatchObject({ ident: 'RW15' })
    expect(distanceNm(unit.truePosition, unit.coordinates('ARCND')!)).toBeLessThan(0.08)
    expect(radii.length).toBeGreaterThan(10)
    // Laboratory guidance tolerance; this is simulator evidence, not a protected-airspace or RNP approval.
    expect(Math.max(...radii.slice(10).map(radius => Math.abs(radius - 1)))).toBeLessThan(0.3)
  }
})

test('a CIFP airport gives its magnetic variation, and runway bearings are made true with it', () => {
  const result = parsed()
  expect(result.invalid).toEqual([])
  const airport = kbtv()
  // RW15 is published at 146.0 degrees magnetic, RW01 at 006.0: 15 degrees west makes them 131.0 and 351.0 true.
  expect(airport.runways.find(r => r.ident === 'RW15')!.course).toBeCloseTo(131, 6)
  expect(airport.runways.find(r => r.ident === 'RW01')!.course).toBeCloseTo(351, 6)
  expect(airport.magneticVariation).toBe(-15)
  expect(result.data.cycle.id).toBe('CIFP2609')
})

test('an RNAV approach is built from its CIFP legs: the final, its FAF, the missed approach and its hold', () => {
  const r15 = approach('R15')!
  expect(r15).toMatchObject({ kind: 'APPROACH', approachType: 'RNAV', runways: ['RW15'], faf: 'FOVES' })
  expect(r15.legs).toEqual([
    { ident: 'STAEV', altitude: '3200A' },
    { ident: 'FOVES', altitude: '2000A', path: 'TF' },
    // The step-down fix and the threshold code the vertical angle, -3.00 degrees.
    { ident: 'JUNEL', altitude: '1020A', path: 'TF', verticalAngleDeg: -3 },
    { ident: 'RW15', altitude: '357', path: 'TF', verticalAngleDeg: -3, overfly: true },
  ])
  // The missed approach: a climb on 145.8 degrees magnetic (130.8 true) to 1000 ft, direct YUNUD turning right, and the
  // HM there on 042.0 (027 true) with 5 NM legs, kept on the DF leg and as the missed approach hold.
  const hm = { path: 'HM', inbound: 27, turn: 'RIGHT', legDistanceNm: 5, legTimeMin: null, exit: 'MANUAL', altitude: '5600A' }
  expect(r15.missed).toEqual([{ path: 'CA', course: expect.closeTo(130.8, 6), altitude: 1000 }, { ident: 'YUNUD', altitude: '5600A', path: 'DF', turnDirection: 'RIGHT', hold: hm }])
  expect(r15.missedHold).toEqual({ fix: 'YUNUD', inbound: 27, turn: 'RIGHT', altitude: '5600A', legDistanceNm: 5 })
  // The STAEV transition is the hold-in-lieu of a procedure turn alone (a single HF record): now kept, where it was dropped.
  expect(r15.transitions.STAEV).toEqual([{ ident: 'STAEV', altitude: '3200A', hold: { path: 'HF', inbound: expect.closeTo(130.7, 6), turn: 'RIGHT', legDistanceNm: 4, legTimeMin: null, exit: 'ONCE', altitude: '3200A' } }])
  expect(r15.endpoint).toMatchObject({
    instrumentEnd: { fix: 'RW15', altitude: '357' }, landingSite: { kind: 'RUNWAY', ident: 'RW15' }, visualSegment: { kind: 'RUNWAY' }, vertical: { kind: 'VPA', angleDeg: 3 },
    // Q8: identified by its coded runway threshold, not by any ident pattern.
    identification: { basis: 'RUNWAY', source: 'the MAP is the runway threshold (coded)' },
  })
  expect(Object.keys(r15.transitions).sort()).toEqual(['STAEV', 'WULEB', 'YUNUD'])
  // An approach whose missed approach point is not a runway is left out, with the reason.
  expect(approach('R33-Y')).toBeUndefined()
  expect(parsed().errors).toContain('KBTV R33-Y: missed approach point is not a runway')
})

test('the published FAS data block is decoded, and its course agrees with the runway made true', () => {
  const fas = approach('R15')!.publishedFas!
  expect(fas).toMatchObject({
    airport: 'KBTV', runway: 15, designator: '', referencePathId: 'W15A', gpaDeg: 3, tchFt: 51.1, courseWidthM: 106.75,
    lengthOffsetM: 216, halM: 40, valM: 50, publishedCrc: '644ADC20',
  })
  expect(fas.ltp.lat).toBeCloseTo(44 + 28 / 60 + 50.428 / 3600, 9)
  expect(fas.ltp.lon).toBeCloseTo(-(73 + 9 / 60 + 57.1635 / 3600), 9)
  expect(fas.ltp.heightM).toBe(64.2)
  // An independent check of the magnetic conversion: the FAS course (LTP to FPAP, true by construction) and the runway's
  // magnetic bearing made true agree to a fraction of a degree.
  const course = bearingDeg(fas.ltp, { lat: fas.ltp.lat + fas.fpapDelta.lat, lon: fas.ltp.lon + fas.fpapDelta.lon })
  expect(Math.abs(course - kbtv().runways.find(r => r.ident === 'RW15')!.course)).toBeLessThan(0.5)
})

test('airports limits a load to those airports and the fixes their procedures use', () => {
  const other = (line: string) => line.replace(/KBTV/g, 'KPBG').replace(/K6/g, 'K6')
  const foreignAirport = FIXTURE.split('\n').filter(line => line[4] === 'P' && line[12] === 'A').map(other).join('\n')
  const unusedFix = 'SUSAEAENRT   ZZZZZ K60    W     N44000000W073000000                                                       0000000000'
  // An airway (ER) nothing asks for: a limited load reads no airways at all.
  const airway = (sequence: string, fix: string) => `SUSAER       J999        ${sequence}${fix.padEnd(5)}    0`.padEnd(132)
  const text = `${FIXTURE}${foreignAirport}\n${unusedFix}\n${airway('0010', 'BTV')}\n${airway('0020', 'YUNUD')}\n`
  const limited = parseArinc424(text, { airports: ['KBTV'] })
  const idents = limited.data.entries.map(e => e.ident)
  expect(idents).toContain('KBTV')
  expect(idents).not.toContain('KPBG')
  expect(idents).not.toContain('ZZZZZ')
  expect(idents).toContain('YUNUD')
  expect(limited.data.airways).toEqual([])
  expect(limited.data.procedures.map(p => p.ident)).toContain('R15')
  // Unlimited, the foreign airport, the unused fix and the airway are read.
  const unlimited = parseArinc424(text)
  expect(unlimited.data.entries.map(e => e.ident)).toEqual(expect.arrayContaining(['KPBG', 'ZZZZZ']))
  expect(unlimited.data.airways.map(a => a.ident)).toContain('J999')
})

test('CIFP altitude descriptions become the constraints the simulation flies', () => {
  // A synthetic approach on KBTV's records: the same fixes, each leg with a different altitude description.
  const leg = (sequence: string, fix: string, description: string, path: string, altitude: string) =>
    `SUSAP KBTVK6FR99   R      ${sequence}${fix.padEnd(5)}K6PC0E  ${description}    ${path}`.padEnd(82) + altitude
  const text = [
    leg('010', 'STAEV', 'I', 'IF', '+ 03200     '),
    leg('020', 'FOVES', 'F', 'TF', '  02000     '),
    leg('021', 'JUNEL', ' ', 'TF', '- 01500     '),
    leg('025', 'CESAL', ' ', 'TF', 'B 0180001200'),
    leg('030', 'RW15 ', 'M', 'TF', '  00357     '),
  ].map(line => line.padEnd(132)).join('\n')
  const r99 = parseArinc424(`${FIXTURE}${text}\n`).data.procedures.find(p => p.ident === 'R99')!
  expect(r99.legs.map(l => ('ident' in l ? `${l.ident} ${l.altitude}` : ''))).toEqual(['STAEV 3200A', 'FOVES 2000', 'JUNEL 1500B', 'CESAL 1800B1200A', 'RW15 357'])
})

test('the FMS flies a CIFP RNAV approach with its published FAS: executed, sent to both receivers and accepted', () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  expect(unit.loadArinc424(FIXTURE, 'kbtv-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.press('RTE')
  for (const ch of 'KBTV') unit.press(`CHAR_${ch}` as CduFunction)
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'R15', 'STAEV')
  unit.press('EXEC')
  fly(1)
  const published = approach('R15')!.publishedFas!
  const { publishedCrc: _crc, ...fields } = published
  expect(unit.executedFas?.fas).toMatchObject(fields)
  expect(unit.executedFas?.cycle).toBe('CIFP2609')
  expect(Math.abs((unit.finalApproachCourse ?? NaN) - 131)).toBeLessThan(0.5)
  // Both receivers hold the block, and it passes their CRC check (the model CRC over the published fields).
  const receivers = (unit as unknown as { gps: readonly GpsReceiver[] }).gps
  for (const receiver of receivers) {
    const status = receiver.bus()!['156'].value!
    expect(status).toMatchObject({ selected: true, available: true, crcInvalid: false, mismatch: false, incomplete: false })
  }
})

test('the aircraft flies the published KBTV RNAV RWY 15 LPV: captured on final, on the published path to the threshold', () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  unit.loadArinc424(FIXTURE, 'kbtv-2609.pc')
  unit.swapCycles()
  unit.press('RTE')
  for (const ch of 'KBTV') unit.press(`CHAR_${ch}` as CduFunction)
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'R15', 'STAEV')
  unit.press('EXEC')
  unit.armApproach(true)
  // Jump to STAEV, the intermediate fix, 3200 ft; then fly the final.
  const active = () => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }
  // The demonstration route ends at CYUL: a discontinuity separates it from the KBTV approach, crossed by the override.
  for (let i = 0; i < 20 && active() !== 'STAEV'; i += 1) if (unit.sequence() === 'discontinuity') unit.overrideDiscontinuity()
  expect(active()).toBe('STAEV')
  // STAEV carries the hold-in-lieu of a procedure turn (HF, EXIT TYPE ONCE; D-H): the first crossing enters it, the
  // next leaves it for the final.
  unit.sequence()
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'STAEV', exit: 'ONCE', status: 'IN PROGRESS' })
  expect(active()).toBe('STAEV')
  unit.sequence()
  expect(unit.activeRoute.hold).toBeUndefined()
  expect(active()).toBe('FOVES')
  // The FMS path agrees with the published one: the CIFP gives the glide path altitude at the JUNEL step-down fix as
  // 1025 ft (the second altitude on its record), and the VNAV profile, with FOVES as the FAF at 2000 ft, predicts it.
  expect(unit.profile().points.find(p => p.ident === 'JUNEL')!.altitude!).toBeCloseTo(1025, -1)
  // APPROACH REF takes the executed approach's FAF altitude (FOVES, 2000 ft), not the demonstration's 1500, and the FAF is
  // crossed at it.
  expect(unit.vnav.fafAltitude).toBe(2000)
  expect(unit.profile().points.find(p => p.ident === 'FOVES')!.altitude!).toBeCloseTo(2000, 0)
  const fas = approach('R15')!.publishedFas!
  // The published path: TCH above the LTP, rising at the glide path angle (heights above the ellipsoid, as the FAS gives them).
  const toThresholdFt = () => {
    const d = unit.truePosition, ltp = fas.ltp
    const dy = (d.lat - ltp.lat) * 364_000, dx = (d.lon - ltp.lon) * 364_000 * Math.cos((ltp.lat * Math.PI) / 180)
    return Math.hypot(dx, dy)
  }
  let captured = false, worst = 0
  for (let t = 0; t < 1800; t += 1) {
    now += 1000
    sim.step(1)
    // Captured on the final approach segment, which starts at the FAF: still flying to JUNEL, the step-down fix inside it.
    if (sim.approachMode === 'CAPTURED' && !captured) expect(active()).toBe('JUNEL')
    if (sim.approachMode === 'CAPTURED') captured = true
    if (captured && active() === 'RW15' && toThresholdFt() < 3 * 6076 && toThresholdFt() > 0.3 * 6076) {
      const path = fas.tchFt + toThresholdFt() * Math.tan((fas.gpaDeg * Math.PI) / 180)
      // The receiver's 117 is the deviation from that path; it stays small once established.
      const vertical = unit.gpsApproach?.verticalFt
      if (vertical !== null && vertical !== undefined) worst = Math.max(worst, Math.abs(vertical))
      expect(path).toBeGreaterThan(0)
    }
    if (active() !== 'RW15' && captured) break
  }
  expect(captured).toBe(true)
  expect(sim.modeEvents.some(e => e.event === 'APPR CAPTURED')).toBe(true)
  // Captured at the FAF (FOVES), not after the last step-down fix, the aircraft stays on the published path.
  expect(worst).toBeLessThan(30)
})
