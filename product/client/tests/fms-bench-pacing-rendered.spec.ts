import { expect, renderedTest as test } from './isolated-client-test'
import type { Page } from '@playwright/test'
import { installPausedClock } from './paused-clock'

// CPU8 failed on both main and head and measures loaded React work. Retained diagnostics are not this ordinary-host gate.
// Logic cannot observe the browser's actual timer tasks, pointer dispatch and completed production frames.
test('64x yields at the first completed frame boundary beyond its slice target while ordinary Pause remains responsive', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.clear()
    const probe = { measuring: false, callbacks: [] as { started: number, duration: number, microtaskTailMs: number | null, kind: string, frames: { start: number, end: number }[] }[], clicks: [] as number[], completedFrames: 0, active: null as number | null }
    ;(window as unknown as { pacingProbe: typeof probe }).pacingProbe = probe
    const wrap = (callback: TimerHandler, kind: string, args: unknown[]) => typeof callback === 'function' ? () => {
      if (!probe.measuring) { callback(...args); return }
      const record = { started: performance.now(), duration: 0, microtaskTailMs: null as number | null, kind, frames: [] as { start: number, end: number }[] }
      probe.active = probe.callbacks.push(record) - 1
      try { callback(...args) }
      finally {
        record.duration = performance.now() - record.started; probe.active = null
        // Tail includes preceding queued React work; it is an attribution sample, not all future rendering.
        queueMicrotask(() => { record.microtaskTailMs = performance.now() - record.started })
      }
    } : callback
    const interval = window.setInterval.bind(window), timeout = window.setTimeout.bind(window)
    window.setInterval = ((callback: TimerHandler, ms?: number, ...args: unknown[]) => interval(ms === 250 ? wrap(callback, 'interval', args) : callback, ms, ...args)) as typeof window.setInterval
    window.setTimeout = ((callback: TimerHandler, ms?: number, ...args: unknown[]) => {
      const simulation = (new Error().stack ?? '').includes('benchPacing')
      return timeout(simulation ? wrap(callback, 'deadline/continuation', args) : callback, ms, ...args)
    }) as typeof window.setTimeout
    addEventListener('click', event => { if ((event.target as HTMLElement).textContent === 'Pause') probe.clicks.push(performance.now()) }, true)
  })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.evaluate(async () => {
    const { FmsKernel } = await import('/src/fmsCdu/kernel/kernel.ts')
    const original = FmsKernel.prototype.advance
    FmsKernel.prototype.advance = function (...args: Parameters<typeof original>) {
      const start = performance.now()
      const done = original.apply(this, args)
      // Independent imposed work makes the whole-frame slice boundary observable on fast hosts too.
      if (!args[1]?.flightFreeze) while (performance.now() - start < 8) { /* minimum complete-frame work */ }
      const probe = (window as unknown as { pacingProbe: { measuring: boolean, completedFrames: number, active: number | null, callbacks: { frames: { start: number, end: number }[] }[] } }).pacingProbe
      if (probe.measuring && !args[1]?.flightFreeze) {
        probe.completedFrames += done
        if (probe.active !== null) probe.callbacks[probe.active].frames.push({ start, end: performance.now() })
      }
      return done
    }
  })
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  const begin = await page.evaluate(() => { (window as unknown as { pacingProbe: { measuring: boolean } }).pacingProbe.measuring = true; return performance.now() })
  await page.waitForTimeout(3000)
  const request = Date.now()
  let error: string | null = null
  try { await page.getByRole('button', { name: 'Pause', exact: true }).click({ timeout: 5000 }) }
  catch (failure) { error = String(failure) }
  const complete = Date.now()
  const observed = await page.evaluate(() => {
    const probe = (window as unknown as { pacingProbe: { measuring: boolean, callbacks: { duration: number, microtaskTailMs: number | null, frames: { start: number, end: number }[] }[], clicks: number[], completedFrames: number } }).pacingProbe
    probe.measuring = false
    return { end: performance.now(), probe: structuredClone(probe) }
  })
  await test.info().attach('actual-pacing-input.json', { body: JSON.stringify({ selectedRate: 64, cpuThrottle: 1, imposedMinimumFrameMs: 8,
    timerScope: 'callbacks scheduled from benchPacing, including progress and presentation callbacks',
    microtaskScope: 'tail sample after callback; preceding queued React work included, later rendering unqualified',
    begin, ...observed, request, complete, inputWallMs: complete - request, error }), contentType: 'application/json' })
  expect(error, 'ordinary Pause requires no forcing, retry or timeout increase').toBeNull()
  expect(observed.probe.clicks).toHaveLength(1)
  expect(observed.probe.completedFrames).toBeGreaterThan(0)
  expect(observed.probe.callbacks.length, 'the actual simulation timer and continuations must be observed').toBeGreaterThan(1)
  const slices = observed.probe.callbacks.filter(callback => callback.frames.length)
  expect(slices.length).toBeGreaterThan(1)
  for (const slice of slices) for (let frame = 0; frame + 1 < slice.frames.length; frame++) {
    expect(slice.frames[frame].end - slice.frames[0].start, 'no later whole frame may start after the slice reaches 32 ms').toBeLessThan(32)
  }
  expect(complete - request, 'ordinary Pause must remain below one second').toBeLessThan(1000)
  expect(Math.max(...slices.map(callback => callback.duration)), 'coarse ordinary-host bound; whole frames cannot be preempted').toBeLessThan(180)
  await expect(page.getByRole('button', { name: 'Fly', exact: true })).toBeVisible()
})

