import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { courseDeg, distanceNm, type Leg } from '../src/fmsCdu/fmsModel'
import { tasFromIas } from '../src/fmsCdu/kinematics'
import { procedureSpeedLimit } from '../src/fmsCdu/procedureSpeed'
import { LAB_AIRLINE_VNAV_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
import { screenText } from '../src/fmsCdu/screen'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import type { CduFunction } from '../src/fmsCdu/variants'

// Stage C follow-ups: the point-in-space final segment ends at the MAP (the instrument end), and the procedure speed
// limits are applied (C.5, MA-SPD-90, R3-04) with the VMINI refusal (Q7). The data is the FAA CIFP 2609 Copter
// point-in-space extract (see fms-heliport-procedures.spec.ts) and the KBTV extract.
const PINS = readFileSync('tests/fixtures/cifp/copter-pins-2609.pc', 'latin1')
const KBTV = readFileSync('tests/fixtures/cifp/kbtv-2609.pc', 'latin1')
const r190 = () => parseArinc424(PINS).data.procedures.find(p => p.ident === 'R190')!
const typeText = (unit: ScriptedFms, text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : leg?.kind === 'cond' ? `(${leg.path})` : null }

function flying(data: string, dest: string, approach: string, transition?: string, profile?: AircraftProfile) {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 29, 14, 0, 0)), profile ? { profile } : {})
  unit.loadArinc424(data, 'fixture.pc')
  unit.swapCycles()
  unit.press('RTE')
  typeText(unit, dest)
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', approach, transition)
  unit.press('EXEC')
  return unit
}

/** Sequences until the named leg is active (crossing any discontinuity), within a bound. */
function sequenceTo(unit: ScriptedFms, ident: string) {
  for (let i = 0; i < 30 && active(unit) !== ident; i += 1) if (unit.sequence() === 'discontinuity') unit.overrideDiscontinuity()
  expect(active(unit)).toBe(ident)
}

test('the point-in-space final ends at its MAP: CRANN is the instrument end, flown over, and there is no final runway', () => {
  const unit = flying(PINS, '87N', 'R190', 'HTO')
  expect(unit.instrumentEnd).toBe('CRANN')
  const crann = unit.activeRoute.legs.find(l => l.kind === 'wpt' && l.ident === 'CRANN')
  expect(crann).toMatchObject({ qualifier: '/O', source: 'APPR' })
  // No runway on a point-in-space approach, even with a runway leg left in the route from the demonstration plan.
  expect(unit.finalRunway).toBeNull()
  sequenceTo(unit, 'STAYS')
  expect(unit.onFinalSegment).toBe(false)
  // Past the FAF (STAYS), with the MAP ahead: the final approach segment.
  sequenceTo(unit, 'CRANN')
  expect(unit.onFinalSegment).toBe(true)
  // No missed approach hold before the MAP is passed.
  expect(unit.activeRoute.hold).toBeUndefined()
  // Past the MAP: the missed approach, no longer the final.
  unit.sequence()
  expect(active(unit)).toBe('(CA)')
  expect(unit.onFinalSegment).toBe(false)
  // Passing the MAP arms the missed approach hold at BEADS, as passing a runway does.
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'BEADS', status: 'ARMED', legDistance: 4 })
})

test('the instrument end is the approach leg: the same fix earlier in the route does not start the final', () => {
  const unit = flying(PINS, '87N', 'R190', 'HTO')
  unit.sequence()
  // The crew puts CRANN ahead of the route as an ordinary waypoint: it is not the approach's MAP.
  unit.modify(route => { route.legs.unshift({ kind: 'wpt', ident: 'CRANN' }) })
  unit.press('EXEC')
  expect(active(unit)).toBe('CRANN')
  expect(unit.onFinalSegment).toBe(false)
})

test('a runway approach keeps the runway as its instrument end and final runway', () => {
  const unit = flying(KBTV, 'KBTV', 'R15', 'STAEV')
  expect(unit.instrumentEnd).toBe('RW15')
  expect(unit.finalRunway).toBe('RW15')
  expect(unit.activeRoute.legs.find(l => l.kind === 'wpt' && l.ident === 'RW15')).toMatchObject({ qualifier: '/O' })
})

