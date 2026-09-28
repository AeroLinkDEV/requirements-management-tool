import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import type { CduFunction } from '../src/fmsCdu/variants'

// Engineering controls are not crew actions (independent review finding R15 and its answer Q-A3): Jump moves along
// the active route but refuses at a discontinuity, and only a separately named override crosses one, which is
// recorded. Normal flight stops sequencing at the gap.
const setup = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  return { unit, sim, fly }
}
const typeText = (unit: ScriptedFms, text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }
const offRouteDirect = (unit: ScriptedFms) => {
  unit.press('LEGS')
  typeText(unit, 'ELIBA')
  unit.press('LSK1L')
  unit.press('EXEC')
  expect(unit.activeRoute.legs[0]).toMatchObject({ kind: 'wpt', ident: 'ELIBA' })
  expect(unit.activeRoute.legs[1]).toMatchObject({ kind: 'disco' })
}

test('Jump refuses at a discontinuity and leaves the route as it was (R15)', () => {
  const { unit } = setup()
  offRouteDirect(unit)
  expect(unit.sequence()).toBe('jumped')
  const before = structuredClone(unit.activeRoute.legs)
  expect(before[0]).toMatchObject({ kind: 'disco' })
  expect(unit.sequence()).toBe('discontinuity')
  expect(unit.activeRoute.legs).toEqual(before)
  expect(unit.engineeringLog).toEqual([])
})

test('the override crosses a discontinuity and records what it did (R15)', () => {
  const { unit } = setup()
  offRouteDirect(unit)
  unit.sequence()
  const next = unit.activeRoute.legs[1]
  expect(next?.kind).toBe('wpt')
  const legsBefore = unit.activeRoute.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind === 'disco' ? 'DISC' : leg.path)).join(' ')
  expect(unit.overrideDiscontinuity()).toBe(true)
  // The record identifies the plan it changed and the plan it made: revision, fingerprint and legs (third review D04).
  const identity = unit.engineeringLog[0]?.detail.match(/plan rev (d+) (([0-9a-f]{8}): (.*)) -> rev (d+) (([0-9a-f]{8}): (.*))$/)
  expect(identity, unit.engineeringLog[0]?.detail).not.toBeNull()
  const [, revBefore, printBefore, textBefore, revAfter, printAfter, textAfter] = identity!
  expect(textBefore).toBe(legsBefore)
  expect(textAfter).toBe(legsBefore.replace(/^DISC /, ''))
  expect(Number(revAfter)).toBe(Number(revBefore) + 1)
  expect(printAfter).not.toBe(printBefore)
  expect(unit.planIdentity).toEqual({ revision: Number(revAfter), fingerprint: printAfter })
  expect(unit.activeRoute.legs[0]).toEqual(next)
  expect(unit.engineeringLog).toHaveLength(1)
  expect(unit.engineeringLog[0]).toMatchObject({ action: 'OVERRIDE DISCONTINUITY', detail: expect.stringContaining(next!.kind === 'wpt' ? next!.ident : '') })
  // Nothing to override when the active leg is not a gap.
  expect(unit.overrideDiscontinuity()).toBe(false)
  expect(unit.engineeringLog).toHaveLength(1)
})

test('in normal flight the aircraft stops sequencing at the gap', () => {
  const { unit, sim, fly } = setup()
  offRouteDirect(unit)
  fly(3600)
  // The gap stays at the head of the route and guidance leaves LNAV for heading: nothing joins the route by itself.
  // (The manual's alert list has no discontinuity message, so none is invented.)
  expect(unit.activeRoute.legs[0]).toMatchObject({ kind: 'disco' })
  expect(sim.guidance.mode).toBe('HDG')
})
