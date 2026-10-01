import { expect, test } from '@playwright/test'
import { ScriptedFms, airacCycle } from '../src/fmsCdu/scriptedFms'

// The demonstration database is invented data, dated to the AIRAC cycle current when the unit is built and the next.
// Fixed September dates made every bench built after 30 September 2026 start DATABASE OUT OF DATE.

test('AIRAC cycles are 28 days from 2601 on 22 January 2026, numbered within the year each begins', () => {
  // Independent values: the published AIRAC effective dates.
  expect(airacCycle(0)).toEqual({ ident: '2601', from: '2026-01-22', to: '2026-02-18' })
  expect(airacCycle(8)).toEqual({ ident: '2609', from: '2026-09-03', to: '2026-09-30' })
  expect(airacCycle(9)).toEqual({ ident: '2610', from: '2026-10-01', to: '2026-10-28' })
  expect(airacCycle(12)).toEqual({ ident: '2613', from: '2026-12-24', to: '2027-01-20' })
  expect(airacCycle(13)).toEqual({ ident: '2701', from: '2027-01-21', to: '2027-02-17' })
  expect(airacCycle(-1)).toEqual({ ident: '2513', from: '2025-12-25', to: '2026-01-21' })
})

test('a unit is built on the current cycle with the next one inactive, and starts in date', () => {
  const at = (iso: string) => new ScriptedFms(() => new Date(iso))
  const september = at('2026-09-30T14:00:00Z')
  expect([september.activeCycle.id, september.inactiveCycle?.id]).toEqual(['DEMO-2609', 'DEMO-2610'])
  // The first minute of a cycle belongs to it; the last day's evening still belongs to the old one.
  const october = at('2026-10-01T00:00:00Z')
  expect([october.activeCycle.id, october.inactiveCycle?.id]).toEqual(['DEMO-2610', 'DEMO-2611'])
  expect(october.activeCycle).toMatchObject({ from: Date.UTC(2026, 9, 1), to: Date.UTC(2026, 9, 28, 23, 59) })
  expect(at('2026-09-30T23:58:00Z').activeCycle.id).toBe('DEMO-2609')
  // Built in date, no DATABASE OUT OF DATE.
  october.tick()
  expect(october.recallList.map(entry => entry.text)).not.toContain('DATABASE OUT OF DATE')
  const newYear = at('2027-01-21T12:00:00Z')
  expect([newYear.activeCycle.id, newYear.inactiveCycle?.id]).toEqual(['DEMO-2701', 'DEMO-2702'])
})
