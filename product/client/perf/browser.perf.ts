import { expect, test } from '@playwright/test'
import { readFileSync, writeFileSync } from 'node:fs'
import { apiLogin, login, selectProgram } from '../tests/auth'
import { frameSummary } from './stats.ts'

// One browser run (#1510 I0a): the 87N offshore SAR mission from its planned start, at one rate, in one outside-view
// and SVS configuration, in a fresh browser against a fresh API serving one arm's production build. The orchestrator
// (run-perf.ts) spawns each run as its own `playwright test` invocation.
//
// Instrumentation is an init script only; the application has no measurement seam:
// - the 250 ms simulation interval's callbacks are wrapped, and each one's end is taken in a microtask queued after
//   the callback, so React's microtask render of the tick is included;
// - requestAnimationFrame is wrapped (frame intervals), and Long Animation Frames and long tasks are observed;
// - Event Timing (key -> next paint) for a benign CDU key, whose durations the browser rounds to 8 ms;
// - JS heap from performance.memory (--enable-precise-memory-info) after a forced collection.
// The bench has no unpaced mode (only 1/4/16/64x on its 250 ms timer), so browser throughput is paced: a deviation
// from D10 12.2 recorded in the report. At 64x every callback is expected to be a long task.

const protocol = JSON.parse(readFileSync(new URL('./protocol.json', import.meta.url), 'utf8'))
const rate = Number(process.env.AEROLINK_PERF_RATE)
const configuration = protocol.browser.configurations.find((entry: { id: string }) => entry.id === process.env.AEROLINK_PERF_CONFIGURATION)
const result = process.env.AEROLINK_PERF_RESULT

test.use({
  viewport: { width: protocol.browser.viewport.width, height: protocol.browser.viewport.height },
  deviceScaleFactor: protocol.browser.viewport.deviceScaleFactor,
  launchOptions: { args: protocol.browser.chromiumArgs },
})

type Measured = {
  callbacks: number[]; ticks: number; rafIntervals: number[]; longTasks: number[]; loaf: { duration: number; blocking: number }[]
  events: { name: string; duration: number; processing: number; delay: number; interaction: number }[]; wall: number
}

