import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import {
  ScenarioRecorder, ScenarioRunner, TICK_SECONDS, advanceTicks, linePattern, parseScenario, procedureText, reportMarkdown, runHeadless,
  scenarioDigest, scenarioProblems, type Scenario,
} from '../src/fmsCdu/scenario'
import { UNMODELLED_CONDITIONS } from '../src/fmsCdu/conditions'
import { HELICOPTER_PROFILE, profileById, profileFingerprint } from '../src/fmsCdu/profile'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { screenText } from '../src/fmsCdu/screen'

// Scenarios, record and playback, screen assertions and test procedure export (the FMS test bench research
// roadmap, step 8). A scenario's steps run in order, each after its trigger; an expectation may wait for its
// condition, and a step the run never reaches fails it.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)
const library = (id: string) => SCENARIO_LIBRARY.find(entry => entry.id === id)!

test('every built-in scenario passes against the simulation', () => {
  for (const scenario of SCENARIO_LIBRARY) {
    const { runner } = runHeadless(scenario)
    expect(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass'), scenario.id).toEqual([])
    expect(runner.passed).toBe(true)
  }
})

test('GPS lost 2 NM before the FAF: the loss is injected at the distance, and the alerts and missed approach follow', () => {
  const { runner, fms } = runHeadless(library('gps-lost-before-faf'))
  // The condition went in when the aircraft reached 2 NM from FERDI, not at the start.
  expect(runner.results[3].at).toBeGreaterThan(600)
  expect(runner.results[4]).toMatchObject({ status: 'pass', actual: 'GPS NAV LOST' })
  expect(runner.results[8].at! - runner.results[7].at!).toBeLessThanOrEqual(30)
  expect(runner.results[9]).toEqual({ status: 'pass', at: runner.results[8].at, actual: 'not raised' })
  expect(fms.hasCondition('gpsLost')).toBe(true)

  // The checks prove the actions: without TOGA the aircraft reaches UL501 too late, and unarmed the FMS asks for it.
  const scenario = library('gps-lost-before-faf')
  // Distance triggers use aircraft truth; ARM APPROACH uses measured position. For the omitted-arm mutation, leave
  // 0.1 NM inside the trigger boundary before immediately calling TOGA, so GPS noise cannot order those two events.
  const without = (kind: string) => runHeadless({ ...scenario, steps: scenario.steps.map(step => step.action.kind === kind
    ? { ...step, action: { kind: 'keys' as const, keys: ['PROG' as const] } }
    : kind === 'armApproach' && step.when.kind === 'distance' ? { ...step, when: { ...step.when, nm: 1.9 } } : step) }).runner.results
  expect(without('goAround')[8]).toMatchObject({ status: 'fail' })
  expect(without('armApproach')[9]).toMatchObject({ status: 'fail', actual: 'ARM APPROACH' })
})

test('steps run strictly in order: a later step waits for an earlier one even if its own trigger has come', () => {
  const scenario: Scenario = {
    id: 'order', title: 'Order', objective: '', maxSeconds: 30,
    steps: [
      { when: { kind: 'time', seconds: 10 }, action: { kind: 'alert', text: 'TEST ALERT' } },
      { when: { kind: 'start' }, action: { kind: 'expectAlert', text: 'TEST ALERT' } },
    ],
  }
  const { runner } = runHeadless(scenario)
  expect(runner.results.map(result => [result.status, result.at])).toEqual([['done', 10], ['pass', 10]])
})

test('an expectation waits up to its "within", passing when met and failing after it with what was shown', () => {
  // CHECK ANP comes after the 60-second time to alert, inside the 120-second window: it passes when it comes.
  const rnp = runHeadless(library('manual-rnp')).runner
  expect(rnp.results[7]).toEqual({ status: 'pass', at: 60, actual: 'CHECK ANP' })

  const waiting: Scenario = {
    id: 'wait', title: 'Wait', objective: '', maxSeconds: 60,
    steps: [
      { when: { kind: 'time', seconds: 5 }, action: { kind: 'alert', text: 'LATE ALERT' } },
      { when: { kind: 'start' }, action: { kind: 'expectAlert', text: 'NEVER RAISED' }, within: 10 },
      { when: { kind: 'start' }, action: { kind: 'expectLine', line: 0, pattern: '^RTE' } },
      { when: { kind: 'start' }, action: { kind: 'expectActive', waypoint: 'NOWHERE' } },
      // A wait longer than the run ends at maxSeconds as a failure with what was shown, not as not reached.
      { when: { kind: 'start' }, action: { kind: 'expectAlert', text: 'NEVER RAISED' }, within: 600 },
    ],
  }
  const { runner } = runHeadless(waiting)
  expect(runner.results[1]).toEqual({ status: 'fail', at: 15, actual: 'LATE ALERT' })
  // A screen check reports the line it saw; an active waypoint check, the active waypoint.
  expect(runner.results[2]).toMatchObject({ status: 'fail', actual: expect.stringMatching(/^IDENT/) })
  expect(runner.results[3]).toMatchObject({ status: 'fail', actual: expect.stringMatching(/^[A-Z0-9]{2,5}$/) })
  expect(runner.results[4]).toEqual({ status: 'fail', at: 60, actual: 'LATE ALERT' })
  expect(runner.passed).toBe(false)
})

test('steps the run never reaches by maxSeconds fail as not reached', () => {
  const scenario: Scenario = {
    id: 'timeout', title: 'Timeout', objective: '', maxSeconds: 20,
    steps: [
      { when: { kind: 'active', waypoint: 'NOWHERE' }, action: { kind: 'alert', text: 'X' } },
      { when: { kind: 'start' }, action: { kind: 'expectLamp', lamp: 'MSG', lit: true } },
    ],
  }
  const { runner } = runHeadless(scenario)
  expect(runner.results).toEqual([{ status: 'not reached' }, { status: 'not reached' }])
  // The run ends at the first tick past its limit.
  expect(runner.endedAfter).toBe(20 + TICK_SECONDS)
  expect(runner.outcome).toBe('timed out')
  expect(runner.passed).toBe(false)
})

test('a recording of keys, conditions and a screen check plays back and passes; a changed screen fails it', () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const recorder = new ScenarioRecorder(() => new Date(now))
  const press = (fn: Parameters<ScriptedFms['press']>[0]) => { fms.press(fn); recorder.key(fn) }
  press('PROG')
  now += 300
  press('CHAR_1')
  now += 2000
  fms.setCondition('gpsLost', true)
  recorder.condition('gpsLost', true)
  now += 1000
  recorder.checkLine(0, screenText(fms.screen())[0])
  const scenario = recorder.toScenario('Recorded')
  // Keys pressed within a second are one step.
  expect(scenario.steps.map(step => step.action.kind)).toEqual(['keys', 'condition', 'expectLine'])
  expect(scenario.steps[0]).toEqual({ when: { kind: 'start' }, action: { kind: 'keys', keys: ['PROG', 'CHAR_1'] } })
  // Recorded times land on the tick grid, at the tick where playback will run them.
  expect(scenario.steps[1].when).toEqual({ kind: 'time', seconds: 2.5 })
  expect(scenario.steps[2].within).toBe(5)
  expect(scenario.maxSeconds).toBe(60)

  // Played back on a fresh simulation, and through JSON as a saved scenario would be.
  const replay = runHeadless(parseScenario(JSON.stringify(scenario)))
  expect(replay.runner.passed).toBe(true)
  expect(replay.fms.hasCondition('gpsLost')).toBe(true)

  const tampered = structuredClone(scenario)
  tampered.steps[0].action = { kind: 'keys', keys: ['RTE'] }
  expect(runHeadless(tampered).runner.passed).toBe(false)
})

