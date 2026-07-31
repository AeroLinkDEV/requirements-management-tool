import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

/**
 * What a build has to run, and what happened when it was run.
 *
 * A build is rarely worth its whole test suite. Somebody decides which procedures this one needs, and the
 * release is then measured against that decision — the two readiness gates that used to be driven by a
 * checkbox on individual verification decisions now read this set. Until this page existed the set could
 * only be populated as a side effect of that checkbox, and could not be seen at all.
 */
test('a lead chooses what the build runs, and the set says why each procedure is there', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Results' }).click()

  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  // Nothing is chosen by default. A build that silently inherited the whole suite would have had no decision
  // made about it, and the gate would measure it against work nobody intended to do.
  await expect(page.getByText('Nothing has been chosen for this build yet')).toBeVisible()

  await page.getByLabel('Find an approved procedure').fill('SYSTP-000001')
  const candidate = page.locator('.testSetCandidates label').first()
  await expect(candidate).toBeVisible({ timeout: 30_000 })
  await candidate.locator('input[type="checkbox"]').check()

  // The two routes a lead arrives by are recorded separately, because "we tested this because it changed"
  // and "we tested this because we swept the area" are different answers to why a build ran what it ran.
  await page.getByRole('button', { name: 'Add — area sweep' }).click()

  const row = page.locator('.testSetRow')
  await expect(row).toHaveCount(1, { timeout: 30_000 })
  await expect(row).toContainText('Area sweep')
  // Whatever the build has recorded against it, said plainly. The showcase has already run this one, so the
  // assertion is on there being a determination rather than on which — the point is that the plan and the
  // result are read together, not that a particular procedure passed.
  await expect(row.locator('i')).toHaveText(/Pass|Fail|Blocked|Not run/)
  await expect(page.getByText('Nothing has been chosen for this build yet')).toHaveCount(0)

  // The counts a lead plans against, and the same facts the release gate reads.
  const summary = page.getByRole('region', { name: 'Test set progress' })
  await expect(summary).toContainText('1')

  await row.getByRole('button', { name: 'Remove' }).click()
  await expect(page.locator('.testSetRow')).toHaveCount(0, { timeout: 30_000 })
  await expect(page.getByText('Nothing has been chosen for this build yet')).toBeVisible()
})

/**
 * Software test work splits into HLR and LLR, which are planned, done and approved by different people. Each
 * gets its own set, so one discipline's plan can never be read as the other's.
 */
test('software HLR and LLR each have their own test set', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Software HLR Test Results' }).click()
  await expect(page).toHaveURL(/software-verification\/hlr\/results$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE HLR')).toBeVisible()

  await page.getByRole('link', { name: 'Software LLR Test Results' }).click()
  await expect(page).toHaveURL(/software-verification\/llr\/results$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE LLR')).toBeVisible()

  // The address is the page, so a plan can be refreshed, shared, or reached with the back button.
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByText('VERIFICATION / SOFTWARE LLR')).toBeVisible({ timeout: 30_000 })
})
