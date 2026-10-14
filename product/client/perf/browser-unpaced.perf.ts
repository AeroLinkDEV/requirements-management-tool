import { expect, test } from '@playwright/test'
import { readFileSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { createHash } from 'node:crypto'
import { createServer } from 'vite'
import { runBrowserUnpaced, type UnpacedInput } from './browser-unpaced.ts'
import { frameSummary } from './stats.ts'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { MISSION_87N_OFFSHORE_SAR } from '../src/fmsCdu/heliDemo'
import { ACTIVE_PROFILE } from '../src/fmsCdu/profile'

const protocol = JSON.parse(readFileSync(new URL('./protocol.json', import.meta.url), 'utf8'))
test.use({ launchOptions: { args: protocol.browser.chromiumArgs } })

test('browser unpaced public composition', async ({ page, context }) => {
  test.setTimeout(15 * 60_000)
  const arm = process.env.AEROLINK_PERF_ARM, output = process.env.AEROLINK_PERF_RESULT
  const workload = process.env.AEROLINK_PERF_WORKLOAD, topology = process.env.AEROLINK_PERF_TOPOLOGY
  if (!arm || !output || !workload || !['single', 'dual'].includes(topology ?? '')) throw new Error('explicit ARM, RESULT, WORKLOAD and TOPOLOGY required')
  const family = protocol.routeContract.workloads.find((entry: any) => entry.id === workload)
  const declared = family?.routes['browser-unpaced']
  if (declared?.status !== 'IMPLEMENTED') throw new Error('unimplemented browser-unpaced route')
  const source = protocol.headless.workloads.find((entry: any) => entry.id === workload)?.definition
    ?? [...SCENARIO_LIBRARY, MISSION_87N_OFFSHORE_SAR].find(entry => entry.id === family.scenario)
  if (!source || source.id !== family.scenario) throw new Error('declared frozen scenario source is missing')
  const frozen = { ...source, startTime: declared.scenarioStartUtc, profile: source.profile ?? ACTIVE_PROFILE.id }
  writeFileSync(`${output}.scenario.json`, JSON.stringify(frozen))
  const load = (name: string) => import(pathToFileURL(join(arm, 'src/fmsCdu', name)).href)
  const [profiles, gps] = await Promise.all(['profile.ts', 'gps.ts'].map(load))
  const profile = profiles.profileById(frozen.profile)
  if (!profile) throw new Error('frozen profile unavailable in ARM')
  const input: UnpacedInput = { scenarioJson: JSON.stringify(frozen), topology: topology as 'single' | 'dual',
    warmupFrames: declared.warmup.value, totalFrames: declared.scored.kind === 'fixed-total-after-warmup' ? declared.scored.maxTotalSimSeconds * 4 : null,
    maximumFrames: declared.scored.maxTotalSimSeconds * 4 + 2, profile: { id: profile.id, summary: profiles.profileSummary(profile) },
    seeds: { constellation: 1, receivers: [101, 202] }, gpsModel: gps.GPS_MODEL_VERSION }
  const port = Number(process.env.AEROLINK_PERF_PORT)
  if (!Number.isSafeInteger(port) || port < 1024 || port > 65535) throw new Error('explicit private AEROLINK_PERF_PORT required')
  const origin = `http://127.0.0.1:${port}`
  const server = await createServer({ root: arm, configFile: resolve(arm, 'vite.config.ts'), logLevel: 'error',
    server: { host: '127.0.0.1', port, strictPort: true, hmr: false } })
  try {
    await server.listen()
    // Inert measurement shell, not a rendered product route. All model modules come from the real ARM server.
    await page.route(`${origin}/__unpaced_measurement__`, route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Unpaced public composition measurement</title>' }))
    await page.goto(`${origin}/__unpaced_measurement__`)
    const observed = await page.evaluate(async ({ origin, program, input }) => {
      const [adapter, kernel, scenario, profiles, gps] = await Promise.all([
        'kernel/legacyPlantAdapter.ts', 'kernel/kernel.ts', 'scenario.ts', 'profile.ts', 'gps.ts',
      ].map(name => import(`${origin}/src/fmsCdu/${name}`)))
      const execute = new Function(`return (${program})`)() as typeof runBrowserUnpaced
      const result = execute({ adapter, kernel, scenario, profiles, gps }, input,
        { now: () => performance.now(), utcNow: () => Date.now(), timeOrigin: performance.timeOrigin })
      ;(window as any).__fmsUnpacedRetained = result
      return result.summary
    }, { origin, program: runBrowserUnpaced.toString(), input })
    const cdp = await context.newCDPSession(page)
    let gc = 'UNAVAILABLE'
    try { await cdp.send('HeapProfiler.collectGarbage'); await cdp.send('HeapProfiler.collectGarbage'); gc = 'CDP_COLLECTED' } catch { /* explicit unavailable metric */ }
    const heap = await page.evaluate(() => {
      const run = (window as any).__fmsUnpacedRetained
      const memory = (performance as any).memory
      return { utcMs: Date.now(), monotonicMs: performance.now(), timeOrigin: performance.timeOrigin,
        kernelTime: run.retained.kernel.now, unitClockMs: run.retained.kernel.unitClockMs,
        completed: run.summary.work.completed, usedBytes: Number.isFinite(memory?.usedJSHeapSize) ? memory.usedJSHeapSize : null }
    })
    const unchanged = heap.kernelTime === observed.boundaries.end.kernelTime && heap.unitClockMs === observed.boundaries.end.unitClockMs && heap.completed === observed.work.completed
    const route = { schema: protocol.routeContract.schema, id: `${workload}/browser-unpaced/${topology}/v1`, workload, scenario: family.scenario,
      topology, host: 'browser-unpaced', ...declared }
    const summary = { ...observed, route, workload, scenario: family.scenario, input,
      inputSha256: createHash('sha256').update(input.scenarioJson).digest('hex'),
      frames: observed.durations.length ? frameSummary(observed.durations) : null,
      heap: { status: gc === 'CDP_COLLECTED' && unchanged && heap.usedBytes !== null ? 'OBSERVED_RETAINED_STATE' : 'UNAVAILABLE', method: gc, unchanged, ...heap },
      browser: { version: context.browser()?.version() ?? null, executionHost: 'inert-shell-public-arm-modules', uiRendering: 'NOT_MEASURED', pacing: 'NOT_APPLICABLE' } }
    writeFileSync(output, JSON.stringify(summary, null, 2))
    await page.evaluate(() => { delete (window as any).__fmsUnpacedRetained })
    expect(summary.status, summary.failure ?? 'actual public work completed').toBe('OBSERVED')
    expect(unchanged, 'retained final state did not advance through heap collection').toBe(true)
    expect(summary.heap.status, 'unavailable retained heap remains unqualified').toBe('OBSERVED_RETAINED_STATE')
  } finally { await page.unrouteAll({ behavior: 'wait' }); await server.close() }
})
