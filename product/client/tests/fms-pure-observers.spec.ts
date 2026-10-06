import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { aircraftData, fmsOutputs } from '../src/fmsCdu/efis'
import { FlightSimulator } from '../src/fmsCdu/flight'
import { offset, type Leg } from '../src/fmsCdu/fmsModel'
import { ScenarioRunner, TICK_SECONDS, advanceTicks, runHeadless, type Scenario } from '../src/fmsCdu/scenario'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// #1518: reading the FMS (the CDU screen, the bus outputs, the guidance) must never change it. The live bench renders
// after every callback, so each case runs twice: once rendered the way the bench renders, once with no read at all.
// Both must give the bench's behaviour, so a headless run, a state digest or an analyzer view sees the same simulation.
const START = Date.UTC(2026, 8, 27, 14, 0, 0)

/** What the bench reads after each callback: the CDU screen and lamps, and the guidance computer's bus and air data. */
const render = (fms: ScriptedFms, sim: FlightSimulator) => {
  fms.screen(); fms.lamps(); fmsOutputs(fms, sim); aircraftData(fms, sim)
}

const single = () => {
  let now = START
  const fms = new ScriptedFms(() => new Date(now))
  const sim = new FlightSimulator(fms)
  return { fms, sim, advance: (ms: number) => { now += ms } }
}
const press = (fms: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) fms.press(fn) }
const legs = (count: number, from: ScriptedFms): Leg[] =>
  Array.from({ length: count }, (_, i) => ({ kind: 'wpt', ident: `PO${String(i + 1).padStart(2, '0')}`, position: offset(from.position, 0, 5 * (i + 1)) }))

// Owner: the CDU page index is settled by actions, not by drawing the screen. A LEGS view on page 3/3 of a modification
// that ERASE shrinks to the 2-page active route shows 2/2 on the bench; NEXT and the line selects then act on that page.
test('a LEGS route that shrinks under the view pages and line-selects from the page shown, with or without a render', () => {
  const drive = (rendered: boolean, then: CduFunction) => {
    const { fms, sim } = single()
    fms.replaceLegs(legs(7, fms)); fms.press('EXEC')
    fms.replaceLegs(legs(12, fms))
    press(fms, 'LEGS', 'NEXT', 'NEXT')
    if (rendered) render(fms, sim)
    fms.press('LSK6L') // ERASE: the view is back on the 7-leg active route, 2 pages.
    if (rendered) render(fms, sim)
    fms.press(then)
    const lines = screenText(fms.screen())
    return { title: lines[0], scratch: lines[SCRATCHPAD_LINE].trim() }
  }
  for (const then of ['NEXT', 'LSK1L'] as const) expect(drive(false, then), then).toEqual(drive(true, then))
  // ERASE left the view on page 2/2: NEXT wraps to 1/2, and LSK1L copies the sixth leg, the first one shown there.
  expect(drive(false, 'NEXT').title).toMatch(/^ACT RTE 1 LEGS\s+1\/2$/)
  expect(drive(false, 'LSK1L')).toMatchObject({ title: expect.stringMatching(/\s2\/2$/), scratch: 'PO06' })
})

// Owner: a moving waypoint's rendezvous is determined when the action that put it in the route ends (M300 11-37), so the
// 10-second recompute schedule runs from the EXEC, not from whenever the route was first drawn.
test('the rendezvous with a moving waypoint is determined at the EXEC, rendered or not, and keeps its 10 s schedule', () => {
  const drive = (rendered: boolean) => {
    const { fms, sim, advance } = single()
    Object.assign(fms.wind, { direction: 0, speed: 0 })
    fms.defineMoving('SHIP1', offset(fms.position, 30, 12), 90, 40)
    expect(fms.directTo('SHIP1')).toBeUndefined()
    const executed = fms.now.getTime()
    fms.press('EXEC')
    const computed: number[] = []
    for (let t = 0; t < 25; t += 1) {
      if (rendered) render(fms, sim)
      advance(1000); sim.step(1)
      const at = fms.rendezvousFor(fms.activeRoute, 0)!.computedAt
      if (computed.at(-1) !== at) computed.push(at)
    }
    return { schedule: computed.map(at => (at - executed) / 1000), position: fms.truePosition }
  }
  const headless = drive(false)
  expect(headless).toEqual(drive(true))
  expect(headless.schedule).toEqual([0, 10, 20])
})

// Owner: the laboratory FMS-failure reversion latches at the action that failed the FMS, paused or flying, whether or
// not anything reads the guidance before the next callback. Its readers are the bench, the map and the bus outputs.
test('an FMS failure latches its reversion at the toggle without a render, paused and flying, on the dual bench and headless', () => {
  const failure = (sim: FlightSimulator) => sim.modeEvents.filter(event => event.event === 'FMS FAILURE').map(event => ({ ...event, at: event.at.getTime() }))
  const dual = (rendered: boolean, flying: boolean) => {
    let now = START
    const system = new DualFmsSystem(() => new Date(now))
    const callback = () => {
      now += TICK_SECONDS * 1000
      if (flying) system.step(TICK_SECONDS); else system.tick()
      if (rendered) render(system.computers[system.guidanceSide - 1], system.simulator)
    }
    for (let i = 0; i < 4; i += 1) callback()
    const toggledAt = now
    system.computers[0].setCondition('fmsFail', true)
    if (rendered) render(system.computers[0], system.simulator)
    for (let i = 0; i < 4; i += 1) callback()
    return { toggledAt, failure: failure(system.flights[0]) }
  }
  for (const flying of [false, true]) {
    const headless = dual(false, flying)
    expect(headless, flying ? 'flying' : 'paused').toEqual(dual(true, flying))
    expect(headless.failure.map(event => event.at), flying ? 'flying' : 'paused').toEqual([headless.toggledAt])
  }

  // A scenario's condition step fails the FMS inside the runner's poll: headless, the latch is the bench's too.
  const scenario: Scenario = {
    id: 'pure-observer-fms-fail', title: 'FMS failure while flying', objective: 'Reversion latch timing', maxSeconds: 6,
    steps: [
      { when: { kind: 'time', seconds: 2 }, action: { kind: 'condition', condition: 'fmsFail', on: true } },
      { when: { kind: 'after', seconds: 2 }, action: { kind: 'expectLamp', lamp: 'FAIL', lit: true } },
    ],
  }
  const rendered = (() => {
    let now = START
    const fms = new ScriptedFms(() => new Date(now))
    const sim = new FlightSimulator(fms)
    const runner = new ScenarioRunner(scenario, fms, undefined, sim)
    while (!runner.finished) { advanceTicks(1, ms => { now += ms }, sim, runner); render(fms, sim) }
    return failure(sim)
  })()
  const headless = failure(runHeadless(scenario, START).sim)
  expect(headless).toEqual(rendered)
  expect(headless.map(event => event.at)).toEqual([START + 2000])
})
