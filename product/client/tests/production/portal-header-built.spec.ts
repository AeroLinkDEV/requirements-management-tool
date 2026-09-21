import { execSync } from 'node:child_process'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, test, type Page } from '@playwright/test'
import { layoutSettled, login } from '../auth'

/**
 * The #1048/#1054 header qualification against the application a deployment actually serves: the API
 * presenting the built client from Client:StaticFiles. The development-lane containment spec proves the
 * behavior; this one proves it survives chunking, CSS extraction and minification (Checkpoint B, F04).
 *
 * Self-contained on purpose: importing a journey spec would register its tests here too. Every capture
 * writes its measured geometry beside the screenshot, and the run writes a manifest binding the evidence
 * to the commit, tree state and served build assets.
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

type HeaderState = {
  route: string
  viewport: { width: number; height: number }
  scroll: { x: number; y: number }
  clientWidth: number
  scrollWidth: number
  bodyMinWidth: string
  density: string
  rects: Record<string, { x: number; y: number; right: number; bottom: number; width: number; height: number } | null>
  disclosureOpen: boolean
}

async function measureHeaderState(page: Page, fixtureName: string, open: boolean): Promise<HeaderState> {
  await page.evaluate(() => document.fonts.ready)
  await layoutSettled(page)
  return page.evaluate((open) => {
    const doc = document.documentElement
    const rect = (selector: string) => {
      const el = document.querySelector(selector)
      if (!el) return null
      const r = el.getBoundingClientRect()
      return { x: r.x, y: r.y, right: r.right, bottom: r.bottom, width: r.width, height: r.height }
    }
    const badge = document.querySelector('[data-testid="instance-badge"]')
    return {
      route: window.location.pathname,
      viewport: { width: window.innerWidth, height: window.innerHeight },
      scroll: { x: window.scrollX, y: window.scrollY },
      clientWidth: doc.clientWidth,
      scrollWidth: doc.scrollWidth,
      bodyMinWidth: getComputedStyle(document.body).minWidth,
      density: document.documentElement.dataset.density ?? 'comfortable',
      observedLabel: document.querySelector('[data-testid="instance-label"]')?.textContent ?? null,
      observedClassification: badge?.getAttribute('data-classification') ?? null,
      rects: {
        topBar: rect('.projectsTopBar'),
        brand: rect('.projectsBrand'),
        badge: rect('[data-testid="instance-badge"]'),
        badgeSummary: rect('[data-testid="instance-summary"]'),
        badgePanel: rect('[data-testid="instance-details"]'),
        account: rect('.projectsAccount'),
        accountText: rect('.projectsAccount > div:not(.personAvatar)'),
        signOut: rect('.projectsSignOut'),
        sidebar: rect('.shell aside.appNavigation'),
      },
      disclosureOpen: badge instanceof HTMLDetailsElement ? badge.open : false,
    }
  }, open).then(state => ({ ...state, fixture: fixtureName }))
}

/** Visible leaf text runs inside the header must stay inside the header and the viewport. A closed
    details hides its non-summary content, but its visible summary text MUST be audited. */
