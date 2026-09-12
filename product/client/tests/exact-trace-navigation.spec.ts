import { expect, test } from "@playwright/test"
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth"

/**
 * #1016 S03. One exact navigation action per destination, in the requirement trace inspector.
 *
 * Every upstream and downstream row carried two controls that did the same thing: the identifier itself, an
 * `ExactArtifactLink` to the exact revision, and a "Focus exact requirement" button beside it. The button was
 * worse than redundant. Where the projection supplied no `revisionId` the link correctly refused to be a link
 * — the identifier is not addressable — while the button fell back to `onOpenRequirement(item.id)`, which
 * opens the artifact's *current* revision under a label promising the exact one. A reader following it
 * arrived somewhere plausible and wrong. The button is gone; the truthful link stays.
 *
 * This is the integrated journey, not a fixture: the application's own routing builds the address, the
 * browser follows it, and Back/Forward are the real history stack. Only the trace projection for one
 * requirement is controlled, because the seeded showcase records no upstream or downstream relation for it
 * and the point here is the presentation of relations, not their derivation.
 */

type Identity = { displayNumber: string; artifactId: string; revisionId: string }

const identityOf = (href: string): Identity => {
  const url = new URL(href, "http://127.0.0.1")
  const artifactId = url.pathname.split("/requirements/")[1] ?? ""
  return { displayNumber: "", artifactId, revisionId: url.searchParams.get("requirementRevisionId") ?? "" }
}

