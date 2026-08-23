import { expect, type APIRequestContext, type Page } from '@playwright/test'
import { apiBase, login, openNavigationGroup, selectProgram } from './auth'

export const proAuditImpacts = JSON.stringify({
  trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
  baseline: 'Not Affected', collaboration: 'Not Affected',
})

/**
 * Builds a release carrying `count` governed procedures through a verified disposable fixture: one
 * controlled change request introduces `count` requirements, its automatic TCR proposes and materializes
 * `count` procedures, and a second approved change request provides an Open trigger TCR over the same
 * build. Returns the trigger TCR id, the carried target rows, and the baseline/release/workspace ids.
 */
export async function seedCarriedProcedures(
  page: Page,
  request: APIRequestContext,
  suffix: string,
  count: number,
) {
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `ProAudit Volume ${suffix}`,
    programCode: `PAV${suffix}`,
    projectName: 'ProAudit Volume Project',
    softwareProduct: 'ProAudit Volume Product',
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
  const leadGrant = await request.post(`${apiBase}/api/admin/users/${testEngineer.id}/memberships`, { data: {
    programId: workspace.program.id, role: 'TestLead',
  } })
  expect(leadGrant.ok(), await leadGrant.text()).toBeTruthy()

  const sections = await (await request.get(
    `${apiBase}/api/authoring/sections?projectId=${workspace.project.id}&level=System`,
  )).json()
  const sectionId = (sections as { id: string }[])[0]?.id
  expect(sectionId).toBeTruthy()
  const requirementChanges = Array.from({ length: count }, (_, index) => ({
    level: 'System', kind: 'Introduce',
    statement: `The proaudit product shall satisfy requirement ${index + 1}.`,
    rationale: 'ProAudit fixture.',
    verificationMethod: 'Test',
    impactDispositionJson: proAuditImpacts,
    targetSectionId: sectionId,
  }))
  // The trigger change request is created and approved FIRST so its single requirement-number claim
  // precedes the volume batch's in-memory number range; creating it afterwards would collide with the
  // volume batch's locally assigned numbers during materialization.
  const triggerResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `ProAudit trigger ${suffix}`,
    problem: 'A second package opens authoring over the volume build.',
    analysis: 'The trigger package authorizes successor and uncarried procedure work.',
    solution: 'Approve the trigger package without selecting it into the manifest.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: sectionId,
      statement: `The proaudit trigger requirement ${suffix} is introduced for the second package.`,
      rationale: 'Creates an Open package over the already-carried build.',
      verificationMethod: 'Test',
      impactDispositionJson: proAuditImpacts,
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
    meaning: 'Approved so the second package can author exact work.',
  } })
  expect(triggerApproved.ok(), await triggerApproved.text()).toBeTruthy()

  const created = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `ProAudit volume change ${suffix}`,
    problem: 'Carry a procedure universe for exact-execution testing.',
    analysis: 'The build must carry exact controlled procedure revisions.',
    solution: 'Carry the volume procedures, then author in a second package.',
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
    meaning: 'Approved for proaudit journey verification.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: 'SW-01.00', revision: 0, projectId: workspace.project.id,
    releaseId: workspace.release.id, predecessorBaselineId: null,
    name: 'ProAudit baseline',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const changeRequestId of [draft.id, trigger.id]) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/selections`, { data: { changeRequestId } })
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
  await selectProgram(page, `ProAudit Volume ${suffix}`)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  const row = page.locator('.downstreamAssessment').filter({ hasText: draft.displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  await assessment.getByRole('button', { name: 'SYSTPCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTPCR Created', { timeout: 30_000 })

  const impactItems = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/verification-impact`,
  )).json() as { id: string; testChangeReviewId: string; subjectDisplayNumber: string; subjectStatement?: string; requirementRevisionId?: string }[]
  const items = impactItems.filter(item => item.requirementRevisionId
    && item.subjectStatement?.startsWith('The proaudit product'))
  expect(items).toHaveLength(count)
  const reviewId = items[0].testChangeReviewId
  for (let index = 0; index < items.length; index++) {
    const item = items[index]
    const proposed = await page.request.post(
      `${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`, { data: {
        kind: 'Introduce', revision: 0, title: `ProAudit procedure ${index + 1}`,
        objective: `Verify proaudit requirement ${index + 1}.`,
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
    title: `ProAudit package ${suffix}`,
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
  await assessment2.getByRole('button', { name: 'SYSTPCR required', exact: true }).click()
  await expect(assessment2).toContainText('SYSTPCR Created', { timeout: 30_000 })
  const afterTrigger = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/verification-impact`,
  )).json() as { testChangeReviewId: string; subjectStatement?: string; id: string; requirementRevisionId?: string }[]
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
  return { workspace, baseline, triggerReviewId, targets }
}
