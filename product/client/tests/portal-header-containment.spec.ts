import { expect, test, type Page } from '@playwright/test'
import { layoutSettled, login } from './auth'

/**
 * #1048 / #1054 regression: the shared portal header contains its content, and the setup portal
 * releases the legacy 960px body floor without leaking into the controlled workspace.
 *
 * Assertions are outcome-based: measured rectangles, document containment, and readable controls —
 * not the implementation's layout mechanism. Screenshots and geometry are captured from the same
 * asserted, settled state so every pair matches (Checkpoint A refinement 4).
 */

type IdentityFixture = Record<string, unknown>

const homeIdentity = (overrides: IdentityFixture = {}): IdentityFixture => ({
  service: 'AeroLink API',
  sourceSha: 'c2602d9360af27a333d789b379d379d66d08ff42',
  sourceShortSha: 'c2602d93',
  sourceIdentity: 'c2602d9360af27a333d789b379d379d66d08ff42',
  mode: 'HOME-PRODUCTION',
  mainCurrency: {
    state: 'Current',
    checkedAtUtc: new Date(Date.now() - 4 * 60_000).toISOString(),
    remoteSha: 'c2602d9360af27a333d789b379d379d66d08ff42',
  },
  instance: {
    id: 'home-canonical',
    label: 'HOME CANONICAL',
    classification: 'HomeCanonical',
    snapshot: null,
  },
  database: { name: 'aerolink' },
  schema: { latestAppliedMigration: '20260918120000_ExampleMigration' },
  startedAtUtc: new Date(Date.now() - 3 * 3_600_000).toISOString(),
  ...overrides,
})

const installIdentity = (page: Page, payload: IdentityFixture) =>
  page.route('**/health/identity', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(payload) }))

type Rect = { x: number; y: number; width: number; height: number; right: number; bottom: number } | null

async function measureGeometry(page: Page) {
  await page.evaluate(() => document.fonts.ready)
  await layoutSettled(page)
  return page.evaluate(() => {
    const doc = document.documentElement
    const rectOf = (selector: string): Rect => {
      const el = document.querySelector(selector)
      if (!el) return null
      const r = el.getBoundingClientRect()
      return { x: r.x, y: r.y, width: r.width, height: r.height, right: r.right, bottom: r.bottom }
    }
    const badge = document.querySelector('[data-testid="instance-badge"]') as HTMLElement | null
    return {
      location: window.location.pathname,
      scroll: { x: window.scrollX, y: window.scrollY },
      viewport: { width: window.innerWidth, height: window.innerHeight, dpr: window.devicePixelRatio },
      document: {
        clientWidth: doc.clientWidth,
        scrollWidth: doc.scrollWidth,
        bodyMinWidth: getComputedStyle(document.body).minWidth,
      },
      rects: {
        topBar: rectOf('.projectsTopBar'),
        topBarInner: rectOf('.projectsTopBarInner'),
        brand: rectOf('.projectsBrand'),
        badge: rectOf('[data-testid="instance-badge"]'),
        badgeSummary: rectOf('[data-testid="instance-summary"]'),
        badgePanel: rectOf('[data-testid="instance-details"]'),
        account: rectOf('.projectsAccount'),
        accountText: rectOf('.projectsAccount > div:not(.personAvatar)'),
        signOut: rectOf('.projectsSignOut'),
        sidebar: rectOf('.shell aside.appNavigation'),
      },
      accountTextVisible: (() => {
        const el = document.querySelector('.projectsAccount > div:not(.personAvatar)')
        return el ? getComputedStyle(el).display !== 'none' && el.textContent!.trim().length > 0 : false
      })(),
      badgePresent: badge !== null,
    }
  })
}

type Geometry = Awaited<ReturnType<typeof measureGeometry>>

async function record(page: Page, testInfo: { outputPath: (p: string) => string; attach: (name: string, o: { body: string; contentType: string }) => Promise<void> }, name: string, geometry: Geometry) {
  const path = testInfo.outputPath(`after-${name}.png`)
  await page.screenshot({ path })
  const { copyFileSync, mkdirSync, writeFileSync } = await import('node:fs')
  await testInfo.attach(`geometry-${name}`, { body: JSON.stringify(geometry, null, 2), contentType: 'application/json' })
  const evidenceRoot = process.env.AEROLINK_E2E_DIAGNOSTIC_DIR
  if (evidenceRoot) {
    mkdirSync(evidenceRoot, { recursive: true })
    writeFileSync(`${evidenceRoot}\\geometry-after-${name}.json`, JSON.stringify(geometry, null, 2))
    copyFileSync(path, `${evidenceRoot}\\after-${name}.png`)
  }
}

