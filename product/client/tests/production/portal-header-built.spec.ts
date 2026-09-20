import { expect, test, type Page } from '@playwright/test'
import { layoutSettled, login } from '../auth'

/**
 * The #1048/#1054 header qualification against the application a deployment actually serves: the API
 * presenting the built client from Client:StaticFiles. The development-lane containment spec proves the
 * behavior; this one proves it survives chunking, CSS extraction and minification (Checkpoint B, F04).
 *
 * Self-contained on purpose: importing a journey spec would register its tests here too.
 */

const homeIdentity = (overrides: Record<string, unknown> = {}): Record<string, unknown> => ({
  service: 'AeroLink API',
  sourceSha: 'c2602d9360af27a333d789b379d379d66d08ff42',
  sourceShortSha: 'c2602d93',
  mode: 'HOME-PRODUCTION',
  mainCurrency: {
    state: 'Current',
    checkedAtUtc: new Date(Date.now() - 4 * 60_000).toISOString(),
    remoteSha: 'c2602d9360af27a333d789b379d379d66d08ff42',
  },
  instance: { id: 'home-canonical', label: 'HOME CANONICAL', classification: 'HomeCanonical', snapshot: null },
  database: { name: 'aerolink' },
  ...overrides,
})

const installIdentity = (page: Page, payload: Record<string, unknown>) =>
  page.route('**/health/identity', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(payload) }))

async function settleAt(page: Page, width: number, height: number) {
  await page.setViewportSize({ width, height })
  await page.evaluate(() => window.scrollTo(0, 0))
  await layoutSettled(page)
}

/** Visible leaf text runs inside the header must stay inside the header and the viewport. */
async function assertVisibleHeaderText(page: Page, context: string) {
  const runs = await page.evaluate(() => {
    const header = document.querySelector('.projectsTopBar')
    if (!header) return null
    const headerRect = header.getBoundingClientRect()
    const walker = document.createTreeWalker(header, NodeFilter.SHOW_TEXT)
    const runs: Array<{ text: string; right: number; bottom: number }> = []
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim()
      const el = node.parentElement
      if (!text || !el) continue
      if (getComputedStyle(el).display === 'none' || getComputedStyle(el).visibility === 'hidden') continue
      const holder = el.closest('details')
      if (holder && !(holder as HTMLDetailsElement).open) continue
      const range = document.createRange()
      range.selectNodeContents(node)
      for (const r of range.getClientRects()) {
        if (r.width === 0 || r.height === 0) continue
        runs.push({ text, right: r.right, bottom: r.bottom })
      }
    }
    return { runs, headerRight: headerRect.right, headerBottom: headerRect.bottom, clientWidth: document.documentElement.clientWidth }
  })
  expect(runs, `${context}: header present`).not.toBeNull()
  expect(runs!.runs.length).toBeGreaterThanOrEqual(4)
  for (const run of runs!.runs) {
    expect(run.right, `${context}: text "${run.text.slice(0, 32)}" exceeds the viewport`).toBeLessThanOrEqual(runs!.clientWidth + 1)
    expect(run.right, `${context}: text "${run.text.slice(0, 32)}" exceeds the header`).toBeLessThanOrEqual(runs!.headerRight + 1)
    expect(run.bottom, `${context}: text "${run.text.slice(0, 32)}" exceeds the header bottom`).toBeLessThanOrEqual(runs!.headerBottom + 1)
  }
}

async function record(page: Page, testInfo: { outputPath: (p: string) => string }, name: string) {
  const path = testInfo.outputPath(`built-${name}.png`)
  await page.screenshot({ path })
  const evidenceRoot = process.env.AEROLINK_E2E_DIAGNOSTIC_DIR
  if (evidenceRoot) {
    const { copyFileSync, mkdirSync } = await import('node:fs')
    mkdirSync(evidenceRoot, { recursive: true })
    copyFileSync(path, `${evidenceRoot}\\built-${name}.png`)
  }
}

