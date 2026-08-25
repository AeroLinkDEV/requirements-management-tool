import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, selectProgram } from './auth'

/**
 * #399 — the Test Procedure Explorer's Trace &amp; impact tab must be a trace, not a count.
 *
 * The server projection names the exact procedure revision the selected build carries and every exact
 * requirement revision it verifies, with Confirmed/Suspect coverage state and TCR/change provenance. The
 * browser's job is to render that projection and to navigate procedure -> exact requirement revision through
 * the canonical route, preserving Program, build, procedure revision and requirement revision context in the
 * address so refresh and direct deep links reopen the same trace.
 */
test('Trace & impact lists the exact requirements and opens the exact requirement revision', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.procedureRow').filter({ hasText: 'SYSTP-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()

  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await inspector.getByRole('button', { name: 'Trace & impact' }).click()

  // The tab is part of the address, so the same trace can be reopened directly.
  await expect(page).toHaveURL(/procedureTab=trace/, { timeout: 30_000 })
  const revisionIdentity = inspector.locator('.traceRevisionIdentity')
  await expect(revisionIdentity).toContainText('SYSTP-000001.00', { timeout: 30_000 })
  await expect(revisionIdentity).toContainText(/revision [0-9a-f-]{36}/)
  await expect(inspector).toContainText('This procedure verifies 2 requirements.')

  const rows = inspector.locator('.traceRequirement')
  await expect(rows).toHaveCount(2, { timeout: 30_000 })
  await expect(rows.first()).toContainText(/SYSR-\d{6}\.\d{2}/)
  await expect(rows.first()).toContainText('System')
  await expect(rows.first()).toContainText('Confirmed')
  await expect(rows.first()).toContainText(/Revision [0-9a-f-]{36}/)

  await rows.first().getByRole('button', { name: /Open requirement/ }).click()
  await expect(page).toHaveURL(
    /\/requirements\/[0-9a-f-]{36}\?discipline=system&requirementRevisionId=[0-9a-f-]{36}/,
    { timeout: 30_000 })
  const requirementHeading = page.getByRole('heading', { name: /^SYSR-\d{6}\.\d{2}$/ })
  await expect(requirementHeading).toBeVisible({ timeout: 30_000 })
  const openedDisplay = (await requirementHeading.textContent())!.trim()
  expect(openedDisplay).toMatch(/^SYSR-\d{6}\.\d{2}$/)

  const exactUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(exactUrl)
  await expect(page.getByRole('heading', { name: openedDisplay })).toBeVisible({ timeout: 30_000 })
})

test('a released build keeps its exact procedure revision trace across refresh', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page)
  await page.getByRole('button', { name: /Back to Software Builds/ }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.procedureRow').filter({ hasText: 'SYSTP-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()

  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await inspector.getByRole('button', { name: 'Trace & impact' }).click()
  await expect(inspector.locator('.traceRevisionIdentity')).toContainText('SYSTP-000001.00', { timeout: 30_000 })
  await expect(inspector.locator('.traceRequirement')).toHaveCount(2, { timeout: 30_000 })
  await expect(inspector.locator('.traceRequirement').first()).toContainText('Confirmed')

  // A direct deep link to the trace reopens the same procedure, revision and tab after refresh.
  await expect(page).toHaveURL(/procedureRevisionId=[0-9a-f-]{36}.*procedureTab=trace/)
  const exactUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(exactUrl)
  await expect(page.locator('.requirementInspector')).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.requirementInspector .traceRevisionIdentity'))
    .toContainText('SYSTP-000001.00', { timeout: 30_000 })
})

test('zero coverage stays explicit and truthful in Trace & impact', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.procedureRow').filter({ hasText: 'SYSTP-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()
  await expect(page).toHaveURL(/procedureId=.*procedureRevisionId=/, { timeout: 30_000 })
  const openedUrl = new URL(page.url())
  const procedureId = openedUrl.searchParams.get('procedureId')!
  const revisionId = openedUrl.searchParams.get('procedureRevisionId')!

  // The server truth for a carried procedure with no current links is an empty exact projection; the browser
  // must say "nothing is verified" rather than hiding the procedure or inventing coverage.
  await page.route(
    url => {
      const requestUrl = new URL(url.toString())
      return requestUrl.pathname === `/api/test-procedures/${procedureId}/trace`
        && requestUrl.searchParams.has('releaseId')
    },
    async route => {
      const requestUrl = new URL(route.request().url())
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          procedureId,
          baseNumber: 'SYSTP-000001',
          title: 'Verify System FMS behavior group 001',
          level: 'System',
          revisionId,
          displayNumber: 'SYSTP-000001.00',
          revision: 0,
          state: 'Approved',
          authorId: 'test.author',
          createdAt: new Date().toISOString(),
          sourceTestChangeRequestId: null,
          requirements: [],
          provenance: [],
          build: {
            releaseId: requestUrl.searchParams.get('releaseId'),
            effectiveBaselineId: null,
            isExactManifest: true,
          },
        }),
      })
    },
  )

  await page.locator('.requirementInspector').getByRole('button', { name: 'Trace & impact' }).click()
  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toContainText('This procedure verifies 0 requirements.')
  await expect(inspector).toContainText(/Nothing is verified by SYSTP-000001\.00/)
})

