import { expect, logicTest as test } from './isolated-client-test'
import { ScriptedFms } from '../src/fmsCdu/scriptedFms'
import { SCRATCHPAD_LINE, screenText } from '../src/fmsCdu/screen'
import { CDU_VARIANTS, functionFor, legendFor, variantById } from '../src/fmsCdu/variants'
import type { CduFunction } from '../src/fmsCdu/variants'

// Key behaviour follows the CMA-9000 Operator's Manual, section 2 (items 10 to 33).
const fms = (start = Date.UTC(2026, 8, 27, 14, 0, 0)) => {
  let now = start
  const unit = new ScriptedFms(() => new Date(now))
  return { unit, advance: (ms: number) => { now += ms } }
}
const press = (unit: ScriptedFms, ...fns: CduFunction[]) => { for (const fn of fns) unit.press(fn) }
const typeText = (unit: ScriptedFms, text: string) => {
  for (const ch of text) unit.press(ch === '.' ? 'DOT' : ch === '/' ? 'SLASH' : ch === ' ' ? 'SP' : `CHAR_${ch}`)
}
const lines = (unit: ScriptedFms) => screenText(unit.screen())
const scratch = (unit: ScriptedFms) => lines(unit)[SCRATCHPAD_LINE].trimEnd()
const titleLine = (unit: ScriptedFms) => lines(unit)[0]

test('CLR deletes one character, a held CLR clears the scratchpad, and CLR on an empty scratchpad arms DELETE', () => {
  const { unit } = fms()
  typeText(unit, 'CYYZ')
  unit.press('CLR')
  expect(scratch(unit)).toBe('CYY')
  unit.press('CLR', { held: true })
  expect(scratch(unit)).toBe('')
  unit.press('CLR')
  expect(scratch(unit)).toBe('DELETE')
  unit.press('CLR')
  expect(scratch(unit)).toBe('')
})

test('+/- shows minus first and alternates on each further press', () => {
  const { unit } = fms()
  typeText(unit, '12')
  unit.press('PLUSMINUS')
  expect(scratch(unit)).toBe('12-')
  unit.press('PLUSMINUS')
  expect(scratch(unit)).toBe('12+')
  unit.press('PLUSMINUS')
  expect(scratch(unit)).toBe('12-')
})

test('a route entry is a MOD until EXEC makes it active, and the EXEC lamp shows the pending modification', () => {
  const { unit } = fms()
  unit.press('RTE')
  typeText(unit, 'CYYZ')
  unit.press('LSK1R')
  expect(titleLine(unit)).toMatch(/^MOD RTE 1/)
  expect(lines(unit)[2]).toMatch(/CYYZ\s*$/)
  expect(unit.lamps().has('EXEC')).toBe(true)
  expect(scratch(unit)).toBe('')

  unit.press('EXEC')
  expect(titleLine(unit)).toMatch(/^ACT RTE 1/)
  expect(lines(unit)[2]).toMatch(/CYYZ\s*$/)
  expect(unit.lamps().has('EXEC')).toBe(false)
})

test('ERASE discards the modification and the active route is unchanged', () => {
  const { unit } = fms()
  unit.press('RTE')
  typeText(unit, 'CYYZ')
  unit.press('LSK1R')
  expect(lines(unit)[12]).toContain('<ERASE')
  unit.press('LSK6L')
  expect(titleLine(unit)).toMatch(/^ACT RTE 1/)
  expect(lines(unit)[2]).toMatch(/CYUL\s*$/)
  expect(unit.lamps().has('EXEC')).toBe(false)
})

