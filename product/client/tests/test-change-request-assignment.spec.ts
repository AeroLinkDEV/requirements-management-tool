import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

/**
 * Answering an assessment is what takes it on.
 *
 * There is no claim step. An assessment nobody has answered is open to anybody with the authority, which is
 * the whole point: work does not wait on somebody pressing a button that says they intend to do it. Recording
 * an answer is what makes the package theirs, and that is what has to survive a reload and reach My Work.
 */
test('answering a test change request makes it yours, and it is in My Work after refresh', async ({ page }) => {
  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()

  // The row carries one control in every state, as the requirements queue does, so the queue is read and the
  // assessment is worked inside it.
  const rows = page.locator('.downstreamAssessment')
  await expect(rows.first()).toBeVisible({ timeout: 30_000 })
  const claimable = rows.filter({ hasText: /SYSTPCR-/ }).first()
  const sourceNumber = (await claimable.locator('b').first().textContent())!.trim()
  const displayNumber = ((await claimable.locator('.linkedScr').first().textContent()) ?? '')
    .match(/(?:SYSTPCR|HLRTCCR|LLRTCCR)-\d{6}\.\d{2}/)![0]

  await claimable.getByRole('button', { name: 'Open assessment' }).click()
  const drawer = page.getByRole('dialog', { name: /test impact/ })
  // Answering one of its decisions is the act that takes the package on. No button says so first.
  const undecided = drawer.locator('.decisionList li').filter({ has: page.getByRole('button', { name: 'Decide' }) })
  await expect(undecided.first()).toBeVisible({ timeout: 30_000 })
  await undecided.first().getByRole('button', { name: 'Decide' }).click()
  const decide = page.getByRole('dialog', { name: /Decide / })
  await expect(decide).toBeVisible({ timeout: 30_000 })
  await decide.getByLabel('Decision').selectOption('NoTestRequired')
  await decide.getByLabel('Rationale').fill('Verified by inspection against the approved design note.')
  await decide.getByRole('button', { name: 'Record decision' }).click()
  await expect(decide).toHaveCount(0, { timeout: 30_000 })

  await expect(drawer).toContainText('Ethan Brooks', { timeout: 30_000 })
  await drawer.getByRole('button', { name: 'Close test assessment' }).click()

  await page.reload()
  const persisted = page.locator('.downstreamAssessment').filter({ hasText: sourceNumber }).first()
  await expect(persisted).toBeVisible({ timeout: 30_000 })
  await persisted.getByRole('button', { name: 'Open assessment' }).click()
  const reopened = page.getByRole('dialog', { name: /test impact/ })
  await expect(reopened).toContainText('Ethan Brooks', { timeout: 30_000 })
  // And nothing anywhere offers to take it on, before or after: the step does not exist.
  await expect(reopened.getByRole('button', { name: 'Take it on' })).toHaveCount(0)
  await reopened.getByRole('button', { name: 'Close test assessment' }).click()

  await page.getByRole('link', { name: 'My Work' }).click()
  await expect(page.getByRole('heading', { name: 'My Work' })).toBeVisible()
  await expect(page.locator('.workQueue').getByText(displayNumber)).toBeVisible()
})