async function assertVisibleHeaderText(page: Page, context: string, expectedTexts: string[] = []): Promise<string[]> {
  const runs = await page.evaluate(() => {
    const header = document.querySelector('.projectsTopBar')
    if (!header) return null
    const headerRect = header.getBoundingClientRect()
    const walker = document.createTreeWalker(header, NodeFilter.SHOW_TEXT)
    const runs: Array<{ text: string; right: number; bottom: number; top: number }> = []
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim()
      const el = node.parentElement
      if (!text || !el) continue
      if (getComputedStyle(el).display === 'none' || getComputedStyle(el).visibility === 'hidden') continue
      const holder = el.closest('details')
      if (holder && !(holder as HTMLDetailsElement).open && !el.closest('summary')) continue
      const range = document.createRange()
      range.selectNodeContents(node)
      for (const r of range.getClientRects()) {
        if (r.width === 0 || r.height === 0) continue
        runs.push({ text, right: r.right, bottom: r.bottom, top: r.top })
      }
    }
    return { runs, headerRight: headerRect.right, headerBottom: headerRect.bottom, clientWidth: document.documentElement.clientWidth }
  })
  expect(runs, `${context}: header present`).not.toBeNull()
  expect(runs!.runs.length).toBeGreaterThanOrEqual(4)
  for (const expected of expectedTexts) {
    expect(runs!.runs.some(run => run.text === expected), `${context}: expected visible summary/status text "${expected}" was not among the audited runs (audited: ${runs!.runs.map(r => r.text).join(' | ')})`).toBe(true)
  }
  for (const run of runs!.runs) {
    expect(run.right, `${context}: text "${run.text.slice(0, 32)}" exceeds the viewport`).toBeLessThanOrEqual(runs!.clientWidth + 1)
    expect(run.right, `${context}: text "${run.text.slice(0, 32)}" exceeds the header`).toBeLessThanOrEqual(runs!.headerRight + 1)
    expect(run.bottom, `${context}: text "${run.text.slice(0, 32)}" exceeds the header bottom`).toBeLessThanOrEqual(runs!.headerBottom + 1)
  }
  return runs!.runs.map(run => run.text)
}

const evidenceDir = process.env.AEROLINK_E2E_DIAGNOSTIC_DIR

async function record(state: HeaderState, page: Page, testInfo: { outputPath: (p: string) => string }, name: string, extra: Record<string, unknown> = {}) {
  const path = testInfo.outputPath(`built-${name}.png`)
  await page.screenshot({ path })
  const record_ = { capture: name, ...extra, ...state }
  if (evidenceDir) {
    mkdirSync(evidenceDir, { recursive: true })
    writeFileSync(join(evidenceDir, `built-geometry-${name}.json`), JSON.stringify(record_, null, 2))
    const { copyFileSync } = await import('node:fs')
    copyFileSync(path, join(evidenceDir, `built-${name}.png`))
  }
}

/** Binds the evidence set to the commit, tree state and served build assets. Written once per run. */
let manifestWritten = false
async function writeManifest(page: Page) {
  if (manifestWritten || !evidenceDir) return
  manifestWritten = true
  mkdirSync(evidenceDir, { recursive: true })
  const assets = await page.evaluate(() => ({
    stylesheets: [...document.querySelectorAll('link[rel="stylesheet"]')].map(l => l.getAttribute('href')),
    scripts: [...document.querySelectorAll('script[src]')].map(s => s.getAttribute('src')),
  }))
  let head = 'unknown'
  let dirty: string | null = null
  try {
    // Playwright runs from product/client; git resolves through the worktree.
    head = execSync('git rev-parse HEAD', { cwd: process.cwd(), encoding: 'utf8' }).trim()
    dirty = execSync('git status --porcelain', { cwd: process.cwd(), encoding: 'utf8' }) || null
  } catch { /* provenance stays 'unknown' rather than fabricating a SHA */ }
  writeFileSync(join(evidenceDir, 'built-header-manifest.json'), JSON.stringify({
    writtenAt: new Date().toISOString(),
    head, dirty, assets, testFile: 'tests/production/portal-header-built.spec.ts',
  }, null, 2))
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
  await writeManifest(page)

  for (const [width, height] of [[1440, 900], [900, 800], [390, 760]] as const) {
    await settleAt(page, width, height)
    const state = await measureHeaderState(page, 'home-canonical-current', false)
    expect(state.rects.badge, `badge renders at ${width}px`).not.toBeNull()
    expect(state.rects.badge!.bottom, `built client at ${width}px: badge leaves the header`).toBeLessThanOrEqual(state.rects.topBar!.bottom + 1)
    expect(state.rects.badge!.right, `built client at ${width}px: badge leaves the header content`).toBeLessThanOrEqual(state.rects.topBar!.right - 18)
    expect(state.rects.badgeSummary!.width, `built client at ${width}px: disclosure target width below 24px`).toBeGreaterThanOrEqual(24)
    expect(state.rects.badgeSummary!.height, `built client at ${width}px: disclosure target height below 24px`).toBeGreaterThanOrEqual(24)
    expect(state.scrollWidth, `built client at ${width}px: document overflows (scrollWidth ${state.scrollWidth} > clientWidth ${state.clientWidth})`).toBeLessThanOrEqual(state.clientWidth + 1)
    await assertVisibleHeaderText(page, `built portal ${width}px closed`, ['HOME', 'Current main'])
    await record(state, page, testInfo, `home-portal-${width}`, { fixtureName: 'home-canonical-current' })
  }

  // Opened disclosure at the narrowest width: the panel and its text must stay usable and contained.
  await settleAt(page, 390, 760)
  await badge.getByTestId('instance-summary').click()
  await expect(badge.getByTestId('instance-details')).toBeVisible()
  await expect(badge.getByTestId('instance-details')).toContainText('HOME CANONICAL (HomeCanonical)')
  await expect(badge.getByTestId('instance-details')).toContainText('c2602d9360af27a333d789b379d379d66d08ff42')
  const opened = await measureHeaderState(page, 'home-canonical-current', true)
  expect(opened.scrollWidth, 'built portal 390px open: document overflows').toBeLessThanOrEqual(opened.clientWidth + 1)
  await assertVisibleHeaderText(page, 'built portal 390px open', ['HOME', 'Current main'])
  await record(opened, page, testInfo, 'home-portal-390-open', { fixtureName: 'home-canonical-current' })
})

