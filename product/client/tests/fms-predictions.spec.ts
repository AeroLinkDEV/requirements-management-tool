import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import { computeProfile, parseConstraint } from '../src/fmsCdu/vnav'
import type { CduFunction } from '../src/fmsCdu/variants'

// Prediction validity (independent review of 27 September, findings R04, R07, R08 and R09): a constraint the plan
// misses is not reported as met, the destination prediction is the landing and not the end of the missed approach,
// nothing downstream of an unresolved gap is predicted as if it were known, and future speed constraints shape the
// times and fuel that are predicted for their legs. Expected values are worked out by hand in each test.
const setup = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  return { unit, sim, fly }
}
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : `CHAR_${ch}` as CduFunction)
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const at = (ident: string, legDistance: number, text?: string, endOfDescent = false) =>
  ({ ident, legDistance, groundSpeed: 120, constraint: parseConstraint(text) ?? null, endOfDescent })

test('a constraint below the planned altitude is not met: AT, at-or-below and window upper bounds (R04)', () => {
  const plan = (text: string) => computeProfile({
    waypoints: [at('A', 5, text), at('B', 10)],
    altitude: 3000, cruiseAltitude: 3000, climbRate: 1000, pathAngle: 3, fuel: 1000, fuelFlow: 500, now: 0,
  })
  // Level at 3000 ft, 5 NM from A: nothing in this plan descends, so A is crossed at 3000 ft.
  for (const text of ['1000', '1000B', '2000B1000A']) {
    const profile = plan(text)
    expect(profile.points[0].altitude, text).toBe(3000)
    expect(profile.points[0].constraintMet, text).toBe(false)
    expect(profile.unableNext, text).toBe('A')
  }
  // Inside or above the bounds it asks for, it is met.
  for (const text of ['3000', '1000A', '4000B', '4000B2000A']) {
    expect(plan(text).points[0].constraintMet, text).toBe(true)
    expect(plan(text).unableNext, text).toBeNull()
  }
})

test('an AT constraint below the aircraft on the active leg is reported unable, not satisfied (R04)', () => {
  const { unit, fly } = setup()
  expect(Math.round(unit.altitude)).toBe(3000)
  unit.press('LEGS')
  enter(unit, '1000', 'LSK1R')
  unit.press('EXEC')
  fly(1)
  const mun = unit.profile().points.find(p => p.ident === 'MUN')!
  expect(mun.constraintMet).toBe(false)
  expect(unit.profile().unableNext).toBe('MUN')
})

test('the destination prediction is the landing, not the end of the missed approach (R07)', () => {
  const { unit } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  const profile = unit.profile()
  expect(profile.points.at(-1)!.ident).not.toBe('RW24R')
  expect(profile.destination?.ident).toBe('RW24R')
  // The same route without the missed approach predicts the same landing.
  const landing = unit.profile({ ...unit.activeRoute, legs: unit.activeRoute.legs.filter(leg => leg.kind === 'disco' || leg.source !== 'MISSED') })
  expect(landing.destination).toEqual(profile.destination)
  // The FUEL page shows that landing fuel, not the missed approach's.
  unit.press('FUEL')
  const efob = `${Math.round(profile.destination!.fuel!)}KG`
  expect(lines(unit).some(line => line.includes(efob))).toBe(true)
})

test('nothing downstream of an unresolved gap is predicted as if it were known (R08)', () => {
  const { unit } = setup()
  unit.press('LEGS')
  enter(unit, 'ELIBA', 'LSK1L')
  unit.press('EXEC')
  const legs = unit.activeRoute.legs
  expect(legs[0]).toMatchObject({ kind: 'wpt', ident: 'ELIBA' })
  expect(legs[1]).toMatchObject({ kind: 'disco' })
  const profile = unit.profile()
  expect(profile.points[0]).toMatchObject({ ident: 'ELIBA', basis: 'known' })
  for (const point of profile.points.slice(1)) {
    expect(point.basis, point.ident).toBe('unknown')
    expect(point.eta, point.ident).toBeNull()
    expect(point.fuel, point.ident).toBeNull()
    expect(point.distance, point.ident).toBeNull()
  }
  expect(profile.destination?.fuel ?? null).toBeNull()
  // The FUEL page says the destination is not predicted, instead of showing a number.
  unit.press('FUEL')
  expect(lines(unit).join('\n')).toMatch(/-----KG|--\.-KG|NOT PREDICTED/)
})

