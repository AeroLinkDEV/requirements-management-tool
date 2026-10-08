import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { profileById } from '../src/fmsCdu/profile'
import { ScenarioRunner } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { runDual } from './fixtures/fms-dual-scenario'

// Fast retains the two previously failing cases; the complete admission sweep is Full-only.
for (const id of ['gps-lost-before-faf', '87n-b-tdn-off-track']) {
  test(`dual regression ${id} passes with synchronized computers`, () => {
    const { runner } = runDual(SCENARIO_LIBRARY.find(entry => entry.id === id)!)
    expect(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass')).toEqual([])
    expect(runner.passed).toBe(true)
  })
}

// The Full library owns FMS1/SYNC. These selections protect refusal/roll withdrawal; INDEPENDENT already passes
// main and is a same-tick preservation control, not an original #1533 reproduction (M300 E-17, 3-24/25).
for (const [side, independent] of [[2, false], [1, true], [2, true]] as const) {
  test(`off-track TDN refusal on FMS${side} ${independent ? 'independent' : 'synchronized'} retains its geometry and heading checks`, () => {
    const scenario = SCENARIO_LIBRARY.find(entry => entry.id === '87n-b-tdn-off-track')!
    const { system, runner } = runDual(scenario, side, independent)
    expect(runner.results.filter(result => result.status !== 'done' && result.status !== 'pass')).toEqual([])
    expect(runner.passed).toBe(true)
    expect(system.computers[side - 1].hover.refusedReason).toBe('OFF FINAL TRACK')
    const alert = scenario.steps.findIndex(step => step.action.kind === 'expectAlert' && step.action.text === 'TDN NOT POSSIBLE')
    const refusal = scenario.steps.findIndex(step => step.action.kind === 'expectHover' && step.action.reason === 'OFF FINAL TRACK')
    expect(runner.results[alert].status).toBe('pass')
    expect(runner.results[refusal].status).toBe('pass')
    if (independent) expect(runner.results[refusal].at).toBe(runner.results[alert].at)
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
