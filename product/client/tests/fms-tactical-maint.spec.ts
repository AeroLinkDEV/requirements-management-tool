import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { iasFromTas } from '../src/fmsCdu/kinematics'
import { LAB_AIRLINE_VNAV_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import { NO_SURFACE, OFFSHORE_87N, radioHeight } from '../src/fmsCdu/surface'
import type { CduFunction } from '../src/fmsCdu/variants'

// Tactical functions, database cycles, maintenance and dual operation (the FMS test bench research roadmap, step 7):
// rendezvous with RENDEZVOUS UNACHIEVABLE, moving waypoints, the tactical descent with TDN NOT POSSIBLE, the active
// and inactive navigation database cycles with DATABASE OUT OF DATE, self test and the fault log, and cross-side sync.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const setup = (start = START, profile?: AircraftProfile) => {
  let now = start
  const unit = new ScriptedFms(() => new Date(now), { profile })
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => {
    for (let t = 0; t < seconds; t += 1) {
      now += 1000
      sim.step(1)
      if (each?.()) return t
    }
    return seconds
  }
  return { unit, sim, fly, advance: (ms: number) => { now += ms } }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : ch === '-' ? 'PLUSMINUS' : `CHAR_${ch}`)
}
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trimEnd()
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }

test('a rendezvous flies the speed that arrives on time, within the speed limits', () => {
  const { unit, fly } = setup(START, LAB_AIRLINE_VNAV_PROFILE)
  press(unit, 'INIT_REF', 'NEXT', 'LSK6R')
  expect(lines(unit)[0]).toMatch(/^RENDEZVOUS/)
  enter(unit, 'RDG', 'LSK1L')
  // RDG is about 35 NM away: 1420Z is 20 minutes, about 106 kt over the ground. With the 270/12 wind mostly behind the
  // aircraft on these legs the required true airspeed is lower (the wind triangle, R3-04), shown with its IAS.
  enter(unit, '1420', 'LSK1R')
  expect(lines(unit)[4]).toMatch(/^3\d\.\dNM\s+9\d\/9\dKT$/)
  expect(lines(unit)[8]).toMatch(/^ON TIME/)
  unit.press('LSK6R')
  expect(lines(unit)[0]).toMatch(/^ACT RENDEZVOUS/)
  expect(unit.targetSpeed).toBeLessThan(120)
  let arrived = 0
  fly(3600, () => { if (active(unit) === 'TOLGU') { arrived = unit.now.getTime(); return true } })
  // On time to within a minute and a half.
  expect(Math.abs(arrived - Date.UTC(2026, 8, 27, 14, 20, 0))).toBeLessThan(90_000)
})

test('a rendezvous that needs more than the maximum speed is RENDEZVOUS UNACHIEVABLE', () => {
  const { unit, fly } = setup()
  press(unit, 'INIT_REF', 'NEXT', 'LSK6R')
  enter(unit, 'RDG', 'LSK1L')
  enter(unit, '1410', 'LSK1R')
  expect(lines(unit)[8]).toMatch(/^ABOVE MAX SPEED/)
  unit.press('LSK6R')
  fly(1)
  expect(recalled(unit, 'RENDEZVOUS UNACHIEVABLE')).toBe(true)
  // Flown at the maximum meanwhile: the profile's 150 KIAS (below the crew's 160), in true airspeed at this altitude.
  expect(iasFromTas(unit.targetSpeed, unit.altitude)).toBeCloseTo(150, 6)
  // A crew maximum above the profile's does not raise it; one below it lowers it.
  enter(unit, '200', 'LSK3R')
  expect(unit.rndz.maxSpeed).toBe(200)
  expect(iasFromTas(unit.targetSpeed, unit.altitude)).toBeCloseTo(150, 6)
  enter(unit, '140', 'LSK3R')
  expect(iasFromTas(unit.targetSpeed, unit.altitude)).toBeCloseTo(140, 6)
})