test("a trace row offers one exact navigation action, and refuses to offer one it cannot honour", async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  await login(page, "admin", { openProject: false })
  await selectProgram(page, "Flight Management System Live Program")
  await openNavigationGroup(page, "SYSTEMS ENGINEERING")
  await page.getByRole("link", { name: "System Requirements Explorer" }).click()
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden()

  // Real controlled identities, taken from the application's own row links rather than invented: each row
  // link already carries the artifact id in its path and the exact revision id in its query.
  await page.getByLabel("Search requirements").fill("SYSR-0001")
  const rowLinks = page.getByRole("link", { name: /SYSR-0001\d\d\.\d{2}/ })
  await expect(rowLinks.first()).toBeVisible()
  const scraped = await rowLinks.evaluateAll(nodes => nodes.slice(0, 3).map(node => ({
    text: (node.textContent ?? "").slice(0, 14),
    href: (node as HTMLAnchorElement).getAttribute("href") ?? "",
  })))
  expect(scraped.length, "three controlled requirements are needed to stand in for a trace relation").toBe(3)
  const [subject, upstream, downstream] = scraped.map(row => ({ ...identityOf(row.href), displayNumber: row.text }))
  expect(subject.artifactId).toBeTruthy()
  expect(upstream.revisionId).toBeTruthy()

  // A historical revision of the downstream artifact: same artifact, an earlier revision identity. It is the
  // case the removed button got wrong, so the address the row produces must name *this* revision and not the
  // artifact's current one.
  const historicalRevisionId = "11111111-2222-3333-4444-555555555555"

  await page.route(`**/api/enterprise-requirements/${subject.artifactId}/impact**`, async route => {
    const response = await route.fetch()
    const body = await response.json()
    body.parents = [{
      id: upstream.artifactId, revisionId: upstream.revisionId, displayNumber: upstream.displayNumber,
      level: "System", type: "AllocatedFrom", statement: "The upstream requirement this one is allocated from.",
      isSuspect: true,
    }]
    body.children = [
      {
        id: downstream.artifactId, revisionId: historicalRevisionId, displayNumber: downstream.displayNumber,
        level: "System", type: "AllocatedTo", statement: "A downstream requirement, at the revision recorded by the relation.",
      },
      {
        // No revisionId. The projection knows the relation exists and cannot say which exact revision it
        // points at, which is precisely what must not be papered over.
        id: "unresolvable-artifact", displayNumber: "SYSR-000999.04",
        level: "System", type: "AllocatedTo", statement: "A downstream requirement with no exact revision identity.",
      },
    ]
    body.tests = [{
      id: "procedure-artifact", artifactRevisionId: "66666666-7777-8888-9999-aaaaaaaaaaaa",
      revisionId: "66666666-7777-8888-9999-aaaaaaaaaaaa", artifactKind: "Procedure",
      displayNumber: "SYSTP-000042.01", title: "Route sequencing procedure", level: "System",
      state: "Approved", coverageState: "Suspect",
    }]
    await route.fulfill({ response, json: body })
  })

  await page.getByLabel("Search requirements").fill(subject.displayNumber)
  await page.getByRole("link", { name: new RegExp(subject.displayNumber.replace(/\./g, "\\.")) }).first().click()
  await page.getByRole("tab", { name: "Trace & impact" }).click()
  await expect(page.getByRole("link", { name: "Open complete Digital Thread →" })).toBeVisible()
  const subjectUrl = page.url()

  const inspector = page.locator(".traceInspector")

  // The removed control, by the exact words it used. Nothing else in the workspace kept it.
  await expect(inspector).not.toContainText("Focus exact requirement")

  const rows = inspector.locator(".traceRelation")
  const upstreamRow = rows.filter({ hasText: upstream.displayNumber })
  const historicalRow = rows.filter({ hasText: downstream.displayNumber })
  const unresolvedRow = rows.filter({ hasText: "SYSR-000999.04" })

  // One action per destination: the identifier is the link, and there is no second control beside it doing
  // the same journey.
  await expect(upstreamRow.locator(".traceRelationTarget a, .traceRelationTarget button")).toHaveCount(1)
  await expect(upstreamRow).toContainText('Suspect relationship')
  await expect(upstreamRow).toHaveClass(/attention/)
  await expect(inspector.locator('.traceSummary')).toContainText('0confirmed tests')
  await expect(historicalRow.locator(".traceRelationTarget a, .traceRelationTarget button")).toHaveCount(1)

  // The exact address, complete with the program, project and build the reader is working in — not a bare
  // artifact route that would resolve against whatever context happened to be current.
  const upstreamHref = await upstreamRow.locator("a").getAttribute("href")
  expect(upstreamHref).toBe(
    `${new URL(subjectUrl).pathname.replace(/\/requirements\/.*$/, "")}/requirements/${upstream.artifactId}` +
    `?discipline=system&requirementRevisionId=${upstream.revisionId}`)

  // The historical relation keeps its own revision. Substituting the artifact's current revision here is the
  // specific failure the removed button committed.
  const historicalHref = await historicalRow.locator("a").getAttribute("href") ?? ""
  expect(historicalHref).toContain(`requirementRevisionId=${historicalRevisionId}`)
  expect(historicalHref).toContain(`/requirements/${downstream.artifactId}`)
  expect(historicalHref).not.toContain(downstream.revisionId)

  // No exact revision, no navigation. The identifier is still shown — the relation is real — and it says why
  // it cannot be followed instead of offering a link to somewhere else.
  await expect(unresolvedRow.locator("a")).toHaveCount(0)
  await expect(unresolvedRow.locator(".traceRelationTarget button")).toHaveCount(0)
  const unresolved = unresolvedRow.locator("[data-exact-artifact-link='unresolved']")
  await expect(unresolved).toBeVisible()
  await expect(unresolved).toHaveText("SYSR-000999.04")
  await expect(unresolved).toHaveAttribute("title", "This requirement revision is not available as an exact link")

  // Verification coverage: one navigation control, like every other row. This asserts what the identifier
  // *says*; where it goes under each way of activating it is the separate regression below, which is where
  // the R3-01 defect lived — the href and the click handler named different destinations.
  const testRow = inspector.locator(".traceRelation").filter({ hasText: "SYSTP-000042.01" })
  const procedureHref = await testRow.locator("a").getAttribute("href") ?? ""
  expect(procedureHref).toContain("/system-verification/procedures")
  expect(procedureHref).toContain("procedureId=procedure-artifact")
  expect(procedureHref).toContain("procedureRevisionId=66666666-7777-8888-9999-aaaaaaaaaaaa")

  // "Resolve in Verification →" is gone: it called the same function with the same argument and reached the
  // same address as the identifier beside it, so it was a second control for one destination.
  await expect(testRow.getByRole("button", { name: "Resolve in Verification →" })).toHaveCount(0)
  await expect(testRow.locator("a, button")).toHaveCount(1)

  // The suspect condition it sat beside is untouched, and still says so in words.
  await expect(testRow).toHaveClass(/attention/)
  await expect(testRow).toContainText("Suspect applicability — does not count as coverage")
})

