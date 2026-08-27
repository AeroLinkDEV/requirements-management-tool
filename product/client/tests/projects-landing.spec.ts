import { expect, test } from '@playwright/test'
import { login } from './auth'

test('successful login opens the accessible Projects selector before the current workspace', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })

  await expect(page).toHaveURL(/\/projects$/)
  await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible()
  await expect(page.getByText('Select a project to continue.')).toBeVisible()

  const cards = page.locator('[data-project-card]')
  await expect(cards).toHaveCount(3)
  await expect(page.getByRole('link', { name: 'Open FMS Product Development' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Open DOORS Import Practice' })).toBeVisible()
  const sampleProjects = page.locator('details.sampleProjectsSection')
  await expect(sampleProjects).not.toHaveAttribute('open', '')
  await expect(sampleProjects.getByText('GPS Receiver Modernization')).not.toBeVisible()
  await sampleProjects.getByRole('button', { name: /Sample projects/ }).click()
  await expect(sampleProjects).toHaveAttribute('open', '')
  await expect(sampleProjects.getByText('GPS Receiver Modernization')).toBeVisible()
  await expect(cards).toHaveCount(12)
  if (process.env.AEROLINK_PROJECTS_SCREENSHOT) {
    await page.screenshot({ path: process.env.AEROLINK_PROJECTS_SCREENSHOT, fullPage: true })
  }

  const active = page.getByRole('link', { name: 'Open FMS Product Development' })
  await expect(active.getByText('Active', { exact: true })).toBeVisible()
  await expect(active).toContainText('Opens your current workspace.')
  await active.focus()
  await expect(active).toBeFocused()

  const mock = cards.filter({ hasText: 'GPS Receiver Modernization' })
  await expect(mock).toHaveAttribute('aria-disabled', 'true')
  await expect(mock.locator('a, button')).toHaveCount(0)
  await expect(mock).not.toHaveAttribute('tabindex')
  const selectorUrl = page.url()
  await mock.click()
  await expect(page).toHaveURL(selectorUrl)

  const create = cards.filter({ hasText: 'Create New Project' })
  await expect(create).toHaveAttribute('aria-disabled', 'true')
  await expect(create.locator('a, button')).toHaveCount(0)
  await create.click()
  await expect(page).toHaveURL(selectorUrl)

  await active.press('Enter')
  await expect(page).toHaveURL(/\/projects\/fms-product-development\/builds$/)
  await expect(page.getByRole('heading', { name: 'Software Builds' })).toBeVisible()
})

test('the project grid collapses cleanly without horizontal scrolling', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await login(page, 'admin', { openProject: false })

  await expect(page.locator('[data-project-card]')).toHaveCount(3)
  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
    columns: new Set(
      [...document.querySelectorAll('[data-project-card]')].map(card =>
        Math.round(card.getBoundingClientRect().left),
      ),
    ).size,
  }))
  expect(dimensions.documentWidth).toBe(dimensions.viewportWidth)
  expect(dimensions.columns).toBe(1)
  const sampleProjects = page.locator('details.sampleProjectsSection')
  await sampleProjects.getByRole('button', { name: /Sample projects/ }).click()
  await expect(page.locator('[data-project-card]')).toHaveCount(12)
  if (process.env.AEROLINK_PROJECTS_MOBILE_SCREENSHOT) {
    await page.screenshot({ path: process.env.AEROLINK_PROJECTS_MOBILE_SCREENSHOT, fullPage: true })
  }
})

test('the project and build selectors survive refresh before entering a build-specific deep route', async ({ page }) => {
  await login(page, 'admin', { openProject: false })
  await page.reload()

  await expect(page).toHaveURL(/\/projects$/)
  await expect(page.getByRole('heading', { name: 'Projects' })).toBeVisible()
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page).toHaveURL(/\/projects\/fms-product-development\/builds$/)
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Software Builds' })).toBeVisible()

  await page.getByRole('button', { name: 'Open build 1.6' }).click()
  const workspaceUrl = page.url()
  await page.reload()
  await expect(page).toHaveURL(workspaceUrl)
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()
})
