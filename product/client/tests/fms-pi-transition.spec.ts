import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { joinTransition } from '../src/fmsCdu/procedures'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import type { ProcedureLeg } from '../src/fmsCdu/navData'
import type { CduFunction } from '../src/fmsCdu/variants'

// #1389: a PI (procedure turn) that ends an approach transition. The FAA CIFP codes the PI's inbound CF in the final,
// which begins IF, then a CF to the PI fix; ARINC 424 puts a CF after every PI. The data is FAA CIFP 2609 (FAACIFP18,
// public domain), extracted unchanged per airport. PACD S15 (Cold Bay VOR runway 15): transition CHUNA is IF CHUNA,
// TF CDB, PI CDB; the final is IF DEADS, CF CDB (the FAF), CF RW15. Its magnetic variation is E0120 (12.0 east).
const read = (name: string) => readFileSync(`tests/fixtures/cifp/${name}-2609.pc`, 'latin1')
const PACD = read('pacd'), VARIATION = 12
const describe = (legs: ProcedureLeg[]) => legs.map(l => ('ident' in l ? `${l.path ?? 'IF'} ${l.ident}${l.procedureTurn ? ` ${l.procedureTurn.role}` : ''}` : l.path))
const record = (prefix: string) => PACD.split('\n').find(line => line.startsWith(prefix))!

test('#1389: a PI that ends a transition is flown through the final\'s CF to the reversal fix, and the final\'s IF is not flown after it', () => {
  const { data } = parseArinc424(PACD, { airports: ['PACD'] })
  const s15 = data.procedures.find(p => p.ident === 'S15')!
  expect(s15).toBeDefined()
  // The records, read by column: the transition ends in the PI at CDB; the final's CF CDB is 149.2 magnetic at 1,100.
  expect(record('SCANP PACDPAFS15   ACHUNA 030').slice(47, 49)).toBe('PI')
  const finalCf = record('SCANP PACDPAFS15   S      020')
  expect([finalCf.slice(47, 49), finalCf.slice(29, 34).trim(), finalCf.slice(70, 74)]).toEqual(['CF', 'CDB', '1492'])

  const route = joinTransition(s15.transitions.CHUNA, s15.legs)
  expect(describe(route)).toEqual(['IF CHUNA', 'TF CDB REFERENCE', 'TF CDBPTL OUTBOUND', 'TF CDBPTR OUTBOUND', 'CF CDB INBOUND', 'CF RW15'])
  const inbound = route[4] as Extract<ProcedureLeg, { ident: string }>
  expect(inbound.course).toBeCloseTo(149.2 + VARIATION, 9)
  // The final's constraint at its FAF takes precedence (M300 7-2), as at any join.
  expect(inbound.altitude).toBe('1100A')
  // DEADS, the final's IF, is the straight-in entry: never flown after the reversal.
  expect(route.some(l => 'ident' in l && l.ident === 'DEADS')).toBe(false)
  // The straight-in arc transitions still join the final at DEADS.
  expect(describe(joinTransition(s15.transitions.KOTIZ, s15.legs))).toEqual(['IF KOTIZ', 'AF DEADS', 'CF CDB', 'CF RW15'])
})

test('#1389: the FMS builds the reversal route from the CDU, with the final\'s FAF and runway after the inbound CF', () => {
  const now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now), {})
  expect(unit.loadArinc424(PACD, 'pacd-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.press('RTE')
  for (const ch of 'PACD') unit.press(`CHAR_${ch}` as CduFunction)
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'S15', 'CHUNA')
  unit.press('EXEC')
  const approach = unit.route.legs.filter(leg => leg.kind === 'wpt' && leg.source === 'APPR').map(leg => (leg.kind === 'wpt' ? leg.ident : ''))
  expect(approach).toEqual(['CHUNA', 'CDB', 'CDBPTL', 'CDBPTR', 'CDB', 'RW15'])
})

test('#1389: a PI whose final does not begin IF, then a CF to its fix is refused with that reason, never joined by guesswork', () => {
  // PABT S02: the PI at the VOR BTT, but the final runs IF VORUC, CF SUCNO, CF YUSNU; BTT is not in it.
  expect(parseArinc424(read('pabt'), { airports: ['PABT'] }).errors)
    .toContain('PABT S02: transition JEVUM: PI at BTT ends the transition, and the final does not begin IF, then a CF to BTT')
  // 44C VOR-A: the final begins with an IF at the PI fix JVL, and no CF leads back to it.
  expect(parseArinc424(read('44c'), { airports: ['44C'] }).errors)
    .toEqual(['44C VOR-A: transition JVL: PI at JVL ends the transition, and the final does not begin IF, then a CF to JVL'])
  // A laboratory variation of PACD: its final's first leg coded TF DEADS instead of IF DEADS. The CF to CDB follows, but
  // the final does not begin with an IF, so the reversal is not joined to it.
  const tfFirst = PACD.split('\n').map(line => (line.startsWith('SCANP PACDPAFS15   S      010') ? `${line.slice(0, 47)}TF${line.slice(49)}` : line)).join('\n')
  expect(parseArinc424(tfFirst, { airports: ['PACD'] }).errors)
    .toContain('PACD S15: transition CHUNA: PI at CDB ends the transition, and the final does not begin IF, then a CF to CDB')
})

test('#1389: the joined reversal is flown: out from CDB, around, and back over CDB on the final course to RW15', () => {
  const unit = new ScriptedFms()
  unit.loadArinc424(PACD, 'pacd-2609.pc'); unit.swapCycles()
  unit.modify(route => { route.dest = 'PACD'; route.legs = [] }); unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'S15', 'CHUNA'); unit.press('EXEC')
  const cdb = unit.coordinates('CDB')!
  // On the TF from CHUNA, 2 NM short of CDB, at the transition's 2,800.
  const chuna = unit.coordinates('CHUNA')!
  unit.placeAircraft({ position: offset(cdb, bearingDeg(cdb, chuna), 2), track: bearingDeg(chuna, cdb), altitude: 2800 }, 'PACD S15 CHUNA before CDB')
  // Direct to CDB, the reversal's reference: the first CDB in the route.
  unit.directTo('CDB'); unit.press('EXEC')
  unit.wind = { direction: 0, speed: 0 }
  const sim = new FlightSimulator(unit)
  const seen: string[] = []
  let overCdbInbound = Infinity
  for (let i = 0; i < 3600; i += 1) {
    sim.step(1)
    const leg = unit.activeRoute.legs[0]
    if (leg?.kind !== 'wpt') break
    const label = `${leg.ident}${leg.procedureTurn ? ` ${leg.procedureTurn.role}` : ''}`
    if (seen.at(-1) !== label) seen.push(label)
    if (leg.ident === 'CDB' && leg.procedureTurn?.role === 'INBOUND') overCdbInbound = Math.min(overCdbInbound, distanceNm(unit.truePosition, cdb))
    if (leg.ident === 'RW15') break
  }
  expect(seen).toEqual(['CDB REFERENCE', 'CDBPTL OUTBOUND', 'CDBPTR OUTBOUND', 'CDB INBOUND', 'RW15'])
  // It came back over the reversal fix before turning for the runway.
  expect(overCdbInbound).toBeLessThan(0.5)
})
