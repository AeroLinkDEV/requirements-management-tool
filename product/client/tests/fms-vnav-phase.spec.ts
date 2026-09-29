import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { computeProfile } from '../src/fmsCdu/vnav'
import type { CduFunction } from '../src/fmsCdu/variants'

// The VNAV descent phase is latched (A23 prerequisite). Once the aircraft passes the top of descent it stays in the
// descent: a level segment at a descent constraint, or being a little below cruise, never turns the rest of the route
// back into a climb. Only an explicit event leaves the descent: a cruise altitude entered above the aircraft, or the
// missed approach becoming active.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
// These run the laboratory airline-style VNAV profile: VNAV is its behaviour, not the helicopter profile's, where
// the crew flies the vertical axis.
const setup = () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now), { profile: LAB_AIRLINE_VNAV_PROFILE })
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
// The demonstration approach's fixes (navData.ts): downwind DEMEL-ALNIT at 3000, base to ULIDA at 2500, final from
// FERDI (the FAF, 1500).
const FIXES = {
  DEMEL: { lat: 45.5349, lon: -73.7698 }, ALNIT: { lat: 45.6166, lon: -73.5902 },
  ULIDA: { lat: 45.56051, lon: -73.53842 }, FERDI: { lat: 45.51889, lon: -73.63027 },
}

test('the demonstration route descends to the downwind, flies it level, and meets each approach constraint without climbing', () => {
  const { unit, sim, fly } = setup()
  // Where each fix is crossed: the altitude where the aircraft passes closest to it (a fly-by fix is not overflown).
  const closest: Record<string, { nm: number; altitude: number }> = {}
  let descentAt: number | null = null, worstVs = -Infinity, climbMode = false
  fly(4 * 3600, () => {
    if (descentAt === null && sim.modeEvents.some(e => e.event === 'VNAV DESCENT')) descentAt = distanceNm(unit.truePosition, FIXES.DEMEL)
    const now = active(unit)
    if (descentAt !== null && now !== 'RW24R') {
      worstVs = Math.max(worstVs, unit.verticalSpeed)
      if (sim.verticalMode === 'VNAV CLB') climbMode = true
    }
    for (const [ident, at] of Object.entries(FIXES)) {
      const nm = distanceNm(unit.truePosition, at)
      if (!closest[ident] || nm < closest[ident].nm) closest[ident] = { nm, altitude: unit.altitude }
    }
    return now === 'RW24R'
  })
  const crossed = (ident: keyof typeof FIXES) => { expect(closest[ident].nm).toBeLessThan(0.3); return closest[ident].altitude }
  expect(Math.abs(crossed('DEMEL') - 3000)).toBeLessThanOrEqual(50)
  expect(Math.abs(crossed('ALNIT') - 3000)).toBeLessThanOrEqual(50)
  expect(Math.abs(crossed('ULIDA') - 2500)).toBeLessThanOrEqual(50)
  expect(Math.abs(crossed('FERDI') - 1500)).toBeLessThanOrEqual(50)
  // The descent phase begins at the top of descent: 1500 ft above DEMEL's 3000 at 318.4 ft/NM is 4.71 NM before it.
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
  const { unit, sim, fly } = setup()
  // An early descent: DES NOW at cruise at the start of the 31 NM leg into DEMEL (AT 3000), far before the T/D.
  fly(4 * 3600, () => active(unit) === 'DEMEL')
  unit.vnav.desNow = true
  fly(30)
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
  // The early descent is cancelled: the climb rules apply again (unchanged), the T/D for 6000 is ahead (3000 ft above
  // DEMEL's 3000 at 318.4 ft/NM is 9.4 NM before it, and DEMEL is still more than 20 NM away), and it climbs.
  expect(unit.vnav.desNow).toBe(false)
  expect(unit.profile().descending).toBe(false)
  expect(unit.profile().topOfDescent).not.toBeNull()
  fly(30)
  expect(unit.verticalPhase).toBe('CLIMB')
  expect(sim.verticalMode).toBe('VNAV CLB')
  expect(unit.verticalSpeed).toBeGreaterThan(500)
})

test('in the descent, below a constraint ahead, VNAV holds the altitude rather than climbing to it', () => {
  const { unit, sim, fly } = setup()
  fly(4 * 3600, () => unit.verticalPhase === 'DESCENT')
  // Put the aircraft 500 ft below DEMEL's AT 3000, still before DEMEL.
  unit.setAircraft({ altitude: 2500, verticalSpeed: 0 })
  expect(active(unit)).toBe('DEMEL')
  expect(unit.profile().points[0].altitude).toBe(2500)
  let worstVs = -Infinity, climbMode = false
  fly(60, () => { worstVs = Math.max(worstVs, unit.verticalSpeed); if (sim.verticalMode === 'VNAV CLB') climbMode = true })
  expect(worstVs).toBeLessThanOrEqual(100)
  expect(climbMode).toBe(false)
  expect(unit.verticalPhase).toBe('DESCENT')
})

test('DES NOW descends at 1000 fpm to the planned altitude at the active fix and levels there', () => {
  const { unit, sim, fly } = setup()
  // At cruise on the leg into DEMEL, whose altitude is 3000 (the downwind).
  fly(4 * 3600, () => active(unit) === 'DEMEL')
  expect(unit.altitude).toBeGreaterThan(4490)
  unit.vnav.desNow = true
  fly(20)
  expect(unit.verticalPhase).toBe('DESCENT')
  expect(sim.modeEvents.at(-1)).toMatchObject({ event: 'VNAV DESCENT', detail: 'DES NOW' })
  expect(unit.verticalSpeed).toBeLessThanOrEqual(-990)
  // 1500 ft at 1000 fpm is a minute and a half; five minutes later it is level at 3000, not below it.
  fly(300)
  expect(unit.altitude).toBeGreaterThanOrEqual(2950)
  expect(unit.altitude).toBeLessThanOrEqual(3050)
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
