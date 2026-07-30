import { expect, test } from '@playwright/test'
import { apiLogin, login, openNavigationGroup, selectProgram } from './auth'

/**
 * Verification opens on the work an approved change created.
 *
 * It used to open on requirement coverage — the inventory of what the release contains — with the outstanding
 * count shown only as a badge on a tab nobody had reason to click. Somebody arriving to do verification work
 * saw a table of everything and no sign of the items the last approval had just made their problem.
 *
 * The inventory has not gone anywhere. It is one tab across, which is the right distance for a question asked
 * occasionally rather than the first thing in the way.
 */
test('verification opens on the queue the release created, grouped by cause', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'VERIFICATION')
  await page.getByRole('link', { name: 'System Verification' }).click()
  await expect(page.getByRole('heading', { name: 'Verification & Evidence' })).toBeVisible({ timeout: 30_000 })

  // Landing on the queue, without anybody clicking a tab.
  await expect(page.getByRole('heading', { name: 'Test procedure alignment' })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.impactSummary')).toBeVisible()

  const summary = page.locator('.impactSummary article')
  await expect(summary).toHaveCount(4)
  await expect(summary.nth(0)).toContainText('Undecided')
  await expect(summary.nth(3)).toContainText('Release gate')

  // The demonstration release carries impact items, so the grouping below is genuinely exercised rather than
  // skipped past. Asserted rather than assumed, because a check that passes by finding nothing to look at is
  // worse than no check — this suite has already produced one of those.
  const rows = page.locator('.impactRow')
  const rowCount = await rows.count()
  expect(rowCount, 'FMSLIVE should carry verification impact for the in-work release').toBeGreaterThan(0)

  // Grouped by what created the work, because the three causes need different things done to them.
  expect(await page.locator('.impactGroup').count(), 'items should be grouped by cause').toBeGreaterThan(0)
  const headings = await page.locator('.impactGroupHead b').allInnerTexts()
  expect(
    headings.every(heading =>
      /New requirements need a procedure|Changed requirements|Retired requirements|New requirement verification decisions|Changed requirement verification decisions|Retired requirement procedure decisions/.test(heading),
    ),
    `unexpected group headings: ${headings.join(' | ')}`,
  ).toBeTruthy()

  // Every row sits inside exactly one group, so nothing is stranded outside the grouping.
  expect(await page.locator('.impactGroup .impactRow').count()).toBe(rowCount)

  // The coverage inventory is still reachable, one tab across.
  await page.getByRole('button', { name: /Requirement coverage/ }).click()
  await expect(page.getByRole('heading', { name: 'Requirement coverage' })).toBeVisible()
})
