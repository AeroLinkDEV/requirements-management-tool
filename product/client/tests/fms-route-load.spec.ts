import { expect, logicTest as test } from './isolated-client-test'
import { screenText, SCRATCHPAD_LINE } from '../src/fmsCdu/screen'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'
import type { CduFunction } from '../src/fmsCdu/variants'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { offset } from '../src/fmsCdu/fmsModel'

// E6, route semantics (plan rev 2 E6, kept by rev 3): CO ROUTE loads DIRECT or INVERSE from SELECT CO ROUTE (M300 3-10,
// 3-11: the INV prefix and the DIRECT/INVERSE toggle). The inverse leg semantics are inferred from BACKTRACK (M300
// 11-35): the origin and destination swap, the waypoints reverse, every leg TF with the first DF, and no airways or
// altitude constraints carried. Fixes resolve by ident at load: a missing one refuses the load, never substituted; a
// moved one is reported.
const clock = () => new Date(Date.UTC(2026, 8, 30, 12, 0, 0))
const fms = () => new ScriptedFms(clock, { userDatabase: { store: memoryUserDatabaseStore(), scope: { userId: 'pilot.one', profileId: 'cma9000-s300-heli-civil' } } })
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const idents = (unit: ScriptedFms) => unit.route.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind))

// Owner: M300 11-35/36 actual passage history and ACT RTE -> MOD LEGS -> EXEC boundaries.
// Prior coverage reverses saved, unflown company routes; it cannot detect a planned-route backtrack facade.
test('BACKTRACK reverses passed fixes, preserves attributes and secondary-route history, and activates only on EXEC', () => {
  const unit = fms()
  unit.replaceLegs([{ kind: 'wpt', ident: 'RDG', qualifier: '/O', altitude: '5000', via: 'T613' }, { kind: 'wpt', ident: 'KILLA' }, { kind: 'wpt', ident: 'AGBEK' }])
  unit.press('EXEC')
  const rdg = unit.coordinates('RDG')!
  unit.sequence()
  unit.definePoint('RDG', { lat: rdg.lat + 0.1, lon: rdg.lon }) // Later data changes cannot move a recorded flown fix.
  unit.copyActiveToSecondary()
  unit.replaceLegs([{ kind: 'wpt', ident: 'ELIBA' }]); unit.press('EXEC')
  unit.sequence()
  unit.activateSecondary(); unit.press('EXEC')
  const active = structuredClone(unit.activeRoute), at = { ...unit.position }
  unit.press('RTE')
  expect(lines(unit)[10]).toMatch(/^<BACKTRACK/)
  unit.press('LSK5L')
  expect(lines(unit)[0]).toMatch(/^MOD RTE 1 LEGS/)
  expect(idents(unit)).toEqual(['KILLA', 'disco', 'BT001', 'ELIBA', 'RDG', 'CYOW'])
  expect(unit.coordinates('BT001', unit.route)).toEqual(at)
  expect(unit.route.legs[4]).toEqual({ kind: 'wpt', ident: 'RDG', path: 'TF', qualifier: '/O', position: rdg })
  expect(unit.activeRoute).toEqual(active)
  unit.press('LSK6L')
  expect(unit.activeRoute).toEqual(active)
  unit.press('RTE'); unit.press('LSK5L'); unit.press('EXEC')
  expect(unit.activeRoute.legs[0]).toMatchObject({ ident: 'KILLA', path: 'TF' })
  expect(unit.activeRoute.legs[1]).toEqual({ kind: 'disco' })
  unit.sequence(); expect(unit.sequence()).toBe('discontinuity')
})

