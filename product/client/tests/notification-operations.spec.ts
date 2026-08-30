import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

test('global administrator can inspect bounded SMTP delivery operations without rendering message content', async ({ page }) => {
  let notificationOperationReads = 0
  page.on('request', request => {
    if (request.method() === 'GET' && new URL(request.url()).pathname === '/api/operations/notifications') {
      notificationOperationReads++
    }
  })
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ADMINISTRATION')
  await page.getByRole('link', { name: 'System Operations' }).click()
  await expect(page.getByRole('button', { name: 'Notifications' })).toBeVisible()
  expect(notificationOperationReads).toBe(0)
  await page.getByRole('button', { name: 'Notifications' }).click()

  await expect.poll(() => notificationOperationReads).toBeGreaterThan(0)
  await expect(page.getByText('INSTALLATION OPERATIONS / EMAIL OUTBOX')).toBeVisible()
  await expect(page.getByRole('heading', { name: /SMTP transport/ })).toBeVisible()
  await expect(page.getByText('Recent delivery state')).toBeVisible()
  await expect(page.getByText(/Credentials, mail bodies, and unredacted recipient addresses are never rendered here/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Send my transport test' })).toBeVisible()
})
