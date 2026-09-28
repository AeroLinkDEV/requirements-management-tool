import { expect, logicTest as test } from './isolated-client-test'
import { parseArinc424 } from '../src/fmsCdu/arinc424'
import { DATALINK_PAGES } from '../src/fmsCdu/datalinkPages'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator, legGeometry } from '../src/fmsCdu/flight'
import { fromLocal, toLocal, type LatLon } from '../src/fmsCdu/fmsModel'
import { CORE_PAGES } from '../src/fmsCdu/fmsPages'
import { NavDatabase } from '../src/fmsCdu/navData'
import { NAV_PAGES } from '../src/fmsCdu/navPages'
import { PLANNING_PAGES } from '../src/fmsCdu/planningPages'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { COLUMNS, SCRATCHPAD_LINE, screenText, type Line, type Segment } from '../src/fmsCdu/screen'
import { TACTICAL_PAGES } from '../src/fmsCdu/tacticalPages'
import type { CduFunction } from '../src/fmsCdu/variants'

// Fix PR 2 of the independent bench review of 27 September: state coherence across pages (R11, R19), the message and
// datalink lifecycles (R13, R14), data validity (R16 fields), global geometry (R17) and position initialisation (R26).
// R18 is a browser lifecycle and is proved in fms-cdu-rendered.spec.ts.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const fms = () => new ScriptedFms(() => new Date(START))
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : ch === '-' ? 'PLUSMINUS' : `CHAR_${ch}`)
}
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trimEnd()

// ------------------------------------------------------------------------------------------------ R11

/** RNP and ANP as PROGRESS 1/4 shows them ("RNP/ANP" value row). */
const progressPerformance = (unit: ScriptedFms) => {
  press(unit, 'PROG')
  const row = lines(unit).find(line => /^\d+\.\d\d\/\d+\.\d\dNM/.test(line.trim()))!
  const [rnp, anp] = row.trim().replace('NM', '').split('/').map(Number)
  return { rnp, anp }
}
/** ANP and RNP as NAV STATUS shows them (its ANP/RNP value, right of the nav mode). */
const navStatusPerformance = (unit: ScriptedFms) => {
  press(unit, 'PROG', 'LSK6R')
  expect(lines(unit)[0]).toMatch(/^NAV STATUS/)
  const [anp, rnp] = lines(unit)[2].trim().split(/\s+/).pop()!.split('/').map(Number)
  return { rnp, anp }
}

test('forced NPA and forced RNP exceeded give one RNP and ANP on PROGRESS, NAV STATUS, the EFIS and the lamp (R11)', () => {
  for (const conditions of [[], ['npa'], ['rnpExceeded'], ['npa', 'rnpExceeded']] as const) {
    const unit = fms()
    for (const id of conditions) unit.setCondition(id, true)
    const progress = progressPerformance(unit)
    const status = navStatusPerformance(unit)
    const efis = fmsOutputs(unit, new FlightSimulator(unit))
    const label = conditions.join('+') || 'no condition'
    expect(status, label).toEqual(progress)
    expect({ rnp: Number(efis.rnp.toFixed(2)), anp: Number(efis.anp.toFixed(2)) }, label).toEqual(progress)
    expect(unit.lamps().has('RNP'), label).toBe(progress.anp > progress.rnp)
    // And the forced layer really applies: NPA gives the approach RNP, RNP exceeded puts ANP above it.
    if (conditions.includes('npa' as never)) expect(progress.rnp, label).toBe(0.3)
    if (conditions.includes('rnpExceeded' as never)) expect(progress.anp, label).toBeGreaterThan(progress.rnp)
  }
})

test('CHECK ANP times out on the same effective RNP the pages show, not the sensor RNP beneath it (R11)', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  unit.setCondition('gpsLost', true)
  unit.setCondition('dmeOutage', true)
  unit.setRnp(2)
  unit.setCondition('npa', true)
  // Twenty minutes of dead reckoning: drift takes ANP above the forced 0.30 NM but far below the 2 NM entered.
  now += 1_200_000
  unit.updateNavigation(1200)
  const { anp } = progressPerformance(unit)
  expect(anp).toBeGreaterThan(0.3)
  expect(anp).toBeLessThan(2)
  expect(unit.recallList.some(message => message.text === 'CHECK ANP')).toBe(false)
  now += 61_000
  unit.updateNavigation(1)
  expect(unit.recallList.some(message => message.text === 'CHECK ANP')).toBe(true)
})

test('a forced value is labelled as forced on the pages that show it (R11)', () => {
  const unit = fms()
  unit.setCondition('rnpExceeded', true)
  press(unit, 'PROG')
  expect(lines(unit).join('\n')).toMatch(/RNP\/ANP TEST/)
  press(unit, 'LSK6R')
  expect(lines(unit)[1]).toMatch(/ANP\/RNP TEST/)
})

