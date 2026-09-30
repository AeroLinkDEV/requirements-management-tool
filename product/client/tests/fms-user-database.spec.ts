import { expect, logicTest as test } from './isolated-client-test'
import { formatPosition } from '../src/fmsCdu/fmsModel'
import { LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { screenText, SCRATCHPAD_LINE } from '../src/fmsCdu/screen'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { USER_WAYPOINT_CAPACITY, memoryUserDatabaseStore, type UserDatabaseStore } from '../src/fmsCdu/userDatabase'
import type { CduFunction } from '../src/fmsCdu/variants'

// E5, the user database (plan rev 2 E5, kept by rev 3; DEC-146 D3; M300 11-23…11-31): user waypoints, among them one
// made from a MARK ON TOP (NEW USER WPT), and user routes, kept behind a storage interface per signed-in user and
// profile, with a versioned export and an all-or-nothing import.
const clock = () => new Date(Date.UTC(2026, 8, 30, 12, 0, 0))
const scope = { userId: 'pilot.one', profileId: 'cma9000-s300-heli-civil' }
const fms = (store: UserDatabaseStore, userId = scope.userId, profileId = scope.profileId) => new ScriptedFms(clock, { userDatabase: { store, scope: { userId, profileId } } })
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trim()
const type = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : ch === '-' ? 'PLUSMINUS' : `CHAR_${ch}` as CduFunction)
}

test('MARK ON TOP, then NEW USER WPT: stored for this user and profile, and there after a restart', () => {
  const store = memoryUserDatabaseStore()
  const unit = fms(store)
  unit.addMark()
  const mark = unit.markList.at(-1)!.position
  // PREDEF WPT 2/2, <NEW USER WPT: USER WPT 1/2 with the mark on top as its reference (ONTOP) and its position.
  unit.press('INIT_REF')
  unit.press('LSK2R')
  unit.press('NEXT')
  expect(lines(unit)[0]).toMatch(/^PREDEF WPT\s+2\/2/)
  unit.press('LSK6L')
  expect(lines(unit)[0]).toMatch(/^USER WPT\s+1\/2/)
  expect(lines(unit)[8]).toMatch(/^ONTOP/)
  expect(lines(unit)[2]).toContain(formatPosition(mark))
  expect(lines(unit)[1]).toContain(`FREE=${USER_WAYPOINT_CAPACITY}`)
  // An ident, then SAVE? CONFIRM.
  type(unit, 'SGT1')
  unit.press('LSK1L')
  expect(lines(unit)[11]).toMatch(/SAVE\?/)
  unit.press('LSK6R')
  expect(scratch(unit)).toBe('SGT1 STORED')
  expect(unit.userWaypoints).toEqual([{ ident: 'SGT1', position: mark, type: 'FIXED' }])
  expect(unit.coordinates('SGT1')).toEqual(mark)
  // A new session for the same user and profile reads it back, and can fly to it.
  const again = fms(store)
  expect(again.userWaypoints).toEqual([{ ident: 'SGT1', position: mark, type: 'FIXED' }])
  expect(again.directTo('SGT1')).toBeUndefined()
  again.press('EXEC')
  expect(again.activeRoute.legs[0]).toMatchObject({ kind: 'wpt', ident: 'SGT1' })
  expect(again.coordinates('SGT1')).toEqual(mark)
})

test('the store keeps users and profiles apart: the same browser storage, another user or profile, sees nothing', () => {
  const store = memoryUserDatabaseStore()
  const unit = fms(store)
  expect(unit.createUserWaypoint('ALPHA', { lat: 40.9, lon: -72.4 })).toBeUndefined()
  expect(fms(store).userWaypoints.map(w => w.ident)).toEqual(['ALPHA'])
  expect(fms(store, 'pilot.two').userWaypoints).toEqual([])
  expect(fms(store, scope.userId, LAB_AIRLINE_VNAV_PROFILE.id).userWaypoints).toEqual([])
  expect(store.entries.size).toBe(1)
})

test('a user waypoint entered by position on REF NAV DATA; bad idents, duplicates and a full database are refused', () => {
  const unit = fms(memoryUserDatabaseStore())
  unit.press('INIT_REF')
  unit.press('LSK1R')
  unit.press('LSK6R')
  expect(lines(unit)[0]).toMatch(/^USER WPT\s+1\/2/)
  // Incomplete: no SAVE? yet.
  expect(lines(unit)[11] ?? '').not.toMatch(/SAVE\?/)
  type(unit, 'N4050.0W07230.0')
  unit.press('LSK1R')
  type(unit, 'BRAVO')
  unit.press('LSK1L')
  unit.press('LSK6R')
  expect(unit.userWaypoints.map(w => [w.ident, w.position])).toEqual([['BRAVO', { lat: 40 + 50 / 60, lon: -(72 + 30 / 60) }]])
  // An ident already in use (the navigation database's YOW, or a stored user waypoint) is refused.
  expect(unit.createUserWaypoint('YOW', { lat: 45, lon: -75 })).toBe('in-use')
  expect(unit.createUserWaypoint('BRAVO', { lat: 41, lon: -72 })).toBe('in-use')
  expect(unit.createUserWaypoint('TOO-LONG', { lat: 41, lon: -72 })).toBe('invalid')
  // On the CDU the duplicate says so, and nothing is stored.
  unit.userWaypointDraft = { ident: 'BRAVO', position: { lat: 41, lon: -72 }, ref: null }
  unit.open('USER_WPT')
  unit.press('LSK6R')
  expect(scratch(unit)).toBe('DUPLICATE IDENT')
  expect(unit.userWaypoints).toHaveLength(1)
  // 460 at most.
  const full = fms(memoryUserDatabaseStore())
  for (let i = 0; i < USER_WAYPOINT_CAPACITY; i += 1) expect(full.createUserWaypoint(`U${i}`, { lat: 10 + i / 1000, lon: 10 })).toBeUndefined()
  expect(full.userWaypointsFree).toBe(0)
  expect(full.createUserWaypoint('LAST', { lat: 20, lon: 20 })).toBe('full')
})

