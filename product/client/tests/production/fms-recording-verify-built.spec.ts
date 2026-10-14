import { expect, test } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import { apiLogin, login, selectProgram } from '../auth'

// The rendered recording owner runs on the development server, where Verify recording must refuse. Only a built
// client has the emitted-asset manifest, the compiled verifier worker and the served bytes a qualified recording is
// bound to; a Vite or hosting change that drops the worker from the manifest, or stops serving the manifest, turns
// every real verification into a refusal. This journey owns that path on the artifact the API actually serves.
test('a recording exported from the built bench verifies in a fresh worker, and a corrupt build identity is refused', async ({ page, context, request }) => {
  test.setTimeout(90_000)
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const path = await page.locator('nav[aria-label="Primary navigation"] a[href$="/fms-test-bench"]').first().getAttribute('href')
  expect(path).toBeTruthy()
  await page.goto(path!)
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const card = page.getByRole('region', { name: 'Kernel journal', exact: true })

  // The bench starts paused and still passes a boundary every paced callback, so the export resolves at the next S_k.
  const downloading = page.waitForEvent('download')
  await card.getByRole('button', { name: 'Export recording', exact: true }).click()
  const file = test.info().outputPath('built-recording.json')
  await (await downloading).saveAs(file)
  // No "Unqualified identity" suffix: the served manifest, every emitted asset and the engine version all bound.
  await expect(card.getByRole('status')).toHaveText('Exported S_k; empty snapshot slot (I2 required for restore).')
  const recording = JSON.parse(await readFile(file, 'utf8')).recording
  expect(recording.identity).toMatchObject({ qualified: true, reason: null, build: { kind: 'built' } })
  expect(recording.identity.build.assets.some((asset: { path: string }) => /verifyRecording\.worker/.test(asset.path))).toBe(true)
  expect(recording.capture).toMatchObject({ kind: 'boundary', exact: true })

  const verify = async () => card.getByLabel('Verify recording', { exact: true }).setInputFiles(file)
  await verify()
  await expect(card.getByRole('status')).toHaveText(`Verified through S_${recording.lastBoundary}.`)

  // The fresh worker resolves the CURRENT served identity itself; a manifest whose hash no longer binds its contents
  // is refused rather than trusting the identity the recording asserts.
  const manifest = await (await page.request.get('/fms-recording-build.json')).json()
  const corrupt = async (route: import('@playwright/test').Route) => route.fulfill({ status: 200, contentType: 'application/json',
    body: JSON.stringify({ ...manifest, bundleHash: '0'.repeat(64) }) })
  await context.route('**/fms-recording-build.json', corrupt)
  try {
    await verify()
    await expect(card.getByRole('status')).toHaveText('Verification refused: unqualified environment: corrupt build identity')
  } finally { await context.unroute('**/fms-recording-build.json', corrupt) }
})