/** True only when the two containers genuinely occupy the same pixels; a wrapped layout that stacks
    them vertically is valid and must pass (Checkpoint A refinement 6). */
function visuallyCollide(a: Rect, b: Rect): boolean {
  return a !== null && b !== null && a.x < b.right && b.x < a.right && a.y < b.bottom && b.y < a.bottom
}

async function settleAt(page: Page, width: number, height: number) {
  await page.setViewportSize({ width, height })
  await page.evaluate(() => window.scrollTo(0, 0))
  await layoutSettled(page)
}

const assertHeaderContainsBadge = (width: number, g: Geometry) => {
  const { topBar, topBarInner, badge, badgeSummary, account, accountText, signOut } = g.rects
  expect(g.badgePresent, `badge renders in the shared header at ${width}px`).toBe(true)
  expect(topBar && badge, 'header and badge measured').toBeTruthy()
  expect(badge!.bottom, `#1048 at ${width}px: badge bottom ${badge!.bottom} leaves the header bottom ${topBar!.bottom}`).toBeLessThanOrEqual(topBar!.bottom + 1)
  expect(badge!.y, `#1048 at ${width}px: badge top leaves the header top ${topBar!.y}`).toBeGreaterThanOrEqual(topBar!.y - 1)
  expect(badge!.right, `#1048 at ${width}px: badge right leaves the header content`).toBeLessThanOrEqual(topBarInner!.right + 1)
  expect(signOut!.right, `#1048 at ${width}px: Sign out leaves the header content`).toBeLessThanOrEqual(topBarInner!.right + 1)
  expect(signOut!.bottom, `#1048 at ${width}px: Sign out leaves the header vertically`).toBeLessThanOrEqual(topBar!.bottom + 1)
  expect(g.accountTextVisible, `#1048 H04 at ${width}px: account name/role stay visible (no display:none fallback)`).toBe(true)
  expect(accountText!.width, `#1048 H04 at ${width}px: account text has measured width`).toBeGreaterThan(0)
  expect(visuallyCollide(badgeSummary ?? badge, account), `#1048 at ${width}px: badge summary collides with the account group`).toBe(false)
  expect(visuallyCollide(g.rects.brand, account), `#1048 at ${width}px: brand group collides with the account group`).toBe(false)
}

const assertDocumentContained = (width: number, g: Geometry) => {
  expect(g.scroll.x, `#1054 at ${width}px: measurements must start at the horizontal scroll origin`).toBe(0)
  expect(g.document.scrollWidth, `#1054 at ${width}px: document scrollWidth ${g.document.scrollWidth} exceeds the viewport`).toBeLessThanOrEqual(g.document.clientWidth + 1)
}

/** DEC-049: the summary is the activation control, so its own box must meet the 24x24 target in both
    dimensions, however short the declared label is (Checkpoint B round 2, F03). */
const assertDisclosureTarget = (context: string, g: Geometry) => {
  const summary = g.rects.badgeSummary
  expect(summary, `${context}: summary measured`).not.toBeNull()
  expect(summary!.width, `${context}: disclosure target width ${summary!.width}px is below the 24px minimum`).toBeGreaterThanOrEqual(24)
  expect(summary!.height, `${context}: disclosure target height ${summary!.height}px is below the 24px minimum`).toBeGreaterThanOrEqual(24)
}

/** Asserts containment for every visible leaf text element in the header — the wrapper rectangles can
    be correct while a text run escapes them, so the rendered text itself is measured (Checkpoint B,
    F01). A closed details hides its non-summary content, but its visible summary text MUST be audited,
    so only non-summary content of a closed details is excluded. expectedTexts asserts the known
    summary/status text really is among the audited runs. Returns the audited run texts. */
