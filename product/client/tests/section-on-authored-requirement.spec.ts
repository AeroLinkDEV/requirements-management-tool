import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

// Where a requirement goes in the document, chosen by the author writing it.
//
// A requirement's place in a specification is part of what a change request proposes, and nothing carried it: an
// introduced requirement landed wherever a backfill put it, and a modification could not move one. The read side
// already filtered by section, so the explorer had a filter nothing could aim.
test('an author chooses the section a new requirement goes in', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'New System SCR' }).click()
  await page.getByRole('button', { name: '+ Introduce System requirement' }).click()

  // The sections come from the specification for this requirement's level. Offering every section in the project
  // would let a system requirement be filed in the software document.
  const section = page.getByLabel('Section for proposal 1')
  await expect(section).toBeVisible()
  const options = await section.locator('option').allTextContents()
  // A default that changes nothing, plus the real sections.
  expect(options[0]).toBe('Decide when the baseline is assembled')
  expect(options.length).toBeGreaterThan(1)

  await section.selectOption({ index: 1 })
  await expect(section).not.toHaveValue('')
})

test('modifying a requirement offers to leave it where it already is', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'New System SCR' }).click()
  await page.getByRole('button', { name: 'Modify existing' }).first().click()

  // Before a requirement is chosen there is nothing to keep, so the default reads as a move rather than a stay.
  const section = page.getByLabel('Section for proposal 1')
  await expect(section.locator('option').first()).toHaveText('Leave where it is')

  const search = page.getByRole('textbox', { name: /Find controlled requirement/ }).last()
  await search.fill('SYSR-000001')
  const candidate = page.locator('.proposalLookupResults button').first()
  await expect(candidate).toBeVisible({ timeout: 30_000 })
  await candidate.click()

  // The requirement arrives with the section it is already in, so choosing one to modify does not silently
  // relocate it — which is the commonest way document structure gets rearranged by accident.
  await expect(section).not.toHaveValue('', { timeout: 30_000 })
})
