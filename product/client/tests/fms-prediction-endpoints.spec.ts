import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm, hhmm, offset, type Hold } from '../src/fmsCdu/fmsModel'
import { iasFromTas, predictedGroundSpeed, tasFromIas } from '../src/fmsCdu/kinematics'
import type { ProcedureHold } from '../src/fmsCdu/navData'
import { holdAllowance, holdPathToPassage, piecesHours } from '../src/fmsCdu/predictions'
import { setUpKbtvRnav15 } from '../src/fmsCdu/kbtvDemo'
import { LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { computeProfile, parseConstraint } from '../src/fmsCdu/vnav'
import { aircraftData, fmsOutputs } from '../src/fmsCdu/efis'
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

// ---------------------------------------------------------------------------------------------- hold time (D-H, R3-03)

/**
 * The 87N route with the HTO transition (TF TIDUE, the HF course reversal at TIDUE: ONCE, 4 NM, left turns, inbound
 * 176), the aircraft `nm` NM from TIDUE on `bearing`, flying directly to it, at 2,000 ft in the given wind.
 */
function towardTidue(bearing: number, nm: number, wind = { direction: 0, speed: 0 }, clock = () => new Date(START)) {
  const unit = new ScriptedFms(clock)
  expect(unit.loadArinc424(COPTER, 'copter-pins-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.press('RTE')
  typeText(unit, '87N')
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'R190', 'HTO')
  unit.press('EXEC')
  Object.assign(unit.wind, wind)
  const tidue = unit.coordinates('TIDUE')!
  unit.placeAircraft({ position: offset(tidue, bearing, nm), track: (bearing + 180) % 360, altitude: 2000 }, 'test: toward TIDUE')
  direct(unit, 'TIDUE')
  const leg = unit.activeRoute.legs[0]
  expect(leg).toMatchObject({ kind: 'wpt', ident: 'TIDUE', hold: { path: 'HF', exit: 'ONCE', legDistanceNm: 4, turn: 'LEFT', inbound: 176 } })
  return unit
}
const point = (unit: ScriptedFms, ident: string) => unit.profile().points.find(p => p.ident === ident)!
/** Seconds from `a` to `b` in the predictions, less the time of the legs between them at `tas` in still air. */
const heldSeconds = (unit: ScriptedFms, a: string, b: string, tas: number) => {
  const from = point(unit, a), to = point(unit, b)
  return (to.eta! - from.eta!) / 1000 - ((to.distance! - from.distance!) / tas) * 3600
}
// Independent of holds.ts: at rate one (3 degrees a second) the turn radius is the speed over the turn rate,
// r = V / (60·π) NM for V in knots, and a half turn is π·r = V / 60 NM. At 120 kt rate one needs about 18 degrees of
// bank, inside the 25 degree limit. The HF's holding speed (100 KIAS at 1,700 ft) is below the 120 kt planned TAS.
const TAS = 120
const RADIUS = TAS / (60 * Math.PI)
// The leg on from TIDUE is flown under the 70 KIAS limit in force from TIDUE (Astra F2), as TAS at TIDUE's 1,800 ft.
const FINAL_TAS = tasFromIas(70, 1800)
const STILL = { direction: 0, speed: 0 }

test('D-H/R3-03: an HF (ONCE) with a direct entry is KNOWN, and the predictions past it add one racetrack', () => {
  const unit = towardTidue(356, 8)
  expect(unit.plannedSpeed).toBe(TAS)
  expect(tasFromIas(100, 1700)).toBeLessThan(TAS)
  // The fix is predicted at its first arrival; after it, one racetrack of two 4 NM legs and two half turns, still air.
  expect(point(unit, 'TIDUE').eta).toBe(START + ((point(unit, 'TIDUE').distance! / TAS) * 3600_000))
  expect(heldSeconds(unit, 'TIDUE', 'STAYS', FINAL_TAS)).toBeCloseTo(((2 * 4 + 2 * Math.PI * RADIUS) / TAS) * 3600, 1)
  expect(heldSeconds(unit, 'TIDUE', 'STAYS', FINAL_TAS)).toBeCloseTo(360, 1)
  for (const ident of ['TIDUE', 'STAYS', 'CRANN']) expect(point(unit, ident)).toMatchObject({ status: 'KNOWN', reason: null })
  expect(unit.profile().endpoint!.point).toMatchObject({ ident: 'CRANN', status: 'KNOWN' })
  // A crew hold at TIDUE (MANUAL) takes the HF's place: the crew's exit, assumed at the next crossing, adds no time.
  const crew = towardTidue(356, 8)
  expect(crew.defineHold('TIDUE')).toBeUndefined()
  crew.press('EXEC')
  expect(point(crew, 'STAYS')).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT NEXT CROSSING' })
  expect(heldSeconds(crew, 'TIDUE', 'STAYS', FINAL_TAS)).toBeCloseTo(0, 3)
  // The fuel burned in the hold counts too.
  const burned = point(unit, 'TIDUE').fuel! - point(unit, 'STAYS').fuel!
  expect(burned).toBeCloseTo(unit.fuel.flow * ((point(unit, 'STAYS').eta! - point(unit, 'TIDUE').eta!) / 3600_000), 6)
})

test('D-H/R3-03: after a teardrop or parallel entry an HF adds only the entry, as the flight leaves where the entry ends', () => {
  // Teardrop: 4 NM out at 40 degrees off the outbound course, a half turn, and back from abeam its end (4·cos 40).
  const unit = towardTidue(200, 8)
  expect(heldSeconds(unit, 'TIDUE', 'STAYS', FINAL_TAS)).toBeCloseTo(((4 + Math.PI * RADIUS + 4 * Math.cos((40 * Math.PI) / 180)) / TAS) * 3600, 3)
  expect(point(unit, 'STAYS').status).toBe('KNOWN')
  // Parallel: 2.6 turn radii out on the outbound course, a half turn, and the same distance back.
  const parallel = towardTidue(120, 8)
  expect(heldSeconds(parallel, 'TIDUE', 'STAYS', FINAL_TAS)).toBeCloseTo(((2 * 2.6 * RADIUS + Math.PI * RADIUS) / TAS) * 3600, 3)
})

test('D-H/R3-03: the predicted time through the HF agrees with the time flown, for each entry (laboratory, still air)', () => {
  for (const [bearing, entry] of [[356, 'DIRECT'], [200, 'TEARDROP'], [120, 'PARALLEL']] as const) {
    let now = START
    const unit = towardTidue(bearing, 1.5, undefined, () => new Date(now))
    const predicted = (point(unit, 'STAYS').eta! - START) / 1000
    const sim = new FlightSimulator(unit)
    let entered: string | null = null, flown = 0
    for (; flown < 1800; flown++) {
      now += 1000
      sim.step(1)
      entered ??= unit.holdEntryFlown
      const leg = unit.activeRoute.legs[0]
      // Past TIDUE the crew flies the procedure's 70 KIAS limit, as the predictions assume (the FMS only advises).
      if (leg?.kind === 'wpt' && leg.ident === 'STAYS' && sim.selectedSpeed !== 70) sim.selectSpeed(70)
      if (leg?.kind === 'wpt' && leg.ident === 'CRANN') break
    }
    expect(entered, `from ${bearing}`).toBe(entry)
    // The flight flies the helicopter's selected IAS (a little faster as TAS) and turns at its own bank: a still-air
    // mean, within 10 percent of the flown time.
    expect(Math.abs(flown - predicted) / flown, `from ${bearing}: predicted ${predicted}s, flown ${flown}s`).toBeLessThan(0.1)
  }
})

test('D-H/R3-03: an AT TGT ALT hold is KNOWN plus a racetrack when the altitude predicted there meets it, else CONDITIONAL', () => {
  const met = towardTidue(356, 8)
  const hold = (met.activeRoute.legs[0] as { hold: ProcedureHold }).hold
  // The HF's altitude is not an exit condition: ONCE stays KNOWN whatever it says.
  hold.altitude = '5000A'
  expect(point(met, 'STAYS')).toMatchObject({ status: 'KNOWN', reason: null })
  Object.assign(hold, { path: 'HA', exit: 'AT ALT', altitude: '1700A' })
  expect(point(met, 'TIDUE').altitude).toBe(2000)
  expect(point(met, 'STAYS')).toMatchObject({ status: 'KNOWN', reason: null })
  expect(heldSeconds(met, 'TIDUE', 'STAYS', FINAL_TAS)).toBeCloseTo(360, 1)
  // 5,000 ft or above is not predicted at TIDUE: the exit there is an assumption, from the fix on.
  hold.altitude = '5000A'
  for (const ident of ['TIDUE', 'STAYS', 'CRANN']) expect(point(met, ident)).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT AT ALTITUDE' })
  expect(point(met, 'CRANN').eta).not.toBeNull()
  expect(met.profile().endpoint!.point).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT AT ALTITUDE' })
  met.press('FUEL')
  expect(lines(met)[7]).toMatch(/^ INSTR END\s+COND $/)
  expect(lines(met)[8]).toMatch(/^HOLD EXIT AT ALTITUDE/)
  // At or below 2,000 is met too; a target that cannot be read is never predicted met.
  hold.altitude = '2500B'
  expect(point(met, 'STAYS').status).toBe('KNOWN')
  hold.altitude = '1500B'
  expect(point(met, 'STAYS')).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT AT ALTITUDE' })
  hold.altitude = ''
  expect(point(met, 'STAYS')).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT AT ALTITUDE' })
})

test('D-H/R3-03: a hold that cannot be flown (the wind at least the TAS) leaves everything past its fix UNKNOWN: UNABLE HOLD', () => {
  // A tailwind on the course to TIDUE and on from it: the legs make progress; only the hold cannot be flown.
  const flyable = towardTidue(356, 8, { direction: 356, speed: TAS - 1 })
  expect(point(flyable, 'STAYS')).toMatchObject({ status: 'KNOWN' })
  const unit = towardTidue(356, 8, { direction: 356, speed: TAS })
  expect(point(unit, 'TIDUE')).toMatchObject({ status: 'KNOWN', reason: null })
  expect(point(unit, 'TIDUE').eta).not.toBeNull()
  for (const ident of ['STAYS', 'CRANN']) expect(point(unit, ident)).toMatchObject({ status: 'UNKNOWN', reason: 'UNABLE HOLD', eta: null, fuel: null })
  expect(unit.profile().endpoint!.point).toMatchObject({ status: 'UNKNOWN', reason: 'UNABLE HOLD' })
  // An RTA past it computes nothing and says why.
  Object.assign(unit.rndz, { wpt: 'CRANN', time: unit.now.getTime() + 60 * 60_000 })
  expect(unit.rendezvous()).toMatchObject({ required: null, status: 'UNKNOWN', reason: 'UNABLE HOLD', achievable: false })
})

test('D-H/R3-04: an RTA past a hold that leaves by itself flies the legs in the time the hold leaves', () => {
  const unit = towardTidue(356, 8)
  // 15 minutes to STAYS, 6 of them in the hold: the 11 NM of legs in the other 9.
  Object.assign(unit.rndz, { wpt: 'STAYS', time: unit.now.getTime() + 15 * 60_000 })
  const legs = point(unit, 'STAYS').distance!
  expect(unit.rendezvous()!.required!).toBeCloseTo(legs / (9 / 60), 1)
  expect(unit.rendezvous()).toMatchObject({ status: 'KNOWN' })
  // To TIDUE itself the hold does not count: it is flown after the fix.
  Object.assign(unit.rndz, { wpt: 'TIDUE', time: unit.now.getTime() + 6 * 60_000 })
  expect(unit.rendezvous()!.required!).toBeCloseTo(point(unit, 'TIDUE').distance! / (6 / 60), 3)
})

test('D-H/MISSED-HOLD: the missed-approach hold is timed for one racetrack after its entry, not held for a crew exit', () => {
  const fix = { lat: 44, lon: -73 }
  const missed: Hold = { fix: 'BEADS', turn: 'RIGHT', inbound: 222, legTime: null, legDistance: 4, exit: 'MANUAL', speed: 90, altitude: '2000A', status: 'ARMED', missed: true }
  // Direct entry: one racetrack (90 KIAS at 2,000 ft is below the 120 kt TAS). Hours to 6 places: holds.ts converts
  // through feet and g, which the rate-one radius here does not.
  expect(holdAllowance(missed, fix, 222, TAS, STILL, 2000)).toMatchObject({ entry: 'DIRECT', racetracks: 1 })
  expect(holdAllowance(missed, fix, 222, TAS, STILL, 2000)!.hours!).toBeCloseTo((2 * 4 + 2 * Math.PI * RADIUS) / TAS, 4)
  // A teardrop entry (arriving on the inbound course's reciprocal, turning right: 180 relative) and then one racetrack.
  const teardrop = holdAllowance(missed, fix, 42, TAS, STILL, 2000)!
  expect(teardrop).toMatchObject({ entry: 'TEARDROP', racetracks: 1 })
  expect(teardrop.hours!).toBeCloseTo((4 + Math.PI * RADIUS + 4 * Math.cos((40 * Math.PI) / 180) + 2 * 4 + 2 * Math.PI * RADIUS) / TAS, 4)
  // In progress: the racetrack still to fly, then nothing once one is flown.
  expect(holdAllowance({ ...missed, status: 'IN PROGRESS', circuits: 0 }, fix, 42, TAS, STILL, 2000)).toMatchObject({ entry: null, racetracks: 1 })
  expect(holdAllowance({ ...missed, status: 'IN PROGRESS', circuits: 1 }, fix, 42, TAS, STILL, 2000)).toMatchObject({ hours: 0, racetracks: 0 })
  // Its exit armed (EXIT HOLD, then not resumed): out at the next crossing, nothing more.
  expect(holdAllowance({ ...missed, status: 'EXIT ARMED', circuits: 0 }, fix, 42, TAS, STILL, 2000)).toMatchObject({ hours: 0, racetracks: 0 })
  // Resumed (MANUAL, no longer the missed-approach hold): the crew's exit, not timed here (HOLD EXIT NEXT CROSSING).
  expect(holdAllowance({ ...missed, missed: false }, fix, 222, TAS, STILL, 2000)).toBeNull()
  // A holding speed faster than the planned TAS sizes and times the pattern: 150 KIAS at 2,000 ft as TAS.
  const fast = tasFromIas(150, 2000)
  expect(holdAllowance({ ...missed, speed: 150 }, fix, 222, TAS, STILL, 2000)!.hours!).toBeCloseTo((2 * 4 + 2 * (fast / 60)) / fast, 4)
  // The wind at least that TAS: UNABLE HOLD.
  expect(holdAllowance(missed, fix, 222, TAS, { direction: 0, speed: TAS }, 2000)).toEqual({ hours: null, reason: 'UNABLE HOLD' })

  // On the route: the armed missed-approach hold (after a go-around at 87N) is KNOWN at its fix, not CONDITIONAL.
  const unit = towardTidue(356, 8)
  expect(unit.goAround()).toBe(true)
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'BEADS', exit: 'MANUAL', missed: true })
  // (Its leg is estimated, after the missed approach's conditional leg: CONDITIONAL for that, not for the hold.)
  expect(point(unit, 'BEADS').reason).not.toBe('HOLD EXIT NEXT CROSSING')
  expect(point(unit, 'BEADS')).toMatchObject({ status: 'CONDITIONAL', reason: 'LEG ESTIMATED' })
})

