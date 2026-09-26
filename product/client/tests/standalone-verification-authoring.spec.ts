import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

/**
 * #1188 / DEC-144: a project that uses Verification without Requirements. Its System test change request is
 * raised on its own case, and the procedures it proposes are Standalone. Drives the real editor against the
 * real API and the run's disposable database.
 */
test('a Verification-only project raises test work on its own case with Standalone procedures', async ({ page, request }, testInfo) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const suffix = `${Date.now()}`.slice(-7)
  const created = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Standalone ${suffix}`,
    programCode: `SV${suffix}`,
    projectName: 'Standalone Verification Project',
    softwareProduct: 'Standalone Verification Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json() as { program: { id: string }; project: { id: string }; release: { id: string } }
  const features = await request.put(`${apiBase}/api/projects/${workspace.project.id}/features`, { data: {
    expectedVersion: 0, reason: 'Verification-only bench project', enabled: ['TeamWork', 'Verification', 'Release'],
  } })
  expect(features.ok(), await features.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  await page.goto(`${root}/system-verification/change-requests/new`)
  const editor = page.locator('[data-tcr-editor]')
  // No change request can exist, so none is asked for.
  await expect(editor.getByRole('note')).toContainText('This project does not use Requirements')
  await expect(editor.locator('.tcrSourceChoices')).toHaveCount(0)
  await expect(editor.locator('.tcrDriverHint')).toHaveCount(0)
  await editor.getByRole('note').scrollIntoViewIfNeeded()
  await page.screenshot({ path: testInfo.outputPath('standalone-tcr-note.png') })

  await editor.getByLabel('Title', { exact: true }).fill('Bench frame capture')
  await editor.getByLabel('Problem', { exact: true }).fill('The bench rig loses frames under load.')
  await editor.getByLabel('Analysis', { exact: true }).fill('Nothing exercises frame capture at load today.')
  await editor.getByLabel('Solution', { exact: true }).fill('Introduce a procedure that counts frames at load.')
  await editor.getByRole('button', { name: /^\+ Introduce System/ }).click()
  const number = `SYSTP-${suffix.padStart(6, '0').slice(-6)}`
  await editor.getByLabel(/ number 1$/).fill(number)
  await editor.getByLabel('Title 1', { exact: true }).fill('Frame capture under load')
  await editor.getByLabel('Objective 1', { exact: true }).fill('Show the rig keeps every frame at load.')
  await editor.getByLabel('Steps 1', { exact: true }).fill('1. Apply load. 2. Count frames.')
  await editor.getByLabel('Expected result 1', { exact: true }).fill('No frame is lost.')
  await editor.getByLabel('Rationale 1', { exact: true }).fill('The rig loses frames.')
  await page.screenshot({ path: testInfo.outputPath('standalone-tcr-editor.png'), fullPage: true })

  const savedPromise = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/test-change-requests'))
  await editor.getByRole('button', { name: 'Raise SYSTPCR', exact: true }).click()
  const saved = await savedPromise
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const raised = await saved.json() as { id: string }

  const detail = await request.get(`${apiBase}/api/test-change-reviews/${raised.id}/procedure-changes`)
  expect(detail.ok(), await detail.text()).toBeTruthy()
  const body = await detail.json() as { originDisplayLabel: string; changes?: { parentKind: string }[]; artifactChanges?: { parentKind: string }[]; procedureChanges?: { parentKind: string }[] }
  expect(body.originDisplayLabel).toBe('Own case')
  const changes = body.artifactChanges ?? body.procedureChanges ?? body.changes ?? []
  expect(changes.map(change => change.parentKind)).toEqual(['Standalone'])
})
