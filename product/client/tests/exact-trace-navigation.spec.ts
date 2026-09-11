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
  await expect(page.getByRole("button", { name: "Open complete Digital Thread →" })).toBeVisible()
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

  // Verification coverage keeps two controls. This asserts only what the identifier *says*; where it actually
  // goes under each way of activating it is the separate regression below, which is where the R3-01 defect
  // lived — the href and the click handler named different destinations.
  const testRow = inspector.locator(".traceRelation").filter({ hasText: "SYSTP-000042.01" })
  const procedureHref = await testRow.locator("a").getAttribute("href") ?? ""
  expect(procedureHref).toContain("/system-verification/procedures")
  expect(procedureHref).toContain("procedureId=procedure-artifact")
  expect(procedureHref).toContain("procedureRevisionId=66666666-7777-8888-9999-aaaaaaaaaaaa")
  await expect(testRow.getByRole("button", { name: "Resolve in Verification →" })).toBeVisible()
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
test("the verification identifier goes where it says by click, keyboard and new tab, and Resolve reaches the same place", async ({ page, request, context }) => {
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
      parents: [], children: [], baselines: [], builds: [], documents: [], activeChanges: [],
      tests: [{
        id: procedureId, artifactRevisionId: procedureRevisionId, revisionId: procedureRevisionId,
        artifactKind: "Procedure", displayNumber: procedureNumber, title: "Controlled system test procedure",
        level: "System", state: "Approved",
        // Suspect, so "Resolve in Verification" is offered and both controls can be compared.
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

  // (D) Resolve, recorded as it actually behaves. It reaches the same Explorer address as the identifier and
  // carries the same exact identity — two controls, one destination. That is asserted here as the current
  // truth rather than described as a distinct action, which is what an earlier review packet claimed on the
  // strength of reading the two call sites instead of activating them. Whether this control should be removed
  // or given a destination of its own is a decision about the coverage workflow, not about this link.
  await backToTraceTab()
  await row.getByRole("button", { name: "Resolve in Verification →" }).click()
  await expect.poll(() => new URL(page.url()).pathname).toBe(`${root}/system-verification/procedures`)
  const resolved = new URL(page.url()).searchParams
  expect(resolved.get("procedureId")).toBe(procedureId)
  expect(resolved.get("procedureRevisionId")).toBe(procedureRevisionId)
  expect(here(), "identifier and Resolve currently share one destination").toBe(declared)
})
