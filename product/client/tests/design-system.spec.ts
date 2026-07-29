import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from './auth'

/**
 * Enforces the design contract across every routed surface rather than one screen.
 *
 * The readability floor previously existed only as a claim in a report and a check on the change-request
 * page. An audit of twelve surfaces found 430 sub-12px declarations, 181 offending elements on People &
 * Authority alone, and 29 browser-default buttons on the requirements workspace. This spec is what stops
 * that returning.
 *
 * It runs every surface in both information densities, because a compact setting that quietly drops text
 * under the readability floor or pushes a control off the side of the page is worse than no compact
 * setting at all.
 */
const READABLE_MINIMUM = 12

const surfaces = [
  ['Command Center', '/command-center'],
  ['My Work', '/my-work'],
  ['Requirements Explorer', '/systems/requirements'],
  ['Change Requests', '/systems/change-requests'],
  ['Verification', '/system-verification'],
  ['Digital Thread', '/traceability'],
  ['Release Readiness', '/release-readiness'],
  ['People & Authority', '/administration'],
  ['Enterprise Control', '/enterprise-control'],
  // Added after the production journey — which reads the navigation instead of a list — found both of these
  // breaking the contract: Review Procedures rendered an 11px eyebrow, and New Change Request pushed the
  // document 106px past the viewport with a file input that had lost its width to a container's blanket
  // `input` rule. Neither was ever measured, because a hardcoded list does not grow when the product does.
  ['Review Procedures', '/review-workflows'],
  ['New Change Request', '/systems/change-requests/new'],
] as const

type Density = 'comfortable' | 'compact'

/** Applies a density the same way a person does — through the stored preference — then reloads. */
async function useDensity(page: import('@playwright/test').Page, density: Density) {
  await page.evaluate(value => localStorage.setItem('aerolink-density', value), density)
  await page.reload({ waitUntil: 'load' })
  await page.waitForTimeout(400)
  expect(await page.evaluate(() => document.documentElement.dataset.density)).toBe(density)
}

const auditSurface = (minimum: number) => {
  const visible = (el: Element) => {
    const box = el.getBoundingClientRect()
    return box.width > 0 && box.height > 0
  }
  const label = (el: Element) => (el.textContent || '').trim().slice(0, 26)
  const readable = [...document.querySelectorAll('body *')]
    .filter(el => visible(el) && !el.children.length && (el.textContent || '').trim().length > 0)
  const tiny = readable
    .filter(el => parseFloat(getComputedStyle(el).fontSize) < minimum)
    .map(el => `${label(el)} @ ${getComputedStyle(el).fontSize}`)
  // Browser-default chrome: grey background, square corners, no author styling.
  const unstyled = [...document.querySelectorAll('button')]
    .filter(el => visible(el) && getComputedStyle(el).backgroundColor === 'rgb(239, 239, 239)')
    .map(label)
  // WCAG 2.2 SC 2.5.8 Target Size (Minimum): 24x24 CSS pixels, which compact must not trade away.
  const smallTargets = [...document.querySelectorAll('button, a[href], select, input:not([type=hidden])')]
    .filter(el => {
      if (!visible(el) || (el as HTMLButtonElement).disabled) return false
      const box = el.getBoundingClientRect()
      return box.height < 24 || box.width < 24
    })
    .map(el => `${el.tagName.toLowerCase()} "${label(el)}" ${Math.round(el.getBoundingClientRect().width)}x${Math.round(el.getBoundingClientRect().height)}`)
  return {
    tiny: [...new Set(tiny)],
    unstyled: [...new Set(unstyled)],
    smallTargets: [...new Set(smallTargets)],
    overflow: document.documentElement.scrollWidth > window.innerWidth + 1,
    crashed: (document.querySelector('body')?.textContent || '').trim().length < 40,
    contentHeight: document.documentElement.scrollHeight,
  }
}

