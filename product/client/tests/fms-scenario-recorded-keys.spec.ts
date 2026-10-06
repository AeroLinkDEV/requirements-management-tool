import { expect, logicTest as test } from './isolated-client-test'
import { ScenarioRecorder, parseScenario, procedureText, runHeadless, scenarioProblems, type Scenario } from '../src/fmsCdu/scenario'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// Recorded CDU keys replay as the crew pressed them (#1505): each key at its own tick, and a held key held. The
// recorder, the saved JSON and the runner are the production path the bench records and plays back through.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)

/** Records keys at the given milliseconds after the start, pressing each on a live simulation as the bench does. */
function record(presses: { at: number; fn: CduFunction; held?: boolean }[]) {
  let now = START
  const live = new ScriptedFms(() => new Date(now))
  const recorder = new ScenarioRecorder(() => new Date(now))
  for (const press of presses) {
    now = START + press.at
    live.press(press.fn, { held: press.held === true })
    recorder.key(press.fn, press.held === true)
  }
  // Saved and loaded as a recording is, so the schema carries what playback needs.
  return { live, scenario: parseScenario(JSON.stringify(recorder.toScenario('Recorded keys'))) }
}

const scratchpad = (fms: ScriptedFms) => screenText(fms.screen())[SCRATCHPAD_LINE].trim()
const keyTimes = (scenario: Scenario) => scenario.steps.map(step => [step.when.kind === 'time' ? step.when.seconds : 0, step.action.kind === 'keys' ? step.action.keys : []])

test('two keys on different ticks stay two steps, each replayed at its own tick; keys on one tick share it', () => {
  const { scenario } = record([{ at: 250, fn: 'CHAR_A' }, { at: 750, fn: 'CHAR_B' }, { at: 1100, fn: 'CHAR_C' }, { at: 1200, fn: 'CHAR_D' }])
  expect(keyTimes(scenario)).toEqual([[0.25, ['CHAR_A']], [0.75, ['CHAR_B']], [1.25, ['CHAR_C', 'CHAR_D']]])
  const { runner } = runHeadless(scenario)
  expect(runner.results.map(result => [result.status, result.at])).toEqual([['done', 0.25], ['done', 0.75], ['done', 1.25]])
})

test('a run of keys under a second apart does not chain into one step pressed at the first key', () => {
  const { scenario } = record([250, 1000, 1750, 2500, 3250].map((at, i) => ({ at, fn: `CHAR_${'ABCDE'[i]}` as CduFunction })))
  expect(keyTimes(scenario)).toEqual([[0.25, ['CHAR_A']], [1, ['CHAR_B']], [1.75, ['CHAR_C']], [2.5, ['CHAR_D']], [3.25, ['CHAR_E']]])
  expect(runHeadless(scenario).runner.results.map(result => result.at)).toEqual([0.25, 1, 1.75, 2.5, 3.25])
})

test('a held CLR is recorded held and replays as one, clearing the whole scratchpad as it did live', () => {
  const { live, scenario } = record([{ at: 250, fn: 'CHAR_A' }, { at: 500, fn: 'CHAR_B' }, { at: 3000, fn: 'CLR', held: true }])
  expect(scratchpad(live)).toBe('')
  expect(scenario.steps.at(-1)).toEqual({ when: { kind: 'time', seconds: 3 }, action: { kind: 'keys', keys: ['CLR'], held: true } })
  expect(procedureText(scenario).steps).toMatch(/^3\. At 3 s, press and hold CLR\.$/m)
  expect(scratchpad(runHeadless(scenario).fms)).toBe('')
})

test('playback holds a held keys step; without held it presses plainly, as saved scenarios from before held do; a non-boolean held is refused', () => {
  const plain: Scenario = {
    id: 'plain-clr', title: 'Plain CLR', objective: '', maxSeconds: 2,
    steps: [{ when: { kind: 'start' }, action: { kind: 'keys', keys: ['CHAR_A', 'CHAR_B'] } }, { when: { kind: 'time', seconds: 1 }, action: { kind: 'keys', keys: ['CLR'] } }],
  }
  expect(scratchpad(runHeadless(parseScenario(JSON.stringify(plain))).fms)).toBe('A')
  const held = { ...plain, steps: [plain.steps[0], { ...plain.steps[1], action: { kind: 'keys', keys: ['CLR'], held: true } }] }
  expect(scratchpad(runHeadless(parseScenario(JSON.stringify(held))).fms)).toBe('')
  const malformed = { ...plain, steps: [{ when: { kind: 'start' }, action: { kind: 'keys', keys: ['CLR'], held: 'yes' } }] }
  expect(scenarioProblems(malformed).join('; ')).toMatch(/held must be true or false/)
})
