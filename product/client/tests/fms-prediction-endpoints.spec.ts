import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { iasFromTas } from '../src/fmsCdu/kinematics'
import { setUpKbtvRnav15 } from '../src/fmsCdu/kbtvDemo'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Stage E (plan section 7, addendum R3-03 and R3-04): where a destination-type prediction ends (INSTRUMENT END at the
// MAP, SITE ARRIVAL over the landing site; no LANDING in v1) and how known it is (KNOWN, CONDITIONAL with its
// assumption, UNKNOWN with the reason), the FUEL and PROGRESS pages stating both, the RTA's required true airspeed from
// the wind triangle compared with the limits in IAS and scoped to the path to its fix, and PLAN DATA.
const COPTER = readFileSync('tests/fixtures/cifp/copter-pins-2609.pc', 'latin1')
const START = Date.UTC(2026, 8, 29, 14, 0, 0)
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : ch === '-' ? 'PLUSMINUS' : (`CHAR_${ch}` as CduFunction))
}
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)

/** The FMS with the Copter fixture active, the route to 87N and its point-in-space approach R190 executed. */
function to87N() {
  const unit = new ScriptedFms(() => new Date(START))
  expect(unit.loadArinc424(COPTER, 'copter-pins-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.press('RTE')
  typeText(unit, '87N')
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'R190')
  unit.press('EXEC')
  return unit
}
const direct = (unit: ScriptedFms, ident: string) => { expect(unit.directTo(ident)).toBeUndefined(); unit.press('EXEC') }

test('R3-03: the point-in-space approach predicts to CRANN as INSTRUMENT END, never the heliport, and says so', () => {
  const unit = to87N()
  direct(unit, 'TIDUE')
  const { endpoint } = unit.profile()
  expect(endpoint).toMatchObject({ kind: 'INSTRUMENT END', label: 'CRANN (MAP)', point: { ident: 'CRANN', status: 'KNOWN' } })
  expect(endpoint!.point.eta).not.toBeNull()
  expect(endpoint!.point.fuel).not.toBeNull()
  unit.press('FUEL')
  expect(lines(unit)[5]).toMatch(/^ CRANN \(MAP\)\s+EFOB $/)
  expect(lines(unit)[6]).toMatch(/^\d{4}Z\s+\d+KG$/)
  expect(lines(unit)[7]).toMatch(/^ INSTR END\s+KNOWN $/)
  expect(lines(unit)[10]).toMatch(/^LANDING NOT MODELLED/)
})

test('vnav.ts:231: the endpoint is the MAP of the executed approach, not the last point before the missed approach elsewhere', () => {
  // The route still holds the demonstration legs to CYUL's runway, then a discontinuity, then the 87N approach. The old
  // rule took the first runway (RW24R, somewhere else entirely) as the destination; the endpoint is CRANN, UNKNOWN
  // past the discontinuity until the crew joins it.
  const unit = to87N()
  const { endpoint } = unit.profile()
  expect(endpoint).toMatchObject({ kind: 'INSTRUMENT END', label: 'CRANN (MAP)', point: { ident: 'CRANN', status: 'UNKNOWN', eta: null, fuel: null } })
  expect(unit.profile().destination?.ident).toBe('CRANN')
})

test('R3-03: a direct-to 87N, still airborne, is SITE ARRIVAL at the heliport, and the landing reserve is unavailable', () => {
  const unit = to87N()
  direct(unit, '87N')
  const profile = unit.profile()
  expect(profile.endpoint).toMatchObject({ kind: 'SITE ARRIVAL', label: '87N', point: { ident: '87N', status: 'KNOWN' } })
  expect(profile.reserve).toEqual({ available: false, reason: 'landing not modelled' })
  unit.press('PROG')
  unit.press('NEXT')
  expect(lines(unit)[3]).toMatch(/^ SITE ARR\s+EFOB $/)
  expect(lines(unit)[4]).toMatch(/^87N\s+\d+KG$/)
  expect(lines(unit)[6]).toMatch(/^KNOWN/)
})

test('R3-03: the KBTV threshold prediction is SITE ARRIVAL (threshold): the label changes, the value does not', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)))
  expect(setUpKbtvRnav15(unit, new FlightSimulator(unit))).toEqual({ ready: true })
  const profile = unit.profile()
  const threshold = profile.points.find(point => point.ident === 'RW15')!
  expect(profile.endpoint).toMatchObject({ kind: 'SITE ARRIVAL', label: 'RW15 (THR)' })
  // The same point, with the same ETA and fuel, as the prediction at the threshold always was.
  expect(profile.endpoint!.point).toBe(threshold)
  expect(threshold.eta).not.toBeNull()
})

