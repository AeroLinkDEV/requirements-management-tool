import { expect, logicTest as test } from './isolated-client-test'
import { fmsOutputs } from '../src/fmsCdu/efis'
import { describeStep, runHeadless, scenarioProblems, ScenarioRunner, ScenarioRecorder, reportMarkdown, parseScenario, type Scenario, type ScenarioStep } from '../src/fmsCdu/scenario'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { rangeObservations } from '../src/fmsCdu/radioNavigation'
import { readFileSync } from 'node:fs'
import { screenText } from '../src/fmsCdu/screen'

// Stage F F14: laboratory sensor/world failures, each a scenario step,
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

test('the radio and station steps are validated on admission, with the reason, and read as procedure text', () => {
  const refused: [object, RegExp][] = [
    [{ kind: 'radioFault', device: 'nav3', receiver: 'FAILED' }, /needs a radio/],
    [{ kind: 'radioFault', device: 'nav1' }, /at least one of controlPath, measurementBus and receiver/],
    [{ kind: 'radioFault', device: 'nav1', controlPath: 'BROKEN' }, /controlPath is NORMAL or LOST/],
    [{ kind: 'radioFault', device: 'dme1', receiver: 'LOST' }, /receiver is NORMAL, FAILED or SILENT/],
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
    step(later(1), { kind: 'stationOffAir', ident: station, off: false }),
    step(later(0), { kind: 'expectLine', line: radialLine, pattern: '^RAD:\\d{3}' }, 5),
  ], 60)
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
  expect(fms.radioReceiving('nav1')).not.toBeNull()
  expect(fms.navRadial('nav1')).not.toBeNull()
})

// Primary F14 dispatcher owner. Fixed-clock, frozen plant: input words change; physical aircraft does not.
const frozen = () => {
  let now = Date.parse(START)
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  fms.setAircraft({ heading: 90, track: 90, groundSpeed: 100, tas: 100 })
  const tick = () => { now += 250; fms.updateNavigation(0.25) }
  for (let i = 0; i < 360; i++) tick()
  const replay = (actions: ScenarioStep['action'][], mode = 'GPS') => {
    const recording = new ScenarioRecorder(() => new Date(now))
    for (const action of actions) recording.steps.push(step({ kind: 'start' }, action))
    const declared = parseScenario(JSON.stringify(scenario([...recording.steps, step(at(2), { kind: 'expectNav', mode })], 3)))
    const runner = new ScenarioRunner(declared, fms, undefined, sim)
    for (let i = 0; i < 12 && !runner.finished; i++) { tick(); runner.poll() }
    expect(runner.outcome, reportMarkdown(runner)).toBe('passed')
    return runner
  }
  return { fms, sim, replay, tick, clock: () => now }
}

test('F14 radio replay applies FAIL, silent and recovery to every NAV/DME/TACAN receiver, with time and value in its report', () => {
  for (const device of ['nav1', 'nav2', 'dme1', 'dme2', 'tacan'] as const) {
    const lab = frozen()
    expect(fmsOutputs(lab.fms, lab.sim).radios[device].activeFrequency.status, device).toBe('NORMAL')
    for (const receiver of ['SILENT', 'FAILED', 'NORMAL'] as const) {
      const runner = lab.replay([{ kind: 'radioFault', device, receiver }])
      const word = fmsOutputs(lab.fms, lab.sim).radios[device].activeFrequency
      expect(word.status, `${device} ${receiver}`).toBe(receiver === 'FAILED' ? 'FAIL' : receiver === 'SILENT' ? 'NCD' : 'NORMAL')
      if (receiver === 'SILENT') expect(screenText(lab.fms.screen()).join('\n')).not.toContain(`${device.toUpperCase()} FAILED`)
      expect(runner.results[0]).toMatchObject({ status: 'done', at: 0, actual: `set ${device.toUpperCase()}'s receiver ${receiver}` })
      expect(reportMarkdown(runner)).toContain(`| 0 s | DONE | set ${device.toUpperCase()}'s receiver ${receiver}`)
    }
  }
})

test('F14 station replay refuses unknown/non-radio targets instead of silently recording an unapplied outage', () => {
  for (const ident of ['ZZZZ', 'KBTV']) {
    const lab = frozen()
    const runner = new ScenarioRunner(scenario([step({ kind: 'start' }, { kind: 'stationOffAir', ident, off: true })]), lab.fms)
    expect(runner.outcome).toBe('error')
    expect(runner.results[0].actual).toContain('requires one unambiguous non-NDB facility')
  }
})

