import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from './auth'

/**
 * Verification is a fork, not a workspace.
 *
 * It used to be one page with four tabs, and which tab held an answer was something a reader had to know
 * before they could ask. The independent choices people actually arrive with — controlled test change work,
 * open downstream assessments, requirement coverage, and recorded results — are pages now. Nothing is computed
 * on the chooser on purpose: waiting on counts would make the reader wait to be shown links they were always
 * going to be shown.
 */
test('system verification offers its named destinations, including Coverage, on real work', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(`${page.url().replace(/\/command-center$/,'')}/system-verification`)
  await expect(page.getByRole('heading', { name: 'Verification' })).toBeVisible({ timeout: 30_000 })

  const system = page.getByRole('region', { name: 'System' })
  const changeRequests = system.getByRole('button', { name: /Open Change Requests/ })
  const downstreamAssessments = system.getByRole('button', { name: /Open Downstream Assessments/ })
  const coverage = system.getByRole('button', { name: /Open Coverage/ })
  const testResults = system.getByRole('button', { name: /Open Test Results/ })
  await expect(changeRequests).toBeVisible()
  await expect(downstreamAssessments).toBeVisible()
  await expect(coverage).toBeVisible()
  await expect(testResults).toBeVisible()

  await changeRequests.click()
  await expect(page.getByRole('heading', { name: 'System Test Change Requests' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/system-verification/change-requests')

  await page.goBack()
  await downstreamAssessments.click()
  await expect(page.getByRole('heading', { name: 'Downstream Assessments' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/system-verification/coverage')

  // The queue is above the inventory, because a reader arriving to do work needs what nobody has picked up
  // before they need the wall of everything. Asserted rather than assumed: a check that passes by finding
  // nothing to look at is worse than no check, and this suite has already produced one of those.
  await expect(page.getByRole('heading', { name: 'Downstream Assessments' })).toBeVisible()
  // Waited for rather than counted: count() does not retry, so reading it the moment the heading paints
  // reports zero packages on a build that has them — which is indistinguishable from a genuinely empty queue.
  const packages = page.locator('.downstreamAssessment').filter({ hasText: /SYSTPCR-/ })
  await expect(packages.first(), 'FMSLIVE should carry test change work for the in-work build').toBeVisible({ timeout: 30_000 })
  // No procedure inventory underneath it. The page is the change requests controlling test work; the
  // procedures they produce are browsed in the Test Procedure Explorer.
  await expect(page.getByRole('heading', { name: 'Test procedures' })).toHaveCount(0)

  await page.goBack()
  await expect(page.getByRole('heading', { name: 'Verification' })).toBeVisible({ timeout: 30_000 })
  await coverage.click()
  await expect(page.getByRole('heading', { name: 'System Test Procedure Explorer' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/system-verification/procedures?coverage=report')
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()

  await page.goBack()
  await expect(page.getByRole('heading', { name: 'Verification' })).toBeVisible({ timeout: 30_000 })
  await testResults.click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/system-verification/results')
})

/**
 * Software is eight destinations rather than four with a switch on them, because HLR and LLR test work is
 * planned, done and approved by different people.
 */
test('software verification offers named HLR and LLR destinations, including Coverage', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(`${page.url().replace(/\/command-center$/,'')}/software-verification`)
  await expect(page.getByRole('heading', { name: 'Verification' })).toBeVisible({ timeout: 30_000 })

  const hlr = page.getByRole('region', { name: 'Software HLR' })
  const llr = page.getByRole('region', { name: 'Software LLR' })
  for (const scope of [hlr, llr]) {
    await expect(scope.getByRole('button', { name: /Open Change Requests/ })).toBeVisible()
    await expect(scope.getByRole('button', { name: /Open Downstream Assessments/ })).toBeVisible()
    await expect(scope.getByRole('button', { name: /Open Coverage/ })).toBeVisible()
    await expect(scope.getByRole('button', { name: /Open Test Results/ })).toBeVisible()
  }

  await llr.getByRole('button', { name: /Open Coverage/ }).click()
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/software-verification/test-artifacts?coverage=report&artifactLevel=LowLevel&artifactKind=Case')
  await expect(page.getByRole('region', { name: 'Coverage summary' })).toBeVisible()

  await page.goBack()
  await expect(page.getByRole('heading', { name: 'Verification' })).toBeVisible({ timeout: 30_000 })
  await llr.getByRole('button', { name: /Open Test Results/ }).click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/software-verification/llr/results')
  await expect(page.getByText('VERIFICATION / SOFTWARE LLR')).toBeVisible()
})
