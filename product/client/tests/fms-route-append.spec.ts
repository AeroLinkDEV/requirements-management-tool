import { expect, logicTest as test } from './isolated-client-test'
import { screenText } from '../src/fmsCdu/screen'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'
import type { CduFunction } from '../src/fmsCdu/variants'

// #1369, airborne route append (M300 3-10…3-13). Airborne, selecting a custom (CO ROUTE) or user route replaces the
// destination with the route's, keeps the active waypoint, inserts a discontinuity after it followed by the route's
// waypoints, and replaces a procedure in the plan as a whole; the RTE page shows "+" after the route name. v1 is
// airborne throughout (DEC-148), so every load into the active route appends. The secondary flight plan is not flown
// and still takes the route whole. Stored-route INVERSE is not BACKTRACK (#1351); its leg rules stay inferred (E6).
const clock = () => new Date(Date.UTC(2026, 8, 30, 12, 0, 0))
const fms = () => new ScriptedFms(clock, { userDatabase: { store: memoryUserDatabaseStore(), scope: { userId: 'pilot.one', profileId: 'cma9000-s300-heli-civil' } } })
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const idents = (route: { legs: ScriptedFms['route']['legs'] }) =>
  route.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind === 'cond' ? `(${leg.path})` : '(disco)'))
const type = (unit: ScriptedFms, text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }
/** Selects a route on SELECT CO ROUTE by its name, in whichever column it is listed. */
const select = (unit: ScriptedFms, name: string) => {
  const row = lines(unit).findIndex(line => new RegExp(`^${name}\\b|\\b${name}$`).test(line))
  expect(row).toBeGreaterThan(0)
  unit.press(`LSK${row / 2}${new RegExp(`^${name}\\b`).test(lines(unit)[row]) ? 'L' : 'R'}` as CduFunction)
}

test('airborne, SELECT CO ROUTE appends after the active waypoint: a discontinuity, the route, its destination, "+"', () => {
  const unit = fms()
  // The demonstration flight is airborne, flying to MUN, from CYOW to CYUL.
  expect(unit.activeRoute.legs[0]).toMatchObject({ ident: 'MUN' })
  unit.press('RTE')
  unit.press('LSK4R')
  select(unit, 'OWUL2')
  expect(lines(unit)[0]).toMatch(/^MOD RTE 1/)
  // OWUL2 is CYOW to CYUL: ELIBA 5000, then RDG, KILLA and AGBEK via T613. Its airways and constraints are kept.
  expect(idents(unit.route)).toEqual(['MUN', '(disco)', 'ELIBA', 'RDG', 'KILLA', 'AGBEK', 'CYUL'])
  expect(unit.route.legs[0]).toEqual(unit.activeRoute.legs[0])
  expect(unit.route.legs[2]).toMatchObject({ ident: 'ELIBA', altitude: '5000' })
  expect(unit.route.legs[4]).toMatchObject({ ident: 'KILLA', via: 'T613' })
  expect(unit.route).toMatchObject({ origin: 'CYOW', dest: 'CYUL', coRoute: 'OWUL2', coRouteAppended: true })
  expect(lines(unit)[4]).toMatch(/^OWUL2\+/)
  // The page between: the discontinuity follows the active waypoint.
  unit.press('NEXT')
  expect(lines(unit)[4]).toMatch(/DISCONTINUITY/)
  // A modification only: guidance is unchanged until EXEC, and ERASE leaves the active route as it was.
  expect(unit.activeRoute.coRoute).toBe('OWUL1')
  unit.press('LSK6L')
  expect(unit.activeRoute.coRouteAppended).toBeUndefined()
  expect(idents(unit.route)[1]).not.toBe('(disco)')
  unit.press('RTE'); unit.press('LSK4R'); select(unit, 'OWUL2'); unit.press('EXEC')
  expect(lines(unit)[0]).toMatch(/^ACT RTE 1/)
  expect(idents(unit.activeRoute)).toEqual(['MUN', '(disco)', 'ELIBA', 'RDG', 'KILLA', 'AGBEK', 'CYUL'])
  expect(lines(unit)[4]).toMatch(/^OWUL2\+/)
})