test('a slower speed constraint on a later leg makes that leg take longer, and leaves the active leg alone (R09)', () => {
  const { unit } = setup()
  // Calm wind (VNAV CRZ), so the leg time is distance over airspeed and can be worked out by hand.
  press(unit, "VNAV", "NEXT")
  enter(unit, "000/0", "LSK2R")
  expect(unit.wind.speed).toBe(0)
  const legs = unit.activeRoute.legs.flatMap(leg => (leg.kind === 'wpt' ? [leg.ident] : []))
  expect(legs.slice(0, 3)).toEqual(['MUN', 'RDG', 'TOLGU'])
  const before = unit.profile().points
  const speedBefore = unit.targetSpeed
  unit.press('LEGS')
  enter(unit, '60/', 'LSK3R')
  unit.press('EXEC')
  expect(unit.activeRoute.legs[2]).toMatchObject({ ident: 'TOLGU', speed: 60 })
  const after = unit.profile().points
  // The legs before TOLGU are unchanged; the RDG to TOLGU leg is flown at 60 kt instead of the cruise speed.
  expect(after[0].eta).toBe(before[0].eta)
  expect(after[1].eta).toBe(before[1].eta)
  const legTime = (points: typeof before) => (points[2].eta! - points[1].eta!) / 3_600_000
  const distance = after[2].distance! - after[1].distance!
  // At the 120 kt cruise the leg took distance/120 hours; at 60 kt it takes distance/60.
  expect(legTime(before)).toBeCloseTo(distance / unit.vnav.cruiseSpeed, 3)
  expect(legTime(after)).toBeCloseTo(distance / 60, 3)
  expect(unit.targetSpeed).toBe(speedBefore)
})

test('computeProfile: a leg of unknown length makes it and everything after it unknown, and no descent is planned across it (R08)', () => {
  const plan = (legs: (number | null)[]) => computeProfile({
    waypoints: legs.map((legDistance, i) => ({ ident: `W${i}`, legDistance, groundSpeed: 120, constraint: i === legs.length - 1 ? parseConstraint('1000') ?? null : null, endOfDescent: i === legs.length - 1 })),
    altitude: 8000, cruiseAltitude: 8000, climbRate: 1000, pathAngle: 3, fuel: 1000, fuelFlow: 500, now: 0,
  })
  const gap = plan([4, 4, null, 4, 4])
  expect(gap.points.map(p => p.basis)).toEqual(['known', 'known', 'unknown', 'unknown', 'unknown'])
  // Without the gap the aircraft is 20 NM from the end of descent at 1000 ft: 7000 ft at three degrees (318 ft/NM)
  // takes 22 NM, so it is past the top of descent and descending.
  expect(plan([4, 4, 4, 4, 4]).descending).toBe(true)
  // With it, the path cannot be traced back to the aircraft: no top of descent, and no descent now.
  expect(gap.topOfDescent).toBeNull()
  expect(gap.descending).toBe(false)
  expect(gap.points[0].altitude).toBe(8000)
})

