import { expect, test } from '@playwright/test'
import { apiLogin, login, openNewSystemChangeRequest, selectProgram } from './auth'

test('a Draft needs only a title and never consumes an identifier for an empty form', async ({ page, request }) => {
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNewSystemChangeRequest(page)

  const save = page.getByRole('button', { name: 'Save SCR Draft' })
  await expect(save).toBeEnabled()
  await save.click()
  await expect(page.getByRole('alert')).toContainText(
    'Title of change request must be filled out before save is available.',
  )
  await expect(page).toHaveURL(/\/systems\/change-requests\/new$/)

  const title = `Minimal Draft ${Date.now()}`
  await page.getByLabel('Title').fill(title)
  await expect(page.getByRole('alert')).toBeHidden()
  await save.click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()
  await expect(page.getByText('Draft', { exact: true }).first()).toBeVisible()
})

test('proposal validation clears when the author corrects the proposal', async ({ page, request }) => {
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNewSystemChangeRequest(page)

  await page.getByLabel('Title').fill(`Corrected proposal ${Date.now()}`)
  await page.getByRole('button', { name: '+ Introduce System requirement' }).click()
  // The browser's required-field guard normally stops this before React submission. Disable only that native
  // layer here so the application's fallback validation and its correction lifecycle are covered directly.
  await page.locator('form').evaluate((form: HTMLFormElement) => { form.noValidate = true })
  await page.getByRole('button', { name: 'Save SCR Draft' }).click()

  const alert = page.getByRole('alert')
  await expect(alert).toContainText(/Add a statement to (SYSR-\d+|the new requirement)/)
  await page.getByLabel('Requirement statement').fill('The system shall report a corrected proposal without stale validation.')
  await expect(alert).toBeHidden()
})