// ---------------------------------------------------------------------------------------------- after the MAP; VNAV 1/3

test('R190: after the MAP the predicted climb levels at the missed approach altitude (BEADS 2000A), not the 4,500 ft cruise', () => {
  const unit = towardTidue(356, 8)
  expect(unit.vnav.cruiseAltitude).toBe(4500)
  expect(unit.activeRoute.legs.find(leg => leg.kind === 'wpt' && leg.ident === 'BEADS')).toMatchObject({ source: 'MISSED', altitude: '2000A' })
  expect(point(unit, 'BEADS').altitude).toBe(2000)
  expect(point(unit, 'BEADS').constraintMet).toBe(true)
  // Before the MAP nothing changes: the approach's own points keep their predictions (at or below the aircraft).
  expect(point(unit, 'CRANN').altitude).toBeLessThanOrEqual(2000)
})

test('the laboratory airline VNAV flying a missed approach climbs to its missed approach altitude (2000A), not to cruise', () => {
  // R190 on the airline profile: TOGA drops the rest of the approach, so the missed approach is the active route.
  const unit = new ScriptedFms(() => new Date(START), { profile: LAB_AIRLINE_VNAV_PROFILE })
  expect(unit.loadArinc424(COPTER, 'copter-pins-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  unit.swapCycles()
  unit.press('RTE')
  typeText(unit, '87N')
  unit.press('LSK1R')
  unit.press('EXEC')
  unit.selectProcedure('APPROACH', 'R190', 'HTO')
  unit.press('EXEC')
  const stays = unit.coordinates('STAYS')!
  unit.placeAircraft({ position: offset(stays, 356, 2), track: 176, altitude: 1700 }, 'test: on the R190 final')
  direct(unit, 'STAYS')
  expect(unit.goAround()).toBe(true)
  expect(unit.activeRoute.legs.every(leg => leg.kind === 'disco' || leg.source === 'MISSED')).toBe(true)
  // Its only coded altitude is at or above (BEADS 2000A), which caps nothing by itself: VNAV used to climb to cruise.
  expect(unit.vnav.cruiseAltitude).toBe(4500)
  expect(unit.profile().climbCap).toBe(2000)
  expect(point(unit, 'BEADS').altitude).toBe(2000)
})

test('VNAV 1/3 on a point-in-space approach says there is no vertical path (LNAV) and where it ends, not that there is no approach', () => {
  const unit = towardTidue(356, 8)
  unit.press('VNAV')
  const screen = lines(unit)
  expect(screen[0]).toMatch(/^\s*VNAV\s+1\/3/)
  expect(screen[2]).toMatch(/^\s*NO VERTICAL PATH \(LNAV\)\s*$/)
  expect(screen[4]).toMatch(/^\s*TO CRANN \(MAP\)\s*$/)
  expect(screen.join('\n')).not.toContain('NO APPROACH IN ROUTE')
  // With no approach in the route at all, it still says so, even flying to the heliport (a prediction endpoint, SITE
  // ARRIVAL at 87N, but no approach).
  const none = new ScriptedFms(() => new Date(START))
  expect(none.loadArinc424(COPTER, 'copter-pins-2609.pc')).toMatchObject({ loaded: 'CIFP2609' })
  none.swapCycles()
  none.modify(route => { route.legs = []; route.dest = '87N' })
  none.press('EXEC')
  direct(none, '87N')
  expect(none.profile().endpoint).toMatchObject({ kind: 'SITE ARRIVAL', label: '87N' })
  expect(none.approachType).toBeNull()
  none.press('VNAV')
  expect(lines(none)[2]).toMatch(/^\s*NO APPROACH IN ROUTE\s*$/)
})

test('KBTV R15 is unchanged: VNAV 1/3 shows its runway path, and its missed approach tops out at its own altitude', () => {
  const unit = new ScriptedFms(() => new Date(START))
  const sim = new FlightSimulator(unit)
  expect(setUpKbtvRnav15(unit, sim)).toEqual({ ready: true })
  unit.press('VNAV')
  expect(lines(unit)[0]).toMatch(/^ACT VNAV 15 LPV\s+1\/3/)
  expect(lines(unit).join('\n')).not.toContain('NO VERTICAL PATH')
  const missedTop = Math.max(...unit.activeRoute.legs.flatMap(leg => (leg.kind === 'wpt' && leg.source === 'MISSED' && leg.altitude ? [Number(/^(\d+)/.exec(leg.altitude)![1])] : [])))
  expect(Number.isFinite(missedTop)).toBe(true)
  const threshold = unit.profile().points.findIndex(p => p.ident === 'RW15')
  expect(threshold).toBeGreaterThan(0)
  for (const p of unit.profile().points.slice(threshold + 1)) if (p.altitude !== null) expect(p.altitude, p.ident).toBeLessThanOrEqual(missedTop)
})

// ---------------------------------------------------------------------------------------------- the missed approach target (F4)

test('F4: the missed approach altitude is its own planning target, above cruise as well as below it', () => {
  // A climb after the MAP over 20 NM at 120 kt (10 minutes at 1,000 fpm): enough to reach either target from 1,000 ft.
  const run = (missed: string) => computeProfile({
    waypoints: [
      { ident: 'MAP', legDistance: 2, groundSpeed: 120, constraint: null, endOfDescent: false },
      { ident: 'MA1', legDistance: 10, groundSpeed: 120, constraint: null, endOfDescent: false, missed: true },
      { ident: 'MAHF', legDistance: 10, groundSpeed: 120, constraint: parseConstraint(missed), endOfDescent: false, missed: true },
    ],
    altitude: 1000, cruiseAltitude: 4500, climbRate: 1000, pathAngle: 3, fuel: 1000, fuelFlow: 500, now: START, phase: 'CLIMB',
  })
  const above = run('5600A')
  expect(above.points.find(p => p.ident === 'MAHF')).toMatchObject({ altitude: 5600, constraintMet: true })
  expect(above.missedTarget).toEqual({ kind: 'A', altitude: 5600 })
  // Cruise does not cap the climb on the way either: the point before the holding fix is already above it.
  expect(above.points.find(p => p.ident === 'MA1')!.altitude).toBeGreaterThan(4500)
  const below = run('2000A')
  expect(below.points.find(p => p.ident === 'MAHF')).toMatchObject({ altitude: 2000, constraintMet: true })
  expect(below.points.find(p => p.ident === 'MA1')!.altitude).toBe(2000)
})

test('F4: KBTV R15 predicts YUNUD at its 5600A, not at the 4,500 ft cruise', () => {
  const unit = new ScriptedFms(() => new Date(START))
  const sim = new FlightSimulator(unit)
  expect(setUpKbtvRnav15(unit, sim)).toEqual({ ready: true })
  expect(unit.vnav.cruiseAltitude).toBe(4500)
  const yunud = point(unit, 'YUNUD')
  expect(yunud.altitude!).toBeGreaterThan(4500)
  expect(yunud.altitude!).toBeLessThanOrEqual(5600)
  expect(unit.profile().missedTarget).toEqual({ kind: 'A', altitude: 5600 })
})

test('F4: the airline VNAV flying the KBTV missed approach climbs to 5600, above its cruise', () => {
  const unit = new ScriptedFms(() => new Date(START), { profile: LAB_AIRLINE_VNAV_PROFILE })
  expect(setUpKbtvRnav15(unit)).toEqual({ ready: true })
  const final = unit.activeRoute.legs.findIndex(leg => leg.kind === 'wpt' && /^RW15/.test(leg.ident))
  expect(final).toBeGreaterThan(0)
  expect(unit.goAround()).toBe(true)
  expect(unit.activeRoute.legs[0]).toMatchObject({ source: 'MISSED' })
  expect(unit.vnav.cruiseAltitude).toBe(4500)
  expect(unit.profile().climbCap).toBe(5600)
})

test('F4: under the helicopter profile a selected altitude below the missed approach altitude is shown, never flown instead', () => {
  const unit = new ScriptedFms(() => new Date(START))
  const sim = new FlightSimulator(unit)
  expect(setUpKbtvRnav15(unit, sim)).toEqual({ ready: true })
  expect(sim.advisory).toBe(true)
  // On the approach, a selection below 5600A is shown.
  expect(unit.flightPhase).toBe('APPROACH')
  sim.selectAltitude(4500)
  expect(sim.missedAltitudeConflict).toEqual({ target: { kind: 'A', altitude: 5600 }, selected: 4500 })
  expect(aircraftData(unit, sim).missedAltitudeConflict).toBe('5600A')
  // The crew's selection stays what the go-around climbs to: the FMS takes over nothing.
  expect(sim.engageGoAround()).toBe(true)
  expect(sim.selectedAltitude).toBe(4500)
  expect(sim.missedAltitudeConflict).not.toBeNull()
  // Reselecting the missed approach altitude clears it; so does anything above an at-or-above target.
  sim.selectAltitude(5600)
  expect(sim.missedAltitudeConflict).toBeNull()
  sim.selectAltitude(6000)
  expect(sim.missedAltitudeConflict).toBeNull()
  // With the FMS failed its missed approach data is gone, and so is the caption.
  sim.selectAltitude(4500)
  unit.setCondition('fmsFail', true)
  expect(sim.missedAltitudeConflict).toBeNull()
  // The airline profile's VNAV flies the missed approach altitude itself: there is no selection to conflict with.
  const airline = new ScriptedFms(() => new Date(START), { profile: LAB_AIRLINE_VNAV_PROFILE })
  const airlineSim = new FlightSimulator(airline)
  expect(setUpKbtvRnav15(airline, airlineSim)).toEqual({ ready: true })
  expect(airlineSim.missedAltitudeConflict).toBeNull()
  expect(aircraftData(airline, airlineSim).missedAltitudeConflict).toBeNull()
})

// ---------------------------------------------------------------------------------------------- the manual hold's next crossing (F1)

test('F1: a leg into the wind and back takes d/(V−W) + d/(V+W), longer than 2d/V, and a hold path is timed piece by piece', () => {
  // Two 4 NM legs, out along 270 and back along 090, at 120 kt TAS in a 30 kt wind from 270.
  const wind = { direction: 270, speed: 30 }
  const hours = piecesHours([{ distance: 4, course: 270 }, { distance: 4, course: 90 }], course => predictedGroundSpeed(120, course, wind))!
  expect(hours * 3600).toBeCloseTo((4 / 90 + 4 / 150) * 3600, 6)
  expect(hours).toBeGreaterThan(8 / 120)
  // The path left from abeam the middle of an inbound line is half of it; a whole turn is π·r, in pieces of 5 degrees.
  const fix = { lat: 45, lon: -75 }
  const from = offset(fix, 270, 4)
  const line = { kind: 'line' as const, from, to: fix }
  const half = holdPathToPassage({ segments: [line], index: 0, passageAt: 0 }, offset(fix, 270, 2))
  expect(half.reduce((sum, p) => sum + p.distance, 0)).toBeCloseTo(2, 3)
  const centre = offset(fix, 0, 1)
  const arc = { kind: 'arc' as const, centre, to: offset(centre, 0, 1), turn: 'R' as const, radius: 1 }
  const turn = holdPathToPassage({ segments: [arc], index: 0, passageAt: 0 }, fix)
  expect(turn.reduce((sum, p) => sum + p.distance, 0)).toBeCloseTo(Math.PI, 3)
  expect(turn.length).toBe(36)
})

test('F1: in a manual hold the next crossing is predicted along the pattern still to fly, and the flown crossing meets it', () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  Object.assign(unit.wind, { direction: 270, speed: 30 })
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, until?: () => boolean) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); if (until?.()) return t } return seconds }
  expect(unit.defineHold('RDG')).toBeUndefined()
  unit.press('EXEC')
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'RDG', exit: 'MANUAL' })
  expect(fly(3600, () => unit.activeRoute.hold?.status === 'IN PROGRESS')).toBeLessThan(3600)
  const rdg = unit.coordinates('RDG')!
  // From in the first turn (30 s after the fix), and from the outbound leg (a minute on), as Astra measured it.
  for (const wait of [30, 60]) {
    fly(wait)
    const predicted = point(unit, 'RDG')
    expect(predicted).toMatchObject({ status: 'CONDITIONAL', reason: 'HOLD EXIT NEXT CROSSING' })
    // The distance shown stays the direct distance to the fix; only the time follows the pattern.
    expect(predicted.distance!).toBeCloseTo(distanceNm(unit.position, rdg), 2)
    // The RTA follows the same path: an RTA at the predicted crossing needs about the airspeed being flown, and the
    // fuel predicted there is what that time burns.
    Object.assign(unit.rndz, { wpt: 'RDG', time: predicted.eta! })
    expect(Math.abs(unit.rendezvous()!.required! - unit.trueAirspeed!)).toBeLessThan(1)
    expect(predicted.fuel!).toBeCloseTo(unit.fuel.quantity - ((predicted.eta! - now) / 3_600_000) * unit.fuel.flow, 3)
    Object.assign(unit.rndz, { wpt: null, time: null })
    // The pages and the output bus show the same crossing: the HOLD page's FIX ETA, PROGRESS 1/4's ETA to the active
    // fix and the bus ETA, not the straight line at the closure speed (which, turning away from the fix, has no time).
    expect(fmsOutputs(unit, sim).eta).toEqual({ status: 'NORMAL', value: predicted.eta })
    const crossing = hhmm(new Date(predicted.eta!))
    unit.press('HOLD')
    expect(lines(unit)[4]).toMatch(new RegExp(`${crossing.replace('.', '\\.')}$`))
    unit.press('PROG')
    expect(lines(unit)[2]).toMatch(new RegExp(`^RDG\\s+\\d+\\.\\dNM ${crossing.replace('.', '\\.')}$`))
    const circuits = unit.activeRoute.hold!.circuits ?? 0
    const start = now
    expect(fly(900, () => (unit.activeRoute.hold?.circuits ?? 0) > circuits)).toBeLessThan(900)
    const flownSeconds = (now - start) / 1000, predictedSeconds = (predicted.eta! - start) / 1000
    // The direct shortcut would predict a few tens of seconds; the flown crossing is minutes away.
    expect(predictedSeconds).toBeGreaterThan(90)
    expect(Math.abs(predictedSeconds - flownSeconds), `predicted ${predictedSeconds.toFixed(1)} s, flown ${flownSeconds} s`).toBeLessThan(6)
  }
})

