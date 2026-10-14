import { expect, test } from '@playwright/test'
import { readFileSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { scenarioDigest, type Scenario } from '../src/fmsCdu/scenario'
import { ACTIVE_PROFILE } from '../src/fmsCdu/profile'
import { GPS_MODEL_VERSION } from '../src/fmsCdu/gps'
import { apiLogin, login, selectProgram } from '../tests/auth'
import { AccountingRefusal, BROWSER_RUN_SCHEMA, browserWindowAccounting, captureEvidence, finiteMissionEvidence, installBrowserRecorder, measureWindow, w3PrefixAccounting, w3RunSummary, w3SetupEvidence, type W3Observation, type RecordedWindow, type TimelineEntry, type WindowAccounting, type WindowProbe } from './browser-accounting.ts'
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
const workload = process.env.AEROLINK_PERF_WORKLOAD
const topology = process.env.AEROLINK_PERF_TOPOLOGY
const family = protocol.routeContract.workloads.find((entry: { id: string }) => entry.id === workload)
const declaredRoute = family?.routes['browser-paced']
if (!family || declaredRoute?.status !== 'IMPLEMENTED' || topology !== 'dual')
  throw new Error(`unsupported product UI route ${workload}/browser-paced/${topology}`)
const route = { schema: protocol.routeContract.schema, id: `${workload}/browser-paced/${topology}/v${declaredRoute.version ?? 1}`, workload, scenario: family.scenario, topology, ...declaredRoute }
const finite = declaredRoute.scored.kind === 'successful-scenario-end-after-warmup'
const w3 = workload === 'W3'
type Recorder = { __fmsPerf: { armW3(): void; w3Snapshot(): Omit<W3Observation, 'identity' | 'setupReport'>; armFinite(): void; finiteStatus: { started: boolean; ended: boolean; error: string | null }; begin(): number; boundary(label: string): unknown; keyDispatch(expectedKey?: string): void; keyOutcome(): void; drain(): RecordedWindow['events']; completed: number; finish(): RecordedWindow; timeline: TimelineEntry[] } }
// The protocol declares the run schema this owner writes; a mismatch is a protocol edit without a harness edit.
if (protocol.browser.accounting.schema !== BROWSER_RUN_SCHEMA) throw new Error(`protocol.json declares ${protocol.browser.accounting.schema}; this owner writes ${BROWSER_RUN_SCHEMA}`)

test.use({
  viewport: { width: protocol.browser.viewport.width, height: protocol.browser.viewport.height },
  deviceScaleFactor: protocol.browser.viewport.deviceScaleFactor,
  launchOptions: { args: protocol.browser.chromiumArgs },
})

test('browser frame cost', async ({ page, request, context, headless }) => {
  test.setTimeout(w3 ? protocol.browser.w3Recipe.executorLimitSeconds * 1000 : 15 * 60_000)
  if (!result || !configuration || !protocol.browser.rates.includes(rate)) throw new Error('AEROLINK_PERF_RATE, _CONFIGURATION and _RESULT are required')
  const windowSim: number = protocol.browser.windowSimSeconds[String(rate)]
  const targetFrames = finite ? null : windowSim * 4
  const { timeline } = protocol.browser.accounting
  await context.addInitScript(installBrowserRecorder, { slice: timeline.slice, discard: timeline.discard, clock: protocol.browser.capture.clockTimeline,
    ...(finite ? { close: declaredRoute.closeTimeline } : {}), ...(w3 ? { close: 'aerolink.fms-bench.pacing-close', successFrame: protocol.browser.w3Recipe.successFrame, frozen: protocol.browser.w3Recipe.frozenFrame } : {}) })

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
  if (!w3) await scenarios.getByLabel('Scenario', { exact: true }).selectOption(family.scenario)
  const sourceScenario: Scenario | null | undefined = w3 ? protocol.headless.workloads.find((entry: any) => entry.id === 'W3').definition : finite ? SCENARIO_LIBRARY.find(entry => entry.id === family.scenario) : null
  if ((finite || w3) && !sourceScenario) throw new Error('declared public scenario source is missing')
  // Public import needs a distinct ID: the card deliberately resolves built-ins before custom scenarios.
  const scenario = sourceScenario ? { ...sourceScenario, id: `${sourceScenario.id}-${w3 ? 'perf-prefix-v1' : 'perf-finite-v2'}`,
    startTime: declaredRoute.scenarioStartUtc, profile: sourceScenario.profile ?? ACTIVE_PROFILE.id } : null
  const inputBytes = scenario ? JSON.stringify(scenario, null, 2) : null
  let expectedProfileSummary: string | null = null
  let expectedGpsModel = GPS_MODEL_VERSION
  if (scenario) {
    const arm = process.env.AEROLINK_PERF_ARM
    if (!arm) throw new Error('finite browser capture requires its declared arm source')
    // Same arm-module loading contract as the headless owner. Never borrow the harness model's profile identity.
    const profiles = await import(pathToFileURL(join(arm, 'src/fmsCdu/profile.ts')).href)
    const selected = profiles.profileById(scenario.profile)
    if (!selected || selected.id !== scenario.profile) throw new Error('finite input profile is not available in the declared arm')
    expectedProfileSummary = profiles.profileSummary(selected)
    if(w3) expectedGpsModel = (await import(pathToFileURL(join(arm,'src/fmsCdu/gps.ts')).href)).GPS_MODEL_VERSION
  }
  const downloadText = async (name: string) => {
    const pending = page.waitForEvent('download')
    await scenarios.getByRole('button', { name, exact: true }).click()
    const stream = await (await pending).createReadStream()
    if (!stream) throw new Error('public download has no retained bytes')
    const chunks: Buffer[] = []
    for await (const chunk of stream) chunks.push(Buffer.from(chunk))
    return Buffer.concat(chunks).toString('utf8')
  }
  if (inputBytes) {
    writeFileSync(`${result}.scenario.json`, inputBytes)
    await scenarios.getByLabel('Scenario file').setInputFiles({ name: 'perf-finite-v2.json', mimeType: 'application/json', buffer: Buffer.from(inputBytes) })
    await expect(scenarios.getByLabel('Scenario', { exact: true })).toHaveValue(scenario!.id)
    expect(await downloadText('Save as JSON'), 'the actual public selection is the exact frozen input').toBe(inputBytes)
    if (finite) await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.armFinite())
  }
  // W3 recipe amendment 1 (Fly at F0 by construction): once the t=0 setup has finished, the bench pauses and its 250 ms
  // frozen-clock interval runs (clock and computers advance). A Fly that arrives after the first tick is refused as late,
  // so any child work between Run and Fly made F0 a race. The public Fly button is therefore clicked in the same page task
  // that commits the finished setup (a MutationObserver armed by the public Run click's own dispatch), before any timer
  // task can run. The setup report is downloaded and verified after Pause instead: its bytes are fixed once produced.
  if (w3) await page.evaluate(() => {
    const w = window as unknown as Recorder & { __w3Fly: { dispatchMs: number | null; clickMs: number | null; error: string | null } }
    w.__w3Fly = { dispatchMs: null, clickMs: null, error: null }
    const onRun = (event: Event) => { if ((event.target as Element | null)?.closest?.('button')?.textContent?.trim() === 'Run the scenario') w.__w3Fly.dispatchMs = performance.now() }
    document.addEventListener('click', onRun, true)
    const observer = new MutationObserver(() => {
      if (w.__w3Fly.dispatchMs === null || !document.querySelector('.fmsScenarioResult')) return
      observer.disconnect(); document.removeEventListener('click', onRun, true)
      const fly = [...document.querySelectorAll('button')].find(button => button.textContent?.trim() === 'Fly')
      if (!fly) { w.__w3Fly.error = 'no public Fly control after the finished setup'; return }
      w.__fmsPerf.armW3(); w.__w3Fly.clickMs = performance.now(); fly.click()
    })
    observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true })
  })
  await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
  if (w3) {
    const identity = JSON.parse(process.env.AEROLINK_PERF_W3_IDENTITY ?? 'null')
    if (!identity) throw new Error('W3 requires its exact original/effective/overlay/build/dependency identity')
    const expected = { scenario: scenario!.id, digest: scenarioDigest(scenario!), startUtc: scenario!.startTime!, profile: scenario!.profile!, profileSummary: expectedProfileSummary!,
      steps: scenario!.steps.length, statuses: ['DONE', 'PASS'] as ('DONE' | 'PASS')[], maxSimSeconds: 1800,
      gps: { model: expectedGpsModel, constellation: 1, receivers: [101,202] } }
    await page.waitForFunction(() => { const fly = (window as unknown as { __w3Fly: { clickMs: number | null; error: string | null } }).__w3Fly; return fly.clickMs !== null || fly.error !== null }, null, { timeout: 60_000 })
    const fly = await page.evaluate(() => (window as unknown as { __w3Fly: { dispatchMs: number | null; clickMs: number | null; error: string | null } }).__w3Fly)
    let raw: Omit<W3Observation, 'identity' | 'setupReport'>, refusal: string | null = fly.error, key = 0
    if (!refusal) try { do {
      raw = await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.w3Snapshot())
      const first = raw.frames[0]
      if (first && (first.entry.kernelTime !== 0 || first.entry.unitClockMs !== Date.parse(expected.startUtc))) { refusal = 'first actual Fly ENTRY was late; no rewind or retry'; break }
      if (raw.closes.length) { refusal = 'unexpected stop before ordinary public Pause'; break }
      if ((raw.frames.at(-1)?.ordinal ?? 0) >= 7200) break
      // Real non-commanding CDU page keys; before/after is actual committed aria-label, not a BRT proxy.
      const expectedKey=key++ % 2 ? 'LEGS' : 'PROG'
      await page.evaluate(key => (window as unknown as Recorder).__fmsPerf.keyDispatch(key),expectedKey)
      await page.locator(`.fmsCduKey[data-fn="${expectedKey}"]`).first().click()
      await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
      await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.keyOutcome())
      await page.waitForTimeout(1000)
    } while (true) } catch(error) { refusal=error instanceof Error?error.message:String(error) }
    const pause = page.getByRole('button', { name: 'Pause', exact: true })
    try { if (await pause.count()) await pause.click() } catch(error) { refusal??=error instanceof Error?error.message:String(error) } // ordinary public cancellation; retain actual batch overrun/discard
    await page.evaluate(() => new Promise<void>(resolve => queueMicrotask(resolve)))
    const beforeHeap = await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.w3Snapshot())
    const cdp = await context.newCDPSession(page); let gc = 'UNAVAILABLE'
    try { await cdp.send('HeapProfiler.collectGarbage'); gc = 'CDP_COLLECTED' } catch { /* retain availability */ }
    const heap = await page.evaluate(() => ({ utcMs:Date.now(),monotonicMs:performance.now(),usedBytes:(performance as any).memory?.usedJSHeapSize ?? null,
      observations:(window as unknown as Recorder).__fmsPerf.w3Snapshot() }))
    // The finished setup's report, taken now: its bytes were fixed when the setup finished; a mismatch still refuses.
    let setupReport = ''
    try { setupReport = await downloadText('Download run report'); writeFileSync(`${result}.setup.md`, setupReport); w3SetupEvidence(setupReport, expected) }
    catch(error) { refusal ??= error instanceof Error ? error.message : String(error) }
    const observation: W3Observation = { ...heap.observations, identity, setupReport }
    let accounting: ReturnType<typeof w3PrefixAccounting> | null = null
    try { accounting = w3PrefixAccounting(observation,expected,identity) } catch(error) { refusal ??= error instanceof Error ? error.message : String(error) }
    const summary = w3RunSummary({ schema:protocol.browser.w3Recipe.schema,route,rate,configuration:configuration.id,identity,expected,observation,accounting,refusal,
      scenarioInput:{sourceScenario:family.scenario,bytes:inputBytes,sha256:createHash('sha256').update(inputBytes!).digest('hex')},
      heap:{status:gc==='CDP_COLLECTED'&&heap.usedBytes!==null?'CONTEXTUAL_OBSERVATION':'UNAVAILABLE',retainedF7200:'UNAVAILABLE',method:gc,utcMs:heap.utcMs,monotonicMs:heap.monotonicMs,usedBytes:heap.usedBytes,
        beforeFrozen:beforeHeap.frozen,afterFrozen:observation.frozen,frozenDuringCollection:observation.frozen.length-beforeHeap.frozen.length,flightClose:observation.closes},
      browser:{version:context.browser()?.version()??null,headless,viewport:protocol.browser.viewport,
        fly:{control:'public Fly button, same task as the finished-setup commit',runDispatchMs:fly.dispatchMs,flyClickMs:fly.clickMs,setupReport:'downloaded after Pause'}} })
    writeFileSync(result,JSON.stringify(summary,null,2))
    if(refusal) throw new Error(refusal)
    expect(accounting?.status).toBe('OBSERVED'); return
  }
  if (finite) await page.waitForFunction(() => {
    const status = (window as unknown as Recorder).__fmsPerf.finiteStatus
    return status.started || status.ended
  })
  else {
    await page.waitForTimeout(protocol.browser.warmupWallSeconds * 1000)
    await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.boundary('warmupEnd'))
  }
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
  const interaction = async () => {
    // BRT uses the unit's simulation clock. At 64x these two wall-separated presses need not dim back.
    const press = async () => {
      if (!finite) {
        await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.keyDispatch())
        await brt.click(); presses += 1
        await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.keyOutcome())
        return
      }
      const allowed = await page.evaluate(finite => {
        const recorder = (window as unknown as Recorder).__fmsPerf
        if (finite && recorder.finiteStatus.ended) return false
        recorder.keyDispatch(); return true
      }, finite)
      if (!allowed) return
      await brt.click(); presses += 1
      await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.keyOutcome())
    }
    await press()
    await page.waitForTimeout(1000)
    await press()
    await page.waitForTimeout(5000)
  }
  let measured: RecordedWindow, steps = 0, stalled = false
  if (finite) {
    while (!(await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.finiteStatus)).ended) { await interaction(); steps++ }
    measured = await probe.finish()
  } else ({ recorded: measured, steps, stalled } = await measureWindow(probe, targetFrames!, interaction, () => page.waitForTimeout(250)))
  // Allow the last input's next paint, then drain the actual observer without extending the scored window.
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
  measured.events = await page.evaluate(() => (window as unknown as Recorder).__fmsPerf.drain())
  const atEnd = await otw()
  const finished = await scenarios.locator('.fmsScenarioResult').count()
  // Refuses (fails the run) unless the timeline conserves every admitted frame of one epoch across the window.
  let accounting: WindowAccounting | null = null, refusal: string | null = null
  try { accounting = browserWindowAccounting({ rate, begin: measured.begin, end: measured.end, timeline: measured.timeline, tasks: measured.tasks, ...(finite ? { closure: measured.closes?.length === 1 ? measured.closes[0] : {} } : {}) }) }
  catch (error) { if (!(error instanceof AccountingRefusal)) throw error; refusal = error.message }

  let mission: ReturnType<typeof finiteMissionEvidence> | null = null, report: string | null = null
  if (finite) {
    const publicReport = scenarios.getByRole('button', { name: 'Download run report', exact: true })
    report = await publicReport.count() === 1 && await publicReport.isEnabled() ? await downloadText('Download run report') : ''
    writeFileSync(`${result}.report.md`, report)
    try {
      if (!accounting) throw new AccountingRefusal('finite mission has no conserved window')
      mission = finiteMissionEvidence(measured, accounting, report, { scenario: scenario!.id, digest: scenarioDigest(scenario!),
        startUtc: scenario!.startTime!, profile: scenario!.profile!, profileSummary: expectedProfileSummary!, steps: scenario!.steps.length, statuses: scenario!.steps.map(step => step.action.kind.startsWith('expect') ? 'PASS' : 'DONE'), maxSimSeconds: scenario!.maxSeconds,
        gps: { model: GPS_MODEL_VERSION, constellation: 1, receivers: [101, 202] } })
    } catch (error) { if (!(error instanceof AccountingRefusal)) throw error; refusal ??= error.message }
  }
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
    schema: BROWSER_RUN_SCHEMA, accountingRevision: protocol.browser.accounting.revision, route, workload, topology, scenario: family.scenario, rate, configuration: configuration.id,
    windowSimSeconds: accounting?.simSeconds ?? null, wallSeconds: accounting?.wallSeconds ?? null, throughput: accounting?.achievedRate ?? null,
    accounting, refusal, window: { begin: measured.begin, end: measured.end, steps, stalled, targetFrames },
    capture: captureEvidence(measured, accounting), rawObservation: measured,
    ...(finite ? { finiteAccountingSchema: 'aerolink.fms-perf-finite-window.v1', executedScenario: scenario!.id, mission, report, inference: 'DISABLED', qualification: 'NOT_CALIBRATED', scenarioInput: { sourceScenario: family.scenario, bytes: inputBytes, sha256: createHash('sha256').update(inputBytes!).digest('hex') } } : {}),
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
  if (finite) { expect(mission?.status).toBe('OBSERVED'); return }
  expect(stalled, 'every step of the window added completed frames').toBe(false)
  expect(finished, 'the mission is still running at the end of the window').toBe(0)
  expect(accounting!.frames.discarded, 'nothing stopped the pacing epoch inside the window').toBe(0)
})
