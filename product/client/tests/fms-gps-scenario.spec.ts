import { expect, logicTest as test } from './isolated-client-test'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { singleComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { GPS_MODEL_VERSION } from '../src/fmsCdu/gps'
import { stimulusFor } from '../src/fmsCdu/gpsStimulus'
import {
  ScenarioRecorder, ScenarioRunner, TICK_SECONDS, parseScenario, procedureText, reportMarkdown, runHeadless, scenarioProblems,
  scenarioStart, type Scenario, type ScenarioStep,
} from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { profileById } from '../src/fmsCdu/profile'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'

// GPS faults scripted and replayed in scenarios. A `gps` step applies one stimulus to receiver 1 or 2 through the
// same stimulus record as the GPS sensors tab (gpsStimulus.ts); expectGpsSource, expectApproachLevel and
// expectReceiverMode check what the FMS and the receiver did. The GPS sky moves with the clock, so a scenario can pin
// its start time; with the model version, the constellation seed and the receiver seeds it fixes the GPS timeline,
// and the report names all of them.
const START = '2026-09-27T14:00:00.000Z'
const library = (id: string) => SCENARIO_LIBRARY.find(entry => entry.id === id)!
const scenarioOf = (steps: ScenarioStep[], maxSeconds = 60): Scenario => ({ id: 'gps-test', title: 'GPS test', objective: '', maxSeconds, startTime: START, steps })
const gps = (receiver: number, stimulus: object, when: ScenarioStep['when'] = { kind: 'start' }) =>
  ({ when, action: { kind: 'gps', receiver, stimulus } }) as unknown as ScenarioStep

test('a GPS step outside the receivers\' domain is refused at import, with the reason', () => {
  const refused: [object, RegExp][] = [
    [gps(3, { op: 'jam', db: 10 }), /receiver 1 or 2, not 3/],
    [gps(0, { op: 'jam', db: 10 }), /receiver 1 or 2, not 0/],
    [gps(1, { op: 'jam', db: 61 }), /jam needs db from 0 to 60/],
    [gps(1, { op: 'jam', db: Number.NaN }), /jam needs db from 0 to 60/],
    [gps(1, { op: 'mask', prns: [4, 33] }), /mask needs a list of PRNs from 1 to 32/],
    [gps(1, { op: 'satelliteFault', prn: 0, fault: 'RAMP', value: 5 }), /PRN from 1 to 32/],
    [gps(1, { op: 'satelliteFault', prn: 24, fault: 'DRIFT', value: 5 }), /fault RAMP or STEP/],
    [gps(1, { op: 'satelliteFault', prn: 24, fault: 'STEP', value: 1001 }), /value from -1000 to 1000/],
    [gps(1, { op: 'sbas', outage: [130] }), /geostationary PRNs \(131, 133\)/],
    [gps(1, { op: 'sbas', ionoStorm: 0 }), /ionoStorm must be from 1 to 100/],
    [gps(1, { op: 'sbas' }), /sbas needs doNotUse, outage or ionoStorm/],
    [gps(1, { op: 'fault', fault: 'ANTENNA', on: true }), /RECEIVER, RF_INPUT or STOP_TRANSMITTING/],
    [gps(1, { op: 'spoof', northM: 200_000, driftEastMps: 0 }), /northM within ±100000/],
    [gps(1, { op: 'override', label: '999', kind: 'FORCE', amount: 1 }), /numeric label/],
    [gps(1, { op: 'override', label: '273', kind: 'FORCE', amount: 1 }), /numeric label/],
    [gps(1, { op: 'override', label: '130', kind: 'SCALE', amount: 1 }), /kind FORCE, FREEZE, BIAS or RAMP/],
    [gps(1, { op: 'override', label: '130', kind: 'BIAS' }), /BIAS needs a finite amount/],
    [gps(1, { op: 'override', label: '130', kind: 'BIAS', amount: 1, ssm: 'NCD' }), /only with FORCE/],
    [gps(1, { op: 'override', label: '130', kind: 'FORCE', amount: 1, ssm: 'BROKEN' }), /NORMAL, NCD, FT or FW/],
    [gps(1, { op: 'clearOverride', label: '999' }), /clearOverride needs a numeric label/],
    [gps(1, { op: 'status', label: '273', patch: { mode: 'WARP' } }), /not valid for 273/],
    [gps(1, { op: 'status', label: '273', patch: { colour: 'red' } }), /not valid for 273/],
    [gps(1, { op: 'status', label: '999', patch: {} }), /status label/],
    [gps(2, { op: 'teleport' }), /unsupported GPS operation "teleport"/],
    [{ when: { kind: 'start' }, action: { kind: 'expectGpsSource', source: 'GPS3' } }, /GPS1, GPS2 or NONE/],
    [{ when: { kind: 'start' }, action: { kind: 'expectApproachLevel', level: 'LP' } }, /LPV, LNAV\/VNAV, LNAV or NO APPR/],
    [{ when: { kind: 'start' }, action: { kind: 'expectReceiverMode', receiver: 3, mode: 'NAV' } }, /receiver 1 or 2 and a mode/],
    [{ when: { kind: 'start' }, action: { kind: 'expectReceiverMode', receiver: 1, mode: 'SLEEPING' } }, /receiver 1 or 2 and a mode/],
  ]
  for (const [step, reason] of refused) {
    const scenario = scenarioOf([step as ScenarioStep])
    expect(scenarioProblems(scenario).join('; '), JSON.stringify(step)).toMatch(reason)
    expect(() => parseScenario(JSON.stringify(scenario)), JSON.stringify(step)).toThrow(reason)
  }
  expect(scenarioProblems({ ...scenarioOf([]), startTime: 'tomorrow' })).toContain('startTime must be an ISO 8601 date and time')
  // The same kinds, in their domain, are accepted.
  const accepted = scenarioOf([
    gps(1, { op: 'jam', db: 60 }), gps(2, { op: 'mask', prns: [] }), gps(1, { op: 'sbas', outage: [131, 133] }),
    gps(1, { op: 'override', label: '130', kind: 'FORCE', amount: 0.5, ssm: 'NCD' }), gps(1, { op: 'override', label: '076', kind: 'FREEZE' }),
    gps(2, { op: 'status', label: '273', patch: { mode: 'NAV', integrity: 'DETECTED' } }), gps(2, { op: 'clearStatus', label: '273' }),
    { when: { kind: 'start' }, action: { kind: 'expectReceiverMode', receiver: 2, mode: 'SBAS_NAV' } },
  ])
  expect(scenarioProblems(accepted)).toEqual([])
})

test('a GPS step acts through the stimulus record the GPS sensors tab shows, on the receiver it names', () => {
  const { runner, fms } = runHeadless(scenarioOf([
    // 6 dB: enough to tell the receivers apart, with margin above the tracking threshold for the lowest satellite used
    // (PRN 24 at 24 degrees), so the fault on it is detected whatever small attitude changes the flight makes.
    gps(2, { op: 'jam', db: 6 }),
    gps(2, { op: 'satelliteFault', prn: 24, fault: 'STEP', value: 300 }),
    gps(1, { op: 'override', label: '130', kind: 'FORCE', amount: 0.9, ssm: 'NCD' }),
    gps(1, { op: 'fault', fault: 'STOP_TRANSMITTING', on: true }, { kind: 'time', seconds: 2 }),
    gps(1, { op: 'fault', fault: 'STOP_TRANSMITTING', on: false }, { kind: 'time', seconds: 4 }),
  ], 10))
  expect(runner.results.map(result => [result.status, result.at])).toEqual([['done', 0], ['done', 0], ['done', 0], ['done', 2], ['done', 4]])
  const stimulus = stimulusFor(fms)
  expect(stimulus.state(1)).toMatchObject({ jamDb: 6, satFault: { prn: 24, kind: 'STEP', amount: 300 } })
  expect(stimulus.state(0)).toMatchObject({ jamDb: 0, satFault: null, stopped: false, overrides: { '130': { text: 'FORCE 0.9 NCD' } } })
  // And on the receivers themselves: GPS 1's HIL (130) goes out forced and NCD; on GPS 2, PRN 24 off by 300 m is caught
  // (with enough satellites, fault detection and exclusion takes it out of the solution) and its signals are 6 dB down.
  expect(fms.gps[0].bus()!['130']).toMatchObject({ value: 0.9, ssm: 'NCD' })
  expect(fms.gps[1].bus()!['060'].map(word => word.value!).find(s => s.prn === 24)).toMatchObject({ excluded: true, used: false })
  // Satellite by satellite, GPS 2 hears each one 6 dB weaker than GPS 1 does.
  const cn0 = (index: number) => new Map(fms.gps[index].bus()!['060'].map(word => word.value!).filter(s => !s.sbas && s.tracked).map(s => [s.prn, s.cn0]))
  const [one, two] = [cn0(0), cn0(1)]
  const common = [...two.keys()].filter(prn => one.has(prn))
  expect(common.length).toBeGreaterThan(3)
  for (const prn of common) expect(one.get(prn)! - two.get(prn)!).toBeCloseTo(6, 0)
  // A stimulus the receiver refuses at run time is an execution error, not a silent pass.
  const accepted = new ScenarioRunner(scenarioOf([gps(1, { op: 'jam', db: 10 })]), new ScriptedFms(() => new Date(Date.parse(START))))
  expect(accepted.results[0]).toEqual({ status: 'done', at: 0 })
  const fms2 = new ScriptedFms(() => new Date(Date.parse(START)))
  stimulusFor(fms2).apply = () => false
  const failing = new ScenarioRunner(scenarioOf([gps(1, { op: 'jam', db: 10 }), { when: { kind: 'start' }, action: { kind: 'expectGpsSource', source: 'GPS1' } }]), fms2)
  expect(failing.outcome).toBe('error')
  expect(failing.results[0]).toMatchObject({ status: 'error', actual: 'GPS 1 refused: jam every signal by 10 dB.' })
})

test('expectGpsSource, expectApproachLevel and expectReceiverMode pass on what is there and fail with what was seen', () => {
  const check = (action: object, stimulus?: object) => runHeadless(scenarioOf([
    ...(stimulus ? [gps(1, stimulus)] : []), { when: { kind: 'time', seconds: 3 }, action } as unknown as ScenarioStep,
  ], 10)).runner.results.at(-1)
  expect(check({ kind: 'expectGpsSource', source: 'GPS1' })).toMatchObject({ status: 'pass', actual: 'GPS1' })
  expect(check({ kind: 'expectGpsSource', source: 'GPS2' })).toMatchObject({ status: 'fail', actual: 'GPS1' })
  expect(check({ kind: 'expectGpsSource', source: 'GPS2' }, { op: 'fault', fault: 'RECEIVER', on: true })).toMatchObject({ status: 'pass', actual: 'GPS2' })
  expect(check({ kind: 'expectReceiverMode', receiver: 1, mode: 'FAULT' }, { op: 'fault', fault: 'RECEIVER', on: true })).toMatchObject({ status: 'pass', actual: 'FAULT' })
  expect(check({ kind: 'expectReceiverMode', receiver: 2, mode: 'FAULT' }, { op: 'fault', fault: 'RECEIVER', on: true })).toMatchObject({ status: 'fail', actual: 'SBAS_NAV' })
  // Both receivers silenced: the FMS is not on GPS, and says what it is on.
  const both = runHeadless(scenarioOf([
    gps(1, { op: 'fault', fault: 'STOP_TRANSMITTING', on: true }), gps(2, { op: 'fault', fault: 'STOP_TRANSMITTING', on: true }),
    { when: { kind: 'time', seconds: 3 }, action: { kind: 'expectGpsSource', source: 'NONE' } },
    { when: { kind: 'start' }, action: { kind: 'expectGpsSource', source: 'GPS1' } },
  ], 10)).runner
  expect(both.results[2]).toMatchObject({ status: 'pass' })
  expect(both.results[3].status).toBe('fail')
  expect(both.results[3].actual).toMatch(/^NONE \((DME|VOR|DR)/)
  // The approach level: none annunciated en route; LPV on the RNAV approach at the pinned start.
  expect(check({ kind: 'expectApproachLevel', level: 'LPV' })).toMatchObject({ status: 'fail', actual: 'none annunciated' })
  const lpv = runHeadless(library('gps1-fde-then-gps2')).runner
  expect(lpv.results[3]).toMatchObject({ status: 'pass', actual: 'LPV' })
})

test('while recording, what the GPS tab applies is recorded with its time, and replays to the same stimulus', () => {
  let now = Date.parse(START)
  const fms = new ScriptedFms(() => new Date(now))
  const recorder = new ScenarioRecorder(() => new Date(now))
  const stimulus = stimulusFor(fms)
  stimulus.listener = (index, op) => recorder.gps((index + 1) as 1 | 2, op)
  stimulus.setJamming(1, 30)
  now += 2_100
  stimulus.setSatFault(0, { prn: 24, kind: 'RAMP', amount: 5 })
  stimulus.setOverride(0, '130', { kind: 'FORCE', amount: 0.9, ssm: 'FW' })
  now += 3_000
  expect(stimulus.setStatusPatch(1, '273', { integrity: 'DETECTED' })).toBe(true)
  stimulus.setSatFault(0, null)
  stimulus.setOverride(0, '130', null)
  stimulus.setStatusPatch(1, '273', null)
  const recorded = recorder.toScenario('recorded GPS')
  expect(recorded.startTime).toBe(START)
  expect(recorded.steps).toEqual([
    { when: { kind: 'start' }, action: { kind: 'gps', receiver: 2, stimulus: { op: 'jam', db: 30 } } },
    { when: { kind: 'time', seconds: 2.25 }, action: { kind: 'gps', receiver: 1, stimulus: { op: 'satelliteFault', prn: 24, fault: 'RAMP', value: 5 } } },
    { when: { kind: 'time', seconds: 2.25 }, action: { kind: 'gps', receiver: 1, stimulus: { op: 'override', label: '130', kind: 'FORCE', amount: 0.9, ssm: 'FW' } } },
    { when: { kind: 'time', seconds: 5.25 }, action: { kind: 'gps', receiver: 2, stimulus: { op: 'status', label: '273', patch: { integrity: 'DETECTED' } } } },
    { when: { kind: 'time', seconds: 5.25 }, action: { kind: 'gps', receiver: 1, stimulus: { op: 'clearSatelliteFault' } } },
    { when: { kind: 'time', seconds: 5.25 }, action: { kind: 'gps', receiver: 1, stimulus: { op: 'clearOverride', label: '130' } } },
    { when: { kind: 'time', seconds: 5.25 }, action: { kind: 'gps', receiver: 2, stimulus: { op: 'clearStatus', label: '273' } } },
  ])
  // A refused change is not recorded.
  expect(stimulus.setStatusPatch(1, '273', { mode: 'WARP' } as never)).toBe(false)
  expect(recorder.toScenario('again').steps).toHaveLength(7)
  // Played back, it leaves the receivers as the tab left them, and it is valid JSON the bench can import.
  const replay = runHeadless({ ...parseScenario(JSON.stringify(recorded)), steps: [...recorded.steps, { when: { kind: 'start' }, action: { kind: 'expectGpsSource', source: 'GPS1' } }] })
  expect(replay.runner.outcome).toBe('passed')
  for (const index of [0, 1]) expect(stimulusFor(replay.fms).state(index)).toEqual(stimulus.state(index))
})

/** Every tick: what both receivers put out and what the FMS made of it. */
const gpsTimeline = (scenario: Scenario, chunk: number) => {
  const { fms, plant } = singleComposition(scenarioStart(scenario)!, { profile: profileById(scenario.profile) })
  const runner = new ScenarioRunner(scenario, fms)
  const kernel = new FmsKernel(plant, runner)
  const timeline: string[] = []
  const snapshot = () => timeline.push(JSON.stringify({
    t: kernel.unitClockMs, assessed: fms.gpsStatus.assessed, chosen: fms.gpsStatus.chosen, source: fms.navState.gpsSource, level: fms.approachType,
    receivers: fms.gps.map(receiver => ({ mode: receiver.mode, satellites: receiver.bus()?.['060'].map(word => word.value) })),
    alerts: fms.recallList.map(message => message.text),
  }))
  // Each frame is taken as its instant closes, before the step that leads into the next.
  while (!runner.finished) kernel.advance(chunk, { onInstantClose: snapshot })
  snapshot()
  return { timeline, results: runner.results, report: reportMarkdown(runner) }
}

// One test per scenario, so the Fast logic shards can spread them (#1456).
for (const id of ['gps1-fde-then-gps2', 'gps1-spoof-walks-off']) {
  test(`the same GPS scenario gives the same GPS timeline on every run, however the ticks are grouped: ${id}`, () => {
    const scenario = library(id)
    const first = gpsTimeline(scenario, 1)
    expect(first.results.every(result => result.status === 'done' || result.status === 'pass'), id).toBe(true)
    expect(first.timeline.length).toBeGreaterThan(2000 / TICK_SECONDS)
    const again = gpsTimeline(scenario, 1)
    expect(again.timeline, `${id} run twice`).toEqual(first.timeline)
    expect(again.report).toBe(first.report)
    for (const chunk of [7, 64]) {
      const grouped = gpsTimeline(scenario, chunk)
      expect(grouped.timeline, `${id} in groups of ${chunk}`).toEqual(first.timeline)
      expect(grouped.results).toEqual(first.results)
    }
  })
}

test('the report and the procedure list every GPS stimulus and clear, and name what fixes the GPS timeline', () => {
  const scenario = library('gps1-spoof-walks-off')
  const { runner } = runHeadless(scenario)
  const report = reportMarkdown(runner)
  expect(report).toContain(`- Started: ${START}`)
  expect(report).toContain(`- GPS: model ${GPS_MODEL_VERSION}; constellation seed 1; receiver seeds 101 and 202;`)
  const spoofAt = runner.results[3].at!, clearAt = runner.results[10].at!
  expect(spoofAt).toBeGreaterThan(2000)
  const minutes = (seconds: number) => `${Math.floor(seconds / 60)} min ${Math.round((seconds % 60) * 100) / 100} s`
  expect(report).toContain('| Step | Receiver | At | Operation | Fields | In words |')
  expect(report).toContain(`| 4 | GPS 1 | ${minutes(spoofAt)} | spoof | northM 0; driftEastMps 2 | spoof the position 0 m north, drifting 2 m/s east |`)
  expect(report).toContain(`| 11 | GPS 1 | ${minutes(clearAt)} | clearSpoof | — | end the spoofing |`)
  expect(report).toContain('| 4 | Within 12 NM of FERDI, on GPS 1, spoof the position 0 m north, drifting 2 m/s east. |')
  const procedure = procedureText(scenario)
  expect(procedure.preconditions).toContain(`The simulated clock starts at ${START}, which fixes the GPS sky the receivers see.`)
  expect(procedure.preconditions).toContain(`The GPS model is ${GPS_MODEL_VERSION}`)
  expect(procedure.steps).toContain('4. Within 12 NM of FERDI, on GPS 1, spoof the position 0 m north, drifting 2 m/s east.')
  expect(procedure.steps).toContain('11. Within 5 NM of FERDI, on GPS 1, end the spoofing.')
  expect(procedure.expectedResult).toContain('- the alert GPS DISAGREE has been raised within 120 s.')
  expect(procedure.expectedResult).toContain('- the FMS navigates on GPS1.')
  expect(procedure.expectedResult).toContain('- GPS 1 is in SBAS_PA mode.')
  expect(procedure.expectedResult).toContain('- the approach annunciated is LPV.')
  // The run starts at the scenario's start time, not the headless default.
  const later = runHeadless({ ...scenarioOf([gps(2, { op: 'jam', db: 5 })], 5), startTime: '2026-09-28T02:15:00.000Z' }).runner
  expect(later.startedAt.toISOString()).toBe('2026-09-28T02:15:00.000Z')
  expect(reportMarkdown(later)).toContain('- Started: 2026-09-28T02:15:00.000Z')
  // A run on a scenario that changes nothing on GPS has no stimulus table, but still names the GPS inputs.
  const plain = reportMarkdown(runHeadless(library('manual-rnp')).runner)
  expect(plain).not.toContain('GPS stimuli:')
  expect(plain).toContain(`- GPS: model ${GPS_MODEL_VERSION}`)
})

test('GPS 1 ramp: FDE excludes the satellite and GPS 1 keeps LPV; its receiver fault moves the FMS to GPS 2 (library)', () => {
  const scenario = library('gps1-fde-then-gps2')
  const { fms, plant } = singleComposition(scenarioStart(scenario)!, { profile: profileById(scenario.profile) })
  const runner = new ScenarioRunner(scenario, fms)
  const kernel = new FmsKernel(plant, runner)
  const excluded = (index: number) => fms.gps[index].bus()?.['060'].filter(word => word.value!.excluded).map(word => word.value!.prn) ?? []
  let excludedSeen = false
  while (!runner.finished) {
    kernel.advance(1)
    if (runner.results[4].status === 'done' && runner.results[8].status === 'pending') excludedSeen ||= excluded(0).includes(24)
  }
  expect(runner.outcome).toBe('passed')
  expect(excludedSeen).toBe(true)
  expect(excluded(1)).toEqual([])
  expect(fms.navSourceLog[0].source).toBe('GPS2')
  expect(fms.recallList.map(message => message.text)).not.toContain('GPS NAV LOST')
})

test('the spoofed GPS 1 is caught only by GPS DISAGREE (library)', () => {
  const { runner, fms } = runHeadless(library('gps1-spoof-walks-off'))
  expect(runner.outcome).toBe('passed')
  expect(fms.recallList.map(message => message.text)).toEqual(['GPS DISAGREE'])
  // Without the spoof the scenario fails on its first check: the alert is the spoof's doing.
  const unspoofed = library('gps1-spoof-walks-off')
  const without = runHeadless({ ...unspoofed, steps: unspoofed.steps.map(step => (step.action.kind === 'gps' ? { ...step, action: { kind: 'keys' as const, keys: ['PROG' as const] } } : step)) }).runner
  expect(without.results[4]).toMatchObject({ status: 'fail' })
})

test('KBTV, SBAS lost on final: the gps steps drop LPV to LNAV and end the approach without an integrity alert (library)', () => {
  const scenario = library('kbtv-rnav15-sbas-lost')
  const { runner, fms } = runHeadless(scenario)
  expect(runner.outcome).toBe('passed')
  expect(fms.gps.map(receiver => receiver.mode)).toEqual(['NAV', 'NAV'])
  expect(fms.recallList.map(message => message.text)).not.toContain('GPS POS UNCERTAIN')
  // Without the SBAS steps the approach stays LPV: the level check fails, so the outcome is the stimulus's doing.
  const without = runHeadless({ ...scenario, steps: scenario.steps.map(step => (step.action.kind === 'gps' ? { ...step, action: { kind: 'keys' as const, keys: ['PROG' as const] } } : step)) }).runner
  expect(without.results[3]).toMatchObject({ status: 'fail', actual: 'LPV' })
})
