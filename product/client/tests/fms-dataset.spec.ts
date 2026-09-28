import { expect, logicTest as test } from './isolated-client-test'
import { arincLatitude, arincLongitude } from '../src/fmsCdu/arinc424'
import { FlightSimulator } from '../src/fmsCdu/flight'
import type { NavData } from '../src/fmsCdu/navData'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Navigation database lifecycle (independent review of 27 September, finding R06 and the loader part of R16). A load
// must not move a fix the aircraft is flying, a bad file must change nothing, a good file becomes an inactive cycle
// that the crew activates on purpose, and cycle dates come from the data or are shown as unknown.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const setup = (at = START) => {
  let now = at
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  const jump = (ms: number) => { now += ms }
  return { unit, sim, fly, jump }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)
const record = (fields: [number, string][]) => {
  const chars = Array.from({ length: 132 }, () => ' ')
  for (const [column, text] of fields) [...text].forEach((ch, i) => { chars[column - 1 + i] = ch })
  return chars.join('')
}
const waypoint = (ident: string, lat: string, lon: string) => record([[1, 'SCAN'], [5, 'EA'], [14, ident], [22, '0'], [33, lat], [42, lon]])

// MUN, the first fix of the demonstration route, as the demonstration database places it.
const MUN = { lat: 45.2150, lon: -75.3900 }
// A dataset whose MUN is somewhere else entirely.
const collidingMun = (): NavData => ({
  cycle: { id: 'COLLIDE', from: '', to: '' },
  entries: [{ kind: 'fix', ident: 'MUN', position: { lat: 0, lon: 0 } }],
  airways: [], procedures: [],
})
const flown = ({ unit, sim }: ReturnType<typeof setup>) => ({
  position: unit.truePosition, track: unit.track, altitude: unit.altitude,
  desiredTrack: sim.guidance.desiredTrack, targetAltitude: sim.guidance.targetAltitude,
  geometry: unit.legGeometry(unit.activeRoute),
})

test('loading and activating a dataset whose MUN is elsewhere does not move the active MUN, its guidance or its predictions', () => {
  const control = setup(), loaded = setup()
  expect(loaded.unit.coordinates('MUN')).toEqual(MUN)
  loaded.unit.loadNavData(collidingMun())
  expect(loaded.unit.coordinates('MUN')).toEqual(MUN)
  loaded.unit.swapCycles()
  expect(loaded.unit.coordinates('MUN')).toEqual(MUN)
  control.fly(30)
  loaded.fly(30)
  expect(flown(loaded)).toEqual(flown(control))
})

test('activation records which active fixes differ in the new cycle, and only EXEC of a modification re-resolves them', () => {
  const { unit } = setup()
  unit.loadNavData(collidingMun())
  unit.swapCycles()
  const activation = unit.datasetLog.at(-1)!
  expect(activation.action).toBe('ACTIVATE COLLIDE')
  expect(activation.detail).toContain('MUN')
  expect(unit.coordinates('MUN')).toEqual(MUN)
  // A modification is shown against the active cycle; executing it is the crew accepting the new positions.
  unit.selectProcedure('APPROACH', 'R24R')
  expect(unit.coordinates('MUN', unit.route)).toEqual({ lat: 0, lon: 0 })
  unit.press('EXEC')
  expect(unit.coordinates('MUN')).toEqual({ lat: 0, lon: 0 })
  const exec = unit.datasetLog.at(-1)!
  expect(exec.action).toBe('ROUTE RE-RESOLVED')
  expect(exec.detail).toContain('MUN')
})

test('an empty file is refused with a diagnostic and changes nothing', () => {
  const { unit } = setup()
  const before = { active: unit.activeCycle, inactive: unit.inactiveCycle, navdb: unit.navdb, log: unit.datasetLog.length, revision: unit.revision() }
  const outcome = unit.loadArinc424('', 'empty.pc')
  expect(outcome).toEqual({ refused: 'empty.pc: no usable ARINC 424 records' })
  expect({ active: unit.activeCycle, inactive: unit.inactiveCycle, navdb: unit.navdb, log: unit.datasetLog.length, revision: unit.revision() }).toEqual(before)
  expect(unit.navdb).toBe(before.navdb)
})