test('E4: RTA WIND is the system wind unless the crew enters one; an entry changes only the RTA, and DELETE restores it (M300 A-141)', () => {
  // 5 NM south of MUN, northbound, still air: MUN in 5 minutes needs 60 kt.
  const unit = fiveMilesFromMun({ direction: 0, speed: 0 })
  Object.assign(unit.rndz, { wpt: 'MUN', time: unit.now.getTime() + 5 * 60_000 })
  const eta = unit.profile().points[0].eta
  expect(unit.rendezvous()!.required!).toBeCloseTo(60, 1)
  unit.press('INIT_REF'); unit.press('NEXT'); unit.press('LSK6R')
  expect(lines(unit)[0]).toMatch(/RENDEZVOUS/)
  expect(lines(unit)[9]).toMatch(/^ RTA WIND/)
  expect(lines(unit)[10]).toMatch(/^000T\/  0KT/)
  // The crew's RTA wind, 30 kt on the nose: the RTA needs 90 kt. The system wind, and the ETAs flown in it, are unchanged.
  enter(unit, '360/30', 'LSK5L')
  expect(unit.rndz.wind).toEqual({ direction: 0, speed: 30 })
  expect(unit.rendezvous()!.required!).toBeCloseTo(90, 1)
  expect(unit.wind).toEqual({ direction: 0, speed: 0 })
  expect(unit.profile().points[0].eta).toBe(eta)
  expect(lines(unit)[10]).toMatch(/^000T\/ 30KT/)
  expect(unit.screen()[10][0].size).toBe('large')
  // An entry out of range is refused and changes nothing.
  enter(unit, '090/250', 'LSK5L')
  expect(unit.rndz.wind).toEqual({ direction: 0, speed: 30 })
  // DELETE brings the default back: the system wind, which the RTA then follows. (CLR clears the message, then the
  // entry a character at a time; CLR on the empty scratchpad arms DELETE.)
  for (let i = 0; i < 12 && lines(unit)[13].trim() !== 'DELETE'; i += 1) unit.press('CLR')
  expect(lines(unit)[13].trim()).toBe('DELETE')
  unit.press('LSK5L')
  expect(unit.rndz.wind).toBeNull()
  expect(unit.rendezvous()!.required!).toBeCloseTo(60, 1)
  Object.assign(unit.wind, { direction: 0, speed: 20 })
  expect(unit.rendezvous()!.required!).toBeCloseTo(80, 1)
  expect(lines(unit)[10]).toMatch(/^000T\/ 20KT/)
})