test('NOT ENOUGH FUEL is judged at the landing, not at the end of the missed approach (R07)', () => {
  const run = (margin: number) => {
    const { unit, fly } = setup()
    unit.selectProcedure('APPROACH', 'R24R')
    unit.press('EXEC')
    const profile = unit.profile()
    const toLanding = unit.fuelState.quantity - profile.destination!.fuel!
    const toEnd = unit.fuelState.quantity - profile.points.at(-1)!.fuel!
    expect(toEnd).toBeGreaterThan(toLanding + 10)
    // Fuel that lands above the reserve by the margin, but would be below it at the end of the missed approach.
    const fuel = Math.round(unit.fuelState.reserve + toLanding + margin)
    unit.press('FUEL')
    enter(unit, String(fuel), 'LSK1L')
    fly(1)
    return { unit, missedEnd: unit.profile().points.at(-1)!.fuel! }
  }
  const landsAbove = run(5)
  expect(landsAbove.missedEnd).toBeLessThan(landsAbove.unit.fuelState.reserve)
  expect(landsAbove.unit.recallList.some(m => m.text === 'NOT ENOUGH FUEL')).toBe(false)
  const landsBelow = run(-5)
  expect(landsBelow.unit.recallList.some(m => m.text === 'NOT ENOUGH FUEL')).toBe(true)
})

test('PROGRESS 2/4 shows the landing EFOB, and dashes when the route is not predicted to it (R07, R08)', () => {
  const { unit } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  press(unit, 'PROG', 'NEXT')
  expect(lines(unit)[4]).toMatch(new RegExp(`${Math.round(unit.profile().destination!.fuel!)}KG$`))
  unit.press('LEGS')
  enter(unit, 'ELIBA', 'LSK1L')
  unit.press('EXEC')
  press(unit, 'PROG', 'NEXT')
  expect(lines(unit)[4]).toMatch(/-----KG$/)
})

test('constraints beyond an unresolved gap are not evaluated and do not command the connected segment (R08, second review C11)', () => {
  // From 1000 ft, cruise 5000 ft: a known 10 NM leg to KNOWN, then a leg of unknown length to AFTER.
  const plan = (after: string | undefined, afterLeg: number | null) => computeProfile({
    waypoints: [
      { ident: 'KNOWN', legDistance: 10, groundSpeed: 120, constraint: null, endOfDescent: false },
      { ident: 'AFTER', legDistance: afterLeg, groundSpeed: 120, constraint: parseConstraint(after) ?? null, endOfDescent: false },
    ],
    altitude: 1000, cruiseAltitude: 5000, climbRate: 1000, pathAngle: 3, fuel: 1000, fuelFlow: 500, now: 0,
  })
  const unconstrained = plan(undefined, null)
  for (const after of ['1500B', '9000A']) {
    const gap = plan(after, null)
    // The point past the gap has no predicted altitude and an unevaluated constraint, and raises no UNABLE.
    expect(gap.points[1]).toMatchObject({ ident: 'AFTER', basis: 'unknown', altitude: null, constraintMet: null })
    expect(gap.unableNext, after).toBeNull()
    // The connected segment is the same whatever lies behind the gap.
    expect(gap.points[0]).toEqual(unconstrained.points[0])
    expect(gap.climbCap, after).toBe(5000)
  }
  // Connected, the at-or-below 1500 ft constraint caps the climb and is assessed.
  const joined = plan('1500B', 10)
  expect(joined.climbCap).toBe(1500)
  expect(joined.points[1]).toMatchObject({ basis: 'known', constraintMet: true })
  expect(plan('9000A', 10).unableNext).toBe('AFTER')
})

test('a leg the aircraft cannot make progress along is predicted unknown, not flown at an invented 30 kt (Stage B1)', () => {
  const { unit } = setup()
  // A 130 kt wind from ahead of the first leg (about 115 degrees) against the 120 kt cruise speed: no progress along it.
  unit.wind.direction = 115
  unit.wind.speed = 130
  const points = unit.profile().points
  expect(points.length).toBeGreaterThan(2)
  for (const point of points) expect(point).toMatchObject({ eta: null, fuel: null, basis: 'unknown' })
  // In calm air the same route is predicted normally.
  unit.wind.speed = 0
  expect(unit.profile().points[0]).toMatchObject({ basis: 'known', eta: expect.any(Number) })
})