test('airborne, the destination changes to the route\'s and a STAR, approach and missed approach are replaced whole', () => {
  const unit = fms()
  // A STAR and an approach into CYUL first (as in fms-planning), executed.
  unit.press('DEP_ARR'); unit.press('LSK1R'); unit.press('LSK1L'); unit.press('LSK2L'); unit.press('LSK1R'); unit.press('LSK2R')
  unit.press('EXEC')
  expect(unit.activeRoute.star).toBeDefined()
  expect(unit.activeRoute.approach).toBeDefined()
  expect(unit.activeRoute.legs.some(leg => leg.kind !== 'disco' && leg.source === 'MISSED')).toBe(true)
  // A user route to another destination, saved from a plan to CYOW: RDG then MUN.
  const doc = JSON.parse(unit.exportUserDatabase())
  doc.routes = [{ name: 'HOME1', origin: 'CYUL', dest: 'CYOW', legs: [{ ident: 'RDG' }, { ident: 'MUN' }] }]
  expect(unit.importUserDatabase(JSON.stringify(doc))).toMatchObject({ imported: { routes: 1 } })
  unit.press('RTE'); unit.press('LSK4R'); select(unit, 'HOME1')
  expect(unit.route).toMatchObject({ origin: 'CYOW', dest: 'CYOW', coRoute: 'HOME1', coRouteAppended: true })
  expect(unit.route.star).toBeUndefined()
  expect(unit.route.approach).toBeUndefined()
  expect(unit.route.legs.some(leg => leg.kind !== 'disco' && leg.source !== undefined)).toBe(false)
  expect(idents(unit.route)).toEqual(['MUN', '(disco)', 'RDG', 'MUN', 'CYOW'])
  expect(lines(unit)[4]).toMatch(/^HOME1\+/)
})

test('airborne INVERSE appends the reversed route to the stored origin, without the first-leg DF; "INV" and "+" shown', () => {
  const unit = fms()
  unit.press('RTE'); unit.press('LSK4R'); unit.press('LSK5L')
  expect(lines(unit)[10]).toMatch(/^>INVERSE/)
  select(unit, 'OWUL2')
  expect(unit.route).toMatchObject({ origin: 'CYOW', dest: 'CYOW', coRoute: 'OWUL2', coRouteInverse: true, coRouteAppended: true })
  expect(idents(unit.route)).toEqual(['MUN', '(disco)', 'AGBEK', 'KILLA', 'RDG', 'ELIBA', 'CYOW'])
  for (const leg of unit.route.legs.slice(2, 6)) expect(leg).toEqual({ kind: 'wpt', ident: (leg as { ident: string }).ident })
  expect(lines(unit)[4]).toMatch(/^INV OWUL2\+/)
})

test('a user route saved on RTE appends airborne the same way; a new route name drops the "+"', () => {
  const unit = fms()
  unit.press('RTE')
  type(unit, 'MINE')
  unit.press('LSK2L'); unit.press('EXEC'); unit.press('LSK5R')
  expect(unit.userRoutes.map(route => route.name)).toEqual(['MINE'])
  // MINE starts at MUN, the active waypoint too: the active one is kept and the route follows the discontinuity.
  expect(unit.loadCompanyRoute('MINE')).toBe(true)
  expect(idents(unit.route).slice(0, 3)).toEqual(['MUN', '(disco)', 'MUN'])
  expect(unit.route).toMatchObject({ coRoute: 'MINE', coRouteAppended: true, dest: 'CYUL' })
  unit.press('RTE')
  expect(lines(unit)[4]).toMatch(/^MINE\+/)
  type(unit, 'LIFE2')
  unit.press('LSK2L')
  expect(unit.route.coRouteAppended).toBeUndefined()
  expect(lines(unit)[4]).toMatch(/^LIFE2\s/)
})

test('the secondary flight plan is not flown: a route loaded into it replaces it whole, without "+"', () => {
  const unit = fms()
  expect(unit.loadCompanyRoute('OWUL2', 'secondary')).toBe(true)
  const secondary = unit.secondary!
  expect(idents(secondary)).toEqual(['ELIBA', 'RDG', 'KILLA', 'AGBEK', 'CYUL'])
  expect(secondary.coRouteAppended).toBeUndefined()
  // INVERSE into the secondary keeps the inferred replacement rules: origin and destination swap, the first leg DF.
  expect(unit.loadCompanyRoute('OWUL2', 'secondary', 'INVERSE')).toBe(true)
  expect(unit.secondary).toMatchObject({ origin: 'CYUL', dest: 'CYOW', coRouteInverse: true })
  expect(unit.secondary!.legs[0]).toEqual({ kind: 'wpt', ident: 'AGBEK', path: 'DF' })
})