test('the visible achieved speed follows an actual browser load change over its recent completed-frame window', async ({ page }) => {
  await page.addInitScript(() => localStorage.clear())
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.evaluate(async () => {
    const { DualFmsSystem } = await import('/src/fmsCdu/dualFms.ts')
    const original = DualFmsSystem.prototype.step
    const probe = { loaded: false, completions: [] as number[] }
    ;(window as unknown as { loadProbe: typeof probe }).loadProbe = probe
    DualFmsSystem.prototype.step = function (...args) {
      const began = performance.now()
      const result = original.apply(this, args)
      if (probe.loaded) while (performance.now() - began < 80) { /* imposed whole-frame browser work */ }
      probe.completions.push(performance.now())
      return result
    }
  })
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.waitForTimeout(3500)
  const fast = await page.getByLabel('Achieved speed', { exact: true }).innerText()
  expect(Number(fast.split('×')[1]), 'ordinary host positive control before imposed work').toBeGreaterThan(8)
  const changed = await page.evaluate(() => { (window as unknown as { loadProbe: { loaded: boolean } }).loadProbe.loaded = true; return performance.now() })
  await page.waitForTimeout(4500)
  const current = await page.evaluate(() => ({ now: performance.now(),
    text: document.querySelector('[aria-label="Achieved speed"]')?.textContent,
    completions: [...(window as unknown as { loadProbe: { completions: number[] } }).loadProbe.completions] }))
  await test.info().attach('browser-load-change.json', { body: JSON.stringify({ fast, changed, ...current,
    scope: 'functional imposed-load observation; display and independent completion snapshot need not share the exact publication instant' }), contentType: 'application/json' })
  const shown = Number(current.text?.split('×')[1])
  expect(shown).toBeGreaterThan(2)
  expect(shown, '80 ms minimum per whole frame limits current capacity to 3.125x, regardless of earlier throughput').toBeLessThan(3.5)
  const request = Date.now()
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  expect(Date.now() - request).toBeLessThan(1000)
})