test('F14 DME no-reply and mismatched ident affect only the range; shared-world replay restores it on independent FMS', () => {
  let now = Date.parse(START)
  const dual = new DualFmsSystem(() => new Date(now))
  dual.computers[0].setCondition('independent', true)
  const tick = () => { now += 250; dual.tick(); dual.computers.forEach(unit => unit.updateNavigation(0.25)) }
  for (let i = 0; i < 120; i++) tick()
  const station = dual.computers[0].dmeStation('dme1')!
  expect(station).toBeDefined()
  const before = dual.computers.map((unit, index) => fmsOutputs(unit, dual.flights[index]).radioMeasurements)
  before.forEach(words => { expect(words.dme1.dmeDistance.status).toBe('NORMAL'); expect(words.nav1.vorRadial.status).toBe('NORMAL') })
  const apply = (action: ScenarioStep['action']) => {
    const runner = new ScenarioRunner(scenario([step({ kind: 'start' }, action), step(at(1), { kind: 'expectNav', mode: 'GPS' })], 2), dual.computers[1])
    for (let i = 0; i < 8 && !runner.finished; i++) { tick(); runner.poll() }
    expect(runner.outcome, reportMarkdown(runner)).toBe('passed')
  }
  for (const action of [
    { kind: 'stationFault', ident: station.ident, component: 'DME', reply: false },
    { kind: 'stationFault', ident: station.ident, component: 'DME', reply: true, reportedIdent: 'BAD' },
  ] as const) {
    apply(action)
    dual.computers.forEach((unit, index) => {
      const words = fmsOutputs(unit, dual.flights[index]).radioMeasurements
      expect(words.dme1.dmeDistance).toEqual({ status: 'NCD', value: null })
      expect(words.nav1.vorRadial.status).toBe('NORMAL')
      // WMM decimal-year interpolation advances with the clock; <1e-6 degree is not a station fault.
      expect(words.nav1.vorRadial.value!).toBeCloseTo(before[index].nav1.vorRadial.value!, 6)
      if ('reportedIdent' in action) {
        expect(words.dme1.stationIdent).toEqual({ status: 'NORMAL', value: 'BAD' })
        const ranges = rangeObservations(unit.radioObservations(), unit.altitude, now)
        expect(ranges.usable.some(range => range.observation.station.ident === station.ident)).toBe(false)
        expect(ranges.rejected).toContainEqual({ ident: station.ident, reason: 'DME ident missing or mismatched' })
      }
    })
  }
  apply({ kind: 'stationFault', ident: station.ident, component: 'DME', reply: true, reportedIdent: null })
  dual.computers.forEach((unit, index) => expect(fmsOutputs(unit, dual.flights[index]).radioMeasurements.dme1.dmeDistance).toEqual(before[index].dme1.dmeDistance))
})

test('F14 VOR radial bias changes the measured bearing by the stated 10 degrees while DME and physical heading stay fixed', () => {
  const lab = frozen()
  const station = lab.fms.navStation('nav1')!
  const before = fmsOutputs(lab.fms, lab.sim).radioMeasurements
  expect(before.nav1.vorRadial.status).toBe('NORMAL')
  expect(before.dme1.dmeDistance.status).toBe('NORMAL')
  lab.replay([{ kind: 'stationFault', ident: station.ident, component: 'VOR', biasDeg: 10 }])
  const after = fmsOutputs(lab.fms, lab.sim).radioMeasurements
  expect((after.nav1.vorRadial.value! - before.nav1.vorRadial.value! + 360) % 360).toBeCloseTo(10, 8)
  expect(after.dme1.dmeDistance).toEqual(before.dme1.dmeDistance)
  expect(lab.fms.heading).toBe(90)
  lab.replay([{ kind: 'stationFault', ident: station.ident, component: 'VOR', biasDeg: 0 }])
  expect(fmsOutputs(lab.fms, lab.sim).radioMeasurements.nav1.vorRadial.value!).toBeCloseTo(before.nav1.vorRadial.value!, 6)
})

