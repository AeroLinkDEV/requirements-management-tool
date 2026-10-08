import { test } from '@playwright/test'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { frameSummary } from './stats.ts'

// One headless run (#1510 I0a): one workload in one topology, in a fresh process the orchestrator (run-perf.ts)
// spawned. The arm's own modules are loaded from its checkout, so both arms run under this one harness and toolchain.
//
// The unit is one frame. runHeadless has no per-frame hook, so its loop is copied here as the measurement point:
// `single` is runHeadless's composition (scenario.ts), `dual` is the bench's scenario composition (FmsCduTestBench.tsx:
// a DualFmsSystem whose first computer and flight carry the runner). Entry contract for later stages: keep these module
// paths and exports, or declare an adapter per arm.
//
// Declared arm adapter (#1517 I1a): an arm with the kernel (src/fmsCdu/kernel/) measures kernel.advance(1) on the legacy
// plant adapter's compositions; an arm before it measures advanceTicks(1, ...) on the same compositions built directly.

const arm = process.env.AEROLINK_PERF_ARM
const workloadId = process.env.AEROLINK_PERF_WORKLOAD
const topology = process.env.AEROLINK_PERF_TOPOLOGY
const result = process.env.AEROLINK_PERF_RESULT

type Scenario = { id: string; maxSeconds: number; profile?: string; startTime?: string }
type Runner = { finished: boolean; elapsed: number; outcome: string; gpsSeeds: unknown }

test('headless frame cost', async () => {
  test.setTimeout(30 * 60_000)
  if (!arm || !workloadId || !topology || !result) throw new Error('AEROLINK_PERF_ARM, _WORKLOAD, _TOPOLOGY and _RESULT are required')
  if (topology !== 'single' && topology !== 'dual') throw new Error(`unknown topology ${topology}`)
  const gc = (globalThis as { gc?: () => void }).gc
  if (!gc) throw new Error('run with --expose-gc')
  const protocol = JSON.parse(readFileSync(new URL('./protocol.json', import.meta.url), 'utf8'))
  const workload = protocol.headless.workloads.find((entry: { id: string }) => entry.id === workloadId)
  if (!workload) throw new Error(`unknown workload ${workloadId}`)

  const load = (name: string) => import(pathToFileURL(join(arm, 'src', 'fmsCdu', name)).href)
  const [scenarioModule, flight, scripted, dualFms, profiles, library, heli] = await Promise.all(
    ['scenario.ts', 'flight.ts', 'scriptedFms.ts', 'dualFms.ts', 'profile.ts', 'scenarioLibrary.ts', 'heliDemo.ts'].map(load))
  const scenario: Scenario = workload.definition
    ?? [...library.SCENARIO_LIBRARY, heli.MISSION_87N_OFFSHORE_SAR].find((entry: Scenario) => entry.id === workload.scenario)
  if (!scenario) throw new Error(`scenario ${workload.scenario} is not in this arm`)

  const start = scenarioModule.scenarioStart(scenario) ?? Date.UTC(2026, 8, 27, 14, 0, 0)
  let now = start
  const clock = () => new Date(now)
  const moveClock = (ms: number) => { now += ms }
  let sim: { step(dt: number): void }, runner: Runner
  let system: { mode: string; computers: readonly unknown[] } | null = null
  let frame: () => void = () => scenarioModule.advanceTicks(1, moveClock, sim, runner)
  let simulatedMs = () => now - start
  if (existsSync(join(arm, 'src', 'fmsCdu', 'kernel', 'kernel.ts'))) {
    const [kernelModule, adapter] = await Promise.all([join('kernel', 'kernel.ts'), join('kernel', 'legacyPlantAdapter.ts')].map(load))
    const profile = profiles.profileById(scenario.profile)
    const composed = topology === 'single'
      ? adapter.singleComposition(start, { profile })
      : adapter.dualComposition(start, { profile: profile ?? profiles.ACTIVE_PROFILE, secondaryProfile: profile ?? profiles.ACTIVE_PROFILE })
    if (topology === 'single') {
      sim = composed.sim
      runner = new scenarioModule.ScenarioRunner(scenario, composed.fms, undefined, composed.sim)
    } else {
      system = composed.system
      sim = composed.system
      runner = new scenarioModule.ScenarioRunner(scenario, composed.system.computers[0], { variant: 'perf harness', cycle: composed.system.computers[0].activeCycle.id }, composed.system.flights[0])
    }
    const kernel = new kernelModule.FmsKernel(composed.plant, runner)
    frame = () => kernel.advance(1)
    simulatedMs = () => kernel.unitClockMs - start
  } else if (topology === 'single') {
    const fms = new scripted.ScriptedFms(clock, { profile: profiles.profileById(scenario.profile) })
    sim = new flight.FlightSimulator(fms)
    runner = new scenarioModule.ScenarioRunner(scenario, fms, undefined, sim)
  } else {
    const profile = profiles.profileById(scenario.profile) ?? profiles.ACTIVE_PROFILE
    const dual = new dualFms.DualFmsSystem(clock, { profile, secondaryProfile: profile })
    system = dual
    sim = dual
    runner = new scenarioModule.ScenarioRunner(scenario, dual.computers[0], { variant: 'perf harness', cycle: dual.computers[0].activeCycle.id }, dual.flights[0])
  }

  const tick = scenarioModule.TICK_SECONDS as number
  const windowTicks = workload.windowSimSeconds ? Math.round(workload.windowSimSeconds / tick) : null
  const limit = windowTicks ?? Math.ceil(scenario.maxSeconds / tick) + 2
  const warmupTicks = Math.round(protocol.warmupSimSeconds / tick)
  const frames: number[] = []
  let syncTicks = 0, firstDrop: { simSeconds: number; reason: string | null } | null = null
  let measuredWallStart = 0
  for (let t = 0; t < limit; t += 1) {
    // The scenario's own end stops a run; a declared window keeps flying after the runner has finished.
    if (windowTicks === null && runner.finished) break
    if (t === warmupTicks) measuredWallStart = performance.now()
    const begin = performance.now()
    frame()
    const duration = performance.now() - begin
    if (t >= warmupTicks) frames.push(duration)
    if (system) {
      if (system.mode === 'SYNC') syncTicks += 1
      else if (!firstDrop) {
        const faults = (system.computers[0] as { faults?: { text: string }[] }).faults ?? []
        firstDrop = { simSeconds: (t + 1) * tick, reason: faults.find(fault => fault.text.startsWith('X-SIDE SYNC LOST'))?.text ?? null }
      }
    }
  }
  const measuredWall = (performance.now() - measuredWallStart) / 1000
  const simulated = simulatedMs() / 1000
  gc(); gc()
  const heapUsed = process.memoryUsage().heapUsed
  const summary = {
    schema: 'aerolink.fms-perf-headless-run.v1', workload: workloadId, scenario: scenario.id, topology,
    simulatedSeconds: simulated, measuredSimSeconds: frames.length * tick, warmupSimSeconds: protocol.warmupSimSeconds,
    ...frameSummary(frames), meanMs: frames.reduce((a, b) => a + b, 0) / frames.length,
    throughput: (frames.length * tick) / measuredWall, heapUsedBytesAfterGc: heapUsed,
    runnerOutcome: runner.outcome, gpsSeeds: runner.gpsSeeds,
    sync: system ? { residency: syncTicks / Math.max(1, Math.round(simulated / tick)), firstDrop } : null,
  }
  // Keep the simulation reachable until the heap is read, so the reading is the retained run state.
  void sim; void runner
  writeFileSync(result, JSON.stringify(summary, null, 2))
})
