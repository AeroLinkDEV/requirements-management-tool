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
      category: 'CodeFunctional', projectId: workspace.project.id,
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

  // A page, not a pop-up: raising a package is the same act as raising a change request, and its counterpart
  // has always been a page.
  await page.getByRole('button', { name: '+ New System Test Procedure Change Request' }).click()
  const editor = page.locator('[data-tcr-editor]')
  await expect(page.getByRole('heading', { name: 'Create System Test Procedure Change Request', level: 1 })).toBeVisible({ timeout: 30_000 })
  await expect(page).toHaveURL(/\/system-verification\/change-requests\/new$/)
  // The same two numbered stages the requirements editor shows. Addressed as headings, because each stage
  // name appears twice on the page — once in the progress rail and once on the card it points at.
  await expect(editor.getByRole('heading', { name: 'Change case', level: 2 })).toBeVisible()
  await expect(editor.getByRole('heading', { name: 'Procedure changes', level: 2 })).toBeVisible()

  await editor.getByLabel('Title').fill('Verify the TCR authoring behavior as one package')
  await editor.getByLabel('Problem').fill('The approved change introduces behavior with no procedure.')
  await editor.getByLabel('Analysis').fill('The behavior spans one procedure boundary and belongs together.')
  await editor.getByLabel('Solution').fill('Raise one SYSTPCR and write the procedure it needs.')

  const sourceCheckbox = editor.getByRole('checkbox', { name: new RegExp(seeded.sourceNumber.replace('.', '\\.')) })
  await expect(sourceCheckbox).toBeVisible()
  await sourceCheckbox.check()

  const reportSearch = editor.getByRole('searchbox', { name: 'Find controlled PR' })
  await reportSearch.fill(seeded.report.title.slice(-12))
  const reportChoice = editor.getByRole('checkbox', { name: new RegExp(seeded.report.displayNumber.replace('.', '\\.')) })
  await expect(reportChoice).toBeVisible()
  await reportChoice.check()

  // Stage two: a procedure decision authored on the page and saved with the package, exactly as a change
  // request is created together with the requirement changes it proposes.
  // The act is chosen before the card exists, as it is on the requirements side.
  await editor.getByRole('button', { name: '+ Introduce System test procedure' }).click()
  const proposal = editor.locator('[data-procedure-proposal="0"]')
  await expect(proposal).toBeVisible()
  await proposal.getByLabel('Procedure number').fill('SYSTP-009901')
  await proposal.getByLabel('Title 1').fill('Verify oceanic sequencing under the new behaviour')
  await proposal.getByLabel('Objective 1').fill('Show the sequencing holds across the transition.')
  await proposal.getByLabel('Steps 1').fill('Exercise the changed behaviour on the rig.')
  await proposal.getByLabel('Expected result 1').fill('The sequencing is observed to hold.')
  await proposal.getByLabel('Rationale 1').fill('The approved change introduces behaviour with no procedure.')

  // An incomplete decision holds the package back rather than being silently dropped on save.
  await expect(editor.getByRole('button', { name: 'Raise SYSTPCR' })).toBeEnabled()

  await editor.getByRole('button', { name: 'Raise SYSTPCR' }).click()

  // The package opens onto its workspace so the engineer can start its procedure decisions.
  const workspace = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(workspace).toBeVisible({ timeout: 30_000 })
  await expect(workspace.getByText('Engineering case')).toBeVisible()
  await expect(workspace.getByText('Verify the TCR authoring behavior as one package', { exact: true })).toBeVisible()
  await expect(workspace.getByText('Raise one SYSTPCR and write the procedure it needs.', { exact: true })).toBeVisible()

  // The case stays correctable while the package is open.
  await workspace.getByRole('button', { name: 'Edit case' }).click()
  const caseDialog = page.getByRole('dialog', { name: /Edit the case of/ })
  await expect(caseDialog).toBeVisible()
  await caseDialog.getByLabel('Title').fill('Verify the TCR authoring behavior as one package (corrected)')
  await caseDialog.getByRole('button', { name: 'Save case' }).click()
  await expect(workspace.getByText('Verify the TCR authoring behavior as one package (corrected)', { exact: true })).toBeVisible({ timeout: 30_000 })

  await workspace.getByRole('button', { name: 'Close test change request' }).click()
  await expect(page.locator('.downstreamAssessment').filter({ hasText: /SYSTPCR-/ }).first())
    .toContainText(/SYSTPCR-\d{6}\.\d{2}/, { timeout: 30_000 })
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
  await expect(page.getByRole('heading', { name: 'Downstream Assessments' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: /New System Test (Case|Procedure) Change Request/ })).toHaveCount(0)
})

test('HLR and LLR Change Requests pages offer their own creation actions', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Software Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Software Test Change Requests' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: '+ New HLR Test Case Change Request' })).toBeVisible()

  await page.getByRole('tab', { name: 'LLR' }).click()
  await expect(page.getByRole('button', { name: '+ New LLR Test Case Change Request' })).toBeVisible()

  await page.getByRole('button', { name: 'System', exact: true }).last().click()
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('button', { name: '+ New System Test Procedure Change Request' })).toBeVisible()
})
