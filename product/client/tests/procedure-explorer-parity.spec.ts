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
  await page.goto(new URL(`${root}/${branch}/cases`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer', level: 1 })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByLabel('Artifact filter')).toHaveValue('Case')
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

test('the Explorer groups Cases by the document they are written into', async ({ page }) => {
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
  await expect(page.getByRole('tablist', { name: 'Test case views' })).toHaveCount(0)

  const rail = page.getByRole('navigation', { name: 'test case documents' })
  await expect(rail).toBeVisible()
  // The rail remains the full software document catalogue while the worklist opens on its configured HLR level.
  await expect(rail.locator('[data-document]')).toHaveCount(2, { timeout: 30_000 })
  await expect(rail.locator('[data-document^="HLRTD-"]')).toHaveCount(1)
  await expect(rail.locator('[data-document^="LLRTD-"]')).toHaveCount(1)
  await expect(rail.getByRole('button', { name: /All (test )?cases/i })).toBeVisible()
})

test('a Case-only profile does not present disabled software Procedure documents', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/software-verification/cases?artifactKind=Procedure`, page.url()).toString(),
    { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer', level: 1 }))
    .toBeVisible({ timeout: 30_000 })
  await expect(page.getByLabel('Artifact filter')).toHaveValue('Case')

  // Historical Procedure rows do not override the Case-only effective profile, which owns no Procedure register
  // or generated-document action. Showing HLRTPD/LLRTPD here would assert activation that has not happened.
  await expect(page.getByRole('navigation', { name: 'test procedure documents' })).toHaveCount(0)
  await expect(page.locator('.documentOutputs')).toHaveCount(1)
  await expect(page.getByText(/HLRTPD|LLRTPD/)).toHaveCount(0)
})

test('a Case-only profile refuses a direct disabled Procedure change route but keeps Case routes usable', async ({ page }) => {
  test.setTimeout(180_000)
  await page.route('**/api/projects/*/configuration', async route => {
    const response = await route.fetch()
    const configuration = await response.json()
    configuration.effectiveSteps = configuration.effectiveSteps.map((step: { catalogueEntry: string }) => ({
      ...step,
      enabledArtifactKinds: step.catalogueEntry === 'System' ? ['Procedure'] : ['Case'],
    }))
    await route.fulfill({ response, json: configuration })
  })
  await login(page)
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/software-verification/hlr/change-requests?kind=Procedure`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Workspace unavailable', level: 1 })).toBeVisible({ timeout: 30_000 })
  await page.goto(new URL(`${root}/software-verification/hlr/change-requests`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Software Test Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })
})

test('saved Explorer views apply legacy Case, explicit all, and explicit Procedure kinds', async ({ page }) => {
  test.setTimeout(180_000)
  await page.route('**/api/projects/*/configuration', async route => {
    const response = await route.fetch()
    const configuration = await response.json()
    configuration.effectiveSteps = configuration.effectiveSteps.map((step: { catalogueEntry: string }) => ({
      ...step,
      enabledArtifactKinds: step.catalogueEntry === 'System' ? ['Procedure'] : ['Case', 'Procedure'],
    }))
    await route.fulfill({ response, json: configuration })
  })
  await page.route('**/api/verification-artifacts?*', async route => {
    const response = await route.fetch()
    const body = await response.json()
    body.views = [
      { id: 'legacy-case-view', name: 'Legacy cases', queryJson: '{"level":"Software"}', columnsJson: '[]', isShared: false, owned: false },
      { id: 'all-artifacts-view', name: 'All artifacts', queryJson: '{"artifactKind":"all","level":"Software"}', columnsJson: '[]', isShared: false, owned: false },
      { id: 'procedure-view', name: 'Procedures', queryJson: '{"artifactKind":"Procedure","level":"Software"}', columnsJson: '[]', isShared: false, owned: false },
    ]
    await route.fulfill({ response, json: body })
  })
  await login(page)
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/software-verification/test-artifacts`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer', level: 1 })).toBeVisible({ timeout: 30_000 })
  const views = page.getByRole('region', { name: 'Saved views' })
  await views.locator('summary').click()
  await views.locator('[data-saved-view="Legacy cases"]').click()
  await expect(page.getByLabel('Artifact filter')).toHaveValue('Case')
  await views.locator('[data-saved-view="All artifacts"]').click()
  await expect(page.getByLabel('Artifact filter')).toHaveValue('all')
  await views.locator('[data-saved-view="Procedures"]').click()
  await expect(page.getByLabel('Artifact filter')).toHaveValue('Procedure')
})

test('a Procedure-enabled profile adds distinct HLRTPD and LLRTPD actions in the shared Document Center', async ({ page }) => {
  test.setTimeout(180_000)
  await page.route('**/api/projects/*/configuration', async route => {
    const response = await route.fetch()
    const configuration = await response.json()
    configuration.effectiveSteps = configuration.effectiveSteps.map((step: { catalogueEntry: string }) => ({
      ...step,
      enabledArtifactKinds: step.catalogueEntry === 'System' ? ['Procedure'] : ['Case', 'Procedure'],
    }))
    await route.fulfill({ response, json: configuration })
  })
  await login(page)
  const verification = page.locator('.navGroup').filter({
    has: page.locator('summary').filter({ hasText: 'VERIFICATION' }),
  })
  if (await verification.getAttribute('open') === null) await verification.locator('summary').click()
  await verification.getByRole('group', { name: 'Verification scope' })
    .getByRole('button', { name: 'Software' }).click()
  await verification.getByRole('link', { name: 'Generated Software Verification Documents' }).click()

  await expect(page.getByRole('heading', { name: 'Documents', level: 1 })).toBeVisible()
  const outputs = page.getByRole('region', { name: 'Software assurance documents' })
  await expect(outputs.locator('.documentOutput')).toHaveCount(4)
  await expect(outputs.getByText('HLR Test Case Document (HLRTD)')).toBeVisible()
  await expect(outputs.getByText('LLR Test Case Document (LLRTD)')).toBeVisible()
  await expect(outputs.getByText('HLR Test Procedure Document (HLRTPD)')).toBeVisible()
  await expect(outputs.getByText('LLR Test Procedure Document (LLRTPD)')).toBeVisible()
})

test('a partial Procedure profile offers only its exact HLR Procedure document target', async ({ page }) => {
  test.setTimeout(180_000)
  await page.route('**/api/projects/*/configuration', async route => {
    const response = await route.fetch()
    const configuration = await response.json()
    configuration.effectiveSteps = configuration.effectiveSteps.map((step: { catalogueEntry: string }) => ({
      ...step,
      enabledArtifactKinds: step.catalogueEntry === 'System'
        ? ['Procedure']
        : step.catalogueEntry === 'HighLevel'
          ? ['Case', 'Procedure']
          : ['Case'],
    }))
    await route.fulfill({ response, json: configuration })
  })
  await login(page)
  const verification = page.locator('.navGroup').filter({
    has: page.locator('summary').filter({ hasText: 'VERIFICATION' }),
  })
  if (await verification.getAttribute('open') === null) await verification.locator('summary').click()
  await verification.getByRole('group', { name: 'Verification scope' })
    .getByRole('button', { name: 'Software' }).click()
  await verification.getByRole('link', { name: 'Generated Software Verification Documents' }).click()

  const outputs = page.getByRole('region', { name: 'Software assurance documents' })
  await expect(outputs.locator('.documentOutput')).toHaveCount(3)
  await expect(outputs.getByText('HLR Test Procedure Document (HLRTPD)')).toBeVisible()
  await expect(outputs.getByText('LLR Test Procedure Document (LLRTPD)')).toHaveCount(0)
})

test('the search reports how many Cases matched', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  const count = page.locator('.resultCount')
  await expect(count).toContainText('found')
  // A count that never moves is decoration. Narrowing the search must narrow it.
  const before = await libraryCount(page)
  await page.getByLabel('Find a case').fill('HLRTC-000001')
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
  await expect(page).toHaveURL(/artifactRows=50/, { timeout: 30_000 })
  await expect(page).toHaveURL(/artifactDocument=/, { timeout: 30_000 })

  // A filtered worklist that does not survive a reload is a worklist somebody has to rebuild by hand.
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer', level: 1 })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByLabel('Rows per page')).toHaveValue('50')
  await expect(page.locator(`[data-document="${documentNumber}"]`)).toHaveAttribute('aria-pressed', 'true')
})

test('the combined document rail is offered where the artifacts are read', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  // The Explorer offers its documents from the page the artifacts are on. Having to leave the inventory to find
  // the generated document is the gap this closes.
  const outputs = page.locator('.documentOutputs')
  await expect(outputs).toBeVisible({ timeout: 30_000 })
  // The neutral software route offers the complete configured software document rail.
  await expect(outputs.getByText('HLR Test Case Document (HLRTD)')).toBeVisible()
  await expect(outputs.getByText('LLR Test Case Document (LLRTD)')).toBeVisible()
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
  await page.getByLabel('case state').selectOption('Draft')
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
  await expect(page.getByLabel('test artifact state')).toHaveValue('')
  await page.locator(`[data-saved-view="${name}"]`).click()
  await expect(page.getByLabel('case state')).toHaveValue('Draft')
  await expect(page).toHaveURL(/artifactView=/, { timeout: 30_000 })

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
  await page.getByLabel('Level filter').selectOption('HighLevel')
  await expect.poll(() => libraryCount(page), { timeout: 30_000 }).toBeLessThan(whole)
  await page.getByLabel('Find a case').fill('HLRTC-000002')
  await expect.poll(async () => Number((await count.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBeLessThan(whole)

  await page.getByRole('button', { name: 'Clear', exact: true }).click()
  await expect.poll(async () => Number((await count.textContent())!.replace(/[^\d]/g, '')), { timeout: 30_000 })
    .toBe(whole)
})

/**
 * The combined Explorer is the same page over the software Case and Procedure inventory.
 *
 * Screenshots of them side by side were the complaint: the requirements Explorer names its discipline, states
 * how many records answer and where in them you are, and puts its filters in one row. The procedure Explorer
 * carried none of that — the same title on all three disciplines, no position in the results, and a caption
 * stacked over every control.
 */
test('the combined Explorer names the software workspace and opens on all software artifacts', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/software-verification/test-artifacts`, page.url()).toString(), { waitUntil: 'load' })

  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer', level: 1 })).toBeVisible()
  await expect(page.getByLabel('Level filter')).toHaveValue('Software')
  await expect(page.getByLabel('Level filter')).toContainText('All software test artifacts')
  await expect(page.getByLabel('Level filter')).toContainText('Software LLR')
  await expect(page.getByLabel('Artifact filter')).toHaveValue('all')
  const rail = page.getByRole('navigation', { name: 'test artifact documents' })
  await expect(rail).toBeVisible()
  await expect(rail.locator('[data-document]')).toHaveCount(2, { timeout: 30_000 })
  await expect(rail.getByRole('button', { name: /All test artifacts/i })).toBeVisible()
})

test('the result summary says how many Cases answer and where in them you are', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  const summary = page.locator('.resultSummary')
  await expect(summary).toBeVisible({ timeout: 30_000 })
  await expect(summary).toContainText('cases')
  // Where you are in the results, which the count in the search box could never say.
  await expect(summary).toContainText(/Page \d+ of \d+ · exact current revisions/)
  await expect(summary).toContainText('Live counts · respects your access')
})

test('the filters read as one row rather than a stack of captioned fields', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openExplorer(page, 'software-verification')

  // The names moved onto the controls rather than being lost: still addressable, still announced.
  await expect(page.getByLabel('Find a case')).toBeVisible()
  await expect(page.getByLabel('Case state')).toBeVisible()
  await expect(page.getByLabel('Latest result')).toBeVisible()
  // The captions that made the bar a form are gone.
  await expect(page.locator('.reqCommand label span')).toHaveCount(1)
})
