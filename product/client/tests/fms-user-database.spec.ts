import { expect, logicTest as test } from './isolated-client-test'
import { distanceNm, formatPosition, offset } from '../src/fmsCdu/fmsModel'
import { LAB_AIRLINE_VNAV_PROFILE } from '../src/fmsCdu/profile'
import { screenText, SCRATCHPAD_LINE } from '../src/fmsCdu/screen'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { USER_WAYPOINT_CAPACITY, memoryUserDatabaseStore, movingUserWaypointPosition, type MovingUserWaypoint, type UserDatabaseStore } from '../src/fmsCdu/userDatabase'
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
  expect(unit.importUserDatabase(JSON.stringify(doc))).toEqual({ refused: ['user waypoint ALPHA is already stored with another position or motion'] })
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

// M300 11-25: TYPE toggled to MOVING, with the track and ground speed at 2R. The stored record carries the epoch, the
// simulation time at which its position held (rev 2 D-R epoch; rev 3 D-R restart), so a restart places it on the
// simulation clock, never the wall clock.
test('a moving user waypoint is stored with its track, ground speed and epoch, and a restart places it by the simulation clock', () => {
  const store = memoryUserDatabaseStore()
  let now = Date.UTC(2026, 8, 30, 12, 0, 0)
  const at = () => new Date(now)
  const session = () => new ScriptedFms(at, { userDatabase: { store, scope } })
  const unit = session()
  unit.press('INIT_REF')
  unit.press('LSK1R')
  unit.press('LSK6R')
  expect(lines(unit)[3]).toMatch(/TRK\/GS/)
  // TRK/GS is taken only once the type is MOVING.
  type(unit, '111/22')
  unit.press('LSK2R')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  unit.press('CLR')
  unit.press('CLR', { held: true })
  unit.press('LSK3L')
  expect(lines(unit)[6]).toMatch(/^>MOVING/)
  type(unit, 'N4050.0W07230.0')
  unit.press('LSK1R')
  type(unit, 'SHIP')
  unit.press('LSK1L')
  // Not complete without the motion.
  expect(lines(unit)[11] ?? '').not.toMatch(/SAVE\?/)
  type(unit, '361/22')
  unit.press('LSK2R')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  unit.press('CLR')
  unit.press('CLR', { held: true })
  type(unit, '111/22')
  unit.press('LSK2R')
  expect(lines(unit)[4]).toMatch(/111°T\/ 22KT\s*$/)
  unit.press('LSK6R')
  expect(scratch(unit)).toBe('SHIP STORED')
  const origin = { lat: 40 + 50 / 60, lon: -(72 + 30 / 60) }
  const record = { ident: 'SHIP', position: origin, type: 'MOVING', trackDeg: 111, groundSpeedKt: 22, epoch: '2026-09-30T12:00:00.000Z' }
  expect(unit.userWaypoints).toEqual([record])
  expect(movingUserWaypointPosition(record as MovingUserWaypoint, new Date(now))).toEqual(origin)
  // Half an hour of simulation time later it is 11 NM along 111°, and it is a moving waypoint wherever it is used.
  now += 30 * 60_000
  expect(distanceNm(origin, unit.coordinates('SHIP')!)).toBeCloseTo(11, 6)
  expect(unit.coordinates('SHIP')).toEqual(offset(origin, 111, 11))
  expect(unit.movingWaypoints.SHIP).toEqual({ track: 111, speed: 22 })
  expect(unit.designateHoverMarkIdent('SHIP')).toBe(false)
  unit.press('NEXT')
  expect(lines(unit)[2]).toMatch(/^SHIP\s+MOVING$/)
  // A restart an hour after the epoch reads the same record and places it 22 NM along; the record is unchanged.
  now += 30 * 60_000
  const again = session()
  expect(again.userWaypoints).toEqual([record])
  expect(again.coordinates('SHIP')).toEqual(offset(origin, 111, 22))
  // Round-trips through export and import, and an identical record is not a collision.
  const text = again.exportUserDatabase()
  const into = fms(memoryUserDatabaseStore(), 'pilot.two')
  expect(into.importUserDatabase(text)).toEqual({ imported: { waypoints: 1, routes: 0 } })
  expect(into.userWaypoints).toEqual([record])
  expect(into.importUserDatabase(text)).toEqual({ imported: { waypoints: 0, routes: 0 } })
})

test('an import refuses a moving waypoint without a readable track, speed or epoch, and one with another epoch collides', () => {
  const unit = fms(memoryUserDatabaseStore())
  expect(unit.createUserWaypoint('SHIP', { lat: 40, lon: -72 }, { track: 90, speed: 20 })).toBeUndefined()
  const doc = JSON.parse(unit.exportUserDatabase())
  const good = doc.waypoints[0]
  expect(good).toEqual({ ident: 'SHIP', position: { lat: 40, lon: -72 }, type: 'MOVING', trackDeg: 90, groundSpeedKt: 20, epoch: '2026-09-30T12:00:00.000Z' })
  const bad = (change: Record<string, unknown>) => JSON.stringify({ ...doc, waypoints: [{ ...good, ident: 'OTHER', ...change }] })
  expect(unit.importUserDatabase(bad({ trackDeg: 361 }))).toEqual({ refused: ['waypoint OTHER: track 361 is not 0-360 degrees'] })
  expect(unit.importUserDatabase(bad({ groundSpeedKt: -1 }))).toEqual({ refused: ['waypoint OTHER: ground speed -1 is not 0-999 kt'] })
  expect(unit.importUserDatabase(bad({ epoch: 'yesterday' }))).toEqual({ refused: ['waypoint OTHER: epoch "yesterday" is not a time'] })
  expect(unit.importUserDatabase(bad({ type: 'DRIFTING' }))).toEqual({ refused: ['waypoint OTHER: type "DRIFTING" is not FIXED or MOVING'] })
  expect(unit.importUserDatabase(JSON.stringify({ ...doc, waypoints: [{ ...good, epoch: '2026-09-30T13:00:00.000Z' }] })))
    .toEqual({ refused: ['user waypoint SHIP is already stored with another position or motion'] })
  expect(unit.importUserDatabase(JSON.stringify({ ...doc, waypoints: [{ ident: 'SHIP', position: good.position, type: 'FIXED' }] })))
    .toEqual({ refused: ['user waypoint SHIP is already stored with another position or motion'] })
  expect(unit.userWaypoints).toEqual([good])
})
