import { expect, logicTest as test } from './isolated-client-test'
import { checkAtTdn, planTransition } from '../src/fmsCdu/transition'
import { checkAtTdn as oracleCheck, planTransition as oraclePlan, type TransitionPlan as OraclePlan } from './support/tdnOracle'

// The production transition planner (src/fmsCdu/transition.ts) against the independent oracle
// (tests/support/tdnOracle.ts, #1253): each stage distance within ±0.005 NM, and the same refusals, over a grid of
// start states; and the T6 check at TDN at full precision against the oracle's own planned distances.

type Start = { ias: number; radioHeight: number | null; verticalSpeed: number; headwind: number; hoverHeight: number; elevation?: number }
const asOracle = (s: Start) => ({ iasKt: s.ias, raFt: s.radioHeight, vsFpm: s.verticalSpeed, headwindKt: s.headwind, hoverFt: s.hoverHeight, elevationFt: s.elevation })
const NM = 0.005

const grid: Start[] = []
for (const ias of [80, 100, 120])
  for (const radioHeight of [80, 105, 200, 500, 1000])
    for (const verticalSpeed of [-500, 0, 500])
      for (const headwind of [0, 20, -10])
        for (const elevation of [0, 3000]) grid.push({ ias, radioHeight, verticalSpeed, headwind, hoverHeight: 50, elevation })

test('every stage distance of the production plan agrees with the oracle within 0.005 NM over the start-state grid (Stage D)', () => {
  for (const start of grid) {
    const mine = planTransition(start), theirs = oraclePlan(asOracle(start))
    const label = JSON.stringify(start)
    expect(mine.refused, label).toBe(theirs.refused)
    if (mine.refused || theirs.refused) continue
    const o = theirs as OraclePlan
    expect(Math.abs(mine.td.distanceNm - o.td.distanceNm), `TD ${label}`).toBeLessThanOrEqual(NM)
    expect(Math.abs(mine.td.groundSpeed - o.gate.gsKt), `gate GS ${label}`).toBeLessThanOrEqual(1)
    expect(Math.abs(mine.td.gateHeight - o.td.gateHeightFt), `gate height ${label}`).toBeLessThanOrEqual(5)
    expect(Math.abs(mine.tdh.distanceNm - o.tdh.distanceNm), `TD/H ${label}`).toBeLessThanOrEqual(NM)
    expect(Math.abs(mine.dtraNm - o.plannedDtraNm), `DTRA ${label}`).toBeLessThanOrEqual(NM)
  }
})

test('the refusals agree with the oracle: below the gate speed, the vertical speed limit, no radio height, the hover height, no closure (Stage D)', () => {
  const base: Start = { ias: 100, radioHeight: 500, verticalSpeed: 0, headwind: 20, hoverHeight: 50 }
  const cases: [Partial<Start>, string][] = [
    [{ ias: 79 }, 'below gate speed'],
    [{ verticalSpeed: 1200 }, 'vertical speed limit'],
    [{ radioHeight: null }, 'radio height invalid'],
    [{ hoverHeight: 250 }, 'hover height out of range'],
    [{ hoverHeight: 20 }, 'hover height out of range'],
  ]
  for (const [change, reason] of cases) {
    const start = { ...base, ...change }
    expect(planTransition(start)).toEqual({ refused: true, reason })
    expect(oraclePlan(asOracle(start))).toEqual({ refused: true, reason })
  }
  // No closure: the oracle's TD/H closed loop refuses a non-positive ground speed; the production planner refuses it up front.
  expect(planTransition({ ...base, headwind: 95 })).toEqual({ refused: true, reason: 'no closure' })
})

test('the worked example: 100 KIAS, 500 ft, 20 kt headwind, still air; the planned distances match the oracle and the amended nominal (Stage D)', () => {
  for (const headwind of [20, 0]) {
    const start: Start = { ias: 100, radioHeight: 500, verticalSpeed: 0, headwind, hoverHeight: 50 }
    const mine = planTransition(start), theirs = oraclePlan(asOracle(start)) as OraclePlan
    if (mine.refused) throw new Error(mine.reason)
    expect(Math.abs(mine.dtraNm - theirs.plannedDtraNm)).toBeLessThanOrEqual(NM)
    if (headwind === 20) {
      // The amended nominal (#1253): DTRA 1.546 NM, D(TD) 0.674 NM.
      expect(mine.dtraNm).toBeCloseTo(1.546, 2)
      expect(mine.td.distanceNm).toBeCloseTo(0.674, 2)
    }
  }
})

test('T6 at full precision: with the remaining distance D(TD) + D(TD/H) the transition engages, a hair less is TDN DIST SHORT, as in the oracle (Stage D)', () => {
  for (const start of grid.filter(s => s.headwind === 20)) {
    const theirs = oraclePlan(asOracle(start))
    if (theirs.refused) continue
    const mine = planTransition(start)
    if (mine.refused) throw new Error(mine.reason)
    const exact = mine.td.distanceNm + mine.tdh.distanceNm
    // Exactly on the boundary, to floating-point round-off ((a + b) - a - b may be -1e-17).
    expect(checkAtTdn(start, exact + 1e-12).engage).toBe(true)
    const short = checkAtTdn(start, exact - 1e-9)
    expect(short.engage).toBe(false)
    expect(!short.engage && short.reason).toBe('TDN DIST SHORT')
    // The two planners draw the line at the same place, within the distance tolerance.
    const o = theirs as OraclePlan
    expect(oracleCheck(asOracle(start), o.td.distanceNm + o.tdh.distanceNm + NM).decision).toBe('engage')
    expect(checkAtTdn(start, o.td.distanceNm + o.tdh.distanceNm + NM).engage).toBe(true)
    expect(oracleCheck(asOracle(start), exact - NM).decision).toBe('refuse')
  }
})
