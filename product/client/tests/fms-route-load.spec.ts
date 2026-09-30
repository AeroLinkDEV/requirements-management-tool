import { expect, logicTest as test } from './isolated-client-test'
import { screenText, SCRATCHPAD_LINE } from '../src/fmsCdu/screen'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'
import type { CduFunction } from '../src/fmsCdu/variants'

// E6, route semantics (plan rev 2 E6, kept by rev 3): CO ROUTE loads DIRECT or INVERSE from SELECT CO ROUTE (M300 3-10,
// 3-11: the INV prefix and the DIRECT/INVERSE toggle). The inverse leg semantics are inferred from BACKTRACK (M300
// 11-35): the origin and destination swap, the waypoints reverse, every leg TF with the first DF, and no airways or
// altitude constraints carried. Fixes resolve by ident at load: a missing one refuses the load, never substituted; a
// moved one is reported.
const clock = () => new Date(Date.UTC(2026, 8, 30, 12, 0, 0))
const fms = () => new ScriptedFms(clock, { userDatabase: { store: memoryUserDatabaseStore(), scope: { userId: 'pilot.one', profileId: 'cma9000-s300-heli-civil' } } })
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const idents = (unit: ScriptedFms) => unit.route.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind))

test('SELECT CO ROUTE loads a route DIRECT, as stored: origin, destination, airways and constraints', () => {
  const unit = fms()
  unit.press('RTE')
  unit.press('LSK4R')
  expect(lines(unit)[0]).toMatch(/^SELECT CO ROUTE/)
  expect(lines(unit)[10]).toMatch(/^>DIRECT/)
  // OWUL2: CYOW to CYUL, ELIBA 5000, then RDG, KILLA and AGBEK via T613.
  const row = lines(unit).findIndex(line => /^OWUL2\b/.test(line) || /\bOWUL2$/.test(line))
  unit.press(`LSK${row / 2}${/^OWUL2/.test(lines(unit)[row]) ? 'L' : 'R'}` as CduFunction)
  expect(lines(unit)[0]).toMatch(/^MOD RTE 1/)
  expect(unit.route).toMatchObject({ origin: 'CYOW', dest: 'CYUL', coRoute: 'OWUL2' })
  expect(unit.route.coRouteInverse).toBeUndefined()
  expect(idents(unit)).toEqual(['ELIBA', 'RDG', 'KILLA', 'AGBEK', 'CYUL'])
  expect(unit.route.legs[0]).toMatchObject({ ident: 'ELIBA', altitude: '5000' })
  expect(unit.route.legs[2]).toMatchObject({ ident: 'KILLA', via: 'T613' })
})

test('INVERSE: origin and destination swap, the waypoints reverse, first leg DF, no airways or constraints; INV shown', () => {
  const unit = fms()
  unit.press('RTE')
  unit.press('LSK4R')
  unit.press('LSK5L')
  expect(lines(unit)[10]).toMatch(/^>INVERSE/)
  expect(unit.loadCompanyRoute('OWUL2', 'active', unit.coRouteLoad)).toBe(true)
  expect(unit.route).toMatchObject({ origin: 'CYUL', dest: 'CYOW', coRoute: 'OWUL2', coRouteInverse: true })
  expect(idents(unit)).toEqual(['AGBEK', 'KILLA', 'RDG', 'ELIBA', 'CYOW'])
  expect(unit.route.legs[0]).toEqual({ kind: 'wpt', ident: 'AGBEK', path: 'DF' })
  for (const leg of unit.route.legs.slice(1, 4)) expect(leg).toEqual({ kind: 'wpt', ident: (leg as { ident: string }).ident })
  unit.press('RTE')
  expect(lines(unit)[4]).toMatch(/^INV OWUL2/)
  // Executed, the inversed route is the active plan; loading it DIRECT again clears the prefix.
  unit.press('EXEC')
  expect(unit.activeRoute.coRouteInverse).toBe(true)
  unit.loadCompanyRoute('OWUL2')
  unit.press('EXEC')
  expect(unit.activeRoute.coRouteInverse).toBeUndefined()
  expect(unit.activeRoute.origin).toBe('CYOW')
})

test('a fix not in the active database refuses the load, reported and never substituted', () => {
  const store = memoryUserDatabaseStore()
  const scope = { userId: 'pilot.one', profileId: 'cma9000-s300-heli-civil' }
  const unit = new ScriptedFms(clock, { userDatabase: { store, scope } })
  const doc = JSON.parse(unit.exportUserDatabase())
  doc.routes = [{ name: 'GHOST', origin: 'CYOW', dest: 'CYUL', legs: [{ ident: 'RDG' }, { ident: 'ZZZZZ' }] }]
  expect(unit.importUserDatabase(JSON.stringify(doc))).toEqual({ imported: { waypoints: 0, routes: 1 } })
  const before = structuredClone(unit.route)
  expect(unit.loadCompanyRoute('GHOST')).toBe(false)
  expect(unit.routeLoadReport).toEqual(['ZZZZZ NOT IN DATA BASE'])
  expect(lines(unit)[SCRATCHPAD_LINE].trim()).toBe('NOT IN DATA BASE')
  expect(unit.route).toEqual(before)
  expect(unit.datasetLog[0]).toMatchObject({ action: 'ROUTE NOT LOADED' })
})

test('a fix a user route recorded at another position is reported as moved, and flown where the database has it', () => {
  const unit = fms()
  // Save a route: its fixes are recorded with their positions.
  unit.press('RTE')
  for (const ch of 'MINE') unit.press(`CHAR_${ch}` as CduFunction)
  unit.press('LSK2L')
  unit.press('EXEC')
  unit.press('LSK5R')
  const saved = unit.userRoutes.find(r => r.name === 'MINE')!
  expect(saved.legs.every(leg => leg.position !== undefined)).toBe(true)
  // The stored copy says the first fix was elsewhere (as if the database had moved it since).
  const doc = JSON.parse(unit.exportUserDatabase())
  const moved = doc.routes.find((r: { name: string }) => r.name === 'MINE')
  moved.name = 'MINE2'
  moved.legs[0].position = { lat: moved.legs[0].position.lat + 0.1, lon: moved.legs[0].position.lon }
  expect(unit.importUserDatabase(JSON.stringify(doc))).toMatchObject({ imported: { routes: 1 } })
  expect(unit.loadCompanyRoute('MINE2')).toBe(true)
  expect(unit.routeLoadReport).toEqual([`${moved.legs[0].ident} MOVED`])
  expect(lines(unit)[SCRATCHPAD_LINE].trim()).toBe('ROUTE FIX MOVED')
  expect(unit.datasetLog[0]).toMatchObject({ action: 'ROUTE FIX MOVED' })
  // The unmoved route loads without a report.
  expect(unit.loadCompanyRoute('MINE')).toBe(true)
  expect(unit.routeLoadReport).toEqual([])
})
