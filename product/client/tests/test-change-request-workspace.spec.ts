import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, showcaseSeed } from './auth'

const completeImpacts = JSON.stringify({
  trace: 'Not Affected',
  verification: 'Not Affected',
  documents: 'Not Affected',
  baseline: 'Not Affected',
  collaboration: 'Not Affected',
})

/**
 * Creating a test procedure through the test change request that governs it.
 *
 * This is the test-side twin of authoring a requirement change inside a change request, and until now the
 * product had no room for it: a package could be raised, assessed and approved, but never say what procedure
 * work it actually proposed.
 *
 * The journey raises its own change request rather than claiming a seeded one. Claiming takes a package out
 * of a pool the other testing journeys draw from, and that pool has no spare — two of them failed when this
 * took one. Approving a change request is also how a test change request comes to exist in the first place,
 * so building the subject here is the honest setup rather than a workaround.
 */
test('a test engineer proposes a new procedure inside the test change request that governs it', async ({ page, request, playwright }) => {
  test.setTimeout(120_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const author = await playwright.request.newContext()
  await apiLogin(author, 'systems.author')

  const title = 'Oceanic sequencing for test change request authoring'
  const created = await author.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    type: 'System',
    title,
    problem: 'Oceanic waypoint sequencing is not represented.',
    analysis: 'The verification discipline must answer for the new behaviour.',
    solution: 'Introduce the requirement and let the test discipline assess it.',
    requirementChanges: [{
      level: 'System',
      kind: 'Introduce',
      targetSectionId: await firstSectionId(author, showcase.projectId, 'System'),
      statement: 'The FMS shall sequence oceanic waypoints in the order the active flight plan holds.',
      rationale: 'New capability.',
      verificationMethod: 'Test',
      impactDispositionJson: completeImpacts,
    }],
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()

  const submitted = await author.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    approvers: [{ userId: 'admin', name: 'Caller supplied name ignored' }],
    mode: 'Sequential',
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  // Approval is what raises the test assessment, so the package this journey authors into exists only from
  // here on.
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved so the verification discipline can assess it.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()
  const sourceNumber = draft.displayNumber as string

  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()

  const row = page.locator('.downstreamAssessment').filter({ hasText: sourceNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  // Claiming and concluding happen inside the assessment; the row offers one control in every state.
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  // Exact, because "SYSTCR required" is a substring of the button beside it that concludes the opposite.
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })

  // The package opens in its own workspace, as a change request does from the requirements drawer.
  await assessment.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}$/ }).click()
  const drawer = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(drawer).toBeVisible()
  // A package that has concluded test work is required but names none is unfinished, and says so rather than
  // rendering an empty list that reads as "nothing to do".
  await expect(drawer).toContainText('No procedure decisions are proposed yet')

  await drawer.getByRole('button', { name: 'Propose a procedure change' }).click()
  const dialog = page.getByRole('dialog', { name: 'Propose a procedure change' })

  // The requirements a procedure verifies are chosen here, not left empty — without them the procedure
  // revision cannot be bound to what caused it.
  await expect(dialog.getByRole('group', { name: 'Requirements this procedure verifies' })).toBeVisible()
  // Introducing allocates the number centrally, so there is deliberately nowhere to type one.
  await expect(dialog.getByRole('combobox', { name: 'Procedure' })).toHaveCount(0)
  await dialog.getByLabel('What is being done').selectOption('Retire')
  // A retirement withdraws a procedure rather than restating it, so no body is asked for — but which
  // procedure is being retired is not optional.
  await expect(dialog.getByRole('combobox', { name: 'Procedure' })).toBeVisible()
  await expect(dialog.getByLabel('Steps')).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Propose decision' })).toBeDisabled()
  await dialog.getByLabel('What is being done').selectOption('Introduce')

  await dialog.getByLabel('Title').fill('Oceanic waypoint sequencing')
  await dialog.getByLabel('Objective').fill('Verify oceanic waypoints sequence in flight-plan order.')
  await dialog.getByLabel('Steps').fill('1. Load the plan. 2. Advance past the first waypoint.')
  await dialog.getByLabel('Expected result').fill('The next eligible oceanic waypoint is sequenced.')
  await dialog.getByLabel('Why this procedure work is required').fill('No procedure exercises oceanic sequencing after the approved change.')
  await dialog.getByRole('button', { name: 'Propose decision' }).click()

  await expect(drawer.getByText(/SYSTP-\d{6}\.00 · New procedure/)).toBeVisible({ timeout: 30_000 })
  await expect(drawer).toContainText('Oceanic waypoint sequencing')
  await expect(drawer).toContainText('1 procedure decision proposed')

  // It is a controlled record, not drawer state: it survives leaving the page and coming back.
  await page.reload()
  const reopened = page.locator('.downstreamAssessment').filter({ hasText: sourceNumber }).first()
  await expect(reopened).toBeVisible({ timeout: 30_000 })
  // Reachable straight from the queue row, without opening the assessment first.
  await reopened.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2} · / }).click()
  const again = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(again.getByText(/SYSTP-\d{6}\.00 · New procedure/)).toBeVisible({ timeout: 30_000 })

  await again.getByRole('button', { name: 'Withdraw this decision' }).click()
  await expect(again).toContainText('No procedure decisions are proposed yet', { timeout: 30_000 })
})
