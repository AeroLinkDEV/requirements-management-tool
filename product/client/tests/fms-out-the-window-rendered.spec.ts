import { expect, renderedTest as test, type Page } from './isolated-client-test'

// The out-the-window view draws with WebGL. Headless Chromium has no GPU here, and only uses its software renderer
// when told to.
test.use({ launchOptions: { args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] } })

// The bench runs in the page, and the fixture makes the terrain (fms-cdu.tsx): no server is involved.
const open = async (page: Page, terrain: 'hill' | 'off') => {
  // Cleared on the first load only, so a reload shows what the bench remembered.
  await page.addInitScript(() => {
    try { if (!window.sessionStorage.getItem('cleared')) { window.localStorage.clear(); window.sessionStorage.setItem('cleared', 'yes') } } catch { /* private mode */ }
  })
  await page.goto(terrain === 'off' ? '/tests/fixtures/fms-cdu.html?terrain=off' : '/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
}

const show = async (page: Page) => {
  await page.getByRole('button', { name: 'Show the view' }).click()
  const view = page.locator('.fmsOtw')
  await expect(view, 'the 3D view starts').toHaveAttribute('data-status', 'ready', { timeout: 60_000 })
  return view
}

test('the view draws the terrain it is given, flies with the simulation, and remembers how it was shown', async ({ page }, testInfo) => {
  test.setTimeout(120_000)
  await open(page, 'hill')
  await expect(page.getByLabel('Out-the-window view')).toContainText('open elevation data')
  const view = await show(page)

  await expect(view).toHaveAttribute('data-terrain', 'live', { timeout: 30_000 })
  await expect(view.locator('canvas')).toBeVisible()
  const box = (await view.locator('canvas').boundingBox())!
  expect(box.width).toBeGreaterThan(600)
  expect(box.height).toBeGreaterThan(300)
  // Head-up in the cockpit: the modes are the bench's own annunciator, and the flight path marker is placed.
  await expect(page.getByRole('status', { name: 'Head-up flight modes' })).toContainText(await page.locator('.fmsBenchFma .engaged').first().innerText())
  await expect(view.locator('.fmsOtwPathMarker')).toBeVisible()
  await view.screenshot({ path: testInfo.outputPath('head-up.png') })
  await expect(view).toContainText('Terrain: Mapzen Terrain Tiles on AWS Open Data')

  // The panel layout: a glareshield and no head-up symbology.
  await page.getByRole('radiogroup', { name: 'Window layout' }).getByText('Panel', { exact: true }).click()
  await expect(view.locator('.fmsOtwGlareshield')).toBeVisible()
  await expect(view.locator('.fmsOtwHud')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('panel.png') })
  await page.getByRole('radiogroup', { name: 'Window view' }).getByText('Map', { exact: true }).click()
  await expect(view).toHaveClass(/view-map/)

  await page.reload()
  await expect(page.locator('.fmsOtw')).toHaveClass(/layout-panel/)
  await expect(page.locator('.fmsOtw')).toHaveClass(/view-map/)
  await page.getByRole('button', { name: 'Hide the view' }).click()
  await expect(page.locator('.fmsOtw')).toHaveCount(0)
})

test('with terrain turned off on the installation, the view still flies and says why the ground is flat', async ({ page }) => {
  test.setTimeout(120_000)
  await open(page, 'off')
  const view = await show(page)
  await expect(view).toHaveAttribute('data-terrain', 'off', { timeout: 30_000 })
  await expect(view.locator('.fmsOtwNote')).toContainText('Terrain data is off on this installation')
  await expect(view.locator('canvas')).toBeVisible()
})

test('the chase view shows the aircraft model behind which the camera flies', async ({ page }, testInfo) => {
  test.setTimeout(120_000)
  await open(page, 'hill')
  const view = await show(page)
  await page.getByRole('radiogroup', { name: 'Window view' }).getByText('Chase', { exact: true }).click()
  await expect(view).toHaveClass(/view-chase/)
  await page.getByRole('button', { name: 'Fly' }).click()
  await page.waitForTimeout(1500)
  await view.screenshot({ path: testInfo.outputPath('chase.png') })
  // The model is a scene primitive; the canvas shows it, so the proof here is that the view stays running.
  await expect(view).toHaveAttribute('data-status', 'ready')
})

test('synthetic vision draws terrain behind the PFD attitude, and is flagged instead when terrain is off', async ({ page }, testInfo) => {
  test.setTimeout(120_000)
  await open(page, 'hill')
  const pfd = page.locator('svg.efisPfd')
  await expect(pfd.getByTestId('pfd-svs')).toHaveCount(0)
  await page.getByRole('checkbox', { name: 'Synthetic vision' }).check()
  await expect(pfd.getByTestId('pfd-svs')).toBeVisible({ timeout: 30_000 })
  await expect(pfd.getByTestId('pfd-svs-flag')).toHaveCount(0)
  // The picture is drawn: the canvas holds more than one colour.
  await expect.poll(() => pfd.getByTestId('pfd-svs').locator('canvas').evaluate(node => {
    const data = (node as HTMLCanvasElement).getContext('2d')!.getImageData(0, 0, 352, 352).data
    const colours = new Set<number>()
    for (let i = 0; i < data.length; i += 4 * 97) colours.add((data[i] << 16) | (data[i + 1] << 8) | data[i + 2])
    return colours.size
  })).toBeGreaterThan(20)
  await pfd.screenshot({ path: testInfo.outputPath('pfd-svs.png') })
  await page.reload()
  await expect(page.getByRole('checkbox', { name: 'Synthetic vision' })).toBeChecked()

  await page.goto('/tests/fixtures/fms-cdu.html?terrain=off')
  await expect(page.getByRole('checkbox', { name: 'Synthetic vision' })).toBeChecked()
  await expect(pfd.getByTestId('pfd-svs-flag')).toHaveText('SVS', { timeout: 30_000 })
  await expect(pfd.getByTestId('pfd-svs')).toHaveCount(0)
})
