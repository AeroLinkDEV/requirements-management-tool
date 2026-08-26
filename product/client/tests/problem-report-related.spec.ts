import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, selectProgram, showcaseSeed } from './auth'

/**
 * Problem Reports that belong together.
 *
 * The point of recording the relationship is that somebody looking at *either* report finds the other, so
 * this journey links from one and then goes and looks at the other — which is the assertion that a
 * one-sided implementation would fail.
 */
test('relating two Problem Reports records it on both, and unlinking removes both halves', async ({ page, request }) => {
  test.setTimeout(300_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const first = `Related first ${stamp}`
  const second = `Related second ${stamp}`

  const raise = async (title: string) => {
    const created = await request.post(`${apiBase}/api/problem-reports`, { data: {
      category: 'CodeFunctional',
      projectId: showcase.projectId,
      releaseId: showcase.activeReleaseId,
      title,
      problem: 'The disconnect tone follows the disconnect.',
    } })
    expect(created.ok(), await created.text()).toBeTruthy()
    return await created.json() as { id: string; displayNumber: string }
  }
  const one = await raise(first)
  const two = await raise(second)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const open = async (title: string) => {
    await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: 'load' })
    await page.getByLabel('Search').fill(title)
    await page.locator('.prList').getByText(title).click()
    await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  }

  await open(first)
  const panel = page.getByRole('region', { name: 'Related Problem Reports' })
  await expect(panel).toContainText('No other Problem Report has been related to this one.')

  await panel.getByRole('button', { name: 'Link a Problem Report' }).click()
  // Clicked rather than checked: choosing closes the picker, so the checkbox is gone by the time
  // Playwright would verify it stayed ticked.
  await panel.getByRole('checkbox', { name: new RegExp(two.displayNumber.replace('.', '\\.')) }).click()
  await expect(panel.locator('.prRelatedCard')).toHaveCount(1, { timeout: 30_000 })
  await expect(panel.locator('.prRelatedCard')).toContainText(two.displayNumber)
  await expect(panel.locator('.prRelatedCard')).toContainText(second)
  // The other report's live state, because "is it still open?" is the first thing anybody asks.
  await expect(panel.locator('.prRelatedState')).toContainText('Draft')

  // The half that was never asked for: the second report knows too.
  await open(second)
  const other = page.getByRole('region', { name: 'Related Problem Reports' })
  await expect(other.locator('.prRelatedCard')).toHaveCount(1, { timeout: 30_000 })
  await expect(other.locator('.prRelatedCard')).toContainText(one.displayNumber)

  // Opening the related record from the card is the whole reason it is a link and not a label.
  await other.locator('.prRelatedOpen').click()
  await expect(page.getByRole('heading', { name: first })).toBeVisible({ timeout: 30_000 })

  // Removed from this side; gone from the other one too.
  await page.getByRole('region', { name: 'Related Problem Reports' })
    .getByRole('button', { name: `Unlink ${two.displayNumber}` }).click()
  await expect(page.getByRole('region', { name: 'Related Problem Reports' }))
    .toContainText('No other Problem Report has been related to this one.', { timeout: 30_000 })

  await open(second)
  await expect(page.getByRole('region', { name: 'Related Problem Reports' }))
    .toContainText('No other Problem Report has been related to this one.', { timeout: 30_000 })
})
