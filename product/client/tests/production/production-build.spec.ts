import { expect, test } from '@playwright/test'
import { writeFile } from 'node:fs/promises'
import type { Page } from '@playwright/test'
import { apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from '../auth'

/**
 * What can only be checked against a build.
 *
 * The rest of the suite runs on `vite dev`, which serves unbundled modules and injects each stylesheet when
 * the module importing it evaluates. A build chunks the code, extracts every stylesheet into one hashed file,
 * minifies, and resolves each import at bundle time. Anything sensitive to which of those two is running —
 * cascade order, chunk boundaries, an asset URL, a dependency only dev could reach — was invisible until this
 * file existed, because nothing anywhere had ever served `client/dist` to a browser.
 *
 * Each test states the failure it exists to catch. A production gate that merely repeats the dev gate costs a
 * build and proves nothing.
 */

/**
 * The routes the product itself offers, read from its navigation rather than written down here.
 *
 * Every route is nested under `/programs/{id}/projects/{id}/releases/{id}/`, so a hardcoded `/baselines` is
 * not a route at all — it resolves against the origin and lands on a path the client does not know, which
 * looks exactly like a chunk that failed to load. Asking the navigation removes both the guess and the
 * dependence on identifiers that change every seed.
 */
async function navigationRoutes(page: Page, expected?: RegExp) {
  const links = page.locator('nav[aria-label="Primary navigation"] a[href]')
  await expect(links.first()).toBeAttached({ timeout: 30_000 })
  // Waiting for the *first* link is not waiting for the navigation.
  //
  // The sidebar renders its groups and their entries across renders, so the first anchor can be attached
  // while the one the caller needs is still on its way. Reading every href at that moment returns a partial
  // list, and the caller then fails with "the navigation must offer …" — a message that reads like the route
  // is gone when it had simply not arrived. It passes locally and fails on a loaded runner, which is exactly
  // the shape of a race rather than a missing route.
  if (expected) await expect.poll(async () =>
    (await links.evaluateAll(nodes => nodes.map(node => (node as HTMLAnchorElement).getAttribute('href') ?? '')))
      .some(href => expected.test(href)),
    { timeout: 30_000, message: `the navigation never rendered a route matching ${expected}` }).toBe(true)
  const hrefs = await links.evaluateAll(nodes => nodes.map(node => (node as HTMLAnchorElement).getAttribute('href') ?? ''))
  return hrefs.filter(Boolean)
}

test('the served document is the build, and it loads nothing from anywhere else', async ({ page, baseURL }) => {
  const origin = new URL(baseURL!).origin
  const offOrigin: string[] = []
  const failed: string[] = []
  const consoleErrors: string[] = []

  page.on('request', request => {
    const url = new URL(request.url())
    if (url.protocol !== 'data:' && url.origin !== origin) offOrigin.push(request.url())
  })
  page.on('response', response => {
    // 401 and 403 are answers, not failures: the sign-in page asks /api/auth/me who it is talking to and is
    // correctly told nobody. What matters here is a resource the build referenced and the server cannot serve,
    // and anything the server got wrong.
    const status = response.status()
    const missingAsset = status === 404 && /\/assets\/|\.(css|js|woff2?|svg|png)$/.test(new URL(response.url()).pathname)
    if (status >= 500 || missingAsset) failed.push(`${status} ${response.url()}`)
  })
  page.on('console', message => {
    // The browser logs the 401 from /api/auth/me as a failed resource load. It is the correct answer to "who
    // is signed in" on the sign-in page, so it is filtered here rather than in the product.
    const text = message.text()
    if (message.type() === 'error' && !/status of 40[13]/.test(text)) consoleErrors.push(text)
  })

  const response = await page.goto('/')
  expect(response?.status()).toBe(200)

  // A content-hashed entry script is the signature of a build. `vite dev` serves /src/main.tsx instead, so
  // this is what proves the gate is aimed at the built artifact and not silently testing dev over again.
  const html = await page.content()
  expect(html, 'the document should reference a content-hashed entry bundle').toMatch(/\/assets\/index-[\w-]+\.js/)
  expect(html, 'a build must not reference the dev entry module').not.toContain('/src/main.tsx')

  await expect(page.getByRole('button', { name: /Sign in securely/ })).toBeVisible()

  const styling = await page.evaluate(() => ({
    sheets: [...document.styleSheets].map(sheet => {
      // A cross-origin stylesheet throws here. That is the shape a reintroduced CDN reference would take.
      try {
        return { href: sheet.href, rules: sheet.cssRules.length }
      } catch {
        return { href: sheet.href, rules: -1 }
      }
    }),
    bodyFont: getComputedStyle(document.body).fontFamily,
    // A browser-default background on a button is the tell that the stylesheet never applied.
    firstButtonBackground: getComputedStyle(document.querySelector('button')!).backgroundColor,
  }))

  const bundled = styling.sheets.find(sheet => /\/assets\/style-[\w-]+\.css$/.test(sheet.href ?? ''))
  expect(bundled, `no hashed stylesheet among ${JSON.stringify(styling.sheets)}`).toBeTruthy()
  expect(bundled!.rules, 'the extracted stylesheet should carry the whole design system').toBeGreaterThan(1000)
  // Self-hosted per DEC-047. Fetched from a CDN and blocked, the family would fall back to the generic.
  expect(styling.bodyFont).toContain('DM Sans')
  expect(styling.firstButtonBackground).not.toBe('rgb(239, 239, 239)')

  expect(offOrigin, `the client requested off-origin resources: ${offOrigin.join(', ')}`).toEqual([])
  expect(failed, `requests failed: ${failed.join(', ')}`).toEqual([])
  expect(consoleErrors, `console errors: ${consoleErrors.join(' | ')}`).toEqual([])
})

test('the API and the document are served with opposite content security policies', async ({ request, baseURL }) => {
  const document = await request.get(`${baseURL}/`)
  const api = await request.get(`${baseURL}/health`)

  const documentPolicy = document.headers()['content-security-policy'] ?? ''
  const apiPolicy = api.headers()['content-security-policy'] ?? ''

  // Serving the document under the API's policy is a blank page: `default-src 'none'` forbids the bundle from
  // loading at all. This is the assertion that catches one policy being applied to both.
  expect(documentPolicy).toContain("default-src 'self'")
  expect(documentPolicy).toContain("script-src 'self'")
  expect(documentPolicy).not.toContain('sandbox')
  // The build inlines assets under its size threshold, so blocking data: URIs silently costs the self-hosted
  // typefaces. Asserted because the omission looks tighter and is simply broken.
  expect(documentPolicy).toContain("font-src 'self' data:")
  // connect-src 'self' is what makes DEC-047 enforced by the browser rather than remembered by people.
  expect(documentPolicy).toContain("connect-src 'self'")
  expect(apiPolicy).toContain("default-src 'none'")
  expect(apiPolicy).toContain('sandbox')

  // A hashed asset may be cached forever, because a new build is a new name. The entry document may not, or an
  // upgraded deployment keeps handing out the previous release's HTML.
  const asset = (await document.text()).match(/\/assets\/index-[\w-]+\.js/)![0]
  expect((await request.get(`${baseURL}${asset}`)).headers()['cache-control']).toContain('immutable')
  expect(document.headers()['cache-control']).toBe('no-cache')
})

test('a deep link reloads, because the server falls back to the client', async ({ page, request, baseURL }) => {
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')

  const routes = await navigationRoutes(page, /\/change-requests/)
  const deepLink = routes.find(href => href.includes("/change-requests"))
  expect(deepLink, 'the navigation must offer a change-request route to deep link into').toBeTruthy()
  await page.goto(deepLink!, { waitUntil: 'load' })
  const headings = page.locator('h1, h2, h3')

  // Waited for the page the address names, not for whichever heading paints first.
  //
  // The shell renders its own default heading while the router resolves the address, so reading the first
  // heading once captured "Command Center" — and the reload then settled on the change-request page and was
  // compared against a baseline that was never the deep-linked page at all. The comparison below was already
  // polled for exactly this reason; the read it compares against had the same race and kept it.
  await expect.poll(async () => (await headings.first().textContent())?.trim(), { timeout: 30_000 })
    .toMatch(/Change Requests/)
  const before = await headings.first().textContent()

  // The reload is the test. With no fallback the server holds no file at this path and answers 404, so the
  // product would work until somebody bookmarked a page or pressed F5.
  const reloaded = await page.reload({ waitUntil: 'load' })
  expect(reloaded?.status(), 'reloading a client route must serve the client, not 404').toBe(200)
  await expect(headings.first()).toBeVisible({ timeout: 30_000 })
  // Polled rather than sampled once. `headings.first()` is whichever heading renders first, and on a slower
  // machine the app paints the Command Center heading while it resolves the deep-linked route — so a single
  // read raced the router and compared the wrong heading. This failed the first time these journeys ran on
  // Windows and had passed on Linux throughout, which is the same "fast enough to look correct" trap that
  // measuring a surface before it settled produced twice elsewhere in this suite.
  //
  // If the route genuinely never resolves, this still fails after the timeout — waiting properly is what
  // tells the two apart.
  await expect.poll(async () => (await headings.first().textContent())?.trim(), { timeout: 30_000 })
    .toBe(before?.trim())

  // An unmatched API path must stay an API error rather than be handed the document, or a mistyped route
  // becomes a JSON parse failure somewhere far from its cause.
  const missing = await request.get(`${baseURL}/api/no-such-endpoint`)
  expect(missing.status()).toBeGreaterThanOrEqual(400)
  expect(missing.headers()['content-type'] ?? '').toContain('json')
})

test('typed change-request URLs preserve System and Software navigation context', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const response = await request.get(`/api/change-requests?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&pageSize=200`)
  expect(response.ok(), await response.text()).toBeTruthy()
  const records = (await response.json()).items as { id: string; type: 'System' | 'Software' }[]
  const system = records.find(item => item.type === 'System')
  const software = records.find(item => item.type === 'Software')
  expect(system).toBeTruthy()
  expect(software).toBeTruthy()

  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  await login(page)

  await page.goto(`${root}/systems/change-requests/${system!.id}`)
  await expect(page).toHaveURL(`${root}/systems/change-requests/${system!.id}`)
  await expect(page.getByRole('link', { name: 'System Change Requests' })).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('link', { name: 'New System SRCR' })).toHaveCount(0)

  await page.goto(`${root}/software/change-requests/${software!.id}`)
  await expect(page).toHaveURL(`${root}/software/change-requests/${software!.id}`)
  await expect(page.getByRole('link', { name: 'Software Change Requests' })).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('link', { name: 'New Software Change Request' })).toHaveCount(0)

  // Old links and a caller-supplied type mismatch are both replaced from the authorized record type.
  await page.goto(`${root}/change-requests/${software!.id}`)
  await expect(page).toHaveURL(`${root}/software/change-requests/${software!.id}`)
  await page.goto(`${root}/systems/change-requests/${software!.id}`)
  await expect(page).toHaveURL(`${root}/software/change-requests/${software!.id}`)
})

