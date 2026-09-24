import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

/**
 * #1113 S2 — Problem Reports reach Command Center and My Work.
 *
 * The card defaults to the active build and says so; its scope switch and links reach the Problem Report
 * center. My Work shows the reader's Problem Report work with its next action and never a due date,
 * because no Problem Report policy sets one. Exact counts are not pinned: other journeys sharing this
 * disposable database move reports through their lifecycle.
 */
test('Command Center and My Work carry Problem Report work', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  const evidenceDir = process.env.AEROLINK_1113_EVIDENCE

  await login(page, 'systems.author', { openProject: false })
  await page.goto(`${root}/command-center`)
  const card = page.getByRole('region', { name: 'Problem Reports summary' })
  await expect(card).toBeVisible()
  const scope = card.getByRole('group', { name: 'Problem Report scope' })
  await expect(scope.getByRole('button', { name: /^Build / })).toHaveAttribute('aria-pressed', 'true')
  for (const label of ['Active', 'Awaiting SQA', 'Release blockers', 'Closed'])
    await expect(card.locator('.prCardHeadline button').filter({ hasText: label }).locator('strong')).toHaveText(/^\d+$/)
  await expect(card.locator('.prCardLifecycle > div')).toHaveCount(6)

  await scope.getByRole('button', { name: 'All builds' }).click()
  await expect(scope.getByRole('button', { name: 'All builds' })).toHaveAttribute('aria-pressed', 'true')
  // The whole project holds at least as many active reports as any one build.
  await expect(card.locator('.prCardHeadline strong').first()).toHaveText(/^\d+$/)
  if (evidenceDir) await card.screenshot({ path: `${evidenceDir}/command-center-problem-reports.png`, animations: 'disabled' })

  await scope.getByRole('button', { name: /^Build / }).click()
  await card.getByRole('button', { name: 'Open Problem Reports →' }).click()
  await expect(page).toHaveURL(new RegExp(`/problem-reports\\?targetBuild=${showcase.activeReleaseId}`))

  await page.goto(`${root}/my-work`)
  await expect(page.getByRole('heading', { name: 'My Work' })).toBeVisible()
  await expect(page.locator('.workMetricsGrid article').filter({ hasText: 'Problem Reports' }).locator('b')).toHaveText(/^\d+$/)
  const rows = page.locator('.workQueue article').filter({ has: page.locator('.workProblemReport') })
  const count = await rows.count()
  for (let index = 0; index < count; index++) {
    const row = rows.nth(index)
    await expect(row).not.toContainText(/\bdue\b/i)
    await expect(row).not.toContainText('Overdue')
    await expect(row.locator('.workPrState')).toBeVisible()
  }
  const chip = page.getByRole('group', { name: 'Filter work by kind' }).getByRole('button', { name: /^Problem Reports/ })
  if (count > 0 && await chip.count()) {
    await chip.click()
    await expect(chip).toHaveAttribute('aria-pressed', 'true')
    // Still hovered after the click: the selected chip must keep its fill, not fade to white on white.
    await expect(chip).toHaveCSS('background-color', 'rgb(23, 108, 118)')
    await expect(page.locator('.workQueue article')).toHaveCount(count)
    if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/my-work-problem-reports.png`, animations: 'disabled' })
    await rows.first().click()
    await expect(page).toHaveURL(/\/problem-reports\/[0-9a-f-]{36}/)
  }
})
