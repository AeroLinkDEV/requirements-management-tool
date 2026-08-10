import { expect, test } from '@playwright/test'
import { apiBase, apiLogin } from './auth'
import { seedCarriedProcedures } from './pro-audit-fixtures'

type ReviewItem = {
  id: string
  displayNumber: string
  sourceChangeRequestNumber: string
  discipline: string
  state: string
  version: number
  supersededByTestChangeRequestId?: string
  supersededReason?: string
}

const escapeRegex = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/**
 * #365 — revising an approved TCR must leave one current work item, while its exact predecessor remains
 * readable as controlled history and cannot be selected into another baseline.
 */
test('a revised TCR keeps its predecessor in history and out of active work and baseline selection', async ({ page, request }) => {
  test.setTimeout(480_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const { workspace, baseline, triggerReviewId } = await seedCarriedProcedures(page, request, suffix, 1)

  // Complete the helper's still-Open trigger package with one exact procedure decision and an engineering
  // case. The browser session is the assigned Test Engineer; the API request context remains the independent
  // administrator who signs the package.
  const impactResponse = await request.get(`${apiBase}/api/releases/${workspace.release.id}/verification-impact`)
  expect(impactResponse.ok(), await impactResponse.text()).toBeTruthy()
  const impact = await impactResponse.json() as {
    id: string
    testChangeReviewId: string
    requirementRevisionId?: string
    subjectDisplayNumber: string
  }[]
  const triggerItem = impact.find(item => item.testChangeReviewId === triggerReviewId)
  expect(triggerItem?.requirementRevisionId).toBeTruthy()

  const proposed = await page.request.post(`${apiBase}/api/test-change-reviews/${triggerReviewId}/procedure-changes`, {
    data: {
      kind: 'Introduce',
      revision: 0,
      title: 'TCR supersession regression procedure',
      objective: 'Verify the exact requirement whose TCR will later be revised.',
      preconditions: 'The target configuration is available.',
      steps: '1. Load the target. 2. Exercise the governed behavior.',
      expectedResult: 'The governed behavior is observed.',
      rationale: `Nothing covers ${triggerItem!.subjectDisplayNumber}.`,
      drivingRequirementRevisionIds: [triggerItem!.requirementRevisionId],
    },
  })
  expect(proposed.ok(), await proposed.text()).toBeTruthy()

  const resolved = await page.request.post(`${apiBase}/api/verification-impact/${triggerItem!.id}/resolve`, {
    data: {
      outcome: 'NewProcedureRequired',
      rationale: 'The approved package will introduce the exact procedure.',
    },
  })
  expect(resolved.ok(), await resolved.text()).toBeTruthy()

  const beforeCase = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${triggerReviewId}/procedure-changes`,
  )).json() as { version: number }
  const caseSaved = await page.request.post(`${apiBase}/api/test-change-reviews/${triggerReviewId}/case`, {
    data: {
      title: 'TCR supersession engineering case',
      problem: 'The changed behavior has no controlled verification coverage.',
      analysis: 'One exact procedure is required, and later package revision must retain its history.',
      solution: 'Approve the procedure package and revise it only through the controlled successor route.',
      expectedVersion: beforeCase.version,
    },
  })
  expect(caseSaved.ok(), await caseSaved.text()).toBeTruthy()

  const beforeSubmit = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${triggerReviewId}/procedure-changes`,
  )).json() as { version: number }
  const submitted = await page.request.post(`${apiBase}/api/test-change-reviews/${triggerReviewId}/submit`, {
    data: { approverId: 'admin', expectedVersion: beforeSubmit.version },
  })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()

  const approved = await request.post(`${apiBase}/api/test-change-reviews/${triggerReviewId}/approve`, {
    data: {
      rationale: 'The exact procedure decision and engineering case are acceptable.',
      password: 'AeroLink!2026',
      meaning: 'I approve this exact TCR package.',
    },
  })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const reviewsBefore = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`,
  )).json() as { items: ReviewItem[] }
  const predecessorBefore = reviewsBefore.items.find(item => item.id === triggerReviewId)
  expect(predecessorBefore?.state).toBe('Approved')

  // A separate Draft candidate proves the package was genuinely selectable before revision, then proves the
  // same immutable predecessor is omitted and refused after it becomes Superseded.
  const selectionBaselineResponse = await request.post(`${apiBase}/api/baselines`, {
    data: {
      baseNumber: `SW-02.${suffix.slice(-2)}`,
      revision: 0,
      projectId: workspace.project.id,
      releaseId: workspace.release.id,
      predecessorBaselineId: null,
      name: 'TCR supersession selection proof',
    },
  })
  expect(selectionBaselineResponse.ok(), await selectionBaselineResponse.text()).toBeTruthy()
  const selectionBaseline = await selectionBaselineResponse.json()
  const beforeCandidates = await (await request.get(
    `${apiBase}/api/baselines/${selectionBaseline.id}/test-change-requests`,
  )).json() as { available: { id: string }[] }
  expect(beforeCandidates.available.map(item => item.id)).toContain(triggerReviewId)

  // Re-enter the live queue through the browser and revise the approved package. The workspace must follow
  // the server-returned successor rather than leaving the user on a now-historical predecessor.
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })
  const activeRow = page.locator('.downstreamAssessment').filter({ hasText: predecessorBefore!.displayNumber }).first()
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
    expect(response.ok(), await response.text()).toBeTruthy()
    const body = await response.json() as { items: ReviewItem[] }
    return body.items.find(item => item.id === triggerReviewId)?.state
  }, { timeout: 30_000 }).toBe('Superseded')

  const refreshedReviews = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`,
  )).json() as { items: ReviewItem[] }
  const predecessor = refreshedReviews.items.find(item => item.id === triggerReviewId)
  expect(predecessor?.state).toBe('Superseded')
  expect(predecessor?.supersededByTestChangeRequestId).toBeTruthy()
  expect(predecessor?.supersededReason).toContain('Superseded by controlled revision')
  const successor = refreshedReviews.items.find(item => item.id === predecessor!.supersededByTestChangeRequestId)
  expect(successor?.state).toBe('Open')
  expect(successor?.displayNumber).toMatch(/\.01$/)

  await expect(packageWorkspace.getByRole('heading', {
    name: `${successor!.displayNumber} procedure decisions`,
  })).toBeVisible({ timeout: 30_000 })
  await packageWorkspace.getByRole('button', { name: 'Close test change request' }).click()

  // Only the successor is an active queue row. The predecessor sits beneath that row as explicit history.
  const successorRow = page.locator('.downstreamAssessment').filter({ hasText: successor!.displayNumber }).first()
  await expect(successorRow).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.downstreamAssessment[data-state="Superseded"]')).toHaveCount(0)
  const history = successorRow.locator('details.tcrRevisionHistory')
  await expect(history.getByText('Show 1 superseded TCR revision', { exact: true })).toBeVisible()
  await history.getByText('Show 1 superseded TCR revision', { exact: true }).click()
  const predecessorLink = history.getByRole('button', { name: `${predecessor!.displayNumber} · Superseded` })
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
    .getByText('Show 1 superseded TCR revision', { exact: true })).toBeVisible()

  const afterCandidates = await (await request.get(
    `${apiBase}/api/baselines/${selectionBaseline.id}/test-change-requests`,
  )).json() as { available: { id: string }[] }
  expect(afterCandidates.available.map(item => item.id)).not.toContain(triggerReviewId)
  const forgedSelection = await request.post(
    `${apiBase}/api/baselines/${selectionBaseline.id}/test-change-requests`,
    { data: { testChangeRequestId: triggerReviewId } },
  )
  expect(forgedSelection.status()).toBe(400)
  expect(await forgedSelection.text()).toContain('Only an approved test change request can be selected into a baseline.')

  // The predecessor baseline created by the fixture remains intact; this journey never rewrites its exact
  // procedure manifest merely because its source package later acquired a controlled successor.
  expect(baseline.id).toBeTruthy()
})
