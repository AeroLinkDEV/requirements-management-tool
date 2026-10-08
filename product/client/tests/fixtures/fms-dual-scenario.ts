import { expect } from '../isolated-client-test'
import { START_STATES } from '../../src/fmsCdu/kbtvDemo'
import { profileById } from '../../src/fmsCdu/profile'
import { FmsKernel } from '../../src/fmsCdu/kernel/kernel'
import { dualComposition } from '../../src/fmsCdu/kernel/legacyPlantAdapter'
import { ScenarioRunner, scenarioStart, TICK_SECONDS, type Scenario } from '../../src/fmsCdu/scenario'

// The single-computer library owner cannot detect peer progress/alert ordering or a missed request that leaves
// the synchronized peer on the approach route (#1533). Exercise the bench's actual two-computer composition.
export function runDual(scenario: Scenario, side: 1 | 2 = 1, independent = false) {
  const start = scenarioStart(scenario) ?? Date.UTC(2026, 8, 27, 14)
  const { system, plant } = dualComposition(start, { profile: profileById(scenario.profile) })
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
  const kernel = new FmsKernel(plant, runner)
  if (independent) fms.setCondition('independent', true)
  const limit = Math.ceil(scenario.maxSeconds / TICK_SECONDS) + 2
  for (let tick = 0; !runner.finished && tick < limit; tick++) {
    kernel.advance(1)
  }
  return { system, runner }
}

