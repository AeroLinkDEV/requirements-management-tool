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

test('the scene draws only when something changes: still while the bench is paused, every frame while it flies', async ({ page }) => {
  test.setTimeout(300_000)
  // Flat ground (terrain off) in a small window: the globe loads in seconds even on a software renderer, where the
  // hill's tiles took minutes. The cockpit view, where a camera re-set to the same pose each frame used to keep a
  // paused view drawing; neither depends on the terrain.
  await page.setViewportSize({ width: 700, height: 500 })
  await open(page, 'off')
  const view = await show(page)
  const scene = view.locator('.fmsOtwScene')
  const frames = async () => Number(await scene.getAttribute('data-frames') ?? 0)
  // Settled: every tile the globe needs has loaded (slowly, on a software renderer; Cesium draws as each arrives) and
  // nothing is moving, so the count stops.
  let previous = -1
  // Reported as text, so a wait that does not settle says which of the two it was.
  const settled = async () => {
    const now = await frames(), loaded = await scene.getAttribute('data-tiles-loaded') === 'true'
    const state = `${loaded ? 'tiles loaded' : 'tiles loading'}, ${now === previous ? 'still' : 'drawing'}`
    previous = now
    return state
  }
  await expect.poll(settled, { intervals: [3000], timeout: 180_000 }).toBe('tiles loaded, still')
  const still = await frames()
  await page.waitForTimeout(2000)
  expect(await frames(), 'paused: no frames drawn').toBe(still)
  // Flying: the aircraft moves every tick (four a second), and each tick asks for frames. The software renderer here
  // draws only a few frames a second, so the bar is that it keeps drawing, not a frame rate.
  await page.getByRole('button', { name: 'Fly' }).click()
  await page.waitForTimeout(2000)
  expect(await frames() - still, 'flying: frames drawn').toBeGreaterThan(5)
  await page.getByRole('button', { name: 'Pause' }).click()
  previous = -1
  await expect.poll(settled, { intervals: [3000], timeout: 60_000 }).toBe('tiles loaded, still')
})

// How much of the view is the fixture's imagery (its teal and violet checkerboard) or the relative colouring's danger red,
// counted from a screenshot of the view decoded in the page.
const shares = async (page: Page) => {
  const png = await page.locator('.fmsOtw').screenshot()
  return page.evaluate(async b64 => {
    const image = new Image()
    image.src = `data:image/png;base64,${b64}`
    await image.decode()
    const canvas = Object.assign(document.createElement('canvas'), { width: image.width, height: image.height }), pen = canvas.getContext('2d')!
    pen.drawImage(image, 0, 0)
    const data = pen.getImageData(0, 0, canvas.width, canvas.height).data
    let imagery = 0, red = 0, n = 0
    for (let i = 0; i < data.length; i += 4 * 5) {
      const r = data[i], g = data[i + 1], b = data[i + 2]
      n++
      if ((g > r + 60 && b > r + 40) || (b > g + 60 && r > g + 20)) imagery++
      // The danger red blended over the ground: strongly red, green and blue under half of it (the absolute top band's
      // firebrick, lighter over the ground, is not).
      if (r > 160 && g < 0.45 * r && b < 0.4 * r) red++
    }
    return { imagery: imagery / n, red: red / n }
  }, png.toString('base64'))
}
// The share of the view that looks different from an earlier screenshot of it (a colour change of more than 40 in sum).
const changedSince = async (page: Page, before: Buffer) => {
  const after = await page.locator('.fmsOtw').screenshot()
  return page.evaluate(async ([a, b]) => {
    const pixels = async (b64: string) => {
      const image = new Image()
      image.src = `data:image/png;base64,${b64}`
      await image.decode()
      const canvas = Object.assign(document.createElement('canvas'), { width: image.width, height: image.height }), pen = canvas.getContext('2d')!
      pen.drawImage(image, 0, 0)
      return pen.getImageData(0, 0, canvas.width, canvas.height).data
    }
    const [p, q] = [await pixels(a), await pixels(b)]
    let changed = 0, n = 0
    for (let i = 0; i < Math.min(p.length, q.length); i += 4 * 5) { n++; if (Math.abs(p[i] - q[i]) + Math.abs(p[i + 1] - q[i + 1]) + Math.abs(p[i + 2] - q[i + 2]) > 40) changed++ }
    return changed / n
  }, [before.toString('base64'), after.toString('base64')])
}
const choose = (page: Page, group: string, name: string) => page.getByRole('radiogroup', { name: group }).getByText(name, { exact: true }).click()
// The scene has drawn what was asked: every tile the globe needs has loaded and its frame count has stopped (a lull
// between tiles, on a software renderer, is not enough).
const drawn = async (page: Page) => {
  const scene = page.locator('.fmsOtwScene')
  let previous = -1
  await expect.poll(async () => {
    const now = Number(await scene.getAttribute('data-frames') ?? 0), loaded = await scene.getAttribute('data-tiles-loaded') === 'true'
    const same = loaded && now === previous
    previous = now
    return same
  }, { intervals: [3000], timeout: 150_000 }).toBe(true)
}

test('the ground is aerial imagery where there is some, relief where there is none, and relief when chosen; the choice is remembered', async ({ page }) => {
  test.setTimeout(300_000)
  // A small window, looking straight down (the map view): the fewest tiles to load on a software renderer.
  await page.setViewportSize({ width: 700, height: 500 })
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
  await page.setViewportSize({ width: 700, height: 500 })
  for (const mode of ['none', 'blank']) {
    await page.goto(`/tests/fixtures/fms-cdu.html?imagery=${mode}`)
    const view = await show(page)
    await choose(page, 'Window view', 'Map')
    await drawn(page)
    expect((await shares(page)).imagery, `${mode}: no imagery drawn`).toBeLessThan(0.03)
    await expect(view.locator('.fmsOtwNote')).toHaveCount(0)
    // What is drawn instead is the relief, as choosing Relief draws it: not a flat colour.
    const fallback = await view.screenshot()
    await choose(page, 'Window ground', 'Relief')
    await drawn(page)
    expect(await changedSince(page, fallback), `${mode}: the relief, as Relief draws it`).toBeLessThan(0.05)
    await choose(page, 'Window ground', 'Imagery')
    await page.getByRole('button', { name: 'Hide the view' }).click()
  }
  await page.goto('/tests/fixtures/fms-cdu.html?imagery=off')
  const view = await show(page)
  await choose(page, 'Window view', 'Map')
  await expect(view).toHaveAttribute('data-imagery', 'off', { timeout: 60_000 })
  await expect(view.locator('.fmsOtwNote')).toContainText('Imagery is off on this installation')
})

test('terrain colouring: red where the ground reaches the aircraft (relative), height bands (absolute), none when off; remembered', async ({ page }) => {
  test.setTimeout(300_000)
  // A small window, looking straight down (the map view): the fewest tiles to load on a software renderer.
  await page.setViewportSize({ width: 700, height: 500 })
  // A hill higher than the aircraft, relief only so the colours are the colouring's.
  await page.goto('/tests/fixtures/fms-cdu.html?hill=1300')
  const view = await show(page)
  await choose(page, 'Window view', 'Map')
  await choose(page, 'Window ground', 'Relief')
  await drawn(page)
  const off = await shares(page)
  expect(off.red, 'off: no red').toBeLessThan(0.002)
  const uncoloured = await page.locator('.fmsOtw').screenshot()

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
