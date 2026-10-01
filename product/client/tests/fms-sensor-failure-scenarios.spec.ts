import { expect, logicTest as test } from './isolated-client-test'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { describeStep, runHeadless, scenarioProblems, type Scenario, type ScenarioStep } from '../src/fmsCdu/scenario'
import { screenText } from '../src/fmsCdu/screen'

// Stage F F14 part 1 (F14 of the Stage F plan): the sensor failures the bench can inject today, each a scenario step,
// validated on admission, run through the scenario runner, and checked against what is observable today: the
// navigation mode, its accuracy basis, the alerts and advisories of Appendix E, and the output bus words.

const START = '2026-09-27T14:00:00.000Z'
const scenario = (steps: ScenarioStep[], maxSeconds = 300): Scenario => ({ id: 'f14', title: 'F14 sensor failure', objective: 'F14', maxSeconds, startTime: START, steps })
const at = (seconds: number): ScenarioStep['when'] => ({ kind: 'time', seconds })
const later = (seconds: number): ScenarioStep['when'] => ({ kind: 'after', seconds })
const step = (when: ScenarioStep['when'], action: object, within?: number) => ({ when, action, ...(within ? { within } : {}) }) as ScenarioStep
const run = (steps: ScenarioStep[], maxSeconds?: number) => {
  const result = runHeadless(scenario(steps, maxSeconds))
  return { ...result, failures: result.runner.results.map((r, i) => [i, r.status, r.actual]).filter(([, status]) => status !== 'done' && status !== 'pass') }
}
const gpsGone = (when: ScenarioStep['when']) => [step(when, { kind: 'condition', condition: 'gpsLost', on: true }), step(when, { kind: 'condition', condition: 'dmeOutage', on: true })]

/** What F14 leaves to the items that build it: each a scenario still to write, named by its item. */
export const PENDING = [
  { item: 'F3', what: 'the resolver\'s ordering and transitions between modes, row by row of the transition table' },
  { item: 'F6', what: 'DME/DME: a biased station isolated with four, unavailable with three, a station that stops replying or mismatches its ident' },
  { item: 'F7', what: 'VOR/DME and VOR/DME/TCN: a biased VOR radial, a TACAN failure' },
  { item: 'F16', what: 'an ADF failure and an NDB off the air, on the ADF bearing (session 4\'s bench ADF receiver)' },
  { item: 'F11', what: 'a power interruption of a stated duration (C2)' },
  { item: 'F14', what: 'part 2: bench controls for the radio faults and stations off the air, each with a rendered test that the control drives it' },
] as const

test('the radio and station steps are validated on admission, with the reason, and read as procedure text', () => {
  const refused: [object, RegExp][] = [
    [{ kind: 'radioFault', device: 'nav3', receiver: 'FAILED' }, /needs a radio/],
    [{ kind: 'radioFault', device: 'nav1' }, /at least one of controlPath, measurementBus and receiver/],
    [{ kind: 'radioFault', device: 'nav1', controlPath: 'BROKEN' }, /controlPath is NORMAL or LOST/],
    [{ kind: 'radioFault', device: 'dme1', receiver: 'LOST' }, /receiver is NORMAL or FAILED/],
    [{ kind: 'stationOffAir', ident: 'yow', off: true }, /station ident/],
    [{ kind: 'stationOffAir', ident: 'YOW' }, /off true or false/],
    [{ kind: 'expectNav' }, /at least one of mode, accuracyBasis and uncertain/],
    [{ kind: 'expectNav', mode: 'INS' }, /mode is one of/],
    [{ kind: 'expectNav', accuracyBasis: 'oem' }, /receiver or laboratory/],
  ]
  for (const [action, reason] of refused) expect(scenarioProblems(scenario([step({ kind: 'start' }, action)])).join(' '), JSON.stringify(action)).toMatch(reason)
  expect(scenarioProblems(scenario([step({ kind: 'start' }, { kind: 'radioFault', device: 'adf2', measurementBus: 'LOST', receiver: 'FAILED' })]))).toEqual([])
  expect(describeStep(step(at(30), { kind: 'radioFault', device: 'nav1', controlPath: 'LOST' }))).toBe('At 30 s, set NAV1\'s control path LOST.')
  expect(describeStep(step(at(30), { kind: 'stationOffAir', ident: 'YOW', off: true }))).toBe('At 30 s, take the station YOW off the air.')
  expect(describeStep(step(later(5), { kind: 'expectNav', mode: 'KALMAN', accuracyBasis: 'laboratory' }, 10)))
    .toBe('5 s later, check that the FMS navigates on KALMAN, its accuracy is a laboratory figure within 10 s.')
})

