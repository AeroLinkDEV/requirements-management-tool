import { expect, logicTest as test } from './isolated-client-test'
import { bearingDeg, distanceNm } from '../src/fmsCdu/fmsModel'
import { RADIO_TEST_S } from '../src/fmsCdu/radioManagement'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'

// Stage F plan F8b (M300 13-3, 13-21 to 13-25): the NAV and ADF radio pages on the shared RMS.

const T0 = Date.UTC(2026, 8, 30, 14)
function unitAt() {
  let now = T0
  const fms = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  const lines = () => screenText(fms.screen())
  const enter = (text: string, key: Parameters<ScriptedFms['press']>[0]) => { fms.setScratch(text); fms.press(key) }
  return { fms, step, lines, enter }
}

test('F8b: RADIO leads to NAV 1/2, which shows each NAV\'s station, its measured radial and its DME distance', () => {
  const { fms, step, lines } = unitAt()
  step(3)
  fms.press('RADIO')
  fms.press('LSK5L')
  expect(lines()[0]).toMatch(/^NAV\s+1\/2$/)
  const station = fms.navStation('nav1')!
  expect(lines()[1]).toMatch(/NAV1 AUTO/)
  expect(lines()[2]).toContain(fms.radioState.nav1)
  expect(lines()[2]).toContain(station.ident)
  const radial = fms.navRadial('nav1')!
  expect(lines()[4]).toMatch(new RegExp(`^RAD:${String(Math.round(radial) || 360).padStart(3, '0')}`))
  expect(lines()[10]).toMatch(/^DME:\d+\.\dNM/)
  // DME distance: **** in amber when the DME's feedback is not valid; blank when no distance comes back (M300 13-22).
  fms.radioPort!.setDmeHold('dme1', false)
  fms.setRadioFaults('dme1', { receiver: 'FAILED' })
  step(1)
  expect(lines()[10]).toMatch(/^DME:\*\*\*\*/)
  expect(JSON.stringify(fms.screen()[10])).toMatch(/amber/)
  fms.setRadioFaults('dme1', { receiver: 'NORMAL' })
  fms.setRadio('nav1', '117.95')
  step(2)
  expect(fms.navStation('nav1')).toBeUndefined()
  expect(lines()[10]).not.toMatch(/^DME:/)
  void bearingDeg; void distanceNm
})

test('F8b: a NAV entry is range- and spacing-checked (108.00 to 117.95 at 50 kHz) and puts the NAV in MAN', () => {
  const { fms, step, enter, lines } = unitAt()
  step(3)
  fms.open('NAV_RADIO')
  enter('117.97', 'LSK1L')
  expect(lines()[SCRATCHPAD_LINE].trim()).toBe('INVALID ENTRY')
  fms.press('CLR')
  enter('112.33', 'LSK1L')
  expect(lines()[SCRATCHPAD_LINE].trim()).toBe('INVALID ENTRY')
  fms.press('CLR')
  enter('107.95', 'LSK1L')
  expect(lines()[SCRATCHPAD_LINE].trim()).toBe('INVALID ENTRY')
  fms.press('CLR')
  const other = fms.vorDmeStations().at(-1)!
  enter(other.frequency, 'LSK1L')
  expect(fms.navRadioMode('nav1')).toBe('MAN')
  step(1)
  expect(fms.navStation('nav1')!.ident).toBe(other.ident)
})

test('F8b: NAV 2/2 toggles AUTOMATIC and MANUAL, and back in AUTOMATIC the FMS retunes the nearest VOR/DME', () => {
  const { fms, step, lines } = unitAt()
  step(3)
  fms.open('NAV_RADIO', 1)
  expect(lines()[2]).toMatch(/^>AUTOMATIC\s+AUTOMATIC<$/)
  fms.press('LSK1L')
  expect(fms.navRadioMode('nav1')).toBe('MAN')
  fms.setRadio('nav1', fms.vorDmeStations().at(-1)!.frequency)
  step(2)
  fms.open('NAV_RADIO', 1)
  fms.press('LSK1L')
  expect(fms.navRadioMode('nav1')).toBe('AUTO')
  step(2)
  expect(fms.navStation('nav1')!.ident).toBe(fms.nearestVorDme()!.ident)
})

