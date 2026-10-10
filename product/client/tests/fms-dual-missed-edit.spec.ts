import { expect, logicTest as test } from './isolated-client-test'
import { profileById } from '../src/fmsCdu/profile'
import { dualComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { ScenarioRunner } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'

// A synchronized missed request is not an EXEC (M300 3-24). Protect the actual pending edit and ownership,
// rather than comparing two post-TOGA plans that could have both lost the edit. EXEC keeps today's MOD legs.
for (const [editorSide, release, sameSide] of [[1, 'EXEC', false], [1, 'ERASE', false], [2, 'EXEC', false],
  [2, 'ERASE', false], [1, 'EXEC', true], [2, 'EXEC', true]] as const) {
  test(`${sameSide ? 'same-side' : 'peer'} TOGA preserves FMS${editorSide} MOD and edit ownership until real ${release}`, () => {
    const { system } = dualComposition(Date.UTC(2026, 8, 27, 14), { profile: profileById('lab-airline-vnav') })
    const editor = system.computers[editorSide - 1], other = system.computers[2 - editorSide]
    const issuer = sameSide ? editor : other
    system.selectGuidance(sameSide ? editorSide : editorSide === 1 ? 2 : 1)
    const scenario = SCENARIO_LIBRARY.find(entry => entry.id === 'gps-lost-before-faf')!
    new ScenarioRunner({ ...scenario, steps: scenario.steps.slice(0, 3) }, issuer, undefined, system.simulator)
    const activeBefore = structuredClone(editor.activeRoute)
    expect(activeBefore.legs.some(leg => leg.kind !== 'disco' && leg.source !== 'MISSED')).toBe(true)
    expect(editor.modify(route => { route.origin = 'EDIT1533' })).toBe(true)
    const pendingBefore = structuredClone(editor.route)
    expect(pendingBefore.origin).toBe('EDIT1533')
    expect(other.modify(route => { route.origin = 'REFUSED' })).toBe(false)
    expect(issuer.goAround()).toBe(true)
    expect(editor.routeStatus).toBe('MOD')
    expect(editor.route).toEqual(pendingBefore)
    const activeAfterToga = system.computers.map(unit => structuredClone(unit.activeRoute))
    for (const unit of system.computers) {
      expect(unit.missedApproachRequested).toBe(true)
      expect(unit.approachArmed).toBe(false)
    }
    expect(editor.goArounds).toBe(sameSide ? 1 : 0)
    expect(other.goArounds).toBe(sameSide ? 0 : 1)
    expect(other.modify(route => { route.origin = 'STILL REFUSED' })).toBe(false)
    expect(editor.modify(route => { route.dest = 'KEPT1533' })).toBe(true)
    const amended = structuredClone(editor.route)
    expect(amended).toMatchObject({ origin: 'EDIT1533', dest: 'KEPT1533' })
    if (release === 'EXEC') editor.press('EXEC')
    else editor.eraseModification()
    for (const [index, unit] of system.computers.entries()) {
      expect(unit.routeStatus).toBe('ACT')
      if (release === 'EXEC') expect(unit.activeRoute).toEqual(amended)
      else {
        expect(unit.activeRoute.origin).toBe(activeBefore.origin)
        expect(unit.activeRoute).toEqual(activeAfterToga[index])
      }
    }
    expect(other.modify(route => { route.origin = 'AFTER RELEASE' })).toBe(true)
    expect(other.route.origin).toBe('AFTER RELEASE')
  })
}

// A failed computer cannot process a synchronized request; keep its route/edit isolated, without a peer TOGA.
for (const side of [1, 2] as const) {
  test(`FMS${side} TOGA leaves the failed peer's pending MOD and ACT untouched`, () => {
    const { system } = dualComposition(Date.UTC(2026, 8, 27, 14), { profile: profileById('lab-airline-vnav') })
    const own = system.computers[side - 1], peer = system.computers[2 - side]
    system.selectGuidance(side)
    const scenario = SCENARIO_LIBRARY.find(entry => entry.id === 'gps-lost-before-faf')!
    new ScenarioRunner({ ...scenario, steps: scenario.steps.slice(0, 3) }, own, undefined, system.simulator)
    expect(peer.modify(route => { route.origin = 'FAILED EDIT' })).toBe(true)
    const active = structuredClone(peer.activeRoute), pending = structuredClone(peer.route)
    peer.setCondition('fmsFail', true)
    expect(own.goAround()).toBe(true)
    expect(peer.routeStatus).toBe('MOD')
    expect(peer.activeRoute).toEqual(active)
    expect(peer.route).toEqual(pending)
    expect(peer.missedApproachRequested).toBe(false)
    expect(peer.goArounds).toBe(0)
    expect(own.goArounds).toBe(1)
  })
}