test('F14 measured air replay biases heading without moving truth, and heading invalid withdraws healthy DVS', () => {
  const lab = frozen()
  const physical = lab.fms.truePosition
  lab.replay([{ kind: 'airInput', headingBiasDeg: 30, tasValid: false }])
  expect(lab.fms.navigationInputs!.air.value).toMatchObject({ headingTrue: 120, headingValid: true, tasValid: false, tasKt: 100 })
  expect(lab.fms.navState.airValid).toBe(false)
  expect(lab.fms.heading).toBe(90)
  expect(lab.fms.truePosition).toEqual(physical)
  const latitude = lab.fms.position.lat
  lab.replay([{ kind: 'condition', condition: 'gpsLost', on: true }, { kind: 'condition', condition: 'dmeOutage', on: true }, { kind: 'condition', condition: 'apirsFail', on: true }], 'DVS')
  // 100 kt east in body axes, rotated by the measured +30° bias: north velocity -50 kt. 2 s runner window.
  expect((lab.fms.position.lat - latitude) * 60 * 3600 / 2).toBeCloseTo(-50, 0)
  expect(lab.fms.navigationInputs!.dvs!.status).toBe('NORMAL')
  lab.replay([{ kind: 'airInput', headingValid: false }], 'DR')
  expect(lab.fms.sensorSolutions.some(solution => solution.mode === 'DVS' && solution.available)).toBe(false)
  expect(lab.fms.navigationInputs!.air.value!.altitudeFt).toBeGreaterThan(0)
  lab.replay([{ kind: 'airInput', headingValid: true, tasValid: true, headingBiasDeg: 0 }], 'DVS')
  expect(lab.fms.navState.airValid).toBe(true)
})

test('F14 power duration replay obeys the independent C2 50ms boundary and records exact duration', () => {
  for (const durationMs of [49, 50, 51]) {
    const lab = frozen()
    const runner = lab.replay([{ kind: 'powerInterrupt', durationMs }, { kind: 'condition', condition: 'gpsLost', on: true }, { kind: 'condition', condition: 'dmeOutage', on: true }], durationMs > 50 ? 'DVS' : 'KALMAN')
    expect(reportMarkdown(runner)).toContain(`for ${durationMs} ms (C2 laboratory rule)`)
  }
})

test('F14 GPS pair integrity-only retains both positions and accuracy; position-gone removes both positions, independently of HIL', () => {
  const lab = frozen()
  for (const mode of ['INTEGRITY_ONLY', 'NORMAL', 'POSITION_GONE', 'NORMAL'] as const) {
    // Frozen demonstration site has fewer than three usable facilities: the existing VOR/DME fallback.
    lab.replay([{ kind: 'gpsPair', mode }], mode === 'POSITION_GONE' ? 'VOR/DME' : 'GPS')
    for (const word of lab.fms.navigationInputs!.gps) {
      expect(word.value!['110'].ssm).toBe(mode === 'POSITION_GONE' ? 'NCD' : 'NORMAL')
      expect(word.value!['130'].ssm).toBe(mode === 'INTEGRITY_ONLY' ? 'NCD' : 'NORMAL')
      expect(word.value!['247'].ssm).toBe('NORMAL')
    }
    if (mode === 'INTEGRITY_ONLY') expect(fmsOutputs(lab.fms, lab.sim).integrityValid).toEqual({ status: 'NORMAL', value: false })
  }
})

test('F14 new action admission fails closed on malformed values and default-profile external radio head', () => {
  const cases = [
    [{ kind: 'airInput' }, /needs tasValid/], [{ kind: 'airInput', headingBiasDeg: 181 }, /within/], [{ kind: 'airInput', tasValid: 'false' }, /validity/],
    [{ kind: 'stationFault', ident: 'BTV', component: 'DME', reply: 'false' }, /reply/], [{ kind: 'stationFault', ident: 'BTV', component: 'VOR', biasDeg: null }, /biasDeg/],
    [{ kind: 'stationFault', ident: 'BTV', component: 'NDB', biasDeg: 3 }, /component/], [{ kind: 'powerInterrupt', durationMs: -1 }, /durationMs/],
    [{ kind: 'gpsPair', mode: 'MAYBE' }, /mode/], [{ kind: 'externalRadioHead', on: true }, /not equipped.*DEC-150/],
    [{ kind: 'radioFault', device: 'constructor', receiver: 'FAILED' }, /needs a radio/],
    [{ kind: 'radioFault', device: 'nav1', receiver: ['NORMAL'] }, /receiver/], [{ kind: 'gpsPair', mode: ['NORMAL'] }, /mode/],
    [{ kind: 'stationFault', ident: 'YOW', component: 'VOR', biasDeg: 10, reply: false }, /unsupported fields/],
  ] as const
  for (const [action, reason] of cases) expect(() => parseScenario(JSON.stringify(scenario([step({ kind: 'start' }, action)]))), JSON.stringify(action)).toThrow(reason)
})

