import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { computeProfile } from '../src/fmsCdu/vnav'
import type { CduFunction } from '../src/fmsCdu/variants'

// The VNAV descent phase is latched (A23 prerequisite). Once the aircraft passes the top of descent it stays in the
// descent: a level segment at a descent constraint, or being a little below cruise, never turns the rest of the route
// back into a climb. Only an explicit event leaves the descent: a cruise altitude entered above the aircraft, or the
// missed approach becoming active.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const setup = () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => {
    for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); if (each?.()) return t }
    return seconds
  }
  return { unit, sim, fly }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }
// A 3-degree path falls 6076.12 × tan 3° = 318.4 ft per NM.
const FT_PER_NM_3DEG = 6076.12 * Math.tan((3 * Math.PI) / 180)
const TOLGU = { lat: 45.5020, lon: -74.5100 }

// The demonstration route with TOLGU made AT 3000: cruise at 4500 to RDG, descend to 3000 at TOLGU, then a 62 NM level
// segment at 3000 before the descent to the FAF (FERDI, 1500). The same shape as a downwind at a constraint altitude.
const levelSegmentRoute = () => {
  const run = setup()
  press(run.unit, 'LEGS')
  typeText(run.unit, '3000')
  press(run.unit, 'LSK3R', 'EXEC')
  expect(run.unit.activeRoute.legs[2]).toMatchObject({ ident: 'TOLGU', altitude: '3000' })
  return run
}

test('past the top of descent the aircraft descends to a constraint followed by a level segment, and never climbs back', () => {
  const { unit, sim, fly } = levelSegmentRoute()
  const crossed: Record<string, number> = {}
  let descentAt: number | null = null, worstVs = -Infinity, climbMode = false
  fly(4 * 3600, () => {
    const events = sim.modeEvents.filter(e => e.event === 'VNAV DESCENT')
    if (descentAt === null && events.length) descentAt = distanceNm(unit.truePosition, TOLGU)
    if (descentAt !== null && crossed.FERDI === undefined) {
      worstVs = Math.max(worstVs, unit.verticalSpeed)
      if (sim.verticalMode === 'VNAV CLB') climbMode = true
    }
    const now = active(unit)
    if (now === 'FERDI' && crossed.TOLGU === undefined) crossed.TOLGU = unit.altitude
    if (now === 'RW24R' && crossed.FERDI === undefined) crossed.FERDI = unit.altitude
    return crossed.FERDI !== undefined
  })
  expect(crossed.TOLGU).toBeGreaterThanOrEqual(2950)
  expect(crossed.TOLGU).toBeLessThanOrEqual(3050)
  // FERDI's crossing altitude is not checked on this route: its 150-degree turn onto final sequences FERDI about 1.5 NM
  // early (fly-by anticipation). The demonstration approach (A23) removes that turn and checks it.
  // The descent phase begins at the top of descent: 1500 ft above TOLGU's 3000 at 318.4 ft/NM is 4.71 NM before it.
  expect(descentAt).not.toBeNull()
  expect(descentAt!).toBeCloseTo(1500 / FT_PER_NM_3DEG, 0)
  expect(climbMode).toBe(false)
  expect(worstVs).toBeLessThanOrEqual(100)
  expect(unit.verticalPhase).toBe('DESCENT')
})

test('in the descent phase the profile traces the path through a level segment between two descent constraints', () => {
  // Legs of 20, 9, 4, 4.6 and 4.4 NM; constraints AT 3000, AT 3000, AT 2500, AT 1500, then the runway at 168.
  const waypoints = [
    { ident: 'A', legDistance: 20, groundSpeed: 120, constraint: { kind: 'AT' as const, altitude: 3000 }, endOfDescent: false },
    { ident: 'B', legDistance: 9, groundSpeed: 120, constraint: { kind: 'AT' as const, altitude: 3000 }, endOfDescent: false },
    { ident: 'C', legDistance: 4, groundSpeed: 120, constraint: { kind: 'AT' as const, altitude: 2500 }, endOfDescent: false },
    { ident: 'D', legDistance: 4.6, groundSpeed: 120, constraint: { kind: 'AT' as const, altitude: 1500 }, endOfDescent: false },
    { ident: 'RWY', legDistance: 4.4, groundSpeed: 120, constraint: { kind: 'AT' as const, altitude: 168 }, endOfDescent: true },
  ]
  const input = { waypoints, cruiseAltitude: 4500, climbRate: 1000, pathAngle: 3, fuel: 1000, fuelFlow: 500, now: START }
  // 10 ft below cruise, just past the top of descent.
  const profile = computeProfile({ ...input, altitude: 4490, phase: 'DESCENT' })
  expect(profile.descending).toBe(true)
  expect(profile.topOfDescent).toBeNull()
  expect(profile.points.map(p => p.altitude)).toEqual([3000, 3000, 2500, 1500, 168])
  expect(profile.climbCap).toBeLessThanOrEqual(4490)
})

