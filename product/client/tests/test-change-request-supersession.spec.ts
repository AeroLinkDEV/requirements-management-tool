import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

type ReviewItem = {
  id: string
  changeRequestId: string
  displayNumber: string
  discipline: string
  state: string
  version: number
  supersededByTestChangeRequestId?: string
  supersededReason?: string
}

const impacts = JSON.stringify({
  trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
  baseline: 'Not Affected', collaboration: 'Not Affected',
})
const escapeRegex = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/**
 * #365 — revising an approved TCR must leave one current work item, while its exact predecessor remains
 * readable as controlled history and cannot be selected into the still-open candidate baseline.
 */
test('a revised TCR keeps its predecessor in history and out of active work and baseline selection', async ({ page, request }) => {
  test.setTimeout(360_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const programName = `TCR Supersession ${suffix}`

  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName,
    programCode: `TS${suffix}`,
    projectName: 'TCR Supersession Project',
    softwareProduct: 'TCR Supersession Product',
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

  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Controlled TCR revision ${suffix}`,
    problem: 'The new behavior has no controlled procedure.',
    analysis: 'A procedure must be introduced and independently approved.',
    solution: 'Create and later revise the exact TCR package.',
    requirementChanges: [{
      level: 'System',
      kind: 'Introduce',
      targetSectionId: await firstSectionId(request, workspace.project.id),
      statement: `The ${suffix} product shall expose a TCR supersession target.`,
      rationale: 'Qualification target.',
      verificationMethod: 'Test',
      impactDispositionJson: impacts,
    }],
  } })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const draft = await draftResponse.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }],
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved for the TCR supersession journey.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  // This is the one candidate baseline for the release. Its requirements are fixed but its procedure manifest
  // remains open, which is exactly the interval in which an approved TCR may be selected.
  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: `SW-97.${suffix.slice(-2)}`,
    revision: 0,
    projectId: workspace.project.id,
    releaseId: workspace.release.id,
    predecessorBaselineId: null,
    name: 'TCR supersession candidate',
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
  const requirementsResponse = await request.get(
    `${apiBase}/api/requirements?projectId=${workspace.project.id}&baselineId=${baseline.id}&scope=System&includeRetired=false&page=1&pageSize=10`,
  )
  expect(requirementsResponse.ok(), await requirementsResponse.text()).toBeTruthy()
  const requirementRevisionId = (await requirementsResponse.json()).items[0].revisionId as string
  expect(requirementRevisionId).toBeTruthy()

  await login(page, 'test.engineer', { openProject: false })
  await selectProgram(page, programName)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })

  const assessmentRow = page.locator('.downstreamAssessment').filter({ hasText: draft.displayNumber }).first()
  await expect(assessmentRow).toBeVisible({ timeout: 30_000 })
  await assessmentRow.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment.getByRole('button', { name: /SYSTCR-\d{6}\.\d{2}/ })).toBeVisible({ timeout: 30_000 })

  const reviews = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`,
  )).json() as { items: ReviewItem[] }
  const initialReview = reviews.items.find(item => item.discipline === 'System' && item.changeRequestId === draft.id)
  expect(initialReview).toBeTruthy()

  const impactResponse = await request.get(`${apiBase}/api/releases/${workspace.release.id}/verification-impact`)
  expect(impactResponse.ok(), await impactResponse.text()).toBeTruthy()
  const impact = await impactResponse.json() as {
    id: string
    testChangeReviewId: string
    requirementRevisionId?: string
    subjectDisplayNumber: string
  }[]
  const impactItem = impact.find(item => item.testChangeReviewId === initialReview!.id)
  expect(impactItem?.requirementRevisionId).toBe(requirementRevisionId)

  const proposed = await page.request.post(`${apiBase}/api/test-change-reviews/${initialReview!.id}/procedure-changes`, {
    data: {
      kind: 'Introduce',
      revision: 0,
      title: 'TCR supersession regression procedure',
      objective: 'Verify the exact requirement whose TCR will later be revised.',
      preconditions: 'The target configuration is available.',
      steps: '1. Load the target. 2. Exercise the governed behavior.',
      expectedResult: 'The governed behavior is observed.',
      rationale: `Nothing covers ${impactItem!.subjectDisplayNumber}.`,
      drivingRequirementRevisionIds: [requirementRevisionId],
    },
  })
  expect(proposed.ok(), await proposed.text()).toBeTruthy()
  const resolved = await page.request.post(`${apiBase}/api/verification-impact/${impactItem!.id}/resolve`, { data: {
    outcome: 'NewProcedureRequired',
    rationale: 'The approved package will introduce the exact procedure.',
  } })
  expect(resolved.ok(), await resolved.text()).toBeTruthy()

  const beforeCase = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${initialReview!.id}/procedure-changes`,
  )).json() as { version: number }
  const caseSaved = await page.request.post(`${apiBase}/api/test-change-reviews/${initialReview!.id}/case`, { data: {
    title: 'TCR supersession engineering case',
    problem: 'The changed behavior has no controlled verification coverage.',
    analysis: 'One exact procedure is required, and later package revision must retain its history.',
    solution: 'Approve the procedure package and revise it only through the controlled successor route.',
    expectedVersion: beforeCase.version,
  } })
  expect(caseSaved.ok(), await caseSaved.text()).toBeTruthy()
  const beforeSubmit = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${initialReview!.id}/procedure-changes`,
  )).json() as { version: number }
  const packageSubmitted = await page.request.post(`${apiBase}/api/test-change-reviews/${initialReview!.id}/submit`, { data: {
    approverId: 'admin',
    expectedVersion: beforeSubmit.version,
  } })
  expect(packageSubmitted.ok(), await packageSubmitted.text()).toBeTruthy()
  const packageApproved = await request.post(`${apiBase}/api/test-change-reviews/${initialReview!.id}/approve`, { data: {
    rationale: 'The exact procedure decision and engineering case are acceptable.',
    password: 'AeroLink!2026',
    meaning: 'I approve this exact TCR package.',
  } })
  expect(packageApproved.ok(), await packageApproved.text()).toBeTruthy()

  const predecessorBefore = (await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`,
  )).json() as { items: ReviewItem[] }).items.find(item => item.id === initialReview!.id)
  expect(predecessorBefore?.state).toBe('Approved')
  const beforeCandidates = await (await request.get(
    `${apiBase}/api/baselines/${baseline.id}/test-change-requests`,
  )).json() as { available: { id: string }[] }
  expect(beforeCandidates.available.map(item => item.id)).toContain(initialReview!.id)

  // Re-enter the live queue and revise the approved package. The workspace follows the exact server-returned
  // successor rather than leaving the user on a package that became historical during the click.
  await page.reload()
  const activeRow = page.locator('.downstreamAssessment').filter({ hasText: draft.displayNumber }).first()
  await expect(activeRow).toBeVisible({ timeout: 30_000 })
  await activeRow.getByRole('button', {
    name: new RegExp(`^${escapeRegex(predecessorBefore!.displayNumber)}`),
  }).click()
  const packageWorkspace = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(packageWorkspace.getByRole('heading', {
    name: `${predecessorBefore!.displayNumber} procedure decisions`,
  })).toBeVisible()
  await packageWorkspace.getByRole('button', { name: 'Revise this test change request' }).click()

  await expect.poll(async () => {
    const response = await request.get(`${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`)
    const body = await response.json() as { items: ReviewItem[] }
    return body.items.find(item => item.id === initialReview!.id)?.state
  }, { timeout: 30_000 }).toBe('Superseded')

  const refreshedReviews = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`,
  )).json() as { items: ReviewItem[] }
  const predecessor = refreshedReviews.items.find(item => item.id === initialReview!.id)
  expect(predecessor?.supersededByTestChangeRequestId).toBeTruthy()
  expect(predecessor?.supersededReason).toContain('Superseded by controlled revision')
  const successor = refreshedReviews.items.find(item => item.id === predecessor!.supersededByTestChangeRequestId)
  expect(successor?.state).toBe('Draft')
  expect(successor?.displayNumber).toMatch(/\.01$/)

  await expect(packageWorkspace.getByRole('heading', {
    name: `${successor!.displayNumber} procedure decisions`,
  })).toBeVisible({ timeout: 30_000 })
  await packageWorkspace.getByRole('button', { name: 'Close test change request' }).click()

  const successorRow = page.locator('.downstreamAssessment').filter({ hasText: successor!.displayNumber }).first()
  await expect(successorRow).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.downstreamAssessment[data-state="Superseded"]')).toHaveCount(0)
  const history = successorRow.locator('details.tcrRevisionHistory')
  await expect(history.getByText('Show 1 superseded history item', { exact: true })).toBeVisible()
  await history.getByText('Show 1 superseded history item', { exact: true }).click()
  const predecessorLink = history.getByRole('button', { name: `${predecessor!.displayNumber} · Superseded TCR` })
  await expect(predecessorLink).toBeVisible()
  await predecessorLink.click()

  const historicalWorkspace = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(historicalWorkspace.getByRole('heading', {
    name: `${predecessor!.displayNumber} procedure decisions`,
  })).toBeVisible()
  await expect(historicalWorkspace.getByText('SYSTCR Superseded', { exact: true })).toBeVisible()
  await expect(historicalWorkspace.getByText(predecessor!.supersededReason!, { exact: true })).toBeVisible()
  const successorLink = historicalWorkspace.getByRole('button', { name: `Open ${successor!.displayNumber}` })
  await expect(successorLink).toBeVisible()
  await successorLink.click()
  await expect(page.getByRole('dialog', { name: /procedure decisions/ }).getByRole('heading', {
    name: `${successor!.displayNumber} procedure decisions`,
  })).toBeVisible()
  await page.getByRole('dialog', { name: /procedure decisions/ })
    .getByRole('button', { name: 'Close test change request' }).click()

  await page.reload()
  const reloadedSuccessor = page.locator('.downstreamAssessment').filter({ hasText: successor!.displayNumber }).first()
  await expect(reloadedSuccessor).toBeVisible({ timeout: 30_000 })
  await expect(reloadedSuccessor.locator('details.tcrRevisionHistory')
    .getByText('Show 1 superseded history item', { exact: true })).toBeVisible()

  const afterCandidates = await (await request.get(
    `${apiBase}/api/baselines/${baseline.id}/test-change-requests`,
  )).json() as { available: { id: string }[] }
  expect(afterCandidates.available.map(item => item.id)).not.toContain(initialReview!.id)
  const forgedSelection = await request.post(`${apiBase}/api/baselines/${baseline.id}/test-change-requests`, {
    data: { testChangeRequestId: initialReview!.id },
  })
  expect(forgedSelection.status()).toBe(400)
  expect(await forgedSelection.text()).toContain('Only an approved test change request can be selected into a baseline.')
})
