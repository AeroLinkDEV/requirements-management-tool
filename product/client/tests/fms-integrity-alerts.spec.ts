import { readdirSync, readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { ALERTS } from '../src/fmsCdu/alerts'
import { APPENDIX_E, appendixEClass } from '../src/fmsCdu/appendixE'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'

// Stage F12 groundwork: every message the bench raises is traced to its M300 Appendix E row (appendixE.ts), or is
// declared laboratory with its reason. The raise sites are read from the source, so a new message without a row fails
// here. The rows' trigger, clear and inhibit behaviour is tested with the code that raises them (F2, F8, F12).
const SOURCE = 'src/fmsCdu'
const files = readdirSync(SOURCE).filter(name => /\.tsx?$/.test(name)).map(name => ({ name, text: readFileSync(`${SOURCE}/${name}`, 'utf8') }))
/** Literal messages raised through the library (alert("…")) or straight to the FMS's own alert method, with optional cause metadata. */
const raised = (pattern: RegExp) => files.flatMap(({ name, text }) => [...text.matchAll(pattern)].map(m => ({ name, text: m[1] })))
const viaLibrary = raised(/(?<![.\w])alert\("([^"]+)"\)/g)
const directLiterals = (source: string) => [...source.matchAll(/this\.alert\("([^"]+)"\s*(?=[,)])/g)].map(m => m[1])
const direct = files.flatMap(({ name, text }) => directLiterals(text).map(text => ({ name, text })))
/** Literal status advisories (this.advisory("…")) whose Appendix E row is mapped: white, not alerts (F10's FMS NAV IN DR). */
const advisories = raised(/this\.advisory\("([^"]+)"\)/g).filter(r => r.text in APPENDIX_E)
// Templated raises: GPS${n} NOT USABLE and APPR ON GPS${n}, for receivers 1 and 2.
const templated = files.flatMap(({ text }) => [...text.matchAll(/alert\(`([^`]+)`\)/g)].map(m => m[1]))

test('F12: every navigation alert has an Appendix E source', () => {
  // Cause metadata must not hide a literal raise from the provenance audit. Dynamic/concatenated text is not a
  // literal message, and an unknown literal still reaches the missing-source check rather than disappearing.
  expect(directLiterals('this.alert("SYSTEM FAILED"); this.alert("MAG VAR CRC FAILED", "MAGVAR:CRC_FAILED");'))
    .toEqual(['SYSTEM FAILED', 'MAG VAR CRC FAILED'])
  expect(directLiterals('other.alert("OTHER"); this.alert(alert("NESTED")); this.alert("PREFIX" + suffix);'))
    .toEqual([])
  const unmapped = directLiterals('this.alert("UNMAPPED PROVENANCE CONTROL", "CONTROL");')
  expect(unmapped.filter(text => !(text in APPENDIX_E))).toEqual(['UNMAPPED PROVENANCE CONTROL'])
  // Every message in the library has a row or a laboratory reason, and the map holds nothing that is never raised.
  for (const { text } of ALERTS) expect(APPENDIX_E[text], text).toBeDefined()
  const raisedTexts = new Set([...ALERTS.map(a => a.text), ...viaLibrary.map(r => r.text), ...direct.map(r => r.text), ...advisories.map(r => r.text)])
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
  // A mapped message raised only as a status advisory is shown as one, and is not counted here.
  const asAdvisory = new Set(advisories.map(r => r.text).filter(t => !ALERTS.some(a => a.text === t)))
  expect([...asAdvisory].map(t => `${t}: ${'page' in APPENDIX_E[t] ? appendixEClass((APPENDIX_E[t] as { page: string }).page) : 'laboratory'}`))
    .toEqual(['FMS NAV IN DR: STATUS ADVISORY'])
  const notSystem = Object.entries(APPENDIX_E).flatMap(([text, s]) => (!asAdvisory.has(text) && 'page' in s && appendixEClass(s.page) !== 'SYSTEM ALERT' ? [`${text}: ${appendixEClass(s.page)}`] : []))
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

// The behaviour rows (F12; M300 15-1, 15-2, 5-16/5-25/5-32 and E-17), on the demonstration route, which starts in the
// terminal phase.
const bench = () => {
  let now = Date.UTC(2026, 8, 27, 14, 0, 0)
  const unit = new ScriptedFms(() => new Date(now), {})
  const sim = new FlightSimulator(unit)
  const fly = (seconds: number) => { for (let t = 0; t < seconds; t += 1) { now += 1000; sim.step(1) } }
  return { unit, sim, fly }
}
const scratchpad = (unit: ScriptedFms) => screenText(unit.screen())[SCRATCHPAD_LINE].trim()

test('F12: the CDI full scale follows Table 15-1: the phase default, or the crew\'s RNP entry by its value', () => {
  const { unit, sim, fly } = bench()
  fly(2)
  expect(unit.flightPhase).toBe('TERMINAL')
  expect(fmsOutputs(unit, sim).lateralFullScaleNm).toBe(1)
  // M300 15-1, Table 15-1: an entry above 1.01 is 5.0; above 0.31, 1.0; any other entry, 0.3, whatever the phase.
  for (const [entry, fullScale] of [[2, 5], [1.02, 5], [1.01, 1], [0.5, 1], [0.32, 1], [0.31, 0.3], [0.1, 0.3]] as const) {
    unit.setRnp(entry)
    expect(fmsOutputs(unit, sim).lateralFullScaleNm, `RNP ${entry}`).toBe(fullScale)
  }
  unit.setRnp(null)
  expect(fmsOutputs(unit, sim).lateralFullScaleNm).toBe(1)
})

test('F12: VERIFY RNP VALUE is a condition: raised while an entry above the phase default is in use, and gone when it is not', () => {
  const { unit, fly } = bench()
  fly(2)
  expect(APPENDIX_E['VERIFY RNP VALUE']).toEqual({ page: 'E-17' })
  // The terminal default is 1.0 (M300 5-32): an entry of 1.0 is not above it, 1.5 is.
  unit.setRnp(1)
  expect(scratchpad(unit)).not.toContain('VERIFY RNP VALUE')
  unit.setRnp(1.5)
  expect(scratchpad(unit)).toBe('VERIFY RNP VALUE')
  // Back to the default: the condition no longer exists, so the alert goes by itself (Appendix E, E-1).
  unit.setRnp(null)
  fly(1)
  expect(scratchpad(unit)).not.toContain('VERIFY RNP VALUE')
})

test('F12: CHECK ANP waits 30 s in the terminal phase (M300 15-2), not the demonstration\'s 60', () => {
  const { unit, fly } = bench()
  fly(2)
  // An RNP below any ANP the bench reaches: the exceedance starts now.
  unit.setRnp(0.01)
  fly(29)
  expect(scratchpad(unit)).not.toContain('CHECK ANP')
  fly(2)
  expect(scratchpad(unit)).toBe('CHECK ANP')
})
