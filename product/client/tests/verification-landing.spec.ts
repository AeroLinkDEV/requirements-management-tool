import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from './auth'

/**
 * Verification is a fork, not a workspace.
 *
 * It used to be one page with four tabs, and which tab held an answer was something a reader had to know
 * before they could ask. The two questions people actually arrive with — "what is tested, and what has nobody
 * picked up?" and "what did we run, and what happened?" — are now two pages, and this is the choice between
 * them. Nothing is computed on the chooser on purpose: waiting on counts would make the reader wait to be
 * shown two links they were always going to be shown.
 */
test('verification offers the two pages by name, and both open on real work', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(`${page.url().replace(/\/command-center$/,'')}/system-verification`)
  await expect(page.getByRole('heading', { name: 'Verification' })).toBeVisible({ timeout: 30_000 })

  const cards = page.locator('.landingCards button')
  await expect(cards).toHaveCount(3)
  await expect(cards.nth(0)).toContainText('Change Requests')
  await expect(cards.nth(1)).toContainText('Downstream Assessments')
  await expect(cards.nth(2)).toContainText('Test Results')

  // The register and the assessments queue are two pages now, and the landing offers both by name.
  await cards.nth(0).click()
  await expect(page.getByRole('heading', { name: 'System Test Change Requests' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/system-verification/change-requests')

  await page.goBack()
  await page.locator('.landingCards button').nth(1).click()
  await expect(page.getByRole('heading', { name: 'Downstream Assessments' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/system-verification/coverage')

  // The queue is above the inventory, because a reader arriving to do work needs what nobody has picked up
  // before they need the wall of everything. Asserted rather than assumed: a check that passes by finding
  // nothing to look at is worse than no check, and this suite has already produced one of those.
  await expect(page.getByRole('heading', { name: 'Downstream Assessments' })).toBeVisible()
  // Waited for rather than counted: count() does not retry, so reading it the moment the heading paints
  // reports zero packages on a build that has them — which is indistinguishable from a genuinely empty queue.
  const packages = page.locator('.downstreamAssessment').filter({ hasText: /TCR-/ })
  await expect(packages.first(), 'FMSLIVE should carry test change work for the in-work build').toBeVisible({ timeout: 30_000 })
  // No procedure inventory underneath it. The page is the change requests controlling test work; the
  // procedures they produce are browsed in the Test Procedure Explorer.
  await expect(page.getByRole('heading', { name: 'Test procedures' })).toHaveCount(0)

  await page.goBack()
  await expect(page.getByRole('heading', { name: 'Verification' })).toBeVisible({ timeout: 30_000 })
  await page.locator('.landingCards button').nth(2).click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/system-verification/results')
})

/**
 * Software is four destinations rather than two with a switch on them, because HLR and LLR test work is
 * planned, done and approved by different people.
 */
test('software verification offers an HLR pair and an LLR pair', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(`${page.url().replace(/\/command-center$/,'')}/software-verification`)
  await expect(page.getByRole('heading', { name: 'Verification' })).toBeVisible({ timeout: 30_000 })

  await expect(page.locator('.landingCards button')).toHaveCount(6)
  const llr = page.locator('section').filter({ hasText: 'Software LLR' }).last()
  await llr.getByRole('button', { name: /Open Test Results/ }).click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  expect(page.url()).toContain('/software-verification/llr/results')
  await expect(page.getByText('VERIFICATION / SOFTWARE LLR')).toBeVisible()
})