/**
 * #1016 S03 / R5-01. An identifier that promises an exact artifact must refuse when it cannot name one.
 *
 * The address builder will compose a plausible Explorer URL out of very little: no revision, no artifact id,
 * or an unstated family that would quietly take a Procedure to a Case-shaped address. A reader following that
 * arrives somewhere that looks right and names the wrong thing, which is worse than a value that declines to
 * be a link. Nothing is inferred from the display number — an identifier's prefix is not evidence of its
 * family or its level.
 */
test("the verification identifier refuses to link when the exact target is not established", async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  await login(page, "admin", { openProject: false })
  await selectProgram(page, "Flight Management System Live Program")
  await openNavigationGroup(page, "SYSTEMS ENGINEERING")
  await page.getByRole("link", { name: "System Requirements Explorer" }).click()
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden()

  await page.getByLabel("Search requirements").fill("SYSR-0001")
  const rowLinks = page.getByRole("link", { name: /SYSR-0001\d\d\.\d{2}/ })
  await expect(rowLinks.first()).toBeVisible()
  const subject = identityOf(await rowLinks.first().getAttribute("href") ?? "")
  const subjectNumber = (await rowLinks.first().textContent() ?? "").slice(0, 14)

  const revision = "66666666-7777-8888-9999-aaaaaaaaaaaa"
  // Each row gets its own revision, as distinct controlled artifacts would: the component keys on the
  // revision, and sharing one would collapse two rows into one for reasons that have nothing to do with the
  // guard under test.
  let issued = 0
  const revisionOf = (index: number) => `${revision.slice(0, -1)}${index}`
  const coverage = (over: Record<string, unknown> & { id: string }) => ({
    artifactRevisionId: revisionOf(issued), revisionId: revisionOf(issued++),
    artifactKind: "Procedure", level: "System", title: "A verification artifact",
    state: "Approved", coverageState: "Confirmed", ...over,
  })

  await page.route(`**/api/enterprise-requirements/${subject.artifactId}/impact**`, route => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      requirementRevisionId: subject.revisionId,
      parents: [], children: [], baselines: [], builds: [], documents: [], activeChanges: [],
      tests: [
        // Supported, and the control case: a System Procedure with a complete identity.
        coverage({ id: "sys-proc", displayNumber: "SYSTP-000001.00" }),
        // Both supported software families, each routed by its own declared kind.
        coverage({ id: "hlr-case", displayNumber: "HLRTC-000001.00", artifactKind: "Case", level: "HighLevel" }),
        coverage({ id: "llr-proc", displayNumber: "LLRTP-000001.00", level: "LowLevel" }),
        // No immutable revision. The relation is real; the exact target is not established.
        coverage({ id: "no-revision", displayNumber: "SYSTP-000002.00", artifactRevisionId: undefined, revisionId: undefined }),
        // No artifact identity.
        coverage({ id: "", displayNumber: "SYSTP-000003.00" }),
        // No declared family. "SYSTP-" is not evidence of one.
        coverage({ id: "no-kind", displayNumber: "SYSTP-000004.00", artifactKind: undefined }),
        // A level the Explorer does not route.
        coverage({ id: "odd-level", displayNumber: "SYSTP-000005.00", level: "Customer" }),
      ],
    }),
  }))

  await page.getByLabel("Search requirements").fill(subjectNumber)
  await page.getByRole("link", { name: subjectNumber }).first().click()
  await page.getByRole("tab", { name: "Trace & impact" }).click()
  const inspector = page.locator(".traceInspector")
  const row = (identifier: string) => inspector.locator(".traceRelation").filter({ hasText: identifier })
  await expect(row("SYSTP-000001.00")).toBeVisible({ timeout: 30_000 })

  // Supported targets link, and their declared family decides the address rather than their prefix.
  const systemHref = await row("SYSTP-000001.00").locator("a").getAttribute("href") ?? ""
  expect(systemHref).toContain("/system-verification/procedures")
  expect(systemHref).toContain(`procedureRevisionId=${revisionOf(0)}`)

  const caseHref = await row("HLRTC-000001.00").locator("a").getAttribute("href") ?? ""
  expect(caseHref).toContain("/software-verification/test-artifacts")
  expect(caseHref).toContain(`caseRevisionId=${revisionOf(1)}`)
  expect(caseHref).toContain("artifactKind=Case")

  const softwareProcedureHref = await row("LLRTP-000001.00").locator("a").getAttribute("href") ?? ""
  expect(softwareProcedureHref).toContain("/software-verification/test-artifacts")
  expect(softwareProcedureHref).toContain(`procedureRevisionId=${revisionOf(2)}`)
  expect(softwareProcedureHref).toContain("artifactKind=Procedure")

  // Everything the guard refuses renders as a truthful unavailable identifier: no anchor, no click, and no
  // substituted broad route.
  for (const identifier of ["SYSTP-000002.00", "SYSTP-000003.00", "SYSTP-000004.00", "SYSTP-000005.00"]) {
    const unresolvable = row(identifier)
    await expect(unresolvable.locator("a"), `${identifier} must not be linkable`).toHaveCount(0)
    await expect(unresolvable.locator("button")).toHaveCount(0)
    await expect(unresolvable.locator("[data-exact-artifact-link='unresolved']")).toBeVisible()
    // And it does not keep a promise it cannot honour.
    await expect(unresolvable.locator("[title='Open this exact verification artifact']")).toHaveCount(0)
  }
})