test('a screen line pattern matches the line as shown, whatever the spacing, and escapes its text', () => {
  const pattern = new RegExp(linePattern(' RNP/ANP   MANUAL (0.3) '))
  expect(pattern.test(' RNP/ANP MANUAL (0.3)   ')).toBe(true)
  expect(pattern.test('RNP/ANP  MANUAL (0.3)')).toBe(true)
  expect(pattern.test('RNP/ANP MANUAL (0x3)')).toBe(false)
  expect(pattern.test('RNP/ANP MANUAL (0.3) X')).toBe(false)
})

test('the runner can drive a bench session: poll after each flight step, and abandon marks the rest not reached', () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  const runner = new ScenarioRunner(library('dead-reckoning'), fms)
  for (let t = 0; t < 130; t += 1) { now += 1000; sim.step(1); runner.poll() }
  expect(runner.current).toBe(5)
  runner.abandon()
  expect(runner.finished).toBe(true)
  expect(runner.results.slice(5).map(result => result.status)).toEqual(['not reached', 'not reached'])
})

test('a scenario becomes test procedure text, and its run a Markdown report marked as simulation evidence', () => {
  const scenario = library('manual-rnp')
  const text = procedureText(scenario)
  expect(text.title).toBe(scenario.title)
  expect(text.objective).toBe(scenario.objective)
  expect(text.preconditions).toMatch(/scripted CMA-9000 simulation \(not a navigation computer\)/)
  expect(text.steps.split('\n')).toHaveLength(scenario.steps.length)
  expect(text.steps.split('\n')[0]).toBe('1. At the start, press PROG.')
  // After the first step, a step with no trigger of its own follows on: "then".
  expect(text.steps).toMatch(/^2\. Then type \.01 into the scratchpad\.$/m)
  expect(text.steps).toMatch(/^7\. At 30 s, check that screen line 14 matches/m)
  expect(text.steps).toMatch(/^8\. Then check that the alert CHECK ANP has been raised within 120 s\.$/m)
  expect(text.expectedResult).toMatch(/^- the RNP annunciator is lit\.$/m)

  const { runner } = runHeadless(scenario, START, { variant: 'A', cycle: 'DEMO-2609' })
  const report = reportMarkdown(runner)
  expect(report).toMatch(/^\*\*Result: PASS\*\*$/m)
  expect(report).toMatch(/not flight-qualified evidence/)
  expect(report).toMatch(/^- Hardware variation: A$/m)
  // The run names the aircraft profile it flew, with a fingerprint that changes with any profile value (plan A2).
  expect(report).toMatch(/^- Aircraft profile: cma9000-s300-heli-civil v5 \(fnv1a-[0-9a-f]{8}\): CMA-9000 helicopter, civil SAR target \(S\/W -300 baseline\); 66 of 67 parameters in force; declared only: settlingTime; civil-sar-s300: 87 configured references resolved; \d+ options on \(\d+ implemented, \d+ partial, \d+ pending\)$/m)
  expect(report).toMatch(new RegExp(`^- Scenario: manual-rnp, ${scenarioDigest(scenario)}$`, 'm'))
  expect(report).toMatch(/^\| 4 \| Then check that screen line 10 matches \/MANUAL\/\. \| 0 s \| PASS \| RNP\/ANP MANUAL \|$/m)
})

