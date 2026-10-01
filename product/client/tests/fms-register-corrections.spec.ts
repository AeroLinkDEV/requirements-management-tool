import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'

// Plan rev 2 A3 (and rev 3 C16): the register corrections C1–C16 are kept in the capability register
// (product/docs/FMS_TEST_BENCH.md, "Register corrections") with what checks each one. This guard holds the table to
// that: every correction is there once, every owner test it names exists by its title, every matrix row it names
// exists, and each named scan finds none of the defect the correction removed. A correction is otherwise a sentence in
// a document that nothing stops from coming untrue.

const REGISTER = readFileSync('../docs/FMS_TEST_BENCH.md', 'utf8')
const MATRIX = readFileSync('../docs/FMS_APPLICABILITY.md', 'utf8')
const SOURCE_DIR = 'src/fmsCdu'
const sources = () => readdirSync(SOURCE_DIR).filter(name => /\.tsx?$/.test(name)).map(name => ({ name, text: readFileSync(`${SOURCE_DIR}/${name}`, 'utf8') }))

type Correction = { id: string; checks: string }
function corrections(): Correction[] {
  const start = REGISTER.indexOf('### Register corrections (plan A3')
  const section = REGISTER.slice(start, REGISTER.indexOf('\n### ', start + 1))
  return section.split('\n').filter(line => /^\| C\d+ \|/.test(line)).map(line => {
    const cells = line.slice(1, -1).split('|').map(cell => cell.trim())
    return { id: cells[0], checks: cells[2] }
  })
}
const namedTests = (cell: string) => [...cell.matchAll(/`(fms-[a-z0-9-]+\.spec\.ts)` "([^"]+)"/g)].map(([, file, title]) => ({ file, title }))
const namedRows = (cell: string) => [...cell.matchAll(/\bmatrix ([A-Z0-9-]+)/g)].map(([, id]) => id)
const namedScans = (cell: string) => [...cell.matchAll(/\bscan ([a-z0-9-]+)/g)].map(([, id]) => id)
/** The titles a spec declares: the first string argument of each test(…) call, escaped quotes read as quotes. */
function titles(file: string): string[] {
  const path = `tests/${file}`
  if (!existsSync(path)) return []
  return [...readFileSync(path, 'utf8').matchAll(/\btest\(\s*(['"`])((?:\\.|(?!\1)[^\\])*)\1/g)].map(([, , title]) => title.replace(/\\(.)/g, '$1'))
}
/** Whether the spec declares a test whose title contains the fragment. */
const declares = (file: string, title: string) => titles(file).some(declared => declared.includes(title))

/** Each scan: the source lines that still show the defect the correction removed. */
const SCANS: Record<string, () => string[]> = {
  // C8: a speed floor or threshold at a literal 30 kt (max(30, …), max(speed, 30), or a > 30 progress gate).
  'no-speed-floors': () => sources().flatMap(({ name, text }) => text.split('\n').map((line, index) => ({ name, line, index }))
    .filter(({ line }) => /Math\.max\(\s*30\s*,|Math\.max\([^()]*(?:[sS]peed|tas|Tas|airspeed|\bgs\b)[^()]*,\s*30\s*\)|(?:[sS]peed|\bgs\b)\s*>=?\s*30\b/.test(line))
    .map(({ name, line, index }) => `${name}:${index + 1}: ${line.trim()}`)),
  // C10: the HOVER page's fixed 50 ft radio altitude.
  'no-fixed-hover-height': () => readFileSync(`${SOURCE_DIR}/tacticalPages.ts`, 'utf8').split('\n')
    .filter(line => /["`']\s*50\s*FT\s*["`']/.test(line)).map(line => `tacticalPages.ts: ${line.trim()}`),
  // C16: the missed-approach hold armed at a fixed 180 kt or a fixed one-minute leg.
  'no-missed-hold-180': () => {
    const text = readFileSync(`${SOURCE_DIR}/scriptedFms.ts`, 'utf8')
    const at = text.indexOf('armMissedHold(')
    const body = at < 0 ? '' : text.slice(at, text.indexOf('\n  }\n', at))
    return body.split('\n').filter(line => /speed:\s*180\b|legTime:\s*1(?:\.0)?\b/.test(line)).map(line => `armMissedHold: ${line.trim()}`)
  },
}

test('A3: the register keeps every correction, C1 to C16, once', () => {
  expect(corrections().map(c => c.id)).toEqual(Array.from({ length: 16 }, (_, i) => `C${i + 1}`))
})

test('A3: every correction names what checks it, and everything it names exists or holds', () => {
  const unchecked = corrections().filter(({ checks }) => checks !== 'statement' && namedTests(checks).length + namedRows(checks).length + namedScans(checks).length === 0)
  expect(unchecked.map(c => c.id)).toEqual([])
  const missingTests = corrections().flatMap(({ id, checks }) => namedTests(checks).filter(t => !declares(t.file, t.title)).map(t => `${id}: ${t.file} "${t.title}"`))
  expect(missingTests).toEqual([])
  const matrixIds = new Set([...MATRIX.matchAll(/^\| ([A-Z0-9-]+) \|/gm)].map(([, id]) => id))
  expect(corrections().flatMap(({ id, checks }) => namedRows(checks).filter(row => !matrixIds.has(row)).map(row => `${id}: ${row}`))).toEqual([])
  const unknownScans = corrections().flatMap(({ id, checks }) => namedScans(checks).filter(scan => !(scan in SCANS)).map(scan => `${id}: ${scan}`))
  expect(unknownScans).toEqual([])
})

test('A3: each scan finds none of the defect its correction removed', () => {
  for (const { id, checks } of corrections()) for (const scan of namedScans(checks)) expect(SCANS[scan](), `${id} ${scan}`).toEqual([])
})

test('A3: the scans are not vacuous: each finds its defect where it is planted', () => {
  expect(/Math\.max\(\s*30\s*,/.test('const gs = Math.max(30, speed)')).toBe(true)
  expect(/Math\.max\([^()]*(?:[sS]peed|tas|Tas|airspeed|\bgs\b)[^()]*,\s*30\s*\)/.test('rate / Math.max(airspeed, 30)')).toBe(true)
  expect(/(?:[sS]peed|\bgs\b)\s*>=?\s*30\b/.test('groundSpeed > 30 ? eta : null')).toBe(true)
  expect(/["`']\s*50\s*FT\s*["`']/.test('right: medium("50FT")')).toBe(true)
  expect(/speed:\s*180\b|legTime:\s*1(?:\.0)?\b/.test('exit: "MANUAL", speed: 180, legTime: 1,')).toBe(true)
  // And each scan reads real source: the files it names are there.
  expect(sources().length).toBeGreaterThan(20)
  expect(readFileSync(`${SOURCE_DIR}/scriptedFms.ts`, 'utf8')).toContain('armMissedHold(')
})