// Owner: exclusion of special-procedure internals, airborne manual DTO PPOS, ground and atomic capacity boundaries.
test('BACKTRACK excludes procedure interiors, keeps holding and SAR origins once, and records only executed airborne manual DTO', () => {
  const unit = fms()
  unit.replaceLegs([{ kind: 'wpt', ident: 'ELIBA', source: 'SID' }, { kind: 'wpt', ident: 'RDG' }, { kind: 'wpt', ident: 'KILLA' }, { kind: 'wpt', ident: 'AGBEK', source: 'STAR' }]); unit.press('EXEC')
  unit.sequence(); unit.sequence()
  unit.directTo('KILLA'); unit.eraseModification()
  unit.directTo('KILLA'); unit.interceptCourse(90); unit.press('EXEC')
  const directPosition = { ...unit.position }
  unit.defineHold('KILLA'); unit.press('EXEC'); unit.sequence(); unit.arrive(true); unit.arrive(true)
  unit.press('RTE'); unit.press('LSK5L')
  expect(idents(unit)).toEqual(['KILLA', 'disco', 'BT002', 'KILLA', 'BT001', 'RDG', 'CYOW'])
  expect(unit.coordinates('BT001', unit.route)).toEqual(directPosition)
  expect(unit.route.legs[3]).toMatchObject({ ident: 'KILLA', qualifier: '/O', path: 'TF' })
  unit.eraseModification()
  unit.setAircraft({ onGround: true })
  unit.press('RTE'); unit.press('LSK5L')
  expect(idents(unit)).toEqual(['KILLA', 'BT001', 'RDG', 'CYOW'])
  expect(unit.route.legs[0]).toMatchObject({ path: 'DF' })
  unit.eraseModification()
  unit.directTo('RDG'); unit.press('EXEC'); unit.sequence()
  unit.press('RTE'); unit.press('LSK5L')
  expect(idents(unit).filter(ident => /^BT/.test(ident))).toEqual(['BT001']) // Ground DTO creates no PPOS.
  const sar = fms()
  sar.activateSar('SECTOR'); sar.press('EXEC'); sar.sequence(); sar.arrive(true); sar.arrive(true); sar.completeSar()
  sar.setAircraft({ onGround: true }); sar.press('RTE'); sar.press('LSK5L')
  expect(idents(sar)).toEqual(['SEC01', 'CYOW'])
  const activeSearch = fms()
  activeSearch.activateSar('SECTOR'); activeSearch.press('EXEC'); activeSearch.sequence()
  activeSearch.press('RTE'); activeSearch.press('LSK5L'); activeSearch.press('EXEC')
  expect(activeSearch.activeRoute.legs[0]).toMatchObject({ ident: 'SEC01', qualifier: '/O' })
  expect(activeSearch.activeRoute.legs[1]).toEqual({ kind: 'disco' })
  expect(activeSearch.sar.status).toBeNull() // Keep the TO fix while leaving its search procedure.
  const codedHold = fms()
  codedHold.replaceLegs([{ kind: 'wpt', ident: 'RDG', source: 'APPR' }]); codedHold.press('EXEC')
  codedHold.defineHold('RDG'); codedHold.press('EXEC'); codedHold.sequence(); codedHold.arrive(true)
  codedHold.setAircraft({ onGround: true }); codedHold.press('RTE'); codedHold.press('LSK5L')
  expect(idents(codedHold)).toEqual(['RDG', 'CYOW']) // Procedure holding origin is the explicit exclusion exception.
  const interiors = fms()
  interiors.replaceLegs([{ kind: 'wpt', ident: 'ELIBA', source: 'SID' }, { kind: 'wpt', ident: 'RDG', special: 'TACTICAL' },
    { kind: 'wpt', ident: 'KILLA', special: 'HOVER' }, { kind: 'wpt', ident: 'AGBEK', source: 'STAR' },
    { kind: 'wpt', ident: 'RDG', source: 'APPR' }, { kind: 'wpt', ident: 'KILLA', source: 'MISSED' }]); interiors.press('EXEC')
  for (let i = 0; i < 6; i++) interiors.sequence()
  interiors.setAircraft({ onGround: true }); interiors.press('RTE'); interiors.press('LSK5L')
  expect(idents(interiors)).toEqual(['CYOW'])
  const movingHistory = fms()
  movingHistory.defineMoving('SHIP1', offset(movingHistory.position, 0, 20), 0, 0)
  movingHistory.replaceLegs([{ kind: 'wpt', ident: 'SHIP1' }]); movingHistory.press('EXEC'); movingHistory.sequence()
  const passedShip = { ...movingHistory.activeLegStart }
  movingHistory.tick() // Completed route no longer owns a rendezvous cache entry.
  movingHistory.defineMoving('SHIP1', offset(movingHistory.position, 0, 600), 0, 0)
  movingHistory.setAircraft({ onGround: true }); movingHistory.press('RTE'); movingHistory.press('LSK5L'); movingHistory.press('EXEC')
  expect(movingHistory.coordinates('SHIP1')).toEqual(passedShip)
  expect(movingHistory.rendezvousRollInvalid).toBe(false)
  expect(movingHistory.activeRoute.legs[0]).toMatchObject({ qualifier: '/O' })
  const capacity = fms(), oldActive = structuredClone(capacity.activeRoute)
  for (let i = 0; i < 50; i++) { capacity.directTo('RDG'); capacity.press('EXEC') }
  const beforeRequest = structuredClone(capacity.activeRoute)
  capacity.press('RTE'); capacity.press('LSK5L')
  expect(lines(capacity)[SCRATCHPAD_LINE].trim()).toBe('TOO MANY TEMP WAYPOINTS')
  expect(capacity.routeStatus).toBe('ACT'); expect(capacity.activeRoute).toEqual(beforeRequest)
  expect(capacity.activeRoute).not.toEqual(oldActive)
  capacity.press('CLR'); capacity.setAircraft({ onGround: true }); capacity.press('LSK5L')
  expect(capacity.route.legs.filter(leg => leg.kind === 'wpt' && leg.temporary)).toHaveLength(50)
})

// Owner: one integrating mission, no jumping to create the route history, and executed deletion on the synchronized peer.
test('a flown outbound leg becomes a synchronized backtrack and returns toward its recorded origin', () => {
  let instant = clock().getTime()
  const system = new DualFmsSystem(() => new Date(instant)), [one, two] = system.computers
  const start = { ...one.position }
  one.definePoint('OUT', { lat: start.lat, lon: start.lon + 0.07 })
  one.replaceLegs([{ kind: 'wpt', ident: 'OUT', qualifier: '/O' }]); one.press('EXEC')
  const fly = (count: number) => { for (let i = 0; i < count; i++) { instant += 1000; system.step(1) } }
  for (let i = 0; i < 600 && one.activeRoute.legs.length; i++) fly(1)
  expect(one.activeRoute.legs).toHaveLength(0)
  one.press('RTE'); one.press('LSK5L')
  expect(idents(one)).toEqual(['BT001', 'OUT', 'CYOW'])
  one.press('EXEC')
  expect(two.activeRoute).toEqual(one.activeRoute)
  system.setOnGround(true); two.press('RTE'); two.press('LSK5L')
  expect(lines(two)[SCRATCHPAD_LINE].trim()).toBe('NO BACKTRACK HISTORY')
  expect(two.routeStatus).toBe('ACT')
  system.setOnGround(false)
  system.simulator.armLnav()
  const before = distanceNm(one.position, one.coordinates('CYOW')!)
  for (let i = 0; i < 900 && one.activeRoute.legs.length; i++) fly(1)
  expect(one.activeRoute.legs).toHaveLength(0)
  expect(distanceNm(one.position, one.coordinates('CYOW')!)).toBeLessThan(before)
  expect(distanceNm(one.position, one.coordinates('CYOW')!)).toBeLessThan(0.2) // Existing guidance owner uses this laboratory passage budget.
})

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