async function assertVisibleHeaderText(page: Page, context: string, expectedTexts: string[] = []): Promise<string[]> {
  const runs = await page.evaluate(() => {
    const header = document.querySelector('.projectsTopBar')
    if (!header) return null
    const headerRect = header.getBoundingClientRect()
    const walker = document.createTreeWalker(header, NodeFilter.SHOW_TEXT)
    const runs: Array<{ text: string; right: number; left: number; bottom: number; top: number }> = []
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim()
      if (!text) continue
      const el = node.parentElement
      if (!el) continue
      if (getComputedStyle(el).display === 'none' || getComputedStyle(el).visibility === 'hidden') continue
      // A closed details hides its non-summary content outside display:none; its summary stays visible
      // and must be audited with the rest.
      const holder = el.closest('details')
      if (holder && !(holder as HTMLDetailsElement).open && !el.closest('summary')) continue
      const range = document.createRange()
      range.selectNodeContents(node)
      for (const r of range.getClientRects()) {
        if (r.width === 0 || r.height === 0) continue
        runs.push({ text, left: r.left, right: r.right, top: r.top, bottom: r.bottom })
      }
    }
    return { runs, headerRight: headerRect.right, headerBottom: headerRect.bottom, clientWidth: document.documentElement.clientWidth }
  })
  expect(runs, `${context}: header present for visible-text audit`).not.toBeNull()
  expect(runs!.runs.length, `${context}: visible header text runs found`).toBeGreaterThanOrEqual(4)
  for (const expected of expectedTexts) {
    expect(runs!.runs.some(run => run.text === expected), `${context}: expected visible summary/status text "${expected}" was not among the audited runs (audited: ${runs!.runs.map(r => r.text).join(" | ")})`).toBe(true)
  }
  for (const run of runs!.runs) {
    expect(run.right, `${context}: visible text "${run.text.slice(0, 32)}" right edge ${run.right.toFixed(1)} exceeds the viewport ${runs!.clientWidth}`).toBeLessThanOrEqual(runs!.clientWidth + 1)
    expect(run.right, `${context}: visible text "${run.text.slice(0, 32)}" exceeds the header content`).toBeLessThanOrEqual(runs!.headerRight + 1)
    expect(run.bottom, `${context}: visible text "${run.text.slice(0, 32)}" exceeds the header bottom`).toBeLessThanOrEqual(runs!.headerBottom + 1)
    expect(run.top, `${context}: visible text "${run.text.slice(0, 32)}" starts above the header`).toBeGreaterThanOrEqual(-1)
  }
  return runs!.runs.map(run => run.text)
}

test('#1048 after: the HOME badge stays inside the portal header at desktop and narrow widths', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity())
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge).toBeVisible()
  await expect(badge.getByTestId('instance-label')).toHaveText('HOME')
  await expect(badge.getByTestId('main-currency')).toContainText('Current main')

  for (const [width, height] of [[1440, 900], [900, 800], [561, 700], [560, 760], [390, 760]] as const) {
    await settleAt(page, width, height)
    const g = await measureGeometry(page)
    await record(page, testInfo, `home-portal-${width}`, g)
    assertHeaderContainsBadge(width, g)
    assertDocumentContained(width, g)
    assertDisclosureTarget(`portal ${width}px closed`, g)
    // The closed summary's own text must be among the audited visible runs, not excluded with the
    // closed panel (Checkpoint B round 2, F04).
    await assertVisibleHeaderText(page, `portal ${width}px closed`, ['HOME', 'Current main'])
  }
})

test('#1048 F03 after: a one-character declared label still meets the 24x24 disclosure target', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity({
    mode: 'UNKNOWN',
    mainCurrency: null,
    instance: { id: 'short', label: 'Q', classification: 'WorkLaptopLocal', snapshot: null },
  }))
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge).toBeVisible()
  for (const [width, height] of [[1440, 900], [390, 760]] as const) {
    await settleAt(page, width, height)
    const g = await measureGeometry(page)
    await record(page, testInfo, `short-label-${width}`, g)
    assertHeaderContainsBadge(width, g)
    assertDocumentContained(width, g)
    assertDisclosureTarget(`short label ${width}px`, g)
    await assertVisibleHeaderText(page, `short label ${width}px closed`, ['Q'])
  }
})