// ------------------------------------------------------------------------------------------------ R13

test('MSG recovered with an FMS failure still has an acknowledgement path, and CLR acknowledges it (R13)', () => {
  const unit = fms()
  unit.setCondition('gpsLost', true)
  expect(scratch(unit)).toBe('GPS NAV LOST')
  expect(unit.lamps().has('MSG')).toBe(true)
  unit.setCondition('fmsFail', true)
  unit.setCondition('fmsFail', false)
  // The unacknowledged alert is presented again rather than left as a lamp with nothing behind it.
  expect(unit.lamps().has('MSG')).toBe(true)
  expect(scratch(unit)).toBe('GPS NAV LOST')
  press(unit, 'CLR')
  expect(unit.lamps().has('MSG')).toBe(false)
  expect(scratch(unit)).toBe('')
  // History is kept separately from acknowledgement.
  expect(unit.recallList.some(message => message.text === 'GPS NAV LOST')).toBe(true)
})

test('an alert displaced by typing comes back when the scratchpad is emptied, and CLR then acknowledges it (R13)', () => {
  const unit = fms()
  unit.setCondition('gpsLost', true)
  typeText(unit, 'AB')
  expect(scratch(unit)).toBe('AB')
  expect(unit.lamps().has('MSG')).toBe(true)
  press(unit, 'CLR', 'CLR')
  expect(scratch(unit)).toBe('')
  press(unit, 'CLR')
  expect(scratch(unit)).toBe('GPS NAV LOST')
  press(unit, 'CLR')
  expect(unit.lamps().has('MSG')).toBe(false)
})

test('recovery with nothing unacknowledged leaves MSG dark and the scratchpad empty (R13)', () => {
  const unit = fms()
  unit.setCondition('gpsLost', true)
  press(unit, 'CLR')
  unit.setCondition('fmsFail', true)
  unit.setCondition('fmsFail', false)
  expect(unit.lamps().has('MSG')).toBe(false)
  expect(scratch(unit)).toBe('')
})

// ------------------------------------------------------------------------------------------------ R14

test('STANDBY keeps an ATC uplink outstanding; only a final response clears the ATC lamp (R14)', () => {
  const unit = fms()
  unit.setCondition('atcUplink', true)
  expect(unit.lamps().has('ATC')).toBe(true)
  press(unit, 'ATC', 'LSK5L')
  expect(unit.uplinks[0].response).toBe('STANDBY')
  expect(unit.lamps().has('ATC')).toBe(true)
  expect(unit.hasCondition('atcUplink')).toBe(true)
  // The standby uplink can still be answered.
  press(unit, 'LSK6L')
  expect(unit.uplinks[0].response).toBe('UNABLE')
  expect(unit.lamps().has('ATC')).toBe(false)
})

test('clearing the ATC uplink condition clears a standby uplink too (R14)', () => {
  const unit = fms()
  unit.setCondition('atcUplink', true)
  press(unit, 'ATC', 'LSK5L')
  unit.setCondition('atcUplink', false)
  expect(unit.lamps().has('ATC')).toBe(false)
})

// ------------------------------------------------------------------------------------------------ R16 (fields)

const record = (fields: [number, string][]) => {
  const chars = Array.from({ length: 132 }, () => ' ')
  for (const [column, text] of fields) [...text].forEach((ch, i) => { chars[column - 1 + i] = ch })
  return chars.join('')
}
const pos: [number, string][] = [[33, 'N45300000'], [42, 'W075000000']]
const vhf = (frequency: string) => record([[1, 'SCAN'], [5, 'D'], [14, 'TST'], [22, '0'], [23, frequency], [28, 'VDHW'], ...pos, [94, 'TEST']])
const ndb = (frequency: string) => record([[1, 'SCAN'], [5, 'DB'], [14, 'TN'], [22, '0'], [23, frequency], ...pos, [94, 'TEST']])
const airport = (elevation: string) => record([[1, 'SCAN'], [5, 'P'], [7, 'CZZZ'], [13, 'A'], [22, '0'], ...pos, [57, elevation], [94, 'FIELD']])
const runway = (length: string, bearing: string) =>
  record([[1, 'SCAN'], [5, 'P'], [7, 'CZZZ'], [13, 'G'], [14, 'RW09'], [22, '0'], [23, length], [28, bearing], ...pos, [67, '00480']])
const waypoint = (ident: string) => record([[1, 'SCAN'], [5, 'EA'], [14, ident], [22, '0'], ...pos])

