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
  await page.getByRole('link', { name: 'Software LLR Test Change Requests' }).click()
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
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
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
  await page.getByRole('link', { name: 'Software LLR Test Change Requests' }).click()
  const row = page.locator('.downstreamAssessment').filter({ hasText: displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  const decided = assessment.locator('.decisionList li').filter({ hasText: /LLR-000651\./ }).first()
  await decided.getByRole('button', { name: 'Decide' }).click()
  const decide = page.getByRole('dialog', { name: /Decide / })
  await decide.getByRole('textbox', { name: 'Search approved procedures' }).fill('LLRTP-000250')
  await expect(decide).toContainText('280 approved procedures in this build', { timeout: 30_000 })
  const option = decide.locator('select[name="procedureId"] option').filter({ hasText: 'LLRTP-000250' })
  await expect(option).toHaveCount(1, { timeout: 30_000 })
  const optionValue = await option.getAttribute('value')
  expect(optionValue).toMatch(/[0-9a-f-]{36}/)
  await decide.getByRole('combobox', { name: 'Covering procedure' }).selectOption(optionValue!)
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