test('#1048 F01 after: an unbroken long label and the opened disclosure stay contained at 560/561/390', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity({
    mode: 'UNKNOWN',
    mainCurrency: null,
    instance: {
      id: 'work-laptop',
      label: 'FLIGHTTESTLAPTOPLONGINSTALLATIONNAMEWITHOUTGAPS',
      classification: 'WorkLaptopLocal',
      snapshot: null,
    },
  }))
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge).toBeVisible()

  for (const [width, height] of [[560, 760], [561, 700], [390, 760]] as const) {
    for (const open of [false, true]) {
      const isOpen = await badge.evaluate(el => el.hasAttribute('open'))
      if (open !== isOpen) await badge.getByTestId('instance-summary').click()
      if (open) await expect(badge.getByTestId('instance-details')).toBeVisible()
      else await expect(badge.getByTestId('instance-details')).toBeHidden()
      await settleAt(page, width, height)
      const g = await measureGeometry(page)
      await record(page, testInfo, `unbroken-label-${open ? 'open' : 'closed'}-${width}`, g)
      const state = open ? 'open' : 'closed'
      const { badge: badgeRect, account } = g.rects
      const label = await labelRect(page)
      expect(badgeRect, 'badge measured').not.toBeNull()
      // The label must wrap inside the chip, not escape it (Checkpoint B, F01: the capped badge did not
      // constrain its flex child).
      expect(label!.right, `unbroken label at ${width}px ${state}: label right ${label!.right.toFixed(1)} escapes the badge right ${badgeRect!.right.toFixed(1)}`).toBeLessThanOrEqual(badgeRect!.right + 1)
      expect(label!.left).toBeGreaterThanOrEqual(badgeRect!.x - 1)
      expect(visuallyCollide(label, account), `unbroken label at ${width}px ${state} overlaps the account group`).toBe(false)
      assertDocumentContained(width, g)
      if (open) {
        const { badgePanel } = g.rects
        expect(badgePanel, 'opened panel measured').not.toBeNull()
        expect(badgePanel!.right, `opened panel at ${width}px leaves the viewport`).toBeLessThanOrEqual(g.document.clientWidth + 1)
      } else {
        assertDisclosureTarget(`unbroken label ${width}px`, g)
      }
      await assertVisibleHeaderText(page, `unbroken label ${width}px ${state}`, ['FLIGHTTESTLAPTOPLONGINSTALLATIONNAMEWITHOUTGAPS'])
    }
  }
})

async function labelRect(page: Page): Promise<Rect> {
  return page.evaluate(() => {
    const el = document.querySelector('[data-testid="instance-label"]')
    if (!el) return null
    const r = el.getBoundingClientRect()
    return { x: r.x, y: r.y, width: r.width, height: r.height, left: r.left, right: r.right, bottom: r.bottom }
  })
}

test('#1048 H04 after: the installation disclosure opens from the keyboard with the full supplied facts', async ({ page }) => {
  let identityReads = 0
  await page.route('**/health/identity', route => {
    identityReads++
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(homeIdentity()) })
  })
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge).toBeVisible()
  const summary = badge.getByTestId('instance-summary')
  await summary.focus()
  await expect(summary).toBeFocused()
  await page.keyboard.press('Enter')
  const panel = badge.getByTestId('instance-details')
  await expect(panel).toBeVisible()
  await expect(panel).toContainText('HOME CANONICAL (HomeCanonical)')
  await expect(panel).toContainText('c2602d93')
  await expect(panel).toContainText(/checked \d+m ago/)
  await expect(panel).toContainText('aerolink')
  await page.keyboard.press('Enter')
  await expect(panel).toBeHidden()
  // Opening and closing the disclosure is presentation only: it must not add a single request. The
  // baseline count is taken after the initial reads settle (dev StrictMode double-mounts the effect);
  // only a delta across open/close would be a defect.
  const readsBeforeToggle = identityReads
  await summary.focus()
  await page.keyboard.press('Enter')
  await expect(panel).toBeVisible()
  await page.keyboard.press('Enter')
  await expect(panel).toBeHidden()
  expect(identityReads, 'the disclosure must not trigger any additional identity read').toBe(readsBeforeToggle)
})

