import { expect, test, type Page } from '@playwright/test'
import { login } from './auth'

/**
 * A test change request is read on a page, the way a change request is.
 *
 * Clicking one opened a drawer over the coverage workspace headed "System test engineering decision" — the
 * assessment's view of the package rather than the package's own. There was no way to read its case, what it
 * proposes, what it was raised from, or to take away the controlled document an approver needs.
 *
 * These assert the sections the requirements change request page has, on the verification one, so the two
 * cannot drift apart again.
 */

const openRegister = async (page: Page) => {
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/system-verification/change-requests`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'System Test Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })
  return root
}

test('a package opens on its own page, not in a drawer', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)

  const row = page.locator('[data-register-row]').first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  const number = (await row.getAttribute('data-register-row'))!
  await row.click()

  // A page, addressed by the package — not an overlay on the page you were already on.
  await expect(page).toHaveURL(/\/system-verification\/change-requests\/[0-9a-f-]{36}$/, { timeout: 30_000 })
  await expect(page.getByText(`TEST CHANGE CONTROL / ${number}`)).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(0)
})

test('the page carries the same sections the requirements change request page carries', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)
  await page.locator('[data-register-row]').first().click()
  await expect(page.getByText(/TEST CHANGE CONTROL \//)).toBeVisible({ timeout: 30_000 })

  for (const section of ['Change case', 'Raised from', 'Supporting files', 'Procedure impact', 'Control status']) {
    await expect(page.getByRole('heading', { name: section, level: 2 })).toBeVisible()
  }
  // The change case is Problem-Analysis-Solution here as it is there, not a paragraph of prose.
  for (const part of ['Problem', 'Analysis', 'Solution']) {
    await expect(page.locator('.pasView article').filter({ hasText: part })).toBeVisible()
  }
  // Allocation and state are two separate answers, as on the requirements page.
  const control = page.locator('.controlStatusCard')
  await expect(control.getByText('Allocation')).toBeVisible()
  await expect(control.getByText('State', { exact: true })).toBeVisible()
  // The review-cycle rail stays present even when a historical package has no cycle evidence to show.
  // That keeps the page structure aligned without inventing a workflow the record never entered.
  await expect(page.getByRole('heading', { name: /Review cycle(?: \d+)?/, level: 2 })).toBeVisible()
})

test('check out and edit uses the same full-page two-stage authoring flow as a requirement change', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)
  await page.getByLabel('Lifecycle state filter').selectOption('Draft')
  await page.locator('[data-register-row]').first().click()
  const exactUrl = page.url()

  await page.getByRole('button', { name: 'Check out & edit' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByRole('navigation', { name: 'Checked-out authoring progress' })).toBeVisible()
  await expect(page.getByRole('link', { name: /Change case/ })).toBeVisible()
  await expect(page.getByRole('link', { name: /Procedure changes/ })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Discard checkout' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save & check in' })).toBeVisible()
  await expect(page).toHaveURL(exactUrl)
  await page.getByRole('button', { name: 'Discard checkout' }).click()
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toBeVisible()
})

test('the shared authoring page checks in and reopens the persisted test change case', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)
  await page.getByLabel('Lifecycle state filter').selectOption('Draft')
  await page.locator('[data-register-row]').first().click()

  const title = `Verification parity check-in ${Date.now()}`
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  await page.getByLabel('Title').fill(title)
  await expect(page.getByRole('button', { name: 'Save & check in' })).toBeEnabled({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Save & check in' }).click()

  await expect(page.getByRole('heading', { name: title, level: 1 })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: title, level: 1 })).toBeVisible({ timeout: 30_000 })
})

test('the controlled publication is offered from the package', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)
  await page.locator('[data-register-row]').first().click()
  await expect(page.getByText(/TEST CHANGE CONTROL \//)).toBeVisible({ timeout: 30_000 })

  // An approver reading a package outside the product needed the document a change request has always had.
  await expect(page.getByText('Professional controlled publication')).toBeVisible()
  const docx = page.getByRole('link', { name: 'Download DOCX' })
  await expect(docx).toBeVisible()
  await expect(docx).toHaveAttribute('href', /\/api\/test-change-reviews\/[0-9a-f-]{36}\/download\?format=docx/)
  await expect(page.getByRole('link', { name: 'Download PDF' })).toBeVisible()
})

test('a draft package can be put away and taken back off the shelf', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)

  // A Draft, because that is the state deferral is offered from.
  await page.getByLabel('Lifecycle state filter').selectOption('Draft')
  const row = page.locator('[data-register-row]').first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()
  await expect(page.getByText(/TEST CHANGE CONTROL \//)).toBeVisible({ timeout: 30_000 })

  page.once('dialog', dialog => dialog.accept('Dropped from this build.'))
  await page.getByRole('button', { name: 'Defer' }).click()
  await expect(page.locator('.controlStatusCard').getByText('Deferred')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('Put away because: Dropped from this build.')).toBeVisible()

  await page.getByRole('button', { name: 'Reinstate' }).click()
  // Off the shelf and back to where it was, which for a Draft is a Draft.
  await expect(page.locator('.controlStatusCard').getByText('Draft')).toBeVisible({ timeout: 30_000 })
})
