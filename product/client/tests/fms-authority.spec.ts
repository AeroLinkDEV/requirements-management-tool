import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Active-plan authority (independent review of 27 September, findings R01, R05 and R12): a route edit left in MOD
// must not change what the aircraft flies, where active runways are, or what the ACT pages show. Each test runs the
// same situation with and without the pending edit and requires the active outputs to be identical.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const setup = () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  return { unit, sim, fly }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const activeIdent = (unit: ScriptedFms) => { const leg = unit.activeRoute.legs[0]; return leg?.kind === 'wpt' ? leg.ident : null }
const state = ({ unit, sim }: ReturnType<typeof setup>) => ({
  altitude: unit.altitude, verticalSpeed: unit.verticalSpeed, position: unit.truePosition, track: unit.track,
  active: activeIdent(unit), targetAltitude: sim.guidance.targetAltitude, desiredTrack: sim.guidance.desiredTrack,
})

test('an unexecuted direct-to to the runway does not change the vertical or lateral motion (R01)', () => {
  const control = setup(), edited = setup()
  for (const run of [control, edited]) { run.unit.sequence(); run.fly(1) }
  expect(activeIdent(edited.unit)).toBe('RDG')
  // Prepare DIRECT TO RW24R and leave it in MOD.
  press(edited.unit, 'LEGS')
  typeText(edited.unit, 'RW24R')
  press(edited.unit, 'LSK1L')
  expect(edited.unit.routeStatus).toBe('MOD')
  control.fly(10)
  edited.fly(10)
  expect(state(edited)).toEqual(state(control))
  // ERASE leaves no trace; EXEC then changes the active leg.
  press(edited.unit, 'LSK6L')
  expect(edited.unit.routeStatus).toBe('ACT')
  expect(state(edited)).toEqual(state(control))
})

test('a pending destination with the same runway ident does not move the active runway (R05)', () => {
  const { unit } = setup()
  // A second airport with its own RW24R, far from CYUL.
  unit.loadNavData({
    cycle: { id: 'TEST', from: '', to: '' },
    entries: [{ kind: 'airport', ident: 'CZZZ', name: 'TEST FIELD', position: { lat: 49, lon: -80 }, elevation: 500,
      runways: [{ ident: 'RW24R', threshold: { lat: 49, lon: -80 }, course: 240, elevation: 500, length: 8000 }] }],
    airways: [], procedures: [],
  })
  // A load is an inactive cycle until activated (R06).
  unit.swapCycles()
  const before = unit.coordinates('RW24R')
  expect(unit.activeRoute.dest).toBe('CYUL')
  press(unit, 'RTE')
  typeText(unit, 'CZZZ')
  press(unit, 'LSK1R')
  expect(unit.routeStatus).toBe('MOD')
  expect(unit.route.dest).toBe('CZZZ')
  // The active route still lands at CYUL, so its RW24R is still CYUL's.
  expect(unit.coordinates('RW24R')).toEqual(before)
  // The pending route resolves its own runway when asked in its own context.
  expect(unit.coordinates('RW24R', unit.route)).toEqual({ lat: 49, lon: -80 })
})

test('ACT PROGRESS shows the active route while a direct-to is pending, and the new one after EXEC (R12)', () => {
  const { unit } = setup()
  const first = activeIdent(unit)
  expect(first).not.toBe('TOLGU')
  press(unit, 'LEGS')
  typeText(unit, 'TOLGU')
  press(unit, 'LSK1L')
  expect(unit.routeStatus).toBe('MOD')
  press(unit, 'PROG')
  expect(lines(unit)[0]).toMatch(/^ACT PROGRESS/)
  expect(lines(unit)[2]).toMatch(new RegExp(`^${first}\\b`))
  unit.press('EXEC')
  press(unit, 'PROG')
  expect(lines(unit)[2]).toMatch(/^TOLGU\b/)
})

test('an approach selected but not executed is not the approach being flown', () => {
  const { unit } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  expect(unit.routeStatus).toBe('MOD')
  expect(unit.approachType).toBeNull()
  unit.press('EXEC')
  expect(unit.approachType).not.toBeNull()
})

test('the modified route measures its own runway; the active route keeps its own (R05)', () => {
  const { unit } = setup()
  unit.loadNavData({
    cycle: { id: 'TEST', from: '', to: '' },
    entries: [{ kind: 'airport', ident: 'CZZZ', name: 'TEST FIELD', position: { lat: 49, lon: -80 }, elevation: 500,
      runways: [{ ident: 'RW24R', threshold: { lat: 49, lon: -80 }, course: 240, elevation: 500, length: 8000 }] }],
    airways: [], procedures: [],
  })
  // A load is an inactive cycle until activated (R06).
  unit.swapCycles()
  const runwayAt = unit.activeRoute.legs.findIndex(leg => leg.kind === 'wpt' && leg.ident === 'RW24R')
  expect(runwayAt).toBeGreaterThan(0)
  const active = unit.legGeometry(unit.activeRoute)[runwayAt]
  press(unit, 'RTE')
  typeText(unit, 'CZZZ')
  press(unit, 'LSK1R')
  const pending = unit.legGeometry(unit.route)[unit.route.legs.findIndex(leg => leg.kind === 'wpt' && leg.ident === 'RW24R')]
  // The MOD route's RW24R is CZZZ's, hundreds of miles away; the active route's is unchanged.
  expect(Math.abs(pending!.distance - active!.distance)).toBeGreaterThan(100)
  expect(unit.legGeometry(unit.activeRoute)[runwayAt]).toEqual(active)
})
