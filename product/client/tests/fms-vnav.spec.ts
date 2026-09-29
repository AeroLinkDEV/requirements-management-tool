import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import { coldTemperatureCorrection, computeProfile, formatConstraint, parseAltitude, parseConstraint } from '../src/fmsCdu/vnav'
import type { CduFunction } from '../src/fmsCdu/variants'

// VNAV and performance, per airline FMS practice (the FMS test bench research roadmap): speed and altitude constraints,
// the planned profile with its top and end of descent, climbs that respect "at or below" constraints, DES NOW,
// winds, ETA and fuel predictions with the fuel alerts, and cold temperature correction.
// These run the laboratory airline-style VNAV profile: VNAV is its behaviour, not the helicopter profile's, where
// the crew flies the vertical axis.
const setup = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now), { profile: LAB_AIRLINE_VNAV_PROFILE })
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => {
    for (let t = 0; t < seconds; t += 1) {
      now += 1000
      sim.step(1)
      if (each?.()) return t
    }
    return seconds
  }
  return { unit, sim, fly }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : `CHAR_${ch}`)
}
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trimEnd()
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }

test('altitude entries follow the FMS rules: three digits are hundreds as an entry, flight levels, and constraint windows', () => {
  expect(parseAltitude('050', true)).toBe(5000)
  expect(parseAltitude('168')).toBe(168)
  expect(parseAltitude('FL080')).toBe(8000)
  expect(parseAltitude('FL80')).toBe(8000)
  expect(parseConstraint('5000A')).toEqual({ kind: 'A', altitude: 5000 })
  expect(parseConstraint('FL120B')).toEqual({ kind: 'B', altitude: 12000 })
  expect(parseConstraint('7000B5000A')).toEqual({ kind: 'WINDOW', lower: 5000, upper: 7000 })
  expect(parseConstraint('5000A7000B')).toEqual({ kind: 'WINDOW', lower: 5000, upper: 7000 })
  expect(parseConstraint('5000B7000A')).toBeNull()
  expect(formatConstraint({ kind: 'AT', altitude: 20000 })).toBe('FL200')
  expect(formatConstraint({ kind: 'WINDOW', lower: 5000, upper: 7000 })).toBe('7000B5000A')
})

test('speed and altitude constraints are entered on the right of LEGS as a modification', () => {
  const unit = new ScriptedFms(undefined, { profile: LAB_AIRLINE_VNAV_PROFILE })
  unit.press('LEGS')
  enter(unit, '100/050B', 'LSK2R')
  expect(lines(unit)[4]).toMatch(/^RDG\s+100\/5000B$/)
  expect(lines(unit)[0]).toMatch(/^MOD RTE 1 LEGS/)
  enter(unit, '7000B5000A', 'LSK3R')
  expect(lines(unit)[6]).toMatch(/7000B5000A$/)
  enter(unit, '10/', 'LSK1R')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  press(unit, 'CLR', 'CLR', 'CLR', 'CLR', 'CLR', 'LSK2R')
  expect(lines(unit)[4]).toMatch(/^RDG\s+-----$/)
})

test('the profile puts the top of descent where a three-degree path from the first descent constraint reaches cruise, and predicts each waypoint', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)), { profile: LAB_AIRLINE_VNAV_PROFILE })
  const profile = unit.profile()
  expect(profile.endOfDescent).toBe('RW24R')
  const [mun, rdg, , demel, alnit, ulida, ferdi, runway] = profile.points
  expect(mun.altitude).toBe(3000)
  expect(rdg.altitude).toBe(4500)
  // The downwind at 3000, the base down to 2500, the FAF at 1500 and the runway: each at its constraint.
  expect(demel.altitude).toBe(3000)
  expect(alnit.altitude).toBe(3000)
  expect(ulida.altitude).toBe(2500)
  expect(ferdi.altitude).toBe(1500)
  expect(runway.altitude).toBe(168)
  // DEMEL, 3000, is the first constraint below cruise: the path descends 1500 ft to it.
  expect(demel.distance - profile.topOfDescent!).toBeCloseTo((4500 - 3000) / (6076.12 * Math.tan((3 * Math.PI) / 180)), 1)
  // ETA and fuel fall along the route at the ground speed and fuel flow.
  expect(rdg.eta).toBeGreaterThan(mun.eta)
  expect(rdg.fuel).toBeLessThan(mun.fuel)
  expect(mun.fuel).toBeCloseTo(1850 - (540 * (mun.eta - Date.UTC(2026, 8, 27, 14, 0, 0))) / 3_600_000, 0)
  press(unit, 'VNAV', 'NEXT')
  expect(lines(unit)[0]).toMatch(/^ACT VNAV CRZ\s+2\/3$/)
  expect(lines(unit)[6]).toMatch(/^\d+\.\dNM \d{4}Z\s+RW24R$/)
})