test('a user route saved on RTE is kept, and CO ROUTE loads it in a new session', () => {
  const store = memoryUserDatabaseStore()
  const unit = fms(store)
  unit.press('RTE')
  type(unit, 'MYRTE')
  unit.press('LSK2L')
  unit.press('EXEC')
  unit.press('LSK5R')
  expect(unit.userRoutes.map(r => r.name)).toEqual(['MYRTE'])
  const legs = unit.userRoutes[0].legs.map(l => l.ident)
  expect(legs.length).toBeGreaterThan(0)
  const again = fms(store)
  expect(again.storedRoutes.map(r => r.name)).toContain('MYRTE')
  expect(again.loadCompanyRoute('MYRTE')).toBeTruthy()
  // The database's own company routes are still offered beside it.
  expect(again.storedRoutes.map(r => r.name)).toEqual(expect.arrayContaining(['MYRTE', 'OWUL1', 'OWUL2']))
})

test('export and import round-trip a versioned document; importing the same again adds nothing', () => {
  const from = fms(memoryUserDatabaseStore())
  from.createUserWaypoint('ALPHA', { lat: 40.9, lon: -72.4 })
  from.createUserWaypoint('BRAVO', { lat: 40.8, lon: -72.5 })
  const text = from.exportUserDatabase()
  const doc = JSON.parse(text)
  expect(doc).toMatchObject({ schema: 'aerolink.fms.user-database', version: 1, userId: scope.userId, profileId: scope.profileId })
  const into = fms(memoryUserDatabaseStore(), 'pilot.two')
  expect(into.importUserDatabase(text)).toEqual({ imported: { waypoints: 2, routes: 0 } })
  expect(into.userWaypoints.map(w => w.ident)).toEqual(['ALPHA', 'BRAVO'])
  expect(into.importUserDatabase(text)).toEqual({ imported: { waypoints: 0, routes: 0 } })
})

test('a malformed import writes nothing, even where most of it is valid', () => {
  const store = memoryUserDatabaseStore()
  const unit = fms(store)
  unit.createUserWaypoint('KEEP', { lat: 40, lon: -72 })
  const before = [...store.entries.values()][0]
  const doc = JSON.parse(unit.exportUserDatabase())
  doc.waypoints = [{ ident: 'GOOD', position: { lat: 41, lon: -72 }, type: 'FIXED' }, { ident: 'BAD', position: { lat: 95, lon: -72 }, type: 'FIXED' }]
  const outcome = unit.importUserDatabase(JSON.stringify(doc))
  expect(outcome).toEqual({ refused: ['waypoint BAD: position is not a latitude and longitude'] })
  expect(unit.userWaypoints.map(w => w.ident)).toEqual(['KEEP'])
  expect([...store.entries.values()][0]).toBe(before)
  expect(unit.importUserDatabase('not json')).toEqual({ refused: ['not JSON'] })
  expect(unit.importUserDatabase(JSON.stringify({ ...doc, version: 2 }))).toMatchObject({ refused: [expect.stringMatching(/version 2 is not supported/), expect.anything()] })
})

test('a collision is reported and nothing is overwritten: the same ident at another position, or an ident in use', () => {
  const store = memoryUserDatabaseStore()
  const unit = fms(store)
  unit.createUserWaypoint('ALPHA', { lat: 40.9, lon: -72.4 })
  const before = [...store.entries.values()][0]
  const doc = JSON.parse(unit.exportUserDatabase())
  doc.waypoints = [{ ident: 'ALPHA', position: { lat: 41.5, lon: -72.4 }, type: 'FIXED' }, { ident: 'NEW', position: { lat: 41, lon: -72 }, type: 'FIXED' }]
  expect(unit.importUserDatabase(JSON.stringify(doc))).toEqual({ refused: ['user waypoint ALPHA is already stored at another position'] })
  doc.waypoints = [{ ident: 'YOW', position: { lat: 41, lon: -72 }, type: 'FIXED' }]
  expect(unit.importUserDatabase(JSON.stringify(doc))).toEqual({ refused: ['YOW is already a navigation database or pilot waypoint'] })
  expect(unit.userWaypoints).toEqual([{ ident: 'ALPHA', position: { lat: 40.9, lon: -72.4 }, type: 'FIXED' }])
  expect([...store.entries.values()][0]).toBe(before)
})

test('a stored database that cannot be read is left as it was: named, and nothing is saved over it', () => {
  const store = memoryUserDatabaseStore()
  store.write(scope, '{ "schema": "something else" }')
  const unit = fms(store)
  expect(unit.userDatabaseProblem).toMatch(/^stored user database not read: schema is not aerolink\.fms\.user-database/)
  expect(unit.userWaypoints).toEqual([])
  expect(unit.createUserWaypoint('ALPHA', { lat: 40, lon: -72 })).toBe('not-saved')
  expect(scratch(unit)).toBe('USER DB NOT SAVED')
  expect(store.read(scope)).toBe('{ "schema": "something else" }')
})

test('a store that refuses the write (quota) keeps the database as it was', () => {
  const refusing: UserDatabaseStore = { read: () => null, write: () => { throw new Error('QuotaExceededError') } }
  const unit = fms(refusing)
  expect(unit.createUserWaypoint('ALPHA', { lat: 40, lon: -72 })).toBe('not-saved')
  expect(unit.userWaypoints).toEqual([])
  expect(scratch(unit)).toBe('USER DB NOT SAVED')
})
