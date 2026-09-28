import { expect, logicTest as test } from './isolated-client-test'
import { arincLatitude, arincLongitude, parseArinc424 } from '../src/fmsCdu/arinc424'
import { distanceNm } from '../src/fmsCdu/fmsModel'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Flight planning on the scripted FMS: procedures, airways, waypoint types, duplicate idents, company routes, the
// secondary flight plan, REF NAV DATA and the ARINC 424 reader. The data is the demonstration database.
const fms = () => new ScriptedFms(() => new Date(Date.UTC(2026, 8, 27, 14, 0, 0)))
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : ch === '-' ? 'PLUSMINUS' : `CHAR_${ch}`)
}
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { typeText(unit, text); unit.press(lsk) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trimEnd()
// A conditional leg shows as its path terminator, e.g. (CA); a gap as (disco).
const idents = (unit: ScriptedFms) => unit.route.legs.map(leg => (leg.kind === 'wpt' ? leg.ident : leg.kind === 'cond' ? `(${leg.path})` : '(disco)'))

test('a SID and transition start the route at the runway end and join the enroute legs where they meet', () => {
  const unit = fms()
  press(unit, 'DEP_ARR', 'LSK1L')
  expect(lines(unit)[0]).toMatch(/^CYOW DEPARTURES/)
  // Runway 25 leaves only the SID that serves it.
  const rw25 = lines(unit).findIndex(line => /RW25$/.test(line))
  unit.press(`LSK${rw25 / 2}R` as CduFunction)
  expect(lines(unit).join('\n')).not.toContain('GATIN2')
  unit.press('LSK1L')
  expect(lines(unit)[2]).toMatch(/^RIDEA3 <SEL>/)
  expect(lines(unit)[3]).toMatch(/^ TRANS/)
  expect(lines(unit)[4]).toMatch(/^MUN/)
  unit.press('LSK2L')
  expect(idents(unit)).toEqual(['(CA)', 'OW501', 'MUN', 'RDG', 'TOLGU', 'FERDI', 'RW24R', 'CYUL'])
  expect(unit.route.sid).toEqual({ ident: 'RIDEA3', transition: 'MUN' })
  expect(unit.routeStatus).toBe('MOD')
})

test('a SID transition that does not meet the route is followed by a route discontinuity', () => {
  const unit = fms()
  unit.selectProcedure('SID', 'RIDEA3', 'ELIBA')
  expect(idents(unit).slice(0, 5)).toEqual(['(CA)', 'OW501', 'ELIBA', '(disco)', 'MUN'])
})

test('a STAR and approach replace the end of the route, and the approach is followed by its missed approach', () => {
  const unit = fms()
  press(unit, 'DEP_ARR', 'LSK1R')
  expect(lines(unit)[0]).toMatch(/^CYUL ARRIVALS/)
  expect(lines(unit)[2]).toMatch(/^LACHN3\s+RNAV 24R$/)
  press(unit, 'LSK1L', 'LSK2L')
  expect(idents(unit)).toEqual(['MUN', 'RDG', 'TOLGU', 'UL301', 'UL302', 'CYUL'])
  unit.press('LSK1R')
  // The approach's first fix is not the STAR's last: a discontinuity until the UL302 transition joins them.
  expect(idents(unit)).toEqual(['MUN', 'RDG', 'TOLGU', 'UL301', 'UL302', '(disco)', 'FERDI', 'RW24R', '(CA)', 'UL501', 'UL502'])
  expect(lines(unit)[4]).toMatch(/UL302$/)
  unit.press('LSK2R')
  expect(idents(unit)).toEqual(['MUN', 'RDG', 'TOLGU', 'UL301', 'UL302', 'FERDI', 'RW24R', '(CA)', 'UL501', 'UL502'])
  // Selecting the STAR again removes it; the approach stays.
  unit.press('LSK1L')
  expect(unit.route.star).toBeUndefined()
  expect(idents(unit)).toContain('UL501')
})

test('passing the runway on the approach starts the missed approach and arms its hold', () => {
  const unit = fms()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  for (let i = 0; i < 5; i += 1) unit.sequence()
  // The missed approach climbs straight ahead to 1000 ft (a CA leg) before turning for UL501.
  expect(idents(unit)[0]).toBe('(CA)')
  unit.sequence()
  expect(idents(unit)[0]).toBe('UL501')
  expect(unit.activeRoute.hold).toMatchObject({ fix: 'UL502', status: 'ARMED' })
})

test('RTE 2 VIA/TO: an airway from the last fix expands into its fixes; a fix not on it is refused', () => {
  const unit = fms()
  press(unit, 'RTE')
  enter(unit, 'OWUL1', 'LSK2L')
  expect(idents(unit)).toEqual(['MUN', 'RDG', 'TOLGU', 'CYUL'])
  press(unit, 'EXEC', 'LEGS', 'CLR', 'LSK2L', 'CLR', 'LSK2L', 'EXEC')
  expect(idents(unit)).toEqual(['MUN', 'CYUL'])
  press(unit, 'RTE', 'NEXT')
  expect(lines(unit)[2]).toMatch(/^DIRECT\s+MUN$/)
  enter(unit, 'T613', 'LSK3L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  press(unit, 'CLR', 'CLR', 'CLR', 'CLR', 'CLR')
  enter(unit, 'V300', 'LSK3L')
  expect(lines(unit)[6]).toMatch(/^V300\s+□{5}$/)
  enter(unit, 'YUL', 'LSK3R')
  expect(idents(unit)).toEqual(['MUN', 'RDG', 'TOLGU', 'YUL', 'CYUL'])
  expect(lines(unit)[4]).toMatch(/^V300\s+YUL$/)
  // DELETE on a segment removes its legs.
  press(unit, 'CLR', 'LSK2L')
  expect(idents(unit)).toEqual(['MUN', 'CYUL'])
})

test('company routes load from CO ROUTE, and SAVE ROUTE stores the current one under its name', () => {
  const unit = fms()
  unit.press('RTE')
  enter(unit, 'OWUL2', 'LSK2L')
  expect(idents(unit)).toEqual(['ELIBA', 'RDG', 'KILLA', 'AGBEK', 'CYUL'])
  unit.press('EXEC')
  enter(unit, 'MINE1', 'LSK2L')
  unit.press('LSK5R')
  expect(scratch(unit)).toBe('ROUTE SAVED')
  unit.press('CLR')
  enter(unit, 'OWUL1', 'LSK2L')
  expect(idents(unit)[0]).toBe('MUN')
  unit.press('EXEC')
  enter(unit, 'MINE1', 'LSK2L')
  expect(idents(unit)).toEqual(['ELIBA', 'RDG', 'KILLA', 'AGBEK', 'CYUL'])
})

test('origin and destination must be airports in the database', () => {
  const unit = fms()
  unit.press('RTE')
  enter(unit, 'KXYZ', 'LSK1R')
  expect(scratch(unit)).toBe('NOT IN DATA BASE')
})

test('a latitude/longitude entry creates WPTnn, listed on PREDEF WPT 1/2', () => {
  const unit = fms()
  unit.press('LEGS')
  enter(unit, 'N4530.0W07500.0', 'LSK2L')
  expect(idents(unit)[1]).toBe('WPT01')
  expect(unit.coordinates('WPT01')).toEqual({ lat: 45.5, lon: -75 })
  press(unit, 'INIT_REF', 'LSK2R')
  expect(lines(unit)[2]).toMatch(/^WPT01\s+N4530\.0W07500\.0$/)
  // A latitude beyond 90 degrees is not a position.
  unit.press('LEGS')
  enter(unit, 'N9530W07500', 'LSK2L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
})

test('place/bearing/distance and place-bearing/place-bearing entries create waypoints named after the place', () => {
  const unit = fms()
  unit.press('LEGS')
  enter(unit, 'RDG090/10', 'LSK3L')
  expect(idents(unit)[2]).toBe('RDG01')
  expect(distanceNm(unit.coordinates('RDG')!, unit.coordinates('RDG01')!)).toBeCloseTo(10, 1)
  enter(unit, 'MUN070/RDG180', 'LSK3L')
  expect(idents(unit)[2]).toBe('MUN01')
  const crossing = unit.coordinates('MUN01')!
  // South of RDG (bearing 180 from it) and east-north-east of MUN.
  expect(crossing.lon).toBeCloseTo(unit.coordinates('RDG')!.lon, 3)
  expect(crossing.lat).toBeGreaterThan(unit.coordinates('MUN')!.lat)
  // Bearings that meet only behind are refused.
  enter(unit, 'MUN225/RDG360', 'LSK3L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
})

test('an along-track entry puts a waypoint the given distance before or after a route fix', () => {
  const unit = fms()
  unit.press('LEGS')
  enter(unit, 'RDG/-5', 'LSK4L')
  expect(idents(unit).slice(0, 3)).toEqual(['MUN', 'RDG01', 'RDG'])
  expect(distanceNm(unit.coordinates('RDG01')!, unit.coordinates('RDG')!)).toBeCloseTo(5, 1)
  enter(unit, 'RDG/3', 'LSK4L')
  expect(idents(unit).slice(2, 4)).toEqual(['RDG', 'RDG02'])
  enter(unit, 'RDG/-90', 'LSK4L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
})

test('a duplicate ident asks SELECT DESIRED WPT, and the chosen one is inserted', () => {
  const unit = fms()
  unit.press('LEGS')
  enter(unit, 'BOBTU', 'LSK3L')
  expect(lines(unit)[0]).toMatch(/^SELECT DESIRED WPT/)
  expect(lines(unit)[1]).toMatch(/NDB 284/)
  expect(lines(unit)[3]).toMatch(/WAYPOINT/)
  unit.press('LSK2L')
  expect(lines(unit)[0]).toMatch(/^MOD RTE 1 LEGS/)
  expect(idents(unit)[2]).toBe('BOBTU')
  expect(unit.coordinates('BOBTU')!.lat).toBeCloseTo(45.21, 2)
})

test('REF NAV DATA shows a navaid frequency, an airport and its runways, and refuses an unknown ident', () => {
  const unit = fms()
  press(unit, 'INIT_REF', 'LSK1R')
  expect(lines(unit)[0]).toMatch(/^REF NAV DATA/)
  enter(unit, 'YOW', 'LSK1L')
  expect(lines(unit)[2]).toMatch(/^YOW\s+VORDME$/)
  expect(lines(unit)[6]).toMatch(/^114\.60/)
  enter(unit, 'CYUL', 'LSK1L')
  expect(lines(unit)[2]).toMatch(/AIRPORT$/)
  expect(lines(unit)[10]).toMatch(/^06L 24R 06R 24L 10 28/)
  enter(unit, 'ZZZZ', 'LSK1L')
  expect(scratch(unit)).toBe('NOT IN DATA BASE')
})

test('the secondary flight plan takes a company route and becomes a modification on ACTIVATE', () => {
  const unit = fms()
  press(unit, 'INIT_REF', 'NEXT', 'LSK5L')
  expect(lines(unit)[0]).toMatch(/^SEC FPLN/)
  expect(lines(unit)[12]).not.toContain('ACTIVATE>')
  enter(unit, 'OWUL2', 'LSK2L')
  expect(lines(unit)[6]).toMatch(/^ELIBA\s+CYUL$/)
  expect(idents(unit)[0]).toBe('MUN')
  unit.press('LSK6R')
  expect(lines(unit)[0]).toMatch(/^MOD RTE 1 LEGS/)
  expect(idents(unit)[0]).toBe('ELIBA')
})

/** A 132-column ARINC 424 record with fields placed at their 1-based columns. */
const record = (fields: [number, string][]) => {
  const chars = Array.from({ length: 132 }, () => ' ')
  for (const [column, text] of fields) [...text].forEach((ch, i) => { chars[column - 1 + i] = ch })
  return chars.join('')
}

test('ARINC 424 coordinates read hemisphere, degrees, minutes, seconds and hundredths', () => {
  expect(arincLatitude('N45300000')).toBeCloseTo(45.5, 6)
  expect(arincLatitude('S12153036')).toBeCloseTo(-(12 + 15 / 60 + 30.36 / 3600), 6)
  expect(arincLongitude('W075000000')).toBeCloseTo(-75, 6)
  expect(arincLatitude('X45300000')).toBeNull()
})

test('the ARINC 424 reader loads waypoints, navaids, airports, runways and airways that the FMS can then fly to', () => {
  const pos = (lat: string, lon: string): [number, string][] => [[33, lat], [42, lon]]
  const file = [
    record([[1, 'SCAN'], [5, 'EA'], [14, 'TESTA'], [22, '0'], ...pos('N45300000', 'W075000000')]),
    record([[1, 'SCAN'], [5, 'D'], [14, 'TST'], [22, '0'], [23, '11250'], [28, 'VDHW'], ...pos('N45400000', 'W074300000'), [94, 'TEST VORDME']]),
    record([[1, 'SCAN'], [5, 'DB'], [14, 'TN'], [22, '0'], [23, '03500'], ...pos('N45100000', 'W074000000'), [94, 'TEST NDB']]),
    record([[1, 'SCAN'], [5, 'P'], [7, 'CZZZ'], [13, 'A'], [22, '0'], ...pos('N45200000', 'W074200000'), [57, '00500'], [94, 'TEST FIELD']]),
    record([[1, 'SCAN'], [5, 'P'], [7, 'CZZZ'], [13, 'G'], [14, 'RW09'], [22, '0'], [23, '08000'], [28, '0900'], ...pos('N45200000', 'W074210000'), [67, '00480']]),
    record([[1, 'SCAN'], [5, 'ER'], [14, 'J999'], [26, '0020'], [30, 'TST'], [39, '0']]),
    record([[1, 'SCAN'], [5, 'ER'], [14, 'J999'], [26, '0010'], [30, 'TESTA'], [39, '0']]),
    // A continuation record and a line that is not a record are skipped.
    record([[1, 'SCAN'], [5, 'EA'], [14, 'TESTA'], [22, '2']]),
    'HDR01 NOT A RECORD',
  ].join('\r\n')
  const result = parseArinc424(file)
  expect(result.read).toBe(7)
  expect(result.skipped).toBe(2)
  expect(result.errors).toEqual([])

  const unit = fms()
  unit.loadNavData(result.data)
  expect(unit.coordinates('TESTA')).toEqual({ lat: 45.5, lon: -75 })
  expect(unit.navdb.find('TST')[0]).toMatchObject({ type: 'VORDME', frequency: '112.50', name: 'TEST VORDME' })
  expect(unit.navdb.find('TN')[0]).toMatchObject({ type: 'NDB', frequency: '350' })
  expect(unit.navdb.airport('CZZZ')).toMatchObject({ elevation: 500, name: 'TEST FIELD', runways: [{ ident: 'RW09', course: 90, length: 8000, elevation: 480 }] })
  expect(unit.navdb.airway('J999')!.fixes).toEqual(['TESTA', 'TST'])
  // The demonstration data is still there, and the loaded fix can be flown to.
  expect(unit.coordinates('RDG')).toBeDefined()
  unit.press('LEGS')
  enter(unit, 'TESTA', 'LSK1L')
  expect(idents(unit)[0]).toBe('TESTA')
})