test('R3-03: a MANUAL hold makes its fix and everything after it CONDITIONAL (ETA, EFOB and the RTA), with the assumption', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)))
  expect(unit.defineHold('TOLGU')).toBeUndefined()
  unit.press('EXEC')
  const profile = unit.profile()
  const status = (ident: string) => profile.points.find(point => point.ident === ident)!
  expect(status('RDG')).toMatchObject({ status: 'KNOWN', reason: null })
  expect(status('TOLGU')).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT NEXT CROSSING' })
  expect(status('FERDI')).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT NEXT CROSSING' })
  expect(status('FERDI').eta).not.toBeNull()
  expect(profile.endpoint!.point).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT NEXT CROSSING' })
  unit.press('FUEL')
  expect(lines(unit)[7]).toMatch(/^ SITE ARR\s+COND $/)
  expect(lines(unit)[8]).toMatch(/^HOLD EXIT NEXT CROSSING/)
  // The RTA past the hold is computed, and CONDITIONAL; one before it is KNOWN.
  Object.assign(unit.rndz, { wpt: 'FERDI', time: unit.now.getTime() + 60 * 60_000 })
  expect(unit.rendezvous()).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT NEXT CROSSING' })
  expect(unit.rendezvous()!.required).not.toBeNull()
  Object.assign(unit.rndz, { wpt: 'RDG', time: unit.now.getTime() + 20 * 60_000 })
  expect(unit.rendezvous()).toMatchObject({ status: 'KNOWN', reason: null })
  // An exit armed is no longer an assumption.
  unit.changeHold(hold => { hold.status = 'EXIT ARMED' })
  unit.press('EXEC')
  expect(unit.profile().points.find(point => point.ident === 'FERDI')!.status).toBe('KNOWN')
})

test('R3-03: an RTA to a fix ahead of an UNKNOWN segment is computed; one past it is not, with the reason', () => {
  const unit = to87N()
  // RDG is on the connected part of the route; TIDUE is past the discontinuity.
  Object.assign(unit.rndz, { wpt: 'RDG', time: unit.now.getTime() + 20 * 60_000 })
  expect(unit.rendezvous()).toMatchObject({ status: 'KNOWN' })
  expect(unit.rendezvous()!.required).toBeGreaterThan(0)
  Object.assign(unit.rndz, { wpt: 'TIDUE', time: unit.now.getTime() + 90 * 60_000 })
  expect(unit.rendezvous()).toMatchObject({ required: null, status: 'UNKNOWN', achievable: false })
  expect(unit.rendezvous()!.reason).toMatch(/PATH NOT DEFINED|AFTER UNKNOWN SEGMENT/)
  unit.rndz.active = true
  // An RTA with no computed speed commands nothing and raises no alert: the planned speed is flown.
  unit.updatePerformance(1)
  expect(recalled(unit, 'RENDEZVOUS UNACHIEVABLE')).toBe(false)
  expect(unit.targetSpeed).toBe(unit.plannedSpeed)
})

/** An FMS flying directly to MUN, 5 NM ahead on a northerly course, at an altitude it cruises at, in a given wind. */
function fiveMilesFromMun(wind: { direction: number; speed: number }, altitude = 2000) {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)))
  const mun = unit.coordinates('MUN')!
  unit.vnav.cruiseAltitude = altitude
  Object.assign(unit.wind, wind)
  unit.placeAircraft({ position: offset(mun, 180, 5), track: 0, altitude }, 'test: 5 NM south of MUN')
  direct(unit, 'MUN')
  expect(distanceNm(unit.truePosition, mun)).toBeCloseTo(5, 6)
  return unit
}

test('E4: the RTA required true airspeed comes from the wind triangle: 5 NM in 5 minutes across 30 kt needs 67.08 kt', () => {
  // 60 kt over the ground with a pure 30 kt crosswind: sqrt(60^2 + 30^2) = 67.082 kt of true airspeed.
  const unit = fiveMilesFromMun({ direction: 270, speed: 30 })
  Object.assign(unit.rndz, { wpt: 'MUN', time: unit.now.getTime() + 5 * 60_000 })
  const plan = unit.rendezvous()!
  expect(plan.required!).toBeCloseTo(Math.hypot(60, 30), 2)
  expect(plan).toMatchObject({ status: 'KNOWN', achievable: true })
})

