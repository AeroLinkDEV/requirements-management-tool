import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

test('a test engineer claims one whole test change request and finds it in My Work after refresh', async ({ page }) => {
  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()

  const claimable = page.locator('.coverageRow').filter({ has: page.getByRole('button', { name: 'Take it on' }) }).first()
  await expect(claimable).toBeVisible({ timeout: 30_000 })
  const displayNumber = (await claimable.locator('b').first().textContent())!.trim()
  await claimable.getByRole('button', { name: 'Take it on' }).click()
  const assigned = page.locator('.coverageRow').filter({ hasText: displayNumber }).first()
  await expect(assigned).toContainText('Ethan Brooks')

  await page.reload()
  const persisted = page.locator('.coverageRow').filter({ hasText: displayNumber }).first()
  await expect(persisted).toContainText('Ethan Brooks', { timeout: 30_000 })
  await expect(persisted.getByRole('button', { name: 'Take it on' })).toHaveCount(0)

  await page.getByRole('link', { name: 'My Work' }).click()
  await expect(page.getByRole('heading', { name: 'My Work' })).toBeVisible()
  await expect(page.locator('.workQueue').getByText(displayNumber)).toBeVisible()
})