test('a file that is not a scenario is refused', () => {
  expect(() => parseScenario('{"title":"x"}')).toThrow(/needs maxSeconds.*needs steps/)
  expect(() => parseScenario('{"title":"x","maxSeconds":5,"steps":[{"when":{"kind":"start"}}]}')).toThrow(/step 1: an action is required/)
  expect(() => parseScenario('not json')).toThrow()
  expect(parseScenario('{"title":"x","maxSeconds":5,"steps":[]}')).toMatchObject({ id: 'imported-x', objective: '' })
})

// The run contract (review observations N01 to N08, 28 September): a run passes only when every check it makes holds,
// and it cannot report success for something it did not evaluate, or evaluate something after its deadline.
const scenarioOf = (steps: unknown[], maxSeconds = 30): Scenario => ({ id: 'contract', title: 'Contract', objective: '', maxSeconds, steps: steps as Scenario['steps'] })

test('an unsupported step or payload is refused at import and never runs as a pass (N01, N02, N07, N08)', () => {
  const refused: [unknown, RegExp][] = [
    [{ when: { kind: 'start' }, action: { kind: 'unsupportedCommand' } }, /unsupported action "unsupportedCommand"/],
    [{ when: { kind: 'start' }, action: { kind: 'expectUnsupported' } }, /unsupported action "expectUnsupported"/],
    [{ when: { kind: 'sometime' }, action: { kind: 'goAround' } }, /unsupported trigger "sometime"/],
    [{ when: { kind: 'start' }, action: { kind: 'expectLine', line: 2, pattern: '[' } }, /not a valid regular expression/],
    [{ when: { kind: 'start' }, action: { kind: 'expectLine', line: 40, pattern: 'X' } }, /line from 0 to 13/],
    [{ when: { kind: 'start' }, action: { kind: 'keys', keys: ['LAUNCH'] } }, /list of CDU functions/],
    [{ when: { kind: 'start' }, action: { kind: 'condition', condition: 'meteor', on: true } }, /known condition/],
    [{ when: { kind: 'time', seconds: Number.NaN }, action: { kind: 'goAround' } }, /seconds between 0 and 86400/],
    [{ when: { kind: 'start' }, action: { kind: 'expectNoAlert', text: 'CHECK ANP' }, within: 5 }, /checked at one moment/],
    [{ when: { kind: 'start' }, action: { kind: 'goAround' }, within: 5 }, /within applies only to a check/],
  ]
  for (const [step, reason] of refused) {
    expect(() => parseScenario(JSON.stringify(scenarioOf([step]))), JSON.stringify(step)).toThrow(reason)
    // Handed to the runner directly, it is not run at all.
    const { runner } = runHeadless(scenarioOf([step]))
    expect(runner.outcome, JSON.stringify(step)).toBe('invalid')
    expect(runner.results[0].status).toBe('pending')
    expect(reportMarkdown(runner)).toMatch(/^\*\*Result: INVALID SCENARIO \(not run\)\*\*$/m)
  }
})

