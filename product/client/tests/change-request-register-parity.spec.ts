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
}

test('the requirements register keeps its shape on the shared component', async ({ page }) => {
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
})

test('the verification register is the same register over test change requests', async ({ page }) => {
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
