import { expect, test } from '@playwright/test'
import { login } from './auth'

test('managed Word documents keep exact revision evidence isolated across active and released builds', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page)

  await page.getByRole('link', { name: 'Documentation Center' }).click()
  await expect(page.getByRole('heading', { name: 'Documentation Center' })).toBeVisible()
  await expect(page.getByText('7 matching records')).toBeVisible()
  await expect(page.locator('.mdMetrics').getByText('4', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: /SDP SDP-000001/ }).click()
  await expect(page).toHaveURL(/documentation-center\/[0-9a-f-]+$/)
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'FMS Software Development Plan' })).toBeVisible()
  await expect(page.locator('.mdIdentity').getByText('Draft SDP-000001.01 · Draft', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Review & release' }).click()
  await expect(page.getByRole('heading', { name: 'Electronic signatures for SDP-000001.01' })).toBeVisible()
  await expect(page.getByText('No signatures are recorded for this exact revision.')).toBeVisible()

  await page.getByRole('button', { name: '← Back to Software Builds' }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await page.getByRole('link', { name: 'Documentation Center' }).click()
  await expect(page.getByText('Build 1.5 · historical read-only')).toBeVisible()
  await expect(page.getByText('7 matching records')).toBeVisible()
  await expect(page.locator('.mdMetrics').getByText('0', { exact: true })).toHaveCount(3)
  await expect(page.getByRole('button', { name: '+ New document' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Open in Word' })).toHaveCount(0)
  await expect(page.getByText(/\.01 · (Draft|In Review|Returned)/)).toHaveCount(0)
})
