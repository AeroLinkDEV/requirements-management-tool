import { existsSync, readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'

// Plan rev 2 A1: the source applicability matrix (product/docs/FMS_APPLICABILITY.md) is kept current as behaviour is
// built. This guard ties it to the code: every behaviour row says whether it is built, in one of the matrix's terms, and
// a row built "Yes" names the owner tests that show it, each of which exists by that title. A test renamed or removed,
// or a row claimed built with no test behind it, fails here rather than leaving the matrix quietly wrong.

const MATRIX = readFileSync('../docs/FMS_APPLICABILITY.md', 'utf8')
const REGISTER = readFileSync('../docs/FMS_TEST_BENCH.md', 'utf8')

type Row = { id: string; built: string; evidence: string }
/** The behaviour rows: ID, behaviour, source, applies to, bench behaviour, deviation, status, built, owner evidence. */
function rows(): Row[] {
  const section = MATRIX.slice(MATRIX.indexOf('## Behaviour rows'), MATRIX.indexOf('## References'))
  return section.split('\n').filter(line => /^\| [A-Z0-9-]+ \|/.test(line) && !line.startsWith('| ID |')).map(line => {
    const cells = line.slice(1, -1).split('|').map(cell => cell.trim())
    return { id: cells[0], built: cells[7], evidence: cells[8] }
  })
}
/** The tests a cell names: a spec file in backticks followed by a quoted fragment of the test's title. */
const namedTests = (cell: string) => [...cell.matchAll(/`(fms-[a-z0-9-]+\.spec\.ts)` "([^"]+)"/g)].map(([, file, title]) => ({ file, title }))
/** Whether the spec declares a test whose title contains the fragment (escaped quotes in the source read as quotes). */
function declares(file: string, title: string) {
  const path = `tests/${file}`
  if (!existsSync(path)) return false
  return readFileSync(path, 'utf8').replace(/\\'/g, "'").split('\n').some(line => /\btest\(/.test(line) && line.includes(title))
}

test('A1: the matrix has its behaviour rows, each with an owner-evidence cell', () => {
  const all = rows()
  expect(all.length).toBeGreaterThan(25)
  expect(new Set(all.map(row => row.id)).size).toBe(all.length)
  for (const row of all) expect(row.evidence, row.id).toBeDefined()
})

test('A1: every row says whether it is built, in the matrix\'s terms: Yes, Partly, No, Data only, or a dash', () => {
  const unknown = rows().filter(row => !/^(Yes|Partly|No|Data only|—)(\b|$| )/.test(row.built)).map(row => `${row.id}: ${row.built}`)
  expect(unknown).toEqual([])
})

test('A1: every row built "Yes" names at least one owner test, and every test the matrix names exists by that title', () => {
  const built = rows().filter(row => row.built.startsWith('Yes'))
  expect(built.length).toBeGreaterThan(15)
  expect(built.filter(row => namedTests(row.evidence).length === 0).map(row => row.id)).toEqual([])
  // Every row, built or not: a named test must exist.
  const missing = rows().flatMap(row => namedTests(row.evidence).filter(({ file, title }) => !declares(file, title)).map(({ file, title }) => `${row.id}: ${file} "${title}"`))
  expect(missing).toEqual([])
  // An evidence cell holds named tests or a dash, nothing the guard cannot check.
  const unchecked = rows().filter(row => row.evidence !== '—' && row.evidence.replace(/`fms-[a-z0-9-]+\.spec\.ts` "[^"]+"/g, '').replace(/[;\s]/g, '') !== '').map(row => row.id)
  expect(unchecked).toEqual([])
})

test('A1: the guard itself sees a missing test: a title no spec declares is reported', () => {
  expect(declares('fms-flight.spec.ts', 'the helicopter hold defaults to its holding speed limit')).toBe(true)
  expect(declares('fms-flight.spec.ts', 'a title nobody wrote')).toBe(false)
  expect(declares('fms-no-such.spec.ts', 'anything')).toBe(false)
  // A fragment that only appears in a comment or an assertion is not a test title.
  expect(declares('fms-flight.spec.ts', 'expect(')).toBe(false)
})

test('A1: the register refers to the matrix for which source governs each behaviour', () => {
  expect(REGISTER).toContain('FMS_APPLICABILITY.md')
})