test('entering a cruise altitude above the aircraft leaves the descent; one below it, or being below the path, does not', () => {
  const { unit, sim, fly } = levelSegmentRoute()
  fly(4 * 3600, () => unit.verticalPhase === 'DESCENT')
  fly(20)
  expect(unit.verticalPhase).toBe('DESCENT')
  // Below the path is not a reason to leave the descent.
  unit.setAircraft({ altitude: unit.altitude - 800 })
  fly(10)
  expect(unit.verticalPhase).toBe('DESCENT')
  // A cruise altitude below the aircraft keeps the descent.
  press(unit, 'VNAV', 'NEXT')
  typeText(unit, '020')
  press(unit, 'LSK1L')
  expect(unit.vnav.cruiseAltitude).toBe(2000)
  fly(5)
  expect(unit.verticalPhase).toBe('DESCENT')
  // One above it is a new climb.
  typeText(unit, '060')
  press(unit, 'LSK1L')
  expect(unit.vnav.cruiseAltitude).toBe(6000)
  fly(1)
  expect(unit.verticalPhase).toBe('CLIMB')
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'VNAV CLIMB', detail: expect.stringContaining('cruise altitude 6000') })
  // Out of the descent, the climb rules apply again (unchanged): TOLGU's AT 3000 ahead caps the climb, so VNAV no
  // longer plans a descent and does not climb through the constraint either.
  expect(unit.profile().descending).toBe(false)
  fly(30)
  expect(unit.verticalSpeed).toBeLessThan(100)
})

test('in the descent, below a constraint ahead, VNAV holds the altitude rather than climbing to it', () => {
  const { unit, sim, fly } = levelSegmentRoute()
  fly(4 * 3600, () => unit.verticalPhase === 'DESCENT')
  // Put the aircraft 500 ft below TOLGU's AT 3000, still before TOLGU.
  unit.setAircraft({ altitude: 2500, verticalSpeed: 0 })
  expect(active(unit)).toBe('TOLGU')
  expect(unit.profile().points[0].altitude).toBe(2500)
  let worstVs = -Infinity, climbMode = false
  fly(60, () => { worstVs = Math.max(worstVs, unit.verticalSpeed); if (sim.verticalMode === 'VNAV CLB') climbMode = true })
  expect(worstVs).toBeLessThanOrEqual(100)
  expect(climbMode).toBe(false)
  expect(unit.verticalPhase).toBe('DESCENT')
})

test('DES NOW descends at 1000 fpm to the planned altitude at the active fix and levels there', () => {
  const { unit, sim, fly } = setup()
  // At cruise on the leg into FERDI, whose altitude is 1500 (the FAF).
  fly(4 * 3600, () => active(unit) === 'FERDI')
  expect(unit.altitude).toBeGreaterThan(4490)
  unit.vnav.desNow = true
  fly(20)
  expect(unit.verticalPhase).toBe('DESCENT')
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'VNAV DESCENT', detail: 'DES NOW' })
  expect(unit.verticalSpeed).toBeLessThanOrEqual(-990)
  // 3000 ft at 1000 fpm is three minutes; five minutes later it is level at 1500, not below it.
  fly(300)
  expect(unit.altitude).toBeGreaterThanOrEqual(1450)
  expect(unit.altitude).toBeLessThanOrEqual(1550)
  expect(Math.abs(unit.verticalSpeed)).toBeLessThan(100)
})

test('the missed approach leaves the descent', () => {
  const { unit, sim, fly } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  fly(4 * 3600, () => active(unit) === 'RW24R')
  expect(unit.verticalPhase).toBe('DESCENT')
  expect(unit.goAround()).toBe(true)
  fly(1)
  expect(unit.verticalPhase).toBe('CLIMB')
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'VNAV CLIMB', detail: 'missed approach' })
})