test('#1054 after: project setup releases the body floor and stays contained across intermediate widths', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity())
  await login(page, 'admin', { openProject: false })
  // Owned disposable state only: this navigation POSTs /api/project-setups against the run's
  // throwaway SQLite database (never the persistent installation).
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Create New Project' })).toBeVisible()

  for (const [width, height] of [[900, 800], [959, 800], [761, 800], [621, 800], [561, 700]] as const) {
    await settleAt(page, width, height)
    const g = await measureGeometry(page)
    await record(page, testInfo, `setup-${width}`, g)
    expect(g.document.bodyMinWidth, `#1054 at ${width}px: the body floor must be released on the setup portal route`).toBe('0px')
    assertDocumentContained(width, g)
    if (width === 900) {
      assertHeaderContainsBadge(width, g)
      const { signOut } = g.rects
      expect(signOut!.right, `#1054 at 900px: Sign out stays inside the viewport`).toBeLessThanOrEqual(g.document.clientWidth)
    }
  }
})

test('#1054 after: the requirement-ladder step stays contained with controls at the scroll origin', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity())
  await login(page, 'admin', { openProject: false })
  await page.goto('/projects/new')
  await expect(page.getByRole('heading', { name: 'Create New Project' })).toBeVisible()
  await page.getByRole('button', { name: /Requirement ladder/ }).click()
  await expect(page.getByRole('button', { name: /Requirement ladder/ })).toHaveClass(/selected/)
  await settleAt(page, 900, 800)
  const g = await measureGeometry(page)
  await record(page, testInfo, 'setup-ladder-900', g)
  expect(g.document.bodyMinWidth, '#1054: the body floor must stay released on the ladder step').toBe('0px')
  assertDocumentContained(900, g)
  await expect(page.getByRole('button', { name: /Requirement ladder/ })).toBeVisible()
})

test('#1054 after guard: the Projects portal stays contained at the adjacent breakpoint values', async ({ page }, testInfo) => {
  await installIdentity(page, homeIdentity())
  await login(page, 'admin', { openProject: false })
  for (const [width, height] of [[959, 800], [761, 800]] as const) {
    await settleAt(page, width, height)
    const g = await measureGeometry(page)
    await record(page, testInfo, `projects-${width}`, g)
    expect(g.document.bodyMinWidth, `#1054 at ${width}px: Projects already opts out of the body floor`).toBe('0px')
    assertDocumentContained(width, g)
  }
})

test('#1048 F02 after: a long declared label with snapshot wraps in the narrow portal without currency claims', async ({ page }, testInfo) => {
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
        activatedAtUtc: new Date(Date.now() - 4 * 86_400_000).toISOString(),
      },
    },
  }))
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge).toBeVisible()
  await settleAt(page, 561, 700)
  const g = await measureGeometry(page)
  await record(page, testInfo, 'long-label-561', g)
  assertHeaderContainsBadge(561, g)
  assertDocumentContained(561, g)
  await expect(badge).not.toContainText('Current main')
  await expect(badge).not.toContainText('Main unverified')
  // The snapshot fact is reachable through the disclosure even where the summary suffix hides. The
  // opened state is measured and inspected separately from the closed state (Checkpoint B, F01).
  await badge.getByTestId('instance-summary').click()
  const panel = badge.getByTestId('instance-details')
  await expect(panel).toBeVisible()
  await expect(panel).toContainText('HOME CANONICAL')
  await settleAt(page, 561, 700)
  const openGeometry = await measureGeometry(page)
  await record(page, testInfo, 'long-label-561-open', openGeometry)
  assertDocumentContained(561, openGeometry)
  await assertVisibleHeaderText(page, 'long label 561px open')
  const { badge: badgeRect, topBar } = g.rects
  expect(badgeRect!.height, 'the long label wraps to a taller summary instead of overflowing').toBeGreaterThan(24)
  expect(badgeRect!.bottom, 'the wrapped badge stays inside the header').toBeLessThanOrEqual(topBar!.bottom + 1)
})

test('#1048 H06 after: the header stays contained at the layout a 200%-zoomed desktop window computes', async ({ page }, testInfo) => {
  // Browser page zoom multiplies CSS pixel sizes, so a 1440px window at 200% zoom lays out at 720 CSS px.
  // This lane proves the wrap/reflow mechanism at that layout width. The visual magnification itself is
  // not rendered by this tooling lane; the effective-text-size check therefore has this limitation, and
  // the full browser-zoom presentation remains an operator check.
  await installIdentity(page, homeIdentity())
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge).toBeVisible()
  await settleAt(page, 720, 450)
  const g = await measureGeometry(page)
  await record(page, testInfo, 'zoom-equivalent-720', g)
  assertHeaderContainsBadge(720, g)
  assertDocumentContained(720, g)
})

