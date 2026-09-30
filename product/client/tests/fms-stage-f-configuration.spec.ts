import { readdirSync, readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { CIVIL_SAR_CONFIGURATION, STAGE_F_SENSORS } from '../src/fmsCdu/configuration'
import type { NavPageId } from '../src/fmsCdu/fmsModel'
import { NAV_MODES } from '../src/fmsCdu/navigation'
import { NAV_PAGES } from '../src/fmsCdu/navPages'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'

// Stage F plan F0 (DEC-150): each navigation sensor is declared on or off against the M300, and the bench never
// simulates, offers or annunciates equipment the profile declares absent.

const options = CIVIL_SAR_CONFIGURATION.options as Record<string, { configured: boolean; implementation: string; source: string; reason: string }>
const sourceDir = new URL('../src/fmsCdu/', import.meta.url)
const alertLiterals = new Set(readdirSync(sourceDir).filter(name => /\.tsx?$/.test(name))
  .flatMap(name => [...readFileSync(new URL(name, sourceDir), 'utf8').matchAll(/\b(?:alert|advisory)\("([^"]+)"\)/g)].map(match => match[1])))

function navPageText() {
  const fms = new ScriptedFms(() => new Date(Date.UTC(2026, 8, 30, 12)))
  return (Object.keys(NAV_PAGES) as NavPageId[]).map(id => {
    fms.open(id)
    return screenText(fms.screen()).join('\n')
  }).join('\n')
}
const word = (text: string) => new RegExp(`(^|[^A-Z/])${text.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')}($|[^A-Z/])`)

test('F0: every Stage F sensor is declared against the M300, with DEC-150 choices', () => {
  const expected: Record<string, boolean> = {
    gpsDual: true, dme1: true, dme2: true, nav1: true, nav2: true,
    tacan: true, kalman: true, doppler: true, autoVorNavigation: true, ndbApproach: true,
    irs: false, military: false, externalRadioHead: false, anpHil99999: false,
  }
  for (const [key, configured] of Object.entries(expected)) {
    expect(options[key], key).toBeDefined()
    expect(options[key].configured, key).toBe(configured)
    expect(options[key].source, key).toMatch(/^M300 /)
  }
  for (const key of ['tacan', 'kalman', 'doppler', 'autoVorNavigation', 'ndbApproach', 'externalRadioHead', 'anpHil99999']) {
    expect(options[key].reason, key).toMatch(/DEC-150/)
  }
  // The AUTO VOR choice departs from the manual: it names the manual default it overrides.
  expect(options.autoVorNavigation.source).toMatch(/12-19/)
  expect(options.autoVorNavigation.reason).toMatch(/overrides/)
  // Every sensor in the registry is a declared option.
  for (const sensor of STAGE_F_SENSORS) expect(options[sensor.option], sensor.option).toBeDefined()
})

test('F0: equipment declared off has no navigation mode, navigation page prompt or alert', () => {
  const pages = navPageText()
  const off = STAGE_F_SENSORS.filter(sensor => !options[sensor.option].configured)
  expect(off.map(sensor => sensor.option).sort()).toEqual(['externalRadioHead', 'irs', 'military'])
  for (const sensor of off) {
    for (const mode of sensor.modes) expect(NAV_MODES as readonly string[], `${sensor.option} mode ${mode}`).not.toContain(mode)
    for (const prompt of sensor.prompts) expect(pages, `${sensor.option} prompt ${prompt}`).not.toMatch(word(prompt))
    for (const alert of sensor.alerts) expect(alertLiterals.has(alert), `${sensor.option} alert ${alert}`).toBe(false)
  }
})

test('F0: a declared sensor has a consumer exactly when its status says it is built', () => {
  const pages = navPageText()
  for (const sensor of STAGE_F_SENSORS.filter(entry => options[entry.option].configured)) {
    const status = options[sensor.option].implementation
    const consumers = [
      ...sensor.modes.filter(mode => (NAV_MODES as readonly string[]).includes(mode)),
      ...sensor.prompts.filter(prompt => word(prompt).test(pages)),
      ...sensor.alerts.filter(alert => alertLiterals.has(alert)),
    ]
    if (status === 'implemented' || status === 'partial') expect(consumers.length, `${sensor.option} is ${status} but nothing consumes it`).toBeGreaterThan(0)
    // A pending sensor claims no behaviour yet: its mode, prompt and alerts arrive with the item that builds it.
    if (status === 'pending') expect(consumers, `${sensor.option} is pending but already consumed`).toEqual([])
  }
})
