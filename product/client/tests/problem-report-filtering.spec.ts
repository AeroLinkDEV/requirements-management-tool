import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

/**
 * Finding one report among many.
 *
 * The queue used to need a button pressed before it would answer, which made looking something up feel like
 * filling in a form. The search box now asks as it is typed into, and nothing on the page tells anybody to
 * refresh it.
 */
test('the queue filters as the search is typed, with no refresh to press', async ({ page, request }) => {
  test.setTimeout(120_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  // Two reports, so narrowing to one proves something. With a single record in the queue a filter that did
  // nothing at all would still leave exactly one row and the journey would pass.
  const distinctive = `Vestibule${Date.now()}`
  for (const title of [`${distinctive} annunciation defect`, `Unrelated defect ${Date.now()}`]) {
    const raised = await request.post(`${apiBase}/api/problem-reports`, {
      data: {
        category: 'CodeFunctional', projectId: showcase.projectId,
        releaseId: showcase.activeReleaseId,
        title,
        problem: 'Raised so this journey owns the records it filters.',
      },
    })
    expect(raised.ok(), await raised.text()).toBeTruthy()
  }

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.prList button').nth(1)).toBeVisible({ timeout: 30_000 })
  const unfiltered = await page.locator('.prList button').count()
  expect(unfiltered).toBeGreaterThan(1)

  // Nothing to press, and nothing telling the reader the list might be stale.
  await expect(page.getByRole('button', { name: 'Refresh' })).toHaveCount(0)

  await page.getByLabel('Search').fill(distinctive)
  // No click. The queue narrows on its own once typing stops.
  await expect(page.locator('.prList button')).toHaveCount(1, { timeout: 30_000 })
  await expect(page.locator('.prList').getByText(distinctive)).toBeVisible()

  // And clearing it brings the rest back, so the filter is a view rather than a state to get stuck in.
  await page.getByLabel('Search').fill('')
  await expect(page.locator('.prList button')).toHaveCount(unfiltered, { timeout: 30_000 })
})

/**
 * Filtering by what kind of problem it is — the reason the field exists.
 */
test('the queue can be narrowed to one kind of problem', async ({ page, request }) => {
  test.setTimeout(120_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const distinctive = `Category filter ${Date.now()}`
  const codeTitle = `${distinctive} code defect`
  const toolingTitle = `${distinctive} tooling defect`
  for (const [category, title] of [['CodeFunctional', codeTitle], ['EnvironmentTooling', toolingTitle]]) {
    const raised = await request.post(`${apiBase}/api/problem-reports`, { data: {
      category,
      projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title,
      problem: 'Raised so this journey owns one record in each category it filters.',
    } })
    expect(raised.ok(), await raised.text()).toBeTruthy()
  }

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await expect(page.getByRole('heading', { name: 'Problem Report queue' })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.prList button').first()).toBeVisible({ timeout: 30_000 })

  await page.getByLabel('Search').fill(distinctive)
  await page.getByLabel('Category', { exact: true }).selectOption('CodeFunctional')
  await page.getByRole('button', { name: 'Apply filters' }).click()
  await expect(page.locator('.prList button')).toHaveCount(1, { timeout: 30_000 })
  await expect(page.locator('.prList')).toContainText(codeTitle)
  await expect(page.locator('.prList')).not.toContainText(toolingTitle)

  await page.getByLabel('Category', { exact: true }).selectOption('EnvironmentTooling')
  await page.getByRole('button', { name: 'Apply filters' }).click()
  await expect(page.locator('.prList button')).toHaveCount(1, { timeout: 30_000 })
  await expect(page.locator('.prList')).toContainText(toolingTitle)
  await expect(page.locator('.prList')).not.toContainText(codeTitle)

  await page.getByLabel('Search').fill(`${distinctive} missing`)
  await expect(page.locator('.prEmpty')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('No Problem Reports match these filters.')).toBeVisible()
})