test('a software HLR trace navigates to the exact software requirement revision', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()
  await page.getByRole('link', { name: 'Test Case/Procedure Explorer' }).click()
  await expect(page).toHaveURL(/software-verification\/cases$/, { timeout: 30_000 })
  await expect(page.getByLabel('Level filter')).toHaveValue('HighLevel')

  await page.getByLabel('Find a case').fill('HLRTC-000001')
  const row = page.locator('.procedureRow').filter({ hasText: 'HLRTC-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()

  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await inspector.getByRole('button', { name: 'Trace & impact' }).click()
  const rows = inspector.locator('.traceRequirement')
  await expect(rows.first()).toBeVisible({ timeout: 30_000 })

  await rows.first().getByRole('button', { name: /Open requirement/ }).click()
  // The shared Software Requirements Explorer opens the exact software requirement revision; the HLR level
  // is carried by the route and the revision identity, never substituted.
  await expect(page).toHaveURL(
    /\/requirements\/[0-9a-f-]{36}\?discipline=software&requirementRevisionId=[0-9a-f-]{36}/,
    { timeout: 30_000 })
  const heading = page.getByRole('heading', { name: /^HLR-\d{6}\.\d{2}$/ })
  await expect(heading).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.requirementInspector')).toContainText('HIGHLEVEL REQUIREMENT')
  const display = (await heading.textContent())!.trim()
  const exactUrl = page.url()

  await page.reload()
  await expect(page).toHaveURL(exactUrl)
  await expect(page.getByRole('heading', { name: display })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.requirementInspector')).toContainText('HIGHLEVEL REQUIREMENT')
})

test('an exact requirement deep link fails closed instead of substituting the latest revision', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Procedure Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Find a procedure').fill('SYSTP-000001')
  const row = page.locator('.procedureRow').filter({ hasText: 'SYSTP-000001' }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()
  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await inspector.getByRole('button', { name: 'Trace & impact' }).click()
  const rows = inspector.locator('.traceRequirement')
  await expect(rows).toHaveCount(2, { timeout: 30_000 })
  const revisionIds = await rows.evaluateAll(nodes =>
    nodes.map(node => node.textContent?.match(/Revision ([0-9a-f-]{36})/i)?.[1] ?? ''))
  expect(revisionIds[0]).toMatch(/^[0-9a-f-]{36}$/)
  expect(revisionIds[1]).toMatch(/^[0-9a-f-]{36}$/)

  await rows.first().getByRole('button', { name: /Open requirement/ }).click()
  await expect(page).toHaveURL(
    /\/requirements\/[0-9a-f-]{36}\?discipline=system&requirementRevisionId=[0-9a-f-]{36}/,
    { timeout: 30_000 })
  const validUrl = new URL(page.url())
  const artifactId = validUrl.pathname.split('/').pop()!
  expect(artifactId).toMatch(/^[0-9a-f-]{36}$/)
  const exactHeading = page.getByRole('heading', { name: /^SYSR-\d{6}\.\d{2}$/ })
  await expect(exactHeading).toBeVisible({ timeout: 30_000 })

  // A revision belonging to a different Requirement artifact must not silently open that artifact's latest.
  const mismatched = new URL(validUrl)
  mismatched.searchParams.set('requirementRevisionId', revisionIds[1])
  await page.goto(mismatched.toString())
  await expect(page.locator('.deepLinkUnavailable')).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.requirementInspector h2')).toHaveCount(0)

  // A revision that does not exist at all must not fall back to the latest either.
  const nonexistent = new URL(validUrl)
  nonexistent.searchParams.set('requirementRevisionId', '00000000-0000-0000-0000-000000000000')
  await page.goto(nonexistent.toString())
  await expect(page.locator('.deepLinkUnavailable')).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.requirementInspector h2')).toHaveCount(0)

  // The build context stays intact in both failure states: same Program, Project and Release route.
  expect(mismatched.pathname).toMatch(/^\/programs\/[^/]+\/projects\/[^/]+\/releases\/[^/]+\/requirements\//)
  expect(nonexistent.pathname).toMatch(/^\/programs\/[^/]+\/projects\/[^/]+\/releases\/[^/]+\/requirements\//)
})
