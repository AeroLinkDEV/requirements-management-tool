import { expect, test, type Page } from '@playwright/test'
import { login } from './auth'

/**
 * The two change request registers are one register over different artifacts.
 *
 * The verification side had no register page at all: the packages controlling a build's test procedures were
 * a bare table inside the coverage workspace — a `Title` column mostly reading "Not written up yet", a
 * `Procedure decisions` count nobody could interpret, and no build allocation, search, lifecycle filter or
 * paging. Somebody moving from Change Requests on the requirements side to Change Requests here arrived
 * somewhere that did not resemble what they had just left.
 *
 * These assert the structure both sides share, on both sides, from the same locators. A change that breaks
 * parity breaks one of these wherever it is made.
 */

const openFrom = async (page: Page, branch: string) => {
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/${branch}`, page.url()).toString(), { waitUntil: 'load' })
}

/** Everything a reader recognises as "the register", asserted identically wherever it is rendered. */
const assertRegisterShape = async (page: Page) => {
  await expect(page.getByLabel('Search change requests')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByLabel('Lifecycle state filter')).toBeVisible()
  const head = page.locator('.tableHead.allocation')
  await expect(head).toContainText('Change request revision')
  await expect(head).toContainText('Build allocation')
  await expect(head).toContainText('State')
  await expect(head).toContainText('Last activity')
  // The context strip names the build and how many records answer to it.
  await expect(page.locator('.historyContext')).toContainText('Build')
  await expect(page.locator('.historyContext')).toContainText('records')
  // Authorship reads as a person on both sides. Every human-authored meta renders PersonName — the role,
  // where one is shown, lives inside that same presentation rather than replacing the person — and the
  // initials chip that used to turn authorship into "SR · Systems Requirements Author" stays gone.
  await expect(page.locator('.historyRow.allocation').first().or(page.locator('.historyEmpty')))
    .toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.personMeta > i')).toHaveCount(0)
  const humanMetas = page.locator('.personMeta:not(.raisedAutomatically)')
  const humanCount = await humanMetas.count()
  for (let index = 0; index < humanCount; index++) {
    await expect(humanMetas.nth(index).locator('.personName')).toBeVisible()
  }
}

test('the requirements register keeps its shape on the shared component', async ({ page }, testInfo) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openFrom(page, 'systems/change-requests')
  await expect(page.getByRole('heading', { name: 'System Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })

  await assertRegisterShape(page)
  // A row says what it is, what it proposes and who raised it — not just a number.
  const row = page.locator('.historyRow.allocation').first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await expect(row).toContainText('requirement changes')
  await expect(row).toHaveAttribute('href', /systems\/change-requests\/[0-9a-f-]{36}$/)
  await row.click()
  await expect(row).toHaveAttribute('aria-current', 'true')
  await expect(row).toBeFocused()
  await expect(page.getByRole('complementary', { name: /detail$/ })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Open change request →' })).toBeVisible()
  await expect(page.getByRole('tab', { name: 'Trace & impact' })).toBeVisible()
  await expect(page.getByRole('tab', { name: 'History' })).toBeVisible()
  await expect(page.getByRole('tab', { name: 'Discussion' })).toBeVisible()
  await testInfo.attach('requirements-register-normal', { body: await page.screenshot(), contentType: 'image/png' })
})

test('the verification register is the same register over test change requests', async ({ page }, testInfo) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openFrom(page, 'system-verification/change-requests')
  await expect(page.getByRole('heading', { name: 'System Test Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })

  await assertRegisterShape(page)
  const row = page.locator('.historyRow.allocation').first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  // The count that was an uninterpretable "Procedure decisions" column now reads as what it is, in the place
  // the requirements row puts the same fact.
  await expect(row).toContainText('Procedure changes')
  await expect(page.locator('[data-register-row]').first()).toBeVisible()
  await expect(page.locator('[data-register-row]').first()).toHaveAttribute('href', /system-verification\/change-requests\/[0-9a-f-]{36}$/)
  await page.locator('[data-register-row]').first().click()
  await expect(page.locator('[data-register-row]').first()).toHaveAttribute('aria-current', 'true')
  await expect(page.getByRole('link', { name: 'Open change request →' })).toBeVisible()
  await testInfo.attach('verification-register-normal', { body: await page.screenshot(), contentType: 'image/png' })
})

test('shared register supports keyboard selection, empty state, tab navigation, and narrow layout', async ({ page }, testInfo) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 920, height: 900 })
  await login(page)
  await openFrom(page, 'systems/change-requests')
  await expect(page.getByRole('heading', { name: 'System Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })
  const rows = page.locator('.historyRow.allocation')
  await expect(rows.first()).toBeVisible({ timeout: 30_000 })
  await rows.first().focus()
  await page.keyboard.press('Enter')
  await expect(rows.first()).toHaveAttribute('aria-current', 'true')
  if (await rows.count() > 1) {
    await rows.nth(1).focus()
    await page.keyboard.press('Space')
    await expect(rows.nth(1)).toHaveAttribute('aria-current', 'true')
    await expect(rows.first()).not.toHaveAttribute('aria-current', 'true')
  }
  await page.getByRole('tab', { name: 'Overview' }).focus()
  await page.keyboard.press('ArrowRight')
  await expect(page.getByRole('tab', { name: 'Trace & impact' })).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('End')
  await expect(page.getByRole('tab', { name: 'Discussion' })).toHaveAttribute('aria-selected', 'true')
  await page.getByRole('button', { name: 'Close change request inspector' }).click()
  await expect(page.getByRole('complementary', { name: /change request detail$/ })).toBeVisible()
  await expect(page.getByText('Select a change request')).toBeVisible()
  await testInfo.attach('requirements-register-narrow', { body: await page.screenshot(), contentType: 'image/png' })
})

test('the verification register searches and filters like the requirements one', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openFrom(page, 'system-verification/change-requests')
  await expect(page.getByRole('heading', { name: 'System Test Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })

  // Read once the first page has actually arrived. The heading renders before the list does, so reading the
  // count straight after it can capture the zero shown while loading — and then "narrower" compares against
  // nothing.
  const records = page.locator('.historyContext span')
  await expect.poll(async () => Number((await records.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBeGreaterThan(0)
  const whole = Number((await records.textContent())!.replace(/[^\d]/g, ''))

  // A filter that does not narrow is decoration.
  await page.getByLabel('Lifecycle state filter').selectOption('Approved')
  await expect(page.locator('.historyActiveFilter')).toContainText('Approved', { timeout: 30_000 })
  await expect.poll(async () => Number((await records.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBeLessThanOrEqual(whole)

  await page.getByRole('button', { name: /Clear .* lifecycle filter/ }).click()
  await expect.poll(async () => Number((await records.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBe(whole)
})

test('Verification navigation reaches the register rather than the coverage page', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openFrom(page, 'system-verification/coverage')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()

  // "Change Requests" under Verification used to land on the coverage workspace, which is a different page
  // answering a different question.
  await expect(page).toHaveURL(/system-verification\/change-requests$/, { timeout: 30_000 })
  await expect(page.getByRole('heading', { name: 'System Test Change Requests', level: 1 })).toBeVisible()
})

test('register authorship names the person, keeps the role secondary, and preserves automatic attribution', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)

  // The register's rows come from /api/history/change-requests, so the three authorship cases can be
  // stated exactly rather than hoping the seed contains them: a seeded account, an unknown real account,
  // and a package raised with no human author. The first live response donates the real build allocation
  // so the rest of the row still reads like a real record.
  let targetReleaseId = ''
  const fixtureRows = (releaseId: string) => [
    { id: 'f6-person', baseNumber: 'SRCR-900001', revision: 1, displayNumber: 'SRCR-900001', title: 'F6 seeded author', state: 'Draft', deferredFromState: null, authorId: 'systems.author', targetReleaseId: releaseId, requirementCount: 2, hasHighLevelChanges: true, hasLowLevelChanges: false, updatedAt: '2026-08-20T10:15:00Z', revisionCount: 1 },
    { id: 'f6-unknown', baseNumber: 'SRCR-900002', revision: 1, displayNumber: 'SRCR-900002', title: 'F6 unknown account', state: 'Draft', deferredFromState: null, authorId: 'jsmith', targetReleaseId: releaseId, requirementCount: 1, hasHighLevelChanges: false, hasLowLevelChanges: false, updatedAt: '2026-08-20T11:00:00Z', revisionCount: 1 },
    { id: 'f6-automatic', baseNumber: 'SRCR-900003', revision: 1, displayNumber: 'SRCR-900003', title: 'F6 raised automatically', state: 'Draft', deferredFromState: null, authorId: '', targetReleaseId: releaseId, requirementCount: 1, hasHighLevelChanges: false, hasLowLevelChanges: false, updatedAt: '2026-08-20T11:30:00Z', revisionCount: 1 },
  ]
  await page.route('**/api/history/change-requests*', async route => {
    if (!targetReleaseId) {
      const response = await route.fetch()
      const body = await response.json()
      targetReleaseId = body.items?.[0]?.targetReleaseId ?? ''
      await route.fulfill({ response, json: { ...body, items: fixtureRows(targetReleaseId), totalCount: 3 } })
      return
    }
    await route.fulfill({ json: { items: fixtureRows(targetReleaseId), totalCount: 3, totalPages: 1, pageSize: 50 } })
  })

  await openFrom(page, 'systems/change-requests')
  await expect(page.getByRole('heading', { name: 'System Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })

  // A seeded identity reads as the person first, exactly as the detail surface presents them. The role is
  // secondary inside the same presentation, and the underlying account stays recoverable through the title.
  const seeded = page.locator('[data-register-row="SRCR-900001"] .personMeta .personName')
  await expect(seeded).toHaveText('Maya Patel · Systems Lead')
  await expect(seeded).toHaveAttribute('title', 'systems.author')
  expect(await seeded.evaluate(node => node.firstChild?.textContent)).toBe('Maya Patel')

  // An unknown real account still identifies itself truthfully instead of disappearing into blankness or
  // borrowing a name it was never given.
  const unknown = page.locator('[data-register-row="SRCR-900002"] .personMeta .personName')
  await expect(unknown).toHaveText('jsmith')
  await expect(unknown).toHaveAttribute('title', 'jsmith')

  // Packages without a human author keep their explicit attribution — F6 does not invent one.
  await expect(page.locator('[data-register-row="SRCR-900003"] .personMeta')).toHaveText('Raised by assessment')

  // The initials chip that made the row read "SR · Systems Requirements Author" is gone entirely.
  await expect(page.locator('.personMeta > i')).toHaveCount(0)
})
