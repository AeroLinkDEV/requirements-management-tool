import { expect, test } from '@playwright/test'
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
async function navigationRoutes(page: Page) {
  const links = page.locator('nav[aria-label="Primary navigation"] a[href]')
  await expect(links.first()).toBeAttached({ timeout: 30_000 })
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

  const routes = await navigationRoutes(page)
  const deepLink = routes.find(href => href.includes('/change-requests'))
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
  const response = await request.get(`/api/scrs?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&pageSize=200`)
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
  await expect(page.getByRole('link', { name: 'New System SCR' })).toHaveCount(0)

  await page.goto(`${root}/software/change-requests/${software!.id}`)
  await expect(page).toHaveURL(`${root}/software/change-requests/${software!.id}`)
  await expect(page.getByRole('link', { name: 'Software Change Requests' })).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('link', { name: 'New Software SWCR' })).toHaveCount(0)

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
  await page.getByLabel('Problem').fill('The compiled production client must perform protected writes.')
  await page.getByRole('textbox', { name: 'Analysis', exact: true }).fill('A durable server query must prove the write rather than trusting the success ceremony.')
  await page.getByLabel('Solution').fill('Resolve relative API URLs and bind CSRF state to the signed-in session.')
  await page.getByLabel('Requirement statement').fill('The production client shall preserve authenticated mutation capability.')
  await page.getByRole('button', { name: 'Save SCR Draft' }).click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()

  await apiLogin(request)
  const list = await request.get(`/api/scrs?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`)
  expect(list.ok(), await list.text()).toBeTruthy()
  const body = await list.json()
  const persisted = body.items.find((item: { title: string }) => item.title === title)
  expect(persisted, 'the success view must correspond to a durable server record').toBeTruthy()
  const detail = await request.get(`/api/scrs/${persisted.id}`)
  expect(detail.ok(), await detail.text()).toBeTruthy()
  expect(await detail.json()).toEqual(expect.objectContaining({
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

test('verification mutation failures retain the engineer input and only confirmed success creates one immutable result', async ({ page, request, playwright, baseURL }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const requirementsResponse = await request.get(`/api/requirements?projectId=${showcase.projectId}&baselineId=${showcase.releasedBaselineId}&scope=System&page=1&pageSize=1`)
  expect(requirementsResponse.ok(), await requirementsResponse.text()).toBeTruthy()
  const requirements = await requirementsResponse.json()

  const engineer = await playwright.request.newContext({ baseURL })
  const engineerLogin = await engineer.post('/api/auth/login', { data: { userName: 'test.engineer', password: 'AeroLink!2026' } })
  expect(engineerLogin.ok(), await engineerLogin.text()).toBeTruthy()
  const created = await engineer.post('/api/test-procedures', { data: {
    projectId: showcase.projectId,
    baseNumber: 'SERVER-ALLOCATED',
    title: `Production result procedure ${Date.now()}`,
    objective: 'Exercise the production mutation and failure contract.',
    preconditions: 'Compiled single-origin client is running.',
    steps: 'Record one externally determined result.',
    expectedResult: 'Exactly one immutable result exists after server confirmation.',
    requirementRevisionIds: [requirements.items[0].revisionId],
    level: 'System',
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const procedure = await created.json()
  const approved = await request.post(`/api/test-procedures/${procedure.revisionId}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved for the compiled production mutation qualification.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()
  await engineer.dispose()

  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'VERIFICATION')
  await page.getByRole('link', { name: 'System Verification' }).click()
  await page.getByRole('button', { name: /Test procedures/ }).click()
  // The list is paged, so a procedure created through the API is found rather than scrolled to.
  await page.getByLabel('Find a procedure').fill(procedure.displayNumber.replace(/\.\d{2}$/, ''))
  const row = page.locator('.procedureRow').filter({ hasText: procedure.displayNumber })
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: 'Record result' }).click()
  const form = page.locator('form.resultForm')
  await form.getByLabel('Configuration').fill('Production qualification rig')
  await form.getByLabel('Evidence reference').fill('evidence/production-mutation.json')
  await form.getByLabel('Human determination', { exact: true }).fill('The compiled client recorded the protected result exactly once.')

  await page.route('**/api/test-executions', route => route.abort('connectionfailed'))
  await form.getByRole('button', { name: 'Record immutable result' }).click()
  await expect(page.getByRole('alert')).toContainText(/Failed to fetch|could not/i)
  await expect(form.getByLabel('Human determination', { exact: true })).toHaveValue('The compiled client recorded the protected result exactly once.')

  await page.unroute('**/api/test-executions')
  await page.route('**/api/test-executions', route => route.fulfill({
    status: 409,
    contentType: 'application/json',
    body: JSON.stringify({ error: 'A conflicting result version is already being reviewed.' }),
  }))
  await form.getByRole('button', { name: 'Record immutable result' }).click()
  await expect(page.getByRole('alert')).toContainText('A conflicting result version is already being reviewed.')
  await expect(form).toBeVisible()

  await page.unroute('**/api/test-executions')
  await form.getByRole('button', { name: 'Record immutable result' }).click()
  await expect(page.getByRole('heading', { name: 'Execution history' })).toBeVisible()
  await expect(page.locator('.executionRow').filter({ hasText: procedure.displayNumber })).toContainText('compiled client recorded')

  const executionsResponse = await request.get(`/api/test-executions?projectId=${showcase.projectId}`)
  expect(executionsResponse.ok(), await executionsResponse.text()).toBeTruthy()
  const executions = await executionsResponse.json()
  expect(executions.filter((item: { procedureRevisionId: string }) => item.procedureRevisionId === procedure.revisionId)).toEqual([
    expect.objectContaining({
      determination: 'The compiled client recorded the protected result exactly once.',
      evidenceReference: 'evidence/production-mutation.json',
    }),
  ])
})

test('every workspace chunk arrives and keeps the design contract in both densities', async ({ page, request }) => {
  test.setTimeout(600_000)
  await page.setViewportSize({ width: 1440, height: 900 })

  const chunks = new Set<string>()
  page.on('request', request => {
    const path = new URL(request.url()).pathname
    if (/^\/assets\/.+\.js$/.test(path) && !/^\/assets\/index-/.test(path)) chunks.add(path)
  })

  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const routes = await navigationRoutes(page)
  expect(routes.length, 'the navigation should offer the workspaces').toBeGreaterThan(4)

  const failures: string[] = []

  for (const density of ['comfortable', 'compact'] as const) {
    await page.evaluate(value => localStorage.setItem('aerolink-density', value), density)
    await page.reload({ waitUntil: 'load' })
    expect(await page.evaluate(() => document.documentElement.dataset.density)).toBe(density)

    for (const route of routes) {
      await page.goto(route, { waitUntil: 'load' })
      await page.waitForTimeout(1200)
      const where = `${route.replace(/^.*\/releases\/[^/]+/, '')} [${density}]`

      const report = await page.evaluate(() => {
        const visible = (element: Element) => {
          const box = element.getBoundingClientRect()
          return box.width > 0 && box.height > 0
        }
        const leaves = [...document.querySelectorAll('main *, body > div > *')].filter(
          element => visible(element) && !element.children.length && (element.textContent || '').trim().length > 0,
        )
        return {
          heading: [...document.querySelectorAll('h1, h2, h3')].some(visible),
          // A chunk that fails to load leaves the surface empty. Dev cannot have this failure because dev
          // never chunks; the error boundary is the other thing that would show here.
          text: (document.querySelector('main')?.textContent || document.body.textContent || '').trim().length,
          boundary: /went wrong|failed to load|Something broke/i.test(document.body.textContent || ''),
          tiny: [
            ...new Set(
              leaves
                .filter(element => parseFloat(getComputedStyle(element).fontSize) < 12)
                .map(element => `${(element.textContent || '').trim().slice(0, 24)} @ ${getComputedStyle(element).fontSize}`),
            ),
          ],
          unstyled: [
            ...new Set(
              [...document.querySelectorAll('button')]
                .filter(element => visible(element) && getComputedStyle(element).backgroundColor === 'rgb(239, 239, 239)')
                .map(element => (element.textContent || '').trim().slice(0, 24)),
            ),
          ],
          overflow: document.documentElement.scrollWidth > window.innerWidth + 1,
        }
      })

      if (report.boundary) failures.push(`${where}: the workspace rendered its error boundary`)
      else if (!report.heading || report.text < 120) failures.push(`${where}: rendered nothing substantial — the chunk did not arrive`)
      // The readability floor and the density system are the two things an extracted, concatenated stylesheet
      // is most likely to change, because both are documented as depending on the order rules load in.
      if (report.tiny.length) failures.push(`${where}: ${report.tiny.length} element(s) under 12px — ${report.tiny.slice(0, 4).join('; ')}`)
      if (report.unstyled.length) failures.push(`${where}: ${report.unstyled.length} unstyled button(s) — ${report.unstyled.slice(0, 3).join('; ')}`)
      if (report.overflow) failures.push(`${where}: the document scrolls horizontally at 1440px`)
    }
  }

  // Proves the workspaces really are separate chunks in the built artifact, not merely intended to be. If the
  // split silently regressed into one bundle this is the only assertion that would notice.
  expect(chunks.size, 'visiting the workspaces should have fetched their own chunks').toBeGreaterThan(3)
  expect(failures, `Production build violated the design contract:\n  ${failures.join('\n  ')}`).toEqual([])
})
