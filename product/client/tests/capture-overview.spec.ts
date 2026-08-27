import { expect, test } from '@playwright/test'
import { login, selectProgram } from './auth'

/**
 * Re-captures the screenshots used by the overview video in docs/overview-video.
 *
 * Skipped unless CAPTURE=1, because it writes files into the repository and has nothing to assert — it is a
 * tool that happens to be convenient to run on Playwright's plumbing, not a journey. Run it with:
 *
 *   cd product/client && CAPTURE=1 npx playwright test capture-overview
 *
 * Every frame in the video comes from here, driving the real product against the live FMS showcase data.
 * Nothing in that video is a mockup, and re-running this is what keeps that true as the interface changes.
 *
 * The 1600x900 viewport at 2x is deliberate: slides.js addresses regions in the resulting 3200x1800 image,
 * so changing either number invalidates every crop and highlight in the deck.
 */
const OUT = '../../docs/overview-video/shots'

test.use({ viewport: { width: 1600, height: 900 }, deviceScaleFactor: 2 })

test('capture the product surfaces used by the overview video', async ({ page }) => {
  test.skip(process.env.CAPTURE !== '1', 'Set CAPTURE=1 to refresh docs/overview-video/shots.')
  test.setTimeout(300_000)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')

  const shot = async (name: string) => {
    await page.waitForTimeout(900)
    await page.screenshot({ path: `${OUT}/${name}.png` })
    console.log(`captured ${name}`)
  }
  const visit = async (path: string, name: string, settle = 2200) => {
    await page.goto(new URL(root + path, page.url()).toString(), { waitUntil: 'load' })
    await page.waitForTimeout(settle)
    await shot(name)
  }

  await visit('/command-center', 'command-center', 2600)
  await visit('/systems/requirements', 'requirements-system', 2400)
  await visit('/systems/change-requests/new', 'change-request-new')
  await visit('/traceability', 'traceability', 2600)
  await visit('/release-readiness', 'release-readiness', 2800)

  // A change request that already carries review history, rather than an empty one.
  await page.goto(new URL(root + '/systems/change-requests', page.url()).toString(), { waitUntil: 'load' })
  await page.waitForTimeout(2200)
  await page.locator('.historyRow').first().click()
  await page.getByRole('link', { name: 'Open change request →' }).click()
  await shot('change-request-detail')

  // The two pages a build's verification work splits into, each addressable in its own right.
  await visit('/system-verification/coverage', 'verification-coverage', 2600)
  await visit('/system-verification/results', 'verification-results', 2600)

  expect(true).toBe(true)
})
