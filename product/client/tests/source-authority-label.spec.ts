import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

/**
 * The source authority names the record it opens.
 *
 * HLR and LLR changes only ever come from an HLRCR or an LLRCR, so a fixed label was wrong every time
 * inspector showed a software requirement. The label now follows the controlled identifier of the change
 * request rather than the workspace the reader happens to be in — which is why both disciplines are checked
 * in one run: a label that merely echoed the current scope would satisfy either half alone.
 */
test('the requirement inspector names its source authority by change-request type', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'REQUIREMENTS')
  await page.getByRole('link', { name: 'Requirements Explorer' }).click()

  // System first: a System requirement is changed by an SRCR.
  await page.getByRole('button', { name: 'System', exact: true }).first().click()
  await page.getByLabel('Search requirements').fill('SYSR-000150')
  await page.getByText(/SYSR-000150\.\d{2}/).first().click()
  await expect(page.getByRole('button', { name: /^Open SRCR/ })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: /^Open HLRCR/ })).toHaveCount(0)

  // Then software: an HLR is changed by an HLRCR, and must say so.
  await page.getByRole('button', { name: 'Software', exact: true }).first().click()
  await page.getByLabel('Search requirements').fill('HLR-000001')
  await page.getByText(/HLR-000001\.\d{2}/).first().click()
  await expect(page.getByRole('button', { name: /^Open HLRCR/ })).toBeVisible({ timeout: 30_000 })
})
