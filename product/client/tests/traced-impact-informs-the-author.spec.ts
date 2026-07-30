import { expect, test } from '@playwright/test'
import { login, openNewSystemChangeRequest, selectProgram } from './auth'

// The author can see the live trace without being asked to perform downstream engineering triage.
test('a modified requirement shows read-only downstream context without author impact controls', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNewSystemChangeRequest(page)

  // Nothing to trace before a requirement is chosen, and nothing for an introduction — a requirement that does
  // not exist yet has nothing downstream.
  await page.getByRole('button', { name: '+ Introduce System requirement' }).click()
  await expect(page.getByRole('region', { name: /Recorded links for proposal 1/ })).toHaveCount(0)

  // Switching to Modify and choosing a real requirement is what makes the traces relevant.
  await page.getByLabel('Change type').selectOption('Modify')
  const search = page.getByRole('textbox', { name: /Find controlled requirement/ }).last()
  await search.fill('SYSR-000001')
  const candidate = page.locator('.proposalLookupResults button').first()
  await expect(candidate).toBeVisible({ timeout: 30_000 })
  await candidate.click()

  const traced = page.getByRole('region', { name: /Recorded links for proposal 1/ })
  await expect(traced).toBeVisible({ timeout: 30_000 })
  await expect(traced.getByText('Requirements derived from this one')).toBeVisible()
  await expect(traced.getByText('Procedures that verify it')).toBeVisible()
  // Read-only: the panel offers no control of any kind. This, not a sentence, is what makes it read-only.
  await expect(traced.locator('select, input, button')).toHaveCount(0)

  // The panel says what is downstream and stops there. It used to narrate what it was not asking the author
  // to do, and to chip every listed record with a level the identifier already states and a state that was
  // "Approved" for nearly all of them — three ways of spending the reader's attention on nothing.
  await expect(traced.getByText(/does not ask the author to make an impact decision/)).toHaveCount(0)
  await expect(traced.getByText(/HighLevel|LowLevel/)).toHaveCount(0)

  await expect(page.locator('.editorColumns aside select')).toHaveCount(0)
  await expect(page.getByText(/lifecycle impact/i)).toHaveCount(0)
})
