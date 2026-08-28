import { expect, test } from '@playwright/test'
import { login, openNavigationGroup, showcaseSeed } from './auth'

test('Verification landing Coverage opens the exact Explorer report and survives reload, Back, and Forward', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  await login(page)

  await page.goto(`${root}/system-verification`)
  await expect(page.getByRole('heading', { name: 'Verification', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Open Coverage →' }).click()
  await expect(page).toHaveURL(/system-verification\/procedures\?coverage=report$/)
  await expect(page.getByRole('heading', { name: 'System Test Procedure Explorer', exact: true })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('System Coverage')
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()

  await page.goBack()
  await expect(page.getByRole('heading', { name: 'Verification', exact: true })).toBeVisible()
  await page.goForward()
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('System Coverage')
  const systemNavigation = page.getByRole('navigation', { name: 'Primary navigation' })
  await expect(systemNavigation.getByRole('link', { name: 'Coverage', exact: true })).toHaveAttribute('aria-current', 'page')
  await expect(systemNavigation.getByRole('link', { name: 'System Test Procedure Explorer' })).not.toHaveAttribute('aria-current', 'page')
  await page.getByRole('button', { name: 'Advanced', exact: true }).click()
  await expect(page).toHaveURL(/system-verification\/procedures$/)
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Advanced', exact: true }).click()
  await expect(page).toHaveURL(/system-verification\/procedures\?coverage=report$/)
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()

  await page.goto(`${root}/software-verification`)
  const hlr = page.getByRole('region', { name: 'Software HLR' })
  await hlr.getByRole('button', { name: 'Open Coverage →' }).click()
  await expect(page).toHaveURL(/software-verification\/test-artifacts\?coverage=report&artifactLevel=HighLevel&artifactKind=Case$/)
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer', exact: true })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('Software HLR Coverage')
  await expect(page.getByLabel('Level filter')).toHaveValue('HighLevel')
  await expect(page.getByLabel('Artifact filter')).toHaveValue('Case')
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()

  await page.reload()
  await expect(page).toHaveURL(/software-verification\/test-artifacts\?coverage=report&artifactLevel=HighLevel&artifactKind=Case$/)
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('Software HLR Coverage')
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()
  await page.goBack()
  await expect(page.getByRole('heading', { name: 'Verification', exact: true })).toBeVisible()
  await page.goForward()
  await expect(page.getByLabel('Level filter')).toHaveValue('HighLevel')
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('Software HLR Coverage')
  await page.goBack()
  const llr = page.getByRole('region', { name: 'Software LLR' })
  await llr.getByRole('button', { name: 'Open Coverage →' }).click()
  await expect(page).toHaveURL(/software-verification\/test-artifacts\?coverage=report&artifactLevel=LowLevel&artifactKind=Case$/)
  await expect(page.getByLabel('Level filter')).toHaveValue('LowLevel')
  await expect(page.getByLabel('Artifact filter')).toHaveValue('Case')
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' })).toContainText('Software LLR Coverage')
})

test('Coverage waits for an authoritative response before showing counts', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  let releaseCoverage = () => {}
  const held = new Promise<void>(resolve => { releaseCoverage = resolve })
  let markRequested = () => {}
  const requested = new Promise<void>(resolve => { markRequested = resolve })
  await page.route('**/api/verification-coverage?*', async route => {
    const response = await route.fetch()
    markRequested()
    await held
    await route.fulfill({ response })
  })
  await login(page)

  await page.goto(`${root}/system-verification`)
  await page.getByRole('button', { name: 'Open Coverage →' }).click()
  await requested
  await expect(page.getByRole('status')).toHaveText('Loading requirement coverage for this build.')
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toHaveCount(0)
  await expect(page.getByRole('heading', { name: 'Requirement coverage', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Show all 0 requirements', exact: true })).toHaveCount(0)

  releaseCoverage()
  await expect(page.getByRole('status')).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Show all [1-9]\d* requirements$/ })).toBeVisible()
})

test('Verification sidebar exposes Coverage for each supported scope and marks that destination active', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  await login(page)

  await page.goto(`${root}/system-verification`)
  await openNavigationGroup(page, 'VERIFICATION')
  const navigation = page.getByRole('navigation', { name: 'Primary navigation' })
  await expect(navigation.getByRole('link', { name: 'Coverage', exact: true })).toHaveAttribute('href', /system-verification\/procedures\?coverage=report$/)
  await navigation.getByRole('link', { name: 'Coverage', exact: true }).click()
  await expect(navigation.getByRole('link', { name: 'Coverage', exact: true })).toHaveAttribute('aria-current', 'page')
  await expect(navigation.getByRole('link', { name: 'System Test Procedure Explorer' })).not.toHaveAttribute('aria-current', 'page')

  await page.goto(`${root}/software-verification`)
  await openNavigationGroup(page, 'VERIFICATION')
  await navigation.getByRole('group', { name: 'Verification scope' }).getByRole('button', { name: 'Software' }).click()
  await expect(navigation.getByRole('link', { name: 'HLR Coverage' })).toHaveAttribute('href', /artifactLevel=HighLevel&artifactKind=Case$/)
  await expect(navigation.getByRole('link', { name: 'LLR Coverage' })).toHaveAttribute('href', /artifactLevel=LowLevel&artifactKind=Case$/)
  await navigation.getByRole('link', { name: 'LLR Coverage' }).click()
  await expect(navigation.getByRole('link', { name: 'LLR Coverage' })).toHaveAttribute('aria-current', 'page')
  await expect(navigation.getByRole('link', { name: 'Test Case/Procedure Explorer' })).not.toHaveAttribute('aria-current', 'page')
})

test('Coverage navigation omits a software level without the Verification capability', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  await page.route('**/api/projects/*/configuration', async route => {
    const response = await route.fetch()
    const configuration = await response.json()
    configuration.effectiveSteps = configuration.effectiveSteps.map((step: { catalogueEntry: string; capabilities: number }) => ({
      ...step,
      capabilities: step.catalogueEntry === 'LowLevel' ? 13 : step.capabilities,
    }))
    await route.fulfill({ response, json: configuration })
  })
  await login(page)

  await page.goto(`${root}/software-verification`)
  await openNavigationGroup(page, 'VERIFICATION')
  const navigation = page.getByRole('navigation', { name: 'Primary navigation' })
  await navigation.getByRole('group', { name: 'Verification scope' }).getByRole('button', { name: 'Software' }).click()
  await expect(navigation.getByRole('link', { name: 'HLR Coverage' })).toBeVisible()
  await expect(navigation.getByRole('link', { name: 'LLR Coverage' })).toHaveCount(0)
})
