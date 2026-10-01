import { expect, renderedTest as test } from './isolated-client-test'
import { SOFTWARE_WEBGL, choose, open, show } from './fixtures/fms-otw-rendered'

// The out-the-window view's core: terrain, terrain off, the chase view, synthetic vision, and drawing only on change.
// Its ground (imagery, relief, colouring) is in fms-out-the-window-imagery-rendered.spec.ts, and its models (the glTF
// helicopter, the FAA obstacles) in fms-out-the-window-models-rendered.spec.ts (#1298, #1232: one file was the floor of
// the heaviest browser shard). The shared helpers are in fixtures/fms-otw-rendered.ts.
test.use(SOFTWARE_WEBGL)

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
  // Observe the real GPU boundary: refining flat meshes must not upload that same uniform image per tile.
  // This is test-side observation only; model, label and photographic textures remain outside the flat count.
  await page.addInitScript(() => {
    const uploads = { all: 0, flat: 0, armed: false }
    ;(window as unknown as { flatReliefUploads: typeof uploads }).flatReliefUploads = uploads
    const classified = new WeakMap<HTMLCanvasElement, boolean>()
    for (const type of [WebGLRenderingContext, WebGL2RenderingContext]) {
      const prototype = type.prototype as unknown as { texImage2D: (...args: unknown[]) => void }
      const upload = prototype.texImage2D
      prototype.texImage2D = function (...args: unknown[]) {
        if (!uploads.armed) return upload.apply(this, args)
        uploads.all++
        const image = args.find(arg => arg instanceof HTMLCanvasElement) as HTMLCanvasElement | undefined
        if (image?.width === 256 && image.height === 256) {
          let flat = classified.get(image)
          if (flat === undefined) {
            const pixels = image.getContext('2d')?.getImageData(0, 0, 256, 256).data
            flat = !!pixels && pixels[3] === 255
            if (pixels) for (let i = 0; flat && i < pixels.length; i += 4) {
              flat = pixels[i] === pixels[0] && pixels[i + 1] === pixels[1]
                && pixels[i + 2] === pixels[2] && pixels[i + 3] === 255
            }
            classified.set(image, flat)
          }
          if (flat) uploads.flat++
        }
        return upload.apply(this, args)
      }
    }
  })
  // Flat ground (terrain off) in a small window: the globe loads in seconds even on a software renderer, where the
  // hill's tiles took minutes. The cockpit view, where a camera re-set to the same pose each frame used to keep a
  // paused view drawing; neither depends on the terrain.
  await page.setViewportSize({ width: 700, height: 500 })
  await open(page, 'off')
  const view = await show(page)
  // Relief ground: with imagery, tiles go on arriving down to zoom 16 long after the globe reports itself loaded (each
  // drawn as it comes, as it should be), which on a software renderer outlasts the waits here.
  await page.getByRole('radiogroup', { name: 'Window ground' }).getByText('Relief', { exact: true }).evaluate(control => {
    // Arm at the user's selection, before React changes the layer, so startup's default Imagery is excluded.
    control.addEventListener('click', () => {
      (window as unknown as { flatReliefUploads: { armed: boolean } }).flatReliefUploads.armed = true
    }, { capture: true, once: true })
  })
  await choose(page, 'Window ground', 'Relief')
  const scene = view.locator('.fmsOtwScene')
  const frames = async () => Number(await scene.getAttribute('data-frames') ?? 0)
  // Settled: every tile the globe needs has loaded (slowly, on a software renderer; Cesium draws as each arrives) and
  // nothing is moving, so the count stops.
  let previous = -1, previousRequests = '', previousCauses = ''
  // Reported as text, so a wait that does not settle says which of the two it was, and, while drawing, who asked for the
  // frames: the view's own requests by source, now and at the previous poll (unchanged means Cesium itself drew), and
  // Cesium's tile-load queue; and why each frame was drawn (asked, camera, tiles, or Cesium's worker, request, atlas or
  // event work), now and at the previous poll, so the cause that went on counting is named (#1298).
  const settled = async () => {
    const now = await frames(), loaded = await scene.getAttribute('data-tiles-loaded') === 'true'
    const requests = await scene.getAttribute('data-requests') ?? 'none'
    const causes = await scene.getAttribute('data-frame-causes') ?? 'none'
    const asked = `frames ${now}; requests ${requests}, at the previous poll ${previousRequests || 'none'}; tile queue ${await scene.getAttribute('data-tile-queue') ?? 'unreported'}; frames drawn for ${causes}, at the previous poll ${previousCauses || 'none'}`
    const state = `${loaded ? 'tiles loaded' : 'tiles loading'}, ${now === previous ? 'still' : `drawing (${asked})`}`
    previous = now
    previousRequests = requests
    previousCauses = causes
    return state
  }
  await expect.poll(settled, { intervals: [3000], timeout: 180_000 }).toBe('tiles loaded, still')
  const uploads = await page.evaluate(() => (window as unknown as { flatReliefUploads: { all: number; flat: number } }).flatReliefUploads)
  expect(uploads.all, 'the real renderer exercised the GPU upload observer').toBeGreaterThan(0)
  expect(uploads.flat, 'uniform off-state relief is shared instead of uploaded for each refined mesh').toBeLessThanOrEqual(1)
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

