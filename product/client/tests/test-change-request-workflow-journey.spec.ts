import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

/**
 * A configured two-stage sequential TCR review, completed through the real browser UI.
 *
 * The package is genuinely submittable: the workspace's driving-requirement choices come from a materialized
 * requirement revision, the workflow is configured through the API, and both stage approvers sign through the
 * controls the read model exposes after each stage activation.
 */
test('a configured two-stage sequential TCR review completes through the UI', async ({ page, browser, request }) => {
  test.setTimeout(480_000)
  const password = 'AeroLink!2026'
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const programName = `Workflow Journey ${suffix}`

  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName,
    programCode: `WJ${suffix}`,
    projectName: 'Workflow Journey Project',
    softwareProduct: 'Workflow Journey Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()

  const impacts = JSON.stringify({
    trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
    baseline: 'Not Affected', collaboration: 'Not Affected',
  })
  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Workflow journey source ${suffix}`,
    problem: 'The new behavior has no test coverage.',
    analysis: 'A procedure must be written.',
    solution: 'Author it from the package.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: await firstSectionId(request, workspace.project.id),
      statement: `The ${suffix} product shall expose a workflow-journey verification target.`,
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
    data: { password: 'AeroLink!2026', meaning: 'Approved for the workflow journey.' },
  })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: `SW-98.${suffix.slice(-2)}`, revision: 0,
    projectId: workspace.project.id, releaseId: workspace.release.id,
    name: 'Workflow journey materialized build',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const [path, data] of [
    ['selections', { changeRequestId: draft.id }],
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), await response.text()).toBeTruthy()
  }
  const requirementsResponse = await request.get(
    `${apiBase}/api/requirements?projectId=${workspace.project.id}&baselineId=${baseline.id}&scope=System&includeRetired=false&page=1&pageSize=10`)
  expect(requirementsResponse.ok(), await requirementsResponse.text()).toBeTruthy()
  const requirementRevisionId = (await requirementsResponse.json()).items[0].revisionId as string
  expect(requirementRevisionId).toBeTruthy()

  // Two seeded demo accounts, granted into this fresh Program so the standard password works in the UI.
  // The workflow below demands an explicit modern authority, so each signer holds that base role.
  const usersResponse = await request.get(`${apiBase}/api/admin/users`)
  expect(usersResponse.ok(), await usersResponse.text()).toBeTruthy()
  const users = await usersResponse.json()
  const stageOne = users.find((user: { userName: string }) => user.userName === 'systems.reviewer')
  const stageTwo = users.find((user: { userName: string }) => user.userName === 'assurance.reviewer')
  expect(stageOne).toBeTruthy()
  expect(stageTwo).toBeTruthy()
  for (const reviewer of [stageOne, stageTwo]) {
    const grant = await request.post(`${apiBase}/api/admin/users/${reviewer.id}/memberships`,
      { data: { programId: workspace.program.id, role: 'SoftwareEngineer' } })
    expect(grant.ok(), await grant.text()).toBeTruthy()
  }

  const workflowResponse = await request.post(`${apiBase}/api/review-workflows`, { data: {
    projectId: workspace.project.id,
    name: 'Two stage TCR',
    appliesTo: 'SystemTest',
    mode: 'Sequential',
    stages: [
      { name: 'First', kind: 'Review', requiredAuthority: { kind: 'BaseRole', role: 'SoftwareEngineer' } },
      { name: 'Second', kind: 'Approval', requiredAuthority: { kind: 'BaseRole', role: 'SoftwareEngineer' } },
    ],
  } })
  expect(workflowResponse.ok(), await workflowResponse.text()).toBeTruthy()
  const workflow = await workflowResponse.json()
  const activated = await request.post(`${apiBase}/api/review-workflows/${workflow.id}/activate`, { data: {} })
  expect(activated.ok(), await activated.text()).toBeTruthy()

  const sourceDetail = await (await request.get(`${apiBase}/api/change-requests/${draft.id}`)).json()
  const sourceDisplay = sourceDetail.baseNumber as string

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, programName)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Downstream Assessments' })).toBeVisible({ timeout: 30_000 })

  const row = page.locator('.downstreamAssessment').filter({ hasText: sourceDisplay })
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const drawer = page.getByRole('dialog', { name: /test impact/ })
  await expect(drawer).toBeVisible()

  await drawer.getByRole('button', { name: 'Decide' }).first().click()
  const decide = page.getByRole('dialog', { name: /Decide / })
  await decide.getByLabel('Decision').selectOption('NewProcedureRequired')
  await decide.getByLabel('Rationale').fill('A new procedure must be written for the new requirement.')
  await decide.getByRole('button', { name: 'Record decision' }).click()
  await expect(decide).toHaveCount(0, { timeout: 30_000 })

  await drawer.getByRole('button', { name: 'SYSTPCR required', exact: true }).click()
  const packageLink = drawer.getByRole('button', { name: /SYSTPCR-\d{6}\.\d{2}/ })
  await expect(packageLink).toBeVisible({ timeout: 30_000 })
  await packageLink.click()
  const workspaceDrawer = page.getByRole('dialog', { name: /test procedure decisions/ })
  await expect(workspaceDrawer).toBeVisible()
  await workspaceDrawer.getByRole('button', { name: 'Propose a test procedure change' }).click()
  const propose = page.getByRole('dialog', { name: 'Propose a test procedure change' })
  await propose.getByLabel('Title').fill('Workflow journey procedure')
  await propose.getByLabel('Objective').fill('Verify the workflow-journey target behavior.')
  await propose.getByLabel('Preconditions').fill('The target is available.')
  await propose.getByLabel('Steps').fill('Exercise the target.')
  await propose.getByLabel('Expected result').fill('The expected behavior is observed.')
  await propose.getByLabel('Why this test procedure work is required').fill('Nothing covers the new requirement.')
  await propose.getByRole('checkbox').first().check()
  await propose.getByRole('button', { name: 'Propose decision' }).click()
  await expect(propose).toHaveCount(0, { timeout: 30_000 })
  await workspaceDrawer.getByRole('button', { name: 'Close test change request' }).click()

  // Procedure work alone is not an approvable engineering case. The queue directs the assigned engineer
  // back to the case and withholds Send until all four governed fields are present.
  await expect(drawer.getByRole('button', { name: 'Send for approval' })).toHaveCount(0)
  const completeCase = drawer.getByRole('button', { name: 'Complete engineering case' })
  await expect(completeCase).toBeVisible({ timeout: 30_000 })
  await completeCase.click()
  const caseWorkspace = page.getByRole('dialog', { name: /test procedure decisions/ })
  await expect(caseWorkspace.getByText(/Missing before review: Title, Problem, Analysis, Solution/)).toBeVisible()
  await caseWorkspace.getByRole('button', { name: 'Write engineering case' }).click()
  const caseDialog = page.getByRole('dialog', { name: /Edit the case of/ })
  await caseDialog.getByLabel('Title').fill('Workflow journey verification case')
  await caseDialog.getByLabel('Problem').fill('The changed behavior has no controlled verification coverage.')
  await caseDialog.getByLabel('Analysis').fill('A new procedure is required to qualify the behavior.')
  await caseDialog.getByLabel('Solution').fill('Introduce and independently approve the proposed procedure.')
  await caseDialog.getByRole('button', { name: 'Save case' }).click()
  await expect(caseDialog).toHaveCount(0, { timeout: 30_000 })
  await caseWorkspace.getByRole('button', { name: 'Close test change request' }).click()

  const send = drawer.getByRole('button', { name: 'Send for approval' })
  await expect(send).toBeVisible({ timeout: 30_000 })
  await send.click()
  const submit = page.getByRole('dialog', { name: /Select approver/ })
  await expect(submit).toBeVisible()
  await expect(submit.getByRole('combobox')).toHaveCount(2, { timeout: 30_000 })
  await submit.getByLabel(/First/).selectOption(stageOne.userName)
  await submit.getByLabel(/Second/).selectOption(stageTwo.userName)
  await submit.getByRole('button', { name: 'Send for approval' }).click()
  await expect(submit).toHaveCount(0, { timeout: 30_000 })
  await expect(page.locator('.workspaceSaved')).toContainText('sent for approval', { timeout: 30_000 })

  const coverageUrl = page.url()

  // Stage one signs; the TCR stays InReview; stage two becomes actionable through the read model.
  const stageOneContext = await browser.newContext()
  const stageOnePage = await stageOneContext.newPage()
  await login(stageOnePage, 'systems.reviewer', { openProject: false })
  await stageOnePage.goto(coverageUrl, { waitUntil: 'load' })
  const stageOneRow = stageOnePage.locator('.downstreamAssessment').filter({ hasText: /SYSTPCR-/ })
  await expect(stageOneRow).toBeVisible({ timeout: 30_000 })
  await stageOneRow.getByRole('button', { name: 'Open assessment' }).click()
  const stageOneDrawer = stageOnePage.getByRole('dialog', { name: /test impact/ })
  await expect(stageOneDrawer.getByRole('button', { name: 'Approve' })).toBeVisible({ timeout: 30_000 })
  await stageOneDrawer.getByRole('button', { name: 'Approve' }).click()
  const stageOneConfirm = stageOnePage.getByRole('dialog', { name: /Approve SYSTPCR/ })
  await expect(stageOneConfirm).toBeVisible()
  await stageOneConfirm.getByLabel('Approval rationale').fill('Stage one is sound.')
  await stageOneConfirm.getByLabel('Signature meaning').fill('I approve the exact Test Lead review stage.')
  await stageOneConfirm.getByLabel('Password').fill(password)
  await stageOneConfirm.getByRole('button', { name: 'Sign and approve package' }).click()
  await expect(stageOnePage.locator('.workspaceSaved')).toContainText('approved', { timeout: 30_000 })
  await expect(stageOneDrawer.getByRole('button', { name: 'Approve' })).toHaveCount(0, { timeout: 30_000 })
  await stageOneContext.close()

  const stageTwoContext = await browser.newContext()
  const stageTwoPage = await stageTwoContext.newPage()
  await login(stageTwoPage, 'assurance.reviewer', { openProject: false })
  await stageTwoPage.goto(coverageUrl, { waitUntil: 'load' })
  const stageTwoRow = stageTwoPage.locator('.downstreamAssessment').filter({ hasText: /SYSTPCR-/ })
  await expect(stageTwoRow).toBeVisible({ timeout: 30_000 })
  await stageTwoRow.getByRole('button', { name: 'Open assessment' }).click()
  const stageTwoDrawer = stageTwoPage.getByRole('dialog', { name: /test impact/ })
  await expect(stageTwoDrawer.getByRole('button', { name: 'Approve' })).toBeVisible({ timeout: 30_000 })
  await stageTwoDrawer.getByRole('button', { name: 'Approve' }).click()
  const stageTwoConfirm = stageTwoPage.getByRole('dialog', { name: /Approve SYSTPCR/ })
  await expect(stageTwoConfirm).toBeVisible()
  await stageTwoConfirm.getByLabel('Approval rationale').fill('Stage two is sound.')
  await stageTwoConfirm.getByLabel('Signature meaning').fill('I approve the exact final review stage.')
  await stageTwoConfirm.getByLabel('Password').fill(password)
  await stageTwoConfirm.getByRole('button', { name: 'Sign and approve package' }).click()
  await expect(stageTwoPage.locator('.workspaceSaved')).toContainText('approved', { timeout: 30_000 })
  await stageTwoContext.close()

  const reviews = await (await request.get(`${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`)).json()
  const packageItem = reviews.items.find((item: { displayNumber: string }) => item.displayNumber.startsWith('SYSTPCR-'))
  expect(packageItem).toBeTruthy()
  expect(packageItem.state).toBe('Approved')
  expect(packageItem.reviewCycle.state).toBe('Approved')
  expect(packageItem.reviewCycle.steps.map((step: { state: string }) => step.state)).toEqual(['Approved', 'Approved'])
})
