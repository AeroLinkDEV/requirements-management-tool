import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { distanceNm, type LatLon } from '../src/fmsCdu/fmsModel'
import type { GpsBus, GpsReceiver } from '../src/fmsCdu/gps'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// GPS phase 3a: the FMS navigates on two simulated CMA-5024 receivers (gps.ts) through their buses alone. It chooses a
// receiver by its words' status and HIL against the phase's alert limit, falls back to radio and dead reckoning, and
// compares the two. The receivers' own physics is proved in fms-gps.spec.ts and fms-gps-sbas.spec.ts.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const setup = () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  expect(receivers(unit), 'the FMS owns GPS1 and GPS2').toHaveLength(2)
  return { unit, advance: (ms: number) => { now += ms; unit.updateNavigation(ms / 1000) } }
}
// Read without assuming the property exists, so a missing integration fails on an expectation, not a TypeError.
function receivers(unit: ScriptedFms): GpsReceiver[] { return (unit as unknown as { gps?: GpsReceiver[] }).gps ?? [] }
const bus = (unit: ScriptedFms, index: number): GpsBus | null => receivers(unit)[index]?.bus() ?? null
const fix = (b: GpsBus | null): LatLon | null =>
  b && b['110'].value !== null && b['120'].value !== null && b['111'].value !== null && b['121'].value !== null
    ? { lat: b['110'].value + b['120'].value, lon: b['111'].value + b['121'].value } : null
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const recalled = (unit: ScriptedFms, text: string) => unit.recallList.some(message => message.text === text)
const navStatus = (unit: ScriptedFms) => { press(unit, 'PROG', 'LSK6R'); return lines(unit) }
const metresApart = (a: LatLon, b: LatLon) => distanceNm(a, b) * 1852

test('the FMS owns two navigating receivers and its position in GPS mode is GPS1\'s fix (3a.1, 3a.3)', () => {
  const { unit } = setup()
  expect(receivers(unit)).toHaveLength(2)
  const [one, two] = [bus(unit, 0), bus(unit, 1)]
  for (const b of [one, two]) expect(['NAV', 'SBAS_NAV', 'SBAS_PA']).toContain(b?.['273'].value?.mode)
  expect(unit.navState.mode).toBe('GPS')
  const gps1 = fix(one)!, gps2 = fix(two)!
  expect(metresApart(unit.position, gps1)).toBeLessThan(0.01)
  // Different seeds: two receivers, not one copied.
  expect(metresApart(gps1, gps2)).toBeGreaterThan(0.01)
  expect(navStatus(unit)[2]).toMatch(/^GPS1\b/)
})

test('ANP in GPS mode is the selected receiver\'s HFOM (label 247), not a fixed value (3a.3)', () => {
  const { unit } = setup()
  const hfom = bus(unit, 0)!['247'].value!
  expect(unit.navState.anp).toBeCloseTo(Math.max(0.02, hfom), 6)
  expect(unit.navPerformance.anp).toBeCloseTo(unit.navState.anp, 9)
})

test('a GPS1 receiver fault moves navigation to GPS2 without GPS NAV LOST, and the change is recorded (3a.2)', () => {
  const { unit, advance } = setup()
  receivers(unit)[0]?.injectFault('RECEIVER', true)
  advance(1000)
  expect(unit.navState.mode).toBe('GPS')
  expect(metresApart(unit.position, fix(bus(unit, 1))!)).toBeLessThan(0.01)
  expect(navStatus(unit)[2]).toMatch(/^GPS2\b/)
  expect(recalled(unit, 'GPS NAV LOST')).toBe(false)
  expect(unit.navSourceLog.map(entry => entry.source)).toContain('GPS2')
})

test('a position word that is not Normal is not used: the FMS moves to GPS2 even with the value present (3a.2)', () => {
  const { unit, advance } = setup()
  const word = bus(unit, 0)!['110']
  // The value is still on the bus, but No Computed Data: the FMS must not navigate on it.
  receivers(unit)[0]?.override('110', { kind: 'FORCE', value: word.value!, ssm: 'NCD' })
  advance(1000)
  expect(bus(unit, 0)!['110'].value).not.toBeNull()
  expect(unit.navState.mode).toBe('GPS')
  expect(navStatus(unit)[2]).toMatch(/^GPS2\b/)
})