test('an invalid entry is refused with INVALID ENTRY, and CLR restores the typed text', () => {
  const { unit } = fms()
  unit.press('RTE')
  typeText(unit, 'XYZ')
  unit.press('LSK1L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  expect(lines(unit)[2]).toMatch(/^CYOW/)
  unit.press('CLR')
  expect(scratch(unit)).toBe('XYZ')
})

test('an empty scratchpad copies the field beside the line select key', () => {
  const { unit } = fms()
  press(unit, 'RTE', 'LSK1L')
  expect(scratch(unit)).toBe('CYOW')
})

test('DELETE removes a leg as a modification; the destination leg cannot be deleted', () => {
  const { unit } = fms()
  unit.press('LEGS')
  expect(lines(unit)[4]).toMatch(/^RDG/)
  press(unit, 'CLR', 'LSK2L')
  expect(titleLine(unit)).toMatch(/^MOD RTE 1 LEGS/)
  expect(lines(unit)[4]).toMatch(/^TOLGU/)
  expect(unit.lamps().has('EXEC')).toBe(true)

  // Eight legs remain, five to a page: the destination is the third leg of page 2 (after FERDI and RW24R).
  unit.press('NEXT')
  expect(lines(unit)[6]).toMatch(/^CYUL/)
  press(unit, 'CLR', 'LSK3L')
  expect(scratch(unit)).toBe('INVALID DELETE')
})

test('HOLD with no hold in the plan opens LEGS with /H, and selecting a waypoint defines the holding fix', () => {
  const { unit } = fms()
  unit.press('HOLD')
  expect(titleLine(unit)).toContain('RTE 1 LEGS')
  expect(scratch(unit)).toBe('/H')
  unit.press('LSK2L')
  expect(titleLine(unit)).toMatch(/^MOD HOLD/)
  expect(lines(unit)[2]).toMatch(/^RDG/)
  press(unit, 'EXEC', 'HOLD')
  expect(titleLine(unit)).toMatch(/^ACT HOLD/)
})

test('MARK creates a Mark On Top waypoint at present position and shows it on PREDEF WPT 2/2', () => {
  const { unit } = fms()
  unit.press('MARK')
  expect(titleLine(unit)).toMatch(/PREDEF WPT\s+2\/2$/)
  expect(lines(unit)[2]).toMatch(/^MRK01\s+N4518\.6W07540\.9$/)
  unit.press('MARK')
  expect(lines(unit)[4]).toMatch(/^MRK02/)
})

test('an alert lights MSG until CLR acknowledges it, and MESSAGE RECALL keeps it', () => {
  const { unit } = fms()
  typeText(unit, 'AB')
  unit.raiseAlert('Unable rnp')
  expect(scratch(unit)).toBe('UNABLE RNP')
  expect(unit.lamps().has('MSG')).toBe(true)
  unit.press('CLR')
  expect(unit.lamps().has('MSG')).toBe(false)
  expect(scratch(unit)).toBe('AB')
  press(unit, 'INIT_REF', 'LSK3R')
  expect(titleLine(unit)).toContain('MESSAGE RECALL')
  expect(lines(unit)[2]).toMatch(/^UNABLE RNP/)
})

test('NEXT and PREV page through a multi-page display and wrap at either end', () => {
  const { unit } = fms()
  unit.press('PROG')
  expect(titleLine(unit)).toMatch(/1\/4$/)
  unit.press('PREV')
  expect(titleLine(unit)).toMatch(/4\/4$/)
  press(unit, 'NEXT', 'NEXT')
  expect(titleLine(unit)).toMatch(/2\/4$/)
})

test('BRT always brightens after five idle seconds, then alternates on each press', () => {
  const { unit, advance } = fms()
  const start = unit.brightness()
  unit.press('BRT')
  expect(unit.brightness()).toBeGreaterThan(start)
  advance(1000)
  unit.press('BRT')
  expect(unit.brightness()).toBe(start)
  advance(1000)
  unit.press('BRT')
  expect(unit.brightness()).toBeGreaterThan(start)
  advance(6000)
  const before = unit.brightness()
  unit.press('BRT')
  expect(unit.brightness()).toBeGreaterThan(before)
})

test('radio entries are range-checked, and a standby field with an empty scratchpad swaps active and standby', () => {
  const { unit, advance } = fms()
  // The radios answer each command after their acknowledgement latency (plan F8a on the #1350 RMS).
  const acknowledge = () => { advance(1000); unit.updateNavigation(1) }
  unit.press('RADIO')
  typeText(unit, '124.35')
  unit.press('LSK1L')
  expect(lines(unit)[2]).toMatch(/^124\.350/)
  acknowledge()
  typeText(unit, '140.00')
  unit.press('LSK1L')
  expect(scratch(unit)).toBe('INVALID ENTRY')
  press(unit, 'CLR', 'CLR', 'CLR', 'CLR', 'CLR', 'CLR', 'CLR')
  unit.press('LSK1R')
  acknowledge()
  expect(lines(unit)[2]).toMatch(/^126\.700\s+124\.350$/)
})

test('every hardware variation keeps FAIL and MSG first, BRT last in row two, and one function per key', () => {
  for (const variant of CDU_VARIANTS) {
    expect(variant.annunciators.slice(0, 2), variant.id).toEqual(['FAIL', 'MSG'])
    const rowTwo = [1, 2, 3, 4, 5, 6].map(n => functionFor(`F2_${n}`, variant))
    expect(rowTwo.at(-1), variant.id).toBe('BRT')
    expect(new Set(rowTwo).size, variant.id).toBe(6)
  }
})

test('a variation relabels the same physical key: TPDR on 030/430, FUEL on the standard panel', () => {
  expect(functionFor('F2_2', variantById('030/430'))).toBe('TPDR')
  expect(legendFor('F2_2', variantById('030/430'))).toEqual(['TPDR'])
  expect(functionFor('F2_2', variantById('002/003/005/102/103/302/303/502/503'))).toBe('FUEL')
  expect(legendFor('F2_3', variantById('001/101/301/501'))).toEqual(['SQK', 'IDT'])
  expect(legendFor('LSK1L', variantById('050'))).toEqual([])
})