test('the served document is the built client, and its HOME header contains at desktop, 900 and mobile widths', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity())
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge).toBeVisible()
  await expect(badge.getByTestId('main-currency')).toContainText('Current main')

  // Prove the application under test is the build, not the Vite entry: hashed assets, no dev module graph.
  const assets = await page.evaluate(() => ({
    styles: [...document.querySelectorAll('link[rel="stylesheet"]')].map(l => l.getAttribute('href') ?? ''),
    devModules: [...document.querySelectorAll('script[src]')].map(s => s.getAttribute('src')).filter(src => src?.includes('/src/')),
  }))
  expect(assets.devModules, 'the Vite dev module graph must not be served').toHaveLength(0)
  expect(assets.styles.some(href => /\/assets\/style-[\w-]+\.css$/.test(href)), `the extracted hashed stylesheet must be served (found: ${assets.styles.join(', ')})`).toBe(true)

  for (const [width, height] of [[1440, 900], [900, 800], [390, 760]] as const) {
    await settleAt(page, width, height)
    const closed = await page.evaluate(() => {
      const doc = document.documentElement
      const rect = (sel: string) => {
        const el = document.querySelector(sel)
        if (!el) return null
        const r = el.getBoundingClientRect()
        return { x: r.x, y: r.y, right: r.right, bottom: r.bottom, width: r.width, height: r.height }
      }
      return {
        scrollWidth: doc.scrollWidth,
        clientWidth: doc.clientWidth,
        badge: rect('[data-testid="instance-badge"]'),
        summary: rect('[data-testid="instance-summary"]'),
        topBar: rect('.projectsTopBar'),
      }
    })
    expect(closed.badge, `badge renders at ${width}px`).not.toBeNull()
    expect(closed.badge!.bottom, `built client at ${width}px: badge leaves the header`).toBeLessThanOrEqual(closed.topBar!.bottom + 1)
    expect(closed.badge!.right, `built client at ${width}px: badge leaves the header content`).toBeLessThanOrEqual(closed.topBar!.right - 18)
    expect(closed.summary!.height, `built client at ${width}px: disclosure target below 24px`).toBeGreaterThanOrEqual(24)
    expect(closed.scrollWidth, `built client at ${width}px: document overflows`).toBeLessThanOrEqual(closed.clientWidth + 1)
    await assertVisibleHeaderText(page, `built portal ${width}px closed`)
    await record(page, testInfo, `home-portal-${width}`)
  }

  // Opened disclosure at the narrowest width: the panel and its text must stay usable and contained.
  await settleAt(page, 390, 760)
  await badge.getByTestId('instance-summary').click()
  await expect(badge.getByTestId('instance-details')).toBeVisible()
  await expect(badge.getByTestId('instance-details')).toContainText('HOME CANONICAL (HomeCanonical)')
  await expect(badge.getByTestId('instance-details')).toContainText('c2602d9360af27a333d789b379d379d66d08ff42')
  await layoutSettled(page)
  await assertVisibleHeaderText(page, 'built portal 390px open')
  await record(page, testInfo, 'home-portal-390-open')
})

test('the built setup portal releases the body floor and stays contained at 900 and mobile widths', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity())
  await login(page, 'admin', { openProject: false })
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Create New Project' })).toBeVisible()
  for (const [width, height] of [[900, 800], [390, 760]] as const) {
    await settleAt(page, width, height)
    const state = await page.evaluate(() => ({
      bodyMinWidth: getComputedStyle(document.body).minWidth,
      scrollWidth: document.documentElement.scrollWidth,
      clientWidth: document.documentElement.clientWidth,
      signOut: document.querySelector('.projectsSignOut')?.getBoundingClientRect().right ?? 0,
    }))
    expect(state.bodyMinWidth, `built setup at ${width}px: the body floor must be released`).toBe('0px')
    expect(state.scrollWidth, `built setup at ${width}px: document overflows`).toBeLessThanOrEqual(state.clientWidth + 1)
    expect(state.signOut, `built setup at ${width}px: Sign out leaves the viewport`).toBeLessThanOrEqual(state.clientWidth + 1)
    await assertVisibleHeaderText(page, `built setup ${width}px`)
    await record(page, testInfo, `setup-${width}`)
  }
})

test('the built workspace sidebar opens the disclosure inside the measured column in both densities', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity({
    mode: 'UNKNOWN',
    mainCurrency: null,
    instance: {
      id: 'work-laptop',
      label: 'FLIGHT TEST LAPTOP LONG INSTALLATION NAME',
      classification: 'WorkLaptopLocal',
      snapshot: {
        sourceLabel: 'HOME CANONICAL',
        sourceSha: 'd4c3b2a1d4c3b2a1d4c3b2a1d4c3b2a1d4c3b2a1',
        createdAtUtc: new Date(Date.now() - 5 * 86_400_000).toISOString(),
        activatedAtUtc: null,
      },
    },
  }))
  await login(page, 'admin')
  const brandBadge = page.locator('.brand').getByTestId('instance-badge')
  await expect(brandBadge).toBeVisible()

  for (const density of ['comfortable', 'compact'] as const) {
    await page.evaluate(value => localStorage.setItem('aerolink-density', value), density)
    await page.reload()
    await expect(brandBadge).toBeVisible()
    await settleAt(page, 1280, 900)
    const summary = await page.evaluate(() => {
      const el = document.querySelector('.brand [data-testid="instance-summary"]')
      return el?.getBoundingClientRect().height ?? 0
    })
    expect(summary, `built sidebar ${density} density: disclosure target ${summary}px below 24px`).toBeGreaterThanOrEqual(24)
  }

  await brandBadge.getByTestId('instance-summary').click()
  const panel = brandBadge.getByTestId('instance-details')
  await expect(panel).toBeVisible()
  await expect(panel).toContainText('HOME CANONICAL')
  await settleAt(page, 1280, 900)
  const state = await page.evaluate(() => {
    const sidebar = document.querySelector('.shell aside.appNavigation')?.getBoundingClientRect()
    const panelRect = document.querySelector('.brand [data-testid="instance-details"]')?.getBoundingClientRect()
    return {
      sidebarRight: sidebar?.right ?? 0,
      sidebarWidth: sidebar?.width ?? 0,
      panelRight: panelRect?.right ?? 0,
      clientWidth: document.documentElement.clientWidth,
    }
  })
  expect(state.panelRight, 'the opened panel leaves the measured sidebar column').toBeLessThanOrEqual(state.sidebarRight + 1)
  expect(state.clientWidth, 'the workspace document is contained at 1280px').toBeLessThanOrEqual(state.clientWidth)
  await record(page, testInfo, 'sidebar-open-1280')
})