// Lifecycle owner: stopping an already paused run must not consume the next deliberate Fly as an auto-pause.
for (const preset of [
  { button: 'Set up KBTV RNAV RWY 15', altitude: 3200 },
  { button: 'Set up 87N COPTER RNAV 190 final', altitude: 1700 },
]) test(`the ${preset.button} preset settles once without advancing its t0 clock`, async ({ page }) => {
  const initial = new Date('2026-09-27T14:00:00Z')
  await installPausedClock(page, initial)
  await page.addInitScript(() => localStorage.clear())
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.evaluate(async () => {
    const { DualFmsSystem } = await import('/src/fmsCdu/dualFms.ts')
    const original = DualFmsSystem.prototype.tick
    const values: { utc: number[], altitude: number[], position: { lat: number, lon: number }[] }[] = []
    ;(window as unknown as { presetTicks: typeof values }).presetTicks = values
    DualFmsSystem.prototype.tick = function () {
      const result = original.call(this)
      values.push({ utc: this.computers.map(fms => fms.now.getTime()), altitude: this.computers.map(fms => fms.physicalAltitude),
        position: this.computers.map(fms => ({ ...fms.truePosition })) })
      return result
    }
  })
  await page.getByRole('tab', { name: 'Nav data', exact: true }).click()
  await page.getByRole('button', { name: preset.button, exact: true }).click()
  const values = await page.evaluate(() => structuredClone((window as unknown as { presetTicks: {
    utc: number[], altitude: number[], position: { lat: number, lon: number }[]
  }[] }).presetTicks))
  expect(values).toHaveLength(1)
  expect(values[0].utc).toEqual([initial.getTime(), initial.getTime()])
  expect(values[0].altitude).toEqual([preset.altitude, preset.altitude])
  expect(values[0].position[0]).toEqual(values[0].position[1])
})