test('impossible field values condemn an ARINC 424 file; boundary values are read (R16)', () => {
  for (const [name, line] of [
    ['VOR frequency above 117.95 MHz', vhf('99999')],
    ['VOR frequency below 108.00 MHz', vhf('10795')],
    ['a non-numeric VOR frequency', vhf('1A250')],
    ['NDB frequency below 190 kHz', ndb('00100')],
    ['NDB frequency above 1750 kHz', ndb('17600')],
    ['runway bearing above 360 degrees', runway('08000', '3601')],
    ['a non-numeric runway bearing', runway('08000', '09X0')],
    ['a non-numeric runway length', runway('8A000', '0900')],
    ['a non-numeric airport elevation', airport('ABCDE')],
    ['an airport elevation above 30,000 ft', airport('40000')],
  ] as const) {
    const result = parseArinc424(line)
    expect(result.invalid.length, name).toBeGreaterThan(0)
    expect(result.read, name).toBe(0)
  }
  const boundary = parseArinc424([
    vhf('10800'), vhf('11795'), ndb('01900'), ndb('17500'), runway('08000', '3600'), runway('08000', '0000'), airport('-0100'), airport('     '),
  ].join('\n'))
  expect(boundary.invalid).toEqual([])
  expect(boundary.errors).toEqual([])
  expect(boundary.read).toBe(8)
})

test('a record missing a required field (ident, navaid frequency) is skipped with an error (R16)', () => {
  for (const [name, line] of [['a blank ident', waypoint('     ')], ['a blank VOR frequency', vhf('     ')], ['a blank NDB frequency', ndb('     ')]] as const) {
    const result = parseArinc424(line)
    expect(result.read, name).toBe(0)
    expect(result.errors.length, name).toBeGreaterThan(0)
  }
})

// ------------------------------------------------------------------------------------------------ R17

// Independent reference: at the equator one degree of longitude is 60 NM, so 0.2 degrees across the date line is 12 NM.
test('a leg across the date line is 12 NM long in the short direction, eastbound and westbound (R17)', () => {
  const east: LatLon = { lat: 0, lon: 179.9 }, west: LatLon = { lat: 0, lon: -179.9 }
  const eastbound = legGeometry(east, west, east)
  expect(eastbound.length).toBeCloseTo(12, 1)
  expect(eastbound.track).toBeCloseTo(90, 1)
  const westbound = legGeometry(west, east, west)
  expect(westbound.length).toBeCloseTo(12, 1)
  expect(westbound.track).toBeCloseTo(270, 1)
  // Halfway along, on the date line itself, the aircraft is on track with 6 NM to go.
  const middle = legGeometry(east, west, { lat: 0, lon: 180 })
  expect(middle.crossTrack).toBeCloseTo(0, 3)
  expect(middle.toGo).toBeCloseTo(6, 1)
})

test('the local frame wraps longitude both ways, and points it returns are in -180..180 (R17)', () => {
  expect(toLocal({ lat: 0, lon: 179.9 }, { lat: 0, lon: -179.9 }).x).toBeCloseTo(12, 1)
  expect(toLocal({ lat: 0, lon: -179.9 }, { lat: 0, lon: 179.9 }).x).toBeCloseTo(-12, 1)
  const across = fromLocal({ lat: 0, lon: 179.9 }, { x: 12, y: 0 })
  expect(across.lon).toBeCloseTo(-179.9, 4)
})

test('a navaid across the date line is found as nearby (R17)', () => {
  const db = new NavDatabase({
    cycle: { id: 'DATELINE', from: '', to: '' },
    entries: [{ kind: 'fix', ident: 'EASTF', position: { lat: 0, lon: -179.95 } }],
    airways: [], procedures: [],
  })
  expect(db.nearby({ lat: 0, lon: 179.95 }, 10).map(entry => entry.ident)).toEqual(['EASTF'])
})

// ------------------------------------------------------------------------------------------------ R19

type Group = 'left' | 'center' | 'right'
const segmentsOf = (value: Segment | Segment[] | undefined) => (value === undefined ? [] : Array.isArray(value) ? value : [value])
/** Where a line's visible characters land and which part of the line wrote each; null when nothing collides. */
function collision(line: Line): string | null {
  const owner: (Group | null)[] = Array(COLUMNS).fill(null)
  const width = (list: Segment[]) => list.reduce((sum, segment) => sum + segment.text.length, 0)
  const place = (group: Group, list: Segment[], start: number): string | null => {
    let column = start
    for (const segment of list) {
      for (const ch of segment.text) {
        if (ch !== ' ') {
          if (column < 0 || column >= COLUMNS) return `${group} "${list.map(s => s.text).join('')}" runs past the row`
          if (owner[column] && owner[column] !== group) return `${group} overwrites ${owner[column]} at column ${column + 1}`
          owner[column] = group
        }
        column += 1
      }
    }
    return null
  }
  const left = segmentsOf(line.left), center = segmentsOf(line.center), right = segmentsOf(line.right)
  return place('left', left, 0) ?? (center.length ? place('center', center, Math.floor((COLUMNS - width(center)) / 2)) : null)
    ?? (right.length ? place('right', right, COLUMNS - width(right)) : null)
}

