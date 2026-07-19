import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from './auth'

test('dashboard lifecycle metrics open shareable, clearable History drill-downs', async ({ page, request }) => {
  test.setTimeout(60_000)
  await apiLogin(request)
  await login(page)
  await selectProgram(page,'Flight Management System Live Program')

  await page.getByRole('button', { name: 'Open Draft changes' }).click()
  await expect(page).toHaveURL(/[?&]state=Draft(?:&|$)/)
  await expect(page.getByLabel('Lifecycle state filter')).toHaveValue('Draft')
  await expect(page.getByText('Draft · System and software', { exact: true })).toBeVisible()
  await expect(page.locator('.historyState').first()).toHaveText('Draft')

  await page.reload()
  await expect(page.getByLabel('Lifecycle state filter')).toHaveValue('Draft')
  await page.getByLabel('Lifecycle state filter').selectOption('SelectedForBaseline')
  await expect(page).toHaveURL(/[?&]state=SelectedForBaseline(?:&|$)/)
  await expect(page.getByText('Selected for baseline · System and software', { exact: true })).toBeVisible()
  const visibleStates = await page.locator('.historyState').allTextContents()
  expect(visibleStates.every(state => state === 'SelectedForBaseline')).toBeTruthy()

  await page.getByRole('button', { name: 'Clear Selected for baseline lifecycle filter' }).click()
  await expect(page).not.toHaveURL(/[?&]state=/)
  await expect(page.getByLabel('Lifecycle state filter')).toHaveValue('')

  await page.getByRole('button', { name: /Command Center/ }).first().click()
  await page.getByRole('button', { name: 'Open Approved and baseline-selected changes' }).click()
  await expect(page.getByLabel('Lifecycle state filter')).toHaveValue('ApprovedOrSelected')
  await expect(page.getByText('Approved or selected for baseline · System and software', { exact: true })).toBeVisible()
  const approvedStates = await page.locator('.historyState').allTextContents()
  expect(approvedStates.every(state => state === 'Approved' || state === 'SelectedForBaseline')).toBeTruthy()
})