test('the climb levels at an at-or-below constraint until passing it, then continues to cruise', () => {
  const { unit, fly } = setup()
  unit.press('LEGS')
  enter(unit, '040B', 'LSK2R')
  expect(lines(unit)[4]).toMatch(/4000B$/)
  unit.press('EXEC')
  // RDG is at or below 4000, and TOLGU at 4500: level at 4000 to RDG, then climb.
  let peakBeforeRdg = 0
  fly(3600, () => { if (active(unit) === 'RDG') peakBeforeRdg = Math.max(peakBeforeRdg, unit.altitude); return active(unit) === 'TOLGU' })
  expect(peakBeforeRdg).toBeLessThanOrEqual(4010)
  expect(peakBeforeRdg).toBeGreaterThan(3950)
  fly(240)
  expect(unit.altitude).toBeGreaterThan(4400)
})

test('a climb constraint the aircraft cannot make shows UNABLE on VNAV CRZ and the advisory', () => {
  const { unit, fly } = setup()
  unit.press('LEGS')
  enter(unit, '9000A', 'LSK1R')
  unit.press('EXEC')
  fly(1)
  expect(unit.profile().unableNext).toBe('MUN')
  expect(scratch(unit)).toBe('UNABLE NEXT ALT')
  press(unit, 'CLR', 'VNAV', 'NEXT')
  expect(lines(unit)[8]).toMatch(/UNABLE MUN$/)
})

test('DES NOW starts the descent early at 1000 fpm and then follows the path', () => {
  const { unit, fly } = setup()
  // At 80 kt the path descends at about 420 fpm, so the 1000 fpm of DES NOW is its own rate.
  press(unit, 'VNAV', 'NEXT')
  enter(unit, '80', 'LSK1R')
  // At cruise on the leg into DEMEL, the first descent constraint, before the top of descent.
  fly(3 * 3600, () => active(unit) === 'DEMEL')
  expect(unit.profile().topOfDescent).not.toBeNull()
  press(unit, 'VNAV', 'NEXT', 'NEXT')
  expect(lines(unit)[12]).toMatch(/^<DES NOW/)
  unit.press('LSK6L')
  expect(unit.vnav.desNow).toBe(true)
  fly(20)
  expect(unit.verticalSpeed).toBeLessThan(-900)
  fly(3600, () => !unit.vnav.desNow)
  expect(unit.vnav.desNow).toBe(false)
  expect(unit.profile().descending).toBe(true)
})

test('below the descent path the aircraft holds its altitude until the path comes down to it', () => {
  const { unit, fly } = setup()
  fly(3 * 3600, () => unit.profile().descending)
  fly(30)
  unit.setAircraft({ altitude: unit.altitude - 800 })
  fly(5)
  expect(Math.abs(unit.verticalSpeed)).toBeLessThan(10)
  fly(3 * 60, () => unit.verticalSpeed < -300)
  expect(unit.verticalSpeed).toBeLessThan(-300)
})

test('a speed constraint slows the aircraft on the leg into its fix', () => {
  const { unit, fly } = setup()
  unit.press('LEGS')
  enter(unit, '90/', 'LSK1R')
  unit.press('EXEC')
  expect(unit.targetSpeed).toBe(90)
  fly(60)
  expect(unit.groundSpeed).toBeLessThan(105)
  fly(3600, () => active(unit) === 'RDG')
  expect(unit.targetSpeed).toBe(120)
})

test('a wind entered on VNAV CRZ changes the ground speed the predictions use', () => {
  const unit = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)), { profile: LAB_AIRLINE_VNAV_PROFILE })
  const before = unit.profile().points.at(-1)!.eta
  press(unit, 'VNAV', 'NEXT')
  // A strong wind from the east: a headwind on the way to Montreal.
  enter(unit, '090/40', 'LSK2R')
  expect(lines(unit)[4]).toMatch(/090°\/40KT$/)
  expect(unit.profile().points.at(-1)!.eta).toBeGreaterThan(before + 10 * 60_000)
  enter(unit, '400/10', 'LSK2R')
  expect(scratch(unit)).toBe('INVALID ENTRY')
})

