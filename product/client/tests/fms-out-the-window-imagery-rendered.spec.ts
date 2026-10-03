import { expect, renderedTest as test } from './isolated-client-test'
import { SOFTWARE_WEBGL, changedSince, choose, drawn, open, sceneShot, shares, show } from './fixtures/fms-otw-rendered'

// The out-the-window view's ground (split from fms-out-the-window-rendered.spec.ts, #1298/#1232): aerial imagery where
// there is some, relief where there is none or when chosen, the coverage edge, and the terrain colouring.
test.use(SOFTWARE_WEBGL)

test('the ground is aerial imagery where there is some, relief where there is none, and relief when chosen; the choice is remembered', async ({ page }) => {
  test.setTimeout(300_000)
  // A small window, looking straight down (the map view): the fewest tiles to load on a software renderer.
  await page.setViewportSize({ width: 480, height: 360 })
  await open(page, 'hill')
  const view = await show(page)
  await choose(page, 'Window view', 'Map')
  await expect(view).toHaveAttribute('data-ground', 'imagery')
  await expect(view).toHaveAttribute('data-imagery', 'live', { timeout: 60_000 })
  await drawn(page)
  expect((await shares(page)).imagery, 'imagery on the ground').toBeGreaterThan(0.25)
  await expect(view.locator('.fmsOtwCredits')).toContainText('Imagery: USGS The National Map, USDA NAIP (public domain)')

  await choose(page, 'Window ground', 'Relief')
  await expect(view).toHaveAttribute('data-ground', 'relief')
  await drawn(page)
  expect((await shares(page)).imagery, 'relief only').toBeLessThan(0.03)
  await expect(view.locator('.fmsOtwCredits')).not.toContainText('Imagery:')
  await page.reload()
  await expect(page.locator('.fmsOtw')).toHaveAttribute('data-ground', 'relief')
})

test('with no imagery (outside the coverage, or the service\'s blank filler) the ground is relief; with imagery off, the view says so', async ({ page }) => {
  test.setTimeout(300_000)
  // A small window, looking straight down (the map view): the fewest tiles to load on a software renderer.
  await page.setViewportSize({ width: 480, height: 360 })
  for (const mode of ['none', 'blank']) {
    await page.goto(`/tests/fixtures/fms-cdu.html?imagery=${mode}`)
    const view = await show(page)
    await choose(page, 'Window view', 'Map')
    await drawn(page)
    expect((await shares(page)).imagery, `${mode}: no imagery drawn`).toBeLessThan(0.03)
    await expect(page.locator('.fmsOtwSourceWarning')).toHaveCount(0)
    // What is drawn instead is the relief, as choosing Relief draws it: not a flat colour. (Once: the fallback is the
    // same code whichever way the imagery was missing, and each draw is slow on a software renderer.)
    if (mode === 'none') {
      const fallback = await sceneShot(page)
      await choose(page, 'Window ground', 'Relief')
      await drawn(page)
      expect(await changedSince(page, fallback), `${mode}: the relief, as Relief draws it`).toBeLessThan(0.05)
      await choose(page, 'Window ground', 'Imagery')
    }
    await page.getByRole('button', { name: 'Hide the view' }).click()
  }
  await page.goto('/tests/fixtures/fms-cdu.html?imagery=off')
  const view = await show(page)
  await choose(page, 'Window view', 'Map')
  await expect(view).toHaveAttribute('data-imagery', 'off', { timeout: 60_000 })
  await expect(page.locator('.fmsOtwSourceWarning')).toHaveText('Imagery off')
  await page.getByRole('button', { name: 'View status and sources', exact: true }).click()
  const details = page.getByRole('dialog', { name: 'View status and sources', exact: true })
  await expect(details).toContainText('Imagery is off on this installation')
  await expect(details).toContainText('FmsBench:ImageryRelay')
  await details.getByRole('button', { name: 'Close', exact: true }).click()
})

test('along the edge of the coverage an imagery tile\'s transparent part shows the relief beneath it, not black', async ({ page }) => {
  // The service sends PNG along coastlines and the border, transparent beyond its coverage; the ground layer is drawn
  // opaque, so a tile passed through as it came drew the far side black.
  test.setTimeout(300_000)
  await page.setViewportSize({ width: 480, height: 360 })
  await page.goto('/tests/fixtures/fms-cdu.html?imagery=edge')
  const view = await show(page)
  await choose(page, 'Window view', 'Map')
  await expect(view).toHaveAttribute('data-imagery', 'live', { timeout: 60_000 })
  await drawn(page)
  const { imagery, dark } = await shares(page)
  expect(imagery, 'the imagery half of each tile').toBeGreaterThan(0.2)
  expect(imagery, 'and only that half').toBeLessThan(0.8)
  expect(dark, 'nothing drawn black where the tiles are transparent').toBeLessThan(0.03)
  await expect(page.locator('.fmsOtwSourceWarning')).toHaveCount(0)
})

test('terrain colouring: red where the ground reaches the aircraft (relative), height bands (absolute), none when off; remembered', async ({ page }) => {
  test.setTimeout(300_000)
  // A small window, looking straight down (the map view): the fewest tiles to load on a software renderer.
  await page.setViewportSize({ width: 480, height: 360 })
  // A hill higher than the aircraft, relief only so the colours are the colouring's.
  await page.goto('/tests/fixtures/fms-cdu.html?hill=1300')
  const view = await show(page)
  await choose(page, 'Window view', 'Map')
  await choose(page, 'Window ground', 'Relief')
  await drawn(page)
  const off = await shares(page)
  expect(off.red, 'off: no red').toBeLessThan(0.002)
  const uncoloured = await sceneShot(page)

  await choose(page, 'Terrain colouring', 'Relative')
  await expect(view).toHaveAttribute('data-colouring', 'relative')
  await drawn(page)
  const relative = await shares(page)
  expect(relative.red, 'relative: the hill above the aircraft is red').toBeGreaterThan(0.01)
  // Only the hill: the lowland far below the aircraft stays uncoloured (about a tenth of the view is red here).
  expect(relative.red, 'relative: the lowland is not red').toBeLessThan(0.3)

  await choose(page, 'Terrain colouring', 'Absolute')
  await expect(view).toHaveAttribute('data-colouring', 'absolute')
  await drawn(page)
  // Bands by height alone: the hill's slopes change colour band by band, and none of it is the danger red.
  const absolute = await shares(page)
  expect(absolute.red, 'absolute: no danger red').toBeLessThan(0.01)
  expect(await changedSince(page, uncoloured), 'absolute: the ground is tinted').toBeGreaterThan(0.2)
  await page.reload()
  await expect(page.locator('.fmsOtw')).toHaveAttribute('data-colouring', 'absolute')
})
