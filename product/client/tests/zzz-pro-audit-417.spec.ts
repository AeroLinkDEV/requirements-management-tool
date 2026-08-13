import { expect, test, type APIRequestContext, type Page, type Route } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram } from './auth'

/**
 * #417 — residual post-#416 picker integrity.
 *
 * A. An unsaved Modify/Retire target selected from the current picker page disappears once search or
 *    paging replaces the picker, because targetOptions only merges the current page with persisted
 *    decision targets from the package payload.
 * B. An obsolete successful picker response clears the newest request's visible error because the
 *    success path clears the error before checking whether the response is still active.
 * C. Changing the Modify/Retire target clears draft.driving but not the client drivingDetails map, so
 *    stale requirement choices from the previous target remain rendered.
 *
 * All journeys run against disposable SQLite data and force the failure deterministically.
 */

const impacts = JSON.stringify({
  trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
  baseline: 'Not Affected', collaboration: 'Not Affected',
})

/**
 * Builds a release carrying `count` governed procedures, then creates a second approved change request
 * whose Open TCR can author against that build. Returns the trigger TCR id and carried target rows.
 */
async function seedCarriedProcedures(
  page: Page,
  request: APIRequestContext,
  suffix: string,
  count: number,
) {
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Audit417 Volume ${suffix}`,
    programCode: `A417V${suffix}`,
    projectName: 'Audit417 Volume Project',
    softwareProduct: 'Audit417 Volume Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const usersResponse = await request.get(`${apiBase}/api/admin/users`)
  const testEngineer = (await usersResponse.json())
    .find((user: { userName: string }) => user.userName === 'test.engineer')
  expect(testEngineer).toBeTruthy()
  const grant = await request.post(`${apiBase}/api/admin/users/${testEngineer.id}/memberships`, { data: {
    programId: workspace.program.id, role: 'TestEngineer',
  } })
  expect(grant.ok(), await grant.text()).toBeTruthy()

  const sections = await (await request.get(
    `${apiBase}/api/authoring/sections?projectId=${workspace.project.id}&level=System`,
  )).json()
  const sectionId = (sections as { id: string }[])[0]?.id
  expect(sectionId).toBeTruthy()
  const requirementChanges = Array.from({ length: count }, (_, index) => ({
    level: 'System', kind: 'Introduce',
    statement: `The audit417 product shall satisfy requirement ${index + 1}.`,
    rationale: 'Audit417 fixture.',
    verificationMethod: 'Test',
    impactDispositionJson: impacts,
    targetSectionId: sectionId,
  }))

  // The trigger change request is created and approved FIRST so it claims the next requirement number
  // before the volume batch; both are selected into the SAME baseline, so the trigger requirement is
  // materialized and the trigger TCR has a real driving-requirement candidate.
  const triggerResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Audit417 trigger ${suffix}`,
    problem: 'A second package opens the Modify picker over the volume build.',
    analysis: 'The picker must retain an unsaved target across search and paging.',
    solution: 'Author one Modify decision against an unsaved target.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: sectionId,
      statement: `The audit417 trigger requirement ${suffix} is introduced for the second package.`,
      rationale: 'Creates an Open package over the already-carried build.',
      verificationMethod: 'Test',
      impactDispositionJson: impacts,
    }],
  } })
  expect(triggerResponse.ok(), await triggerResponse.text()).toBeTruthy()
  const trigger = await triggerResponse.json()
  const triggerSubmitted = await request.post(`${apiBase}/api/change-requests/${trigger.id}/submit`, { data: {
    expectedVersion: trigger.version,
    approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }],
    mode: 'Sequential',
  } })
  expect(triggerSubmitted.ok(), await triggerSubmitted.text()).toBeTruthy()
  const triggerApproved = await request.post(`${apiBase}/api/change-requests/${trigger.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved so the second package can open the Modify picker.',
  } })
  expect(triggerApproved.ok(), await triggerApproved.text()).toBeTruthy()

  const created = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Audit417 volume change ${suffix}`,
    problem: 'Carry a procedure universe for unsaved-target picker testing.',
    analysis: 'The picker must retain an unsaved selection across search and paging.',
    solution: 'Carry the volume procedures, then author in a second package.',
    requirementChanges,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()
  const triggerNumbers = (trigger as { requirementChanges: { baseNumber: string }[] }).requirementChanges
  const volumeNumbers = (draft as { requirementChanges: { baseNumber: string }[] }).requirementChanges
  expect(triggerNumbers).toHaveLength(1)
  expect(volumeNumbers).toHaveLength(count)
  expect(volumeNumbers.some(change => change.baseNumber === triggerNumbers[0].baseNumber)).toBe(false)
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }],
    mode: 'Sequential',
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved for audit417 journey verification.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: 'SW-01.00', revision: 0, projectId: workspace.project.id,
    releaseId: workspace.release.id, predecessorBaselineId: null,
    name: 'Audit417 baseline',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const changeRequestId of [draft.id, trigger.id]) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/selections`, {
      data: { changeRequestId },
    })
    expect(response.ok(), `selection ${changeRequestId}: ${await response.text()}`).toBeTruthy()
  }
  for (const [path, data] of [
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy()
  }

  await login(page, 'test.engineer', { openProject: false })
  await selectProgram(page, `Audit417 Volume ${suffix}`)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Downstream Assessments' }).click()
  const row = page.locator('.downstreamAssessment').filter({ hasText: draft.displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })

  const impactItems = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/verification-impact`,
  )).json() as { id: string; testChangeReviewId: string; subjectDisplayNumber: string; subjectStatement?: string; requirementRevisionId?: string }[]
  const items = impactItems.filter(item => item.requirementRevisionId
    && item.subjectStatement?.startsWith('The audit417 product'))
  expect(items).toHaveLength(count)
  const reviewId = items[0].testChangeReviewId
  for (let index = 0; index < items.length; index++) {
    const item = items[index]
    const proposed = await page.request.post(
      `${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`, { data: {
        kind: 'Introduce', revision: 0, title: `Audit417 procedure ${index + 1}`,
        objective: `Verify audit417 requirement ${index + 1}.`,
        preconditions: 'The configuration is available.',
        steps: '1. Load. 2. Exercise.',
        expectedResult: 'The expected behavior is observed.',
        rationale: `The governed requirement ${item.subjectDisplayNumber} needs exact verification.`,
        drivingRequirementRevisionIds: [item.requirementRevisionId],
      } })
    expect(proposed.ok(), `proposal ${index + 1}: ${await proposed.text()}`).toBeTruthy()
  }
  for (const item of items) {
    const resolved = await page.request.post(
      `${apiBase}/api/verification-impact/${item.id}/resolve`, {
        data: { outcome: 'NewProcedureRequired', rationale: `A procedure will verify ${item.subjectDisplayNumber}.` },
      })
    expect(resolved.ok(), await resolved.text()).toBeTruthy()
  }
  const initialPayload = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`,
  )).json() as { version: number }
  const caseSaved = await page.request.post(`${apiBase}/api/test-change-reviews/${reviewId}/case`, { data: {
    title: `Audit417 package ${suffix}`,
    problem: 'The build needs exact procedure coverage.',
    analysis: `${count} governed procedures must be carried.`,
    solution: 'Approve the exact procedure set.',
    expectedVersion: initialPayload.version,
  } })
  expect(caseSaved.ok(), await caseSaved.text()).toBeTruthy()
  const afterCase = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`,
  )).json() as { version: number }
  const submittedPackage = await page.request.post(
    `${apiBase}/api/test-change-reviews/${reviewId}/submit`, { data: { approverId: 'admin', expectedVersion: afterCase.version } })
  expect(submittedPackage.ok(), await submittedPackage.text()).toBeTruthy()
  const approvedPackage = await request.post(
    `${apiBase}/api/test-change-reviews/${reviewId}/approve`, {
      data: { rationale: `All ${count} procedures are governed and complete.`,
        password: 'AeroLink!2026', meaning: 'Approve the exact procedure set.' },
    })
  expect(approvedPackage.ok(), await approvedPackage.text()).toBeTruthy()
  const selectedTcr = await request.post(`${apiBase}/api/baselines/${baseline.id}/test-change-requests`, {
    data: { testChangeRequestId: reviewId },
  })
  expect(selectedTcr.ok(), await selectedTcr.text()).toBeTruthy()
  const materialized = await request.post(
    `${apiBase}/api/baselines/${baseline.id}/materialize-test-procedures`, { data: {}, timeout: 180_000 })
  expect(materialized.ok(), await materialized.text()).toBeTruthy()

  await page.reload()
  const reopenedRow = page.locator('.downstreamAssessment').filter({ hasText: trigger.displayNumber }).first()
  await expect(reopenedRow).toBeVisible({ timeout: 30_000 })
  await reopenedRow.getByRole('button', { name: 'Open assessment' }).click()
  const assessment2 = page.getByRole('dialog', { name: /test impact/ })
  await assessment2.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment2).toContainText('SYSTCR Created', { timeout: 30_000 })
  const afterTrigger = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/verification-impact`,
  )).json() as { testChangeReviewId: string; subjectStatement?: string }[]
  const triggerItem = afterTrigger.find(entry => entry.subjectStatement?.includes('trigger requirement'))
  expect(triggerItem).toBeTruthy()
  const triggerReviewId = triggerItem!.testChangeReviewId

  const targets: { baseNumber: string; currentRevision: number }[] = []
  for (let targetPage = 1; targetPage <= Math.ceil(count / 200); targetPage++) {
    const pageBody = await (await request.get(
      `${apiBase}/api/test-change-reviews/${triggerReviewId}/procedure-targets?page=${targetPage}&pageSize=200`,
    )).json() as { items: { baseNumber: string; currentRevision: number }[] }
    targets.push(...pageBody.items)
  }
  expect(targets).toHaveLength(count)
  return { workspace, triggerReviewId, targets }
}

test('an unsaved Modify target and its driving selections survive search, paging and target changes', async ({ page, request }) => {
  test.setTimeout(600_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const carriedCount = 60
  const { triggerReviewId, targets } = await seedCarriedProcedures(
    page, request, suffix, carriedCount)
  // Both targets sit on the initial 50-row page and have no persisted decision in the trigger TCR.
  const unsavedTarget = targets[carriedCount - 11].baseNumber
  const otherTarget = targets[carriedCount - 12].baseNumber
  const retireTarget = targets[carriedCount - 13].baseNumber

  await page.reload()
  const row = page.locator('.downstreamAssessment').filter({ hasText: /Audit417 trigger/ }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}/ }).click()
  const workspaceDrawer = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(workspaceDrawer.getByRole('button', { name: 'Propose a procedure change' }))
    .toBeVisible({ timeout: 60_000 })
  await workspaceDrawer.getByRole('button', { name: 'Propose a procedure change' }).click()
  const dialog = page.getByRole('dialog', { name: 'Propose a procedure change' })
  await dialog.getByLabel('What is being done').selectOption('Modify')
  const options = dialog.locator('select[aria-label="Procedure"] option')
  await expect(options.filter({ hasText: unsavedTarget })).toHaveCount(1, { timeout: 30_000 })
  await dialog.getByRole('combobox', { name: 'Procedure' }).selectOption(unsavedTarget)

  // Check one governed driving requirement for the selected target (stale-map Finding C).
  const drivingFieldset = dialog.locator('fieldset.drivingRequirements').last()
  const drivingBoxes = drivingFieldset.locator('input[type="checkbox"]')
  await expect(drivingBoxes.first()).toBeVisible({ timeout: 30_000 })
  await drivingBoxes.first().check()
  const coverageIdentity = (await dialog.locator('fieldset.drivingRequirements').first()
    .locator('label.drivingChoice').first().textContent())!.trim()
  expect(coverageIdentity).toMatch(/SYSR-\d{6}\.\d{2}/)

  // Finding A: search so the unsaved target would be excluded; it must remain visible and selected.
  const search = dialog.getByRole('textbox', { name: 'Search procedures' })
  await search.fill(targets[0].baseNumber)
  await expect(dialog.locator('select[aria-label="Procedure"] option').filter({ hasText: unsavedTarget }))
    .toHaveCount(1, { timeout: 30_000 })
  await expect(dialog.getByRole('combobox', { name: 'Procedure' })).toHaveValue(unsavedTarget)

  // The same target remains selected after paging forward and back.
  await search.fill('')
  const targetPager = dialog.locator('.pickerMeta').first()
  await targetPager.getByRole('button', { name: 'Next' }).click()
  await expect(dialog.locator('select[aria-label="Procedure"] option').filter({ hasText: unsavedTarget }))
    .toHaveCount(1, { timeout: 15_000 })
  await expect(dialog.getByRole('combobox', { name: 'Procedure' })).toHaveValue(unsavedTarget)
  await targetPager.getByRole('button', { name: 'Previous' }).click()
  await expect(dialog.locator('select[aria-label="Procedure"] option').filter({ hasText: unsavedTarget }))
    .toHaveCount(1, { timeout: 15_000 })
  await expect(dialog.getByRole('combobox', { name: 'Procedure' })).toHaveValue(unsavedTarget)
  // The retained target keeps its exact carried revision and exact current coverage across paging.
  await expect(dialog.locator('select[aria-label="Procedure"] option').filter({ hasText: unsavedTarget }))
    .toContainText(`${unsavedTarget}.00`)
  await expect(dialog.locator('fieldset.drivingRequirements').first()).toContainText('Current exact coverage')
  await expect(dialog.locator('fieldset.drivingRequirements').first().locator('label.drivingChoice'))
    .toHaveCount(1, { timeout: 15_000 })
  await expect(dialog.locator('fieldset.drivingRequirements').first()).toContainText(coverageIdentity)
  await targetPager.getByRole('button', { name: 'Next' }).click()
  await expect(dialog.locator('fieldset.drivingRequirements').first()).toContainText(coverageIdentity)
  await targetPager.getByRole('button', { name: 'Previous' }).click()
  await expect(dialog.locator('fieldset.drivingRequirements').first()).toContainText(coverageIdentity)

  // Finding C: switching to another target while a search excludes the previous selection must clear the
  // previous target's driving selections from the rendered candidate list (no stale unchecked choices
  // from drivingDetails).
  await search.fill('')
  const drivingSearch = dialog.getByRole('textbox', { name: 'Search requirements' })
  await drivingSearch.fill('zz-no-match')
  await dialog.getByRole('combobox', { name: 'Procedure' }).selectOption(otherTarget)
  await expect(dialog.locator('select[aria-label="Procedure"] option').filter({ hasText: otherTarget }))
    .toHaveCount(1, { timeout: 15_000 })
  await expect(drivingFieldset.locator('label.drivingChoice')).toHaveCount(0, { timeout: 15_000 })

  // Return to the unsaved target and submit a real Modify; the exact target persists.
  await drivingSearch.fill('')
  await dialog.getByRole('combobox', { name: 'Procedure' }).selectOption(unsavedTarget)
  await expect(dialog.getByRole('combobox', { name: 'Procedure' })).toHaveValue(unsavedTarget)
  await dialog.getByLabel('Title').fill(`Audit417 modify ${suffix}`)
  await dialog.getByLabel('Objective').fill('Verify the carried behavior after modification.')
  await dialog.getByLabel('Preconditions').fill('The configuration is available.')
  await dialog.getByLabel('Steps').fill('1. Load. 2. Exercise.')
  await dialog.getByLabel('Expected result').fill('The expected behavior is observed.')
  await dialog.getByLabel('Why this procedure work is required').fill('The approved change requires an exact procedure update.')
  await dialog.getByRole('button', { name: 'Propose decision' }).click()
  await expect(dialog).toHaveCount(0, { timeout: 30_000 })

  const payload = await (await request.get(
    `${apiBase}/api/test-change-reviews/${triggerReviewId}/procedure-changes`,
  )).json() as { procedureChanges: { kind: string; baseNumber: string; revision: number }[] }
  const modify = payload.procedureChanges.find(change => change.baseNumber === unsavedTarget)
  expect(modify).toBeTruthy()
  expect(modify!.kind).toBe('Modify')
  expect(modify!.revision).toBe(targets[carriedCount - 11].currentRevision + 1)

  // Retire-specific acceptance: an unsaved Retire target stays visibly selected through an excluding
  // search, is the same exact target immediately before submission, and the Retire decision persists.
  await workspaceDrawer.getByRole('button', { name: 'Propose a procedure change' }).click()
  const retireDialog = page.getByRole('dialog', { name: 'Propose a procedure change' })
  await retireDialog.getByLabel('What is being done').selectOption('Retire')
  const retireOptions = retireDialog.locator('select[aria-label="Procedure"] option')
  await expect(retireOptions.filter({ hasText: retireTarget })).toHaveCount(1, { timeout: 30_000 })
  await retireDialog.getByRole('combobox', { name: 'Procedure' }).selectOption(retireTarget)
  const retireSearch = retireDialog.getByRole('textbox', { name: 'Search procedures' })
  await retireSearch.fill(targets[0].baseNumber)
  await expect(retireOptions.filter({ hasText: retireTarget })).toHaveCount(1, { timeout: 30_000 })
  await expect(retireDialog.getByRole('combobox', { name: 'Procedure' })).toHaveValue(retireTarget)
  await expect(retireDialog).toContainText(`${retireTarget}.00`)
  await retireDialog.getByLabel('Why this procedure work is required').fill('The carried procedure is retired by the approved change.')
  await retireDialog.getByRole('button', { name: 'Propose decision' }).click()
  await expect(retireDialog).toHaveCount(0, { timeout: 30_000 })
  const retirePayload = await (await request.get(
    `${apiBase}/api/test-change-reviews/${triggerReviewId}/procedure-changes`,
  )).json() as { procedureChanges: { kind: string; baseNumber: string }[] }
  const retire = retirePayload.procedureChanges.find(change => change.baseNumber === retireTarget && change.kind === 'Retire')
  expect(retire).toBeTruthy()

  // Reload and reopen: the persisted Retire decision visibly names the same exact target and revision.
  await page.reload()
  const reopenedRow = page.locator('.downstreamAssessment').filter({ hasText: /Audit417 trigger/ }).first()
  await expect(reopenedRow).toBeVisible({ timeout: 30_000 })
  await reopenedRow.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}/ }).click()
  const reopened = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(reopened).toContainText(`${retireTarget}.01`, { timeout: 30_000 })
  await expect(reopened).toContainText('Retired procedure')
})

test('an obsolete successful picker response cannot clear a newer visible failure', async ({ page, request }) => {
  test.setTimeout(600_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const carriedCount = 60
  const { targets } = await seedCarriedProcedures(page, request, suffix, carriedCount)

  await page.reload()
  const row = page.locator('.downstreamAssessment').filter({ hasText: /Audit417 trigger/ }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}/ }).click()
  const workspaceDrawer = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(workspaceDrawer.getByRole('button', { name: 'Propose a procedure change' }))
    .toBeVisible({ timeout: 60_000 })

  // Hold the first procedure-targets request; fail the second; then release the first successfully.
  const heldTargetRoutes: Route[] = []
  let targetRequestCount = 0
  await page.route('**/procedure-targets?**', async route => {
    targetRequestCount++
    if (targetRequestCount === 1) { heldTargetRoutes.push(route); return }
    await route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"forced failure"}' })
  })
  const heldRequirementRoutes: Route[] = []
  let requirementRequestCount = 0
  await page.route('**/requirement-candidates?**', async route => {
    requirementRequestCount++
    if (requirementRequestCount === 1) { heldRequirementRoutes.push(route); return }
    await route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"forced failure"}' })
  })

  await workspaceDrawer.getByRole('button', { name: 'Propose a procedure change' }).click()
  const dialog = page.getByRole('dialog', { name: 'Propose a procedure change' })
  await dialog.getByLabel('What is being done').selectOption('Modify')
  await expect.poll(() => heldTargetRoutes.length).toBeGreaterThan(0)
  await expect.poll(() => heldRequirementRoutes.length).toBeGreaterThan(0)

  // A newer request fails first and must display its visible error.
  await dialog.getByRole('textbox', { name: 'Search procedures' }).fill('zz-force-failure')
  const targetAlert = dialog.getByRole('alert').filter({ hasText: 'The procedures for this build' })
  await expect(targetAlert).toContainText('The procedures for this build could not be loaded.', { timeout: 15_000 })
  await dialog.getByRole('textbox', { name: 'Search requirements' }).fill('zz-force-failure')
  const requirementAlert = dialog.getByRole('alert').filter({ hasText: 'The governed requirements for this build' })
  await expect(requirementAlert).toContainText('The governed requirements for this build could not be loaded.', { timeout: 15_000 })

  // Releasing the obsolete successes must NOT clear either visible error.
  for (const route of heldTargetRoutes) {
    await route.continue()
  }
  for (const route of heldRequirementRoutes) {
    await route.continue()
  }
  await expect(targetAlert).toContainText('The procedures for this build could not be loaded.', { timeout: 15_000 })
  await expect(requirementAlert).toContainText('The governed requirements for this build could not be loaded.', { timeout: 15_000 })
  // The obsolete successes must also NOT replace the newest request's result state: with the failing search
  // still active, the picker reports zero matches rather than the obsolete page's match count.
  await expect(dialog).toContainText('0 matching carried procedures.', { timeout: 15_000 })
  await expect(dialog).toContainText('0 matching governed requirements.', { timeout: 15_000 })

  // A later successful retry clears the errors and restores current results.
  await page.unroute('**/procedure-targets?**')
  await page.unroute('**/requirement-candidates?**')
  await dialog.getByRole('textbox', { name: 'Search procedures' }).fill('')
  await dialog.getByRole('textbox', { name: 'Search requirements' }).fill('')
  await expect(dialog.getByRole('alert')).toHaveCount(0, { timeout: 15_000 })
  await expect(dialog).toContainText(`${carriedCount} carried procedures in this build`, { timeout: 15_000 })
  await expect(dialog).toContainText('1 governed requirement in scope', { timeout: 15_000 })

  // Success-vs-success: a newer successful query must publish its own distinct result, and an older
  // successful response arriving later must not replace it, on BOTH picker surfaces.
  const heldPhase2Target: Route[] = []
  let phase2TargetCount = 0
  await page.route('**/procedure-targets?**', async route => {
    phase2TargetCount++
    if (phase2TargetCount === 1) { heldPhase2Target.push(route); return }
    await route.continue()
  })
  const heldPhase2Requirement: Route[] = []
  let phase2RequirementCount = 0
  await page.route('**/requirement-candidates?**', async route => {
    phase2RequirementCount++
    if (phase2RequirementCount === 1) { heldPhase2Requirement.push(route); return }
    await route.continue()
  })
  await dialog.getByRole('textbox', { name: 'Search procedures' }).fill('zz-no-match')
  await expect.poll(() => heldPhase2Target.length).toBeGreaterThan(0)
  await dialog.getByRole('textbox', { name: 'Search requirements' }).fill('zz-no-match')
  await expect.poll(() => heldPhase2Requirement.length).toBeGreaterThan(0)

  await dialog.getByRole('textbox', { name: 'Search procedures' }).fill(targets[0].baseNumber)
  await expect(dialog).toContainText('1 matching carried procedure.', { timeout: 15_000 })
  await dialog.getByRole('textbox', { name: 'Search requirements' }).fill('trigger requirement')
  await expect(dialog).toContainText('1 matching governed requirement.', { timeout: 15_000 })

  for (const route of heldPhase2Target) await route.continue()
  for (const route of heldPhase2Requirement) await route.continue()
  await expect(dialog).toContainText('1 matching carried procedure.', { timeout: 15_000 })
  await expect(dialog).toContainText('1 matching governed requirement.', { timeout: 15_000 })
})
