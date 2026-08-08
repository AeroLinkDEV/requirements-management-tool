import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

/**
 * A disposable Program with one approved System change request and one Problem Report, so the authoring
 * journey owns its state instead of depending on the shared showcase dataset.
 */
async function seedWorkspace(request: import('@playwright/test').APIRequestContext, suffix: string, released = false) {
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `TCR Authoring ${suffix}`,
    programCode: `TCR${suffix}`,
    projectName: 'TCR Authoring Project',
    softwareProduct: 'TCR Authoring Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: released,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const impacts = JSON.stringify({
    trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
    baseline: 'Not Affected', collaboration: 'Not Affected',
  })
  if (released) return { workspace, sourceChangeId: undefined, sourceNumber: '', report: undefined }

  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `TCR authoring source ${suffix}`,
    problem: 'The new behavior has no test coverage.',
    analysis: 'No procedure exercises it today.',
    solution: 'Write one from this package.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: await firstSectionId(request, workspace.project.id),
      statement: `The ${suffix} product shall expose a TCR-authoring verification target.`,
      rationale: 'Capability qualification.',
      verificationMethod: 'Test',
      impactDispositionJson: impacts,
    }],
  } })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const draft = await draftResponse.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, {
    data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] },
  })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, {
    data: { password: 'AeroLink!2026', meaning: 'Approved for TCR authoring journey verification.' },
  })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const reportResponse = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      projectId: workspace.project.id,
      releaseId: workspace.release.id,
      title: `TCR driving report ${suffix}`,
      problem: 'The observed behavior disagrees with the approved plan.',
    },
  })
  expect(reportResponse.ok(), await reportResponse.text()).toBeTruthy()
  const report = await reportResponse.json()
  const changeRequest = await request.get(`${apiBase}/api/change-requests/${draft.id}`)
  const detail = await changeRequest.json()
  return { workspace, sourceChangeId: draft.id, sourceNumber: detail.displayNumber, report }
}

test('an engineer raises a System test change request with its case from the Change Requests page', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const seeded = await seedWorkspace(request, suffix)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, `TCR Authoring ${suffix}`)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: '+ New System Test Change Request' }).click()
  const dialog = page.getByRole('dialog', { name: 'Raise a System test change request' })
  await expect(dialog).toBeVisible()

  await dialog.getByLabel('Title').fill('Verify the TCR authoring behavior as one package')
  await dialog.getByLabel('Problem').fill('The approved change introduces behavior with no procedure.')
  await dialog.getByLabel('Analysis').fill('The behavior spans one procedure boundary and belongs together.')
  await dialog.getByLabel('Solution').fill('Raise one SYSTCR and write the procedure it needs.')

  const sourceCheckbox = dialog.getByRole('checkbox', { name: new RegExp(seeded.sourceNumber.replace('.', '\\.')) })
  await expect(sourceCheckbox).toBeVisible()
  await sourceCheckbox.check()

  const reportSearch = dialog.getByRole('searchbox', { name: 'Find controlled PR' })
  await reportSearch.fill(seeded.report.title.slice(-12))
  const reportChoice = dialog.getByRole('checkbox', { name: new RegExp(seeded.report.displayNumber.replace('.', '\\.')) })
  await expect(reportChoice).toBeVisible()
  await reportChoice.check()

  await dialog.getByRole('button', { name: 'Raise SYSTCR' }).click()
  await expect(page.locator('.workspaceSaved')).toContainText('raised.', { timeout: 30_000 })

  // The package opens onto its workspace so the engineer can start its procedure decisions.
  const workspace = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(workspace).toBeVisible({ timeout: 30_000 })
  await expect(workspace.getByText('Engineering case')).toBeVisible()
  await expect(workspace.getByText('Verify the TCR authoring behavior as one package', { exact: true })).toBeVisible()
  await expect(workspace.getByText('Raise one SYSTCR and write the procedure it needs.', { exact: true })).toBeVisible()

  // The case stays correctable while the package is open.
  await workspace.getByRole('button', { name: 'Edit case' }).click()
  const caseDialog = page.getByRole('dialog', { name: /Edit the case of/ })
  await expect(caseDialog).toBeVisible()
  await caseDialog.getByLabel('Title').fill('Verify the TCR authoring behavior as one package (corrected)')
  await caseDialog.getByRole('button', { name: 'Save case' }).click()
  await expect(workspace.getByText('Verify the TCR authoring behavior as one package (corrected)', { exact: true })).toBeVisible({ timeout: 30_000 })

  await workspace.getByRole('button', { name: 'Close test change request' }).click()
  await expect(page.locator('.downstreamAssessment').filter({ hasText: /SYSTCR-/ }).first())
    .toContainText(/SYSTCR-\d{6}\.\d{2}/, { timeout: 30_000 })
})

test('released builds offer no new test change request action', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const seeded = await seedWorkspace(request, suffix, true)
  await login(page, 'admin', { openProject: false })
  await page.goto(
    `/programs/${seeded.workspace.program.id}/projects/${seeded.workspace.project.id}/releases/${seeded.workspace.release.id}/system-verification/coverage`,
    { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: /New System Test Change Request/ })).toHaveCount(0)
})

test('HLR and LLR Change Requests pages offer their own creation actions', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Software HLR Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: '+ New HLR Test Change Request' })).toBeVisible()

  await page.getByRole('link', { name: 'Software LLR Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: '+ New LLR Test Change Request' })).toBeVisible()

  await page.getByRole('button', { name: 'System', exact: true }).last().click()
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('button', { name: '+ New System Test Change Request' })).toBeVisible()
})