test('the built setup portal releases the body floor and stays contained at 900 and mobile widths', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity())
  await login(page, 'admin', { openProject: false })
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Create New Project' })).toBeVisible()
  for (const [width, height] of [[900, 800], [390, 760]] as const) {
    await settleAt(page, width, height)
    const state = await measureHeaderState(page, 'home-canonical-current', false)
    expect(state.bodyMinWidth, `built setup at ${width}px: the body floor must be released`).toBe('0px')
    expect(state.scrollWidth, `built setup at ${width}px: document overflows (scrollWidth ${state.scrollWidth} > clientWidth ${state.clientWidth})`).toBeLessThanOrEqual(state.clientWidth + 1)
    expect(state.rects.signOut!.right, `built setup at ${width}px: Sign out leaves the viewport`).toBeLessThanOrEqual(state.clientWidth + 1)
    await assertVisibleHeaderText(page, `built setup ${width}px`)
    await record(state, page, testInfo, `setup-${width}`, { fixtureName: 'home-canonical-current' })
  }
})

test('the built workspace sidebar meets the disclosure target in both densities and both label lengths, and the opened panel stays in the column', async ({ page }, testInfo) => {
  // Stable fixtures, defined once: the active route payload is selected from these per case. The
  // round-3 version read from the mutable payload here, so the compact "long-label" case silently
  // rendered Q — the observed label is now asserted and recorded per capture.
  const LONG_FIXTURE = homeIdentity({
    mode: 'UNKNOWN', mainCurrency: null,
    instance: {
      id: 'work-laptop', label: 'FLIGHT TEST LAPTOP LONG INSTALLATION NAME', classification: 'WorkLaptopLocal',
      snapshot: { sourceLabel: 'HOME CANONICAL', sourceSha: 'd4c3b2a1d4c3b2a1d4c3b2a1d4c3b2a1d4c3b2a1', createdAtUtc: new Date(Date.now() - 5 * 86_400_000).toISOString(), activatedAtUtc: null },
    },
  })
  const SHORT_FIXTURE = homeIdentity({
    mode: 'UNKNOWN', mainCurrency: null,
    instance: { id: 'short', label: 'Q', classification: 'WorkLaptopLocal', snapshot: null },
  })
  const CASES = [
    { fixtureName: 'long-label', fixture: LONG_FIXTURE, expectedLabel: 'FLIGHT TEST LAPTOP LONG INSTALLATION NAME', hasSnapshot: true },
    { fixtureName: 'short-label', fixture: SHORT_FIXTURE, expectedLabel: 'Q', hasSnapshot: false },
  ] as const
  let payload = LONG_FIXTURE
  await page.route('**/health/identity', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(payload) }))
  await login(page, 'admin')
  const brandBadge = page.locator('.brand').getByTestId('instance-badge')
  await expect(brandBadge).toBeVisible()

  for (const density of ['comfortable', 'compact'] as const) {
    for (const { fixtureName, fixture, expectedLabel, hasSnapshot } of CASES) {
      payload = fixture
      await page.evaluate(value => localStorage.setItem('aerolink-density', value), density)
      await page.reload()
      // Wait for the rendered density, exactly as the design-system journeys do.
      await expect.poll(() => page.evaluate(() => document.documentElement.dataset.density), { timeout: 10_000 }).toBe(density)
      await expect(brandBadge).toBeVisible()
      // The capture must prove which identity actually rendered, not which one was intended.
      await expect(brandBadge.getByTestId('instance-label')).toHaveText(expectedLabel)
      await expect(brandBadge).toHaveAttribute('data-classification', 'WorkLaptopLocal')
      await settleAt(page, 1280, 900)
      const closed = await measureHeaderState(page, fixtureName, false)
      expect(closed.observedLabel, `sidebar ${fixtureName} ${density}: the rendered label is not the fixture's`).toBe(expectedLabel)
      expect(closed.observedClassification).toBe('WorkLaptopLocal')
      expect(closed.rects.badgeSummary!.width, `built sidebar ${fixtureName} ${density}: target width below 24px`).toBeGreaterThanOrEqual(24)
      expect(closed.rects.badgeSummary!.height, `built sidebar ${fixtureName} ${density}: target height below 24px`).toBeGreaterThanOrEqual(24)
      expect(closed.rects.badge!.right, `built sidebar ${fixtureName} ${density}: badge leaves the ${closed.rects.sidebar?.width}px column`).toBeLessThanOrEqual(closed.rects.sidebar!.right + 1)
      // At 1280px the workspace document is contained; the 960px floor below 960px is unaffected.
      expect(closed.scroll.x, `built sidebar ${fixtureName} ${density}: measurements must start at the scroll origin`).toBe(0)
      expect(closed.scrollWidth, `built sidebar ${fixtureName} ${density}: document overflows (scrollWidth ${closed.scrollWidth} > clientWidth ${closed.clientWidth})`).toBeLessThanOrEqual(closed.clientWidth + 1)
      await record(closed, page, testInfo, `sidebar-${fixtureName}-${density}-closed`, { fixtureName, expectedLabel })

      await brandBadge.getByTestId('instance-summary').click()
      await expect(brandBadge.getByTestId('instance-details')).toBeVisible()
      if (hasSnapshot) {
        // The long fixture carries snapshot provenance; assert it rendered.
        await expect(brandBadge.getByTestId('instance-details')).toContainText('HOME CANONICAL')
        await expect(brandBadge.getByTestId('instance-details')).toContainText('5 days old')
      } else {
        await expect(brandBadge.getByTestId('instance-details')).not.toContainText('Snapshot')
      }
      await settleAt(page, 1280, 900)
      const opened = await measureHeaderState(page, fixtureName, true)
      expect(opened.observedLabel).toBe(expectedLabel)
      expect(opened.scroll.x, `built sidebar ${fixtureName} ${density} open: measurements must start at the scroll origin`).toBe(0)
      expect(opened.scrollWidth, `built sidebar ${fixtureName} ${density} open: document overflows (scrollWidth ${opened.scrollWidth} > clientWidth ${opened.clientWidth})`).toBeLessThanOrEqual(opened.clientWidth + 1)
      expect(opened.rects.badgePanel!.right, `built sidebar ${fixtureName} ${density}: opened panel leaves the column`).toBeLessThanOrEqual(opened.rects.sidebar!.right + 1)
      await record(opened, page, testInfo, `sidebar-${fixtureName}-${density}-open`, { fixtureName, expectedLabel })
    }
  }
})