test('F8b: a radio test asks CONFIRM?, runs, and ends PASS, FAIL or TIMEOUT by the radio\'s health (M300 13-23, 13-25)', () => {
  const { fms, step, lines } = unitAt()
  step(1)
  fms.setRadioFaults('dme2', { receiver: 'FAILED' })
  fms.setRadioFaults('adf2', { measurementBus: 'LOST' })
  fms.open('NAV_RADIO', 1)
  fms.press('LSK3L')
  expect(lines()[6]).toMatch(/^>CONFIRM\?/)
  fms.press('LSK3L')
  expect(lines()[6]).toMatch(/^>STARTED/)
  fms.press('LSK5R'); fms.press('LSK5R')
  fms.open('ADF_RADIO', 1)
  fms.press('LSK5R'); fms.press('LSK5R')
  step(RADIO_TEST_S - 1)
  expect(fms.radioPort!.testState('nav1')).toBe('STARTED')
  step(1)
  expect(fms.radioPort!.testState('nav1')).toBe('PASS')
  expect(fms.radioPort!.testState('dme2')).toBe('FAIL')
  expect(fms.radioPort!.testState('adf2')).toBe('TIMEOUT')
  fms.open('NAV_RADIO', 1)
  expect(lines()[6]).toMatch(/^>PASS/)
  expect(lines()[10]).toMatch(/FAIL<$/)
})

test('F8b: DME HOLD freezes the DME on its station while the NAV is retuned (M300 13-22)', () => {
  const { fms, step } = unitAt()
  step(3)
  const held = fms.navStation('nav1')!
  fms.open('NAV_RADIO')
  fms.press('LSK4L')
  expect(fms.radioPort!.dmeHold('dme1')).toBe(held.frequency)
  // NAV2's station: another VOR/DME the radios receive now, so its words are already valid.
  const other = fms.navStation('nav2')!
  fms.setRadio('nav1', other.frequency)
  // The newly tuned station takes its acquisition time before its bearing is valid.
  step(6)
  expect(fms.navStation('nav1')!.ident).toBe(other.ident)
  expect(fms.dmeStation('dme1')!.ident).toBe(held.ident)
  const observation = (ident: string) => fms.radioObservations().find(entry => entry.station.ident === ident)
  expect(observation(held.ident)?.slantRangeNm.status).toBe('NORMAL')
  expect(observation(other.ident)?.bearingTrue.status).toBe('NORMAL')
  fms.press('LSK4L')
  expect(fms.radioPort!.dmeHold('dme1')).toBeNull()
  expect(fms.dmeStation('dme1')!.ident).toBe(other.ident)
})

test('F8b: the ADF page tunes an NDB and shows its bearing, relative, magnetic or true; ANT gives none (M300 13-24)', () => {
  const { fms, step, enter, lines } = unitAt()
  step(3)
  fms.press('RADIO'); fms.press('NEXT')
  fms.press('LSK5L')
  expect(lines()[0]).toMatch(/^ADF\s+1\/2$/)
  enter('1800', 'LSK1L')
  expect(lines()[SCRATCHPAD_LINE].trim()).toBe('INVALID ENTRY')
  fms.press('CLR')
  const ndb = fms.navdb.nearby(fms.truePosition, 75).find(entry => entry.kind === 'navaid' && entry.type === 'NDB')!
  enter((ndb as { frequency: string }).frequency, 'LSK1L')
  step(1)
  expect(lines()[9]).toMatch(/^ REL-BRG/)
  const relative = fms.adfBearing('adf')!
  expect(relative).not.toBeNull()
  expect(lines()[10]).toMatch(new RegExp(`^${String(Math.round(relative) || 360).padStart(3, '0')}°`))
  fms.press('LSK5L')
  expect(lines()[9]).toMatch(/^ MAG-BRG/)
  const declination = fms.magneticField!.declination
  expect(Math.abs(declination)).toBeGreaterThan(1)
  expect(fms.adfBearing('adf')).toBeCloseTo(((relative + fms.heading - declination) % 360 + 360) % 360, 5)
  fms.press('LSK5L')
  expect(lines()[9]).toMatch(/^ TRUE-BRG/)
  expect(fms.adfBearing('adf')).toBeCloseTo((relative + fms.heading) % 360, 5)
  fms.press('LSK3L')
  expect(lines()[6]).toMatch(/^>ANT/)
  expect(fms.adfBearing('adf')).toBeNull()
  expect(lines()[10]).toMatch(/^---/)
})

test('F8b: a failed radio\'s frequency is small amber on the RADIO page (M300 13-3)', () => {
  const { fms, step } = unitAt()
  step(1)
  fms.press('RADIO')
  expect(JSON.stringify(fms.screen()[6])).not.toMatch(/amber/)
  fms.setRadioFaults('nav1', { receiver: 'FAILED' })
  step(1)
  expect(JSON.stringify(fms.screen()[6])).toMatch(/amber/)
})
