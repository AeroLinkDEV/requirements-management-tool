import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, showcaseSeed } from './auth'

test('Command Center renders the build-scoped software verification population', async ({ page, request }) => {
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const dashboardResponse = await request.get(
    `${apiBase}/api/dashboard?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`,
  )
  expect(dashboardResponse.ok(), await dashboardResponse.text()).toBeTruthy()
  const dashboard = await dashboardResponse.json()
  const impactResponse = await request.get(`${apiBase}/api/releases/${showcase.activeReleaseId}/verification-impact`)
  expect(impactResponse.ok(), await impactResponse.text()).toBeTruthy()
  const currentImpacts = (await impactResponse.json()).filter((item: { state: string }) => item.state !== 'Superseded')
  expect(currentImpacts.some((item: { subjectDisplayNumber: string }) => item.subjectDisplayNumber.startsWith('HLR-'))).toBe(true)

  await login(page)
  const rows = page.locator('.verificationTriageRows article')
  const verificationSummaries = [
    ['System', dashboard.verification.system],
    ['Software HLR', dashboard.verification.hlr],
    ['Software LLR', dashboard.verification.llr],
  ] as const
  const expectedRows = verificationSummaries.filter(([, summary]) =>
    Object.values(summary).some(value => value !== 0),
  )
  await expect(rows).toHaveCount(expectedRows.length)
  expect(await rows.locator('b').allTextContents()).toEqual(expectedRows.map(([label]) => label))
  for (const [index, [, summary]] of expectedRows.entries()) {
    await expect(rows.nth(index)).toContainText(`${summary.triagedChangeRequests} of ${summary.totalChangeRequests} change requests triaged`)
    await expect(rows.nth(index).locator('strong')).toHaveText(String(summary.openDecisions))
    await expect(rows.nth(index)).toContainText(`${summary.resolvedDecisions} resolved`)
  }
})

