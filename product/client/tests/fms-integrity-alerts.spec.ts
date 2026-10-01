import { readdirSync, readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { ALERTS } from '../src/fmsCdu/alerts'
import { APPENDIX_E, appendixEClass } from '../src/fmsCdu/appendixE'

// Stage F12 groundwork: every message the bench raises is traced to its M300 Appendix E row (appendixE.ts), or is
// declared laboratory with its reason. The raise sites are read from the source, so a new message without a row fails
// here. The rows' trigger, clear and inhibit behaviour is tested with the code that raises them (F2, F8, F12).
const SOURCE = 'src/fmsCdu'
const files = readdirSync(SOURCE).filter(name => /\.tsx?$/.test(name)).map(name => ({ name, text: readFileSync(`${SOURCE}/${name}`, 'utf8') }))
/** Literal messages raised through the library (alert("…")) or straight to the FMS's own alert method (this.alert("…")). */
const raised = (pattern: RegExp) => files.flatMap(({ name, text }) => [...text.matchAll(pattern)].map(m => ({ name, text: m[1] })))
const viaLibrary = raised(/(?<![.\w])alert\("([^"]+)"\)/g)
const direct = raised(/this\.alert\("([^"]+)"\)/g)
// Templated raises: GPS${n} NOT USABLE and APPR ON GPS${n}, for receivers 1 and 2.
const templated = files.flatMap(({ text }) => [...text.matchAll(/alert\(`([^`]+)`\)/g)].map(m => m[1]))

test('F12: every navigation alert has an Appendix E source', () => {
  // Every message in the library has a row or a laboratory reason, and the map holds nothing that is never raised.
  for (const { text } of ALERTS) expect(APPENDIX_E[text], text).toBeDefined()
  const raisedTexts = new Set([...ALERTS.map(a => a.text), ...viaLibrary.map(r => r.text), ...direct.map(r => r.text)])
  expect(Object.keys(APPENDIX_E).filter(text => !raisedTexts.has(text))).toEqual([])
  // Every literal raise, through the library or not, has its source; the templated ones for both receivers.
  for (const { name, text } of [...viaLibrary, ...direct]) expect(APPENDIX_E[text], `${name}: ${text}`).toBeDefined()
  expect(templated.sort()).toEqual(['APPR ON GPS${chosen + 1}', 'GPS${index + 1} NOT USABLE'])
  for (const text of ['APPR ON GPS1', 'APPR ON GPS2', 'GPS1 NOT USABLE', 'GPS2 NOT USABLE']) expect(APPENDIX_E[text], text).toBeDefined()
  // A row's page is a real Appendix E page (it has a class); a laboratory message says why.
  for (const [text, source] of Object.entries(APPENDIX_E)) {
    if ('laboratory' in source) expect(source.laboratory.length, text).toBeGreaterThan(20)
    else {
      expect(appendixEClass(source.page), text).not.toBeNull()
      if (source.also) expect(appendixEClass(source.also), text).toBe('STATUS ADVISORY')
    }
  }
  // The library says the same as the map about which messages are the bench's own: its laboratory meanings start
  // "(Laboratory)". TDN DIST SHORT is the one that does not yet (open for F8, which owns the library's text).
  const libraryLab = ALERTS.filter(a => a.meaning.startsWith('(Laboratory)')).map(a => a.text)
  const mapLab = Object.entries(APPENDIX_E).filter(([, s]) => 'laboratory' in s).map(([t]) => t)
  expect(mapLab.filter(t => !libraryLab.includes(t))).toEqual(['TDN DIST SHORT'])
  expect(libraryLab.filter(t => !mapLab.includes(t))).toEqual([])
})

test('F12: the messages raised as alerts whose Appendix E row is another class, and the raises that bypass the library, are the known ones', () => {
  // The bench raises every library message as an amber alert. These rows are not system alerts in the manual; F8's
  // per-row message work decides each one's display. A new one fails here.
  const notSystem = Object.entries(APPENDIX_E).flatMap(([text, s]) => ('page' in s && appendixEClass(s.page) !== 'SYSTEM ALERT' ? [`${text}: ${appendixEClass(s.page)}`] : []))
  expect(notSystem.sort()).toEqual([
    'MAG VAR CRC FAILED: MAINTENANCE ALERT', 'RALT FAILED: MAINTENANCE ADVISORY', 'SYSTEM FAILED: MAINTENANCE ALERT', 'TRANSITION DOWN: STATUS ADVISORY',
  ])
  // Raised straight to the FMS's alert method, not checked against the library: the MAGVAR checksum failure's two.
  expect(direct.map(r => `${r.name}: ${r.text}`).sort()).toEqual(['scriptedFms.ts: MAG VAR CRC FAILED', 'scriptedFms.ts: SYSTEM FAILED'])
})

test('F12: Appendix E classes follow its contents page ranges, and anything else has none', () => {
  expect(['E-1', 'E-18', 'E-19', 'E-20', 'E-21', 'E-28', 'E-29', 'E-37', 'E-38', 'E-50'].map(appendixEClass)).toEqual([
    'SYSTEM ALERT', 'SYSTEM ALERT', 'MAINTENANCE ALERT', 'MAINTENANCE ALERT', 'MAINTENANCE ADVISORY', 'MAINTENANCE ADVISORY',
    'STATUS ADVISORY', 'STATUS ADVISORY', 'DATA ENTRY ADVISORY', 'DATA ENTRY ADVISORY',
  ])
  for (const page of ['E-0', 'E-51', 'E-52', 'E-i', '3-1', 'E-', '']) expect(appendixEClass(page), page).toBeNull()
})
