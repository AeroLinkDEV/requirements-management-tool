import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator, legGeometry } from '../src/fmsCdu/flight'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import { setUp87nRnav190Final } from '../src/fmsCdu/heliDemo'
import { LATER_SBAS_PROFILE } from '../src/fmsCdu/profile'
import { distanceNm, offset } from '../src/fmsCdu/fmsModel'
import { iasFromTas } from '../src/fmsCdu/kinematics'
import { LAB_AIRLINE_VNAV_PROFILE, type AircraftProfile } from '../src/fmsCdu/profile'
import { RENDEZVOUS_RANGE_NM, ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { runHeadless, type Scenario } from '../src/fmsCdu/scenario'
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

// Integration owner: a manual 500-NM travel bound cannot be shortened by assuming the fastest wind-assisted speed.
test('a reachable moving waypoint inside 500 NM remains achievable against a headwind', () => {
  const { unit } = setup()
  Object.assign(unit.wind, { direction: 0, speed: 60 })
  unit.setAircraft({ tas: 140, groundSpeed: 80, track: 0, heading: 0 })
  expect(unit.trueAirspeed).toBeCloseTo(140, 6)
  unit.defineMoving('SHIP1', offset(unit.position, 0, 400), 0, 0)
  unit.replaceLegs([{ kind: 'wpt', ident: 'SHIP1' }]); unit.press('EXEC')
  const rendezvous = unit.rendezvousFor(unit.activeRoute, 0)!
  expect(rendezvous.achievable).toBe(true)
  expect(rendezvous.distanceNm).toBeCloseTo(400, 5)
  expect(rendezvous.ttg).toBeCloseTo(400 / 80 * 3600, 1)
})

// Owner: the closed 500-NM airborne boundary uses measured airspeed and system wind, not entered ground planning.
test('airborne moving rendezvous uses measured TAS and wind and includes the exact 500 NM boundary', () => {
  const { unit } = setup()
  unit.setAircraft({ tas: 100, groundSpeed: 80, track: 0, heading: 0 })
  unit.planData.cruiseTas = 140; unit.planData.cruiseWind = { direction: 180, speed: 50 }
  Object.assign(unit.wind, { direction: 0, speed: 20 })
  unit.defineMoving('SHIP1', offset(unit.position, 0, 500), 0, 0)
  unit.replaceLegs([{ kind: 'wpt', ident: 'SHIP1' }]); unit.press('EXEC')
  const rendezvous = unit.rendezvousFor(unit.activeRoute, 0)!
  expect(rendezvous.achievable).toBe(true)
  expect(rendezvous.ttg).toBeCloseTo(500 / 80 * 3600, 1)
})

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

// Rev 2 D-R epoch: the epoch is the simulation time of entry, and the position follows the simulation clock along the
// track at the ground speed: stopped clock (paused), nothing moves; the same place however the time was ticked.
test('a moving waypoint is placed from its epoch by the simulation clock alone', () => {
  const { unit, sim, advance } = setup()
  const origin = { lat: 45.3, lon: -75.6 }
  unit.defineMoving('SHIP1', origin, 90, 30)
  // Paused: the simulation clock stands still, and so does the waypoint, however often the aircraft is stepped.
  for (let i = 0; i < 10; i += 1) sim.step(1)
  expect(unit.coordinates('SHIP1')).toEqual(origin)
  // Twenty minutes in one step, or in 1200 one-second steps, places it at the same point: 10 NM east.
  advance(20 * 60_000)
  expect(unit.coordinates('SHIP1')).toEqual(offset(origin, 90, 10))
  const ticked = setup()
  ticked.unit.defineMoving('SHIP1', origin, 90, 30)
  ticked.fly(1200)
  expect(ticked.unit.coordinates('SHIP1')!.lat).toBeCloseTo(offset(origin, 90, 10).lat, 9)
  expect(ticked.unit.coordinates('SHIP1')!.lon).toBeCloseTo(offset(origin, 90, 10).lon, 9)
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
  // Built two minutes before DEMO-2609 ends and flown past its end (the demonstration cycles follow the clock the unit
  // is built with, so a unit built after the end would start on the next cycle).
  const late = setup(Date.UTC(2026, 8, 30, 23, 58, 0))
  expect(late.unit.activeCycle.id).toBe('DEMO-2609')
  late.fly(120)
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

// Owner: real computer separation, confirmed mode transition, single-editor lock and receiving MOD/EXEC activation.
// A copied-route facade cannot detect one CDU editing or failing while the other remains usable.
const dualSetup = (secondaryProfile?: AircraftProfile) => {
  let now = START
  const system = new DualFmsSystem(() => new Date(now), { secondaryProfile })
  return { system, one: system.computers[0], two: system.computers[1],
    tick: (seconds = 1) => { for (let i = 0; i < seconds; i++) { now += 1000; system.tick() } },
    fly: (seconds: number) => { let left = seconds; while (left > 1e-9) { const dt = Math.min(1, left); now += dt * 1000; system.step(dt); left -= dt } } }
}
const mode = (unit: ScriptedFms) => { unit.open('SETUP'); unit.press('LSK5L'); unit.press('LSK6R'); unit.press('CLR', { held: true }); unit.press('CLR', { held: true }) }
test('two computers retain independent CDU and MOD state, synchronize EXEC and require receiving EXEC after crossfill', () => {
  const { system, one, two } = dualSetup()
  expect(system.mode).toBe('SYNC')
  one.open('LEGS'); two.open('PROG'); two.setScratch('KEEP')
  one.modify(route => { route.legs = [{ kind: 'wpt', ident: 'TOLGU' }] })
  expect(two.modify(route => { route.dest = 'CYOW' })).toBe(false)
  expect(two.routeStatus).toBe('ACT')
  expect(lines(two)[SCRATCHPAD_LINE]).toContain('CDU ENTRY CONFLICT')
  one.press('EXEC')
  expect(two.activeRoute.legs).toEqual([{ kind: 'wpt', ident: 'TOLGU' }])
  expect(lines(two)[0]).toContain('PROGRESS')
  one.open('SETUP'); one.press('LSK5L'); one.press('LSK6L')
  expect(system.mode).toBe('SYNC')
  mode(one); expect(system.mode).toBe('INDEPENDENT')
  one.modify(route => { route.legs = [{ kind: 'wpt', ident: 'CYUL' }] }); one.press('EXEC')
  expect(two.activeRoute.legs).toEqual([{ kind: 'wpt', ident: 'TOLGU' }])
  expect(one.dualOperation!.crossfill(false)).toBe(true)
  expect(two.routeStatus).toBe('MOD')
  expect(two.route.legs).toEqual([{ kind: 'wpt', ident: 'CYUL' }])
  expect(two.activeRoute.legs).toEqual([{ kind: 'wpt', ident: 'TOLGU' }])
  expect(one.dualOperation!.crossfill(false)).toBe(false)
  expect(two.route.legs).toEqual([{ kind: 'wpt', ident: 'CYUL' }])
  two.press('EXEC'); expect(two.activeRoute.legs).toEqual([{ kind: 'wpt', ident: 'CYUL' }])
  one.copyActiveToSecondary(); one.modify(route => { route.legs = [{ kind: 'wpt', ident: 'MUN' }] }); one.press('EXEC')
  one.open('RTE'); one.setScratch('SEC'); one.press('LSK4L')
  expect(two.route.legs).toEqual([{ kind: 'wpt', ident: 'CYUL' }])
  expect(two.activeRoute.legs).toEqual([{ kind: 'wpt', ident: 'CYUL' }])
  two.eraseModification()
  mode(two) // The initiating computer is explicitly authoritative.
  expect(system.mode).toBe('SYNC')
  expect(one.activeRoute.legs).toEqual([{ kind: 'wpt', ident: 'CYUL' }])
  expect(one.activeRoute).not.toBe(two.activeRoute)
  one.raiseAlert('CHECK ANP'); expect(two.recallList[0].text).toBe('CHECK ANP')
  two.press('CLR'); expect(one.lamps().has('MSG')).toBe(false)
  one.setCondition('fmsFail', true)
  expect(two.hasCondition('fmsFail')).toBe(false)
  expect(new ScriptedFms().otherFms).toBeNull()
})

// Owner: committed helicopter procedure geometry crosses the link; preview/ERASE cannot change receiver guidance.
test('SAR and hover committed data follow synchronized EXEC and receiving crossfill EXEC', () => {
  const { system, one, two, tick, fly } = dualSetup()
  one.sar.trackSpacing = 1.3; one.sar.sarBearing = 130
  one.activateSar('SECTOR')
  expect(two.sar.active).toBeNull()
  one.press('EXEC')
  expect(two.sar.active).toBe('SECTOR')
  expect(two.sar.status).toBe('ARMED')
  expect(two.sar.sarBearing).toBe(130)
  expect(two.sar).not.toBe(one.sar)
  system.selectGuidance(2); fly(1)
  expect(system.simulator.guidance.mode).toBe('SAR')
  expect(system.flights[0].sarPath).toEqual(system.flights[1].sarPath)
  system.selectGuidance(1)
  mode(one); two.interruptSar(); fly(2)
  mode(one); tick()
  expect(system.flights[1].guidance.mode).toBe('SAR') // Accepted SYNC resumes the current flown pattern, not its origin.
  mode(one)
  one.sar.diameter = 6; one.activateSar('SQUARE'); one.press('EXEC')
  expect(one.dualOperation!.crossfill(false)).toBe(true)
  expect(two.sar.active).toBe('SECTOR')
  two.eraseModification(); expect(two.sar.active).toBe('SECTOR')
  expect(one.dualOperation!.crossfill(false)).toBe(true)
  two.press('EXEC'); expect(two.sar.active).toBe('SQUARE')
  expect(two.sar.diameter).toBe(6)
  mode(one)
  one.declareSurface('offshore-87n')
  one.placeAircraft({ position: { lat: 40.7, lon: -72.45 }, track: 230, altitude: 500 }, 'test: common offshore aircraft')
  fly(1) // AFCS must publish the real airspeed used by the hover activation gate.
  expect(one.designateHoverMarkOnTop()).toBe(true)
  expect(one.activateHover()).toBeNull()
  expect(two.hover.active).toBeNull()
  one.press('EXEC')
  expect(two.hover.status).toBe('ACT')
  expect(two.hoverJoin).toEqual(one.hoverJoin)
  expect(two.hover.active).not.toBe(one.hover.active)
  expect(two.sar.active).toBeNull()
  mode(one)
  expect(one.designateHoverMark({ ident: 'OTHER', position: offset(one.position, 90, 2), label: null })).toBe(true)
  expect(one.activateHover()).toBeNull(); one.press('EXEC')
  const oldMark = structuredClone(two.hover.active!.mark)
  expect(one.dualOperation!.crossfill(false)).toBe(true)
  expect(two.hover.active!.mark).toEqual(oldMark)
  two.eraseModification(); expect(two.hover.active!.mark).toEqual(oldMark)
  expect(one.dualOperation!.crossfill(false)).toBe(true)
  one.setCondition('raFail', true); tick(); two.press('EXEC')
  expect(two.routeStatus).toBe('MOD'); expect(two.hover.active!.mark).toEqual(oldMark)
  one.setCondition('raFail', false); tick(); two.press('EXEC'); expect(two.hover.active!.mark.ident).toBe('OTHER')
  expect(two.hoverJoin).toEqual(one.hoverJoin)
  system.selectGuidance(2); tick()
  expect(system.simulator.guidance.desiredTrack).not.toBeNull()
})

// Owner: settings actually cross the link; mode refusals preserve two plans rather than reporting a false match.
test('SETUP transfers configured settings and sync refuses link, software, data, user, MOD and active-hold mismatches', () => {
  const { system, one, two, tick } = dualSetup()
  one.open('SETUP'); one.press('LSK1L'); one.press('LSK2L'); one.setScratch('+5.5'); one.press('LSK2R')
  expect(two.angleReference).toBe('TRUE')
  expect(two.setup).toEqual({ localTime: true, localOffsetHours: 5.5 })
  one.createUserWaypoint('OWN01', { lat: 42, lon: -72 }); tick()
  expect(two.userWaypoints).toContainEqual({ ident: 'OWN01', type: 'FIXED', position: { lat: 42, lon: -72 } })
  system.setLinkAvailable(false)
  const plans = [structuredClone(one.activeRoute), structuredClone(two.activeRoute)]
  mode(one); expect(system.mode).toBe('INDEPENDENT')
  expect(lines(one)[SCRATCHPAD_LINE]).not.toContain('UNABLE') // CLR acknowledges the refusal, recall retains it.
  expect(one.recallList.some(message => message.text.includes('UNABLE FMS-FMS SYNC'))).toBe(true)
  system.setLinkAvailable(true); expect(system.mode).toBe('INDEPENDENT')
  two.modify(route => { route.legs = [{ kind: 'wpt', ident: 'CYUL' }] })
  mode(one); expect(system.mode).toBe('INDEPENDENT'); two.eraseModification()
  one.modify(route => { route.hold = { fix: 'MUN', inbound: 90, turn: 'RIGHT', legTime: 1, legDistance: null, exit: 'MANUAL', speed: 90, altitude: '3000', status: 'IN PROGRESS' } }); one.press('EXEC')
  mode(one); expect(system.mode).toBe('INDEPENDENT')
  one.modify(route => { route.hold = undefined }); one.press('EXEC')
  two.createUserWaypoint('OWN02', { lat: 43, lon: -72 }); mode(one)
  expect(system.mode).toBe('INDEPENDENT')
  expect(one.faultLog[0].text).toContain('USER DATA DIFFER')
  expect(two.activeRoute).toEqual(plans[1])
  const different = dualSetup(LATER_SBAS_PROFILE)
  expect(different.system.mode).toBe('INDEPENDENT'); mode(different.one)
  expect(different.one.faultLog[0].text).toContain('OP PROGRAM DIFFER')
  const cycles = dualSetup(); mode(cycles.one); cycles.two.swapCycles(); mode(cycles.one)
  expect(cycles.system.mode).toBe('INDEPENDENT'); expect(cycles.one.faultLog[0].text).toContain('NAV DATA DIFFER')
  const approaches = dualSetup(); mode(approaches.one)
  expect(setUp87nRnav190Final(approaches.one, approaches.system.flights[0])).toEqual({ ready: true })
  expect(setUp87nRnav190Final(approaches.two, approaches.system.flights[1])).toEqual({ ready: true })
  mode(approaches.one); expect(approaches.system.mode).toBe('INDEPENDENT')
  expect(approaches.one.faultLog[0].text).toContain('GPS APPROACH')
  approaches.one.armApproach(false); expect(approaches.two.goAround()).toBe(true)
  mode(approaches.one); expect(approaches.system.mode).toBe('INDEPENDENT')
  expect(approaches.one.faultLog[0].text).toContain('MISSED APPROACH')
  const missed = dualSetup()
  expect(setUp87nRnav190Final(missed.one, missed.system.flights[0])).toEqual({ ready: true })
  missed.tick()
  expect(missed.one.requestMissedApproach()).toBe(true)
  expect(missed.two.missedApproachActive).toBe(true)
  expect(missed.one.goArounds).toBe(0); expect(missed.two.goArounds).toBe(0) // The synchronized FMS request is not TOGA.
  expect(missed.one.goAround()).toBe(true)
  expect(missed.two.missedApproachActive).toBe(true)
  expect(missed.one.goArounds).toBe(1); expect(missed.two.goArounds).toBe(0)
  const winds = dualSetup()
  winds.one.setCondition('gpsLost', true); winds.one.setCondition('dmeOutage', true); winds.tick(10)
  expect(winds.one.windComputed).toBe(false); expect(winds.two.windComputed).toBe(false)
  winds.one.open('PROG'); winds.one.press('CLR', { held: true }); winds.one.press('CLR', { held: true })
  winds.one.setScratch('090/30'); winds.one.press('LSK3L')
  expect(winds.two.systemWind).toEqual({ direction: 90, speed: 30 })
  expect(winds.two.manualWindEntered).toBe(true)
  expect(winds.one.navigationWindEstimate.east).toBeCloseTo(-30, 6)
  expect(winds.two.navigationWindEstimate.east).toBeCloseTo(-30, 6)
})

// Owner: measured same-type source selection, 100 m civil hysteresis, independent computed-position disagreement.
test('synchronized navigation retains its sensor until the peer is 100 metres better and independent estimates can disagree', () => {
  const { system, one, two, tick } = dualSetup()
  const stimulus = stimulusFor(one)
  stimulus.apply(0, { op: 'override', label: '247', kind: 'FORCE', amount: 0.2 })
  stimulus.apply(1, { op: 'override', label: '247', kind: 'FORCE', amount: 0.15 }); tick()
  expect(one.localNavigationSolution.anp).toBeCloseTo(0.2, 6)
  expect(two.localNavigationSolution.anp).toBeCloseTo(0.15, 6)
  expect(system.navigationSide).toBe(1) // 92.6 m improvement is insufficient.
  stimulus.apply(1, { op: 'override', label: '247', kind: 'FORCE', amount: 0.14 }); tick()
  expect(system.navigationSide).toBe(2) // 111.12 m improvement.
  expect(one.navState.gpsSource).toBe(2); expect(two.navState.gpsSource).toBe(2)
  stimulus.apply(1, { op: 'spoof', northM: 1852, driftEastMps: 0 }); tick()
  // Geometry has its own owner; this protects delivery of the selected estimate into both controllers.
  expect(system.flights[0].guidance.distanceToGo).toBeCloseTo(legGeometry(one.activeLegStart, one.coordinates('MUN')!, one.position).toGo, 6)
  expect(system.flights[1].guidance.distanceToGo).toBeCloseTo(legGeometry(two.activeLegStart, two.coordinates('MUN')!, two.position).toGo, 6)
  stimulus.apply(1, { op: 'clearSpoof' }); tick()
  stimulus.apply(0, { op: 'override', label: '247', kind: 'FORCE', amount: 0.10 }); tick()
  expect(system.navigationSide).toBe(2)
  stimulus.apply(0, { op: 'override', label: '247', kind: 'FORCE', amount: 0.08 }); tick()
  expect(system.navigationSide).toBe(1)
  mode(one)
  stimulus.apply(1, { op: 'spoof', northM: 1852, driftEastMps: 0 }); tick()
  expect(distanceNm(one.localNavigationSolution.position, two.localNavigationSolution.position)).toBeGreaterThan(0.9)
  expect(one.recallList.some(message => message.text === 'GPS-GPS POS DISAGREE')).toBe(true)
  expect(two.recallList.some(message => message.text === 'GPS-GPS POS DISAGREE')).toBe(true)
  expect(one.truePosition).toEqual(two.truePosition)
  const phases = dualSetup(), phaseStimulus = stimulusFor(phases.one)
  for (const index of [0, 1]) {
    phaseStimulus.apply(index, { op: 'override', label: '110', kind: 'FORCE', amount: index === 0 ? 45.31 : 46.0 })
    phaseStimulus.apply(index, { op: 'override', label: '111', kind: 'FORCE', amount: -75.6817 })
    phaseStimulus.apply(index, { op: 'override', label: '120', kind: 'FORCE', amount: 0 })
    phaseStimulus.apply(index, { op: 'override', label: '121', kind: 'FORCE', amount: 0 })
  }
  phases.tick()
  const departure = phases.one.navdb.airport('CYOW')!.position
  expect(distanceNm(phases.one.localNavigationSolution.position, departure)).toBeLessThan(33)
  expect(distanceNm(phases.two.localNavigationSolution.position, departure)).toBeGreaterThan(33)
  phases.tick(20)
  phases.one.setCondition('rnpExceeded', true) // A healthy-computer condition notification must not reset local phase history.
  phases.tick(10)
  expect(phases.system.mode).toBe('SYNC') // Exactly 30 seconds is not "more than 30".
  phases.tick()
  expect(phases.system.mode).toBe('INDEPENDENT')
  expect(phases.one.faultLog.some(fault => fault.text.includes('PHASE DISAGREEMENT'))).toBe(true)
})

// Owner: physical RMS acknowledgement is independent of FMS mode/link; standby swap commits only on acknowledgement.
test('both FMSs tune shared civil devices through feedback and a cross-talk fault only isolates standby entries', () => {
  const { system, one, two, tick } = dualSetup()
  one.setRadio('com1Stby', '123.450')
  expect(two.radioState.com1Stby).toBe('123.450')
  one.swapRadio('com1')
  expect(one.radioState.com1).toBe('121.500'); expect(one.radioState.com1Stby).toBe('123.450')
  tick(); expect(two.radioState.com1).toBe('123.450'); expect(two.radioState.com1Stby).toBe('121.500')
  mode(two); system.setLinkAvailable(false)
  two.setRadio('com1Stby', '128.700')
  expect(one.radioState.com1Stby).toBe('121.500')
  two.setRadio('nav1', '114.50'); expect(one.radioState.nav1).toBe('113.90')
  tick(); expect(one.radioState.nav1).toBe('114.50'); expect(two.radioState.nav1).toBe('114.50')
  two.open('RADIO', 1); two.setScratch('0420'); two.press('LSK1R'); two.setScratch('4321'); two.press('LSK2R'); tick()
  expect(one.radioState.adf2).toBe('0420'); expect(one.radioState.tpdr2).toBe('4321')
  system.rms.injectFailure('com1', true); two.swapRadio('com1'); tick()
  expect(two.radioRequests[0].status).toBe('PENDING'); tick()
  expect(two.radioRequests[0].status).toBe('FAILED')
  expect(one.radioState.com1).toBe('123.450'); expect(two.radioState.com1Stby).toBe('128.700')
  system.rms.injectFailure('com1', false); two.setRadio('com1', '129.100'); tick()
  expect(one.radioState.com1).toBe('129.100'); expect(two.radioState.com1).toBe('129.100')
  expect(system.linked).toBe(false); expect(system.mode).toBe('INDEPENDENT')
})

// Owner: the common plant advances once, and selecting the other computer preserves its motion/independent guidance.
test('selecting FMS 2 guidance never creates or resets another aircraft and FMS 1 failure leaves FMS 2 output usable', () => {
  const { system, one, two, fly } = dualSetup()
  mode(one)
  two.modify(route => { route.legs = [{ kind: 'wpt', ident: 'CYUL' }] }); two.press('EXEC')
  const start = { ...one.truePosition }
  fly(5)
  expect(one.truePosition).toEqual(two.truePosition)
  const travelled = distanceNm(start, one.truePosition)
  expect(travelled).toBeGreaterThan(0.14); expect(travelled).toBeLessThan(0.20) // 120 kt × 5 seconds, single integration.
  const before = { ...one.truePosition }
  system.simulator.selectSpeed(80); system.simulator.selectAltitude(4000); system.simulator.engageVerticalSpeed(500)
  system.selectGuidance(2); expect(two.truePosition).toEqual(before)
  expect(system.simulator.selectedSpeed).toBe(80); expect(system.simulator.selectedAltitude).toBe(4000)
  expect(system.simulator.verticalSpeedTarget).toBe(500)
  one.setCondition('fmsFail', true); fly(5)
  expect(two.hasCondition('fmsFail')).toBe(false)
  expect(system.flights[1].guidance.desiredTrack).not.toBeNull()
  expect(system.flights[0].guidance.desiredTrack).toBeNull()
  expect(one.truePosition).toEqual(two.truePosition)
  expect(distanceNm(before, two.truePosition)).toBeGreaterThan(0.1)
  // The observer must use the one AFCS's recovered HDG selection, not keep an independently engaged LNAV.
  const recovered = dualSetup()
  recovered.one.powerOff(); recovered.fly(1)
  recovered.one.powerOn('COLD', true); recovered.fly(6)
  expect(recovered.system.mode).toBe('INDEPENDENT')
  mode(recovered.one); expect(recovered.system.mode).toBe('SYNC')
  expect(recovered.system.simulator.guidance.mode).toBe('HDG')
  recovered.fly(600)
  expect(recovered.two.activeRoute).toEqual(recovered.one.activeRoute)
  // #1370: source selection must continue the same fuselage estimator, not restart its pitch from zero.
  // A settled heading/IAS leaves physical pitch unchanged over the next quarter-second; continuity is the contract.
  const settled = dualSetup()
  settled.system.simulator.selectHeading(settled.one.heading)
  settled.system.simulator.selectSpeed(120); settled.fly(30)
  const pitchBefore = settled.one.attitude.pitch
  expect(pitchBefore).toBeLessThan(-4) // Positive control: this is a nonzero established rotorcraft attitude.
  expect(settled.one.attitude.bank).toBeCloseTo(0, 8)
  settled.system.selectGuidance(2); settled.fly(0.25)
  expect(settled.two.attitude.pitch).toBeCloseTo(pitchBefore, 8)
  expect(settled.two.attitude.bank).toBeCloseTo(0, 8)
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

// ------------------------------------------------------------------ the rendezvous with a moving waypoint (M300 11-37)

/** A moving waypoint SHIP1 placed `nm` from the aircraft on `bearing`, moving on `track` at `speed`, flown direct. */
function shipDirect(run: ReturnType<typeof setup>, bearing: number, nm: number, track: number, speed: number) {
  const { unit } = run
  Object.assign(unit.wind, { direction: 0, speed: 0 })
  run.fly(1)
  const ship = offset(unit.position, bearing, nm)
  unit.defineMoving('SHIP1', ship, track, speed)
  expect(unit.directTo('SHIP1')).toBeUndefined()
  unit.press('EXEC')
  return ship
}

test('D-R: the rendezvous is the intercept point with the moving waypoint\'s trajectory, flown straight at the ground speed (M300 11-37)', () => {
  const run = setup()
  const { unit } = run
  // SHIP1 20 NM north, moving east at 60 kt, in still air: the intercept is where (60 t)² + 20² = (V t)².
  const ship = shipDirect(run, 0, 20, 90, 60)
  const tas = unit.trueAirspeed!
  const hours = 20 / Math.sqrt(tas * tas - 60 * 60)
  const rendezvous = unit.rendezvousFor(unit.activeRoute, 0)!
  expect(rendezvous).toMatchObject({ achievable: true, condition: 1 })
  expect(rendezvous.ttg!).toBeCloseTo(hours * 3600, 0)
  expect(rendezvous.distanceNm!).toBeCloseTo(tas * hours, 2)
  expect(distanceNm(rendezvous.position!, offset(ship, 90, 60 * hours))).toBeLessThan(0.01)
  // The route flies to the rendezvous point, not to where the ship is now; the MOVING WPT page shows the ship itself.
  expect(distanceNm(unit.coordinates('SHIP1')!, rendezvous.position!)).toBeLessThan(1e-9)
  expect(distanceNm(unit.movingPositionNow('SHIP1')!, ship)).toBeLessThan(0.02)
})

test('D-R: the rendezvous is determined again every 10 s while the time to go is over one minute, then kept; the aircraft meets the ship', () => {
  const run = setup()
  const { unit } = run
  shipDirect(run, 30, 12, 90, 40)
  const computed: { at: number; ttg: number }[] = []
  let met = Infinity
  run.fly(1800, () => {
    const r = unit.rendezvousFor(unit.activeRoute, 0)
    if (!r) return true
    if (computed.at(-1)?.at !== r.computedAt) computed.push({ at: r.computedAt, ttg: r.ttg! })
    met = Math.min(met, distanceNm(unit.position, unit.movingPositionNow('SHIP1')!))
    return false
  })
  expect(active(unit)).not.toBe('SHIP1')
  // Every 10 s while the time to go was over a minute, none after.
  const gaps = computed.slice(1).map((c, i) => (c.at - computed[i].at) / 1000)
  expect(gaps.every(gap => gap === 10)).toBe(true)
  expect(computed.slice(0, -1).every(c => c.ttg > 60)).toBe(true)
  expect(computed.at(-1)!.ttg).toBeLessThanOrEqual(70)
  expect(computed.length).toBeGreaterThan(5)
  // Flown to the rendezvous point, the aircraft passes close to the ship itself.
  expect(met).toBeLessThan(0.3)
})

test('D-R: no interception within 500 NM is unachievable; as the active waypoint, the alert, guidance toward the ship and the roll command invalid', () => {
  const run = setup()
  const { unit, sim } = run
  run.sim.armLnav?.()
  // A ship 600 NM away, almost stationary: beyond 500 NM of travel.
  shipDirect(run, 0, 600, 0, 1)
  run.fly(1)
  const rendezvous = unit.rendezvousFor(unit.activeRoute, 0)!
  expect(rendezvous).toMatchObject({ achievable: false, condition: 1, position: null })
  expect(recalled(unit, 'RENDEZVOUS UNACHIEVABLE')).toBe(true)
  expect(fmsOutputs(unit, sim).rollCommand.status).toBe('NCD')
  expect(sim.lateralMode).not.toBe('LNAV')
  // Guidance is toward the ship itself.
  expect(distanceNm(unit.coordinates('SHIP1')!, unit.movingPositionNow('SHIP1')!)).toBeLessThan(1e-9)
  // At 480 NM it is within reach.
  const near = setup()
  shipDirect(near, 0, 480, 0, 1)
  expect(near.unit.rendezvousFor(near.unit.activeRoute, 0)).toMatchObject({ achievable: true })
  expect(near.unit.rendezvousFor(near.unit.activeRoute, 0)!.distanceNm!).toBeLessThanOrEqual(RENDEZVOUS_RANGE_NM)
  // A ship faster than the aircraft running away is never reached, however close.
  const away = setup()
  shipDirect(away, 0, 5, 0, 400)
  expect(away.unit.rendezvousFor(away.unit.activeRoute, 0)).toMatchObject({ achievable: false })
})

test('D-R: later in the active route, or in the modified route, an unachievable rendezvous is an advisory, not an alert (conditions 2 to 4)', () => {
  // Condition 4: SHIP1 is the first waypoint of the modification (a direct-to not yet executed).
  const four = setup()
  Object.assign(four.unit.wind, { direction: 0, speed: 0 })
  four.unit.defineMoving('SHIP1', offset(four.unit.position, 0, 600), 0, 1)
  expect(four.unit.directTo('SHIP1')).toBeUndefined()
  four.fly(1)
  expect(four.unit.rendezvousFor(four.unit.route, 0)).toMatchObject({ achievable: false, condition: 4 })
  expect(scratch(four.unit)).toBe('RENDEZVOUS UNACHIEVABLE')
  expect(recalled(four.unit, 'RENDEZVOUS UNACHIEVABLE')).toBe(false)
  // Condition 3: after the first waypoint of the modification; condition 2 once executed, still not the active waypoint.
  const later = setup()
  const { unit } = later
  Object.assign(unit.wind, { direction: 0, speed: 0 })
  unit.defineMoving('SHIP1', offset(unit.position, 0, 700), 0, 1)
  press(unit, 'LEGS')
  enter(unit, 'SHIP1', 'LSK2L')
  const at = unit.route.legs.findIndex(leg => leg.kind === 'wpt' && leg.ident === 'SHIP1')
  expect(at).toBeGreaterThan(0)
  later.fly(1)
  expect(unit.rendezvousFor(unit.route, at)).toMatchObject({ achievable: false, condition: 3 })
  unit.press('EXEC')
  later.fly(1)
  expect(unit.rendezvousFor(unit.activeRoute, at)).toMatchObject({ achievable: false, condition: 2 })
  expect(recalled(unit, 'RENDEZVOUS UNACHIEVABLE')).toBe(false)
})

test('D-R: a moving waypoint never expires, and its age is the simulation time since its entry', () => {
  const run = setup()
  const { unit } = run
  unit.defineMoving('SHIP1', offset(unit.position, 0, 10), 90, 10)
  expect(unit.movingAge('SHIP1')).toBe(0)
  run.advance(6 * 3_600_000)
  expect(unit.movingAge('SHIP1')).toBe(6 * 3600)
  // Six hours on it is still defined, 60 NM east of where it was entered.
  expect(unit.movingWaypoints.SHIP1).toEqual({ track: 90, speed: 10 })
  expect(unit.movingPositionNow('SHIP1')).toBeDefined()
})

test('D-R: a restarted scenario restores the moving waypoint\'s epoch from its own clock, never the wall clock', () => {
  // SHIP1 is entered on the MOVING WPT page at scenario time 60 s; the run is replayed from its recorded start.
  const scenario: Scenario = {
    id: 'moving-restart', title: 'moving restart', objective: 'epoch', maxSeconds: 600, startTime: '2026-09-27T14:00:00Z',
    steps: [
      { when: { kind: 'start' }, action: { kind: 'keys', keys: ['INIT_REF', 'LSK5L', 'LSK1L'] } }, // TRUE geometry, independent of MAGVAR.
      { when: { kind: 'time', seconds: 60 }, action: { kind: 'keys', keys: ['INIT_REF', 'NEXT', 'LSK6L'] } },
      { when: { kind: 'start' }, action: { kind: 'type', text: 'SHIP1' } },
      { when: { kind: 'start' }, action: { kind: 'keys', keys: ['LSK1L'] } },
      { when: { kind: 'start' }, action: { kind: 'type', text: 'RDG180/5' } },
      { when: { kind: 'start' }, action: { kind: 'keys', keys: ['LSK2L'] } },
      { when: { kind: 'start' }, action: { kind: 'type', text: '270/20' } },
      { when: { kind: 'start' }, action: { kind: 'keys', keys: ['LSK1R', 'LSK6R'] } },
      // The run's last step, at 600 s.
      { when: { kind: 'time', seconds: 600 }, action: { kind: 'keys', keys: ['PROG'] } },
    ],
  }
  const first = runHeadless(scenario), again = runHeadless(scenario)
  for (const run of [first, again]) {
    expect(run.fms.movingWaypoints.SHIP1).toEqual({ track: 270, speed: 20 })
    // Entered at 60 s of scenario time: at the end (600 s) it is 540 s old, whatever the wall clock was.
    expect(run.fms.movingAge('SHIP1')).toBeCloseTo(540, 0)
  }
  expect(again.fms.movingPositionNow('SHIP1')).toEqual(first.fms.movingPositionNow('SHIP1'))
})
