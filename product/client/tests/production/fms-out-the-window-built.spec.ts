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

  const violations = await page.evaluate(() => (window as unknown as { violations?: string[] }).violations ?? [])
  expect(violations, 'content security policy violations').toEqual([])
  expect(offOrigin, 'requests to another origin').toEqual([])
  expect(missing, 'files the build should have served').toEqual([])
  expect(problems, 'errors in the page').toEqual([])
})
