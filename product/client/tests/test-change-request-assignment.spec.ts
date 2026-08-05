import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

test('a test engineer claims one whole test change request and finds it in My Work after refresh', async ({ page }) => {
  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()

  const claimable = page.locator('.coverageRow').filter({ has: page.getByRole('button', { name: 'Take it on' }) }).first()
  await expect(claimable).toBeVisible({ timeout: 30_000 })
  // A row leads with the change it assesses; the package's own controlled number sits in its detail line,
  // and that is what My Work lists it by.
  const sourceNumber = (await claimable.locator('b').first().textContent())!.trim()
  const displayNumber = ((await claimable.locator('small').first().textContent()) ?? '')
    .match(/(?:SYS|HLR|LLR)TCR-\d{6}\.\d{2}/)![0]
  await claimable.getByRole('button', { name: 'Take it on' }).click()
  const assigned = page.locator('.coverageRow').filter({ hasText: sourceNumber }).first()
  await expect(assigned).toContainText('Ethan Brooks')

  await page.reload()
  const persisted = page.locator(".coverageRow").filter({ hasText: sourceNumber }).first()
  await expect(persisted).toContainText('Ethan Brooks', { timeout: 30_000 })
  await expect(persisted.getByRole('button', { name: 'Take it on' })).toHaveCount(0)

  await page.getByRole('link', { name: 'My Work' }).click()
  await expect(page.getByRole('heading', { name: 'My Work' })).toBeVisible()
  await expect(page.locator('.workQueue').getByText(displayNumber)).toBeVisible()
})
