import { expect, logicTest as test } from './isolated-client-test'
import { aircraftData, fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// The FMS output bus the EFIS draws from (efis.ts): what the FMS publishes, each word with a status, so the displays
// show only what the FMS actually computes, remove it when the FMS fails, and never show a pending edit as active.
const setup = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number, each?: () => boolean | void) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1); if (each?.()) return } }
  return { unit, sim, fly }
}
const active = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }

test('in LNAV the bus carries desired track, cross-track, roll command and the TO waypoint as valid words', () => {
  const { unit, sim, fly } = setup()
  fly(20)
  const bus = fmsOutputs(unit, sim)
  expect(bus.desiredTrack).toEqual({ value: sim.guidance.desiredTrack, status: 'NORMAL' })
  expect(bus.crossTrack).toEqual({ value: sim.guidance.crossTrack, status: 'NORMAL' })
  expect(bus.rollCommand.status).toBe('NORMAL')
  expect(bus.toWaypoint).toEqual({ value: active(unit), status: 'NORMAL' })
  expect(bus.distanceToGo.value).toBeCloseTo(sim.guidance.distanceToGo!, 6)
  expect(bus.lateralMode).toBe('LNAV')
  // Full-scale deviation follows the phase: 1 NM in the terminal area where the demonstration starts.
  expect(unit.flightPhase).toBe('TERMINAL')
  expect(bus.lateralFullScaleNm).toBe(1)
  expect(bus.activeRoute[0]).toMatchObject({ ident: active(unit), active: true })
})

test('a pending edit appears only as the modified route; the active route and steering are unchanged', () => {
  const { unit, sim, fly } = setup()
  fly(5)
  const before = fmsOutputs(unit, sim)
  unit.press('LEGS')
  for (const ch of 'TOLGU') unit.press(`CHAR_${ch}` as CduFunction)
  unit.press('LSK1L')
  const after = fmsOutputs(unit, sim)
  expect(after.modifiedRoute?.[0].ident).toBe('TOLGU')
  expect(after.activeRoute).toEqual(before.activeRoute)
  expect(after.toWaypoint).toEqual(before.toWaypoint)
  expect(after.desiredTrack).toEqual(before.desiredTrack)
})

test('a failed FMS publishes failure words and no route; the modes shown are the basic reversion modes', () => {
  const { unit, sim, fly } = setup()
  fly(20)
  unit.setCondition('fmsFail', true)
  fly(1)
  const bus = fmsOutputs(unit, sim)
  expect(bus.failed).toBe(true)
  for (const word of [bus.desiredTrack, bus.crossTrack, bus.verticalDeviation, bus.rollCommand, bus.distanceToGo, bus.toWaypoint, bus.targetSpeed, bus.targetAltitude])
    expect(word).toEqual({ value: null, status: 'FAIL' })
  expect(bus.activeRoute).toEqual([])
  expect(bus.lateralMode).toBe('HDG HOLD')
  expect(bus.verticalMode).toBe('ALT HOLD')
})

test('on final the vertical deviation is advisory until the approach captures, then coupled; without integrity nothing is armed', () => {
  const { unit, sim, fly } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  unit.armApproach(true)
  fly(3 * 3600, () => active(unit) === 'FERDI')
  expect(fmsOutputs(unit, sim).verticalArmed).toEqual(['LPV'])
  let coupled = false
  fly(3 * 3600, () => { const bus = fmsOutputs(unit, sim); if (bus.verticalSource === 'APPR' && bus.verticalCoupled) { coupled = true; return true } })
  expect(coupled).toBe(true)
  const bus = fmsOutputs(unit, sim)
  expect(bus.approach).toEqual({ type: 'LPV', state: 'CAPTURED' })
  expect(bus.verticalMode).toBe('APPR')
  // On an RNAV final the full scale is the GPS's angular scaling beside 117 (GPS phase 3b), not a fixed 150 ft.
  expect(bus.verticalFullScaleFt).toBeCloseTo(unit.gpsApproach!.scale!.verticalFullScaleFt, 9)
  // Integrity lost: the approach cannot be armed as available.
  unit.setCondition('gpsIntegrity', true)
  fly(2)
  expect(fmsOutputs(unit, sim).verticalArmed).toEqual([])
})

test('the top of descent is placed on the route, the stated distance ahead along it', () => {
  const { unit, sim, fly } = setup()
  fly(3 * 3600, () => unit.profile().topOfDescent !== null && unit.profile().topOfDescent! < 30)
  const bus = fmsOutputs(unit, sim)
  expect(bus.topOfDescent).not.toBeNull()
  expect(bus.endOfDescent).not.toBeNull()
})

test('unarmed on final, the glidepath deviation is shown as advisory information, not coupled guidance', () => {
  const { unit, sim, fly } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  fly(3 * 3600, () => active(unit) === 'RW24R')
  fly(5)
  const bus = fmsOutputs(unit, sim)
  expect(bus.verticalSource).toBe('APPR')
  expect(bus.verticalCoupled).toBe(false)
  expect(bus.verticalDeviation.status).toBe('NORMAL')
  // Held at the FAF altitude above a descending path: well above it, and not being flown down it.
  expect(bus.verticalDeviation.value!).toBeGreaterThan(0)
  expect(bus.approach.state).toBe('OFF')
})

