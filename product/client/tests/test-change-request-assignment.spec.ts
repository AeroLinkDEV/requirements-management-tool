import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

test('a test engineer claims one whole test change request and finds it in My Work after refresh', async ({ page }) => {
  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()

  // Claiming happens inside the assessment now. The row carries one control in every state, as the
  // requirements queue does, so the queue is read and the assessment is worked.
  const rows = page.locator('.downstreamAssessment')
  await expect(rows.first()).toBeVisible({ timeout: 30_000 })
  const claimable = rows.filter({ hasText: /SYSTCR-/ }).first()
  const sourceNumber = (await claimable.locator('b').first().textContent())!.trim()
  const displayNumber = ((await claimable.locator('.linkedScr').first().textContent()) ?? '')
    .match(/(?:SYS|HLR|LLR)TCR-\d{6}\.\d{2}/)![0]

  await claimable.getByRole('button', { name: 'Open assessment' }).click()
  const drawer = page.getByRole('dialog', { name: /test impact/ })
  await drawer.getByRole('button', { name: 'Take it on' }).click()
  await expect(drawer).toContainText('Ethan Brooks', { timeout: 30_000 })
  await drawer.getByRole('button', { name: 'Close test assessment' }).click()

  await page.reload()
  const persisted = page.locator('.downstreamAssessment').filter({ hasText: sourceNumber }).first()
  await expect(persisted).toBeVisible({ timeout: 30_000 })
  await persisted.getByRole('button', { name: 'Open assessment' }).click()
  const reopened = page.getByRole('dialog', { name: /test impact/ })
  await expect(reopened).toContainText('Ethan Brooks', { timeout: 30_000 })
  // Claimed once. It is no longer offered, because it is already held.
  await expect(reopened.getByRole('button', { name: 'Take it on' })).toHaveCount(0)
  await reopened.getByRole('button', { name: 'Close test assessment' }).click()

  await page.getByRole('link', { name: 'My Work' }).click()
  await expect(page.getByRole('heading', { name: 'My Work' })).toBeVisible()
  await expect(page.locator('.workQueue').getByText(displayNumber)).toBeVisible()
})