test('on an RNAV approach a receiver reporting no approach level gives NO APPR INTEGRITY while GPS still navigates (3a.4)', () => {
  const { unit, advance } = setup()
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  for (let i = 0; i < 3; i += 1) unit.sequence()
  advance(1000)
  expect(unit.flightPhase).toBe('APPROACH')
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(false)
  for (const receiver of receivers(unit)) receiver.override('305', { kind: 'FORCE', value: { paActive: false, provider: null, level: 'NONE' }, ssm: 'NORMAL' })
  advance(1000)
  expect(unit.navState.mode).toBe('GPS')
  expect(recalled(unit, 'NO APPR INTEGRITY')).toBe(true)
  expect(unit.approachType).toBe('NO APPR')
})

test('GPS1 with HIL over the phase alert limit gives GPS POS UNCERTAIN and GPS2 takes over (3a.4)', () => {
  const { unit, advance } = setup()
  expect(unit.flightPhase).toBe('TERMINAL')
  // Terminal HAL is 1 NM: an HIL of 1.5 NM on an otherwise normal bus fails it.
  receivers(unit)[0]?.override('130', { kind: 'FORCE', value: 1.5, ssm: 'NORMAL' })
  advance(1000)
  expect(recalled(unit, 'GPS POS UNCERTAIN')).toBe(true)
  expect(navStatus(unit)[2]).toMatch(/^GPS2\b/)
  // A failure warning on 130 is the same loss of integrity.
  receivers(unit)[0]?.override('130', { kind: 'FORCE', ssm: 'FW' })
  advance(1000)
  expect(navStatus(unit)[2]).toMatch(/^GPS2\b/)
})

test('the GPS lost condition takes the RF input from both receivers: GPS NAV LOST and radio navigation (3a.4, 3a.5)', () => {
  const { unit } = setup()
  unit.setCondition('gpsLost', true)
  for (const index of [0, 1]) expect(bus(unit, index)?.['355'].value?.rfInput, `GPS${index + 1}`).toBe(true)
  expect(unit.navState.mode).not.toBe('GPS')
  expect(recalled(unit, 'GPS NAV LOST')).toBe(true)
  unit.setCondition('gpsLost', false)
  expect(unit.navState.mode).toBe('GPS')
})

test('the GPS integrity condition is a satellite fault neither receiver can exclude: GPS POS UNCERTAIN, retained position without authority (3a.4, S300 1-7)', () => {
  const { unit } = setup()
  unit.setCondition('gpsIntegrity', true)
  for (const index of [0, 1]) {
    const status = bus(unit, index)?.['273'].value
    expect(status?.integrity, `GPS${index + 1}`).toBe('DETECTED')
    expect(bus(unit, index)?.['130'].ssm, `GPS${index + 1}`).toBe('FW')
  }
  expect(recalled(unit, 'GPS POS UNCERTAIN')).toBe(true)
  expect(unit.navState).toMatchObject({ mode: 'GPS', uncertain: true })
  expect(unit.gpsStatus.chosen).toBeNull()
  expect(unit.hoverFeedback).toBeNull()
  // The GPS line on NAV STATUS reads the RAIM state from 273, not a fixed text.
  expect(navStatus(unit)[6]).toMatch(/\b5 SAT NO RAIM$/)
  unit.setCondition('gpsIntegrity', false)
  expect(unit.navState.mode).toBe('GPS')
})

test('the GPS integrity condition holds through a whole flight as the sky moves: RAIM always sees the faulted satellite (3a.5)', () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(unit)
  unit.selectProcedure('APPROACH', 'R24R')
  unit.press('EXEC')
  unit.setCondition('gpsIntegrity', true)
  // The flight to the approach, second by second: a fault RAIM cannot see would let a receiver pass for GPS again.
  const passed: string[] = []
  for (let t = 0; t < 2 * 3600; t += 1) {
    now += 1000
    sim.step(1)
    unit.gpsStatus.assessed.forEach((a, index) => { if (a.usable || a.integrity !== 'DETECTED') passed.push(`GPS${index + 1} at ${t} s: ${a.integrity}`) })
  }
  expect(passed.slice(0, 5)).toEqual([])
  expect(unit.gpsStatus.chosen).toBeNull()
  expect(unit.navState.uncertain).toBe(true)
})

