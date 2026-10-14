/** Actual public-module work owner, also serialized into the inert browser measurement page. No UI or pacing. */
export type UnpacedInput = {
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
  const composed = input.topology === 'single' ? modules.adapter.singleComposition(start, { profile })
    : modules.adapter.dualComposition(start, { profile, secondaryProfile: profile })
  const units = input.topology === 'single' ? [composed.fms] : composed.system.computers
  const runner = new modules.scenario.ScenarioRunner(scenario, units[0],
    { variant: 'browser unpaced public composition', cycle: units[0].activeCycle.id },
    input.topology === 'single' ? composed.sim : composed.system.flights[0], composed.settleStart)
  const kernel = new modules.kernel.FmsKernel(composed.plant, runner)
  let completed = 0, attempted = 0, failure: string | null = null
  const durations: number[] = []
  const sample = () => ({ utcMs: clock.utcNow(), monotonicMs: clock.now(), timeOrigin: clock.timeOrigin,
    kernelTime: kernel.now, unitClockMs: kernel.unitClockMs, completed })
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
  let syncTotal = 0, syncScored = 0
  let firstDrop: { completed: number; simSeconds: number; mode: string } | null = null
  const limit = input.totalFrames ?? input.maximumFrames
  while (!failure && completed < limit) {
    if (input.totalFrames === null && runner.finished) break
    if (completed === input.warmupFrames) warmup = sample()
    const before = { kernelTime: kernel.now, unitClockMs: kernel.unitClockMs }
    const begin = clock.now()
    let done = 0
    attempted++
    try { done = kernel.advance(1) }
    catch (error) { failure = `actual kernel advance failed: ${error instanceof Error ? error.message : String(error)}`; break }
    const end = clock.now()
    if (Number.isSafeInteger(done) && done > 0) completed += done
    if (done !== 1 || kernel.now - before.kernelTime !== 2_500_000 || kernel.unitClockMs - before.unitClockMs !== 250 ||
        !Number.isFinite(begin) || !Number.isFinite(end) || end < begin) {
      failure = 'actual returned completion, clock advance or frame duration disagrees'; break
    }
    if (completed > input.warmupFrames) durations.push(end - begin)
    if (input.topology === 'dual') {
      if (composed.system.mode === 'SYNC') { syncTotal++; if (completed > input.warmupFrames) syncScored++ }
      else firstDrop ??= { completed, simSeconds: completed / 4, mode: composed.system.mode }
    }
  }
  const end = sample()
  const scoredWallSeconds = warmup ? (end.monotonicMs - warmup.monotonicMs) / 1000 : null
  if (!failure && (!warmup || !durations.length || scoredWallSeconds === null || scoredWallSeconds <= 0 ||
      completed !== attempted || end.unitClockMs - start !== completed * 250 || end.kernelTime !== completed * 2_500_000 ||
      (input.totalFrames !== null ? completed !== input.totalFrames : !runner.finished || runner.outcome !== 'passed' || completed / 4 > scenario.maxSeconds)))
    failure = 'unpaced work is incomplete, empty, outside its extent or lacks the required actual mission outcome'
  return {
    summary: { schema: 'aerolink.fms-perf-browser-unpaced-run.v1', status: failure ? 'FAILED' : 'OBSERVED', failure,
      qualification: 'NOT_CALIBRATED', inference: 'DISABLED', topology: input.topology,
      work: { attempted, completed, warmupFrames: input.warmupFrames, scoredFrames: durations.length,
        totalSimSeconds: completed / 4, scoredSimSeconds: durations.length / 4, fixedTotalFrames: input.totalFrames },
      boundaries: { initial, warmup, end }, durations, scoredWallSeconds,
      throughput: scoredWallSeconds && scoredWallSeconds > 0 ? durations.length / 4 / scoredWallSeconds : null,
      profile: input.profile, dataset: { cycle: runner.context.cycle, source: runner.context.data, status: 'OBSERVED_PUBLIC_CONTEXT' },
      gps: { model: modules.gps.GPS_MODEL_VERSION, seeds: seedReceipts, runnerSeeds: runner.gpsSeeds },
      runner: { outcome: runner.outcome, finished: runner.finished, endedAfter: runner.endedAfter,
        report: modules.scenario.reportMarkdown(runner) },
      sync: input.topology === 'dual' ? { totalFrames: syncTotal, scoredFrames: syncScored,
        totalResidency: completed ? syncTotal / completed : null, scoredResidency: durations.length ? syncScored / durations.length : null, firstDrop } : null },
    // Real caller retains this exact final state through GC/heap observation, then releases it.
    retained: { composed, kernel, runner },
  }
}