test('the ND route stops at a fix without a position and marks only the active leg active (fourth review E03)', () => {
  const route = (...legs: ({ kind: 'wpt'; ident: string } | { kind: 'disco' } | { kind: 'cond'; path: 'CA'; course: number; altitude: number })[]) => {
    const { unit, sim } = setup()
    unit.replaceLegs(legs)
    unit.press('EXEC')
    return fmsOutputs(unit, sim)
  }
  const wpt = (ident: string) => ({ kind: 'wpt' as const, ident })
  // GAPX is in no cycle: the plan has no geometry past MUN, so nothing is drawn beyond it.
  const middle = route(wpt('MUN'), wpt('GAPX'), wpt('RDG'))
  expect(middle.activeRoute.map(point => [point.ident, point.active])).toEqual([['MUN', true]])
  // The active fix itself unresolved: nothing is drawn and nothing is marked active, while the TO waypoint stays GAPX.
  const first = route(wpt('GAPX'), wpt('RDG'))
  expect(first.activeRoute).toEqual([])
  expect(first.toWaypoint.value).toBe('GAPX')
  // Control: an explicit discontinuity stops the route the same way.
  const disco = route(wpt('MUN'), { kind: 'disco' }, wpt('RDG'))
  expect(disco.activeRoute.map(point => point.ident)).toEqual(['MUN'])
  // A conditional leg active (a climb to an altitude): the fix after it is drawn but is not the active leg.
  const climbing = route({ kind: 'cond', path: 'CA', course: 237, altitude: 1000 }, wpt('RDG'))
  expect(climbing.activeRoute.map(point => [point.ident, point.active])).toEqual([['RDG', false]])
  // And a fully resolved route is drawn whole, its first fix active.
  const whole = route(wpt('MUN'), wpt('RDG'))
  expect(whole.activeRoute.map(point => [point.ident, point.active])).toEqual([['MUN', true], ['RDG', false]])
})

test('without measurable progress the bus publishes no ETA, rather than one from an invented speed (Stage B1)', () => {
  const { unit, sim, fly } = setup()
  fly(20)
  expect(fmsOutputs(unit, sim).eta.status).toBe('NORMAL')
  // A headwind equal to the airspeed holds the aircraft over the ground: the distance to go stops shrinking.
  // Flown on a held heading straight into it, so nothing turns the aircraft out of the wind.
  sim.selectHeading(unit.heading)
  unit.wind.direction = unit.heading
  unit.wind.speed = sim.tas
  fly(30)
  expect(unit.groundSpeed).toBeLessThan(1)
  expect(fmsOutputs(unit, sim).eta).toEqual({ value: null, status: 'NCD' })
})

test('under the helicopter profile the FMS commands no altitude or speed: the bus says so, and the crew selections are aircraft data (Stage B3)', () => {
  const { unit, sim, fly } = setup()
  fly(5)
  const bus = fmsOutputs(unit, sim)
  expect(bus.targetAltitude).toEqual({ value: null, status: 'NCD' })
  expect(bus.targetSpeed).toEqual({ value: null, status: 'NCD' })
  sim.selectAltitude(5000)
  sim.selectSpeed(90)
  expect(aircraftData(unit, sim)).toMatchObject({ selectedAltitude: 5000, selectedSpeed: 90 })
  // The laboratory airline-style VNAV profile keeps the FMS targets, and has no crew selections to show.
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const lab = new ScriptedFms(() => new Date(now), { profile: LAB_AIRLINE_VNAV_PROFILE })
  const labSim = new FlightSimulator(lab)
  for (let t = 0; t < 5; t += 1) { now += 1000; labSim.step(1) }
  expect(fmsOutputs(lab, labSim).targetSpeed.status).toBe('NORMAL')
  expect(aircraftData(lab, labSim)).toMatchObject({ selectedAltitude: null, selectedSpeed: null })
})

test('moving away from the active waypoint, or stopped over the ground, is no progress: no ETA anywhere, never NaN (review of B1)', () => {
  const { unit, sim, fly } = setup()
  fly(5)
  // Stopped: the helicopter's speed selected to zero in calm air.
  unit.wind.speed = 0
  sim.selectSpeed(0)
  fly(90)
  expect(unit.groundSpeed).toBeLessThan(1)
  expect(fmsOutputs(unit, sim).eta.status).toBe('NCD')
  unit.press('PROG')
  expect(screenText(unit.screen()).join('\n')).not.toMatch(/NaN/)
  // Drifting away: a 40 kt wind from ahead of the leg carries the stopped aircraft backwards; the ground speed is 40 kt,
  // but the aircraft is not closing on the waypoint.
  unit.wind.direction = unit.track
  unit.wind.speed = 40
  fly(30)
  expect(unit.groundSpeed).toBeGreaterThan(30)
  expect(unit.closureSpeed).toBeLessThan(0)
  expect(fmsOutputs(unit, sim).eta.status).toBe('NCD')
  expect(screenText(unit.screen()).join('\n')).not.toMatch(/NaN|\d{4}\.\dZ/)
})
