import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { apiBase, apiLogin, openNavigationGroup } from './auth'
import { seedCarriedProcedures } from './pro-audit-fixtures'

/**
 * #422 — a test execution is configuration evidence. The browser must offer only the exact procedure
 * revision the selected build's controlled manifest carries, record a real execution against it, and keep
 * execution history and coverage telling the same exact revision/build story. Stray successor or uncarried
 * revisions stay absent.
 */

async function proposeAndApproveTriggerWork(
  request: APIRequestContext,
  page: Page,
  reviewId: string,
  triggerItem: { id: string; requirementRevisionId?: string },
  changes: { kind: string; baseNumber?: string; revision: number; title: string; driving: string[] }[],
) {
  for (const change of changes) {
    const proposed = await page.request.post(`${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`, { data: {
      kind: change.kind,
      baseNumber: change.baseNumber ?? null,
      revision: change.revision,
      title: change.title,
      objective: 'Verify the exact behavior.',
      preconditions: 'The configuration is available.',
      steps: '1. Load. 2. Exercise.',
      expectedResult: 'The expected behavior is observed.',
      rationale: 'Exact execution-effectivity evidence.',
      drivingRequirementRevisionIds: change.driving,
      removedRequirementRevisionIds: [],
    } })
    expect(proposed.ok(), await proposed.text()).toBeTruthy()
  }
  const resolved = await page.request.post(`${apiBase}/api/verification-impact/${triggerItem.id}/resolve`, {
    data: { outcome: 'NewProcedureRequired', rationale: 'The trigger requirement needs an exact procedure.' },
  })
  expect(resolved.ok(), await resolved.text()).toBeTruthy()
  const payload = await (await page.request.get(
    `${apiBase}/api/test-change-reviews/${reviewId}/procedure-changes`,
  )).json() as { version: number }
  const caseSaved = await page.request.post(`${apiBase}/api/test-change-reviews/${reviewId}/case`, { data: {
    title: 'Exact execution package',
    problem: 'P', analysis: 'A', solution: 'S', expectedVersion: payload.version,
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
      data: { rationale: 'Approved for execution-effectivity journey.', password: 'AeroLink!2026', meaning: 'Approve.' },
    })
  expect(approvedPackage.ok(), await approvedPackage.text()).toBeTruthy()
}

test('the execution workflow offers and records only the exact carried procedure revision', async ({ page, request }) => {
  test.setTimeout(900_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const { workspace, baseline, triggerReviewId, targets } = await seedCarriedProcedures(
    page, request, suffix, 60)
  const carried = targets[0]

  // The trigger TCR is approved but never selected, so its successor revision .01 of the carried procedure
  // and the introduced SYSTP-000999 remain approved-but-uncarried.
  const impact = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/verification-impact`,
  )).json() as { testChangeReviewId: string; subjectStatement?: string; id: string; requirementRevisionId?: string }[]
  const triggerItem = impact.find(x => x.testChangeReviewId === triggerReviewId)!
  await proposeAndApproveTriggerWork(request, page, triggerReviewId, triggerItem, [
    { kind: 'Modify', baseNumber: carried.baseNumber, revision: carried.currentRevision + 1,
      title: 'Verify route sequencing and discontinuities', driving: [] },
    { kind: 'Introduce', revision: 0, title: 'Uncarried procedure', driving: [triggerItem.requirementRevisionId!] },
  ])

  // The in-work release's exact manifest carries only the .00 revisions; .01 and SYSTP-000999 are absent.
  const effectivity = await (await request.get(
    `${apiBase}/api/test-procedures?projectId=${workspace.project.id}&releaseId=${workspace.release.id}&scope=System&page=1&pageSize=50`,
  )).json() as { items: { displayNumber: string; baseNumber: string }[] }
  expect(effectivity.items.map(x => x.displayNumber)).toContain(`${carried.baseNumber}.00`)
  expect(effectivity.items.map(x => x.displayNumber)).not.toContain(`${carried.baseNumber}.01`)
  expect(effectivity.items.map(x => x.baseNumber)).not.toContain('SYSTP-000999')

  // The real browser execution workflow: only the exact carried revision is offered.
  await page.reload()
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Results' }).click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  await page.getByLabel('Find an approved procedure').fill(carried.baseNumber)
  const candidates = page.locator('.testSetCandidates label')
  await expect(candidates).toHaveCount(1, { timeout: 30_000 })
  await expect(candidates.first()).toContainText(`${carried.baseNumber}.00`)
  await candidates.first().locator('input[type="checkbox"]').check()
  await page.getByRole('button', { name: 'Add — covers a change' }).click()
  const row = page.locator('.testSetRow').filter({ hasText: `${carried.baseNumber}.00` })
  await expect(row).toHaveCount(1, { timeout: 30_000 })

  // The successor .01 and the uncarried procedure are never offered for this build.
  await page.getByLabel('Find an approved procedure').fill(`${carried.baseNumber}.01`)
  await expect(page.locator('.testSetCandidates label')).toHaveCount(0, { timeout: 15_000 })
  await page.getByLabel('Find an approved procedure').fill('SYSTP-000999')
  await expect(page.locator('.testSetCandidates label')).toHaveCount(0, { timeout: 15_000 })

  // Record a real execution against the exact carried revision.
  await row.getByRole('button', { name: /Record result/ }).click()
  const record = page.getByRole('dialog', { name: new RegExp(`Record a result for ${carried.baseNumber}\\.00`) })
  await expect(record).toBeVisible({ timeout: 30_000 })
  await record.getByLabel('Configuration under test').fill('Execution rig 1')
  await record.getByLabel('Determination', { exact: true }).fill('Sequencing held across the transition.')
  await record.getByLabel('Evidence reference').fill('rig1/execution-route.log')
  await record.getByRole('button', { name: 'Record determination' }).click()
  await expect(record).toHaveCount(0, { timeout: 30_000 })
  await expect(row).toContainText('Pass')

  // Refresh/reopen: execution history retains the exact build, release, procedure and revision identity.
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  const reopenedRow = page.locator('.testSetRow').filter({ hasText: `${carried.baseNumber}.00` })
  await expect(reopenedRow).toHaveCount(1, { timeout: 30_000 })
  await expect(reopenedRow).toContainText('Pass')
  await reopenedRow.getByRole('button', { name: 'Runs' }).click()
  await expect(reopenedRow.locator('.runList')).toContainText('Pass', { timeout: 15_000 })

  // API truth: the exact execution row carries the release, revision and metadata; coverage agrees.
  const executions = await (await request.get(
    `${apiBase}/api/test-executions?projectId=${workspace.project.id}&releaseId=${workspace.release.id}`,
  )).json() as { procedureRevisionId: string; releaseId: string; outcome: string; configuration: string; determination: string; evidenceReference: string }[]
  expect(executions).toHaveLength(1)
  expect(executions[0].releaseId).toBe(workspace.release.id)
  expect(executions[0].procedureRevisionId).toBeTruthy()
  expect(executions[0].outcome).toBe('Pass')
  expect(executions[0].configuration).toBe('Execution rig 1')
  expect(executions[0].evidenceReference).toBe('rig1/execution-route.log')

  const coverage = await (await request.get(
    `${apiBase}/api/verification-coverage?projectId=${workspace.project.id}&baselineId=${baseline.id}`,
  )).json() as { items: { covered: boolean; coveredBy: { revisionId: string }[] }[] }
  expect(coverage.items.some(x => x.covered && x.coveredBy.some(c => c.revisionId === executions[0].procedureRevisionId)))
    .toBe(true)
})
