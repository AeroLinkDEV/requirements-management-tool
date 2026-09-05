import { expect, test } from '@playwright/test'
import { login } from './auth'

// #902: the shell navigation draws its icons from the repository-owned SVG icon system, not from
// Unicode glyphs whose shape depends on OS font fallback. These checks prove the migrated navigation
// at representative desktop widths in both workspace densities: every entry carries a real vector icon,
// the sidebar never overflows, labels/behavior are untouched, and accessible names survive.

async function assertNavigationIcons(page: import('@playwright/test').Page) {
  const nav = page.locator('.primaryNavigation')
  await expect(nav).toBeVisible()

  // Every navigation entry draws an inline SVG icon on the shared viewBox, visible or collapsed.
  const entries = nav.locator('a')
  const entryCount = await entries.count()
  expect(entryCount).toBeGreaterThanOrEqual(5)
  for (let index = 0; index < entryCount; index++) {
    await expect(entries.nth(index).locator('i svg[viewBox="0 0 16 16"]')).toHaveCount(1)
  }

  // The always-visible home entries render their icons inside the fixed slot.
  const homeIcons = nav.locator('.navHome a i svg')
  await expect(homeIcons).toHaveCount(3)
  for (let index = 0; index < 3; index++) {
    const box = await homeIcons.nth(index).boundingBox()
    expect(box).not.toBeNull()
    expect(box!.width).toBeGreaterThan(0)
    expect(box!.width).toBeLessThanOrEqual(19)
  }

  // Expanding a group reveals its entries with their own icons; grouped entries must not clip either.
  await nav.locator('details summary', { hasText: 'REQUIREMENTS' }).click()
  const groupedIcons = nav.locator('a i svg:visible')
  const groupedCount = await groupedIcons.count()
  expect(groupedCount).toBeGreaterThanOrEqual(6)
  for (let index = 0; index < groupedCount; index++) {
    const box = await groupedIcons.nth(index).boundingBox()
    expect(box).not.toBeNull()
    expect(box!.width).toBeGreaterThan(0)
    expect(box!.width).toBeLessThanOrEqual(19)
  }

  // No sideways overflow in the sidebar: an icon must never push the shell wider.
  const overflow = await page.locator('aside.appNavigation')
    .evaluate(element => element.scrollWidth - element.clientWidth)
  expect(overflow).toBeLessThanOrEqual(1)
}

async function useCompactDensity(page: import('@playwright/test').Page) {
  await page.getByRole('button', { name: 'Open workspace display settings' }).click()
  await page.getByRole('button', { name: /Compact More records in view/ }).click()
  await expect(page.getByRole('button', { name: 'Open workspace display settings' })).toContainText('compact density')
  // The display panel overlays the sidebar; the shell closes it on Escape.
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: 'Open workspace display settings' })).toBeVisible()
}

test.describe('navigation icon system', () => {
  for (const width of [1920, 1366]) {
    test(`shell navigation renders SVG icons without clipping at ${width}px wide`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 })
      await login(page, 'admin')
      await assertNavigationIcons(page)
    })
  }

  test('navigation meaning stays on the labels, with icons decorative and behavior unchanged', async ({ page }) => {
    await page.setViewportSize({ width: 1366, height: 900 })
    await login(page, 'admin')

    const nav = page.locator('.primaryNavigation')
    // Icons are decorative and hidden from assistive technology; the readable label carries the meaning.
    const home = nav.getByRole('link', { name: 'Command Center', exact: true })
    await expect(home).toBeVisible()
    await expect(home.locator('i svg')).toHaveAttribute('aria-hidden', 'true')
    await expect(home.locator('i svg')).toHaveAttribute('focusable', 'false')

    // The search affordance keeps its visible text and its own accessible name.
    await expect(page.getByRole('button', { name: /Search & navigate/ })).toBeVisible()

    // Active-state behavior is unchanged: the current page is still marked, still by label.
    await expect(home).toHaveAttribute('aria-current', 'page')

    // Navigation still works: labels, routes and behavior untouched by the icon migration.
    await home.click()
    await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()
  })

  test('the command palette renders the same SVG system for its page destinations', async ({ page }) => {
    await page.setViewportSize({ width: 1366, height: 900 })
    await login(page, 'admin')
    await page.getByRole('button', { name: /Search & navigate/ }).click()
    await page.getByPlaceholder(/Search/i).fill('Test Results')

    const paletteIcons = page.locator('.paletteGroup a i svg[viewBox="0 0 16 16"]')
    await expect(paletteIcons.first()).toBeVisible()

    // Artifact entries keep their controlled-identifier acronyms as text, exactly as the issue excludes
    // controlled identifiers from icon replacement. Search a known seeded requirement and require its
    // identifier prefix to be rendered as text in the acronym slot.
    const search = page.getByPlaceholder(/Search/i)
    await search.fill('SYSR-000001')
    await expect(page.locator('.paletteGroup a i.kind').first()).toHaveText(/SYSR/)
  })

  test('a pre-migration recent destination renders the canonical SVG, not its stored glyph', async ({ page }) => {
    await page.setViewportSize({ width: 1366, height: 900 })
    await login(page, 'admin')

    // A recent destination saved by the pre-#902 palette carries a Unicode glyph in its icon field.
    await page.evaluate(() => {
      localStorage.setItem('aerolink-recent-destinations', JSON.stringify([{
        key: 'page-dashboard-system-', category: 'page', label: 'Command Center',
        detail: 'Program health and next actions', view: 'dashboard', discipline: 'system', icon: '⌂',
      }]))
    })

    // First open: the recent entry draws the canonical SVG projected from its current command
    // definition; the stored glyph must not render anywhere in the palette.
    await page.getByRole('button', { name: /Search & navigate/ }).click()
    const recentGroup = page.locator('.paletteGroup', { hasText: 'RECENT DESTINATIONS' })
    await expect(recentGroup.locator('a i svg[viewBox="0 0 16 16"]').first()).toBeVisible()
    await expect(page.locator('.commandPalette')).not.toContainText('⌂')

    // Selecting it keeps the saved destination, and the record is stored with the canonical icon.
    await page.keyboard.press('Enter')
    await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()
    const stored = await page.evaluate(() => localStorage.getItem('aerolink-recent-destinations'))
    expect(stored).not.toBeNull()
    expect(stored!).not.toContain('⌂')

    // Reopening after selection: still recent, still the canonical SVG.
    await page.getByRole('button', { name: /Search & navigate/ }).click()
    await expect(page.locator('.paletteGroup', { hasText: 'RECENT DESTINATIONS' })
      .locator('a i svg[viewBox="0 0 16 16"]').first()).toBeVisible()
  })

  test('compact workspace density keeps the navigation icons aligned and unclipped', async ({ page }) => {
    await page.setViewportSize({ width: 1366, height: 900 })
    await login(page, 'admin')
    await useCompactDensity(page)
    await assertNavigationIcons(page)
  })
})
