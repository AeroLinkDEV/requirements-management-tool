import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from './auth'

/**
 * Enforces the design contract across every routed surface rather than one screen.
 *
 * The readability floor previously existed only as a claim in a report and a check on the change-request
 * page. An audit of twelve surfaces found 430 sub-12px declarations, 181 offending elements on People &
 * Authority alone, and 29 browser-default buttons on the requirements workspace. This spec is what stops
 * that returning.
 */
const READABLE_MINIMUM = 12

const surfaces = [
  ['Command Center', '/command-center'],
  ['My Work', '/my-work'],
  ['Requirements Explorer', '/systems/requirements'],
  ['Change Requests', '/systems/change-requests'],
  ['Verification', '/system-verification'],
  ['Problem Reports', '/problem-reports'],
  ['Digital Thread', '/traceability'],
  ['Baselines', '/baselines'],
  ['Release Readiness', '/release-readiness'],
  ['Release Planning', '/release-planning'],
  ['People & Authority', '/administration'],
  ['Enterprise Control', '/enterprise-control'],
] as const

test('every surface honours the readability floor, styles its controls, and never scrolls sideways', async ({ page, request }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')

  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const failures: string[] = []

  for (const [name, path] of surfaces) {
    await page.goto(new URL(root + path, page.url()).toString(), { waitUntil: 'load' })
    await page.waitForTimeout(1200)

    const report = await page.evaluate(minimum => {
      const visible = (el: Element) => {
        const box = el.getBoundingClientRect()
        return box.width > 0 && box.height > 0
      }
      const tiny = [...document.querySelectorAll('body *')]
        .filter(el => visible(el) && !el.children.length && (el.textContent || '').trim().length > 0)
        .filter(el => parseFloat(getComputedStyle(el).fontSize) < minimum)
        .map(el => `${(el.textContent || '').trim().slice(0, 26)} @ ${getComputedStyle(el).fontSize}`)
      // Browser-default chrome: grey background, square corners, no author styling.
      const unstyled = [...document.querySelectorAll('button')]
        .filter(el => visible(el) && getComputedStyle(el).backgroundColor === 'rgb(239, 239, 239)')
        .map(el => (el.textContent || '').trim().slice(0, 26))
      return {
        tiny: [...new Set(tiny)],
        unstyled: [...new Set(unstyled)],
        overflow: document.documentElement.scrollWidth > window.innerWidth + 1,
        crashed: (document.querySelector('body')?.textContent || '').trim().length < 40,
      }
    }, READABLE_MINIMUM)

    if (report.crashed) failures.push(`${name}: rendered nothing — the page is broken, not merely empty`)
    if (report.tiny.length) failures.push(`${name}: ${report.tiny.length} element(s) below ${READABLE_MINIMUM}px — ${report.tiny.slice(0, 4).join('; ')}`)
    if (report.unstyled.length) failures.push(`${name}: ${report.unstyled.length} browser-default button(s) — ${report.unstyled.slice(0, 4).join('; ')}`)
    if (report.overflow) failures.push(`${name}: document scrolls horizontally at 1440px`)
  }

  expect(failures, `Design contract violations:\n  ${failures.join('\n  ')}`).toEqual([])
})
