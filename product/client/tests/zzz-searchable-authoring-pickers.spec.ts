import { expect, test, type APIRequestContext, type Playwright } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from './auth'

/**
 * #402 — TCR authoring pickers must not silently truncate the candidate universe.
 *
 * Bounded server-searched pickers with totals and exact-ID hydration. Each journey is order-independent:
 * the FMS journeys never freeze or materialize the shared in-work baseline, and the requirement-volume
 * journey builds its own isolated program so the shared showcase state cannot change its outcome.
 */

const impacts = JSON.stringify({
  trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
  baseline: 'Not Affected', collaboration: 'Not Affected',
})

async function createApprovedLlrChange(
  request: APIRequestContext, playwright: Playwright, showcase: { projectId: string; activeReleaseId: string },
  baseNumber: string, suffix: string,
) {
  const author = await playwright.request.newContext()
  await apiLogin(author, 'software.author')
  const baseline = (await (await author.get(
    `${apiBase}/api/build-context?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`,
  )).json()).effectiveBaselineId as string
  const listed = await (await author.get(
    `${apiBase}/api/requirements?projectId=${showcase.projectId}&baselineId=${baseline}&scope=LowLevelSoftware&search=${baseNumber}&page=1&pageSize=5`,
  )).json()
  const current = (listed.items ?? []).find((item: { baseNumber: string }) => item.baseNumber === baseNumber)
  const revision = ((current?.revision ?? 0) as number) + 1
  const hlrs = await (await author.get(
    `${apiBase}/api/requirements?projectId=${showcase.projectId}&baselineId=${baseline}&scope=HighLevelSoftware&search=HLR-000250&page=1&pageSize=5`,
  )).json()
  const hlr = (hlrs.items ?? []).find((item: { baseNumber: string }) => item.baseNumber === 'HLR-000250')
  expect(hlr?.revisionId).toBeTruthy()
  const created = await author.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    type: 'Software',
    softwareLevel: 'LowLevel',
    title: `LLR picker change ${suffix}`,
    problem: 'The verification discipline must answer for the changed behavior.',
    analysis: 'An exact procedure or coverage confirmation is required.',
    solution: 'Let the LLR test discipline assess and author the exact verification work.',
    requirementChanges: [{
      level: 'LowLevel', kind: 'Modify', baseNumber, revision,
      statement: `${baseNumber} shall satisfy the approved LLR picker refinement ${suffix}.`,
      rationale: 'Controlled LLR change for picker reachability.',
      verificationMethod: 'Test',
      impactDispositionJson: impacts,
      upstreamRevisionIds: [hlr.revisionId],
    }],
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()
  const submitted = await author.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }],
    mode: 'Sequential',
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved so the LLR test discipline can author the exact verification work.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()
  await author.dispose()
  return { displayNumber: draft.displayNumber as string, revision }
}

