import { expect, renderedTest as test } from './isolated-client-test'
import { SOFTWARE_WEBGL, open, show } from './fixtures/fms-otw-rendered'

// The out-the-window view's models (split from fms-out-the-window-rendered.spec.ts, #1298/#1232): the glTF helicopter in
// the chase view, and the FAA obstacles.
test.use(SOFTWARE_WEBGL)

test('the chase view flies the glTF helicopter model, its rotors turning, in place of the fallback shapes', async ({ page }, testInfo) => {
  test.setTimeout(180_000)
  // Flat ground in a small window, so the globe loads quickly on the software renderer; the model does not need terrain.
  await page.setViewportSize({ width: 700, height: 500 })
  // Enter Chase while decoding is still pending, as on a fresh/reloaded demo. The old owner selected
  // Chase only after the model was ready and missed the decoded-file/GPU initialization race (#1443).
  let releaseModel!: () => void
  const modelGate = new Promise<void>(resolve => { releaseModel = resolve })
  await page.route('**/fms-cdu/models/helicopter-light-twin.glb', async route => {
    await modelGate
    await route.fallback()
  })
  const modelRequested = page.waitForRequest('**/fms-cdu/models/helicopter-light-twin.glb')
  try {
  await open(page, 'off')
  const view = await show(page)
  const component = page.getByLabel('Out-the-window view', { exact: true })
  const status = page.getByRole('status', { name: 'Out-the-window view status', exact: true })
  const scene = view.locator('.fmsOtwScene')
  // The model (public/fms-cdu/models/helicopter-light-twin.glb) loads from the bench's own origin.
  await modelRequested
  await page.getByRole('radiogroup', { name: 'Window view' }).getByText('Chase', { exact: true }).click()
  await expect(view).toHaveClass(/view-chase/)
  // Engine-ready is not aircraft-ready: explain the real pending GLB without blocking the view.
  // This assertion fails on the pre-fix renderer even though its data-status already says ready.
  await expect(view).toHaveAttribute('data-status', 'ready')
  await expect(status).toContainText('Loading the aircraft model')
  await page.getByRole('radiogroup', { name: 'Window view' }).getByText('Map', { exact: true }).click()
  await expect(component).not.toContainText('Loading the aircraft model')
  await page.getByRole('radiogroup', { name: 'Window view' }).getByText('Chase', { exact: true }).click()
  await expect(status).toContainText('Loading the aircraft model')
  releaseModel()
  await expect(scene, 'the helicopter model becomes render-ready in Chase').toHaveAttribute('data-model', 'glb', { timeout: 60_000 })
  await expect(component).not.toContainText('Loading the aircraft model')
  await page.getByRole('button', { name: 'Fly' }).click()
  await page.waitForTimeout(2000)
  const first = await view.screenshot({ path: testInfo.outputPath('chase-model-1.png') })
  await page.waitForTimeout(120)
  const second = await view.screenshot({ path: testInfo.outputPath('chase-model-2.png') })
  // Flying, the frames differ (the rotors turn and the aircraft moves); the view stays running.
  expect(Buffer.compare(first, second)).not.toBe(0)
  await expect(view).toHaveAttribute('data-status', 'ready')
  } finally {
    releaseModel()
  }
})

// Brief C: the FAA Digital Obstacle File extract near the bench areas (public/fms-cdu/obstacles, with its provenance)
// is loaded from the bench's own origin and drawn in the view; the scene element records how many, or why none.
test('the FAA obstacles near the bench areas are loaded and drawn in the view (Brief C)', async ({ page }) => {
  test.setTimeout(120_000)
  await open(page, 'off')
  const view = await show(page)
  const scene = view.locator('.fmsOtwScene')
  // The layer settles once, as the count or "failed"; a failure names its reason (#1492).
  await expect(scene).toHaveAttribute('data-obstacles', /^(\d+|failed)$/, { timeout: 60_000 })
  const reason = await scene.getAttribute('data-obstacles-reason')
  expect(await scene.getAttribute('data-obstacles'), `the obstacle layer's outcome (reason: ${reason})`).toBe('11973')
  expect(reason).toBeNull()
})
