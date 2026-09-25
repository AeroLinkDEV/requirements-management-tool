import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

/**
 * #1113 S1 — a project shows only the features it has switched on.
 *
 * The shared showcase project holds records in every feature, so the server will never let it switch one
 * off, and switching one off here would change other journeys' world. The gating journey therefore
 * answers the features read with the owner's target shape (Team Work and Problem Reports only); the server
 * rules themselves are covered by ProjectFeatureApiTests. The Features panel is read for real.
 */
test('a Problem Reports-only project hides every other feature everywhere', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  const evidenceDir = process.env.AEROLINK_1113_EVIDENCE
  await page.route(`**/api/projects/${showcase.projectId}/features`, route => route.request().method() === 'GET'
    ? route.fulfill({ json: { persisted: true, version: 1, canManage: true, enabled: ['TeamWork', 'ProblemReports'], features: [], history: [] } })
    : route.continue())

  await login(page, 'admin', { openProject: false })
  await page.goto(`${root}/command-center`)
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()
  const nav = page.getByRole('navigation', { name: 'Primary navigation' })
  for (const visible of ['Command Center', 'My Work', 'Team Work', 'Problem Reports'])
    await expect(nav.getByRole('link', { name: visible, exact: true })).toBeVisible()
  for (const hidden of ['REQUIREMENTS', 'VERIFICATION', 'CODE', 'RELEASE'])
    await expect(nav.locator('summary', { hasText: hidden })).toHaveCount(0)
  for (const hidden of ['Documentation Center', 'Digital Thread'])
    await expect(nav.getByRole('link', { name: hidden })).toHaveCount(0)
  // Administration is not a feature.
  await expect(nav.locator('summary', { hasText: 'ADMINISTRATION' })).toHaveCount(1)

  await expect(page.locator('.dashboardAreaCard.system, .dashboardAreaCard.software, .dashboardAreaCard.verification')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Release Readiness →' })).toHaveCount(0)
  if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/problem-reports-only-command-center.png`, animations: 'disabled' })

  await page.getByRole('button', { name: /Search & navigate/ }).click()
  const palette = page.getByRole('dialog', { name: 'Quick navigation' })
  await palette.getByLabel('Search AeroLink').fill('problem')
  await expect(palette.getByRole('link', { name: /^Problem Reports/ }).first()).toBeVisible()
  await palette.getByLabel('Search AeroLink').fill('re')
  await expect(palette.getByRole('link', { name: /Release Readiness/ })).toHaveCount(0)
  await expect(palette.getByRole('link', { name: /Requirements Explorer/ })).toHaveCount(0)
  await page.keyboard.press('Escape')

  // A deep link into a switched-off feature explains itself instead of opening it.
  await page.goto(`${root}/release-readiness`)
  await expect(page.getByRole('heading', { name: 'Not enabled in this project' })).toBeVisible()
  await page.goto(`${root}/team-work`)
  await expect(page.getByRole('heading', { name: 'Not enabled in this project' })).toHaveCount(0)

  // The real panel: the showcase holds records everywhere, so every feature is locked on.
  await page.unroute(`**/api/projects/${showcase.projectId}/features`)
  await page.goto(`/projects/${showcase.projectId}/configuration`)
  await page.getByRole('button', { name: /^Features/ }).click()
  await expect(page.getByRole('heading', { name: 'Features', exact: true })).toBeVisible()
  const problemReports = page.getByRole('checkbox', { name: 'Problem Reports' })
  await expect(problemReports).toBeChecked()
  await expect(problemReports).toBeDisabled()
  await expect(page.getByText('Holds records, so it stays on.').first()).toBeVisible()
  if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/features-panel.png`, animations: 'disabled' })
})