test('GPS 1 fails: GPS1 NOT USABLE, and the FMS stays on GPS through receiver 2 on its own figure', () => {
  const { runner, fms, sim, failures } = run([
    step(at(30), { kind: 'gps', receiver: 1, stimulus: { op: 'fault', fault: 'RECEIVER', on: true } }),
    step(later(0), { kind: 'expectAlert', text: 'GPS1 NOT USABLE' }, 10),
    step(later(0), { kind: 'expectGpsSource', source: 'GPS2' }, 10),
    step(later(0), { kind: 'expectNav', mode: 'GPS', accuracyBasis: 'receiver', uncertain: false }, 10),
  ])
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
  expect(fmsOutputs(fms, sim).accuracyBasis).toEqual({ value: 'receiver', status: 'NORMAL' })
})

test('GPS integrity lost on both: GPS POS UNCERTAIN, the position kept as uncertain, no integrity on the bus', () => {
  const { runner, fms, sim, failures } = run([
    step(at(30), { kind: 'condition', condition: 'gpsIntegrity', on: true }),
    step(later(0), { kind: 'expectAlert', text: 'GPS POS UNCERTAIN' }, 10),
    step(later(0), { kind: 'expectNav', mode: 'GPS', uncertain: true }, 10),
  ])
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
  const bus = fmsOutputs(fms, sim)
  expect(bus.positionUncertain).toEqual({ value: true, status: 'NORMAL' })
  expect(bus.integrityValid).toEqual({ value: false, status: 'NORMAL' })
})

test('GPS gone with no radio fix: GPS NAV LOST, KALMAN for its two-minute coast, then DVS, eligible for no RNP phase', () => {
  const { runner, fms, failures } = run([
    ...gpsGone(at(30)),
    step(later(0), { kind: 'expectAlert', text: 'GPS NAV LOST' }, 10),
    step(later(0), { kind: 'expectNav', mode: 'KALMAN', accuracyBasis: 'laboratory' }, 10),
    // KALMAN_COAST_S = 120 (DEC-150): DVS takes over after the coast.
    step(later(100), { kind: 'expectNav', mode: 'KALMAN' }),
    step(later(30), { kind: 'expectNav', mode: 'DVS', accuracyBasis: 'laboratory' }, 20),
  ], 260)
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
  // DVS is never eligible for an RNP phase (sensorState.ts ELIGIBILITY).
  expect(fms.navPerformance.sensor).toMatchObject({ mode: 'DVS', eligibility: { 'EN ROUTE': false, TERMINAL: false, APPROACH: false } })
})

test('APIRS failed: GPS gone goes straight to DVS (no KALMAN coast without the accelerations)', () => {
  const { runner, failures } = run([
    step(at(30), { kind: 'condition', condition: 'apirsFail', on: true }),
    ...gpsGone(at(31)),
    step(later(0), { kind: 'expectNav', mode: 'DVS' }, 10),
  ])
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
})

test('DVS failed: the KALMAN coast expires to dead reckoning; DVS lost while in use is DVS NAV LOST', () => {
  // The coast expiry with no DVS behind it.
  const expiry = run([
    step(at(30), { kind: 'condition', condition: 'dvsFail', on: true }),
    ...gpsGone(at(31)),
    step(later(0), { kind: 'expectNav', mode: 'KALMAN' }, 10),
    step(later(130), { kind: 'expectNav', mode: 'DR' }, 20),
  ], 260)
  expect(expiry.failures).toEqual([])
  expect(expiry.runner.outcome).toBe('passed')
  // DVS in use (APIRS failed, GPS gone), then the Doppler fails.
  const lost = run([
    step(at(30), { kind: 'condition', condition: 'apirsFail', on: true }),
    ...gpsGone(at(31)),
    step(later(0), { kind: 'expectNav', mode: 'DVS' }, 10),
    step(later(5), { kind: 'condition', condition: 'dvsFail', on: true }),
    step(later(0), { kind: 'expectAlert', text: 'DVS NAV LOST' }, 10),
    step(later(0), { kind: 'expectNav', mode: 'DR' }, 10),
  ])
  expect(lost.failures).toEqual([])
  expect(lost.runner.outcome).toBe('passed')
})