test('C.5: a coded limit applies from its fix onward; 70 kt from TIDUE through the final and the missed approach', () => {
  const approach = r190()
  const wpt = (ident: string, source: 'APPR' | 'MISSED' = 'APPR'): Leg => ({ kind: 'wpt', ident, source })
  const limit = (leg: Leg, baro: number | null = 1700) => procedureSpeedLimit(approach, 'HTO', leg, baro)
  // Toward TIDUE (the transition and the HF there) no procedure limit yet: the chart limits the final.
  expect(limit(wpt('HTO'))).toBeNull()
  expect(limit(wpt('TIDUE'))).toBeNull()
  // From TIDUE (its IF codes 070, at or below) through the final.
  expect(limit(wpt('STAYS'))).toEqual({ kt: 70, descriptor: 'AT OR BELOW', source: 'R190 TIDUE' })
  expect(limit(wpt('CRANN'))).toMatchObject({ kt: 70 })
  // The missed approach: the CA from the MAP, then DF BEADS, still 70.
  expect(limit({ kind: 'cond', path: 'CA', course: 176, altitude: 439, source: 'MISSED' })).toMatchObject({ kt: 70, source: 'R190 CRANN' })
  expect(limit(wpt('BEADS', 'MISSED'), 1500)).toMatchObject({ kt: 70 })
  // A leg that is not part of the approach has no procedure limit, even at one of its fixes.
  expect(limit({ kind: 'wpt', ident: 'HTO' })).toBeNull()
  expect(limit({ kind: 'wpt', ident: 'CRANN' })).toBeNull()
  expect(procedureSpeedLimit(undefined, undefined, wpt('STAYS'), 1700)).toBeNull()
})

test('R3-04: the 90 kt release is valid baro altitude at or above 2,000 ft, not the altitude capture', () => {
  const approach = r190()
  const beads: Leg = { kind: 'wpt', ident: 'BEADS', source: 'MISSED' }
  // ALT captured at 1,985 ft (inside its band): still 70.
  expect(procedureSpeedLimit(approach, 'HTO', beads, 1985)).toMatchObject({ kt: 70 })
  // An exact crossing of 2,000.0 ft: 90 from that tick.
  expect(procedureSpeedLimit(approach, 'HTO', beads, 2000)).toEqual({ kt: 90, descriptor: 'AT OR BELOW', source: 'R190 missed approach altitude 2000 reached' })
  // Invalid barometric altitude: 70 stays in force.
  expect(procedureSpeedLimit(approach, 'HTO', beads, null)).toMatchObject({ kt: 70 })
  // The release is for the missed approach only: 2,000 ft on the final does not lift the final's limit.
  expect(procedureSpeedLimit(approach, 'HTO', { kind: 'wpt', ident: 'CRANN', source: 'APPR' }, 2500)).toMatchObject({ kt: 70 })
})

test('a limit coded on a conditional leg applies from the start of that leg', () => {
  const approach = r190()
  // Only the missed approach CA codes a limit (60 kt): it is in force while the CA is flown, not before.
  const bare = {
    ...approach,
    legs: approach.legs.map(leg => ({ ...leg, speedLimit: undefined })),
    missed: [{ ...approach.missed![0], speedLimit: { kt: 60, descriptor: 'AT' as const } }, ...approach.missed!.slice(1)],
  }
  const ca: Leg = { kind: 'cond', path: 'CA', course: 176, altitude: 439, source: 'MISSED' }
  expect(procedureSpeedLimit(bare, undefined, { kind: 'wpt', ident: 'CRANN', source: 'APPR' }, 600)).toBeNull()
  expect(procedureSpeedLimit(bare, undefined, ca, 600)).toMatchObject({ kt: 60, source: 'R190 CA' })
})

test('an at-or-above value is a minimum, not a limit', () => {
  const approach = r190()
  const raised = { ...approach, legs: approach.legs.map((leg, i) => (i === 0 ? { ...leg, speedLimit: { kt: 70, descriptor: 'AT OR ABOVE' as const } } : leg)) }
  expect(procedureSpeedLimit(raised, undefined, { kind: 'wpt', ident: 'STAYS', source: 'APPR' }, 1700)).toBeNull()
})

