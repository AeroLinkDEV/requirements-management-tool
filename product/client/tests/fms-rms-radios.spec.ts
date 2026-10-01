import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { HELICOPTER_PROFILE } from '../src/fmsCdu/profile'
import { RADIO_MESSAGE_ROWS, RADIO_NAMES, RadioManagementSystem } from '../src/fmsCdu/radioManagement'
import { APPENDIX_E } from '../src/fmsCdu/appendixE'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'

// Stage F plan F8a (M300 13-1 to 13-26, 3-26, Appendix E), on the shared civil RMS of #1350: each radio device answers
// with feedback, its separate internal states (plan C3) decide what it reports and which Appendix E rows are met, and navigation measures only through what the radios report.
// The FMS is the only tuning source (DEC-150).

const T0 = Date.UTC(2026, 8, 30, 14)
const LATENCY = 0.25, TIMEOUT = 2

function rmsAt() {
  let now = T0
  const rms = new RadioManagementSystem(() => now, () => true, () => {}, LATENCY, TIMEOUT)
  return { rms, one: rms.port(1), two: rms.port(2), advance: (seconds: number) => { now += seconds * 1000; rms.tick() } }
}

test('F8a: a tune request stays pending until the radio acknowledges it, and the state is the acknowledged frequency', () => {
  const { one, advance } = rmsAt()
  one.tune('nav1', '115.80')
  expect(one.requests[0]).toMatchObject({ device: 'nav1', value: '115.80', status: 'PENDING' })
  expect(one.state.nav1).toBe('113.90')
  advance(LATENCY)
  expect(one.requests[0].status).toBe('ACK')
  expect(one.receiving('nav1')).toBe('115.80')
})

test('C3: a command-only timeout raises CONTROL LOST (E-13) on the requesting computer alone; the live bus keeps the measurements and no FAILED row is met', () => {
  const { rms, one, two, advance } = rmsAt()
  rms.setFaults('nav1', { controlPath: 'LOST' })
  expect(one.drainEvents()).toEqual([])
  two.tune('nav1', '115.80')
  advance(TIMEOUT - 0.5)
  expect(two.requests[0].status).toBe('PENDING')
  advance(0.5)
  expect(two.requests[0].status).toBe('TIMEOUT')
  expect(two.drainEvents()).toEqual([{ kind: 'alert', text: 'NAV1 CONTROL LOST', row: 'E-13' }])
  expect(one.drainEvents()).toEqual([])
  expect(one.receiving('nav1')).toBe('113.90')
  advance(5)
  expect(two.drainEvents()).toEqual([])
})

test('C3: an ADF receiver failure meets both E-2 (alert, with its configuration and inhibit) and E-21 (advisory) on both computers', () => {
  const { rms, one, two } = rmsAt()
  rms.setFaults('adf', { receiver: 'FAILED' })
  const expected = [
    { kind: 'alert', text: 'ADF1 CONTROL LOST', row: 'E-2', inhibit: 'polarOrRoll', configuredBy: 'adfControlLostAlert' },
    { kind: 'advisory', text: 'ADF1 FAILED', row: 'E-21' },
  ]
  expect(one.drainEvents()).toEqual(expected)
  expect(two.drainEvents()).toEqual(expected)
  expect(one.receiving('adf')).toBeNull()
  // Still met: nothing new. Cleared and met again: raised again.
  rms.setFaults('adf', { measurementBus: 'LOST' })
  expect(one.drainEvents()).toEqual([])
  rms.setFaults('adf', { receiver: 'NORMAL', measurementBus: 'NORMAL' })
  expect(one.receiving('adf')).toBe('0350')
  rms.setFaults('adf', { measurementBus: 'LOST' })
  expect(one.drainEvents().map(event => event.row)).toEqual(['E-2', 'E-21'])
})

test('C3: each FAILED advisory is raised from its own row: DME on its bus (E-23), NAV on its radio or bus (E-27)', () => {
  const { rms, one } = rmsAt()
  // A DME receiver failure stops its ranges, but E-23 names only the communication bus: no advisory.
  rms.setFaults('dme1', { receiver: 'FAILED' })
  expect(one.dmeReceiving('dme1')).toBe(false)
  expect(one.drainEvents()).toEqual([])
  rms.setFaults('dme1', { measurementBus: 'LOST' })
  expect(one.drainEvents()).toEqual([{ kind: 'advisory', text: 'DME1 FAILED', row: 'E-23' }])
  rms.setFaults('nav2', { receiver: 'FAILED' })
  expect(one.drainEvents()).toEqual([{ kind: 'advisory', text: 'NAV2 FAILED', row: 'E-27' }])
  expect(one.receiving('nav2')).toBeNull()
})

