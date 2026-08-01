import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

test('administrators see current Program roles and retain revoke failures', async ({ page }) => {
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ADMINISTRATION')
  await page.getByRole('link', { name: 'People & Authority' }).click()
  await page.getByLabel('Search people and authority').fill('systems.author')
  const account = page.locator('.userTable article').filter({ hasText: 'systems.author' })
  await account.getByRole('button', { name: 'Manage roles' }).click()

  const currentRole = page.getByRole('button', { name: /Current · Revoke/ }).first()
  await expect(currentRole).toBeVisible()
  await expect(page.getByRole('button', { name: /^Grant / }).first()).toBeVisible()
  await page.route('**/api/admin/users/*/memberships/*/*', route => route.fulfill({
    status: 409,
    contentType: 'application/json',
    body: JSON.stringify({ error: 'The role changed in another administrator session.' }),
  }))
  await currentRole.click()
  await expect(page.getByRole('alert')).toContainText('another administrator session')
  await expect(currentRole).toBeEnabled()
})

test('account security identifies the current session and retains delegation history controls', async ({ page }) => {
  await login(page, 'admin', { openProject: false })
  await page.getByRole('button', { name: 'Account security' }).click()
  const dialog = page.getByRole('dialog', { name: 'Account security' })
  await expect(dialog.getByText('Current session')).toBeVisible()
  await expect(dialog.getByRole('heading', { name: 'Delegated authority' })).toBeVisible()
  await expect(dialog.getByText(/No delegation history|Active|Future|Expired|Revoked/).first()).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Revoke other active sessions' })).toBeVisible()
})