test('navigation data with nothing in it, or a position off the globe, is refused and changes nothing', () => {
  const { unit } = setup()
  const inactive = unit.inactiveCycle, log = unit.datasetLog.length
  expect(unit.loadNavData({ cycle: { id: 'EMPTY', from: '', to: '' }, entries: [], airways: [], procedures: [] }))
    .toEqual({ refused: 'EMPTY: no usable navigation data' })
  expect(unit.loadNavData({ cycle: { id: 'OFF', from: '', to: '' }, entries: [{ kind: 'fix', ident: 'FAR', position: { lat: 100.04, lon: 0 } }], airways: [], procedures: [] }))
    .toEqual({ refused: 'OFF: impossible position for FAR' })
  expect(unit.inactiveCycle).toBe(inactive)
  expect(unit.datasetLog.length).toBe(log)
})

test('a file that is not ARINC 424 is refused and changes nothing', () => {
  const { unit } = setup()
  const inactive = unit.inactiveCycle
  const outcome = unit.loadArinc424('ident,lat,lon\nMUN,0,0\n', 'fixes.csv')
  expect(outcome).toEqual({ refused: 'fixes.csv: no usable ARINC 424 records (2 lines not recognised)' })
  expect(unit.inactiveCycle).toBe(inactive)
})

test('ARINC 424 coordinates outside their ranges are not coordinates', () => {
  expect(arincLatitude('N99619999')).toBeNull()
  expect(arincLatitude('N45600000')).toBeNull()
  expect(arincLatitude('N45306000')).toBeNull()
  expect(arincLatitude('N90000001')).toBeNull()
  expect(arincLatitude('N90000000')).toBe(90)
  expect(arincLongitude('W181000000')).toBeNull()
  expect(arincLongitude('E180000100')).toBeNull()
  expect(arincLongitude('W180000000')).toBe(-180)
  expect(arincLongitude('E075590000')).toBeCloseTo(75 + 59 / 60, 9)
})

test('a file with an impossible coordinate is refused whole, naming the record, and changes nothing', () => {
  const { unit } = setup()
  const inactive = unit.inactiveCycle
  const file = [waypoint('GOODA', 'N45300000', 'W075000000'), waypoint('BADDA', 'N99619999', 'W075000000')].join('\r\n')
  const outcome = unit.loadArinc424(file, 'bad.pc')
  expect(outcome).toEqual({ refused: 'bad.pc: line 2: impossible waypoint position' })
  expect(unit.inactiveCycle).toBe(inactive)
  expect(unit.coordinates('GOODA')).toBeUndefined()
})

test('a valid file becomes the inactive cycle, and IDENT activation makes it active and records it', () => {
  const { unit } = setup()
  const outcome = unit.loadArinc424(waypoint('TESTA', 'N45300000', 'W075000000'), 'test.pc')
  expect(outcome).toEqual({ loaded: 'LOADED', read: 1, skipped: 0, errors: [] })
  expect(unit.activeCycle.id).toBe('DEMO-2609')
  expect(unit.inactiveCycle?.id).toBe('LOADED')
  expect(unit.inactiveCycle?.source).toBe('test.pc')
  // Not usable until activated.
  expect(unit.coordinates('TESTA')).toBeUndefined()
  expect(unit.datasetLog.at(-1)).toMatchObject({ action: 'LOAD LOADED' })
  press(unit, 'INIT_REF', 'LSK1L')
  expect(lines(unit)[6]).toMatch(/^LOADED\s+UNKNOWN$/)
  press(unit, 'LSK3R')
  expect(unit.activeCycle.id).toBe('LOADED')
  expect(unit.inactiveCycle?.id).toBe('DEMO-2609')
  expect(unit.coordinates('TESTA')).toEqual({ lat: 45.5, lon: -75 })
  expect(unit.datasetLog.at(-1)).toMatchObject({ action: 'ACTIVATE LOADED' })
})

