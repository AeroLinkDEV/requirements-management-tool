import { expect, logicTest as test } from './isolated-client-test'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { distanceNm, offset } from '../src/fmsCdu/fmsModel'

const START = Date.UTC(2026, 8, 27, 14)
function setup(side: 1 | 2, independent = false) {
  let now = START
  const system = new DualFmsSystem(() => new Date(now))
  const [one] = system.computers
  one.defineMoving('SHIP1', offset(one.position, 30, 12), 90, 40)
  one.directTo('SHIP1'); one.press('EXEC')
  system.selectGuidance(side)
  system.simulator.selectHeading(270)
  if (independent) one.setCondition('independent', true)
  const fly = (seconds: number) => {
    for (let tick = 0; tick < seconds * 4; tick++) { now += 250; system.step(0.25) }
  }
  return { system, own: system.computers[2 - side], selected: system.computers[side - 1], fly }
}

// Primary owner: periodic offside rendezvous computations (#1523, M30011-37). Existing single/EXEC-purity owners
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

for (const side of [1, 2] as const) {
  test(`offside FMS${3 - side} also refreshes its own modified-route rendezvous`, () => {
    const { own, fly } = setup(side, true)
    own.defineMoving('SHIP2', offset(own.position, 250, 18), 120, 35)
    own.directTo('SHIP2')
    const initial = structuredClone(own.rendezvousFor(own.route, 0)!)
    expect(own.routeStatus).toBe('MOD')
    expect(initial).toMatchObject({ condition: 4, achievable: true })
    expect(initial.ttg!).toBeGreaterThan(90)
    fly(10)
    const refreshed = own.rendezvousFor(own.route, 0)!
    expect(refreshed.computedAt).toBe(initial.computedAt + 10_000)
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