test("the exact trace link is operable by keyboard, opens in a new tab, and survives Back and Forward", async ({ page, request, context }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  await login(page, "admin", { openProject: false })
  await selectProgram(page, "Flight Management System Live Program")
  await openNavigationGroup(page, "SYSTEMS ENGINEERING")
  await page.getByRole("link", { name: "System Requirements Explorer" }).click()
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden()

  await page.getByLabel("Search requirements").fill("SYSR-0001")
  const rowLinks = page.getByRole("link", { name: /SYSR-0001\d\d\.\d{2}/ })
  await expect(rowLinks.first()).toBeVisible()
  const scraped = await rowLinks.evaluateAll(nodes => nodes.slice(0, 2).map(node => ({
    text: (node.textContent ?? "").slice(0, 14),
    href: (node as HTMLAnchorElement).getAttribute("href") ?? "",
  })))
  expect(scraped.length).toBe(2)
  const [subject, upstream] = scraped.map(row => ({ ...identityOf(row.href), displayNumber: row.text }))

  // Fulfilled outright rather than layered onto the live response: this journey opens a second tab and
  // walks history, and a passthrough fetch can be disposed underneath the handler while that happens. The
  // subject requirement records no relation in the seed, so nothing real is being masked.
  await page.route(`**/api/enterprise-requirements/${subject.artifactId}/impact**`, route => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      requirementRevisionId: subject.revisionId,
      parents: [{
        id: upstream.artifactId, revisionId: upstream.revisionId, displayNumber: upstream.displayNumber,
        level: "System", type: "AllocatedFrom", statement: "The upstream requirement this one is allocated from.",
      }],
      children: [], tests: [], baselines: [], builds: [], documents: [], activeChanges: [],
    }),
  }))

  await page.getByLabel("Search requirements").fill(subject.displayNumber)
  await page.getByRole("link", { name: new RegExp(subject.displayNumber.replace(/\./g, "\\.")) }).first().click()
  await page.getByRole("tab", { name: "Trace & impact" }).click()
  const link = page.locator(".traceInspector .traceRelation a").first()
  await expect(link).toBeVisible()
  const exactHref = await link.getAttribute("href") ?? ""
  const subjectUrl = page.url()

  // Reached by tabbing. A native anchor is in the tab order for free, but it only stays that way while it is
  // a real anchor — the unresolved case renders a span, and that difference has to be the honest one.
  let reached = false
  for (let press = 0; press < 60 && !reached; press += 1) {
    await page.keyboard.press("Tab")
    reached = await link.evaluate(node => node === document.activeElement)
  }
  expect(reached, "the exact trace link was not reachable by Tab").toBe(true)

  // Supported new-tab behaviour keeps the exact destination. The link primitive leaves modified clicks
  // native precisely so that Ctrl-click, middle-click and "copy link" address the same exact revision the
  // primary click does.
  const newTab = context.waitForEvent("page")
  await link.click({ modifiers: ["ControlOrMeta"] })
  const opened = await newTab
  await opened.waitForURL(url => url.pathname !== "blank" && url.pathname !== "about:blank" && url.pathname.includes("/requirements/"))
  expect(new URL(opened.url()).pathname + new URL(opened.url()).search).toBe(exactHref)
  await opened.close()
  // The original tab did not move: a modified click is not also a navigation here.
  expect(page.url()).toBe(subjectUrl)

  // Keyboard activation follows the same address as the pointer.
  await link.focus()
  await page.keyboard.press("Enter")
  await expect.poll(() => new URL(page.url()).pathname + new URL(page.url()).search).toBe(exactHref)
  await expect(page.getByRole("heading", { name: "System Requirements Explorer" })).toBeVisible()

  // Back and Forward through the application's own history, not a test-only router.
  await page.goBack()
  await expect.poll(() => page.url()).toBe(subjectUrl)
  await page.goForward()
  await expect.poll(() => new URL(page.url()).pathname + new URL(page.url()).search).toBe(exactHref)
})