test('browser frame cost', async ({ page, request, context }) => {
  test.setTimeout(15 * 60_000)
  if (!result || !configuration || !protocol.browser.rates.includes(rate)) throw new Error('AEROLINK_PERF_RATE, _CONFIGURATION and _RESULT are required')
  const windowSim: number = protocol.browser.windowSimSeconds[String(rate)]
  const targetCallbacks = Math.round((windowSim * 4) / rate)

  await context.addInitScript(() => {
    if (window !== window.top) return
    const state = {
      measuring: false, begin: 0, end: 0, ticks: 0, callbacks: [] as number[], lastFrame: -1, rafIntervals: [] as number[],
      longTasks: [] as { start: number; duration: number }[], loaf: [] as { start: number; duration: number; blocking: number }[],
      events: [] as { start: number; name: string; duration: number; processing: number; delay: number; interaction: number }[],
    }
    const nativeInterval = window.setInterval.bind(window)
    window.setInterval = ((handler: TimerHandler, timeout?: number, ...args: unknown[]) => {
      if (typeof handler !== 'function' || timeout !== 250) return nativeInterval(handler, timeout, ...args)
      return nativeInterval((...callArgs: unknown[]) => {
        const start = performance.now()
        try { (handler as (...a: unknown[]) => void)(...callArgs) } finally {
          queueMicrotask(() => {
            const end = performance.now()
            if (state.measuring) { state.ticks += 1; state.callbacks.push(end - start) }
          })
        }
      }, timeout)
    }) as typeof window.setInterval
    const nativeRaf = window.requestAnimationFrame.bind(window)
    window.requestAnimationFrame = callback => nativeRaf(time => {
      if (state.measuring && time !== state.lastFrame) {
        if (state.lastFrame >= 0) state.rafIntervals.push(time - state.lastFrame)
        state.lastFrame = time
      }
      callback(time)
    })
    const observe = (type: string, take: (entry: PerformanceEntry) => void, extra: Record<string, unknown> = {}) => {
      try { new PerformanceObserver(list => list.getEntries().forEach(take)).observe({ type, buffered: true, ...extra } as PerformanceObserverInit) } catch { /* unsupported: recorded as absent */ }
    }
    observe('longtask', entry => state.longTasks.push({ start: entry.startTime, duration: entry.duration }))
    observe('long-animation-frame', entry => state.loaf.push({ start: entry.startTime, duration: entry.duration, blocking: (entry as unknown as { blockingDuration: number }).blockingDuration }))
    observe('event', entry => {
      const timing = entry as PerformanceEventTiming
      state.events.push({ start: timing.startTime, name: timing.name, duration: timing.duration, processing: timing.processingEnd - timing.processingStart,
        delay: timing.processingStart - timing.startTime, interaction: (timing as unknown as { interactionId: number }).interactionId })
    }, { durationThreshold: 16 })
    ;(window as unknown as { __fmsPerf: unknown }).__fmsPerf = {
      begin() { state.measuring = true; state.begin = performance.now(); state.ticks = 0; state.callbacks = []; state.rafIntervals = []; state.lastFrame = -1 },
      get ticks() { return state.ticks },
      finish() {
        state.measuring = false; state.end = performance.now()
        const inside = <T extends { start: number }>(list: T[]) => list.filter(entry => entry.start >= state.begin && entry.start <= state.end)
        return { callbacks: state.callbacks, ticks: state.ticks, rafIntervals: state.rafIntervals, wall: (state.end - state.begin) / 1000,
          longTasks: inside(state.longTasks).map(entry => entry.duration), loaf: inside(state.loaf).map(({ duration, blocking }) => ({ duration, blocking })),
          events: inside(state.events).map(({ name, duration, processing, delay, interaction }) => ({ name, duration, processing, delay, interaction })) }
      },
    }
  })

  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const bench = await page.locator('nav[aria-label="Primary navigation"] a[href$="/fms-test-bench"]').first().getAttribute('href')
  expect(bench, 'the navigation offers the FMS Test Bench').toBeTruthy()
  // The pinned conditions are the bench's own remembered choices, set before it mounts.
  await page.evaluate(({ shown, svs, view }) => {
    window.localStorage.setItem('aerolink.fmsCdu.window', JSON.stringify({ shown, ...view }))
    window.localStorage.setItem('aerolink.fmsCdu.svs', svs ? 'on' : 'off')
    window.localStorage.setItem('aerolink.fmsCdu.tab', 'scenarios')
  }, { shown: configuration.outsideView, svs: configuration.svs, view: protocol.browser.outsideView })
  await page.goto(bench!)
  const brt = page.locator('.fmsCduKey[data-fn="BRT"]')
  await expect(brt).toBeVisible({ timeout: 60_000 })
  const scene = page.locator('.fmsOtwScene')
  if (configuration.outsideView) await expect(page.locator('.fmsOtw')).toHaveAttribute('data-status', 'ready', { timeout: 120_000 })
  else await expect(page.locator('.fmsOtw')).toHaveCount(0)
  await expect(page.getByRole('checkbox', { name: 'Synthetic vision' })).toBeChecked({ checked: configuration.svs })

  await page.getByLabel('Simulation rate').selectOption(String(rate))
  const scenarios = page.getByRole('region', { name: 'Scenarios' })
  await scenarios.getByLabel('Scenario', { exact: true }).selectOption(protocol.browser.scenario)
  await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
  await page.waitForTimeout(protocol.browser.warmupWallSeconds * 1000)
  const otw = async () => configuration.outsideView
    ? { frames: Number(await scene.getAttribute('data-frames') ?? 0), tilesLoaded: await scene.getAttribute('data-tiles-loaded'), causes: await scene.getAttribute('data-frame-causes') }
    : null
  const atWarmup = await otw()

  await page.evaluate(() => (window as unknown as { __fmsPerf: { begin(): void } }).__fmsPerf.begin())
  const ticks = () => page.evaluate(() => (window as unknown as { __fmsPerf: { ticks: number } }).__fmsPerf.ticks)
  let presses = 0
  while (await ticks() < targetCallbacks) {
    // A BRT pair brightens and dims back within the 5 s alternation window: no effect on the simulation.
    await brt.click(); presses += 1
    await page.waitForTimeout(1000)
    await brt.click(); presses += 1
    await page.waitForTimeout(5000)
  }
  const measured = await page.evaluate(() => (window as unknown as { __fmsPerf: { finish(): Measured } }).__fmsPerf.finish())
  const atEnd = await otw()
  const finished = await scenarios.locator('.fmsScenarioResult').count()

  const cdp = await context.newCDPSession(page)
  await cdp.send('HeapProfiler.collectGarbage')
  const heap = await page.evaluate(() => (performance as unknown as { memory: { usedJSHeapSize: number } }).memory.usedJSHeapSize)
  const renderer = await page.evaluate(() => {
    const gl = document.createElement('canvas').getContext('webgl')
    const info = gl?.getExtension('WEBGL_debug_renderer_info')
    return gl && info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : null
  })
  // Event Timing durations are rounded to 8 ms by the browser; the observed step is reported, not assumed.
  const durations = measured.events.map(entry => entry.duration)
  const step = durations.length ? durations.reduce((g, d) => { let [a, b] = [g, d]; while (b) [a, b] = [b, a % b]; return a }) : null
  const callbackSimSeconds = (measured.ticks * rate) / 4
  const summary = {
    schema: 'aerolink.fms-perf-browser-run.v1', scenario: protocol.browser.scenario, rate, configuration: configuration.id,
    windowSimSeconds: callbackSimSeconds, wallSeconds: measured.wall, throughput: callbackSimSeconds / measured.wall,
    callbacks: frameSummary(measured.callbacks), longTasks: { count: measured.longTasks.length, totalMs: measured.longTasks.reduce((a, b) => a + b, 0) },
    raf: measured.rafIntervals.length ? frameSummary(measured.rafIntervals) : null,
    longAnimationFrames: { count: measured.loaf.length, p95Ms: measured.loaf.length ? frameSummary(measured.loaf.map(entry => entry.duration)).p95 : null },
    interaction: { key: protocol.browser.interaction.key, presses, entries: measured.events, resolutionStepMs: step, exemptFromOnePercentTarget: true },
    heapUsedBytesAfterGc: heap, outsideView: { atWarmup, atEnd }, scenarioFinishedInWindow: finished > 0,
    browser: { version: context.browser()?.version() ?? null, headless: true, renderer, viewport: protocol.browser.viewport },
  }
  writeFileSync(result, JSON.stringify(summary, null, 2))
  expect(finished, 'the mission is still running at the end of the window').toBe(0)
})