test('FMS selection opens the ordered, accessible Software Builds lineage', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()

  await expect(page).toHaveURL(/\/projects\/fms-product-development\/builds$/)
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await expect(page.getByText('Select a build to explore or work on.')).toBeVisible()

  const cards = page.locator('[data-build-card]')
  await expect(cards).toHaveCount(5)
  expect(await cards.evaluateAll(items => items.map(item => item.getAttribute('data-build-version'))))
    .toEqual(['0.5', '1.0', '1.5', '1.6', 'next'])
  await expect(page.getByText('Released', { exact: true })).toHaveCount(3)
  await expect(cards.filter({ hasText: '1.6' }).getByText('In Work', { exact: true })).toBeVisible()
  await expect(cards.filter({ hasText: '1.6' })).toHaveClass(/current/)
  const plan = cards.filter({ hasText: 'Plan next build' })
  await expect(plan).toContainText('Future-build placeholder')
  await expect(plan).not.toContainText('Build 1.7')
  await expect(page.getByRole('button', { name: 'Plan next build placeholder' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Plan next build placeholder' })).toHaveAttribute('title', 'No future build record has been created')

  await expect(page.getByRole('button', { name: 'Open build 0.5' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Open build 1.0' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Open build 0.5' })).toHaveAttribute('title', 'Controlled workspace not available')
  await expect(page.getByRole('button', { name: 'Open build 1.0' })).toHaveAttribute('title', 'Controlled workspace not available')
  await expect(page.getByText(/shown for lineage only|controlled workspace is not available/)).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Open build 1.5' })).toBeEnabled()
  await expect(page.getByRole('button', { name: 'Open build 1.6' })).toBeEnabled()

  const selectorUrl = page.url()
  await page.getByRole('button', { name: 'Open build 0.5' }).click({ force: true })
  await expect(page).toHaveURL(selectorUrl)
  await expect(page.getByText(/requirement totals|traceability percentage|verification percentage/i)).toHaveCount(0)
  await expect(page.getByText('Recent Activity', { exact: true })).toHaveCount(0)

  if (process.env.AEROLINK_BUILDS_SCREENSHOT)
    await page.screenshot({ path: process.env.AEROLINK_BUILDS_SCREENSHOT, fullPage: true })
})

test('released Build 1.5 is a durable read-only workspace and exits explicitly', async ({ page }) => {
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()

  await expect(page).toHaveURL(/\/releases\/[^/]+\/command-center$/)
  await expect(page.getByLabel('Active build 1.5')).toContainText('Released · read-only')
  await expect(page.locator('.releaseSelector')).toHaveCount(0)
  await expect(page.getByRole('link', { name: /New .*Change Request/ })).toHaveCount(0)
  const build15Url = page.url()
  await page.reload()
  await expect(page).toHaveURL(build15Url)
  await expect(page.getByLabel('Active build 1.5')).toBeVisible()

  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()
  await page.getByLabel('Search requirements').fill('SYSR-000001')
  await page.getByRole('button', { name: /SYSR-000001\.00/ }).first().click()
  await page.getByRole('button', { name: 'Trace & impact' }).click()
  // Opening a procedure named on a requirement's trace lands in the Test Procedure Explorer, which is where a
  // procedure is read. It used to open a record dialog on the coverage page, which carried a procedure
  // library; the library moved and the link followed it rather than being left pointing at nothing.
  await page.getByRole('button', { name: /SYSTP-000001\.00.*Open procedure/ }).click()
  await expect(page.getByRole('heading', { name: 'Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })
  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await expect(inspector).toContainText('SYSTP-000001.00')
  await expect(inspector).toContainText('Objective')
  // Released, so nothing here writes: the procedure is readable and there is no editor to reach.
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toHaveCount(0)
  await inspector.getByRole('button', { name: 'History' }).click()
  await expect(inspector.locator('.revisionList li').first()).toContainText('SYSTP-000001')
  const procedureUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(procedureUrl)
  await expect(page.locator('.requirementInspector')).toBeVisible({ timeout: 30_000 })

  const refusal = await page.evaluate(async (base) => {
    const response = await fetch(`${base}/api/change-request-drafts`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: '{}',
    })
    return { status: response.status, body: await response.json() }
  }, apiBase)
  expect(refusal.status).toBe(409)
  expect(refusal.body.code).toBe('released_build_read_only')

  if (process.env.AEROLINK_BUILD_15_SCREENSHOT) {
    await expect(page.locator('.dashboardSkeleton')).toHaveCount(0)
    await page.screenshot({ path: process.env.AEROLINK_BUILD_15_SCREENSHOT, fullPage: true })
  }

  await page.locator('.requirementInspector').getByRole('button', { name: 'Close procedure detail' }).click()
  await page.getByRole('button', { name: 'Back to Software Builds' }).click()
  await expect(page).toHaveURL(/\/projects\/fms-product-development\/builds$/)
  await page.getByRole('button', { name: 'Open build 1.6' }).click()
  await expect(page.getByLabel('Active build 1.6')).toContainText('In work')
})

test('Build 1.6 keeps editing capability, scopes search, and labels predecessor evidence', async ({ page }) => {
  await login(page)
  await expect(page.getByLabel('Active build 1.6')).toContainText('In work')
  await expect(page.locator('.releaseSelector')).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'New System SRCR' })).toHaveCount(0)
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Change Requests' }).click()
  await expect(page.getByRole('button', { name: '+ New System Change Request' })).toBeVisible()

  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()
  await expect(page.getByRole('heading', { name: 'System Requirements Explorer' })).toBeVisible()
  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.getByText('Historical version — Build 1.5').first()).toBeVisible()
  await expect(page.getByLabel('Active build 1.6')).toBeVisible()

  if (process.env.AEROLINK_BUILD_16_SCREENSHOT)
    await page.screenshot({ path: process.env.AEROLINK_BUILD_16_SCREENSHOT, fullPage: true })
})

test('an authenticated invalid build deep link stays authenticated and shows not found', async ({ page }) => {
  await login(page)
  const validUrl = new URL(page.url())
  const parts = validUrl.pathname.split('/')
  const releaseIndex = parts.indexOf('releases') + 1
  parts[releaseIndex] = '00000000-0000-0000-0000-000000000000'

  await page.goto(parts.join('/'))

  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
  await expect(page.getByLabel('Username')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Return to Command Center' })).toBeVisible()
})

test('the build lineage stacks without horizontal scrolling on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
    columns: new Set([...document.querySelectorAll('[data-build-card]')].map(card =>
      Math.round(card.getBoundingClientRect().left))).size,
  }))
  expect(dimensions.documentWidth).toBe(dimensions.viewportWidth)
  expect(dimensions.columns).toBe(1)
})