test('C3: a superseded command raises nothing, a rejected one is amber-worthy but no fault, and the #1350 device failure is a lost control path', () => {
  const { rms, one, two, advance } = rmsAt()
  one.tune('nav2', '112.80')
  two.tune('nav2', '113.20')
  expect(one.requests[0].status).toBe('SUPERSEDED')
  advance(LATENCY)
  expect(two.requests[0].status).toBe('ACK')
  rms.rejectNext('adf')
  one.tune('adf', '0400')
  advance(LATENCY)
  expect(one.requests[0].status).toBe('REJECTED')
  expect([...one.drainEvents(), ...two.drainEvents()]).toEqual([])
  rms.injectFailure('com1', true)
  expect(rms.faults('com1')).toMatchObject({ controlPath: 'LOST', measurementBus: 'NORMAL', receiver: 'NORMAL' })
  expect(one.receiving('com1')).toBe('121.500')
})

test('C3: a radio under TEST gives no navigation measurements until its result', () => {
  const { one, advance } = rmsAt()
  one.pressTest('nav1'); one.pressTest('nav1')
  expect(one.testState('nav1')).toBe('STARTED')
  expect(one.receiving('nav1')).toBeNull()
  advance(3)
  expect(one.testState('nav1')).toBe('PASS')
  expect(one.receiving('nav1')).toBe('113.90')
})

function standalone() {
  let now = T0
  const fms = new ScriptedFms(() => new Date(now))
  const step = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; fms.updateNavigation(1) } }
  return { fms, step }
}

test('F8a: with both DME transceivers failed there is no range at all: no DME/DME, no VOR/DME', () => {
  const { fms, step } = standalone()
  fms.setCondition('gpsLost', true)
  step(30)
  expect(['DME/DME', 'VOR/DME']).toContain(fms.navState.mode)
  fms.setRadioFaults('dme1', { measurementBus: 'LOST' })
  fms.setRadioFaults('dme2', { measurementBus: 'LOST' })
  step(30)
  // No radio mode remains (KALMAN or DVS may carry the position: F11).
  expect(['DME/DME', 'VOR/DME']).not.toContain(fms.navState.mode)
  expect(fms.lastAdvisories).toEqual(expect.arrayContaining(['DME1 FAILED', 'DME2 FAILED']))
  expect(fms.radioObservations().every(observation => observation.slantRangeNm.status !== 'NORMAL')).toBe(true)
})

test('F8a: NAV AUTO tunes the nearest VOR/DME; its bearing feeds VOR/DME under autoVorNavigation, and only in MAN without it (M300 12-19)', () => {
  const bearingOf = (fms: ScriptedFms, ident: string) => fms.radioObservations().find(observation => observation.station.ident === ident)?.bearingTrue.status
  const { fms, step } = standalone()
  step(5)
  const nearest = fms.nearestVorDme()!
  expect(fms.navRadioMode('nav1')).toBe('AUTO')
  expect(fms.radioReceiving('nav1')).toBe(nearest.frequency)
  expect(bearingOf(fms, nearest.ident)).toBe('NORMAL')
  let now = T0
  const options = { ...HELICOPTER_PROFILE.configuration.options, autoVorNavigation: { ...HELICOPTER_PROFILE.configuration.options.autoVorNavigation, configured: false } }
  const manual = new ScriptedFms(() => new Date(now), { profile: { ...HELICOPTER_PROFILE, configuration: { ...HELICOPTER_PROFILE.configuration, options } } })
  const run = (seconds: number) => { for (let i = 0; i < seconds; i++) { now += 1000; manual.updateNavigation(1) } }
  run(5)
  expect(manual.navRadioMode('nav1')).toBe('AUTO')
  expect(manual.radioReceiving('nav1')).toBe(nearest.frequency)
  expect(bearingOf(manual, nearest.ident)).not.toBe('NORMAL')
  manual.setRadio('nav1', nearest.frequency)
  run(5)
  expect(manual.navRadioMode('nav1')).toBe('MAN')
  expect(bearingOf(manual, nearest.ident)).toBe('NORMAL')
})

test('F8a: a crew entry puts NAV in MAN; bearings come only from reported stations, and a stuck NAV raises CONTROL LOST', () => {
  const { fms, step } = standalone()
  step(5)
  const other = fms.vorDmeStations().find(station => station.ident !== fms.nearestVorDme()!.ident)!
  fms.setRadio('nav1', other.frequency)
  step(1)
  expect(fms.navRadioMode('nav1')).toBe('MAN')
  expect(fms.radioReceiving('nav1')).toBe(other.frequency)
  // In MAN the FMS's own AUTO tuning leaves the NAV where the crew put it.
  step(5)
  expect(fms.radioReceiving('nav1')).toBe(other.frequency)
  const reported = [fms.radioReceiving('nav1'), fms.radioReceiving('nav2')]
  for (const observation of fms.radioObservations().filter(entry => entry.bearingTrue.status === 'NORMAL')) {
    expect(reported).toContain(observation.station.frequency)
  }
  fms.setRadioFaults('nav1', { controlPath: 'LOST' })
  fms.setRadio('nav1', fms.nearestVorDme()!.frequency)
  step(3)
  expect(fms.radioReceiving('nav1')).toBe(other.frequency)
  expect(fms.recallList.map(message => message.text)).toContain('NAV1 CONTROL LOST')
})

