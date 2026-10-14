import { expect, test } from '@playwright/test'
import { readFileSync, writeFileSync } from 'node:fs'
import { apiLogin, login, selectProgram } from '../tests/auth'
import { AccountingRefusal, BROWSER_RUN_SCHEMA, browserWindowAccounting, captureEvidence, installBrowserRecorder, measureWindow, type RecordedWindow, type TimelineEntry, type WindowAccounting, type WindowProbe } from './browser-accounting.ts'
import { frameSummary } from './stats.ts'

// One browser run (#1510 I0a; accounting revision 2, #1557): the 87N offshore SAR mission from its planned start, at
// one rate, in one outside-view and SVS configuration, in a fresh browser against a fresh API serving one arm's
// production build. The orchestrator (run-perf.ts) spawns each run as its own `playwright test` invocation.
//
// Instrumentation is an init script (installBrowserRecorder). The simulated work is the frames the kernel completed,
// read from the bench's own pacing timeline (User Timing measures, protocol.json browser.accounting.timeline), and
// conserved by browserWindowAccounting; an arm without that timeline refuses. The window opens and closes in
// page.evaluate tasks, so no pacing slice can straddle it; it ends early, and is accounted, when a step adds no
// completed frames (measureWindow). Simulation cost (slices) is separate from continuation cost
// (yield gaps between a batch's slices, which contain the earlier task's tail, so the two are not added; the timer
// task's tail after its slice: sync work and the microtask render of a
// published frame). Long tasks, Long Animation Frames, rAF and Event Timing (key -> next paint for a benign CDU key,
// durations rounded to 8 ms by the browser) are attributed separately; JS heap from performance.memory
// (--enable-precise-memory-info) after a forced collection. Worker backlog (D10 12.2) is not applicable before I3.

const protocol = JSON.parse(readFileSync(new URL('./protocol.json', import.meta.url), 'utf8'))
const rate = Number(process.env.AEROLINK_PERF_RATE)
const configuration = protocol.browser.configurations.find((entry: { id: string }) => entry.id === process.env.AEROLINK_PERF_CONFIGURATION)
const result = process.env.AEROLINK_PERF_RESULT
type Recorder = { __fmsPerf: { begin(): number; boundary(label: string): unknown; keyDispatch(): void; keyOutcome(): void; drain(): RecordedWindow['events']; completed: number; finish(): RecordedWindow; timeline: TimelineEntry[] } }
// The protocol declares the run schema this owner writes; a mismatch is a protocol edit without a harness edit.
if (protocol.browser.accounting.schema !== BROWSER_RUN_SCHEMA) throw new Error(`protocol.json declares ${protocol.browser.accounting.schema}; this owner writes ${BROWSER_RUN_SCHEMA}`)

test.use({
  viewport: { width: protocol.browser.viewport.width, height: protocol.browser.viewport.height },
  deviceScaleFactor: protocol.browser.viewport.deviceScaleFactor,
  launchOptions: { args: protocol.browser.chromiumArgs },
})

test('browser frame cost', async ({ page, request, context, headless }) => {
  test.setTimeout(15 * 60_000)
  if (!result || !configuration || !protocol.browser.rates.includes(rate)) throw new Error('AEROLINK_PERF_RATE, _CONFIGURATION and _RESULT are required')
  const windowSim: number = protocol.browser.windowSimSeconds[String(rate)]
  const targetFrames = windowSim * 4
  const { timeline } = protocol.browser.accounting
  await context.addInitScript(installBrowserRecorder, { slice: timeline.slice, discard: timeline.discard, clock: protocol.browser.capture.clockTimeline })

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
  const brt = page.locator('.fmsCduKey[data-fn="BRT"]').first()
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
  await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.boundary('warmupEnd'))
  const otw = async () => configuration.outsideView
    ? { frames: Number(await scene.getAttribute('data-frames') ?? 0), tilesLoaded: await scene.getAttribute('data-tiles-loaded'),
      tileQueue: await scene.getAttribute('data-tile-queue'), causes: await scene.getAttribute('data-frame-causes') }
    : null
  const atWarmup = await otw()

  const probe: WindowProbe = {
    begin: () => page.evaluate(() => (window as unknown as Recorder).__fmsPerf.begin()),
    completed: () => page.evaluate(() => (window as unknown as Recorder).__fmsPerf.completed),
    finish: () => page.evaluate(() => (window as unknown as Recorder).__fmsPerf.finish()),
    timeline: () => page.evaluate(() => (window as unknown as Recorder).__fmsPerf.timeline),
  }
  let presses = 0
  const { recorded: measured, steps, stalled } = await measureWindow(probe, targetFrames, async () => {
    // BRT uses the unit's simulation clock. At 64x these two wall-separated presses need not dim back.
    const press = async () => {
      await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.keyDispatch())
      await brt.click(); presses += 1
      await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.keyOutcome())
    }
    await press()
    await page.waitForTimeout(1000)
    await press()
    await page.waitForTimeout(5000)
  }, () => page.waitForTimeout(250))
  // Allow the last input's next paint, then drain the actual observer without extending the scored window.
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
  measured.events = await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.drain())
  const atEnd = await otw()
  const finished = await scenarios.locator('.fmsScenarioResult').count()
  // Refuses (fails the run) unless the timeline conserves every admitted frame of one epoch across the window.
  let accounting: WindowAccounting | null = null, refusal: string | null = null
  try { accounting = browserWindowAccounting({ rate, begin: measured.begin, end: measured.end, timeline: measured.timeline, tasks: measured.tasks }) }
  catch (error) { if (!(error instanceof AccountingRefusal)) throw error; refusal = error.message }

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
  const summary = {
    schema: BROWSER_RUN_SCHEMA, accountingRevision: protocol.browser.accounting.revision, scenario: protocol.browser.scenario, rate, configuration: configuration.id,
    windowSimSeconds: accounting?.simSeconds ?? null, wallSeconds: accounting?.wallSeconds ?? null, throughput: accounting?.achievedRate ?? null,
    accounting, refusal, window: { begin: measured.begin, end: measured.end, steps, stalled, targetFrames },
    capture: captureEvidence(measured, accounting), rawObservation: measured,
    longTasks: { count: measured.longTasks.length, totalMs: measured.longTasks.reduce((a, b) => a + b, 0) },
    raf: measured.rafIntervals.length ? frameSummary(measured.rafIntervals) : null,
    longAnimationFrames: { count: measured.loaf.length, p95Ms: measured.loaf.length ? frameSummary(measured.loaf.map(entry => entry.duration)).p95 : null },
    interaction: { key: protocol.browser.interaction.key, presses, entries: measured.events, resolutionStepMs: step, exemptFromOnePercentTarget: true },
    heapUsedBytesAfterGc: heap, outsideView: { atWarmup, atEnd }, scenarioFinishedInWindow: finished > 0,
    browser: { version: context.browser()?.version() ?? null, headless, renderer, viewport: protocol.browser.viewport },
  }
  // Written before the end checks: a failed run keeps its numbers (the report scores passed runs only).
  writeFileSync(result, JSON.stringify(summary, null, 2))
  if (refusal) throw new Error(refusal)
  expect(stalled, 'every step of the window added completed frames').toBe(false)
  expect(finished, 'the mission is still running at the end of the window').toBe(0)
  expect(accounting!.frames.discarded, 'nothing stopped the pacing epoch inside the window').toBe(0)
})