test('a run with no checks is "no checks", not a pass, and its procedure text promises nothing (N03, N06)', () => {
  const empty = runHeadless(scenarioOf([])).runner
  expect(empty.outcome).toBe('no checks')
  expect(empty.passed).toBe(false)
  expect(reportMarkdown(empty)).toMatch(/^\*\*Result: NO CHECKS/m)
  const actionsOnly = scenarioOf([{ when: { kind: 'start' }, action: { kind: 'alert', text: 'SURPRISE' } }])
  expect(runHeadless(actionsOnly).runner.outcome).toBe('no checks')
  expect(procedureText(actionsOnly).expectedResult).toBe('None: this scenario has no checks. It plays back its actions and verifies no outcome.')
})

test('nothing runs after the time limit; a step due at the limit still runs (N04)', () => {
  const late = runHeadless(scenarioOf([
    { when: { kind: 'time', seconds: 1 }, action: { kind: 'alert', text: 'ON TIME' } },
    { when: { kind: 'time', seconds: 2 }, action: { kind: 'alert', text: 'AT THE LIMIT' } },
    { when: { kind: 'time', seconds: 3 }, action: { kind: 'alert', text: 'TOO LATE' } },
  ], 2))
  expect(late.runner.results.map(result => [result.status, result.at])).toEqual([['done', 1], ['done', 2], ['not reached', undefined]])
  expect(late.fms.recallList.map(message => message.text)).toEqual(expect.arrayContaining(['ON TIME', 'AT THE LIMIT']))
  expect(late.fms.recallList.some(message => message.text === 'TOO LATE')).toBe(false)
  expect(late.runner.outcome).toBe('timed out')
})

test('a condition first met after its window does not satisfy the check (N05)', () => {
  const { runner } = runHeadless(scenarioOf([
    { when: { kind: 'start' }, action: { kind: 'expectAlert', text: 'LATE' }, within: 1 },
    { when: { kind: 'time', seconds: 2 }, action: { kind: 'alert', text: 'LATE' } },
  ]))
  // The check fails at its one-second deadline; the alert that comes at 2 s is too late for it.
  expect(runner.results[0]).toEqual({ status: 'fail', at: 1, actual: 'no alerts' })
  expect(runner.outcome).toBe('failed')
  // Met inside the window, it passes when met.
  const inTime = runHeadless(scenarioOf([
    { when: { kind: 'start' }, action: { kind: 'condition', condition: 'gpsLost', on: true } },
    { when: { kind: 'start' }, action: { kind: 'expectLamp', lamp: 'MSG', lit: true }, within: 1 },
  ])).runner
  expect(inTime.results[1].status).toBe('pass')
  expect(inTime.results[1].at).toBeLessThanOrEqual(1)
})