test('a moving waypoint advances on its track and the aircraft closes on it', () => {
  const { unit, fly } = setup()
  unit.toggleAngleReference() // This propagation fixture specifies a physical TRUE westbound track.
  press(unit, 'INIT_REF', 'NEXT', 'LSK6L')
  expect(lines(unit)[0]).toMatch(/^MOVING WPT/)
  enter(unit, 'SHIP1', 'LSK1L')
  enter(unit, 'RDG180/5', 'LSK2L')
  enter(unit, '270/20', 'LSK1R')
  unit.press('LSK6R')
  const start = unit.coordinates('SHIP1')!
  expect(unit.movingWaypoints.SHIP1).toEqual({ track: 270, speed: 20 })
  press(unit, 'LEGS')
  enter(unit, 'SHIP1', 'LSK1L')
  unit.press('EXEC')
  let reached = false
  fly(3600, () => { reached = active(unit) !== 'SHIP1'; return reached })
  expect(reached).toBe(true)
  // It moved west at 20 kt while the aircraft flew to it.
  const moved = distanceNm(start, unit.coordinates('SHIP1')!)
  expect(moved).toBeGreaterThan(1)
  expect(unit.coordinates('SHIP1')!.lon).toBeLessThan(start.lon)
})

test('a tactical descent flies its angle down to its altitude; too steep is TDN NOT POSSIBLE', () => {
  const { unit, fly } = setup(START, LAB_AIRLINE_VNAV_PROFILE)
  press(unit, 'TACT', 'LSK5R')
  expect(lines(unit)[0]).toMatch(/^TACTICAL DESCENT/)
  enter(unit, '1000', 'LSK1L')
  enter(unit, 'MUN', 'LSK1R')
  enter(unit, '13', 'LSK2L')
  // Two thousand feet in well under a mile: far steeper than the six-degree limit.
  unit.press('LSK6R')
  expect(scratch(unit)).toBe('TDN NOT POSSIBLE')
  press(unit, 'CLR', 'CLR', 'CLR')
  enter(unit, '2', 'LSK2L')
  expect(lines(unit)[4]).toMatch(/\d\.\d°$/)
  unit.press('LSK6R')
  expect(unit.tdn.active).toBe(true)
  const vs: number[] = []
  fly(3600, () => { vs.push(unit.verticalSpeed); return !unit.tdn.active })
  expect(unit.altitude).toBeLessThan(1030)
  expect(unit.altitude).toBeGreaterThan(950)
  // A shallow, steady descent (about 2 degrees at 130 kt), not a dive.
  expect(Math.min(...vs)).toBeGreaterThan(-600)
  // Complete: it levels at the target altitude and stays there, not climbing back to the VNAV altitude.
  expect(unit.tdn.active).toBe(false)
  expect(unit.tdn.level).toBe(true)
  expect(lines(unit)[12]).toMatch(/CANCEL>$/)
  fly(120)
  expect(Math.abs(unit.altitude - 1000)).toBeLessThan(30)
  // Cancelled, the altitude goes back to VNAV.
  unit.press('LSK6R')
  expect(unit.tdn.level).toBe(false)
  fly(60)
  expect(unit.altitude).toBeGreaterThan(1100)
})

test('IDENT shows the active and inactive database cycles; past its end the active one is DATABASE OUT OF DATE until swapped', () => {
  const unit = new ScriptedFms(() => new Date(START))
  expect(lines(unit)[4]).toMatch(/^DEMO-2609\s+03SEP-30SEP$/)
  expect(lines(unit)[6]).toMatch(/^DEMO-2610\s+01OCT-28OCT$/)
  const late = setup(Date.UTC(2026, 9, 2, 12, 0, 0))
  late.unit.tick()
  expect(recalled(late.unit, 'DATABASE OUT OF DATE')).toBe(true)
  // Flagged once, not on every tick.
  const flagged = late.unit.recallList.length
  late.unit.tick()
  expect(late.unit.recallList.length).toBe(flagged)
  late.unit.press('CLR')
  late.unit.press('LSK3R')
  expect(late.unit.activeCycle.id).toBe('DEMO-2610')
  const count = late.unit.recallList.length
  late.unit.tick()
  expect(late.unit.recallList.length).toBe(count)
  // Swapped back to the expired cycle, it is flagged again.
  late.unit.press('LSK3R')
  late.unit.tick()
  expect(late.unit.recallList.length).toBe(count + 1)
})

