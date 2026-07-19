import { expect, test } from '@playwright/test'
import { apiLogin, login, openNavigationGroup } from './auth'

test('ReqIF Exchange Center exports and securely previews a governed package', async ({ page, request }, testInfo) => {
  await apiLogin(request)
  await login(page)
  await openNavigationGroup(page, 'ADMINISTRATION')
  await page.getByRole('link', { name: 'Integration Command Center' }).click()
  await expect(page.getByRole('heading', { name: 'Round-trip gateway' })).toBeVisible()
  await page.getByRole('button', { name: 'Open center' }).click()
  await expect(page.getByRole('heading', { name: 'ReqIF Exchange Center' })).toBeVisible()
  await expect(page.getByText('Stable identities', { exact: true })).toBeVisible()
  await expect(page.getByText('Attachment binaries', { exact: true })).toBeVisible()

  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Export package' }).click()
  const download = await downloadPromise
  expect(download.suggestedFilename()).toMatch(/\.reqifz$/)
  const path = testInfo.outputPath('round-trip.reqifz')
  await download.saveAs(path)
  await page.locator('input[type=file]').setInputFiles(path)
  await expect(page.getByText('IMPORT INSPECTION')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText(/requirements$/).first()).toBeVisible()
  await expect(page.getByText(/issue\(s\)/).first()).toBeVisible()
})