const STATES: [string, (unit: ScriptedFms) => void][] = [
  ['the start of a session', () => {}],
  ['forced NPA and RNP exceeded', unit => { unit.setCondition('npa', true); unit.setCondition('rnpExceeded', true) }],
  ['dead reckoning', unit => { unit.setCondition('gpsLost', true); unit.setCondition('dmeOutage', true) }],
  ['a manual RNP', unit => unit.setRnp(0.5)],
  ['an ATC uplink', unit => unit.setCondition('atcUplink', true)],
  // GPS phase 3a: the receiver pages at their longest (integrity DETECTED, one receiver failed, GPS selected out).
  ['GPS integrity lost', unit => unit.setCondition('gpsIntegrity', true)],
  ['GPS1 failed, on GPS2', unit => { unit.gps[0].injectFault('RECEIVER', true); unit.gpsUpdated() }],
  ['GPS selected out', unit => unit.selectGpsReceiver('OFF')],
  // GPS phase 3b: an RNAV approach in the route, its level LNAV/VNAV (outside the approach region) or none without GPS.
  ['an RNAV approach, LNAV/VNAV', unit => { unit.selectProcedure('APPROACH', 'R24R'); unit.press('EXEC') }],
  ['an RNAV approach without GPS', unit => { unit.selectProcedure('APPROACH', 'R24R'); unit.press('EXEC'); unit.setCondition('gpsLost', true) }],
]

test('no authored page line writes one caption or value over another, or past the 24th column (R19)', () => {
  const pages = { ...CORE_PAGES, ...PLANNING_PAGES, ...NAV_PAGES, ...TACTICAL_PAGES, ...DATALINK_PAGES }
  const found: string[] = []
  for (const [state, arrange] of STATES) {
    const unit = fms()
    arrange(unit)
    for (const [id, page] of Object.entries(pages)) {
      const count = Math.max(1, page.pages(unit))
      for (let index = 0; index < count; index += 1) {
        page.render(unit, index).forEach((line, row) => {
          const problem = line ? collision(line) : null
          if (problem) found.push(`${id} ${index + 1}/${count} row ${row + 1} (${state}): ${problem}`)
        })
      }
    }
  }
  expect(found).toEqual([])
})

test('NAV STATUS in the terminal phase shows its nav mode and ANP/RNP captions intact (R19)', () => {
  const unit = fms()
  expect(unit.flightPhase).toBe('TERMINAL')
  press(unit, 'PROG', 'LSK6R')
  expect(lines(unit)[1]).toMatch(/^ NAV MODE\s+ANP\/RNP TERM\s*$/)
})

// ------------------------------------------------------------------------------------------------ R26

test('SET POS in dead reckoning resets the position estimate to the entry and shows it as the reference (R26)', () => {
  const unit = fms()
  unit.setCondition('gpsLost', true)
  unit.setCondition('dmeOutage', true)
  expect(unit.navState.mode).toBe('DR')
  const truth = { ...unit.truePosition }
  press(unit, 'INIT_REF', 'LSK2L')
  expect(lines(unit)[0]).toMatch(/^POS INIT/)
  typeText(unit, 'N4000.0W07000.0')
  press(unit, 'LSK3R')
  expect(unit.position.lat).toBeCloseTo(40, 3)
  expect(unit.position.lon).toBeCloseTo(-70, 3)
  // Resetting the estimate does not move the aircraft.
  expect(unit.truePosition).toEqual(truth)
  expect(lines(unit)[6]).toMatch(/N4000\.0W07000\.0\s*$/)
  expect(scratch(unit)).toBe('')
})

test('SET POS with GPS navigating records the reference without moving the sensor position (R26)', () => {
  const unit = fms()
  const before = { ...unit.position }
  press(unit, 'INIT_REF', 'LSK2L')
  typeText(unit, 'N4000.0W07000.0')
  press(unit, 'LSK3R')
  expect(lines(unit)[6]).toMatch(/N4000\.0W07000\.0\s*$/)
  expect(unit.position).toEqual(before)
})

test('SET POS refuses an impossible position and changes nothing (R26)', () => {
  for (const entry of ['N9100.0W07000.0', 'N4060.0W07000.0', 'N4000.0W18100.0']) {
    const unit = fms()
    unit.setCondition('gpsLost', true)
    unit.setCondition('dmeOutage', true)
    const before = { ...unit.position }
    press(unit, 'INIT_REF', 'LSK2L')
    typeText(unit, entry)
    press(unit, 'LSK3R')
    expect(scratch(unit), entry).toBe('INVALID ENTRY')
    expect(unit.position, entry).toEqual(before)
    expect(lines(unit)[6], entry).not.toMatch(/N\d{4}\.\dW\d{5}\.\d\s*$/)
  }
})