test('a cycle without dates shows them as unknown and is never DATABASE OUT OF DATE', () => {
  const run = setup()
  run.unit.loadNavData({ cycle: { id: 'NODATES', from: '', to: '' }, entries: [{ kind: 'fix', ident: 'ZZZZZ', position: { lat: 45, lon: -75 } }], airways: [], procedures: [] })
  expect(run.unit.inactiveCycle).toMatchObject({ id: 'NODATES', from: null, to: null })
  run.unit.swapCycles()
  run.jump(400 * 86_400_000)
  run.unit.tick()
  expect(recalled(run.unit, 'DATABASE OUT OF DATE')).toBe(false)
  press(run.unit, 'INIT_REF', 'LSK1L')
  expect(lines(run.unit)[4]).toMatch(/^NODATES\s+UNKNOWN$/)
})

test('a cycle keeps the dates its data gives, not the time it was loaded', () => {
  const { unit } = setup()
  unit.loadNavData({ cycle: { id: 'SRC2611', from: '2026-10-29', to: '2026-11-25' }, entries: [{ kind: 'fix', ident: 'ZZZZZ', position: { lat: 45, lon: -75 } }], airways: [], procedures: [] })
  expect(unit.inactiveCycle).toMatchObject({ from: Date.UTC(2026, 9, 29), to: Date.UTC(2026, 10, 25, 23, 59) })
})

test('the two demonstration cycles are separate datasets: swapping changes the database in use', () => {
  const { unit } = setup()
  const first = unit.navdb
  expect(first.cycle.id).toBe('DEMO-2609')
  unit.swapCycles()
  expect(unit.navdb).not.toBe(first)
  expect(unit.navdb.cycle.id).toBe('DEMO-2610')
})

test('a fix the active plan was executed without stays unresolved when a later cycle defines it, until EXEC (third review D01)', () => {
  const { unit } = setup()
  const typeText = (text: string) => { for (const ch of text) unit.press(`CHAR_${ch}` as CduFunction) }
  unit.replaceLegs([{ kind: 'wpt', ident: 'MUN' }])
  unit.press('EXEC')
  // An airway through a fix no loaded cycle defines yet.
  const airway = ['MUN', 'GAPX', 'RDG'].map((fix, i) => record([[1, 'SCAN'], [5, 'ER'], [14, 'T900'], [26, String(i + 1).padStart(4, '0')], [30, fix], [39, '0']])).join('\r\n')
  expect(unit.loadArinc424(airway, 'airway-with-missing-fix.pc')).toMatchObject({ skipped: 0 })
  unit.swapCycles()
  press(unit, 'RTE', 'NEXT')
  typeText('T900')
  unit.press('LSK2L')
  typeText('RDG')
  unit.press('LSK2R')
  unit.press('EXEC')
  expect(unit.activeRoute.legs.flatMap(leg => (leg.kind === 'wpt' ? [leg.ident] : []))).toEqual(expect.arrayContaining(['MUN', 'GAPX', 'RDG']))
  expect(unit.coordinates('GAPX')).toBeUndefined()
  const before = structuredClone(unit.profile().points)
  // A later cycle defines GAPX. Activating it does not give the executed plan a position it was executed without.
  expect(unit.loadArinc424(waypoint('GAPX', 'N45180000', 'W075120000'), 'added-fix.pc')).toMatchObject({ skipped: 0 })
  unit.swapCycles()
  expect(unit.coordinates('GAPX')).toBeUndefined()
  expect(unit.profile().points).toEqual(before)
  expect(unit.datasetLog.at(-1)!.detail).toMatch(/newly defined in \S+, unresolved in the active plan until EXEC: .*GAPX/)
  // A modification is shown against the new cycle; executing it resolves GAPX and records that it was newly resolved.
  unit.selectProcedure('APPROACH', 'R24R')
  expect(unit.coordinates('GAPX', unit.route)).toEqual({ lat: 45.3, lon: -75.2 })
  unit.press('EXEC')
  expect(unit.coordinates('GAPX')).toEqual({ lat: 45.3, lon: -75.2 })
  expect(unit.datasetLog.at(-1)).toMatchObject({ action: 'ROUTE RE-RESOLVED', detail: expect.stringMatching(/newly resolved: .*GAPX/) })
})
