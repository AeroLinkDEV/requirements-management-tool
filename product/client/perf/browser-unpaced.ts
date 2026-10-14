/** Actual public-module work owner, also serialized into the inert browser measurement page. No UI or pacing. */
export type UnpacedInput = {
  driverKind: 'CURRENT_KERNEL' | 'LEGACY_PUBLIC_ADVANCE_TICKS';
  scenarioJson: string; topology: 'single' | 'dual'; warmupFrames: number; totalFrames: number | null; maximumFrames: number;
  profile: { id: string; summary: string }; seeds: { constellation: number; receivers: number[] }; gpsModel: string;
}
export type UnpacedClock = { now(): number; utcNow(): number; timeOrigin: number | null }

// Module objects are the declared ARM's unchanged public exports, never a custom plant or private hook.
// The function references only its arguments/globals, so the browser executes exactly this work owner.
export function runBrowserUnpaced(modules: Record<string, any>, input: UnpacedInput, clock: UnpacedClock) {
  const scenario = JSON.parse(input.scenarioJson)
  const start = Date.parse(scenario.startTime)
  if (!Number.isSafeInteger(start) || !['single', 'dual'].includes(input.topology) ||
      !Number.isSafeInteger(input.warmupFrames) || input.warmupFrames < 0 ||
      !Number.isSafeInteger(input.maximumFrames) || input.maximumFrames <= input.warmupFrames ||
      (input.totalFrames !== null && (!Number.isSafeInteger(input.totalFrames) || input.totalFrames <= input.warmupFrames || input.totalFrames > input.maximumFrames)))
    throw new Error('invalid unpaced input extent, UTC or topology')
  const profile = modules.profiles.profileById(input.profile.id)
  if (!profile || scenario.profile !== profile.id || modules.profiles.profileSummary(profile) !== input.profile.summary)
    throw new Error('unpaced input profile does not resolve to its exact ARM identity')
  const legacy = input.driverKind === 'LEGACY_PUBLIC_ADVANCE_TICKS'
  if (!legacy && input.driverKind !== 'CURRENT_KERNEL') throw new Error('unknown declared unpaced driver kind')
  if (typeof modules.scenario.ScenarioRunner !== 'function' || typeof modules.profiles.profileSummary !== 'function' ||
      (legacy ? typeof modules.scenario.advanceTicks !== 'function' || modules.scenario.TICK_SECONDS !== 0.25 ||
        typeof modules.scripted?.ScriptedFms !== 'function' || typeof modules.flight?.FlightSimulator !== 'function' || typeof modules.dual?.DualFmsSystem !== 'function'
        : typeof modules.adapter?.singleComposition !== 'function' || typeof modules.adapter?.dualComposition !== 'function' || typeof modules.kernel?.FmsKernel !== 'function'))
    throw new Error('declared ARM driver exports are unavailable; no alternate driver fallback')
  let injectedClockMs = start
  const moveClock = (ms: number) => { injectedClockMs += ms }
  const unitClock = () => new Date(injectedClockMs)
  let composed: any
  if (!legacy) composed = input.topology === 'single' ? modules.adapter.singleComposition(start, { profile })
    : modules.adapter.dualComposition(start, { profile, secondaryProfile: profile })
  else if (input.topology === 'single') {
    const fms = new modules.scripted.ScriptedFms(unitClock, { profile })
    composed = { fms, sim: new modules.flight.FlightSimulator(fms) }
  } else composed = { system: new modules.dual.DualFmsSystem(unitClock, { profile, secondaryProfile: profile }) }
  const units = input.topology === 'single' ? [composed.fms] : composed.system.computers
  const context = { variant: 'browser unpaced public composition', cycle: units[0].activeCycle.id }
  const flight = input.topology === 'single' ? composed.sim : composed.system.flights[0]
  // Legacy's public four-argument constructor owns its own start/poll. No additional settling action.
  const runner = legacy ? new modules.scenario.ScenarioRunner(scenario, units[0], context, flight)
    : new modules.scenario.ScenarioRunner(scenario, units[0], context, flight, composed.settleStart)
  const kernel = legacy ? null : new modules.kernel.FmsKernel(composed.plant, runner)
  const readClock = () => ({ kernelTime: kernel ? kernel.now as number : null,
    injectedClockMs: legacy ? injectedClockMs : null,
    unitClockMs: units[0].now.getTime() as number, unitClocksMs: units.map((unit: any) => unit.now.getTime() as number) })
  const setup = { outcome: runner.outcome, finished: runner.finished, endedAfter: runner.endedAfter, report: modules.scenario.reportMarkdown(runner) }
  let returnedCompleted: number | null = legacy ? null : 0
  let completed = 0, attempted = 0, failure: string | null = null
  const durations: number[] = []
  const frameWitnesses: { attempt: number; entry: ReturnType<typeof readClock>; exit: ReturnType<typeof readClock>; returnedCompleted: number | null; successfulReturn: boolean; acceptedCompletion: boolean }[] = []
  const sample = () => ({ utcMs: clock.utcNow(), monotonicMs: clock.now(), timeOrigin: clock.timeOrigin,
    ...readClock(), completed })
  const initial = sample()
  let warmup: ReturnType<typeof sample> | null = null
  const seedReceipts = units.map((unit: any) => {
    const receivers = unit.gps
    const observed = Array.isArray(receivers) && receivers.length === 2 && receivers.every((r: any) =>
      Number.isSafeInteger(r.seed) && Number.isSafeInteger(r.constellationSeed))
    return observed ? { status: 'OBSERVED_PUBLIC_RECEIVERS', constellation: receivers[0].constellationSeed,
      receiverConstellations: receivers.map((r: any) => r.constellationSeed), receivers: receivers.map((r: any) => r.seed) }
      : { status: 'UNOBSERVED', constellation: null, receiverConstellations: null, receivers: null }
  })
  if (runner.context.profile !== input.profile.summary || modules.gps.GPS_MODEL_VERSION !== input.gpsModel ||
      seedReceipts.some((r: any) => r.status !== 'OBSERVED_PUBLIC_RECEIVERS' || r.constellation !== input.seeds.constellation ||
        r.receiverConstellations.some((seed: number) => seed !== input.seeds.constellation) || JSON.stringify(r.receivers) !== JSON.stringify(input.seeds.receivers)))
    failure = 'unpaced profile, GPS model or actual public seed evidence disagrees or is unobserved'
  if (!failure && input.totalFrames !== null && (!runner.finished || runner.outcome !== 'passed' || runner.endedAfter !== 0))
    failure = 'fixed unpaced continuation requires actual successful t0 setup before any model work'
  let syncTotal = 0, syncScored = 0
  let firstDrop: { completed: number; simSeconds: number; mode: string } | null = null
  const limit = input.totalFrames ?? input.maximumFrames
  while (!failure && completed < limit) {
    if (input.totalFrames === null && runner.finished) break
    if (completed === input.warmupFrames) warmup = sample()
    const before = readClock()
    const begin = clock.now()
    attempted++
    returnedCompleted = null
    try {
      if (legacy) modules.scenario.advanceTicks(1, moveClock, input.topology === 'single' ? composed.sim : composed.system, runner)
      else returnedCompleted = kernel.advance(1)
    } catch (error) {
      frameWitnesses.push({ attempt: attempted, entry: before, exit: readClock(), returnedCompleted: null, successfulReturn: false, acceptedCompletion: false })
      failure = `actual ${legacy ? 'legacy' : 'kernel'} advance failed: ${error instanceof Error ? error.message : String(error)}`; break
    }
    const end = clock.now(), after = readClock()
    const witness = { attempt: attempted, entry: before, exit: after, returnedCompleted, successfulReturn: true, acceptedCompletion: false }
    frameWitnesses.push(witness)
    if ((!legacy && (returnedCompleted !== 1 || after.kernelTime! - before.kernelTime! !== 2_500_000)) ||
        (legacy && after.injectedClockMs! - before.injectedClockMs! !== 250) ||
        after.unitClocksMs.some((value: number, index: number) => value - before.unitClocksMs[index] !== 250) ||
        !Number.isFinite(begin) || !Number.isFinite(end) || end < begin) {
      failure = 'actual returned completion, clock advance or frame duration disagrees'; break
    }
    completed++
    witness.acceptedCompletion = true
    if (completed > input.warmupFrames) durations.push(end - begin)
    if (input.topology === 'dual') {
      if (composed.system.mode === 'SYNC') { syncTotal++; if (completed > input.warmupFrames) syncScored++ }
      else firstDrop ??= { completed, simSeconds: completed / 4, mode: composed.system.mode }
    }
  }
  const end = sample()
  const scoredWallSeconds = warmup ? (end.monotonicMs - warmup.monotonicMs) / 1000 : null
  if (!failure && (!warmup || !durations.length || scoredWallSeconds === null || scoredWallSeconds <= 0 ||
      completed !== attempted || end.unitClockMs - start !== completed * 250 || (kernel && end.kernelTime !== completed * 2_500_000) || end.unitClocksMs.some((value: number) => value - start !== completed * 250) || (legacy && end.injectedClockMs! - start !== completed * 250) ||
      (input.totalFrames !== null ? completed !== input.totalFrames : !runner.finished || runner.outcome !== 'passed' || completed / 4 > scenario.maxSeconds)))
    failure = 'unpaced work is incomplete, empty, outside its extent or lacks the required actual mission outcome'
  return {
    summary: { schema: 'aerolink.fms-perf-browser-unpaced-run.v1', status: failure ? 'FAILED' : 'OBSERVED', failure,
      qualification: 'NOT_CALIBRATED', inference: 'DISABLED', topology: input.topology,
      driver: { kind: input.driverKind, witnessKind: legacy ? 'SUCCESSFUL_PUBLIC_CALL_AND_CLOCK_DELTA' : 'RETURNED_KERNEL_COMPLETION_AND_CLOCK_DELTA', returnedCompleted }, setup,
      work: { attempted, completed, warmupFrames: input.warmupFrames, scoredFrames: durations.length,
        totalSimSeconds: completed / 4, scoredSimSeconds: durations.length / 4, fixedTotalFrames: input.totalFrames },
      boundaries: { initial, warmup, end }, frameWitnesses, durations, scoredWallSeconds,
      throughput: scoredWallSeconds && scoredWallSeconds > 0 ? durations.length / 4 / scoredWallSeconds : null,
      profile: input.profile, dataset: { cycle: runner.context.cycle, source: runner.context.data, status: 'OBSERVED_PUBLIC_CONTEXT' },
      gps: { model: modules.gps.GPS_MODEL_VERSION, seeds: seedReceipts, runnerSeeds: runner.gpsSeeds },
      runner: { outcome: runner.outcome, finished: runner.finished, endedAfter: runner.endedAfter,
        report: modules.scenario.reportMarkdown(runner) },
      sync: input.topology === 'dual' ? { totalFrames: syncTotal, scoredFrames: syncScored,
        totalResidency: completed ? syncTotal / completed : null, scoredResidency: durations.length ? syncScored / durations.length : null, firstDrop } : null },
    // Real caller retains this exact final state through GC/heap observation, then releases it.
    retained: { composed, kernel, runner, readClock },
  }
}
