import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from '../auth'
import { observeFmsNodeWorker } from '../../test-support/fms-node-worker-telemetry'
import { observeFmsPublicHeartbeat } from '../../test-support/fms-public-browser-heartbeat'

/**
 * The FMS Test Bench's out-the-window view, against the build and under the document's real Content Security Policy.
 *
 * CesiumJS loads web workers and data files at run time from beside the bundle (vite.config.ts copies them to
 * dist/cesium), so none of that is exercised by `vite dev`: a copy step that missed a folder, a worker the policy
 * refuses, or a file requested from a CDN would all show only here. This installation runs as Production, so the
 * terrain relay is off (FmsBenchTerrainEndpoints.cs) and the view must still start and say why the ground is flat.
 */

// The view draws with WebGL; headless Chromium has no GPU here and only uses its software renderer when told to.
// Omit automatic filmstrip and DOM snapshot barriers for this live WebGL journey. Playwright also couples
// its network archive to snapshots: DOM/network replay is lost. Action/source traces, API logs, telemetry,
// request-policy assertions and focused/manual/failure images remain, with the original guard and oracles.
test.use({
  launchOptions: { args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] },
  trace: { mode: 'retain-on-failure', screenshots: false, snapshots: false, sources: true, attachments: true },
})

test('the out-the-window view and synthetic vision start under the production policy, from this server only, with terrain off', async ({ page, request, baseURL, context }) => {
  test.setTimeout(180_000)
  const telemetry = observeFmsNodeWorker(test.info())
  let ownerBodyThrew = false
  let heartbeat: Awaited<ReturnType<typeof observeFmsPublicHeartbeat>> | undefined
  try {
  heartbeat = await observeFmsPublicHeartbeat(context, test.info())
  const origin = new URL(baseURL!).origin
  const offOrigin: string[] = [], missing: string[] = [], problems: string[] = []
  // Context listeners include the station child's first stylesheet/worker request, before its Page is delivered.
  context.on('request', entry => {
    const url = new URL(entry.url())
    if (!['data:', 'blob:'].includes(url.protocol) && url.origin !== origin) offOrigin.push(entry.url())
  })
  context.on('response', response => {
    const path = new URL(response.url()).pathname
    // The relay answering "off" is the expected 404; a Cesium file that is not there is the failure this looks for.
    if (response.status() >= 400 && !path.startsWith('/api/')) missing.push(`${response.status()} ${path}`)
  })
  context.on('console', message => {
    if (message.type() === 'error' && !/status of 40[134]/.test(message.text())) problems.push(message.text())
  })
  context.on('weberror', error => problems.push(error.error().message))

  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const bench = await page.locator('nav[aria-label="Primary navigation"] a[href$="/fms-test-bench"]').first().getAttribute('href')
  expect(bench, 'the navigation offers the FMS Test Bench').toBeTruthy()
  await page.goto(bench!)
  await page.evaluate(() => document.addEventListener('securitypolicyviolation', event => {
    (window as unknown as { violations?: string[] }).violations ??= []
    ;(window as unknown as { violations: string[] }).violations.push(`${event.violatedDirective} ${event.blockedURI}`)
  }))

  // #1382: establish the real shell and cockpit before software WebGL starts refining its scene.
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await expect(page.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  await page.getByRole('button', { name: 'Focus bench', exact: true }).click()
  await expect(page.locator('.appNavigation')).toBeHidden()
  await page.getByRole('button', { name: 'Show the view' }).click()
  // This policy journey needs real terrain meshes and workers, not imagery refinement. Use the pilot's ground choice.
  await page.getByRole('radiogroup', { name: 'Window ground' }).getByText('Relief', { exact: true }).click()
  const view = page.locator('.fmsOtw')
  await expect(view).toHaveAttribute('data-ground', 'relief')
  await expect(view, 'the 3D view starts').toHaveAttribute('data-status', 'ready', { timeout: 90_000 })
  await expect(view).toHaveAttribute('data-terrain', 'off', { timeout: 30_000 })
  await expect(page.locator('.fmsOtwSourceWarning')).toHaveText('Terrain off')
  await page.getByRole('button', { name: 'View status and sources', exact: true }).click()
  const sourceDetails = page.getByRole('dialog', { name: 'View status and sources', exact: true })
  await expect(sourceDetails).toContainText('FmsBench:TerrainRelay')
  await sourceDetails.getByRole('button', { name: 'Close', exact: true }).click()
  // Workers build the terrain meshes, so a mesh on screen means the workers ran.
  await expect.poll(() => page.evaluate(() => performance.getEntriesByType('resource')
    .some(entry => /\/cesium\/Workers\/.+\.js$/.test(new URL(entry.name).pathname))), { timeout: 30_000 }).toBe(true)

  // Synthetic vision shares the terrain: chosen with the relay off, the PFD keeps its attitude and flags SVS.
  await page.getByRole('checkbox', { name: 'Synthetic vision' }).check()
  await expect(page.locator('svg.efisPfd').getByTestId('pfd-svs-flag')).toHaveText('SVS')
  await expect(page.locator('svg.efisPfd').getByTestId('pfd-svs')).toHaveCount(0)

  // Only the built application can prove this CSS cascade under CSP while the real scene remains active.
  await expect(view).toHaveAttribute('data-status', 'ready')
  // The real shell adds its own header rhythm and control-height rules; a fixture cannot prove this fit.
  const selections = await page.getByRole('form', { name: 'Vertical and speed selections' }).boundingBox()
  expect(selections!.y + selections!.height, 'pilot controls fit the focused 1440 × 900 cockpit').toBeLessThanOrEqual(900)
  await page.screenshot({ path: test.info().outputPath('cockpit-built-focused.png'), fullPage: false })
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await page.getByRole('button', { name: 'More instructor room', exact: true }).click()
  await page.getByRole('tab', { name: 'GPS sensors', exact: true }).click()
  await expect(page.getByRole('region', { name: 'GPS 1', exact: true })).toBeVisible()
  await page.screenshot({ path: test.info().outputPath('cockpit-built-instructor.png'), fullPage: false })
  await page.keyboard.press('Escape')

  // D: the shipped CSS, inherited policy and real WebGL must also work in the destination document.
  await context.addInitScript(() => document.addEventListener('securitypolicyviolation', event => {
    (window as unknown as { violations?: string[] }).violations ??= []
    ;(window as unknown as { violations: string[] }).violations.push(`${event.violatedDirective} ${event.blockedURI}`)
  }))
  await page.getByText('Station windows', { exact: true }).click()
  await page.getByRole('combobox', { name: 'Fixed station arrangement' }).selectOption('three')
  const outsideOpened = context.waitForEvent('page')
  await page.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
  const outside = await outsideOpened
  await outside.setViewportSize({ width: 1000, height: 700 })
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toBeVisible()
  const childView = outside.locator('.fmsOtw'), childScene = childView.locator('.fmsOtwScene')
  await expect(childView).toHaveAttribute('data-status', 'ready', { timeout: 90_000 })
  await expect(childView).toHaveAttribute('data-terrain', 'off')
  const childCanvas = childScene.locator('canvas').first()
  const canvasOnly = { style: '.fmsOtw > :not(.fmsOtwScene) { visibility: hidden !important; }' }
  await expect(childCanvas).toBeVisible()
  const canvasBox = await childCanvas.boundingBox()
  expect(canvasBox!.width, 'the detached scene uses the station width').toBeGreaterThan(600)
  expect(canvasBox!.height, 'the detached scene uses the station height').toBeGreaterThan(300)
  await expect.poll(async () => Number(await childScene.getAttribute('data-frames') ?? 0)).toBeGreaterThan(0)

  // Synthetic scheduling proof: stop only the owner's RAF, drain its queued frame, then demand a child repaint.
  // This discriminates renderer realms; it does not qualify hidden-owner simulation timing.
  try {
    await page.evaluate(() => new Promise<void>(resolve => {
      const owner = window as unknown as { stationSavedRaf?: typeof requestAnimationFrame }
      owner.stationSavedRaf = window.requestAnimationFrame
      window.requestAnimationFrame = () => 0
      owner.stationSavedRaf.call(window, () => resolve())
    }))
    const before = await childCanvas.screenshot({ ...canvasOnly, path: test.info().outputPath('station-child-canvas-before.png') })
    const firstFrames = Number(await childScene.getAttribute('data-frames') ?? 0)
    await outside.getByRole('radiogroup', { name: 'Window view' }).getByText('Map', { exact: true }).click()
    await expect.poll(async () => Number(await childScene.getAttribute('data-frames') ?? 0)).toBeGreaterThan(firstFrames)
    // Compare the canvas rectangle with sibling overlays hidden: React text cannot satisfy the repaint check.
    await expect.poll(async () => {
      const after = await childCanvas.screenshot(canvasOnly)
      return outside.evaluate(async ([a, b]) => {
        const pixels = async (b64: string) => {
          const image = new Image()
          image.src = `data:image/png;base64,${b64}`
          await image.decode()
          const canvas = Object.assign(document.createElement('canvas'), { width: image.width, height: image.height })
          const pen = canvas.getContext('2d')!
          pen.drawImage(image, 0, 0)
          return pen.getImageData(0, 0, canvas.width, canvas.height).data
        }
        const [p, q] = [await pixels(a), await pixels(b)]
        let changed = 0, total = 0
        for (let i = 0; i < Math.min(p.length, q.length); i += 4 * 97) {
          total++
          if (Math.abs(p[i] - q[i]) + Math.abs(p[i + 1] - q[i + 1]) + Math.abs(p[i + 2] - q[i + 2]) > 40) changed++
        }
        return changed / total
      }, [before.toString('base64'), after.toString('base64')])
    }, { timeout: 30_000 }).toBeGreaterThan(0.05)
    await outside.screenshot({ path: test.info().outputPath('station-built-outside.png'), fullPage: false })
    const firstBuffer = await childCanvas.evaluate(node => ({ width: (node as HTMLCanvasElement).width, height: (node as HTMLCanvasElement).height }))
    await outside.setViewportSize({ width: 800, height: 600 })
    await expect.poll(async () => {
      const canvas = await childCanvas.boundingBox()
      return canvas!.width <= 800 && canvas!.height <= 600 && canvas!.width > 600 && canvas!.height > 300
    }).toBe(true)
    await expect.poll(() => childCanvas.evaluate((node, previous) => {
      const canvas = node as HTMLCanvasElement
      return canvas.width < previous.width && canvas.height < previous.height
        && Math.abs(canvas.width - canvas.clientWidth) <= 1 && Math.abs(canvas.height - canvas.clientHeight) <= 1
    }, firstBuffer), { message: 'the destination WebGL drawing buffer follows the resized viewport' }).toBe(true)
  } finally {
    await page.evaluate(() => {
      const owner = window as unknown as { stationSavedRaf?: typeof requestAnimationFrame }
      if (owner.stationSavedRaf) window.requestAnimationFrame = owner.stationSavedRaf
      delete owner.stationSavedRaf
    })
  }
  expect(await outside.evaluate(() => (window as unknown as { violations?: string[] }).violations ?? []), 'child content security policy violations').toEqual([])

  // Complete the fixed three-screen arrangement and instructor-apart option with the built shell's CSS/assets.
  const cockpitOpened = context.waitForEvent('page')
  await page.getByRole('button', { name: 'Open cockpit in a window', exact: true }).click()
  const cockpit = await cockpitOpened
  await cockpit.setViewportSize({ width: 1440, height: 900 })
  await expect(cockpit.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  for (const image of await cockpit.locator('.fmsCduImage').all()) {
    await expect.poll(() => image.evaluate(node => (node as HTMLImageElement).naturalWidth),
      { message: 'the built child loads the real CDU faceplate' }).toBeGreaterThan(0)
  }
  const childRight = cockpit.locator('.fmsBenchCduStation[data-side="2"] .fmsCdu')
  await childRight.focus()
  await cockpit.keyboard.type('stays')
  await expect(childRight.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYS\s*$/)
  await expect(cockpit.getByLabel('FMS guidance source')).toHaveValue('1')
  const childSelections = await cockpit.getByRole('form', { name: 'Vertical and speed selections' }).boundingBox()
  expect(childSelections!.y + childSelections!.height, 'pilot controls fit the built cockpit window').toBeLessThanOrEqual(900)
  await cockpit.bringToFront()
  await cockpit.screenshot({ path: test.info().outputPath('station-built-cockpit.png'), fullPage: false })
  expect(await cockpit.evaluate(() => (window as unknown as { violations?: string[] }).violations ?? []), 'cockpit child policy violations').toEqual([])

  await page.getByRole('checkbox', { name: 'Instructor apart' }).check()
  const instructorOpened = context.waitForEvent('page')
  await page.getByRole('button', { name: 'Open instructor station in a window', exact: true }).click()
  const instructor = await instructorOpened
  const childInstructor = instructor.getByRole('region', { name: 'Instructor station', exact: true })
  await childInstructor.getByRole('tab', { name: 'GPS sensors', exact: true }).click()
  await expect(childInstructor.getByRole('region', { name: 'GPS 1', exact: true })).toBeVisible()
  await instructor.bringToFront()
  await instructor.screenshot({ path: test.info().outputPath('station-built-instructor.png'), fullPage: false })
  expect(await instructor.evaluate(() => (window as unknown as { violations?: string[] }).violations ?? []), 'instructor child policy violations').toEqual([])
  // Dismiss the arrangement disclosure before using the bench dock beneath its overlay.
  await page.getByText(/^Station windows(?: · \d+ open)?$/).click()
  await expect(page.getByRole('region', { name: 'Station arrangement', exact: true })).toBeHidden()
  await instructor.getByRole('button', { name: 'Return to bench', exact: true }).click()
  await expect.poll(() => instructor.isClosed()).toBe(true)
  await cockpit.getByRole('button', { name: 'Return to bench', exact: true }).click()
  await expect.poll(() => cockpit.isClosed()).toBe(true)
  await page.getByRole('region', { name: 'Out the window dock' }).getByRole('button', { name: 'Return Out the window' }).click()
  await expect.poll(() => outside.isClosed()).toBe(true)
  await expect(view).toHaveAttribute('data-status', 'ready', { timeout: 90_000 })
  const returnedScene = view.locator('.fmsOtwScene')
  const returnedFrames = Number(await returnedScene.getAttribute('data-frames') ?? 0)
  await page.getByRole('radiogroup', { name: 'Window view' }).getByText('Cockpit', { exact: true }).click()
  await expect.poll(async () => Number(await returnedScene.getAttribute('data-frames') ?? 0)).toBeGreaterThan(returnedFrames)
  await page.getByRole('button', { name: 'Show navigation', exact: true }).click()
  await expect(page.locator('.appNavigation')).toBeVisible()
  await page.getByRole('button', { name: 'Engineering view', exact: true }).click()
  await expect(page.locator('.fmsCdu')).toHaveCount(1)
  await expect(view).toHaveAttribute('data-status', 'ready')

  const violations = await page.evaluate(() => (window as unknown as { violations?: string[] }).violations ?? [])
  expect(violations, 'content security policy violations').toEqual([])
  expect(offOrigin, 'requests to another origin').toEqual([])
  expect(missing, 'files the build should have served').toEqual([])
  expect(problems, 'errors in the page').toEqual([])
  } catch (error) { ownerBodyThrew = true; throw error }
  finally {
    let observationError: unknown
    try { heartbeat?.finish(ownerBodyThrew) } catch (error) { observationError = error }
    try { telemetry.finish(ownerBodyThrew) } catch (error) { observationError ??= error }
    if (!ownerBodyThrew && observationError) throw observationError
  }
})
