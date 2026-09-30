import { expect, logicTest as test } from './isolated-client-test'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import type { CduFunction } from '../src/fmsCdu/variants'

// E3, the FUEL pages to the operator's manual field set (S300 manual 14-1…14-4; plan rev 2 E3, rev 3 E3): RTE 1 FUEL
// 1/2 (MAX RANGE, GROSS WT, ENDURANCE, FUEL WT, FUEL FLOW, MILEAGE, FUEL REMAINING AT a FIX, UNIT) and 2/2 with the
// FUEL+WEIGHTS option (FUEL+RES, RESERVE, EMPTY, EQUIP, CREW and CARGO weights). The fuel computer (the simulation)
// gives the fuel on board and the flow; a crew entry of either is a "what if" (EST in the title), replaced by the fuel
// computer's values on each new access of the page.
const START = Date.UTC(2026, 8, 30, 14, 0, 0)
const setup = () => {
  let now = START
  const unit = new ScriptedFms(() => new Date(now))
  return { unit, advance: (ms: number) => { now += ms } }
}
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trim()
const type = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : `CHAR_${ch}` as CduFunction)
}
const enter = (unit: ScriptedFms, text: string, lsk: CduFunction) => { type(unit, text); unit.press(lsk) }
const hoursPlusMinutes = (hours: number) => { const m = Math.floor(hours * 60); return `${String(Math.floor(m / 60)).padStart(2, '0')}+${String(m % 60).padStart(2, '0')}` }
const lastWaypoint = (unit: ScriptedFms) => [...unit.activeRoute.legs].reverse().find(leg => leg.kind === 'wpt')!
/** Fuel remaining (excluding the reserve) at a waypoint: usable fuel less the flow over the predicted time to it. */
const remainingAt = (unit: ScriptedFms, ident: string, usable: number, flow: number) => {
  const point = [...unit.profile().points].reverse().find(p => p.ident === ident)!
  return Math.round(usable - (flow * (point.eta! - unit.now.getTime())) / 3_600_000)
}

test('FUEL 1/2 shows the manual field set from the fuel computer, the remaining fuel at the last waypoint by default', () => {
  const { unit } = setup()
  const { quantity, flow, reserve } = unit.fuelState
  const usable = quantity - reserve
  unit.press('FUEL')
  const screen = lines(unit)
  expect(screen[0]).toMatch(/^ACT RTE 1 FUEL\s+1\/2$/)
  expect(screen[1]).toMatch(/^ MAX RANGE\s+GROSS WT $/)
  // No weights entered on 2/2: no gross weight.
  expect(screen[2]).toMatch(new RegExp(`^${Math.round((usable / flow) * unit.groundSpeed)}NM\\s+-----KG$`))
  expect(screen[3]).toMatch(/^ ENDURANCE\s+FUEL WT $/)
  expect(screen[4]).toMatch(new RegExp(`^${hoursPlusMinutes(usable / flow).replace('+', '\\+')}\\s+${usable}KG$`))
  expect(screen[5]).toMatch(/^ FUEL FLOW\s+MILEAGE $/)
  expect(screen[6]).toMatch(new RegExp(`^${flow}KG/HR\\s+${(flow / unit.groundSpeed).toFixed(1)}KG/NM$`))
  expect(screen[7]).toMatch(/^ FUEL REMAINING AT\s+FIX $/)
  const fix = lastWaypoint(unit).kind === 'wpt' ? (lastWaypoint(unit) as { ident: string }).ident : ''
  expect(screen[8]).toMatch(new RegExp(`^${remainingAt(unit, fix, usable, flow)}KG\\s+${fix}$`))
  // The basis of that result, as every fuel result states it (R3-03), and the unit.
  expect(screen[9]).toMatch(/^ (KNOWN|COND|UNKNOWN)\s+UNIT $/)
  expect(screen[10]).toMatch(/KG<$/)
  // In the hover there is no range or mileage to give; the endurance stands.
  unit.setAircraft({ groundSpeed: 0 })
  expect(lines(unit)[2]).toMatch(/^---NM/)
  expect(lines(unit)[6]).toMatch(/---KG\/NM$/)
  expect(lines(unit)[4]).toMatch(new RegExp(`^${hoursPlusMinutes(usable / flow).replace('+', '\\+')}`))
})