test('#1054 P05 after: the workspace keeps its floor, the portal releases it, and navigation restores each state', async ({ page }, testInfo) => {
  test.skip(!process.env.AEROLINK_SHOWCASE_SEED, 'requires the seeded disposable workspace lane')
  // The HOME fixture keeps the workspace/builds/consumer evidence on the identity the header defects
  // are about; the undeclared default of a bare disposable API is a different evidence state.
  await installIdentity(page, homeIdentity())
  await login(page, 'admin')
  const workspaceUrl = page.url()
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()

  await settleAt(page, 900, 800)
  const workspace = await measureGeometry(page)
  await record(page, testInfo, 'workspace-900', workspace)
  expect(workspace.document.bodyMinWidth, '#1054 P05: the controlled workspace keeps its 960px floor').toBe('960px')

  await page.goto('/projects')
  await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible()
  await settleAt(page, 900, 800)
  const portal = await measureGeometry(page)
  await record(page, testInfo, 'portal-return-900', portal)
  expect(portal.document.bodyMinWidth, '#1054 P05: returning to the portal releases the floor again').toBe('0px')
  assertDocumentContained(900, portal)

  // H02/P02: the Builds landing shares the consolidated header — measure it directly.
  await page.locator('[data-project-card]').first().click()
  await expect(page.getByRole('heading', { name: 'Software Builds' })).toBeVisible()
  await settleAt(page, 900, 800)
  const builds = await measureGeometry(page)
  await record(page, testInfo, 'builds-900', builds)
  expect(builds.document.bodyMinWidth, '#1054: the Builds landing already opts out of the body floor').toBe('0px')
  assertHeaderContainsBadge(900, builds)
  assertDocumentContained(900, builds)

  // P04/H05: the remaining PortalHeader consumers get a header smoke at 900px, reached the way users
  // reach them: buttons on the Builds landing and the Project configuration sections. These are
  // administration surfaces: the header is the scope — unrelated content width is reported, not failed.
  const smokeConsumer = async (label: string) => {
    await expect(page.locator('.projectsTopBar')).toBeVisible()
    await settleAt(page, 900, 800)
    const g = await measureGeometry(page)
    await record(page, testInfo, `consumer-${label.toLowerCase().replace(/\s+/g, '-')}-900`, g)
    const { topBar, badge, account, signOut } = g.rects
    expect(topBar && badge, `${label}: shared header renders`).toBeTruthy()
    expect(badge!.bottom, `${label}: badge leaves the header`).toBeLessThanOrEqual(topBar!.bottom + 1)
    expect(signOut!.right, `${label}: Sign out leaves the header content`).toBeLessThanOrEqual(topBarInnerSafe(g) + 1)
    expect(g.accountTextVisible, `${label}: account identity stays visible`).toBe(true)
    expect(visuallyCollide(badge, account), `${label}: badge collides with the account group`).toBe(false)
  }
  const backToBuilds = () => page.getByRole('button', { name: /Software Builds/i }).first().click()

  for (const button of ['Personnel', 'Imported baselines'] as const) {
    await page.getByRole('button', { name: button }).click()
    await smokeConsumer(button)
    await backToBuilds()
    await expect(page.getByRole('heading', { name: 'Software Builds' })).toBeVisible()
  }

  await page.getByRole('button', { name: 'Project configuration' }).click()
  await smokeConsumer('Project configuration')
  await page.getByRole('button', { name: 'Approval configuration' }).click()
  await smokeConsumer('Approval configuration')

  await page.goto(workspaceUrl)
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()
  const restored = await measureGeometry(page)
  expect(restored.document.bodyMinWidth, '#1054 P05: the workspace floor is active again after returning').toBe('960px')
})

function topBarInnerSafe(g: Geometry): number {
  return g.rects.topBarInner ? g.rects.topBarInner.right : (g.rects.topBar?.right ?? g.document.clientWidth)
}