test('GPS NAV on NAV OPTIONS selects AUTO, GPS1, GPS2 or off, and a manual choice does not fall to the other receiver (3a.2)', () => {
  const { unit, advance } = setup()
  press(unit, 'PROG', 'LSK6R', 'LSK6R')
  expect(lines(unit)[0]).toMatch(/^NAV OPTIONS/)
  expect(lines(unit)[6]).toMatch(/^<AUTO\/GPS1\/GPS2\/OFF/)
  press(unit, 'LSK3L')
  expect(unit.gpsReceiverChoice).toBe('GPS1')
  // The chosen option is large and green: "<AUTO/" puts GPS1 at columns 6 to 9.
  expect(unit.screen()[6].slice(6, 10).map(cell => `${cell.size} ${cell.color}`)).toEqual(Array(4).fill('large green'))
  receivers(unit)[0]?.injectFault('RECEIVER', true)
  advance(1000)
  // GPS1 chosen by hand and failed: the FMS does not silently use GPS2.
  expect(unit.navState.mode).not.toBe('GPS')
  press(unit, 'PROG', 'LSK6R', 'LSK6R', 'LSK3L')
  expect(unit.navState.mode).toBe('GPS')
  expect(metresApart(unit.position, fix(bus(unit, 1))!)).toBeLessThan(0.01)
})

test('a spoofed GPS1 walks the FMS position off, and only the GPS1/GPS2 compare catches it (3a.4)', () => {
  const { unit, advance } = setup()
  // Spoofing: consistent false position words with a Normal status; integrity reads fine.
  receivers(unit)[0]?.override('110', { kind: 'BIAS', amount: 0.01 })
  advance(1000)
  expect(unit.navState.mode).toBe('GPS')
  expect(distanceNm(unit.position, unit.truePosition)).toBeGreaterThan(0.5)
  expect(recalled(unit, 'GPS POS UNCERTAIN')).toBe(false)
  expect(recalled(unit, 'GPS DISAGREE')).toBe(true)
})

test('an undetected satellite bias on GPS1 stays under the compare limit: integrity fine, no alert, position biased (3a.4)', () => {
  const { unit, advance } = setup()
  const truth = unit.truePosition
  const before = metresApart(fix(bus(unit, 0))!, truth)
  const used = bus(unit, 0)!['060'].map(word => word.value!).filter(sat => sat.used && !sat.sbas)
  // A step small enough that RAIM does not detect it.
  receivers(unit)[0]?.satelliteFault(used[0].prn, { kind: 'STEP', metres: 4 })
  advance(1000)
  expect(bus(unit, 0)!['273'].value!.integrity).toBe('OK')
  expect(metresApart(fix(bus(unit, 0))!, unit.truePosition)).not.toBeCloseTo(before, 3)
  expect(recalled(unit, 'GPS DISAGREE')).toBe(false)
  expect(recalled(unit, 'GPS POS UNCERTAIN')).toBe(false)
})

test('the GPS pages show the receivers\' counts, HIL and mode, not fixed values (3a.6)', () => {
  const { unit } = setup()
  // One satellite deselected on GPS1: still visible but not used, so the page must show both numbers, not one twice.
  const highest = bus(unit, 0)!['060'].map(word => word.value!).filter(sat => sat.used && !sat.sbas)[0]
  receivers(unit)[0]?.deselect([highest.prn])
  unit.gpsUpdated()
  const status = bus(unit, 0)!['273'].value!
  expect(status.used).toBeLessThan(status.visible)
  const hil = bus(unit, 0)!['130'].value!
  press(unit, 'PROG', 'NEXT', 'NEXT')
  expect(lines(unit)[0]).toMatch(/\bPROGRESS\s+3\/4$/)
  expect(lines(unit)[2]).toMatch(new RegExp(`\\b${status.used} SAT\\b`))
  expect(lines(unit)[2]).toMatch(new RegExp(`${hil.toFixed(2)}NM\\s*$`))
  expect(navStatus(unit)[6]).toMatch(new RegExp(`\\b${status.used} SAT\\b`))
  press(unit, 'LSK3R')
  expect(lines(unit)[0]).toMatch(/^GPS STATUS/)
  const second = bus(unit, 1)!['273'].value!
  // SAT USED/VIS: GPS1 in the left column, GPS2 in the right.
  expect(lines(unit)[4]).toMatch(new RegExp(`^${status.used}/${status.visible}\\s.*\\s${second.used}/${second.visible}$`))
  press(unit, 'LSK6R')
  expect(lines(unit)[0]).toMatch(/^POS SENSORS/)
  expect(lines(unit)[SCRATCHPAD_LINE].trim()).toBe('')
})
