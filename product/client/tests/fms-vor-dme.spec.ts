import { expect, logicTest as test } from './isolated-client-test'
import { bearingDeg, distanceNm, offset } from '../src/fmsCdu/fmsModel'
import type { Navaid } from '../src/fmsCdu/navData'
import { radioFixes, vorDmeAccuracy } from '../src/fmsCdu/radioNavigation'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'
import type { RadioObservation } from '../src/fmsCdu/sensorPorts'

// Stage F plan F7 (M300 1-5, 12-19 to 12-20, 15-3): VOR/DME/TACAN from acknowledged, eligible tunings; M300 15-3's
// accuracy steps; reasonableness without the prior (coverage, and agreement between sources); VOR/DME/TCN STATUS.

const AT = { lat: 45, lon: -75 }, ALT = 3000, NOW = 10_000
const navaid = (ident: string, type: Navaid['type'], bearing: number, distance: number): Navaid => ({ kind: 'navaid', ident, type, name: 'Fixture',
  frequency: '115.00', elevation: { feet: 0, source: 'data', provenance: 'fixture' }, position: offset(AT, bearing, distance), ...(type === 'TACAN' ? { channel: '99X' } : {}) })
const observe = (stations: Navaid[], radialError: Record<string, number> = {}): RadioObservation[] => stations.map(s => ({ station: s,
  slantRangeNm: { at: NOW, sequence: 1, status: 'NORMAL', value: Math.hypot(distanceNm(AT, s.position), ALT / 6076.12) },
  bearingTrue: { at: NOW, sequence: 1, status: 'NORMAL', value: (bearingDeg(s.position, AT) + (radialError[s.ident] ?? 0) + 360) % 360 } }))
const vorFix = (observations: RadioObservation[]) => radioFixes(observations, AT, ALT, NOW).find(f => f.mode === 'VOR/DME')

test('F7: the 95% accuracy follows M300 15-3: 0.6 NM at the station to 0.8 NM at 7 NM, then 1.5 NM', () => {
  expect(vorDmeAccuracy(0)).toBeCloseTo(0.6, 9)
  expect(vorDmeAccuracy(3.5)).toBeCloseTo(0.7, 9)
  expect(vorDmeAccuracy(7)).toBeCloseTo(0.8, 9)
  expect(vorDmeAccuracy(7.01)).toBe(1.5)
  expect(vorFix(observe([navaid('NEAR', 'VORDME', 90, 5)]))!.anp).toBeCloseTo(0.6 + 0.2 * 5 / 7, 2)
  expect(vorFix(observe([navaid('FAR', 'VORDME', 90, 20)]))!.anp).toBeCloseTo(1.5, 2)
})

test('F7: a TACAN bearing and distance give a VOR/DME/TCN fix', () => {
  const fix = vorFix(observe([navaid('UHU', 'TACAN', 200, 6)]))!
  expect(fix.vor).toBe('UHU')
  expect(distanceNm(fix.position, AT)).toBeLessThan(0.05)
})

test('F7: an unreasonable radial is rejected: two VOR/DMEs that disagree are both left out; one that disagrees with DME/DME is rejected', () => {
  const two = [navaid('VA', 'VORDME', 90, 5), navaid('VB', 'VORDME', 0, 5)]
  expect(vorFix(observe(two))).toBeDefined()
  // A 30-degree radial error moves VA's fix 2.6 NM: more than both accuracies together; neither can be trusted alone.
  expect(vorFix(observe(two, { VA: 30 }))).toBeUndefined()
  const withDmeDme = [navaid('VA', 'VORDME', 90, 5), navaid('D1', 'DME', 0, 10), navaid('D2', 'DME', 120, 10), navaid('D3', 'DME', 240, 10)]
  const fixes = radioFixes(observe(withDmeDme, { VA: 30 }), AT, ALT, NOW)
  expect(fixes.find(f => f.mode === 'DME/DME')).toBeDefined()
  expect(fixes.find(f => f.mode === 'VOR/DME')).toBeUndefined()
  expect(fixes[0].rejected).toEqual(expect.arrayContaining([{ ident: 'VA', reason: 'VOR/DME position disagrees with another source' }]))
})

test('F7: a range beyond line-of-sight coverage is rejected as unreasonable', () => {
  // At 3,000 ft the radio horizon is about 67 NM: a VOR/DME reporting 150 NM cannot be the station.
  const far = navaid('FAR', 'VORDME', 90, 150)
  const fixes = radioFixes(observe([far]), AT, ALT, NOW)
  expect(fixes.find(f => f.mode === 'VOR/DME')).toBeUndefined()
  expect(fixes.length === 0 || fixes[0].rejected.some(r => r.ident === 'FAR' && /coverage/.test(r.reason))).toBe(true)
})

function unit() {
  let now = Date.UTC(2026, 8, 30, 14)
  const fms = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  return { fms, step, lines: () => screenText(fms.screen()) }
}

test('F7: a pending, unacknowledged tuning gives no fix: the NAV reports its old station until the radio confirms', () => {
  const { fms, step } = unit()
  step(5)
  const before = fms.navStation('nav1')!
  const onNav2 = fms.navStation('nav2')?.ident
  const other = fms.vorDmeStations().find(station => station.ident !== before.ident && station.ident !== onNav2)
  expect(other).toBeDefined()
  if (!other) return
  fms.setRadio('nav1', other.frequency)
  expect(fms.radioRequests[0]).toMatchObject({ device: 'nav1', status: 'PENDING' })
  expect(fms.navStation('nav1')!.ident).toBe(before.ident)
  fms.updateNavigation(0)
  expect(fms.radioObservations().find(o => o.station.ident === other.ident)?.bearingTrue.status ?? 'NCD').not.toBe('NORMAL')
})

test('F7: VOR/DME/TCN STATUS shows each NAV and DME, the TACAN and the position, from NAV STATUS (M300 12-20)', () => {
  const { fms, step, lines } = unit()
  step(6)
  fms.open('NAV_STATUS')
  fms.press('LSK3L')
  expect(lines()[0]).toMatch(/^VOR\/DME\/TCN STATUS\s+1\/1$/)
  expect(lines()[2]).toMatch(/^VOR1 /)
  expect(lines()[3]).toMatch(/^DME1 /)
  expect(lines()[4]).toMatch(/^VOR2 /)
  expect(lines()[5]).toMatch(/^DME2 /)
  expect(lines()[7]).toMatch(/^TCN /)
  const station = fms.navStation('nav1')
  if (station) expect(lines()[2]).toContain(station.ident)
  expect(lines()[12]).toMatch(/^<NAV STATUS/)
})