test('a step that throws ends the run as an execution error, not a pass or a failed check', () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  fms.press = () => { throw new Error('panel disconnected') }
  const runner = new ScenarioRunner(scenarioOf([
    { when: { kind: 'start' }, action: { kind: 'keys', keys: ['PROG'] } },
    { when: { kind: 'start' }, action: { kind: 'expectLamp', lamp: 'MSG', lit: false } },
  ]), fms)
  advanceTicks(4, ms => { now += ms }, sim, runner)
  expect(runner.results).toEqual([{ status: 'error', at: 0, actual: 'panel disconnected' }, { status: 'not reached' }])
  expect(runner.outcome).toBe('error')
})

test('stopping a run is its own outcome', () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const runner = new ScenarioRunner(library('dead-reckoning'), fms)
  runner.abandon()
  expect(runner.outcome).toBe('stopped')
})

test('the same scenario gives the same timeline however the ticks are grouped, as the bench groups them by rate', () => {
  const scenario = library('gps-lost-before-faf')
  const run = (chunk: number) => {
    let now = START
    const fms = new ScriptedFms(() => new Date(now), { profile: profileById(scenario.profile) })
    const sim = new FlightSimulator(fms)
    const runner = new ScenarioRunner(scenario, fms)
    while (!runner.finished) advanceTicks(chunk, ms => { now += ms }, sim, runner)
    return { results: runner.results, position: fms.truePosition, altitude: fms.altitude, elapsed: runner.elapsed }
  }
  const oneAtATime = run(1)
  expect(oneAtATime.results.every(result => result.status === 'done' || result.status === 'pass')).toBe(true)
  // At 64 times real time the bench runs 64 ticks per callback; an uneven grouping stands for a throttled browser.
  for (const chunk of [7, 64]) {
    const grouped = run(chunk)
    expect(grouped.results).toEqual(oneAtATime.results)
    expect(grouped.position).toEqual(oneAtATime.position)
    expect(grouped.altitude).toBe(oneAtATime.altitude)
  }
})

// N05, second review (Astra C07): a window shorter than a tick. The deadline falls between observations; a condition
// first seen after it must fail, even when the next tick is the first chance to look.
test('a check first observed strictly after its window fails, even when the deadline falls between ticks (N05)', () => {
  // The DEMO-2609 cycle ends at 23:59:00Z; starting 0.125 s before, DATABASE OUT OF DATE first appears at the 0.25 s tick.
  const start = Date.UTC(2026, 8, 30, 23, 58, 59, 875)
  const run = (within: number) => runHeadless(scenarioOf([
    { when: { kind: 'start' }, action: { kind: 'expectAlert', text: 'DATABASE OUT OF DATE' }, within },
  ], 10), start).runner
  const late = run(0.1)
  expect(late.results[0]).toMatchObject({ status: 'fail', at: 0.25 })
  expect(late.outcome).toBe('failed')
  // Observed exactly at the deadline, or inside it, it passes.
  expect(run(0.25).results[0]).toMatchObject({ status: 'pass', at: 0.25 })
  expect(run(0.3).results[0]).toMatchObject({ status: 'pass', at: 0.25 })
})

test('a cleared scratchpad can be checked with an empty expectation (second review C12)', () => {
  const scenario = scenarioOf([
    { when: { kind: 'start' }, action: { kind: 'type', text: 'ABC' } },
    { when: { kind: 'start' }, action: { kind: 'expectScratchpad', text: 'ABC' } },
    { when: { kind: 'start' }, action: { kind: 'keys', keys: ['CLR', 'CLR', 'CLR'] } },
    { when: { kind: 'start' }, action: { kind: 'expectScratchpad', text: '' } },
  ])
  expect(() => parseScenario(JSON.stringify(scenario))).not.toThrow()
  expect(runHeadless(scenario).runner.outcome).toBe('passed')
  // Alert text still needs something to look for.
  expect(() => parseScenario(JSON.stringify(scenarioOf([{ when: { kind: 'start' }, action: { kind: 'expectAlert', text: '' } }])))).toThrow(/1 to 24 characters/)
})

