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

  // A determination is a person's judgement, recorded by them. AeroLink never executes anything, so this is
  // the only way a result exists at all — and the page is called Test Results, so being unable to record one
  // would have been a page that could not do the thing it is named for.
  await row.getByRole('button', { name: /Record result|Record retest/ }).click()
  const record = page.getByRole('dialog', { name: /Record a result for SYSTP-000001/ })
  await expect(record).toBeVisible({ timeout: 30_000 })
  await record.getByLabel('Configuration under test').fill('FMS rig 2, data set B')
  await record.getByLabel('Determination').fill('Sequencing held across the oceanic transition with no dropped waypoint.')
  // A Pass claims something was observed, so the product requires it to say where that observation lives.
  await record.getByLabel('Evidence reference').fill('rig2/oceanic-2026-07-30.log')
  await record.getByRole('button', { name: 'Record determination' }).click()

  await expect(record).toHaveCount(0, { timeout: 30_000 })
  await expect(row).toContainText('Pass')
  // The run is now attachable: evidence belongs to a result, and there was no result before this.
  await expect(row.getByLabel(/Attach evidence for SYSTP-000001/)).toBeAttached()

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

/**
 * A determination read beside the ones before it.
 *
 * A result read alone says what happened. Read with its history it says whether the build is getting better
 * or worse — and a retest can then answer the specific failure rather than whatever happened last, which is
 * what a corrective action is for.
 */
test('a procedure shows every run against this build, and a failure can be retested by name', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Results' }).click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find an approved procedure').fill('SYSTP-000001')
  await page.locator('.testSetCandidates label').first().locator('input[type="checkbox"]').check()
  await page.getByRole('button', { name: 'Add — covers a change' }).click()

  const row = page.locator('.testSetRow').first()
  await expect(row).toBeVisible({ timeout: 30_000 })

  // Record a failure, so there is something a retest can answer. Determinations are immutable and this
  // build's procedure keeps every run ever recorded against it, so each run is found by its own text
  // rather than by position — a journey that asserted "the first run" would be asserting the last time
  // this journey ran.
  const failure = `Waypoint dropped at the oceanic transition ${Date.now()}`
  await row.getByRole('button', { name: /Record result|Record retest/ }).click()
  const record = page.getByRole('dialog', { name: /Record a result for SYSTP-000001/ })
  await record.getByLabel('Outcome').selectOption('Fail')
  await record.getByLabel('Configuration under test').fill('FMS rig 2, data set B')
  await record.getByLabel('Determination').fill(failure)
  await record.getByLabel('Evidence reference').fill('rig2/oceanic-fail.log')
  await record.getByRole('button', { name: 'Record determination' }).click()
  await expect(record).toHaveCount(0, { timeout: 30_000 })

  await row.getByRole('button', { name: 'Runs' }).click()
  const failed = row.locator('.runList li').filter({ hasText: failure })
  await expect(failed).toHaveCount(1)
  await expect(failed).toContainText('Fail')

  // The retest names the run it answers rather than simply the latest.
  const answer = `Sequencing held after the correction ${Date.now()}`
  await failed.getByRole('button', { name: 'Retest this run' }).click()
  const retest = page.getByRole('dialog', { name: /Record a result for SYSTP-000001/ })
  await retest.getByLabel('Configuration under test').fill('FMS rig 2, corrected build')
  await retest.getByLabel('Determination').fill(answer)
  await retest.getByLabel('Evidence reference').fill('rig2/oceanic-retest.log')
  await retest.getByRole('button', { name: 'Record determination' }).click()
  await expect(retest).toHaveCount(0, { timeout: 30_000 })

  await expect(row).toContainText('Pass')
  // The panel is still open — the button now reads 'Hide runs', so clicking again would collapse it.
  const answered = row.locator('.runList li').filter({ hasText: answer })
  await expect(answered).toContainText('retest', { timeout: 30_000 })
  await expect(answered).toContainText('Pass')
})
