import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

/**
 * What a build's requirements are tested by, and what still has nobody looking at it.
 *
 * Two different questions are asked on this page. "Is this requirement covered by a procedure?" is about the
 * library as it stands. "Has the test work this build's changes created been picked up?" is about people and
 * queues. A page answering only the first would show a wall of green while nobody had started on the changes
 * about to ship, which is why the queue is above the inventory.
 */
test('coverage opens on the work the build created, then the inventory behind it', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()

  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  // The queue first: the packages this build's approved changes created.
  await expect(page.getByRole('heading', { name: 'Test change requests' })).toBeVisible()
  // Numbered as controlled records rather than borrowing the number of the change that raised them. The
  // showcase raises one System package for the in-work build, so this is a fact about the page.
  const packages = page.locator('.coverageRow').filter({ hasText: /TCR-/ })
  await expect(packages.first()).toContainText(/SYSTCR-\d{6}\.\d{2}/, { timeout: 30_000 })

  // The counts a reader plans against.
  const summary = page.getByRole('region', { name: 'Coverage summary' })
  await expect(summary).toBeVisible()
  await expect(summary).toContainText('Requirements')

  // And the browsable procedure library, which used to be a tab of its own and was the only way to find a
  // procedure by number.
  await expect(page.getByRole('heading', { name: 'Test procedures' })).toBeVisible()
  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.coverageRow').filter({ hasText: 'SYSTP-000001' })
  await expect(row.first()).toBeVisible({ timeout: 30_000 })
})

/**
 * The decisions inside a package, worked on this page.
 *
 * A test change request is a package of decisions and its state is the sum of them, so a queue that could
 * only be looked at was a queue nobody could act on — and the tabbed workspace could not be retired while
 * the only place to record a decision was inside it.
 */
test('a package opens onto its decisions, and each one is an explicit judgement', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  const first = page.locator('.coverageRow').filter({ hasText: /SYSTCR-/ }).first()
  await expect(first).toBeVisible({ timeout: 30_000 })
  await first.getByRole('button', { name: 'Decisions' }).click()

  const undecided = first.locator('.decisionList li').filter({ has: page.getByRole('button', { name: 'Decide' }) })
  await expect(undecided.first()).toBeVisible({ timeout: 30_000 })
  await undecided.first().getByRole('button', { name: 'Decide' }).click()

  const decide = page.getByRole('dialog', { name: /Decide / })
  await expect(decide).toBeVisible({ timeout: 30_000 })
  // Every value is a judgement somebody made. There is deliberately none meaning "nobody looked", because a
  // requirement must never reach an approved baseline without a decision against it.
  await decide.getByLabel('Decision').selectOption('NoTestRequired')
  await decide.getByLabel('Rationale').fill('Verified by inspection against the approved design note.')
  await decide.getByRole('button', { name: 'Record decision' }).click()

  await expect(decide).toHaveCount(0, { timeout: 30_000 })
  await expect(page.getByRole('status')).toContainText('Decision recorded')
})

/**
 * Who wrote a procedure, and what made them change it.
 *
 * A procedure is read by somebody deciding whether to trust it, and its revisions were reachable only one at
 * a time with no way to see what drove any of them. The change request behind a revision is reached through
 * the verification decision that resolved to it, which is the record that actually connects the two.
 */
test('a procedure says who wrote it and what drove each revision', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.coverageRow').filter({ hasText: 'SYSTP-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'History' }).click()

  const history = page.getByRole('dialog', { name: /History of SYSTP-000001/ })
  await expect(history).toBeVisible({ timeout: 30_000 })
  await expect(history).toContainText('Created by')
  // Every revision, newest first, each saying who wrote it — a name, not an account handle.
  await expect(history.locator('.revisionList li').first()).toContainText('Written by')
  await expect(history.locator('.revisionList li').first()).toContainText(/SYSTP-000001\.\d{2}/)
  // A revision written outside a change request says so rather than leaving the reader to guess.
  await expect(history.locator('.revisionDriver').first()).toBeVisible()

  await history.getByRole('button', { name: 'Close' }).click()
  await expect(history).toHaveCount(0)
})

test('software HLR and LLR each have their own coverage page', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Software HLR Testing Coverage' }).click()
  await expect(page).toHaveURL(/software-verification\/hlr\/coverage$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE HLR')).toBeVisible()
  // The showcase raises one HLR package for this build. This page reported none for a while, and nothing had
  // failed: two loaders on the page shared one "only the newest reply may write the screen" counter, so the
  // procedure search cancelled the load that was fetching the packages. Asserting the package here is what
  // stops that returning as an empty queue nobody can distinguish from having no work.
  await expect(page.locator('.coverageRow').filter({ hasText: /TCR-/ }).first())
    .toContainText(/HLRTCR-\d{6}\.\d{2}/, { timeout: 30_000 })

  await page.getByRole('link', { name: 'Software LLR Testing Coverage' }).click()
  await expect(page).toHaveURL(/software-verification\/llr\/coverage$/, { timeout: 30_000 })
  await expect(page.getByText('VERIFICATION / SOFTWARE LLR')).toBeVisible()
})