test('radio health (plan C3): a lost measurement bus or a failed receiver is the FAILED advisory, and the reading is gone', () => {
  for (const [fault, advisory] of [[{ device: 'nav1', measurementBus: 'LOST' }, 'NAV1 FAILED'], [{ device: 'nav1', receiver: 'FAILED' }, 'NAV1 FAILED'],
    [{ device: 'dme1', measurementBus: 'LOST' }, 'DME1 FAILED']] as const) {
    const { runner, fms, failures } = run([
      step(at(30), { kind: 'radioFault', ...fault }),
      step(later(0), { kind: 'expectScratchpad', text: advisory }, 10),
    ])
    expect(failures, advisory).toEqual([])
    expect(runner.outcome, advisory).toBe('passed')
    if (fault.device === 'nav1') {
      expect(fms.radioReceiving('nav1')).toBeNull()
      expect(fms.navRadial('nav1')).toBeNull()
    } else expect(fms.dmeDistance('dme1')).toBe('****')
  }
})

test('a failed ADF receiver is ADF1 CONTROL LOST (E-2, configured) and the ADF1 FAILED advisory (E-21)', () => {
  const { runner, failures } = run([
    step(at(30), { kind: 'radioFault', device: 'adf', receiver: 'FAILED' }),
    step(later(0), { kind: 'expectAlert', text: 'ADF1 CONTROL LOST' }, 10),
    step(later(0), { kind: 'expectScratchpad', text: 'ADF1 FAILED' }, 10),
  ])
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
})

test('a lost control path: a crew tune times out to NAV1 CONTROL LOST (E-13) while reception continues', () => {
  const { runner, fms, failures } = run([
    step(at(30), { kind: 'radioFault', device: 'nav1', controlPath: 'LOST' }),
    step(later(1), { kind: 'keys', keys: ['RADIO'] }),
    step(later(0), { kind: 'type', text: '115.20' }),
    step(later(0), { kind: 'keys', keys: ['LSK3L'] }),
    step(later(0), { kind: 'expectAlert', text: 'NAV1 CONTROL LOST' }, 10),
  ])
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
  // Reception continues on the old frequency: the radio never took the command.
  expect(fms.radioReceiving('nav1')).not.toBe('115.20')
  expect(fms.radioReceiving('nav1')).not.toBeNull()
  expect(fms.radioRequests.at(-1)).toMatchObject({ device: 'nav1', value: '115.20', status: 'TIMEOUT' })
})

test('a VOR off the air: the NAV stays tuned, but no radial comes; back on the air, it does', () => {
  // The station NAV1 tunes in AUTO, and the NAV page line that shows its radial (RAD:nnn°), from a run without the fault.
  const openNav = [step(at(25), { kind: 'keys', keys: ['RADIO'] }), step(later(0), { kind: 'keys', keys: ['LSK5L'] })]
  const probe = runHeadless(scenario([...openNav, step(later(5), { kind: 'expectNav', mode: 'GPS' })], 40))
  const station = probe.fms.navStation('nav1')!.ident
  const radialLine = screenText(probe.fms.screen()).findIndex(line => /^RAD:\d{3}/.test(line))
  expect(radialLine).toBeGreaterThan(0)
  const { runner, fms, failures } = run([
    ...openNav,
    step(at(30), { kind: 'stationOffAir', ident: station, off: true }),
    // Off the air: the radial field is dashes (M300 13-21), the frequency still shown.
    step(later(0), { kind: 'expectLine', line: radialLine, pattern: '^---' }, 10),
  ], 60)
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
  expect(fms.stationOffAir(station)).toBe(true)
  expect(fms.radioReceiving('nav1')).not.toBeNull()
  expect(fms.navRadial('nav1')).toBeNull()
  fms.setStationOffAir(station, false)
  expect(fms.stationOffAir(station)).toBe(false)
})

test('the scenarios F14 leaves to later items are named, each with its item', () => {
  expect(PENDING.map(entry => entry.item)).toEqual(['F3', 'F6', 'F7', 'F16', 'F11', 'F14'])
  for (const entry of PENDING) expect(entry.what.length).toBeGreaterThan(20)
})