test('the first protected production mutation after deep-linked sign-in creates durable controlled state', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  const title = `Production mutation ${Date.now()}`
  const deepLink = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/systems/change-requests/new`

  // Start signed out on the protected destination. Login must replace any unauthenticated CSRF state before
  // this first write becomes actionable; requiring a refresh here is the regression from #119.
  await page.goto(deepLink)
  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('AeroLink!2026')
  await page.getByRole('button', { name: /Sign in securely/ }).click()
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible()

  await page.getByRole('button', { name: '+ Introduce System requirement' }).click()
  await page.getByLabel('Title').fill(title)
  await page.getByLabel('Problem', { exact: true }).fill('The compiled production client must perform protected writes.')
  await page.getByRole('textbox', { name: 'Analysis', exact: true }).fill('A durable server query must prove the write rather than trusting the success ceremony.')
  await page.getByLabel('Solution').fill('Resolve relative API URLs and bind CSRF state to the signed-in session.')
  await page.getByLabel('Requirement statement').fill('The production client shall preserve authenticated mutation capability.')
  const savedResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/change-request-drafts')
  await page.getByRole('button', { name: 'Save SRCR Draft' }).click()
  const saved = await savedResponse
  expect(saved.status(), await saved.text()).toBe(201)
  const created = await saved.json()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()

  await apiLogin(request)
  // Independently read the exact saved record; the populated register spans several pages.
  const detail = await request.get(`/api/change-requests/${created.id}`)
  expect(detail.ok(), await detail.text()).toBeTruthy()
  expect(await detail.json()).toEqual(expect.objectContaining({
    id: created.id,
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    title,
    problem: 'The compiled production client must perform protected writes.',
  }))
})

test('the production wrapper accepts relative and absolute unsafe request URLs for every supported method', async ({ page, baseURL }) => {
  const methods: string[] = []
  await page.route('**/api/client-wrapper-probe*', async route => {
    methods.push(route.request().method())
    await route.fulfill({ status: 204 })
  })
  await login(page)

  const results = await page.evaluate(async origin => {
    const targets = [
      ['/api/client-wrapper-probe?shape=relative', 'POST'],
      [`${origin}/api/client-wrapper-probe?shape=absolute`, 'PUT'],
      ['/api/client-wrapper-probe?shape=relative', 'PATCH'],
      [`${origin}/api/client-wrapper-probe?shape=absolute`, 'DELETE'],
    ] as const
    return Promise.all(targets.map(async ([url, method]) => {
      const response = await fetch(url, { method })
      return { method, status: response.status }
    }))
  }, new URL(baseURL!).origin)

  expect(results).toEqual([
    { method: 'POST', status: 204 },
    { method: 'PUT', status: 204 },
    { method: 'PATCH', status: 204 },
    { method: 'DELETE', status: 204 },
  ])
  expect(methods).toEqual(['POST', 'PUT', 'PATCH', 'DELETE'])
})

test('verification mutation failures retain the engineer input and only confirmed success creates one immutable result', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)

  // This journey is about the compiled client's mutation and failure contract, not about where procedures
  // come from. It used to write one through the direct-create route and sign it; both are gone, because a
  // procedure is introduced by a test change request and approved with that package. So it takes an approved
  // procedure the build already carries — which the candidate list holds by definition, since that list is
  // exactly the approved procedures not yet in the set.
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Results' }).click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })

  const candidate = page.getByRole('checkbox', { name: /SYSTP-\d{6}\.\d{2}/ }).first()
  await expect(candidate).toBeVisible({ timeout: 30_000 })
  const displayNumber = (await candidate.getAttribute('aria-label') ?? await candidate.evaluate(node =>
    (node.closest('label') ?? node.parentElement)?.textContent ?? ''))!.match(/SYSTP-\d{6}\.\d{2}/)![0]

  const procedureResponse = await request.get(
    `/api/test-procedures?projectId=${showcase.projectId}&search=${displayNumber.replace(/\.\d{2}$/, '')}&page=1&pageSize=1`)
  expect(procedureResponse.ok(), await procedureResponse.text()).toBeTruthy()
  const procedure = (await procedureResponse.json()).items[0]
  expect(procedure.state).toBe('Approved')

  await candidate.check()
  await expect(candidate).toBeChecked()
  const addToSet = page.getByRole('button', { name: 'Add — covers a change' })
  await expect(addToSet).toBeEnabled()
  await addToSet.click()
  const row = page.locator('.testSetRow').filter({ hasText: procedure.displayNumber })
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: /Record result|Record retest/ }).click()

  const form = page.locator('.recordResultModal form')
  await form.getByLabel('Configuration under test').fill('Production qualification rig')
  await form.getByLabel('Outcome').selectOption('Pass')
  await form.getByLabel('Evidence reference').fill('evidence/production-mutation.json')
  await form.getByLabel('Determination', { exact: true }).fill('The compiled client recorded the protected result exactly once.')

  await page.route('**/api/test-executions', route => route.abort('connectionfailed'))
  await form.getByRole('button', { name: 'Record determination' }).click()
  await expect(page.getByRole('alert')).toContainText(/Failed to fetch|could not/i)
  await expect(form.getByLabel('Determination', { exact: true })).toHaveValue('The compiled client recorded the protected result exactly once.')

  await page.unroute('**/api/test-executions')
  await page.route('**/api/test-executions', route => route.fulfill({
    status: 409,
    contentType: 'application/json',
    body: JSON.stringify({ error: 'A conflicting result version is already being reviewed.' }),
  }))
  await form.getByRole('button', { name: 'Record determination' }).click()
  await expect(page.getByRole('alert')).toContainText('A conflicting result version is already being reviewed.')
  await expect(form).toBeVisible()

  await page.unroute('**/api/test-executions')
  await form.getByRole('button', { name: 'Record determination' }).click()
  await expect(form).toHaveCount(0, { timeout: 30_000 })
  await row.getByRole('button', { name: 'Runs' }).click()
  await expect(row.locator('.runList li').filter({ hasText: 'compiled client recorded' })).toBeVisible({ timeout: 30_000 })

  // Exactly one result from this journey: the aborted attempt and the refused one recorded nothing, and the
  // confirmed one recorded once. Matched on the evidence this journey supplies rather than on the procedure,
  // because the procedure is one the build already carries and it already has runs behind it from an earlier
  // build — counting those would be counting somebody else's work as this test's output.
  const executionsResponse = await request.get(`/api/test-executions?projectId=${showcase.projectId}`)
  expect(executionsResponse.ok(), await executionsResponse.text()).toBeTruthy()
  const executions = await executionsResponse.json()
  const recorded = executions.filter((item: { procedureRevisionId: string; evidenceReference: string }) =>
    item.procedureRevisionId === procedure.revisionId
    && item.evidenceReference === 'evidence/production-mutation.json')
  expect(recorded).toEqual([
    expect.objectContaining({
      determination: 'The compiled client recorded the protected result exactly once.',
      evidenceReference: 'evidence/production-mutation.json',
    }),
  ])
})

function productionDesignReport() {
  // Station portals adopt existing nodes into another document, preserving their original prototypes.
  // Namespaces and capabilities identify SVG across that boundary; child-realm instanceof does not.
  const svgGraphics = (element: Element): element is SVGGraphicsElement =>
    element.namespaceURI === 'http://www.w3.org/2000/svg' &&
    typeof (element as SVGGraphicsElement).getScreenCTM === 'function'
  const svgText = (element: Element) => element.namespaceURI === 'http://www.w3.org/2000/svg' && element.localName === 'text'
  const visible = (element: Element) => {
    if (!element.checkVisibility({ visibilityProperty: true })) return false
    const box = element.getBoundingClientRect()
    return box.width > 0 && box.height > 0
  }
  // Smallest singular value of the rendered affine transform. Column lengths alone miss skew and
  // rotated nonuniform scaling; this measures the most compressed direction without penalizing rotation.
  const minimumScale = (matrix: DOMMatrix) => {
    const determinant = matrix.a * matrix.d - matrix.b * matrix.c
    // This equivalent hypot form avoids discriminant cancellation for an unscaled rotation at 12px.
    const maximum = (Math.hypot(matrix.a + matrix.d, matrix.b - matrix.c) +
      Math.hypot(matrix.a - matrix.d, matrix.b + matrix.c)) / 2
    return maximum ? Math.abs(determinant) / maximum : 0
  }
  const fontPixels = (element: Element) => {
    const size = parseFloat(getComputedStyle(element).fontSize)
    if (svgGraphics(element)) {
      // Includes responsive viewBox sizing, SVG transforms and CSS transforms on layout ancestors.
      const matrix = element.getScreenCTM()
      return matrix ? size * minimumScale(matrix) : 0
    }
    return size
  }
  // DEC-117 owns legibility inside the reader-zoomed Digital Thread canvas. Its surrounding UI remains
  // audited, as does every other surface. Physical CDU key legends retain their #1444 fit owner.
  const leaves = [...document.querySelectorAll('main *, body > div > *')].filter(element =>
    visible(element) &&
    (!element.children.length || (svgText(element) &&
      [...element.childNodes].some(node => node.nodeType === Node.TEXT_NODE && node.textContent?.trim()))) &&
    (element.textContent || '').trim().length > 0 &&
    !element.closest('.dtCanvasScene') && !element.closest('.fmsCduKey .legend'),
  )
  const label = (element: Element) => svgText(element) && element.children.length
    ? [...element.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join('').trim()
    : (element.textContent || '').trim()
  const fonts = leaves.map(element => ({
    text: label(element), authoredPixels: parseFloat(getComputedStyle(element).fontSize),
    effectivePixels: fontPixels(element),
    svg: svgGraphics(element),
    element: element.tagName.toLowerCase(),
    testId: element.getAttribute('data-testid'),
    box: element.getBoundingClientRect().toJSON(),
  }))
  return {
    heading: [...document.querySelectorAll('h1, h2, h3')].some(visible),
    // A labelled main is the Digital Thread's readiness signal (#880), which intentionally has no H1.
    landmark: !!document.querySelector('main[aria-label]'),
    text: (document.querySelector('main')?.textContent || document.body.textContent || '').trim().length,
    boundary: /went wrong|failed to load|Something broke/i.test(document.body.textContent || ''),
    // Ignore only affine floating-point roundoff at the unchanged 12px boundary.
    tiny: [...new Set(fonts.filter(font => font.effectivePixels < 12 - 1e-8)
      .map(font => `${font.text.slice(0, 24)} @ ${font.effectivePixels.toFixed(2)}px`))],
    fonts: fonts.filter(font => font.svg || font.effectivePixels < 12 - 1e-8),
    unstyled: [...new Set([...document.querySelectorAll('button')]
      .filter(element => visible(element) && getComputedStyle(element).backgroundColor === 'rgb(239, 239, 239)')
      .map(element => (element.textContent || '').trim().slice(0, 24)))],
    overflow: document.documentElement.scrollWidth > window.innerWidth + 1,
  }
}

test('every workspace chunk arrives and keeps the design contract in both densities', async ({ page, request }) => {
  test.setTimeout(600_000)
  await page.setViewportSize({ width: 1440, height: 900 })

  // One owner, exercised against deliberately small rendered labels before auditing the real build.
  // Authored sizes all meet 12px; only the browser's viewBox and composed transforms reveal the defect.
  await page.setContent(`<main><h1>Readability controls</h1>
    <svg width="100" height="60" viewBox="0 0 200 120"><text x="8" y="30" font-size="16">viewBox control</text></svg>
    <svg width="240" height="100"><g transform="translate(40 40) rotate(30) scale(2 .5)"><text font-size="16">rotated control</text></g></svg>
    <svg width="240" height="100"><g transform="translate(20 40) skewX(60)"><text font-size="16">skew control</text></g></svg>
    <div style="width:240px; transform:rotate(30deg); transform-origin:top left"><svg width="240" height="100" style="transform:scale(2, .5); transform-origin:top left"><text x="8" y="30" font-size="16">layout control</text></svg></div>
    <svg width="240" height="100"><g transform="translate(20 40) rotate(30)"><text font-size="12">readable control</text></g></svg>
    <svg width="240" height="100"><g transform="translate(20 40) rotate(6)"><text font-size="12">rotation boundary control</text></g></svg>
    <div style="width:240px; transform:rotate(30deg); transform-origin:top left"><svg width="240" height="100"><text x="8" y="30" font-size="12">layout readable control</text></svg></div>
  </main>`)
  const controlReport = await page.evaluate(productionDesignReport)
  expect(controlReport.tiny.map(label => label.split(' @ ')[0])).toEqual([
    'viewBox control', 'rotated control', 'skew control', 'layout control',
  ])

  const chunks = new Set<string>()
  page.on('request', request => {
    const path = new URL(request.url()).pathname
    if (/^\/assets\/.+\.js$/.test(path) && !/^\/assets\/index-/.test(path)) chunks.add(path)
  })

  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const routes = await navigationRoutes(page, /\/change-requests/)
  expect(routes.length, 'the navigation should offer the workspaces').toBeGreaterThan(4)

  const failures: string[] = []
  const inventory: { route: string; density: string; width: number; fonts: ReturnType<typeof productionDesignReport>['fonts'] }[] = []

  for (const density of ['comfortable', 'compact'] as const) {
    await page.evaluate(value => localStorage.setItem('aerolink-density', value), density)
    await page.reload({ waitUntil: 'load' })
    expect(await page.evaluate(() => document.documentElement.dataset.density)).toBe(density)

    for (const route of routes) {
      const previousMain = await page.locator('main').innerText().catch(() => '')
      await page.goto(route, { waitUntil: 'networkidle' })
      // React keeps the preceding workspace visible while the next lazy chunk resolves. Waiting for any
      // heading therefore accepts stale content; require the main content to change before evaluating the
      // destination, then wait for its user-visible readiness signal. A failed or empty chunk still falls
      // through to the report below.
      await page.waitForFunction(previous => {
        const current = (document.querySelector('main')?.textContent || '').trim()
        return current.length > 0 && current !== previous
      }, previousMain.trim(), { timeout: 15_000 }).catch(() => {})
      // Both lazy loading and context loading are user-visible opening states. Their heading is not the
      // destination's readiness signal and must not be measured as a missing or unstyled workspace.
      await expect(page.getByRole('main', { name: 'Opening workspace', exact: true })).toHaveCount(0)
      await expect(page.getByRole('heading', { name: 'Opening workspace', exact: true })).toHaveCount(0)
      await page.locator('main h1, main h2, main h3').first().waitFor({ state: 'visible', timeout: 15_000 }).catch(() => {})
      const where = `${route.replace(/^.*\/releases\/[^/]+/, '')} [${density}]`

      const report = await page.evaluate(productionDesignReport)
      inventory.push({ route, density, width: 1440, fonts: report.fonts })
      if (report.fonts.some(font => font.svg)) {
        await page.screenshot({ path: test.info().outputPath(`svg-surface-${density}-${inventory.length}.png`), fullPage: true })
      }

      if (report.boundary) failures.push(`${where}: the workspace rendered its error boundary`)
      else if ((!report.heading && !report.landmark) || report.text < 120) failures.push(`${where}: rendered nothing substantial — the chunk did not arrive`)
      // The readability floor and the density system are the two things an extracted, concatenated stylesheet
      // is most likely to change, because both are documented as depending on the order rules load in.
      if (report.tiny.length) failures.push(`${where}: ${report.tiny.length} element(s) under 12px — ${report.tiny.slice(0, 4).join('; ')}`)
      if (report.unstyled.length) failures.push(`${where}: ${report.unstyled.length} unstyled button(s) — ${report.unstyled.slice(0, 3).join('; ')}`)
      if (report.overflow) failures.push(`${where}: the document scrolls horizontally at 1440px`)
      if (await page.getByTestId('nd-rmi').isVisible()) {
        await page.locator('.efisPfd').screenshot({ path: test.info().outputPath(`pfd-production-${density}.png`) })
        await page.getByTestId('nd-rmi').screenshot({ path: test.info().outputPath(`rmi-production-${density}.png`) })
        await page.locator('.efisNd').screenshot({ path: test.info().outputPath(`nd-rmi-production-${density}.png`) })
        // A mount-only font correction goes stale when the same instrument becomes smaller. Exercise
        // the real responsive layout in this owner; the ResizeObserver must update before it is readable.
        for (const width of [1280, 960, 1440]) {
          await page.setViewportSize({ width, height: 900 })
          await expect.poll(async () => (await page.evaluate(productionDesignReport)).tiny,
            { message: `EFIS readability after resizing to ${width}px [${density}]` }).toEqual([])
          const resized = await page.evaluate(productionDesignReport)
          inventory.push({ route, density, width, fonts: resized.fonts })
          await page.locator('.efisPfd').screenshot({ path: test.info().outputPath(`pfd-resized-${density}-${width}.png`) })
          await page.locator('.efisNd').screenshot({ path: test.info().outputPath(`nd-resized-${density}-${width}.png`) })
        }
        // A station portal moves the same instruments into another document without remounting.
        // Resize the child independently after establishing a wide instrument, then return the same nodes.
        // A size observer left in the owner realm can keep a stale font floor in the child.
        await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
        await page.getByText('Station windows', { exact: true }).click()
        await page.getByRole('combobox', { name: 'Fixed station arrangement' }).selectOption('three')
        const outsideOpened = page.context().waitForEvent('page')
        await page.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
        const outside = await outsideOpened
        const cockpitOpened = page.context().waitForEvent('page')
        await page.getByRole('button', { name: 'Open cockpit in a window', exact: true }).click()
        const cockpit = await cockpitOpened
        await cockpit.locator('.efisPfd').waitFor()
        await cockpit.setViewportSize({ width: 1280, height: 900 })
        // This owner transition establishes the wide child before the independent shrink. Without
        // destination rebinding it is also the only event that can wake the misplaced size observer.
        await page.setViewportSize({ width: 1280, height: 900 })
        for (const [step, width] of [1280, 1440, 960, 1440].entries()) {
          await cockpit.setViewportSize({ width, height: 900 })
          await cockpit.locator('.efisPfd').screenshot({ path: test.info().outputPath(`pfd-child-${density}-${step}-${width}.png`) })
          await cockpit.locator('.efisNd').screenshot({ path: test.info().outputPath(`nd-child-${density}-${step}-${width}.png`) })
          const childReport = await cockpit.evaluate(productionDesignReport)
          await test.info().attach(`child-readability-${density}-${step}-${width}`, { body: JSON.stringify(childReport.fonts), contentType: 'application/json' })
          await expect.poll(async () => (await cockpit.evaluate(productionDesignReport)).tiny,
            { message: `EFIS child readability after resizing to ${width}px [${density}]` }).toEqual([])
          inventory.push({ route: `${route}#cockpit-child`, density, width, fonts: (await cockpit.evaluate(productionDesignReport)).fonts })
        }
        await cockpit.getByRole('button', { name: 'Return to bench', exact: true }).click()
        await outside.getByRole('button', { name: 'Return to bench', exact: true }).click()
        await page.getByRole('button', { name: 'Engineering view', exact: true }).click()
        await page.setViewportSize({ width: 1440, height: 900 })
        await expect.poll(async () => (await page.evaluate(productionDesignReport)).tiny,
          { message: `EFIS readability after the child returns [${density}]` }).toEqual([])
        await page.locator('.efisPfd').screenshot({ path: test.info().outputPath(`pfd-child-returned-${density}.png`) })
        await page.locator('.efisNd').screenshot({ path: test.info().outputPath(`nd-child-returned-${density}.png`) })
      }
    }
  }

  const inventoryPath = test.info().outputPath('rendered-readability-inventory.json')
  await writeFile(inventoryPath, JSON.stringify(inventory, null, 2))
  await test.info().attach('rendered-readability-inventory', {
    path: inventoryPath, contentType: 'application/json',
  })

  // Proves the workspaces really are separate chunks in the built artifact, not merely intended to be. If the
  // split silently regressed into one bundle this is the only assertion that would notice.
  expect(chunks.size, 'visiting the workspaces should have fetched their own chunks').toBeGreaterThan(3)
  expect(failures, `Production build violated the design contract:\n  ${failures.join('\n  ')}`).toEqual([])
})
