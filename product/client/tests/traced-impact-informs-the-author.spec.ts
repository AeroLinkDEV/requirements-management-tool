import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

// What the traces already record, shown to the person deciding impact.
//
// A proposal asks its author to close five impact decisions, two of which — trace relationships and verification
// coverage — are answerable from links the product already holds. Those links were visible on the requirements
// explorer and nowhere near the author, so the decision was made from memory beside a database that knew.
//
// The line this journey holds is that informing is not deciding: the panel is read-only, and the five
// dispositions stay Pending until a person sets them.
test('a modified requirement shows what the traces record, without deciding anything', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'New System SCR' }).click()

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
  // Said plainly, because a reader must not take this for the decision.
  await expect(traced.getByText(/You still decide each disposition below/)).toBeVisible()

  // Read-only: the panel offers no control of any kind.
  await expect(traced.locator('select, input, button')).toHaveCount(0)

  // And the five decisions are untouched. Seeing the evidence must not close the gate.
  const dispositions = page.locator('.editorColumns aside select')
  await expect(dispositions).toHaveCount(5)
  for (let index = 0; index < 5; index++) await expect(dispositions.nth(index)).toHaveValue('Pending')
  await expect(page.getByText('0/5 impact decisions complete')).toBeVisible()
})
