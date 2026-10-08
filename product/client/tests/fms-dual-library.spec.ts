import { expect, logicTest as test } from './isolated-client-test'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { runCensus } from './fixtures/fms-kernel-census'

// Complete 23-row admission sweep remains Full-only; targeted regression owners run in Fast (#1551).
for (const scenario of SCENARIO_LIBRARY) {
  test(`built-in scenario ${scenario.id} passes with two synchronized computers`, () => {
    const run = runCensus({ id: `${scenario.id}/dual`, topology: 'dual', kind: 'scenario', scenario },
      { rate: 1, rendered: false, digest: false })
    expect(run.results!.filter(result => result.status !== 'done' && result.status !== 'pass'), scenario.id).toEqual([])
    expect(run.outcome, scenario.id).toBe('passed')
  })
}