test('F8a: the RADIO page shows a frequency being tuned in inverse, then the acknowledged one (M300 13-3)', () => {
  const { fms, step } = standalone()
  fms.press('RADIO')
  fms.setScratch('118.30')
  fms.press('LSK1L')
  const line = fms.screen()[2]
  expect(screenText(fms.screen())[2]).toMatch(/^118\.300/)
  expect(JSON.stringify(line)).toMatch(/"inverse":true/)
  step(1)
  expect(fms.radioState.com1).toBe('118.300')
  expect(JSON.stringify(fms.screen()[2])).not.toMatch(/"inverse":true/)
})

test('F8a: two computers share the NAVs: FMS 1 auto-tunes them, and both navigate on what the shared radios report', () => {
  let now = T0
  const system = new DualFmsSystem(() => new Date(now))
  const [one, two] = system.computers
  for (let i = 0; i < 5; i++) { now += 1000; system.tick() }
  const nearest = one.nearestVorDme()!
  expect(one.radioState.nav1).toBe(nearest.frequency)
  expect(two.radioState.nav1).toBe(nearest.frequency)
  system.rms.setFaults('nav1', { receiver: 'FAILED' })
  for (let i = 0; i < 2; i++) { now += 1000; system.tick() }
  expect(one.lastAdvisories).toContain('NAV1 FAILED')
  expect(two.lastAdvisories).toContain('NAV1 FAILED')
})

test('C3: the computer applies E-2 configuration and inhibits: no ADF CONTROL LOST above 20 degrees of roll, in the polar area or unconfigured; E-21 still raised', () => {
  const raised = (fms: ScriptedFms) => fms.recallList.map(message => message.text)
  const banked = standalone()
  banked.step(2)
  banked.fms.setAircraft({ bank: 25 })
  banked.fms.setRadioFaults('adf', { receiver: 'FAILED' })
  banked.step(1)
  expect(raised(banked.fms)).not.toContain('ADF1 CONTROL LOST')
  expect(banked.fms.lastAdvisories).toContain('ADF1 FAILED')
  const level = standalone()
  level.step(2)
  level.fms.setRadioFaults('adf', { receiver: 'FAILED' })
  level.step(1)
  expect(raised(level.fms)).toContain('ADF1 CONTROL LOST')
  let now = T0
  const options = { ...HELICOPTER_PROFILE.configuration.options, adfControlLostAlert: { ...HELICOPTER_PROFILE.configuration.options.adfControlLostAlert, configured: false } }
  const unconfigured = new ScriptedFms(() => new Date(now), { profile: { ...HELICOPTER_PROFILE, configuration: { ...HELICOPTER_PROFILE.configuration, options } } })
  now += 2000; unconfigured.updateNavigation(2)
  unconfigured.setRadioFaults('adf', { receiver: 'FAILED' })
  now += 1000; unconfigured.updateNavigation(1)
  expect(raised(unconfigured)).not.toContain('ADF1 CONTROL LOST')
  expect(unconfigured.lastAdvisories).toContain('ADF1 FAILED')
})

test('C3: a healthy ADF tuned to nothing in range shows no bearing and raises no fault message', () => {
  const { fms, step } = standalone()
  fms.setRadio('adf', '1750')
  step(3)
  expect(fms.adfBearing('adf')).toBeNull()
  expect(fms.recallList.map(message => message.text).filter(text => /ADF/.test(text))).toEqual([])
  expect(fms.lastAdvisories.filter(text => /ADF/.test(text))).toEqual([])
})

test('C3: each radio message row names the Appendix E page the shared catalogue gives for its message', () => {
  for (const row of RADIO_MESSAGE_ROWS) for (const device of row.devices) {
    const text = row.text(RADIO_NAMES[device])
    const source = APPENDIX_E[text]
    // Alerts are in the catalogue; the FAILED maintenance advisories are raised outside the alert library.
    if (row.kind === 'alert') expect(source, text).toEqual({ page: row.row })
    else if (source) expect(source, text).toEqual({ page: row.row })
  }
  // The command-timeout rows: NAV E-13, ADF E-2.
  expect(APPENDIX_E['NAV1 CONTROL LOST']).toEqual({ page: 'E-13' })
  expect(APPENDIX_E['ADF2 CONTROL LOST']).toEqual({ page: 'E-2' })
})