test('Astra F2: the forecast flies each leg ahead at the limit in force on it, not at cruise', () => {
  const unit = flying(PINS, '87N', 'R190', 'HTO')
  // Still before TIDUE: STAYS to CRANN is two legs ahead, flown under the 70 KIAS limit from TIDUE.
  sequenceTo(unit, 'TIDUE')
  const points = unit.profile().points
  const at = (ident: string) => points.find(p => p.ident === ident)!
  const stays = unit.coordinates('STAYS')!, crann = unit.coordinates('CRANN')!
  // The leg starts from STAYS's 1700 ft: 70 KIAS as TAS there, over the ground in the present wind.
  const groundSpeed = unit.groundSpeedOn(courseDeg(stays, crann), tasFromIas(70, 1700))
  const expected = (distanceNm(stays, crann) / groundSpeed) * 3_600_000
  expect(at('CRANN').eta! - at('STAYS').eta!).toBeCloseTo(expected, -2)
  // Not the cruise speed: about 90 s at 120 KTAS against about 150 s at 70 KIAS in still air.
  expect(at('CRANN').eta! - at('STAYS').eta!).toBeGreaterThan((distanceNm(stays, crann) / unit.groundSpeedOn(courseDeg(stays, crann), 110)) * 3_600_000)
  // The missed approach leg to BEADS starts from the CA's 439 ft, below the 2,000 ft release: still 70 KIAS.
  const crannToBeads = at('BEADS').eta! - at('CRANN').eta!
  const beadsTas = tasFromIas(70, 439)
  expect(crannToBeads).toBeGreaterThan((distanceNm(crann, unit.coordinates('BEADS')!) / unit.groundSpeedOn(courseDeg(crann, unit.coordinates('BEADS')!), beadsTas + 15)) * 3_600_000)
})

test('in the FMS the limit in force caps the planned speed, and LEGS shows the coded limits', () => {
  const unit = flying(PINS, '87N', 'R190', 'HTO')
  sequenceTo(unit, 'TIDUE')
  expect(unit.procedureSpeed).toBeNull()
  sequenceTo(unit, 'STAYS')
  expect(unit.procedureSpeed).toMatchObject({ kt: 70 })
  expect(unit.plannedSpeed).toBeCloseTo(tasFromIas(70, unit.altitude), 6)
  // LEGS shows the coded limits like speed constraints: 70 at CRANN.
  unit.press('LEGS')
  expect(screenText(unit.screen()).join('\n')).toMatch(/70\/\s*560/)
})

test('under the laboratory airline VNAV profile, where the FMS commands speed, the limit caps the target speed', () => {
  const unit = flying(PINS, '87N', 'R190', 'HTO', LAB_AIRLINE_VNAV_PROFILE)
  sequenceTo(unit, 'CRANN')
  expect(unit.targetSpeed).toBeLessThanOrEqual(tasFromIas(70, unit.altitude) + 1e-9)
})

test('Q7: an approach coding a speed limit below VMINI is refused coupled activation, and stays plannable', () => {
  // 87N as coded (70 kt, above the profile's 50 KIAS VMINI) arms.
  const ok = flying(PINS, '87N', 'R190', 'HTO')
  expect(ok.approachRefusal).toBeNull()
  expect(ok.armApproach(true)).toBe(true)
  expect(ok.approachArmed).toBe(true)
  // The same procedure with the TIDUE limit coded 040: planned and executed, but APPR is refused with the reason.
  const slow = PINS.split('\n').map(line => (/^SUSAH 87N K6FR190  R      010TIDUE/.test(line) ? `${line.slice(0, 99)}040${line.slice(102)}` : line)).join('\n')
  const unit = flying(slow, '87N', 'R190', 'HTO')
  expect(unit.activeRoute.approach).toMatchObject({ ident: 'R190' })
  expect(unit.approachRefusal).toBe('procedure speed limit below profile VMINI (40 < 50 KIAS)')
  expect(unit.armApproach(true)).toBe(false)
  expect(unit.approachArmed).toBe(false)
  expect(screenText(unit.screen()).join('\n')).toContain('SPD LIMIT BELOW VMINI')
  // Pressing APPR off is always allowed.
  expect(unit.armApproach(false)).toBe(false)
})
