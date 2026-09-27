import { expect, logicTest as test } from './isolated-client-test'
import { FlightSimulator } from '../src/fmsCdu/flight'
import {
  ScenarioRecorder, ScenarioRunner, linePattern, parseScenario, procedureText, reportMarkdown, runHeadless, type Scenario,
} from '../src/fmsCdu/scenario'
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
  expect(runner.results[9].at! - runner.results[8].at!).toBeLessThanOrEqual(30)
  expect(runner.results[10]).toEqual({ status: 'pass', at: runner.results[9].at, actual: 'not raised' })
  expect(fms.hasCondition('gpsLost')).toBe(true)

  // The checks prove the actions: without TOGA the aircraft reaches UL501 too late, and unarmed the FMS asks for it.
  const scenario = library('gps-lost-before-faf')
  const without = (kind: string) => runHeadless({ ...scenario, steps: scenario.steps.map(step => (step.action.kind === kind ? { ...step, action: { kind: 'keys' as const, keys: [] } } : step)) }).runner.results
  expect(without('goAround')[9]).toMatchObject({ status: 'fail' })
  expect(without('armApproach')[10]).toMatchObject({ status: 'fail', actual: 'ARM APPROACH' })
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
  expect(runner.elapsed).toBe(20)
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
  expect(scenario.steps[1].when).toEqual({ kind: 'time', seconds: 2.3 })
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
  expect(text.steps).toMatch(/^2\. Then type \.03 into the scratchpad\.$/m)
  expect(text.steps).toMatch(/^7\. At 30 s, check that screen line 14 matches/m)
  expect(text.steps).toMatch(/^8\. Then check that the alert CHECK ANP has been raised within 120 s\.$/m)
  expect(text.expectedResult).toMatch(/^- the RNP annunciator is lit\.$/m)

  const { runner } = runHeadless(scenario)
  const context = { startedAt: new Date(START), cycle: 'DEMO-2609', variant: 'A' }
  const report = reportMarkdown(scenario, runner.results, context)
  expect(report).toMatch(/^\*\*Result: PASS\*\*$/m)
  expect(report).toMatch(/not flight-qualified evidence/)
  expect(report).toMatch(/^\| 4 \| Then check that screen line 10 matches \/MANUAL\/\. \| 0 s \| PASS \| RNP\/ANP MANUAL \|$/m)
  const failed = reportMarkdown(scenario, runner.results.map((result, i) => (i === 5 ? { status: 'fail' as const, at: 0, actual: 'out' } : result)), context)
  expect(failed).toMatch(/^\*\*Result: FAIL\*\*$/m)
})

test('a file that is not a scenario is refused', () => {
  expect(() => parseScenario('{"title":"x"}')).toThrow(/needs a title, maxSeconds and steps/)
  expect(() => parseScenario('{"title":"x","maxSeconds":5,"steps":[{"when":{"kind":"start"}}]}')).toThrow(/trigger \(when\) and an action/)
  expect(() => parseScenario('not json')).toThrow()
  expect(parseScenario('{"title":"x","maxSeconds":5,"steps":[]}')).toMatchObject({ id: 'imported-x', objective: '' })
})
