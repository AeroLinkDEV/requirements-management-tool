import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, selectProgram, showcaseSeed } from './auth'

/**
 * Stopping a review that should not be running.
 *
 * There was no way to do it. `RequestChanges` returned a change request to Draft but only the reviewer whose
 * turn it was could use it, so an author who submitted too early had to ask that reviewer to formally reject
 * work everybody already knew was going to change.
 */
test('an author stops a review they should not have started, and the history says why', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  const created = await request.post(`${apiBase}/api/scr-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    type: 'System',
    title: `Cancelled review ${Date.now()}`,
    problem: 'A controlled change is needed.',
    analysis: 'The downstream effect has been assessed.',
    solution: 'Introduce the behaviour under change control.',
    requirementChanges: [{
      level: 'System',
      kind: 'Introduce',
      targetSectionId: await firstSectionId(request, showcase.projectId),
      statement: 'The FMS shall allow a review to be stopped.',
      rationale: 'Review control.',
      verificationMethod: 'Test',
    }],
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    mode: 'Sequential',
    approvers: [{ userId: 'lead.reviewer', name: 'Maya Patel' }],
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/systems/change-requests/${draft.id}`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.locator('[data-state="InReview"]').first()).toBeVisible({ timeout: 30_000 })

  // Asked twice: once to confirm the intent, once for the reason. A review in flight has people's attention
  // booked against it, and a misplaced click beside the review status should not unwind somebody's queue.
  const prompts: string[] = []
  page.on('dialog', async (dialog) => {
    prompts.push(dialog.message())
    await (dialog.type() === 'prompt' ? dialog.accept('Submitted before the analysis was finished.') : dialog.accept())
  })

  await page.getByRole('button', { name: 'Cancel review' }).click()
  await expect(page.locator('[data-state="Draft"]').first()).toBeVisible({ timeout: 30_000 })
  expect(prompts).toHaveLength(2)
  expect(prompts[0]).toContain('return it to Draft')

  // The reason is the message to whoever looks at this next, so it has to survive into the record.
  const history = page.getByRole('heading', { name: 'Audit history' }).locator('../../..')
  await expect(history).toContainText('Submitted before the analysis was finished.')
  // Its own event: cancelling and requesting changes both land in Draft, and they are not the same decision.
  await expect(history.locator('.auditRow b').filter({ hasText: /^Review cancelled$/ })).toBeVisible()
  await expect(history.locator('.auditRow b').filter({ hasText: 'Changes requested' })).toHaveCount(0)

  // Back in Draft at the same revision, and submittable again — the work continues.
  await expect(page.getByRole('button', { name: 'Configure & Submit Review' })).toBeVisible({ timeout: 30_000 })
})

test('somebody with no part in a review is not offered the control', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  const created = await request.post(`${apiBase}/api/scr-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    type: 'System',
    title: `Bystander review ${Date.now()}`,
    problem: 'A controlled change is needed.',
    analysis: 'The downstream effect has been assessed.',
    solution: 'Introduce the behaviour under change control.',
    requirementChanges: [{
      level: 'System',
      kind: 'Introduce',
      targetSectionId: await firstSectionId(request, showcase.projectId),
      statement: 'The FMS shall scope who may stop a review.',
      rationale: 'Review control.',
      verificationMethod: 'Test',
    }],
  } })
  const draft = await created.json()
  await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    mode: 'Sequential',
    approvers: [{ userId: 'lead.reviewer', name: 'Maya Patel' }],
  } })

  // An engineer in the Program who neither wrote this nor is being waited on. "Anyone can cancel" would let
  // them halt a review they have nothing to do with.
  await login(page, 'systems.author', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/systems/change-requests/${draft.id}`, page.url()).toString(), { waitUntil: 'load' })

  await expect(page.locator('[data-state="InReview"]').first()).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: 'Cancel review' })).toHaveCount(0)
})
