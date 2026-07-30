import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from './auth'

test('System and Software dashboard metrics open shareable, build-scoped History drill-downs', async ({ page, request }) => {
  test.setTimeout(60_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page,'Flight Management System Live Program')

  await page.locator('.dashboardAreaCard.system .dashboardStateGrid button').filter({hasText:'Draft'}).click()
  await expect(page).toHaveURL(/[?&]state=Draft(?:&|$)/)
  await expect(page.getByLabel('Lifecycle state filter')).toHaveValue('Draft')
  await expect(page.locator('.historyActiveFilter b')).toHaveText('Draft')
  await expect(page.getByRole('heading', { name: 'System Change Requests' })).toBeVisible()
  await page.reload()
  await expect(page.getByLabel('Lifecycle state filter')).toHaveValue('Draft')
  await page.getByLabel('Lifecycle state filter').selectOption('SelectedForBaseline')
  await expect(page).toHaveURL(/[?&]state=SelectedForBaseline(?:&|$)/)
  // "Selected for baseline" described the mechanism, and the replacement — "Allocated to 1.6" — answered two
  // questions with one word. Which build the work is going into and how far it has got are separate facts, so
  // they are separate columns: Allocation holds the build, State holds the progress. The stored enum is
  // unchanged and still available as data-state.
  await expect(page.locator('.historyActiveFilter b')).toHaveText('Allocated to a build')

  await page.getByRole('button', { name: 'Clear Allocated to a build lifecycle filter' }).click()
  await expect(page).not.toHaveURL(/[?&]state=/)
  await expect(page.getByLabel('Lifecycle state filter')).toHaveValue('')

  await page.getByRole('button', { name: /Command Center/ }).first().click()
  await page.locator('.dashboardAreaCard.software .dashboardStateGrid button').filter({hasText:'Approved'}).click()
  await expect(page.getByLabel('Lifecycle state filter')).toHaveValue('ApprovedOrSelected')
  await expect(page.getByRole('heading', { name: 'Software Change Requests' })).toBeVisible()
  await expect(page.locator('.historyActiveFilter b')).toHaveText('Approved or allocated')
})
