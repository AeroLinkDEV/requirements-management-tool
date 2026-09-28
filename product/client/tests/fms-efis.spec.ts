import { expect, logicTest as test } from './isolated-client-test'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
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
  expect(bus.verticalFullScaleFt).toBe(150)
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
  const route = (...legs: ({ kind: 'wpt'; ident: string } | { kind: 'disco' })[]) => {
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
  // And a fully resolved route is drawn whole, its first fix active.
  const whole = route(wpt('MUN'), wpt('RDG'))
  expect(whole.activeRoute.map(point => [point.ident, point.active])).toEqual([['MUN', true], ['RDG', false]])
})
