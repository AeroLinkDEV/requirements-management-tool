import { expect, test } from '@playwright/test'
import { login } from './auth'

/**
 * What each artifact requires before release, resolved against the roster.
 *
 * The page's whole claim is that it answers "can this actually be signed" before somebody submits work and
 * discovers it cannot. These assert the artifact types by name and count, and that every stage reports a
 * required position — a page that renders an empty table would satisfy a markup-only assertion.
 */

const openConfiguration = async (page: import('@playwright/test').Page) => {
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Approval configuration' }).click()
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()
}

test('Approval configuration is reached beside Software Builds and survives a reload', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openConfiguration(page)

  await expect(page).toHaveURL(/\/projects\/fms-product-development\/approval-configuration$/)
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Approval configuration', level: 1 })).toBeVisible()
  await expect(page).toHaveURL(/\/projects\/fms-product-development\/approval-configuration$/)
})

test('Every artifact type a procedure can govern is listed, configured or not', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openConfiguration(page)

  const artifacts = page.locator('[data-artifact]')
  await expect(artifacts.first()).toBeVisible()
  await expect(artifacts).toHaveCount(5)
  expect(await artifacts.evaluateAll(items => items.map(item => item.getAttribute('data-artifact'))))
    .toEqual(['System', 'Software', 'SystemTest', 'HighLevelSoftwareTest', 'LowLevelSoftwareTest'])

  // Documents are deliberately absent: their reviewers are chosen per document by the author.
  await expect(page.getByText('Controlled documents are absent on purpose', { exact: false })).toBeVisible()
})

test('Selecting an artifact shows either its stages or why it has none', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await openConfiguration(page)

  for (const subject of ['System', 'Software']) {
    await page.locator(`[data-artifact="${subject}"]`).click()
    const stages = page.locator('[data-stage]')
    const unconfigured = page.getByText('No procedure is recorded', { exact: false })
    // One of the two must be true, and the page must say which rather than showing an empty table.
    await expect(stages.first().or(unconfigured)).toBeVisible({ timeout: 15_000 })

    if (await stages.count()) {
      // Every stage names what it requires and what a signature on it means.
      await expect(page.getByRole('columnheader', { name: 'Required position' })).toBeVisible()
      await expect(page.getByRole('columnheader', { name: 'Who can sign today' })).toBeVisible()
      const rows = await stages.count()
      for (let index = 0; index < rows; index++) {
        await expect(stages.nth(index)).not.toBeEmpty()
      }
    }
  }
})