test('a Modify target beyond the former 200-row limit is searchable, selectable and survives reload', async ({ page, request, playwright }) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const { displayNumber } = await createApprovedLlrChange(
    request, playwright, showcase, 'LLR-000650', suffix)

  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()
  await page.getByRole('link', { name: 'Software LLR Downstream Assessments' }).click()
  const row = page.locator('.downstreamAssessment').filter({ hasText: displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  await assessment.getByRole('button', { name: 'LLRTCR required', exact: true }).click()
  await expect(assessment).toContainText('LLRTCR Created', { timeout: 30_000 })
  await assessment.getByRole('button', { name: /^LLRTCR-\d{6}\.\d{2}$/ }).click()
  const workspace = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(workspace).toBeVisible()

  await workspace.getByRole('button', { name: 'Propose a procedure change' }).click()
  const dialog = page.getByRole('dialog', { name: 'Propose a procedure change' })
  await dialog.getByLabel('What is being done').selectOption('Modify')
  // The universe total is stated, so a bounded page is never presented as the complete candidate set.
  await expect(dialog).toContainText('280 carried procedures in this build', { timeout: 30_000 })
  await dialog.getByRole('textbox', { name: 'Search procedures' }).fill('LLRTP-000250')
  const targetOption = dialog.locator('select[aria-label="Procedure"] option').filter({ hasText: 'LLRTP-000250' })
  await expect(targetOption).toHaveCount(1, { timeout: 30_000 })
  await dialog.getByRole('combobox', { name: 'Procedure' }).selectOption('LLRTP-000250')
  await dialog.getByLabel('Title').fill(`LLR picker modify ${suffix}`)
  await dialog.getByLabel('Objective').fill('Verify the carried LLR procedure behavior.')
  await dialog.getByLabel('Steps').fill('Execute the controlled steps.')
  await dialog.getByLabel('Expected result').fill('The expected behavior is observed.')
  await dialog.getByLabel('Why this procedure work is required').fill('The approved change requires an exact procedure update.')
  await dialog.getByRole('button', { name: 'Propose decision' }).click()
  await expect(dialog).toHaveCount(0, { timeout: 30_000 })

  // The proposal is a controlled record: it survives a reload, hydrated by its exact identifier.
  await page.reload()
  const reopenedRow = page.locator('.downstreamAssessment').filter({ hasText: displayNumber }).first()
  await expect(reopenedRow).toBeVisible({ timeout: 30_000 })
  await reopenedRow.getByRole('button', { name: /^LLRTCR-\d{6}\.\d{2}/ }).click()
  const reopened = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(reopened).toContainText('LLRTP-000250.01', { timeout: 30_000 })
})

test('a requirement beyond the former 200-row limit is hydrated, searchable and selectable for authoring', async ({ page, request }) => {
  test.setTimeout(300_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Picker Volume ${suffix}`,
    programCode: `PV${suffix}`,
    projectName: 'Picker Volume Project',
    softwareProduct: 'Picker Volume Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const sections = await (await request.get(
    `${apiBase}/api/authoring/sections?projectId=${workspace.project.id}&level=System`,
  )).json()
  const sectionId = (sections as { id: string }[])[0]?.id
  expect(sectionId).toBeTruthy()

  // One controlled change request introduces 250 System requirements: a volume the old fixed first-page
  // picker could never reach.
  const requirementChanges = Array.from({ length: 250 }, (_, index) => ({
    level: 'System', kind: 'Introduce',
    statement: `The picker volume product shall satisfy requirement ${index + 1}.`,
    rationale: 'Picker reachability volume.',
    verificationMethod: 'Test',
    impactDispositionJson: impacts,
    targetSectionId: sectionId,
  }))
  const created = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Picker volume change ${suffix}`,
    problem: 'Authoring reachability must hold beyond a fixed page.',
    analysis: 'The picker must search and hydrate the exact requirement.',
    solution: 'Introduce 250 requirements and author a procedure for the last one.',
    requirementChanges,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }],
    mode: 'Sequential',
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved for picker-volume journey verification.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: 'SW-01.00', revision: 0, projectId: workspace.project.id,
    releaseId: workspace.release.id, predecessorBaselineId: null,
    name: 'Picker volume baseline',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const [path, data] of [
    ['selections', { changeRequestId: draft.id }],
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy()
  }

  const impactItems = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/verification-impact`,
  )).json()
  const item = (impactItems as { subjectDisplayNumber: string; requirementRevisionId?: string }[])
    .find((entry) => entry.subjectDisplayNumber.endsWith('00250.00'))
  expect(item?.requirementRevisionId).toBeTruthy()
  const subject = item!.subjectDisplayNumber

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, `Picker Volume ${suffix}`)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Downstream Assessments' }).click()
  const row = page.locator('.downstreamAssessment').filter({ hasText: draft.displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })

  const decided = assessment.locator('.decisionList li').filter({ hasText: subject }).first()
  await decided.getByRole('button', { name: 'Decide' }).click()
  const decide = page.getByRole('dialog', { name: /Decide / })
  await decide.getByLabel('Decision').selectOption('NewProcedureRequired')
  await decide.getByLabel('Rationale').fill('A new procedure must be written for this requirement.')
  await decide.getByRole('button', { name: 'Record decision' }).click()
  await expect(decide).toHaveCount(0, { timeout: 30_000 })

  await decided.getByRole('button', { name: 'Author the procedure' }).click()
  const authoring = page.getByRole('dialog', { name: 'Propose a test procedure' })
  await expect(authoring.getByRole('textbox', { name: 'Search requirements' })).toBeVisible()
  // The total is stated, so a bounded page is never presented as the complete universe.
  await expect(authoring).toContainText('250 requirements in scope', { timeout: 30_000 })
  // The exact requirement sits beyond the first page; it is hydrated by immutable ID and already selected.
  const requirementOption = authoring.locator('select[name="requirement"] option').filter({ hasText: subject })
  await expect(requirementOption).toHaveCount(1, { timeout: 30_000 })
  expect(await requirementOption.evaluate(option => option.selected)).toBe(true)
  // Searching by controlled number finds it too.
  await authoring.getByRole('textbox', { name: 'Search requirements' }).fill(subject.split('.')[0])
  await expect(authoring.locator('select[name="requirement"] option').filter({ hasText: subject })).toHaveCount(1)
  await authoring.getByLabel('Title').fill(`Picker volume procedure ${suffix}`)
  await authoring.getByLabel('Objective').fill('Verify the picker volume requirement.')
  await authoring.getByLabel('Preconditions').fill('The picker volume configuration is available.')
  await authoring.getByLabel('Steps').fill('1. Load the configuration. 2. Exercise the behavior.')
  await authoring.getByLabel('Expected result').fill('The expected behavior is observed.')
  await authoring.getByLabel('Why it is needed').fill('Nothing covers the picker volume requirement yet.')
  await authoring.getByRole('button', { name: 'Propose procedure' }).click()
  await expect(authoring).toHaveCount(0, { timeout: 30_000 })

  // The proposal is a controlled record: it survives a reload.
  await page.reload()
  const reopenedRow = page.locator('.downstreamAssessment').filter({ hasText: draft.displayNumber }).first()
  await expect(reopenedRow).toBeVisible({ timeout: 30_000 })
  await reopenedRow.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}/ }).click()
  const reopened = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(reopened.getByText(/SYSTP-\d{6}\.00/)).toBeVisible({ timeout: 30_000 })
})

test('an approved procedure beyond the former 200-row limit is searchable in the coverage-confirmed picker and survives reload', async ({ page, request, playwright }) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const { displayNumber } = await createApprovedLlrChange(
    request, playwright, showcase, 'LLR-000651', suffix)

  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()
  await page.getByRole('link', { name: 'Software LLR Downstream Assessments' }).click()
  const row = page.locator('.downstreamAssessment').filter({ hasText: displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  const decided = assessment.locator('.decisionList li').filter({ hasText: /LLR-000651\./ }).first()
  await decided.getByRole('button', { name: 'Decide' }).click()
  const decide = page.getByRole('dialog', { name: /Decide / })
  const searchProcedures = decide.getByRole('textbox', { name: 'Search approved procedures' })

  // With no active search the metadata truthfully states the full eligible candidate count.
  await expect(decide).toContainText('280 approved procedures in this build.', { timeout: 30_000 })

  await searchProcedures.fill('LLRTP-000250')
  await expect(decide).toContainText('1 matching approved procedure.', { timeout: 30_000 })
  const option = decide.locator('select[name="procedureId"] option').filter({ hasText: 'LLRTP-000250' })
  await expect(option).toHaveCount(1, { timeout: 30_000 })
  const optionValue = await option.getAttribute('value')
  expect(optionValue).toMatch(/[0-9a-f-]{36}/)
  await decide.getByRole('combobox', { name: 'Covering procedure' }).selectOption(optionValue!)

  // The current unsaved choice is hydrated alongside the search result: changing the search to something
  // that would normally exclude procedure A must not remove A from the DOM, because the resolve mutation
  // reads procedureId from the select. This controlled-number search matches more than one page
  // (LLRTP-000100 through LLRTP-000199) while excluding procedure A (LLRTP-000250), so the metadata
  // describes the cross-page match count as matches and separately states that the current selection is
  // kept visible independently of the search. These showcase revisions predate exact TCR title snapshots;
  // their mutable catalog titles are deliberately not treated as revision-specific search evidence.
  await searchProcedures.fill('LLRTP-0001')
  await expect(decide).toContainText('100 matching approved procedures.', { timeout: 30_000 })
  await expect(decide).toContainText('Current selection is kept visible independently of the search.', { timeout: 30_000 })
  await expect(decide.locator('select[name="procedureId"] option').filter({ hasText: 'LLRTP-000250' }))
    .toHaveCount(1, { timeout: 30_000 })
  await expect(decide.getByRole('combobox', { name: 'Covering procedure' })).toHaveValue(optionValue!)
  await expect(decide.getByRole('button', { name: 'Next' })).toBeEnabled()

  // A search with no matches still keeps the exact current selection hydrated, and the metadata reports
  // zero matches truthfully rather than claiming there are no procedures in this build.
  await searchProcedures.fill('zz-no-such-procedure-zz')
  await expect(decide.locator('select[name="procedureId"] option').filter({ hasText: 'LLRTP-000250' }))
    .toHaveCount(1, { timeout: 30_000 })
  await expect(decide.getByRole('combobox', { name: 'Covering procedure' })).toHaveValue(optionValue!)
  await expect(decide).toContainText('0 matching approved procedures.', { timeout: 30_000 })
  await expect(decide).toContainText('Current selection is kept visible independently of the search.', { timeout: 30_000 })
  await expect(decide).not.toContainText('0 approved procedures in this build')

  await decide.getByLabel('Rationale').fill('The carried LLR procedure already verifies this requirement.')
  await decide.getByRole('button', { name: 'Record decision' }).click()
  await expect(decide).toHaveCount(0, { timeout: 30_000 })
  await expect(assessment.locator('.decisionList')).toContainText('LLRTP-000250.00', { timeout: 30_000 })

  await page.reload()
  const reopenedRow = page.locator('.downstreamAssessment').filter({ hasText: displayNumber }).first()
  await expect(reopenedRow).toBeVisible({ timeout: 30_000 })
  await reopenedRow.getByRole('button', { name: 'Open assessment' }).click()
  const reopened = page.getByRole('dialog', { name: /test impact/ })
  await expect(reopened.locator('.decisionList')).toContainText('LLRTP-000250.00', { timeout: 30_000 })
})

test('a Modify target beyond the former 500 limit is reachable in the real picker and survives reload', async ({ page, request }) => {
  test.setTimeout(900_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Modify Volume ${suffix}`,
    programCode: `MV${suffix}`,
    projectName: 'Modify Volume Project',
    softwareProduct: 'Modify Volume Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const usersResponse = await request.get(`${apiBase}/api/admin/users`)
  expect(usersResponse.ok(), await usersResponse.text()).toBeTruthy()
  const testEngineer = (await usersResponse.json())
    .find((user: { userName: string }) => user.userName === 'test.engineer')
  expect(testEngineer).toBeTruthy()
  const grant = await request.post(`${apiBase}/api/admin/users/${testEngineer.id}/memberships`, { data: {
    programId: workspace.program.id,
    role: 'TestEngineer',
  } })
  expect(grant.ok(), await grant.text()).toBeTruthy()

  const sections = await (await request.get(
    `${apiBase}/api/authoring/sections?projectId=${workspace.project.id}&level=System`,
  )).json()
  const sectionId = (sections as { id: string }[])[0]?.id
  expect(sectionId).toBeTruthy()

  // One controlled change request introduces 520 System requirements, each with its own exact governed
  // revision once materialized. The old Modify/Retire projection took the first 500 rows.
  const requirementChanges = Array.from({ length: 520 }, (_, index) => ({
    level: 'System', kind: 'Introduce',
    statement: `The modify volume product shall satisfy requirement ${index + 1}.`,
    rationale: 'Modify picker volume.',
    verificationMethod: 'Test',
    impactDispositionJson: impacts,
    targetSectionId: sectionId,
  }))
  const created = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Modify volume change ${suffix}`,
    problem: 'Modify targets must remain reachable beyond 500 rows.',
    analysis: 'The picker must search and page the full carried universe.',
    solution: 'Carry 520 procedures and select the one at deterministic position 520.',
    requirementChanges,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }],
    mode: 'Sequential',
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved for modify-picker volume verification.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: 'SW-01.00', revision: 0, projectId: workspace.project.id,
    releaseId: workspace.release.id, predecessorBaselineId: null,
    name: 'Modify volume baseline',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const [path, data] of [
    ['selections', { changeRequestId: draft.id }],
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy()
  }

  // Conclude the assessment and create the controlled package through the UI, as an engineer would.
  await login(page, 'test.engineer', { openProject: false })
  await selectProgram(page, `Modify Volume ${suffix}`)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Downstream Assessments' }).click()
  const row = page.locator('.downstreamAssessment').filter({ hasText: draft.displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })

  // Propose one Introduce per governed requirement through the API (each names its exact requirement,
  // satisfying #413), then carry all 520 into the build's procedure manifest.
  const impactItems = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/verification-impact`,
  )).json()
  const reviewId = (impactItems as { testChangeReviewId: string }[])[0].testChangeReviewId
  const items = (impactItems as { subjectDisplayNumber: string; requirementRevisionId?: string }[])
    .filter(item => item.requirementRevisionId)
  expect(items).toHaveLength(520)
  for (let index = 0; index < items.length; index++) {
    const item = items[index]
    const proposed = await page.request.post(
      `${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`, { data: {
        kind: 'Introduce', revision: 0, title: `Volume procedure ${index + 1}`,
        objective: `Verify modify volume requirement ${index + 1}.`,
        preconditions: 'The volume configuration is available.',
        steps: '1. Load. 2. Exercise.',
        expectedResult: 'The expected behavior is observed.',
        rationale: `The governed requirement ${item.subjectDisplayNumber} needs exact verification.`,
        drivingRequirementRevisionIds: [item.requirementRevisionId],
      } })
    expect(proposed.ok(), `proposal ${index + 1}: ${await proposed.text()}`).toBeTruthy()
  }

  // The package must be complete and approved before a baseline can carry it: every impact item resolved,
  // the engineering case recorded, submitted and approved.
  const itemIds = impactItems as { id: string; requirementRevisionId?: string }[]
  for (const item of items) {
    const resolvedItem = itemIds.find(entry => entry.requirementRevisionId === item.requirementRevisionId)
    expect(resolvedItem).toBeTruthy()
    const resolved = await page.request.post(
      `${apiBase}/api/verification-impact/${resolvedItem!.id}/resolve`, {
        data: { outcome: 'NewProcedureRequired', rationale: `A volume procedure will verify ${item.subjectDisplayNumber}.` },
      })
    expect(resolved.ok(), await resolved.text()).toBeTruthy()
  }
  const initialPayload = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`,
  )).json() as { version: number }
  const caseSaved = await page.request.post(`${apiBase}/api/test-change-reviews/${reviewId}/case`, { data: {
    title: `Modify volume package ${suffix}`,
    problem: 'The volume build needs exact procedure coverage.',
    analysis: '520 governed procedures must be carried and independently modifiable.',
    solution: 'Approve the exact volume procedure set so the build can carry it.',
    expectedVersion: initialPayload.version,
  } })
  expect(caseSaved.ok(), await caseSaved.text()).toBeTruthy()
  const afterCase = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`,
  )).json() as { version: number }
  const submittedPackage = await page.request.post(
    `${apiBase}/api/test-change-reviews/${reviewId}/submit`, {
      data: {
        approverId: 'admin',
        expectedVersion: afterCase.version,
      },
    })
  expect(submittedPackage.ok(), await submittedPackage.text()).toBeTruthy()
  const approvedPackage = await request.post(
    `${apiBase}/api/test-change-reviews/${reviewId}/approve`, {
      data: {
        rationale: 'All 520 volume procedures are governed and complete.',
        password: 'AeroLink!2026',
        meaning: 'Approve the exact volume procedure set.',
      },
    })
  expect(approvedPackage.ok(), await approvedPackage.text()).toBeTruthy()

  const selectedTcr = await request.post(`${apiBase}/api/baselines/${baseline.id}/test-change-requests`, {
    data: { testChangeRequestId: reviewId },
  })
  expect(selectedTcr.ok(), await selectedTcr.text()).toBeTruthy()
  const materialized = await request.post(
    `${apiBase}/api/baselines/${baseline.id}/materialize-test-procedures`, { data: {}, timeout: 180_000 })
  const materializedBody = await materialized.text()
  expect(materialized.ok(), materializedBody).toBeTruthy()
  const result = JSON.parse(materializedBody) as { activeProcedureCount?: number; createdRevisionCount?: number }
  expect(result.activeProcedureCount ?? result.createdRevisionCount).toBe(520)

  // Position 520 under the server's deterministic ordering is the last row of the third 200-row page.
  const page3 = await (await request.get(
    `${apiBase}/api/test-change-reviews/${reviewId}/procedure-targets?page=3&pageSize=200`,
  )).json() as { totalCount: number; items: { baseNumber: string }[] }
  expect(page3.totalCount).toBe(520)
  const targetNumber = page3.items.at(-1)!.baseNumber
  expect(targetNumber).toMatch(/^SYSTP-\d{6}$/)

  // The Modify journey needs an Open package that has not itself created every procedure, so it opens
  // against the build that already carries 520 procedures. A second approved change request provides that
  // package: one requirement, no procedure decisions yet.
  const triggerResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Modify volume trigger ${suffix}`,
    problem: 'A second package opens the Modify picker over the 520-procedure build.',
    analysis: 'The picker must expose every carried target, not only the first page.',
    solution: 'Author one Modify decision against the procedure at deterministic position 520.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: sectionId,
      statement: `The modify volume trigger requirement ${suffix} is introduced for the second package.`,
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

  // The real browser picker: the first bounded page must not contain the >500 target; searching finds it,
  // and the exact carried revision is what gets proposed.
  await page.reload()
  const reopenedRow = page.locator('.downstreamAssessment').filter({ hasText: trigger.displayNumber }).first()
  await expect(reopenedRow).toBeVisible({ timeout: 30_000 })
  await reopenedRow.getByRole('button', { name: 'Open assessment' }).click()
  const assessment2 = page.getByRole('dialog', { name: /test impact/ })
  await assessment2.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment2).toContainText('SYSTCR Created', { timeout: 30_000 })
  await assessment2.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}$/ }).click()
  const workspaceDrawer = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(workspaceDrawer).toBeVisible({ timeout: 30_000 })
  await expect(workspaceDrawer.getByRole('button', { name: 'Propose a procedure change' }))
    .toBeVisible({ timeout: 60_000 })
  await workspaceDrawer.getByRole('button', { name: 'Propose a procedure change' }).click()
  const dialog = page.getByRole('dialog', { name: 'Propose a procedure change' })
  await dialog.getByLabel('What is being done').selectOption('Modify')
  await expect(dialog).toContainText('520 carried procedures in this build', { timeout: 30_000 })
  const firstPageOptions = dialog.locator('select[aria-label="Procedure"] option')
  await expect(firstPageOptions).toHaveCount(51, { timeout: 30_000 })
  await expect(firstPageOptions.filter({ hasText: targetNumber })).toHaveCount(0)
  await dialog.getByRole('textbox', { name: 'Search procedures' }).fill(targetNumber)
  const targetOption = dialog.locator('select[aria-label="Procedure"] option').filter({ hasText: targetNumber })
  await expect(targetOption).toHaveCount(1, { timeout: 30_000 })
  await dialog.getByRole('combobox', { name: 'Procedure' }).selectOption(targetNumber)
  await dialog.getByLabel('Title').fill(`Volume modify ${suffix}`)
  await dialog.getByLabel('Objective').fill('Verify the carried volume procedure behavior.')
  await dialog.getByLabel('Steps').fill('Execute the controlled steps.')
  await dialog.getByLabel('Expected result').fill('The expected behavior is observed.')
  await dialog.getByLabel('Why this procedure work is required').fill('The approved change requires an exact procedure update.')
  await dialog.getByRole('button', { name: 'Propose decision' }).click()
  await expect(dialog).toHaveCount(0, { timeout: 30_000 })

  // Reload and reopen: the exact controlled procedure at position >500 is hydrated from persisted state.
  await page.reload()
  const afterReload = page.locator('.downstreamAssessment').filter({ hasText: trigger.displayNumber }).first()
  await expect(afterReload).toBeVisible({ timeout: 30_000 })
  await afterReload.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}/ }).click()
  const reopened = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(reopened).toContainText(`${targetNumber}.01`, { timeout: 30_000 })
})