test('APPR pressed off is recorded and replayed, and a non-boolean on is refused at import (third review D03)', () => {
  let now = START
  const recorder = new ScenarioRecorder(() => new Date(now))
  recorder.armApproach()
  now += 2000
  recorder.armApproach(false)
  const scenario = recorder.toScenario('APPR on then off')
  expect(scenario.steps.map(step => step.action)).toEqual([{ kind: 'armApproach' }, { kind: 'armApproach', on: false }])
  expect(procedureText(scenario).steps).toContain('press APPR off')
  const replayed = parseScenario(JSON.stringify(scenario))
  const { fms } = runHeadless(replayed)
  expect(fms.approachArmed).toBe(false)
  const armedOnly = runHeadless(parseScenario(JSON.stringify({ ...scenario, steps: scenario.steps.slice(0, 1) }))).fms
  expect(armedOnly.approachArmed).toBe(true)
  expect(() => parseScenario(JSON.stringify({ ...scenario, steps: [{ when: { kind: 'start' }, action: { kind: 'armApproach', on: 'no' } }] }))).toThrow(/armApproach on must be true or false/)
})

test('the profile fingerprint changes when any profile value changes, so evidence names the exact profile (plan A2)', () => {
  const base = profileFingerprint(HELICOPTER_PROFILE)
  expect(base).toMatch(/^fnv1a-[0-9a-f]{8}$/)
  expect(profileFingerprint(structuredClone(HELICOPTER_PROFILE))).toBe(base)
  const faster = structuredClone(HELICOPTER_PROFILE)
  faster.parameters.gateSpeed.value = 81
  expect(profileFingerprint(faster)).not.toBe(base)
  const forced = structuredClone(HELICOPTER_PROFILE)
  forced.parameters.rollRate.inForce = !forced.parameters.rollRate.inForce
  expect(profileFingerprint(forced)).not.toBe(base)
})