test('#1048 F02 after: the sidebar badge wraps a long label inside the measured column and opens its disclosure', async ({ page }, testInfo) => {
  test.skip(!process.env.AEROLINK_SHOWCASE_SEED, 'requires the seeded disposable workspace lane')
  // A mutable payload lets one route serve both the long-label and short-label identity states.
  let payload = homeIdentity({
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
  })
  await page.route('**/health/identity', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(payload) }))
  await login(page, 'admin')
  const brandBadge = page.locator('.brand').getByTestId('instance-badge')
  await expect(brandBadge).toBeVisible()

  for (const [width, height] of [[1280, 900], [800, 700]] as const) {
    await settleAt(page, width, height)
    const g = await measureGeometry(page)
    await record(page, testInfo, `sidebar-${width}`, g)
    const { sidebar, badge } = g.rects
    expect(sidebar, 'the workspace sidebar is measured from the live cascade').not.toBeNull()
    expect(badge!.right, `#1048 F02 at ${width}px: the badge leaves the measured ${sidebar!.width}px sidebar column`).toBeLessThanOrEqual(sidebar!.right + 1)
    expect(badge!.x, `#1048 F02 at ${width}px: the badge starts inside the sidebar`).toBeGreaterThanOrEqual(sidebar!.x - 1)
    expect(g.scroll.x, `at ${width}px: measurements must start at the horizontal scroll origin`).toBe(0)
    // Document containment is deliberately NOT asserted here: the controlled workspace keeps its
    // 960px floor at intermediate widths (asserted in the P05 test), so whole-document containment
    // is a portal-only expectation. What F02 requires is containment within the sidebar column.
    await expect(brandBadge).not.toContainText('Current main')
  }

  // The disclosure target must meet DEC-049's 24x24 in BOTH workspace densities, for the long label
  // AND the short label, and the opened panel is measured inside each density (Checkpoint B rounds
  // 2-3, F03/F04). Density is applied the way a person applies it - the stored preference, then a
  // reload - and the rendered data-density value is awaited before measuring.
  for (const density of ['comfortable', 'compact'] as const) {
    for (const [fixtureName, fixture] of [
      ['long-label', homeIdentity({
        mode: 'UNKNOWN', mainCurrency: null,
        instance: {
          id: 'work-laptop', label: 'FLIGHT TEST LAPTOP LONG INSTALLATION NAME', classification: 'WorkLaptopLocal',
          snapshot: { sourceLabel: 'HOME CANONICAL', sourceSha: 'd4c3b2a1d4c3b2a1d4c3b2a1d4c3b2a1d4c3b2a1', createdAtUtc: new Date(Date.now() - 5 * 86_400_000).toISOString(), activatedAtUtc: null },
        },
      })],
      ['short-label', homeIdentity({
        mode: 'UNKNOWN', mainCurrency: null,
        instance: { id: 'short', label: 'Q', classification: 'WorkLaptopLocal', snapshot: null },
      })],
    ] as const) {
      payload = fixture
      await page.evaluate(value => localStorage.setItem('aerolink-density', value), density)
      await page.reload()
      await expect.poll(() => page.evaluate(() => document.documentElement.dataset.density), { timeout: 10_000 }).toBe(density)
      await expect(brandBadge).toBeVisible()
      await settleAt(page, 1280, 900)
      const g = await measureGeometry(page)
      await record(page, testInfo, `sidebar-${fixtureName}-${density}-closed`, g)
      assertDisclosureTarget(`sidebar ${fixtureName} ${density} density`, g)
      const { sidebar, badge } = g.rects
      expect(badge!.right, `sidebar ${fixtureName} ${density}: the badge leaves the ${sidebar!.width}px column`).toBeLessThanOrEqual(sidebar!.right + 1)
      await brandBadge.getByTestId('instance-summary').click()
      const panel = brandBadge.getByTestId('instance-details')
      await expect(panel).toBeVisible()
      await settleAt(page, 1280, 900)
      const opened = await measureGeometry(page)
      await record(page, testInfo, `sidebar-${fixtureName}-${density}-open`, opened)
      const { badgePanel } = opened.rects
      expect(badgePanel, 'the opened panel is measured').not.toBeNull()
      expect(badgePanel!.right, `sidebar ${fixtureName} ${density}: the opened panel leaves the column`).toBeLessThanOrEqual(sidebar!.right + 1)
      await expect(brandBadge).not.toContainText('Current main')
    }
  }
})
