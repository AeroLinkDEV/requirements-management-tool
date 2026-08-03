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

test('security implementation remains dormant while MFA and account-security controls are hidden', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByLabel(/MFA|authentication code/i)).toHaveCount(0)
  await expect(page.getByText('Built for Engineering Excellence')).toHaveCount(0)
  await expect(page.getByText('Systems Engineering Ready')).toHaveCount(0)
  await login(page, 'admin', { openProject: false })
  await expect(page.getByRole('button', { name: /Account security/i })).toHaveCount(0)
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByText('Ongoing enhancements and integration for the upcoming release.')).toHaveCount(0)
  await expect(page.getByText(/Informally Build/)).toHaveCount(0)
})