test('F14 a silent NAV cannot acknowledge a crew tune or self-test, and remains distinct from a FAILED receiver', () => {
  const { fms, runner, failures } = run([
    step(at(20), { kind: 'radioFault', device: 'nav1', receiver: 'SILENT' }),
    step(later(0), { kind: 'keys', keys: ['RADIO'] }), step(later(0), { kind: 'type', text: '115.20' }),
    step(later(0), { kind: 'keys', keys: ['LSK3L'] }), step(later(0), { kind: 'expectAlert', text: 'NAV1 CONTROL LOST' }, 10),
    step(later(0), { kind: 'keys', keys: ['LSK5L', 'NEXT', 'LSK3L', 'LSK3L'] }),
    step(later(4), { kind: 'expectNoAlert', text: 'NAV1 FAILED' }),
  ], 40)
  expect(failures).toEqual([])
  expect(runner.outcome).toBe('passed')
  expect(fms.radioRequests.some(request => request.device === 'nav1' && request.value === '115.20' && request.status === 'TIMEOUT')).toBe(true)
  expect(fms.radioPort!.testState('nav1')).toBe('TIMEOUT')
})

test('F14 active ADF failure is a scenario effect on the RMI bus and recovery restores the same raw bearing', () => {
  const lab = frozen()
  lab.replay([{ kind: 'keys', keys: ['RADIO', 'NEXT'] }, { kind: 'type', text: '236' }, { kind: 'keys', keys: ['LSK1L'] }])
  const before = fmsOutputs(lab.fms, lab.sim).radioMeasurements.adf.adfBearing
  expect(before.status).toBe('NORMAL')
  lab.replay([{ kind: 'radioFault', device: 'adf', receiver: 'FAILED' }])
  expect(fmsOutputs(lab.fms, lab.sim).radioMeasurements.adf.adfBearing).toEqual({ status: 'FAIL', value: null })
  lab.replay([{ kind: 'radioFault', device: 'adf', receiver: 'NORMAL' }])
  expect(fmsOutputs(lab.fms, lab.sim).radioMeasurements.adf.adfBearing).toEqual(before)
})

test('F14 DME world faults resolve the unique CIFP component and never silence its same-ident NDB; ambiguous targets error', () => {
  const fixture = readFileSync('tests/fixtures/cifp/pasd-2609.pc', 'latin1')
  const lab = frozen()
  expect(lab.fms.loadArinc424(fixture, 'pasd-2609.pc')).toMatchObject({ loaded: 'CIFP2609' }); lab.fms.swapCycles()
  lab.fms.setAircraft({ position: { lat: 55 + 18 / 60 + 56.4 / 3600 - 0.02, lon: -(160 + 31 / 60 + 6.22 / 3600) }, heading: 90 })
  lab.fms.setRadio('adf', '0390'); lab.fms.setRadio('nav1', '113.20')
  for (let i = 0; i < 40; i++) lab.tick()
  const bearing = fmsOutputs(lab.fms, lab.sim).radioMeasurements.adf.adfBearing
  expect(bearing).toEqual({ status: 'NORMAL', value: 270 }) // literal CIFP NDB directly north; aircraft heading east.
  expect(lab.fms.dmeSlantRangeNm('dme1')).not.toBeNull()
  const runner = new ScenarioRunner(scenario([step({ kind: 'start' }, { kind: 'stationFault', ident: 'HBT', component: 'DME', reply: false })]), lab.fms)
  expect(runner.results[0].status).toBe('done')
  lab.tick()
  expect(lab.fms.dmeSlantRangeNm('dme1')).toBeNull()
  expect(fmsOutputs(lab.fms, lab.sim).radioMeasurements.adf.adfBearing).toEqual(bearing)
  const dme = fixture.split('\n').find(line => line.startsWith('SCAND ') && line.includes('HBT'))!
  const duplicate = `${fixture}\n${dme.replace('N55185695', 'N56185695')}`
  expect(lab.fms.loadArinc424(duplicate, 'ambiguous.pc')).toMatchObject({ loaded: 'CIFP2609' }); lab.fms.swapCycles()
  for (const action of [
    { kind: 'stationFault', ident: 'HBT', component: 'DME', reply: false },
    { kind: 'stationFault', ident: 'HBT', component: 'VOR', biasDeg: 10 },
    { kind: 'stationFault', ident: 'PASD', component: 'DME', reply: false },
  ] as const) {
    const refused = new ScenarioRunner(scenario([step({ kind: 'start' }, action)]), lab.fms)
    expect(refused.outcome).toBe('error')
    expect(refused.results[0].actual).toContain('requires one unambiguous compatible facility')
  }
})