test('fuel burns as the aircraft flies: FUEL RESERVE when it reaches the reserve, NOT ENOUGH FUEL from the prediction', () => {
  const { unit, fly } = setup()
  unit.press('FUEL')
  enter(unit, '700', 'LSK1L')
  // 700 kg now, about 450 kg to Montreal at 540 kg/h: arriving below the 400 kg reserve.
  fly(1)
  expect(recalled(unit, 'NOT ENOUGH FUEL')).toBe(true)
  // The prediction ends over the landing site, the threshold, and says so; no landing is modelled, so no reserve is
  // shown as met (plan C.11, R3-03).
  expect(lines(unit)[5]).toMatch(/^ RW24R \(THR\)\s+EFOB $/)
  expect(lines(unit)[6]).toMatch(/^\d{4}Z\s+\d+KG$/)
  expect(lines(unit)[7]).toMatch(/^ SITE ARR\s+KNOWN $/)
  expect(lines(unit)[9]).toMatch(/^ LDG RESERVE/)
  expect(lines(unit)[10]).toMatch(/^LANDING NOT MODELLED/)
  expect(recalled(unit, 'FUEL RESERVE')).toBe(false)
  fly(35 * 60)
  expect(recalled(unit, 'FUEL RESERVE')).toBe(true)
  expect(unit.fuelState.quantity).toBeLessThan(400)
})

test('a cold destination raises the FAF altitude by the temperature correction, and the profile follows it', () => {
  expect(coldTemperatureCorrection(1382, 15, 118)).toBe(0)
  expect(coldTemperatureCorrection(1382, -20, 118)).toBeGreaterThan(150)
  expect(coldTemperatureCorrection(1382, -20, 118)).toBeLessThan(250)
  const unit = new ScriptedFms(undefined, { profile: LAB_AIRLINE_VNAV_PROFILE })
  unit.press('VNAV')
  enter(unit, '-20', 'LSK4L')
  const corrected = 1500 + coldTemperatureCorrection(1382, -20, 118)
  expect(lines(unit)[1]).toMatch(/FAF ALT TEMP COMP $/)
  expect(lines(unit)[2]).toMatch(new RegExp(`FERDI ${corrected}A$`))
  expect(unit.profile().points.find(p => p.ident === 'FERDI')!.altitude).toBe(corrected)
})

test('computeProfile: a window constraint in the descent holds the path inside it', () => {
  const at = (ident: string, legDistance: number, text?: string, endOfDescent = false) =>
    ({ ident, legDistance, groundSpeed: 120, constraint: parseConstraint(text) ?? null, endOfDescent })
  const profile = computeProfile({
    waypoints: [at('A', 10), at('B', 10, '6000B4000A'), at('C', 30, '1000', true)],
    altitude: 8000, cruiseAltitude: 8000, climbRate: 1000, pathAngle: 3, fuel: 1000, fuelFlow: 500, now: 0,
  })
  // 30 NM back from C at three degrees is about 10 500 ft: the window caps B at 6000.
  expect(profile.points[1].altitude).toBe(6000)
  expect(profile.points[2].altitude).toBe(1000)
})

test('after the MAP the climb levels at the missed approach altitude, the highest the missed approach codes, never at cruise', () => {
  const at = (ident: string, legDistance: number, text?: string, missed = false) =>
    ({ ident, legDistance, groundSpeed: 120, constraint: parseConstraint(text) ?? null, endOfDescent: false, missed })
  const profile = computeProfile({
    // Climbing out on the missed approach: 1500A, then 2500A (the missed approach altitude), then its hold fix.
    waypoints: [at('M1', 10, '1500A', true), at('M2', 10, '2500A', true), at('HOLD', 10, undefined, true)],
    altitude: 1000, cruiseAltitude: 8000, climbRate: 1000, pathAngle: 3, fuel: 1000, fuelFlow: 500, now: 0,
  })
  // 1,000 ft a minute for 5 minutes a leg would reach 6,000 by M1: the climb levels at 2500 (not 1500, the lowest).
  expect(profile.points.map(p => Math.round(p.altitude!))).toEqual([2500, 2500, 2500])
  expect(profile.climbCap).toBe(2500)
  // The route before the MAP still climbs to cruise.
  const enRoute = computeProfile({
    waypoints: [at('A', 10, '1500A'), at('B', 10, '2500A'), at('C', 10)],
    altitude: 1000, cruiseAltitude: 8000, climbRate: 1000, pathAngle: 3, fuel: 1000, fuelFlow: 500, now: 0,
  })
  expect(enRoute.points[2].altitude).toBe(8000)
})
