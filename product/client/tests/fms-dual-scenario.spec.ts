import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { START_STATES } from '../src/fmsCdu/kbtvDemo'
import { profileById } from '../src/fmsCdu/profile'
import { advanceTicks, ScenarioRunner, scenarioStart, TICK_SECONDS, type Scenario } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'

// The single-computer library owner cannot detect peer progress/alert ordering or a missed request that leaves
// the synchronized peer on the approach route (#1533). Exercise the bench's actual two-computer composition.
function runDual(scenario: Scenario, side: 1 | 2 = 1, independent = false) {
  let now = scenarioStart(scenario) ?? Date.UTC(2026, 8, 27, 14)
  const system = new DualFmsSystem(() => new Date(now), { profile: profileById(scenario.profile) })
  let playback = scenario
  if (side === 2 && scenario.start) {
    // The bench starts the common generator/aircraft on FMS1, then transfers the completed setup before choosing FMS2.
    const [one, two] = system.computers
    expect(one.compute(() => {
      const result = START_STATES[scenario.start!].setUp(one, system.flights[0])
      one.dualOperation?.settingsChanged(); one.dualOperation?.finishEdit(true); two.observeAircraft(one)
      return result
    })).toEqual({ ready: true })
    playback = { ...scenario, start: undefined }
  }
  system.selectGuidance(side)
  const fms = system.computers[side - 1]
  const runner = new ScenarioRunner(playback, fms, undefined, system.simulator)
  if (independent) fms.setCondition('independent', true)
  const limit = Math.ceil(scenario.maxSeconds / TICK_SECONDS) + 2
  for (let tick = 0; !runner.finished && tick < limit; tick++) {
    advanceTicks(1, ms => { now += ms }, system, runner)
  }
  return { system, runner }
}

for (const scenario of SCENARIO_LIBRARY) {
  test(`built-in scenario ${scenario.id} passes with two synchronized computers`, () => {
    const { runner } = runDual(scenario)
    expect(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass'), scenario.id).toEqual([])
    expect(runner.passed, scenario.id).toBe(true)
  })
}

// The library owns FMS1/SYNC. These additional selections protect the local refusal/roll-withdrawal boundary,
// including INDEPENDENT where the valid peer alert is not broadcast (M300 E-17, 3-24/25).
for (const [side, independent] of [[2, false], [1, true], [2, true]] as const) {
  test(`off-track TDN refusal on FMS${side} ${independent ? 'independent' : 'synchronized'} retains its geometry and heading checks`, () => {
    const scenario = SCENARIO_LIBRARY.find(entry => entry.id === '87n-b-tdn-off-track')!
    const { system, runner } = runDual(scenario, side, independent)
    expect(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass')).toEqual([])
    expect(runner.passed).toBe(true)
    expect(system.computers[side - 1].hover.refusedReason).toBe('OFF FINAL TRACK')
    expect(system.mode).toBe(independent ? 'INDEPENDENT' : 'SYNC')
  })
}

// Library playback selects FMS1/SYNC only. This boundary owns the resulting TOGA plan on either guidance side,
// with INDEPENDENT isolation and S300's early-missed lateral continuation as positive controls (M300 3-24, 7-15/16).
for (const profile of ['lab-airline-vnav', 'cma9000-s300-heli-civil']) for (const side of [1, 2] as const) for (const independent of [false, true]) {
  test(`${profile} TOGA on FMS${side} ${independent ? 'independent' : 'synchronized'} preserves the applicable peer plan`, () => {
    const system = new DualFmsSystem(() => new Date(Date.UTC(2026, 8, 27, 14)), { profile: profileById(profile) })
    system.selectGuidance(side)
    const own = system.computers[side - 1], peer = system.computers[2 - side]
    const scenario = SCENARIO_LIBRARY.find(entry => entry.id === 'gps-lost-before-faf')!
    const runner = new ScenarioRunner({ ...scenario, steps: scenario.steps.slice(0, 3) }, own, undefined, system.simulator)
    expect(runner.outcome).toBe('no checks')
    const before = structuredClone(own.activeRoute)
    expect(peer.activeRoute).toEqual(before)
    expect(before.legs.some(leg => leg.kind !== 'disco' && leg.source === 'MISSED')).toBe(true)
    if (independent) own.setCondition('independent', true)
    expect(own.goAround()).toBe(true)
    expect(own.missedApproachRequested).toBe(true)
    expect(own.approachArmed).toBe(false)
    if (profile === 'lab-airline-vnav') expect(own.activeRoute.legs[0]).toMatchObject({ source: 'MISSED' })
    else expect(own.activeRoute.legs.filter(leg => leg.kind === 'disco' || leg.source !== 'MISSED'))
      .toEqual(before.legs.filter(leg => leg.kind === 'disco' || leg.source !== 'MISSED'))
    expect(own.activeRoute.hold).toMatchObject({ fix: 'UL502', status: 'ARMED' })
    expect(peer.activeRoute).toEqual(independent ? before : own.activeRoute)
    expect(peer.missedApproachRequested).toBe(!independent)
    expect(system.mode).toBe(independent ? 'INDEPENDENT' : 'SYNC')
  })
}
