import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from '../auth'

/**
 * The FMS Test Bench's out-the-window view, against the build and under the document's real Content Security Policy.
 *
 * CesiumJS loads web workers and data files at run time from beside the bundle (vite.config.ts copies them to
 * dist/cesium), so none of that is exercised by `vite dev`: a copy step that missed a folder, a worker the policy
 * refuses, or a file requested from a CDN would all show only here. This installation runs as Production, so the
 * terrain relay is off (FmsBenchTerrainEndpoints.cs) and the view must still start and say why the ground is flat.
 */

// The view draws with WebGL; headless Chromium has no GPU here and only uses its software renderer when told to.
test.use({ launchOptions: { args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] } })

test('the out-the-window view and synthetic vision start under the production policy, from this server only, with terrain off', async ({ page, request, baseURL }) => {
  test.setTimeout(180_000)
  const origin = new URL(baseURL!).origin
  const offOrigin: string[] = [], missing: string[] = [], problems: string[] = []
  page.on('request', entry => {
    const url = new URL(entry.url())
    if (!['data:', 'blob:'].includes(url.protocol) && url.origin !== origin) offOrigin.push(entry.url())
  })
  page.on('response', response => {
    const path = new URL(response.url()).pathname
    // The relay answering "off" is the expected 404; a Cesium file that is not there is the failure this looks for.
    if (response.status() >= 400 && !path.startsWith('/api/')) missing.push(`${response.status()} ${path}`)
  })
  page.on('console', message => {
    if (message.type() === 'error' && !/status of 40[134]/.test(message.text())) problems.push(message.text())
  })
  page.on('pageerror', error => problems.push(error.message))

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

  await page.getByRole('button', { name: 'Show the view' }).click()
  const view = page.locator('.fmsOtw')
  await expect(view, 'the 3D view starts').toHaveAttribute('data-status', 'ready', { timeout: 90_000 })
  await expect(view).toHaveAttribute('data-terrain', 'off', { timeout: 30_000 })
  await expect(view.locator('.fmsOtwNote')).toContainText('FmsBench:TerrainRelay')
  // Workers build the terrain meshes, so a mesh on screen means the workers ran.
  await expect.poll(() => page.evaluate(() => performance.getEntriesByType('resource')
    .some(entry => /\/cesium\/Workers\/.+\.js$/.test(new URL(entry.name).pathname))), { timeout: 30_000 }).toBe(true)

  // Synthetic vision shares the terrain: chosen with the relay off, the PFD keeps its attitude and flags SVS.
  await page.getByRole('checkbox', { name: 'Synthetic vision' }).check()
  await expect(page.locator('svg.efisPfd').getByTestId('pfd-svs-flag')).toHaveText('SVS')
  await expect(page.locator('svg.efisPfd').getByTestId('pfd-svs')).toHaveCount(0)

  // #1382: only the built application can prove the focus mode's shell integration and this CSS cascade under CSP.
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await expect(page.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  await page.getByRole('button', { name: 'Focus bench', exact: true }).click()
  await expect(page.locator('.appNavigation')).toBeHidden()
  await expect(view).toHaveAttribute('data-status', 'ready')
  // The real shell adds its own header rhythm and control-height rules; a fixture cannot prove this fit.
  const selections = await page.getByRole('form', { name: 'Vertical and speed selections' }).boundingBox()
  expect(selections!.y + selections!.height, 'pilot controls fit the focused 1440 × 900 cockpit').toBeLessThanOrEqual(900)
  await page.screenshot({ path: test.info().outputPath('cockpit-built-focused.png'), fullPage: true })
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await page.getByRole('button', { name: 'More instructor room', exact: true }).click()
  await page.getByRole('tab', { name: 'GPS sensors', exact: true }).click()
  await expect(page.getByRole('region', { name: 'GPS 1', exact: true })).toBeVisible()
  await page.screenshot({ path: test.info().outputPath('cockpit-built-instructor.png'), fullPage: true })
  await page.keyboard.press('Escape')
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
})
