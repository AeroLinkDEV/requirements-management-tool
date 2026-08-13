import { expect, test } from '@playwright/test'
import { login } from './auth'

/**
 * The Test Procedure Explorer reads like the Requirements Explorer.
 *
 * It was a flat list with three plain filters where its requirements counterpart has a document rail, a
 * result count, row controls and a columned table. The owner's rule for the verification side is that layout,
 * behaviour and flow are ~99% identical to the requirements equivalent — thin data is acceptable, missing
 * structure is not — so these assert the structure and the behaviour rather than the contents.
 */

const openExplorer = async (page: import('@playwright/test').Page, branch: string) => {
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/${branch}/procedures`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer', level: 1 })).toBeVisible({ timeout: 30_000 })
  return root
}

/**
 * The library size, read only once the first page has actually arrived.
 *
 * The heading renders before the list does, so reading the count straight after the heading appears can
 * capture the zero it shows while loading — and a later "narrower than the whole library" assertion then
 * compares against nothing.
 */
const libraryCount = async (page: import('@playwright/test').Page) => {
  const count = page.locator('.resultCount')
  await expect(count).toBeVisible()
  await expect.poll(async () => Number((await count.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBeGreaterThan(1)
  return Number((await count.textContent())!.replace(/[^\d]/g, ''))
}

test('the Explorer groups procedures by the document they are written into', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  await expect(page.locator('main.reqWorkspace')).toBeVisible()
  await expect(page.locator('.reqCommand')).toBeVisible()
  await expect(page.locator('.reqLayout')).toBeVisible()
  await expect(page.locator('.specRail')).toBeVisible()
  await expect(page.locator('.reqResults')).toBeVisible()
  await expect(page.locator('.requirementInspector')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Advanced' })).toBeVisible()
  await expect(page.getByRole('tablist', { name: 'Test procedure views' })).toHaveCount(0)

  const rail = page.getByRole('navigation', { name: 'Test procedure documents' })
  await expect(rail).toBeVisible()
  // The Software Explorer matches Requirements: both controlled levels are visible from the combined view.
  await expect(rail.locator('[data-document]')).toHaveCount(2, { timeout: 30_000 })
  await expect(rail.locator('[data-document^="HLRTD-"]')).toHaveCount(1)
  await expect(rail.locator('[data-document^="LLRTD-"]')).toHaveCount(1)
  await expect(rail.getByRole('button', { name: /All procedures/ })).toBeVisible()
})

test('the search reports how many procedures matched', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  const count = page.locator('.resultCount')
  await expect(count).toContainText('found')
  // A count that never moves is decoration. Narrowing the search must narrow it.
  const before = await libraryCount(page)
  await page.getByLabel('Find a procedure').fill('HLRTP-000001')
  await expect.poll(async () => Number((await count.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBeLessThan(before)
})

test('procedures are listed in a columned table, not a flat list', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  for (const column of ['Identifier & title', 'Level', 'Verifies', 'Latest result', 'State']) {
    await expect(page.getByRole('columnheader', { name: column })).toBeVisible()
  }
  await expect(page.locator('[data-procedure]').first()).toBeVisible({ timeout: 30_000 })
})

test('rows and the document selection survive a reload', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  const document = page.locator('[data-document]').first()
  await expect(page.locator('[data-document]')).toHaveCount(2, { timeout: 30_000 })
  const documentNumber = (await document.getAttribute('data-document'))!
  await page.getByLabel('Rows per page').selectOption('50')
  await document.click()
  await expect(page).toHaveURL(/procedureRows=50/, { timeout: 30_000 })
  await expect(page).toHaveURL(/procedureDocument=/, { timeout: 30_000 })

  // A filtered worklist that does not survive a reload is a worklist somebody has to rebuild by hand.
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer', level: 1 })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByLabel('Rows per page')).toHaveValue('50')
  await expect(page.locator(`[data-document="${documentNumber}"]`)).toHaveAttribute('aria-pressed', 'true')
})

test('the procedure document is offered where the procedures are read', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  // The requirements Explorer offers its document from the page the requirements are on. Having to leave the
  // procedures to go and find the procedure document is the gap this closes.
  const outputs = page.locator('.documentOutputs')
  await expect(outputs).toBeVisible({ timeout: 30_000 })
  // The combined Software view offers both controlled procedure documents, exactly as Requirements does.
  await expect(outputs.getByText('HLR Test Procedure Document (HLRTD)')).toBeVisible()
  await expect(outputs.getByText('LLR Test Procedure Document (LLRTD)')).toBeVisible()
  await expect(outputs.locator('.documentOutput')).toHaveCount(2)
  await expect(outputs.getByRole('link', { name: /DOCX$/ })).toHaveCount(2)
  await expect(outputs.getByRole('link', { name: /PDF$/ })).toHaveCount(2)
})

test('a worklist can be saved, reopened and removed', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  const name = `Blocked runs ${Date.now()}`
  const count = page.locator('.resultCount')
  const whole = await libraryCount(page)

  // Save the narrowed list, not the whole library — a saved view that does not narrow proves nothing.
  await page.getByLabel('Procedure state').selectOption('Draft')
  await expect.poll(async () => Number((await count.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBeLessThan(whole)
  const views = page.locator('.savedViews')
  await views.locator('summary').click()
  await views.getByRole('button', { name: 'Save this view' }).click()
  await views.getByLabel('Name this worklist').fill(name)
  await views.getByRole('button', { name: 'Save view' }).click()
  await expect(page.locator(`[data-saved-view="${name}"]`)).toBeVisible({ timeout: 30_000 })

  // Clearing puts the whole library back, and applying the view has to bring the worklist back with it.
  await page.getByRole('button', { name: 'Clear', exact: true }).click()
  await expect(page.getByLabel('Procedure state')).toHaveValue('')
  await page.locator(`[data-saved-view="${name}"]`).click()
  await expect(page.getByLabel('Procedure state')).toHaveValue('Draft')
  await expect(page).toHaveURL(/procedureView=/, { timeout: 30_000 })

  // An owner who cannot remove their own view is how duplicates that have to be lived with accumulate.
  page.once('dialog', dialog => dialog.accept())
  await page.locator(`[data-saved-view="${name}"]`).locator('xpath=..')
    .getByRole('button', { name: 'Delete' }).click()
  await expect(page.locator(`[data-saved-view="${name}"]`)).toHaveCount(0, { timeout: 30_000 })
})

test('Clear returns the whole library', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  const count = page.locator('.resultCount')
  const whole = await libraryCount(page)
  await page.getByLabel('Find a procedure').fill('HLRTP-000002')
  await expect.poll(async () => Number((await count.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBeLessThan(whole)

  await page.getByRole('button', { name: 'Clear', exact: true }).click()
  await expect.poll(async () => Number((await count.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBe(whole)
})

/**
 * The two Explorers are the same page over different artifacts.
 *
 * Screenshots of them side by side were the complaint: the requirements Explorer names its discipline, states
 * how many records answer and where in them you are, and puts its filters in one row. The procedure Explorer
 * carried none of that — the same title on all three disciplines, no position in the results, and a caption
 * stacked over every control.
 */
test('the Explorer names the software workspace and opens on both levels', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  await expect(page.getByRole('heading', { name: 'Software Test Procedure Explorer', level: 1 })).toBeVisible()
  await expect(page.getByLabel('Level filter')).toHaveValue('Software')
  await expect(page.getByLabel('Level filter')).toContainText('All software test procedures')
})

test('the result summary says how many answer and where in them you are', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  const summary = page.locator('.resultSummary')
  await expect(summary).toBeVisible({ timeout: 30_000 })
  await expect(summary).toContainText('procedures')
  // Where you are in the results, which the count in the search box could never say.
  await expect(summary).toContainText(/Page \d+ of \d+ · exact current revisions/)
  await expect(summary).toContainText('Permission-aware · Live index')
})

test('the filters read as one row rather than a stack of captioned fields', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  // The names moved onto the controls rather than being lost: still addressable, still announced.
  await expect(page.getByLabel('Find a procedure')).toBeVisible()
  await expect(page.getByLabel('Procedure state')).toBeVisible()
  await expect(page.getByLabel('Latest result')).toBeVisible()
  // The captions that made the bar a form are gone.
  await expect(page.locator('.reqCommand label span')).toHaveCount(1)
})
