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
  await save.click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()
  await expect(page.getByText('Draft', { exact: true }).first()).toBeVisible()
})