/**
 * #1016 S03 / R3-01. The verification identifier's declared destination and its activated destination.
 *
 * The row carried an `onOpen` callback alongside its exact-artifact href. `ExactArtifactLink` suppresses the
 * native navigation whenever `onOpen` is supplied, so the same identifier went to two different places
 * depending on how it was clicked: a plain click reached the Verification (Procedure) Explorer through
 * `openVerificationProcedure`, while Ctrl-click, middle-click and copy-link followed the artifact-record href
 * the link actually declared. A reader following the link the ordinary way did not arrive where it said.
 *
 * The identity used here is a real controlled procedure from this build, taken from the Procedure Explorer's
 * own selection, so the destinations are records that exist. The coverage *relation* is supplied, because the
 * seeded requirement carries none — that limit is stated rather than implied.
 */
test("the verification identifier goes where it says by click, keyboard and new tab, and is the row's only control", async ({ page, request, context }) => {
  test.setTimeout(300_000)
  await apiLogin(request)
  await login(page, "admin", { openProject: false })
  await selectProgram(page, "Flight Management System Live Program")
  await openNavigationGroup(page, "SYSTEMS ENGINEERING")
  await page.getByRole("link", { name: "System Requirements Explorer" }).click()
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden()
  const root = new URL(page.url()).pathname.split("/").slice(0, 7).join("/")

  // A real controlled procedure and its exact revision, taken from the Explorer's own selection rather than
  // invented. Both destinations under test are therefore records that exist in this build.
  await page.goto(`${root}/system-verification/procedures`)
  const procedureRow = page.getByRole("button", { name: /SYSTP-\d+\.\d{2}/ }).first()
  await expect(procedureRow).toBeVisible({ timeout: 30_000 })
  await procedureRow.click()
  await expect.poll(() => new URL(page.url()).searchParams.get("procedureId")).not.toBeNull()
  const selected = new URL(page.url()).searchParams
  const procedureId = selected.get("procedureId")!
  const procedureRevisionId = selected.get("procedureRevisionId")!
  const procedureNumber = selected.get("procedure")!
  expect(procedureId).toBeTruthy()
  expect(procedureRevisionId).toBeTruthy()

  await page.goto(`${root}/systems/requirements`)
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden()
  await page.getByLabel("Search requirements").fill("SYSR-0001")
  const rowLinks = page.getByRole("link", { name: /SYSR-0001\d\d\.\d{2}/ })
  await expect(rowLinks.first()).toBeVisible()
  const subject = identityOf(await rowLinks.first().getAttribute("href") ?? "")
  const subjectNumber = (await rowLinks.first().textContent() ?? "").slice(0, 14)

  // Scoped to this one requirement's impact read, so the handler cannot quietly answer for another record.
  await page.route(`**/api/enterprise-requirements/${subject.artifactId}/impact**`, route => route.fulfill({
    contentType: "application/json",
    body: JSON.stringify({
      requirementRevisionId: subject.revisionId,
      parents: [], children: [], baselines: [], builds: [], documents: [], activeChanges: [],
      tests: [{
        id: procedureId, artifactRevisionId: procedureRevisionId, revisionId: procedureRevisionId,
        artifactKind: "Procedure", displayNumber: procedureNumber, title: "Controlled system test procedure",
        level: "System", state: "Approved",
        // Suspect, so the row carries its attention state while the single navigation control is checked.
        coverageState: "Suspect",
      }],
    }),
  }))

  await page.getByLabel("Search requirements").fill(subjectNumber)
  await page.getByRole("link", { name: subjectNumber }).first().click()
  await page.getByRole("tab", { name: "Trace & impact" }).click()
  const row = page.locator(".traceInspector .traceRelation").filter({ hasText: procedureNumber })
  const identifier = row.locator("a")
  await expect(identifier).toBeVisible({ timeout: 30_000 })

  // The declared destination is the Procedure Explorer, deep-linked to this exact procedure revision. That
  // is where a procedure is read, and `software-builds.spec.ts` has asserted this link lands there since the
  // procedure library moved. The defect was never which destination was right — it was that the href named
  // one and an ordinary click performed another.
  const declared = `${root}/system-verification/procedures`
    + `?procedure=${encodeURIComponent(procedureNumber)}`
    + `&procedureId=${procedureId}&procedureRevisionId=${procedureRevisionId}`
  expect(await identifier.getAttribute("href")).toBe(declared)
  const subjectUrl = page.url()
  const here = () => new URL(page.url()).pathname + new URL(page.url()).search
  // Back returns to the requirement, but the inspector reopens on its default tab. Re-selecting Trace is
  // part of getting back to the row, not part of what is being proved.
  // Compared by path, not by the whole address: returning here re-establishes the requirement's own exact
  // revision in the query, which is correct and is not what this test is about.
  const subjectPath = new URL(subjectUrl).pathname
  const backToTraceTab = async () => {
    await page.goBack()
    await expect.poll(() => new URL(page.url()).pathname).toBe(subjectPath)
    if (!(await identifier.count())) await page.getByRole("tab", { name: "Trace & impact" }).click()
    await expect(identifier).toBeVisible()
  }

  // (A) Ordinary click. On the old wiring the href named an artifact-record address while this click went to
  // the Explorer, so the two disagreed; the assertion holds only once both come from one function.
  await identifier.click()
  await expect.poll(here).toBe(declared)
  await expect(page.locator("body")).toContainText(procedureNumber)

  // (B) Keyboard activation reaches the same declared destination.
  await backToTraceTab()
  await identifier.focus()
  await page.keyboard.press("Enter")
  await expect.poll(here).toBe(declared)

  // (C) Supported new-tab activation reaches the same declared destination as the ordinary click. Before the
  // correction this was the *only* path that honoured the href, which is how the two disagreed.
  await backToTraceTab()
  const opened = context.waitForEvent("page")
  await identifier.click({ modifiers: ["ControlOrMeta"] })
  const newTab = await opened
  await newTab.waitForURL(url => url.pathname.endsWith("/system-verification/procedures"))
  expect(new URL(newTab.url()).pathname + new URL(newTab.url()).search).toBe(declared)
  await newTab.close()
  expect(new URL(page.url()).pathname).toBe(subjectPath)

  // (D) One control, and it is the one that reaches the Explorer. "Resolve in Verification →" used to sit
  // here calling the same function with the same argument, so it arrived at this same address under a label
  // that promised a different action. Its removal loses no entry capability: everything it opened, the
  // identifier above opens — same path, same procedure id, same exact revision, proven in (A).
  await backToTraceTab()
  await expect(row.getByRole("button", { name: "Resolve in Verification →" })).toHaveCount(0)
  await expect(row.locator("a, button")).toHaveCount(1)
  const reached = new URL(declared, "http://127.0.0.1").searchParams
  expect(reached.get("procedureId")).toBe(procedureId)
  expect(reached.get("procedureRevisionId")).toBe(procedureRevisionId)

  // The suspect condition the removed button sat beside is still stated.
  await expect(row).toContainText("Suspect applicability — does not count as coverage")
})