// Lifecycle owner: stopping an already paused run must not consume the next deliberate Fly as an auto-pause.
// The CPU-load cases above cannot see this finished-run bookkeeping path.
for (const stoppedWait of [0, 750]) test(`a paused then stopped run accepts the first deliberate Fly after ${stoppedWait}ms and advances its clock`, async ({ page }) => {
  const start = new Date('2026-09-27T14:00:00Z')
  await installPausedClock(page, new Date(start.getTime() + 1000))
  await page.addInitScript(() => localStorage.clear())
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await observeFlight(page)
  const scenario = { id: 'pacing-paused-stop', title: 'Paused stop', objective: 'Finished-run lifecycle',
    maxSeconds: 200, startTime: start.toISOString(),
    steps: [{ when: { kind: 'time', seconds: 100 }, action: { kind: 'expectLamp', lamp: 'MSG', lit: true } }] }
  await page.getByLabel('Scenario file').setInputFiles({ name: 'paused-stop.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(scenario)) })
  await expect(page.getByLabel('Scenario', { exact: true })).toHaveValue(scenario.id)
  await page.getByLabel('Simulation rate').selectOption('4')
  await page.getByRole('button', { name: 'Run the scenario', exact: true }).click()
  await page.clock.runFor(1000)
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await expect(page.getByText('Run paused: its clock is stopped.')).toBeVisible()
  const paused = await terminalState(page)
  await page.getByRole('button', { name: 'Stop the run', exact: true }).click()
  if (stoppedWait) await page.clock.runFor(stoppedWait)
  const stopped = await terminalState(page)
  expect(stopped.steps, 'stopping a paused run keeps the aircraft frozen').toEqual(paused.steps)
  if (stoppedWait) expect(stopped.ticks.length, 'paused Stop must retain the free-running clock timer').toBeGreaterThan(paused.ticks.length)
  await page.locator('.fmsCduKey[data-key="INIT_REF"]').click()
  await page.locator('.fmsCduKey[data-key="LSK5R"]').click()
  await expect(page.locator('.fmsScenarioResult')).toContainText('STOPPED by the operator')
  const screen = page.locator('.fmsCduScreen')
  const before = await screen.getAttribute('aria-label')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.clock.runFor(1000)
  await expect(page.getByRole('button', { name: 'Pause', exact: true })).toBeVisible()
  expect(await screen.getAttribute('aria-label'), 'the public TIMER clock must advance after the first Fly').not.toBe(before)
})

// Observe existing public integration/clock ports without changing their inputs or results.
// The terminal-boundary contract is zero further flight integration, while the frozen-aircraft clock runs.
const observeFlight = async (page: Page) => page.evaluate(async () => {
  const path = '/src/fmsCdu/dualFms.ts'
  const { DualFmsSystem } = await import(path)
  const values = { steps: [] as number[], ticks: [] as number[] }
  ;(window as unknown as { terminalProbe: typeof values }).terminalProbe = values
  for (const name of ['step', 'tick'] as const) {
    const original = DualFmsSystem.prototype[name]
    DualFmsSystem.prototype[name] = function (...args: unknown[]) {
      const result = original.apply(this, args)
      values[name === 'step' ? 'steps' : 'ticks'].push(this.computers[0].now.getTime())
      return result
    }
  }
})
const terminalState = (page: Page) => page.evaluate(() => structuredClone((window as unknown as {
  terminalProbe: { steps: number[], ticks: number[] }
}).terminalProbe))

for (const alreadyFinished of [false, true]) test(`${alreadyFinished ? 'constructor-complete' : 'operator-stopped'} run integrates no extra terminal tick and permits later Fly`, async ({ page }) => {
  const start = new Date('2026-09-27T14:00:00Z')
  await installPausedClock(page, new Date(start.getTime() + 1000))
  await page.addInitScript(() => localStorage.clear())
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await observeFlight(page)
  const scenario = { id: 'pacing-terminal', title: 'Terminal boundary', objective: 'Exact ending boundary',
    maxSeconds: 200, startTime: start.toISOString(),
    steps: [{ when: { kind: 'time', seconds: alreadyFinished ? 0 : 100 }, action: { kind: 'expectLamp', lamp: 'MSG', lit: true } }] }
  await page.getByLabel('Scenario file').setInputFiles({ name: 'terminal.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(scenario)) })
  await expect(page.getByLabel('Scenario', { exact: true })).toHaveValue(scenario.id)
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Run the scenario', exact: true }).click()
  if (!alreadyFinished) {
    await page.clock.runFor(250)
    await page.getByRole('button', { name: 'Stop the run', exact: true }).click()
  }
  const terminal = await terminalState(page)
  await page.clock.runFor(1000)
  const paused = await terminalState(page)
  expect(paused.steps, 'terminal run must not integrate a further flight tick').toEqual(terminal.steps)
  expect(paused.ticks.length, 'free clock resumes with the aircraft frozen').toBeGreaterThan(terminal.ticks.length)
  await expect(page.getByRole('button', { name: 'Fly', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.clock.runFor(500)
  await expect(page.getByRole('button', { name: 'Pause', exact: true })).toBeVisible()
  expect((await terminalState(page)).steps.length).toBeGreaterThan(paused.steps.length)
})

// Visible fault owner: real INTEGRATE moves the adapter clock before the plant throws. Logic alone cannot prove
// the stopped indicator, absence of further browser work, or explicit first-Fly recovery from that instant.
for (const firstBoundary of ['step', 'tick'] as const) test(`a ${firstBoundary === 'step' ? 'flying' : 'paused flightFreeze'} INTEGRATE fault stops visibly and resumes only on deliberate Fly`, async ({ page }) => {
  const initial = new Date('2026-09-27T14:00:00Z')
  await installPausedClock(page, new Date(initial.getTime() + 1000))
  await page.addInitScript(() => localStorage.clear())
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.evaluate(async (firstBoundary) => {
    const { DualFmsSystem } = await import('/src/fmsCdu/dualFms.ts')
    const probe = { attempts: [] as number[], failed: false }
    ;(window as unknown as { faultProbe: typeof probe }).faultProbe = probe
    for (const method of ['step', 'tick'] as const) {
      const original = DualFmsSystem.prototype[method]
      DualFmsSystem.prototype[method] = function (...args: unknown[]) {
        if (method === firstBoundary || (probe.failed && method === 'step')) {
          probe.attempts.push(this.computers[0].now.getTime())
          if (!probe.failed) { probe.failed = true; throw new Error('injected INTEGRATE boundary') }
        }
        return original.apply(this, args)
      }
    }
  }, firstBoundary)
  await page.getByLabel('Simulation rate').selectOption('64')
  if (firstBoundary === 'step') await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.clock.runFor(250)
  await expect(page.getByRole('alert')).toContainText('Simulation stopped: injected INTEGRATE boundary')
  await expect(page.getByRole('button', { name: 'Fly', exact: true })).toBeVisible()
  const read = () => page.evaluate(() => structuredClone((window as unknown as {
    faultProbe: { attempts: number[], failed: boolean }
  }).faultProbe))
  const stopped = await read()
  expect(stopped.attempts).toHaveLength(1)
  await page.clock.runFor(1000)
  expect(await read(), 'faulted clock/plant must not advance until explicit recovery').toEqual(stopped)
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.clock.runFor(250)
  await expect(page.getByRole('button', { name: 'Pause', exact: true })).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
  const resumed = await read()
  expect(resumed.attempts.length).toBeGreaterThan(1)
  expect(resumed.attempts[1] - resumed.attempts[0], 'resume advances to the next frame and never replays the fault instant').toBe(250)
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
})
// The model-frame owner cannot observe UI publication after replacing a high-rate presentation subscription.
test('rate replacement resets the current-speed measurement and retains the 4 Hz presentation target', async ({ page }) => {
  const initial = new Date('2026-09-27T14:00:00Z')
  await installPausedClock(page, new Date(initial.getTime() + 1000))
  await page.addInitScript(() => localStorage.clear())
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.clock.runFor(750)
  await page.getByLabel('Simulation rate').selectOption('4')
  await expect(page.getByLabel('Achieved speed', { exact: true })).toHaveText('Achieved — (measuring)')
  // The first admitted complete frame and normal presentation target are both 250 ms.
  await page.clock.runFor(251)
  await expect(page.getByLabel('Achieved speed', { exact: true })).toHaveText('Achieved ×4.0')
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await expect(page.getByLabel('Achieved speed', { exact: true })).toHaveText('Achieved — (paused)')
  await page.getByRole('button', { name: 'Restart the simulation', exact: true }).click()
  await expect(page.getByLabel('Achieved speed', { exact: true })).toHaveText('Achieved — (paused)')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(page.getByLabel('Achieved speed', { exact: true })).toHaveText('Achieved — (measuring)')
  await page.clock.runFor(251)
  await expect(page.getByLabel('Achieved speed', { exact: true })).toHaveText('Achieved ×4.0')
})

// The rate badge owner cannot detect a CDU memo that reuses another presentation store's numeric revision.
for (const action of ['Pause', 'rate replacement'] as const) test(`the CDU publishes current TIMER immediately after ${action} replaces a pending presentation`, async ({ page }) => {
  const initial = new Date('2026-09-27T14:00:00Z')
  await installPausedClock(page, initial)
  await page.addInitScript(() => localStorage.clear())
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.evaluate(async () => {
    const { FmsKernel } = await import('/src/fmsCdu/kernel/kernel.ts')
    const original = FmsKernel.prototype.advance
    FmsKernel.prototype.advance = function (...args: Parameters<typeof original>) {
      const done = original.apply(this, args)
      ;(window as unknown as { observedUnitClock: number }).observedUnitClock = this.unitClockMs
      return done
    }
  })
  await page.locator('.fmsCduKey[data-key="INIT_REF"]').click()
  await page.locator('.fmsCduKey[data-key="LSK5R"]').click()
  const screen = page.locator('.fmsCduScreen')
  await expect(screen).toHaveAttribute('aria-label', /TIMER/)
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.clock.runFor(750)
  const clock = () => page.evaluate(() => (window as unknown as { observedUnitClock: number }).observedUnitClock)
  expect(await clock(), 'three 64-frame batches complete within the explicit 750 ms clock advance').toBe(initial.getTime() + 48000)
  if (action === 'Pause') await page.getByRole('button', { name: 'Pause', exact: true }).click()
  else await page.getByLabel('Simulation rate').selectOption('4')
  expect(await clock(), 'the control publishes without an additional simulation frame').toBe(initial.getTime() + 48000)
  await expect(screen).toHaveAttribute('aria-label', /1400:48Z/)
})