test('every surface honours the readability floor and target sizes in both densities', async ({ page, request }) => {
  test.setTimeout(360_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')

  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const failures: string[] = []

  for (const density of ['comfortable', 'compact'] as const) {
    await useDensity(page, density)
    for (const [name, path] of surfaces) {
      await page.goto(new URL(root + path, page.url()).toString(), { waitUntil: 'load' })
      await page.waitForTimeout(1000)
      const report = await page.evaluate(auditSurface, READABLE_MINIMUM)
      const where = `${name} [${density}]`

      if (report.crashed) failures.push(`${where}: rendered nothing — the page is broken, not merely empty`)
      if (report.tiny.length) failures.push(`${where}: ${report.tiny.length} element(s) below ${READABLE_MINIMUM}px — ${report.tiny.slice(0, 4).join('; ')}`)
      if (report.unstyled.length) failures.push(`${where}: ${report.unstyled.length} browser-default button(s) — ${report.unstyled.slice(0, 4).join('; ')}`)
      if (report.smallTargets.length) failures.push(`${where}: ${report.smallTargets.length} target(s) under 24x24 — ${report.smallTargets.slice(0, 4).join('; ')}`)
      if (report.overflow) failures.push(`${where}: document scrolls horizontally at 1440px`)
    }
  }

  expect(failures, `Design contract violations:\n  ${failures.join('\n  ')}`).toEqual([])
})

test('compact density fits materially more on the screen than comfortable', async ({ page, request }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')

  // Record-heavy surfaces, where the setting is supposed to earn its place.
  const measured = ['/systems/requirements', '/system-verification', '/administration', '/command-center']
  const heights: Record<string, Record<string, number>> = {}

  for (const density of ['comfortable', 'compact'] as const) {
    await useDensity(page, density)
    for (const path of measured) {
      await page.goto(new URL(root + path, page.url()).toString(), { waitUntil: 'load' })
      await page.waitForTimeout(1000)
      const height = await page.evaluate(() => {
        const main = document.querySelector('.workspaceView > main') as HTMLElement | null
        return main ? main.scrollHeight : document.documentElement.scrollHeight
      })
      ;(heights[path] ??= {})[density] = height
    }
  }

  for (const [path, value] of Object.entries(heights)) {
    const saved = value.comfortable - value.compact
    const percent = ((saved / value.comfortable) * 100).toFixed(1)
    console.log(`${path}: comfortable ${value.comfortable}px -> compact ${value.compact}px (${saved}px, ${percent}% shorter)`)
  }

  const notDenser = Object.entries(heights)
    .filter(([, value]) => value.compact >= value.comfortable)
    .map(([path, value]) => `${path}: comfortable ${value.comfortable}px, compact ${value.compact}px`)

  expect(notDenser, `Compact density made no difference on:\n  ${notDenser.join('\n  ')}`).toEqual([])
})

/**
 * Page height is the wrong measure for a surface that scrolls inside a fixed-height pane: the requirements
 * table sits in a `calc(100vh - …)` scroller, so compressing its rows barely moves the page. What the
 * setting is for on those surfaces is how many records a person can see at once, so that is what is
 * asserted. This is also what caught compact achieving only 4.3% there — the row padding had been made
 * density-aware while its min-height floor had not.
 */
test('compact density shows more records at once where the list scrolls inside a pane', async ({ page, request }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const visibleRows: Record<string, number> = {}

  for (const density of ['comfortable', 'compact'] as const) {
    await useDensity(page, density)
    await page.goto(new URL(root + '/systems/requirements', page.url()).toString(), { waitUntil: 'load' })
    await page.locator('.reqTable article').first().waitFor()
    await page.waitForTimeout(600)
    visibleRows[density] = await page.evaluate(() =>
      [...document.querySelectorAll('.reqTable article')].filter(row => {
        const box = row.getBoundingClientRect()
        return box.top >= 0 && box.bottom <= window.innerHeight
      }).length)
  }

  console.log(`Requirement rows fully in view: comfortable ${visibleRows.comfortable}, compact ${visibleRows.compact}`)
  expect(visibleRows.compact, 'compact must fit more requirement rows in the viewport than comfortable')
    .toBeGreaterThan(visibleRows.comfortable)
})

/**
 * A surface can be contained in its resting state and overflow the moment a panel opens. The requirements
 * workspace did exactly that: the command row could not shrink below the intrinsic width of its search
 * input, so opening the inspector pushed the view-mode switch 12px past the edge of the document. Auditing
 * only default states missed it for as long as the audit existed.
 */
test('opening the requirement inspector does not push the page sideways', async ({ page, request }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')

  for (const density of ['comfortable', 'compact'] as const) {
    await useDensity(page, density)
    await page.goto(new URL(root + '/systems/requirements', page.url()).toString(), { waitUntil: 'load' })
    await page.locator('.reqTable article').first().waitFor()
    await page.locator('.reqTable article > button').first().click()
    await expect(page.locator('.requirementInspector')).toBeVisible()
    await page.waitForTimeout(400)

    const spill = await page.evaluate(() => {
      const limit = window.innerWidth
      return [...document.querySelectorAll('*')]
        .filter(el => {
          const box = el.getBoundingClientRect()
          return (box.width > 0 || box.height > 0) && box.right > limit + 1
        })
        .map(el => `${el.tagName.toLowerCase()}.${(el.className || '').toString().split(' ')[0]} right=${Math.round(el.getBoundingClientRect().right)}`)
        .slice(0, 6)
    })
    expect(spill, `[${density}] elements spill past 1440px with the inspector open`).toEqual([])
  }
})