test('a FUEL WT or FUEL FLOW entry is a what-if: EST in the title, the results follow it, the fuel computer does not, and a new access restores it', () => {
  const { unit } = setup()
  const before = { ...unit.fuelState }
  unit.press('FUEL')
  enter(unit, '1000', 'LSK2R')
  expect(lines(unit)[0]).toMatch(/^ACT RTE 1 FUEL EST\s+1\/2$/)
  expect(lines(unit)[4]).toMatch(new RegExp(`^${hoursPlusMinutes(1000 / before.flow).replace('+', '\\+')}\\s+1000KG$`))
  enter(unit, '400', 'LSK3L')
  expect(lines(unit)[4]).toMatch(/^02\+30\s+1000KG$/)
  expect(lines(unit)[6]).toMatch(/^400KG\/HR/)
  const fix = (lastWaypoint(unit) as { ident: string }).ident
  expect(lines(unit)[8]).toMatch(new RegExp(`^${remainingAt(unit, fix, 1000, 400)}KG\\s+${fix}$`))
  // What the fuel computer says, and so what the aircraft burns, is unchanged.
  expect(unit.fuelState).toEqual(before)
  // A malformed or zero entry is refused.
  enter(unit, '0', 'LSK3L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  unit.press('CLR')
  unit.press('CLR', { held: true })
  // 2/2 is the same access: the what-if stays.
  unit.press('NEXT')
  unit.press('PREV')
  expect(lines(unit)[0]).toMatch(/EST/)
  // Another page, then FUEL again: a new access, with the fuel computer's values.
  unit.press('PROG')
  unit.press('FUEL')
  expect(lines(unit)[0]).toMatch(/^ACT RTE 1 FUEL\s+1\/2$/)
  expect(lines(unit)[4]).toMatch(new RegExp(`\\s${before.quantity - before.reserve}KG$`))
  expect(lines(unit)[6]).toMatch(new RegExp(`^${before.flow}KG/HR`))
})

test('FIX at 4R takes a waypoint of the active route; one not in it is refused', () => {
  const { unit } = setup()
  const { quantity, flow, reserve } = unit.fuelState
  unit.press('FUEL')
  const first = unit.activeRoute.legs.find(leg => leg.kind === 'wpt') as { ident: string }
  enter(unit, first.ident, 'LSK4R')
  expect(lines(unit)[8]).toMatch(new RegExp(`^${remainingAt(unit, first.ident, quantity - reserve, flow)}KG\\s+${first.ident}$`))
  enter(unit, 'ZZZZZ', 'LSK4R')
  expect(scratch(unit)).toBe('NOT IN ROUTE')
  expect(lines(unit)[8]).toMatch(new RegExp(`\\s${first.ident}$`))
})

test('FUEL 2/2 (FUEL+WEIGHTS): the crew weights give the gross weight on 1/2, which falls as the fuel burns', () => {
  const { unit } = setup()
  const { quantity, reserve } = unit.fuelState
  unit.press('FUEL')
  unit.press('NEXT')
  const screen = lines(unit)
  expect(screen[0]).toMatch(/^ACT RTE 1 FUEL\s+2\/2$/)
  expect(screen[1]).toMatch(/^ FUEL\+RES\s+EMPTY WT $/)
  expect(screen[2]).toMatch(new RegExp(`^${quantity}KG\\s+-----KG$`))
  expect(screen[3]).toMatch(/^ RESERVE\s+EQUIP WT $/)
  expect(screen[4]).toMatch(new RegExp(`^${reserve}KG\\s+-----KG$`))
  expect(screen[5]).toMatch(/^\s+CREW WT $/)
  expect(screen[6]).toMatch(/^\s+-----KG$/)
  expect(screen[7]).toMatch(/^\s+CARGO WT $/)
  // No landing is modelled, so no landing reserve is claimed as met (R3-03).
  expect(screen[9]).toMatch(/^ LDG RESERVE\s+UNIT $/)
  expect(screen[10]).toMatch(/^LANDING NOT MODELLED\s+KG<$/)
  expect(screen[12]).toMatch(/^<INIT\/REF/)
  // The fuel computer gives the fuel on board, so FUEL+RES takes no entry while it is valid.
  enter(unit, '1700', 'LSK1L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  unit.press('CLR')
  unit.press('CLR', { held: true })
  enter(unit, '3000', 'LSK1R')
  enter(unit, '400', 'LSK2R')
  enter(unit, '180', 'LSK3R')
  enter(unit, '250', 'LSK4R')
  expect(lines(unit)[2]).toMatch(/\s3000KG$/)
  expect(lines(unit)[8]).toMatch(/\s250KG$/)
  // The crew's reserve is the reserve the FMS keeps.
  enter(unit, '300', 'LSK2L')
  expect(unit.fuelState.reserve).toBe(300)
  unit.press('PREV')
  expect(lines(unit)[2]).toMatch(new RegExp(`\\s${3000 + 400 + 180 + 250 + quantity}KG$`))
  expect(lines(unit)[4]).toMatch(new RegExp(`\\s${quantity - 300}KG$`))
  // An hour at the flow later, the gross weight has fallen by the fuel burned.
  unit.updatePerformance(3600)
  const burned = quantity - unit.fuelState.quantity
  expect(burned).toBeCloseTo(unit.fuelState.flow, 6)
  expect(lines(unit)[2]).toMatch(new RegExp(`\\s${Math.round(3000 + 400 + 180 + 250 + unit.fuelState.quantity)}KG$`))
  // The weights are the crew's, kept when the page is left.
  unit.press('PROG')
  unit.press('FUEL')
  unit.press('NEXT')
  expect(lines(unit)[2]).toMatch(/\s3000KG$/)
  unit.press('LSK6L')
  expect(lines(unit)[0]).toMatch(/^INIT\/REF INDEX/)
})

test('UNIT toggles KG and LB on both pages, and entries are read in the unit shown', () => {
  const { unit } = setup()
  const { quantity, flow, reserve } = unit.fuelState
  const lb = (kg: number) => Math.round(kg * 2.20462)
  unit.press('FUEL')
  unit.press('LSK5R')
  expect(lines(unit)[10]).toMatch(/LB<$/)
  expect(lines(unit)[4]).toMatch(new RegExp(`\\s${lb(quantity - reserve)}LB$`))
  expect(lines(unit)[6]).toMatch(new RegExp(`^${lb(flow)}LB/HR`))
  unit.press('NEXT')
  expect(lines(unit)[2]).toMatch(new RegExp(`^${lb(quantity)}LB`))
  expect(lines(unit)[10]).toMatch(/LB<$/)
  // A reserve typed in pounds is kept in kilograms.
  enter(unit, '661', 'LSK2L')
  expect(unit.fuelState.reserve).toBeCloseTo(661 / 2.20462, 6)
  unit.press('LSK5R')
  expect(lines(unit)[4]).toMatch(/^300KG/)
})