test('R3-04: RTA feasibility compares like units: 152 KTAS at 2,000 ft is 147.587 KIAS, feasible; the limits are IAS', () => {
  expect(iasFromTas(152, 2000)).toBeCloseTo(147.587, 3)
  const unit = fiveMilesFromMun({ direction: 0, speed: 0 })
  unit.rndz.maxSpeed = 200
  // 5 NM at 152 kt TAS in still air: 118.4 s.
  Object.assign(unit.rndz, { wpt: 'MUN', time: unit.now.getTime() + (5 / 152) * 3_600_000 })
  const plan = unit.rendezvous()!
  // Within 0.05 kt: the leg length is the route geometry's, a few feet from the placement's great-circle distance.
  expect(plan.required!).toBeCloseTo(152, 1)
  expect(plan.requiredIas!).toBeCloseTo(147.587, 1)
  // A raw 152 > 150 comparison would have refused it.
  expect(plan).toMatchObject({ achievable: true, reason: null })
  // Faster than 150 KIAS, and slower than VMINI (50 KIAS): refused, each with its reason.
  Object.assign(unit.rndz, { time: unit.now.getTime() + (5 / 170) * 3_600_000 })
  expect(unit.rendezvous()).toMatchObject({ achievable: false, reason: 'ABOVE MAX SPEED' })
  unit.rndz.minSpeed = 40
  Object.assign(unit.rndz, { time: unit.now.getTime() + (5 / 45) * 3_600_000 })
  expect(unit.rendezvous()).toMatchObject({ achievable: false, reason: 'BELOW VMINI' })
})

test('E4: an overdue RTA has no computed speed, is shown as such, and raises nothing', () => {
  const unit = fiveMilesFromMun({ direction: 0, speed: 0 })
  Object.assign(unit.rndz, { wpt: 'MUN', time: unit.now.getTime() - 60_000, active: true })
  expect(unit.rendezvous()).toMatchObject({ required: null, reason: 'OVERDUE' })
  unit.press('INIT_REF'); unit.press('NEXT'); unit.press('LSK6R')
  expect(lines(unit)[4]).toMatch(/---\/---KT$/)
  expect(lines(unit)[8]).toMatch(/^OVERDUE/)
  unit.updatePerformance(1)
  expect(recalled(unit, 'RENDEZVOUS UNACHIEVABLE')).toBe(false)
})

test('B1.7: held stationary off the plan, there is NO PROGRESS: no ETA or EFOB ahead, and the pages say why', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)))
  unit.setAircraft({ groundSpeed: 0 })
  const profile = unit.profile()
  expect(profile.points.every(point => point.status === 'UNKNOWN' && point.reason === 'NO PROGRESS' && point.eta === null && point.fuel === null)).toBe(true)
  // The distance is still known: only the time and fuel are not.
  expect(profile.points[0].distance).not.toBeNull()
  // And an RTA across NO PROGRESS computes no speed (plan E4): the reason is shown instead.
  Object.assign(unit.rndz, { wpt: 'RDG', time: unit.now.getTime() + 20 * 60_000 })
  expect(unit.rendezvous()).toMatchObject({ required: null, status: 'UNKNOWN', reason: 'NO PROGRESS' })
  unit.press('FUEL')
  expect(lines(unit)[6]).toMatch(/^-----\s+-----KG$/)
  expect(lines(unit)[7]).toMatch(/^ SITE ARR\s+UNKNOWN $/)
  expect(lines(unit)[8]).toMatch(/^NO PROGRESS/)
})

test('B1.7: a leg the planned airspeed cannot make progress along (headwind at least the TAS) is UNKNOWN from there', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)))
  // A 130 kt wind from 115 degrees: straight on the nose of the first legs, stronger than the 120 kt cruise.
  Object.assign(unit.wind, { direction: 115, speed: 130 })
  const first = unit.profile().points[0]
  expect(first).toMatchObject({ status: 'UNKNOWN', reason: 'NO PROGRESS ON A LEG', eta: null })
})

test('E1: PLAN DATA shows the transition altitude and level, the cruise wind and CRZ TAS 130 for ROTOR, apart from the speed flown', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)))
  unit.press('INIT_REF')
  expect(lines(unit)[12]).toMatch(/PLAN DATA>$/)
  unit.press('LSK6R')
  expect(lines(unit)[0]).toMatch(/^PLAN DATA\s+1\/1$/)
  expect(lines(unit)[1]).toMatch(/^ TRANS ALT\s+CRZ WIND $/)
  expect(lines(unit)[2]).toMatch(/^18000FT\s+000T\/  0KT$/)
  expect(lines(unit)[3]).toMatch(/^ TRANS LVL\s+CRZ TAS $/)
  expect(lines(unit)[4]).toMatch(/^FL180\s+130KT$/)
  const flown = unit.targetSpeed
  enter(unit, '140', 'LSK2R')
  enter(unit, '300/25', 'LSK1R')
  enter(unit, '5000', 'LSK1L')
  enter(unit, '050', 'LSK2L')
  expect(unit.planData).toEqual({ transAlt: 5000, transLevel: 50, cruiseWind: { direction: 300, speed: 25 }, cruiseTas: 140 })
  // Planning data only: the speed flown and the system wind are unchanged.
  expect(unit.targetSpeed).toBe(flown)
  expect(unit.wind).toEqual({ direction: 270, speed: 12 })
  enter(unit, '999', 'LSK2R')
  expect(unit.planData.cruiseTas).toBe(140)
  unit.press('LSK6L')
  expect(lines(unit)[0]).toMatch(/^INIT\/REF INDEX/)
})
