import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

/**
 * #925 P3 — the My Work metric row is compact and states its scope once.
 *
 * The four metrics are server-authoritative and all stay. What changed is the repetition: the scope
 * line used to be printed inside three of the four cards, and the cards carried enough padding to
 * measure 132.6px tall at every supported width. The row is now a single scope line above four compact
 * cards, measured here against the whole populated page rather than an isolated tile.
 */

test('the metric row states the scope once and keeps all four metrics', async ({ page, request }) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`

  await login(page, 'software.author', { openProject: false })
  await page.goto(`${root}/my-work`)
  await expect(page.getByRole('heading', { name: 'My Work' })).toBeVisible()

  const metrics = page.locator('.workMetrics')
  // The scope is stated once for the whole row.
  await expect(metrics.getByText('Current program scope')).toHaveCount(1)
  // All four server-authoritative metrics remain.
  const cards = page.locator('.workMetricsGrid article')
  await expect(cards).toHaveCount(4)
  await expect(cards.filter({ hasText: 'Assigned to me' })).toContainText('4')
  await expect(cards.filter({ hasText: 'Drafts I own' })).toContainText('4')

  // The compacted card row: measured, not assumed. The pre-correction row measured 132.6px tall at
  // every supported width.
  const gridHeight = await page.locator('.workMetricsGrid').evaluate(
    element => element.getBoundingClientRect().height)
  expect(gridHeight).toBeGreaterThan(0)
  expect(gridHeight).toBeLessThan(120)
  // The exact measurement travels with the report, so the before/after comparison cites a number.
  await test.info().attach('metric-row-height-px', { body: String(gridHeight), contentType: 'text/plain' })
  if (process.env.AEROLINK_C6_EVIDENCE) {
    await page.screenshot({ path: `${process.env.AEROLINK_C6_EVIDENCE}/my-work-metrics.png`, fullPage: false })
  }
})