test('the maintenance page runs a self test, which fails while a fault is present, and keeps a fault log', () => {
  const { unit, advance } = setup()
  press(unit, 'INIT_REF', 'LSK6L')
  expect(lines(unit)[0]).toMatch(/^MAINTENANCE/)
  expect(lines(unit)[8]).toMatch(/^NO FAULTS/)
  unit.press('LSK2L')
  expect(lines(unit)[4]).toMatch(/IN PROG$/)
  advance(4000)
  unit.tick()
  expect(lines(unit)[4]).toMatch(/IN PROG$/)
  advance(1000)
  unit.tick()
  expect(lines(unit)[4]).toMatch(/PASS$/)
  unit.setCondition('gpsLost', true)
  unit.press('CLR')
  unit.press('LSK2L')
  advance(5000)
  unit.tick()
  expect(lines(unit)[4]).toMatch(/FAIL$/)
  expect(lines(unit)[8]).toMatch(/^1400Z GPS LOST/)
})

test('in dual operation the executed route is cross-loaded; independent, the sides differ until they resynchronise', () => {
  const unit = new ScriptedFms()
  unit.press('LEGS')
  typeText(unit, 'TOLGU')
  press(unit, 'LSK1L', 'EXEC')
  expect(unit.crossSideInSync).toBe(true)
  unit.setCondition('independent', true)
  unit.press('CLR')
  unit.press('LEGS')
  typeText(unit, 'CYUL')
  press(unit, 'LSK1L', 'EXEC')
  expect(unit.crossSideInSync).toBe(false)
  press(unit, 'INIT_REF', 'LSK6L')
  expect(lines(unit)[6]).toMatch(/^INDEPENDENT\s+RTE DIFFER$/)
  expect(lines(unit)[8]).toMatch(/^\d{4}Z X-SIDE SYNC LOST\s*$/)
  unit.setCondition('independent', false)
  expect(unit.crossSideInSync).toBe(true)
  expect(lines(unit)[6]).toMatch(/^DUAL SYNC\s+RTE MATCH$/)
})

// Stage B2 of the helicopter-first plan: the radio altimeter measures the aircraft's physical height above a declared
// flat surface, and has no height (NCD) where none is declared. Nothing shows a fixed or invented radio height.
test('the radio altimeter reads height above the declared surface, NCD off it or above its range, FAIL when failed', () => {
  const offshore = { lat: 40.7, lon: -72.45 }, heliport = { lat: 40.8463, lon: -72.4664 }
  expect(radioHeight(OFFSHORE_87N, offshore, 1500, false)).toEqual({ value: 1500, status: 'NORMAL' })
  expect(radioHeight(OFFSHORE_87N, offshore, 2500, false)).toEqual({ value: 2500, status: 'NORMAL' })
  expect(radioHeight(OFFSHORE_87N, offshore, 2501, false)).toEqual({ value: null, status: 'NCD' })
  // Southampton heliport is on land, north of the declared sea: no surface there, so no radio height.
  expect(radioHeight(OFFSHORE_87N, heliport, 500, false)).toEqual({ value: null, status: 'NCD' })
  expect(radioHeight(NO_SURFACE, offshore, 500, false)).toEqual({ value: null, status: 'NCD' })
  expect(radioHeight(OFFSHORE_87N, offshore, 500, true)).toEqual({ value: null, status: 'FAIL' })
})

test('the HOVER page shows the radio altimeter, dashes without a surface or with the altimeter failed, never a fixed value', () => {
  const { unit } = setup()
  unit.open('HOVER')
  // The line under the RAD ALT caption.
  const radAlt = () => { const lines = screenText(unit.screen()); return lines[lines.findIndex(line => /RAD ALT/.test(line)) + 1] }
  // The demonstration route is over land with no declared surface.
  expect(radAlt()).toMatch(/^\s*----FT/)
  expect(unit.declareSurface('offshore-87n')).toBe(true)
  unit.placeAircraft({ position: { lat: 40.7, lon: -72.45 }, track: 230, altitude: 1500 }, 'test: offshore south of 87N')
  expect(unit.radioHeight).toEqual({ value: 1500, status: 'NORMAL' })
  expect(radAlt()).toMatch(/^\s*1500FT/)
  unit.setCondition('raFail', true)
  expect(radAlt()).toMatch(/^\s*----FT/)
  expect(unit.declareSurface('nowhere')).toBe(false)
})
