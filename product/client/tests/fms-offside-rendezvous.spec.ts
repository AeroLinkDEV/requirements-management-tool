import { ScenarioRunner } from '../src/fmsCdu/scenario'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import { expect, logicTest as test } from './isolated-client-test'
import { dualComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { distanceNm, offset } from '../src/fmsCdu/fmsModel'
import type { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { fmsOutputs } from '../src/fmsCdu/efis'

const START = Date.UTC(2026, 8, 27, 14)
function setup(side: 1 | 2, independent = false) {
  const { system, plant } = dualComposition(START)
  const kernel = new FmsKernel(plant)
  const [one] = system.computers
  one.defineMoving('SHIP1', offset(one.position, 30, 12), 90, 40)
  one.directTo('SHIP1'); one.press('EXEC')
  system.selectGuidance(side)
  system.simulator.selectHeading(270)
  if (independent) one.setCondition('independent', true)
  const fly = (seconds: number) => {
    kernel.advance(seconds * 4)
  }
  return { system, own: system.computers[2 - side], selected: system.computers[side - 1], fly }
}

const recalled = (unit: ScriptedFms) => structuredClone(unit.recallList).filter(message => message.text === 'RENDEZVOUS UNACHIEVABLE')
const far = (unit: ScriptedFms) => {
  unit.defineMoving('FAR1', offset(unit.position, 0, 600), 0, 0)
  unit.directTo('FAR1'); unit.press('EXEC')
}

// #1564 primary delayed-episode regression: the existing same-deadline rows below cannot see a peer whose
// first healthy computation establishes a different 10-second phase. CLR acknowledges the synchronized alert,
// but is not the end of the still-unachievable cause. No private warning/cache state is used as an oracle.
for (const side of [1, 2] as const) {
  test(`SYNC FMS${side} CLR does not reopen the same rendezvous episode at its recovered peer's later deadline`, () => {
    const { system, plant } = dualComposition(START)
    const kernel = new FmsKernel(plant)
    const origin = system.computers[side - 1], peer = system.computers[2 - side]
    const fly = (seconds: number) => { kernel.advance(seconds * 4) }
    system.selectGuidance(side)
    system.simulator.selectHeading(270)
    origin.defineMoving('FAR1', offset(origin.position, 0, 480), 0, 0)
    origin.directTo('FAR1'); origin.press('EXEC')
    expect(system.mode).toBe('SYNC')
    expect(origin.rendezvousFor(origin.activeRoute, 0)).toMatchObject({ achievable: true, computedAt: START })
    expect(peer.rendezvousFor(peer.activeRoute, 0)).toMatchObject({ achievable: true, computedAt: START })
    fly(3)
    peer.setCondition('fmsFail', true)
    expect(system.mode).toBe('INDEPENDENT')
    fly(1)
    peer.setCondition('fmsFail', false)
    origin.dualOperation!.requestMode('SYNC'); origin.dualOperation!.confirmMode(true)
    expect(system.mode).toBe('SYNC')
    expect(origin.rendezvousFor(origin.activeRoute, 0)).toMatchObject({ achievable: true, computedAt: START })
    expect(peer.rendezvousFor(peer.activeRoute, 0)).toMatchObject({ achievable: true, computedAt: START + 4_000 })
    fly(1)
    for (const unit of system.computers) unit.defineMoving('FAR1', offset(unit.position, 0, 600), 0, 0)
    fly(5)
    expect(origin.now.getTime()).toBe(START + 10_000)
    expect(origin.rendezvousFor(origin.activeRoute, 0)).toMatchObject({ achievable: false, computedAt: START + 10_000 })
    expect(peer.rendezvousFor(peer.activeRoute, 0)).toMatchObject({ achievable: true, computedAt: START + 4_000 })
    for (const unit of system.computers) {
      expect(recalled(unit)).toHaveLength(1)
      expect(unit.lamps().has('MSG')).toBe(true)
    }
    const acknowledged = system.computers.map(unit => structuredClone(recalled(unit)))
    origin.press('CLR')
    for (const unit of system.computers) expect(unit.lamps().has('MSG')).toBe(false)
    // A crew mode transition before the delayed peer's deadline is not physical recovery.
    origin.dualOperation!.requestMode('INDEPENDENT'); origin.dualOperation!.confirmMode(true)
    expect(system.mode).toBe('INDEPENDENT')
    origin.press('CLR'); peer.press('CLR')
    origin.dualOperation!.requestMode('SYNC'); origin.dualOperation!.confirmMode(true)
    expect(system.mode).toBe('SYNC')
    expect(peer.rendezvousFor(peer.activeRoute, 0)).toMatchObject({ achievable: true, computedAt: START + 4_000 })
    fly(4)
    expect(peer.rendezvousFor(peer.activeRoute, 0)).toMatchObject({ achievable: false, computedAt: START + 14_000 })
    for (let index = 0; index < 2; index++) {
      expect(recalled(system.computers[index])).toEqual(acknowledged[index])
      expect(system.computers[index].lamps().has('MSG')).toBe(false)
    }
    // Later deadlines do not turn acknowledgment into recurrence either.
    fly(20)
    for (let index = 0; index < 2; index++) {
      expect(recalled(system.computers[index])).toEqual(acknowledged[index])
      expect(system.computers[index].lamps().has('MSG')).toBe(false)
    }
  })
}

// Primary owner: periodic offside rendezvous computations (#1523, M300 11-37). Existing single/EXEC-purity owners
// do not fly an unselected computer through successive refresh deadlines. Different independent targets and EXEC
// epochs also reject borrowing the selected computer's cache instead of computing the receiving computer's plan.
for (const side of [1, 2] as const) for (const independent of [false, true]) {
  test(`FMS${3 - side} keeps its own 10s rendezvous cadence while FMS${side} guides ${independent ? 'independently' : 'in SYNC'}, then becomes guidance`, () => {
    const { system, own, selected, fly } = setup(side, independent)
    let ident = 'SHIP1'
    if (independent) {
      fly(3)
      ident = 'SHIP2'
      own.defineMoving(ident, offset(own.position, 250, 18), 120, 35)
      own.directTo(ident); own.press('EXEC')
    }
    const initial = structuredClone(own.rendezvousFor(own.activeRoute, 0)!)
    expect(initial).toMatchObject({ achievable: true, condition: 1 })
    expect(initial.ttg!).toBeGreaterThan(90)
    fly(9.75)
    expect(own.rendezvousFor(own.activeRoute, 0)).toEqual(initial)
    fly(0.25)
    const refreshed = structuredClone(own.rendezvousFor(own.activeRoute, 0)!)
    expect(refreshed.computedAt).toBe(initial.computedAt + 10_000)
    expect(refreshed.ttg).not.toBe(initial.ttg)
    expect(distanceNm(refreshed.position!, initial.position!)).toBeGreaterThan(1e-6)
    expect(refreshed.distanceNm).toBeCloseTo(distanceNm(own.position, refreshed.position!), 7)
    if (independent) {
      expect(own.activeRoute.legs[0]).toMatchObject({ ident: 'SHIP2' })
      expect(selected.activeRoute.legs[0]).toMatchObject({ ident: 'SHIP1' })
      expect(refreshed.computedAt).not.toBe(selected.rendezvousFor(selected.activeRoute, 0)!.computedAt)
      expect(distanceNm(refreshed.position!, selected.rendezvousFor(selected.activeRoute, 0)!.position!)).toBeGreaterThan(1)
    }
    fly(10)
    const beforeSwitch = structuredClone(own.rendezvousFor(own.activeRoute, 0)!)
    expect(beforeSwitch.computedAt).toBe(initial.computedAt + 20_000)
    system.selectGuidance(side === 1 ? 2 : 1)
    expect(own.rendezvousFor(own.activeRoute, 0)).toEqual(beforeSwitch)
    fly(9.75)
    expect(own.rendezvousFor(own.activeRoute, 0)).toEqual(beforeSwitch)
    fly(0.25)
    expect(own.rendezvousFor(own.activeRoute, 0)!.computedAt).toBe(initial.computedAt + 30_000)
    expect(system.mode).toBe(independent ? 'INDEPENDENT' : 'SYNC')
  })
}

// M300 3-24 synchronizes alert messages. A pending peer alert must consume the local warned episode too,
// so acknowledging it does not cause another local alert when the next refresh deadline arrives.
for (const side of [1, 2] as const) for (const independent of [false, true]) {
  test(`FMS${side} and its peer annunciate an unachievable rendezvous once ${independent ? 'independently' : 'in SYNC'}`, () => {
    const { system, selected, own, fly } = setup(side, independent)
    far(selected)
    if (independent) far(own)
    fly(0.25)
    for (const unit of system.computers) {
      expect(recalled(unit)).toHaveLength(1)
      expect(unit.lamps().has('MSG')).toBe(true)
      expect(unit.rendezvousFor(unit.activeRoute, 0)).toMatchObject({ achievable: false, condition: 1 })
    }
    system.flights.forEach((flight, index) => expect(fmsOutputs(system.computers[index], flight).rollCommand.status).toBe('NCD'))
    selected.press('CLR')
    if (independent) own.press('CLR')
    fly(20)
    for (const unit of system.computers) {
      expect(recalled(unit)).toHaveLength(1)
      expect(unit.lamps().has('MSG')).toBe(false)
    }
    selected.defineMoving('FAR1', offset(selected.position, 0, 480), 0, 0)
    own.defineMoving('FAR1', offset(own.position, 0, 480), 0, 0)
    fly(10)
    for (const unit of system.computers) expect(unit.rendezvousFor(unit.activeRoute, 0)!.achievable).toBe(true)
    selected.defineMoving('FAR1', offset(selected.position, 0, 600), 0, 0)
    own.defineMoving('FAR1', offset(own.position, 0, 600), 0, 0)
    fly(10)
    for (const unit of system.computers) {
      expect(recalled(unit)).toHaveLength(2)
      expect(unit.lamps().has('MSG')).toBe(true)
    }
  })
}

for (const side of [1, 2] as const) for (const role of ['driver', 'observer'] as const) {
  for (const unavailable of ['failure', 'OFF', 'TEST'] as const) {
    test(`FMS${role === 'driver' ? side : 3 - side} ${role} retains its rendezvous while ${unavailable}`, () => {
      const { selected, own, fly } = setup(side, true)
      const unit = role === 'driver' ? selected : own
      const initial = structuredClone(unit.rendezvousFor(unit.activeRoute, 0)!)
      expect(initial).toMatchObject({ achievable: true, condition: 1 })
      expect(initial.ttg!).toBeGreaterThan(90)
      if (unavailable === 'failure') unit.setCondition('fmsFail', true)
      else {
        unit.powerOff()
        if (unavailable === 'TEST') unit.powerOn('WARM', true)
      }
      expect(unit.hasCondition('fmsFail')).toBe(true)
      fly(unavailable === 'TEST' ? 4 : 12)
      expect(unit.hasCondition('fmsFail')).toBe(true)
      expect(unit.rendezvousFor(unit.activeRoute, 0)).toEqual(initial)
      expect(recalled(unit)).toHaveLength(0)
    })
  }

  test(`FMS${role === 'driver' ? side : 3 - side} ${role} re-determines a frozen rendezvous after a short failure`, () => {
    const { selected, own, fly } = setup(side, true)
    const unit = role === 'driver' ? selected : own
    unit.defineMoving('NEAR1', offset(unit.position, 0, 0.5), 0, 0)
    unit.directTo('NEAR1'); unit.press('EXEC')
    const before = structuredClone(unit.rendezvousFor(unit.activeRoute, 0)!)
    expect(before.ttg!).toBeGreaterThan(0)
    expect(before.ttg!).toBeLessThan(60)
    unit.setCondition('fmsFail', true)
    fly(1)
    expect(unit.rendezvousFor(unit.activeRoute, 0)).toEqual(before)
    unit.setCondition('fmsFail', false)
    const recovered = unit.rendezvousFor(unit.activeRoute, 0)!
    expect(recovered.computedAt).toBe(before.computedAt + 1000)
    expect(recovered.ttg).not.toBe(before.ttg)
    expect(recovered.distanceNm).toBeCloseTo(distanceNm(unit.position, recovered.position!), 7)
  })

  for (const previouslyWarned of [false, true]) test(`FMS${role === 'driver' ? side : 3 - side} ${role} power cycle annunciates an unachievable rendezvous after TEST ${previouslyWarned ? 'with prior warning' : 'before its first refresh'}`, () => {
    const { selected, own, fly } = setup(side, true)
    const unit = role === 'driver' ? selected : own
    far(unit)
    if (previouslyWarned) fly(0.25)
    expect(recalled(unit)).toHaveLength(previouslyWarned ? 1 : 0)
    unit.powerOff(); fly(0.25)
    const prior = structuredClone(unit.rendezvousFor(unit.activeRoute, 0)!)
    unit.powerOn('WARM', true)
    expect(unit.powerState).toBe('TEST')
    expect(recalled(unit)).toHaveLength(0)
    fly(4.75)
    expect(unit.powerState).toBe('TEST')
    expect(unit.rendezvousFor(unit.activeRoute, 0)).toEqual(prior)
    expect(recalled(unit)).toHaveLength(0)
    fly(0.5)
    expect(unit.powerState).toBe('ON')
    expect(recalled(unit)).toHaveLength(1)
    expect(unit.rendezvousFor(unit.activeRoute, 0)!.computedAt).toBeGreaterThan(prior.computedAt)
    expect(unit.lamps().has('MSG')).toBe(true)
    unit.press('CLR'); fly(20)
    expect(recalled(unit)).toHaveLength(1)
  })
}

// The production route-entry computation also settles predictions. Failure must prevent this retained first solve,
// rather than only disabling the periodic caller; a public observer may not calculate a replacement while failed.
for (const side of [1, 2] as const) {
  test(`failed offside FMS${3 - side} does not determine a newly pending MOD until recovery`, () => {
    const { own, fly } = setup(side, true)
    own.defineMoving('WAIT1', offset(own.position, 250, 18), 120, 35)
    own.setCondition('fmsFail', true)
    own.replaceLegs([{ kind: 'wpt', ident: 'WAIT1' }])
    expect(own.routeStatus).toBe('MOD')
    expect(own.rendezvousFor(own.route, 0)).toBeNull()
    fly(1)
    expect(own.rendezvousFor(own.route, 0)).toBeNull()
    own.setCondition('fmsFail', false)
    const recovered = own.rendezvousFor(own.route, 0)!
    expect(recovered).toMatchObject({ computedAt: START + 1000, condition: 4, achievable: true })
    expect(recovered.distanceNm).toBeCloseTo(distanceNm(own.position, recovered.position!), 7)
  })
}

for (const side of [1, 2] as const) {
  test(`offside FMS${3 - side} also refreshes its own modified-route rendezvous`, () => {
    const { own, selected, fly } = setup(side, true)
    fly(3)
    own.defineMoving('SHIP2', offset(own.position, 250, 18), 120, 35)
    own.directTo('SHIP2')
    const initial = structuredClone(own.rendezvousFor(own.route, 0)!)
    expect(own.routeStatus).toBe('MOD')
    expect(initial).toMatchObject({ condition: 4, achievable: true })
    expect(initial.ttg!).toBeGreaterThan(90)
    fly(10)
    const refreshed = own.rendezvousFor(own.route, 0)!
    expect(refreshed.computedAt).toBe(initial.computedAt + 10_000)
    expect(refreshed.computedAt).not.toBe(selected.rendezvousFor(selected.activeRoute, 0)!.computedAt)
    expect(refreshed.ttg).not.toBe(initial.ttg)
    expect(refreshed.distanceNm).toBeCloseTo(distanceNm(own.position, refreshed.position!), 7)
  })

  // An unconditional offside refresh would pass the regression rows but violate the manual's one-minute freeze.
  test(`offside FMS${3 - side} retains its rendezvous within one minute while the selected computer refreshes`, () => {
    const { own, selected, fly } = setup(side, true)
    own.defineMoving('NEAR1', offset(own.position, 0, 0.5), 0, 0)
    own.directTo('NEAR1'); own.press('EXEC')
    const frozen = structuredClone(own.rendezvousFor(own.activeRoute, 0)!)
    const selectedAt = selected.rendezvousFor(selected.activeRoute, 0)!.computedAt
    expect(frozen).toMatchObject({ achievable: true, condition: 1 })
    expect(frozen.ttg!).toBeGreaterThan(0)
    expect(frozen.ttg!).toBeLessThan(60)
    fly(20)
    expect(own.rendezvousFor(own.activeRoute, 0)).toEqual(frozen)
    expect(selected.rendezvousFor(selected.activeRoute, 0)!.computedAt).toBe(selectedAt + 20_000)
  })
}

// CONDITIONAL SOURCE PROPOSAL, not executed. R13/#1564: an advisory may be visible while an alert is pending;
// there is no scratchpad-priority oracle here. CLR on that advisory must not acknowledge the latest alert.
// The literal library trace supplies genuine shared GPS loss/DR antecedents, not a synthetic raised alert.
for (const side of [1, 2] as const) {
  test(`SYNC FMS${side} CLR preserves the latest GPS alert until CLR on that alert after library GPS loss into DR`, () => {
    const scenario = SCENARIO_LIBRARY.find(row => row.id === 'dead-reckoning')!
    const { system, plant, settleStart } = dualComposition(START)
    const [one] = system.computers
    // Same scenario, physical origin, default profile, UTC and runner APIs as the dual census; CLR side mirrors.
    const runner = new ScenarioRunner(scenario, one, { variant: 'census', cycle: one.activeCycle.id }, system.flights[0], settleStart)
    const kernel = new FmsKernel(plant, runner)
    const selected = system.computers[side - 1]
    const shown = (unit: ScriptedFms) => screenText(unit.screen())[SCRATCHPAD_LINE].trim()
    const gpsRecall = (unit: ScriptedFms) => structuredClone(unit.recallList).filter(message => message.text === 'GPS NAV LOST')
    const textColor = (unit: ScriptedFms) => unit.screen()[SCRATCHPAD_LINE].filter(cell => cell.ch !== ' ').map(cell => cell.color)
    expect(runner.problems).toEqual([])
    expect(system.mode).toBe('SYNC')
    expect(kernel.advance(479)).toBe(479)
    expect(kernel.unitClockMs).toBe(START + 119_750)
    expect(one.hasCondition('apirsFail')).toBe(true)
    expect(one.hasCondition('dvsFail')).toBe(true)
    expect(one.hasCondition('dmeOutage')).toBe(true)
    expect(one.hasCondition('gpsLost')).toBe(false)
    for (const unit of system.computers) {
      expect(unit.hasCondition('fmsFail')).toBe(false)
      expect(unit.localNavigationSolution.mode).toBe('GPS')
      expect(unit.lamps().has('POS')).toBe(false)
      expect(gpsRecall(unit)).toHaveLength(0)
    }
    expect(kernel.advance(1)).toBe(1)
    expect(kernel.unitClockMs).toBe(START + 120_000)
    expect(one.hasCondition('gpsLost')).toBe(true)
    for (const unit of system.computers) {
      expect(unit.localNavigationSolution.mode).toBe('DR')
      expect(unit.navState.mode).toBe('DR')
      expect(unit.lamps().has('POS')).toBe(true)
      expect(unit.lamps().has('MSG')).toBe(true)
      expect(gpsRecall(unit)).toEqual([{ text: 'GPS NAV LOST', alert: true }])
      expect(unit.recallList.some(message => message.text === 'FMS NAV IN DR')).toBe(false)
      // Accept either presentation: current docs declare no priority beyond the latest alert.
      expect(['GPS NAV LOST', 'FMS NAV IN DR']).toContain(shown(unit))
      expect(textColor(unit)).toEqual(Array(10).fill(shown(unit) === 'GPS NAV LOST' ? 'amber' : 'white'))
    }
    const atLoss = system.computers.map(unit => structuredClone(unit.recallList))
    expect(atLoss[0]).toEqual(atLoss[1])
    const pendingGps = gpsRecall(selected)
    if (shown(selected) === 'FMS NAV IN DR') {
      expect(textColor(selected)).toEqual(Array(10).fill('white'))
      selected.press('CLR')
      // Primary intended fail-before boundary, if actual causal review confirms this public antecedent:
      // clearing an advisory is not CLR on GPS NAV LOST and cannot acknowledge that pending alert.
      expect(selected.lamps().has('MSG')).toBe(true)
      for (const unit of system.computers) expect(unit.lamps().has('MSG')).toBe(true)
      expect(shown(selected)).toBe('')
      selected.press('CLR')
      expect(shown(selected)).toBe('GPS NAV LOST')
    }
    expect(shown(selected)).toBe('GPS NAV LOST')
    expect(textColor(selected)).toEqual(Array(10).fill('amber'))
    selected.press('CLR')
    expect(shown(selected)).toBe('')
    for (let index = 0; index < 2; index++) {
      expect(system.computers[index].lamps().has('MSG')).toBe(false)
      expect(system.computers[index].recallList).toEqual(atLoss[index])
    }
    expect(kernel.advance(4)).toBe(4)
    for (const unit of system.computers) {
      expect(unit.lamps().has('MSG')).toBe(false)
      expect(gpsRecall(unit)).toEqual(pendingGps)
    }
    // Keep every original scenario step, including physical GPS recovery at 720s. Other genuine alerts may occur.
    kernel.advance(2_396)
    expect(kernel.unitClockMs).toBe(START + 720_000)
    expect(runner.outcome).toBe('passed')
    expect(one.hasCondition('gpsLost')).toBe(false)
    for (let index = 0; index < 2; index++) {
      const unit = system.computers[index]
      expect(unit.localNavigationSolution.mode).toBe('GPS')
      expect(unit.lamps().has('POS')).toBe(false)
      expect(gpsRecall(unit)).toEqual(pendingGps)
      expect(unit.recallList.slice(-atLoss[index].length)).toEqual(atLoss[index])
    }
    expect(system.computers[0].recallList).toEqual(system.computers[1].recallList)
  })
}