test('a scenario declares the radio altimeter surface by id; the report names it, and an unknown surface is refused (Stage B2)', () => {
  const declared = runHeadless({ id: 's', title: 's', objective: '', maxSeconds: 1, surface: 'offshore-87n', steps: [] }).runner
  expect(reportMarkdown(declared)).toMatch(/^- Surface for the radio altimeter: offshore-87n \(declared flat sea at 0 ft MSL/m)
  const none = runHeadless({ id: 'n', title: 'n', objective: '', maxSeconds: 1, steps: [] }).runner
  expect(reportMarkdown(none)).toMatch(/^- Surface for the radio altimeter: none \(radio height NCD everywhere\)$/m)
  const unknown = runHeadless({ id: 'u', title: 'u', objective: '', maxSeconds: 1, surface: 'moon', steps: [] }).runner
  expect(unknown.outcome).toBe('invalid')
  expect(unknown.problems.join(' ')).toMatch(/surface must be one of none, offshore-87n/)
})

// Rev 3 B3.5 F10: baro, heading and attitude failures are not modelled in v1, so a scenario that injects one is refused
// at admission with that reason, rather than run as though the aircraft had them.
test('a scenario that injects baro, heading or attitude invalid is refused at admission, with the reason (F10)', () => {
  for (const [condition, name] of [['baroFail', 'barometric altitude invalid'], ['headingFail', 'heading invalid'], ['attitudeFail', 'attitude invalid']]) {
    for (const on of [true, false]) {
      const scenario = { id: 'f10', title: 'f10', objective: '', maxSeconds: 5, steps: [{ when: { kind: 'after', seconds: 1 }, action: { kind: 'condition', condition, on } }] }
      expect(scenarioProblems(scenario), condition).toEqual([`step 1: ${name} is not modelled in v1, so a scenario that injects it is refused (rev 3 B3.5 F10)`])
      const { runner } = runHeadless(scenario as unknown as Scenario)
      expect(runner.outcome).toBe('invalid')
      // Refused before it ran: no step was reached.
      expect(runner.results.every(result => result.status === 'pending')).toBe(true)
    }
  }
  expect(UNMODELLED_CONDITIONS.map(condition => condition.id)).toEqual(['baroFail', 'headingFail', 'attitudeFail'])
  // An unknown condition is still an unknown condition.
  expect(scenarioProblems({ id: 'x', title: 'x', objective: '', maxSeconds: 5, steps: [{ when: { kind: 'start' }, action: { kind: 'condition', condition: 'gremlins', on: true } }] }))
    .toEqual(['step 1: condition needs a known condition and on true or false'])
})

test('a scenario names its aircraft profile and makes autopilot selections; both are validated (Stage B3)', () => {
  const climb = runHeadless({ id: 'p', title: 'p', objective: '', maxSeconds: 60, steps: [
    { when: { kind: 'start' }, action: { kind: 'autopilot', altitude: 3500, verticalSpeed: 500, speed: 100 } },
  ] }).runner
  expect(climb.results[0]).toEqual({ status: 'done', at: 0 })
  expect(reportMarkdown(climb)).toMatch(/^- Aircraft profile: cma9000-s300-heli-civil v5 /m)
  const lab = runHeadless({ id: 'l', title: 'l', objective: '', maxSeconds: 1, profile: 'lab-airline-vnav', steps: [
    { when: { kind: 'start' }, action: { kind: 'autopilot', verticalSpeed: 500 } },
  ] })
  expect(reportMarkdown(lab.runner)).toMatch(/^- Aircraft profile: lab-airline-vnav v5 /m)
  expect(reportMarkdown(lab.runner)).toContain('64 of 67 parameters in force; declared only: advisoryMinimumProgress, temperatureLapseRate, settlingTime')
  // VS is a helicopter-profile mode: under the laboratory VNAV profile it is an execution error, not a silent pass.
  expect(lab.runner.outcome).toBe('error')
  expect(scenarioProblems({ id: 'x', title: 'x', objective: '', maxSeconds: 1, profile: 'jet', steps: [] })).toEqual([expect.stringMatching(/profile must be one of cma9000-s300-heli-civil, cma9000-later-sbas-heli, lab-airline-vnav/)])
  expect(scenarioProblems({ id: 'y', title: 'y', objective: '', maxSeconds: 1, steps: [{ when: { kind: 'start' }, action: { kind: 'autopilot' } }] }).join(' ')).toMatch(/autopilot needs at least one/)
})

test('a TOGA the FMS refuses is an error in the run, never a silent pass', () => {
  // No approach in the demonstration route: nothing to go around onto.
  const scenario: Scenario = {
    id: 'toga-refused', title: 'TOGA refused', objective: '', maxSeconds: 5,
    steps: [{ when: { kind: 'start' }, action: { kind: 'goAround' } }, { when: { kind: 'start' }, action: { kind: 'expectActive', waypoint: 'MUN' } }],
  }
  const { runner } = runHeadless(scenario)
  expect(runner.outcome).toBe('error')
  expect(runner.results[0]).toMatchObject({ status: 'error', actual: expect.stringContaining('TOGA refused') })
})

test('a fresh alert check counts only what was raised since the last action began', () => {
  const run = (steps: Scenario['steps']) => runHeadless({ id: 'fresh', title: 'Fresh', objective: '', maxSeconds: 5, steps }).runner
  const raise = { when: { kind: 'start' as const }, action: { kind: 'alert' as const, text: 'CHECK ANP' } }
  const fresh = { when: { kind: 'start' as const }, action: { kind: 'expectAlert' as const, text: 'CHECK ANP', fresh: true } }
  const other = { when: { kind: 'start' as const }, action: { kind: 'keys' as const, keys: ['PROG' as const] } }
  expect(run([raise, fresh]).outcome).toBe('passed')
  // Raised before a later action: not fresh any more; without fresh it still counts.
  expect(run([raise, other, fresh]).results[2]).toMatchObject({ status: 'fail' })
  expect(run([raise, other, { ...fresh, action: { kind: 'expectAlert', text: 'CHECK ANP' } }]).outcome).toBe('passed')
  expect(scenarioProblems({ id: 'x', title: 'x', objective: '', maxSeconds: 5, steps: [{ when: { kind: 'start' }, action: { kind: 'expectNoAlert', text: 'X', fresh: true } }] })).toEqual(['step 1: fresh applies only to expectAlert, true or false'])
})

test('below triggers, expectAircraft ranges and expectHover are validated', () => {
  const problems = (action: unknown, when: unknown = { kind: 'start' }) => scenarioProblems({ id: 'x', title: 'x', objective: '', maxSeconds: 5, steps: [{ when, action }] })
  expect(problems({ kind: 'expectAircraft', minGroundSpeed: 4, track: 50 })).toEqual([])
  expect(problems({ kind: 'expectAircraft' })).toEqual([expect.stringContaining('expectAircraft needs at least one of')])
  expect(problems({ kind: 'expectAircraft', track: 400 })).toEqual(['step 1: expectAircraft track must be between 0 and 360'])
  expect(problems({ kind: 'expectAircraft', maxCrossTrack: 0.1, nearMetres: 10 })).toEqual(['step 1: expectAircraft nearMetres and minNearMetres need near'])
  expect(problems({ kind: 'expectHover' })).toEqual(['step 1: expectHover needs refused or reason'])
  expect(problems({ kind: 'expectHover', reason: 'OFF FINAL TRACK' })).toEqual([])
  expect(problems({ kind: 'expectActive', waypoint: 'MUN' }, { kind: 'below', feet: 350 })).toEqual([])
  expect(problems({ kind: 'expectActive', waypoint: 'MUN' }, { kind: 'below' })).toEqual(['step 1: a below trigger needs feet between -1500 and 60000'])
})

test('an above trigger fires when the altimeter reads the altitude, to the foot; expectAfcs checks the low-height caption', () => {
  const problems = (action: unknown, when: unknown = { kind: 'start' }) => scenarioProblems({ id: 'x', title: 'x', objective: '', maxSeconds: 5, steps: [{ when, action }] })
  expect(problems({ kind: 'expectActive', waypoint: 'MUN' }, { kind: 'above', feet: 2000 })).toEqual([])
  expect(problems({ kind: 'expectActive', waypoint: 'MUN' }, { kind: 'above' })).toEqual(['step 1: an above trigger needs feet between -1500 and 60000'])
  expect(problems({ kind: 'expectAfcs', lowHeight: 'LOW HT OFF' })).toEqual([])
  expect(problems({ kind: 'expectAfcs', lowHeight: 'OFF' })).toEqual(['step 1: expectAfcs lowHeight must be LOW HT, LOW HT OFF or NONE'])
  expect(problems({ kind: 'expectAfcs' })).toEqual(['step 1: expectAfcs needs at least one of collective, pitch, roll and lowHeight'])
  // The trigger: 1,999.4 ft reads 1,999; 1,999.5 reads 2,000 (a capture settles onto its altitude without reaching it).
  const unit = new ScriptedFms(() => new Date(START))
  const place = (altitude: number) => unit.placeAircraft({ position: unit.truePosition, track: 0, altitude }, 'test: altitude')
  place(1999.4)
  const runner = new ScenarioRunner({ id: 'x', title: 'x', objective: '', maxSeconds: 5, steps: [{ when: { kind: 'above', feet: 2000 }, action: { kind: 'keys', keys: ['PROG'] } }] }, unit)
  runner.poll()
  expect(runner.results[0].status).toBe('pending')
  place(1999.5)
  runner.poll()
  expect(runner.results[0].status).toBe('done')
  // The caption: none in level flight, so a step expecting LOW HT OFF fails there; LOW HT OFF once the radio height is
  // lost under a radio-height mode.
  const wrong = new ScenarioRunner({ id: 'x', title: 'x', objective: '', maxSeconds: 5, steps: [{ when: { kind: 'start' }, action: { kind: 'expectAfcs', lowHeight: 'LOW HT OFF' } }] }, unit, undefined, new FlightSimulator(unit))
  wrong.poll()
  expect(wrong.results[0]).toMatchObject({ status: 'fail', actual: expect.stringContaining('low height NONE') })
  const { runner: a4 } = runHeadless(library('87n-a4-ra-lost-in-hover'))
  expect(a4.outcome).toBe('passed')
  const caption = library('87n-a4-ra-lost-in-hover').steps.findIndex(step => step.action.kind === 'expectAfcs' && step.action.lowHeight === 'LOW HT OFF')
  expect(a4.results[caption]).toMatchObject({ status: 'pass', actual: 'ALT | HOV | HOV, low height LOW HT OFF' })
})
