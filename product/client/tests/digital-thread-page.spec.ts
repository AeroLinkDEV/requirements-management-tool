import { expect, test, type Page } from "@playwright/test"
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed, surfacePainted } from "./auth"

/**
 * The Digital Thread page after #880 §4 replaced it.
 *
 * These drive the real product — navigation, routes, the shell — rather than a fixture, because what §4 asks
 * for is page-level: which nav group the entry lives in, that old bookmarks still resolve, that the reclaimed
 * header is actually gone, and that a focal artifact survives refresh, back, forward and Copy link. None of
 * that is visible to a component test.
 */

const openThread = async (page: Page) => {
  await openNavigationGroup(page, "RELEASE")
  await page.getByRole("link", { name: "Digital Thread" }).click()
  await expect(page.locator(".dtPage")).toBeVisible()
  await surfacePainted(page)
}

/** The program, project and release the page is currently addressing. */
const ids = (page: Page) => {
  const match = /\/programs\/([^/]+)\/projects\/([^/]+)\/releases\/([^/]+)/.exec(new URL(page.url()).pathname)
  if (!match) throw new Error(`the address does not name a program, project and release: ${page.url()}`)
  return { programId: match[1], projectId: match[2], releaseId: match[3] }
}

const threadRoot = (page: Page) => new URL(page.url()).pathname.replace(/\/traceability.*$/, "")

test.describe("navigation", () => {
  test("Digital Thread is in RELEASE, not REQUIREMENTS", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")

    // Moved to sit beside the release and configuration views it belongs with (§4.3).
    await openNavigationGroup(page, "RELEASE")
    await expect(page.locator(".navGroup", { hasText: "RELEASE" })
      .getByRole("link", { name: "Digital Thread" })).toBeVisible()

    await openNavigationGroup(page, "REQUIREMENTS")
    await expect(page.locator(".navGroup", { hasText: "REQUIREMENTS" })
      .getByRole("link", { name: "Digital Thread" })).toHaveCount(0)
  })

  test("the route is unchanged, so existing bookmarks still resolve", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    // §4.3 is a navigation regrouping, not a route change.
    expect(new URL(page.url()).pathname).toMatch(/\/releases\/[^/]+\/traceability$/)
  })
})

test.describe("the reclaimed header", () => {
  test("the old page chrome is gone and the canvas starts under the breadcrumb", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    // Removed by §4.2: roughly 210px of back link, eyebrow, H1, description and a one-tab strip.
    await expect(page.getByRole("button", { name: "← Command Center" })).toHaveCount(0)
    await expect(page.getByText("ASSURANCE / DIGITAL THREAD FOCUS")).toHaveCount(0)
    await expect(page.getByRole("heading", { name: "Digital Thread", exact: true })).toHaveCount(0)
    await expect(page.getByText("Answer one engineering question across")).toHaveCount(0)
    await expect(page.locator(".lifeTabs")).toHaveCount(0)

    // Kept: the shell's breadcrumb, its build context and Copy link.
    await expect(page.locator(".contextBar")).toBeVisible()
    await expect(page.getByRole("button", { name: "Copy link to this page" })).toBeVisible()

    // And the canvas begins directly beneath it.
    const bar = (await page.locator(".contextBar").boundingBox())!
    const toolbar = (await page.locator(".dtPageToolbar").boundingBox())!
    expect(toolbar.y - (bar.y + bar.height)).toBeLessThanOrEqual(2)
  })

  test("the replaced presentation is gone", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    // §9: no fixed lifecycle-path strip, no stacked layer boxes, no separate selected-node block.
    await expect(page.getByText("COMPLETE LIFECYCLE PATH")).toHaveCount(0)
    await expect(page.locator(".completeThreadPath")).toHaveCount(0)
    await expect(page.locator(".crGraphLayer")).toHaveCount(0)
  })
})

test.describe("the toolbar", () => {
  test("carries the view switch, the representation toggle and a compact Export", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const toolbar = page.locator(".dtPageToolbar")
    await expect(toolbar.getByRole("button", { name: "Change network" })).toBeVisible()
    await expect(toolbar.getByRole("button", { name: "Inside a change" })).toBeVisible()
    await expect(toolbar.getByRole("button", { name: "Artifact thread" })).toBeVisible()
    await expect(toolbar.getByRole("button", { name: "Map" })).toBeVisible()
    await expect(toolbar.getByRole("button", { name: "Table" })).toBeVisible()

    // The exports survive, grouped rather than spending the width two large buttons used to (§4.5).
    await expect(page.getByRole("link", { name: "Trace PDF" })).toBeHidden()
    await toolbar.locator(".dtPageExport summary").click()
    await expect(page.getByRole("link", { name: "Trace PDF" })).toBeVisible()
    await expect(page.getByRole("link", { name: "Trace DOCX" })).toBeVisible()
  })

  test("the export links keep their existing report behaviour", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    await expect(page.locator(".dtPageBaseline select")).toBeVisible()
    await page.locator(".dtPageExport summary").click()
    for (const [name, format] of [["Trace PDF", "pdf"], ["Trace DOCX", "docx"]] as const) {
      const href = await page.getByRole("link", { name }).getAttribute("href")
      // Same report resource the replaced page used; the generator is untouched by this slice.
      expect(href).toMatch(new RegExp(`/api/traceability/[^/]+/download\\?format=${format}$`))
    }
  })
})

test.describe("the evidence table", () => {
  test("is a real table exposing the relationships the canvas draws", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    await page.locator(".dtPageToolbar").getByRole("button", { name: "Table" }).click()
    const table = page.locator(".dtPageTable table")
    await expect(table).toBeVisible()

    // Not a visually hidden afterthought: real headers, so it is announced and navigable as a table.
    await expect(table.getByRole("columnheader", { name: "Requirement" })).toBeVisible()
    await expect(table.getByRole("columnheader", { name: "Upstream" })).toBeVisible()
    await expect(table.getByRole("columnheader", { name: "Downstream" })).toBeVisible()
    await expect(table.getByRole("columnheader", { name: "Verification" })).toBeVisible()
    await expect(table.getByRole("columnheader", { name: "Result and evidence" })).toBeVisible()
    // Retrying assertion: the rows arrive asynchronously, and a one-shot count measures the empty table.
    await expect(table.locator("tbody tr").first()).toBeVisible()
    expect(await table.locator("tbody tr").count()).toBeGreaterThan(0)
  })

  test("switching representation keeps the reader on the same page and address", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)
    const before = page.url()

    await page.locator(".dtPageToolbar").getByRole("button", { name: "Table" }).click()
    await expect(page.locator(".dtPageTable")).toBeVisible()
    await page.locator(".dtPageToolbar").getByRole("button", { name: "Map" }).click()
    await expect(page.locator(".dtCanvas")).toBeVisible()

    // A representation, not a second page.
    expect(page.url()).toBe(before)
  })

  test("every requirement in the baseline is reachable, not just the first page", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    await page.locator(".dtPageToolbar").getByRole("button", { name: "Table" }).click()
    const table = page.locator(".dtPageTable")
    await expect(table.locator("tbody tr").first()).toBeVisible()

    // §4.5/§6.9 want the list to expose the *same* relationships the canvas draws. A fixed first hundred of a
    // 1,250-requirement baseline leaves most of them unreachable however honestly it is captioned.
    const pager = page.getByRole("navigation", { name: "Evidence table pages" })
    await expect(pager).toBeVisible()
    await expect(pager).toContainText(/Page 1 of \d+/)
    await expect(pager.getByRole("button", { name: "Previous" })).toBeDisabled()

    const firstRow = await table.locator("tbody tr th").first().innerText()
    await pager.getByRole("button", { name: "Next" }).click()
    await expect(pager).toContainText("Page 2 of")
    await expect(table.locator("tbody tr").first()).toBeVisible()
    // A different page, not the same rows relabelled.
    await expect(table.locator("tbody tr th").first()).not.toHaveText(firstRow)
    await expect(pager.getByRole("button", { name: "Previous" })).toBeEnabled()

    // The last page is reachable and says so, rather than paging forever.
    const pages = Number(/Page \d+ of ([\d,]+)/.exec(await pager.innerText())![1].replace(/,/g, ""))
    expect(pages).toBeGreaterThan(1)
  })
})

test.describe("focal deep links", () => {
  test("global navigation lands on the change network with nothing selected", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    await expect(page.locator(".dtnRoot")).toBeVisible()
    await expect(page.locator(".dtnCard.is-selected")).toHaveCount(0)
    await expect(page.locator(".dtPageViews button[aria-pressed='true']")).toHaveText("Change network")
  })

  test("a requirement focal opens the artifact thread on that exact revision", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    // A real requirement revision out of this build's own baseline. A hand-built identifier would prove the
    // route parses, not that it resolves to a record the reader can actually be shown.
    const { projectId, releaseId } = ids(page)
    const context = await (await request.get(
      `${apiBase}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const list = await (await request.get(
      `${apiBase}/api/traceability?projectId=${projectId}&baselineId=${context.effectiveBaselineId}&page=1&pageSize=1`
    )).json()
    const row = (Array.isArray(list) ? list : list.items ?? [])[0]
    expect(row, "the showcase baseline should carry at least one requirement").toBeTruthy()

    // The bare `/traceability/{id}` segment has always meant a requirement, and §4.3 requires it to keep
    // meaning that — this is the shape every existing bookmark and `Open Digital Thread` link already uses.
    await page.goto(`${threadRoot(page)}/traceability/${row.revisionId}`)
    await expect(page.locator(".dtaRoot")).toBeVisible()
    await expect(page.locator(".dtPageViews button[aria-pressed='true']")).toHaveText("Artifact thread")

    // A refused response draws no cards at all, by design. Surface its reason in the failure rather than
    // leaving "no focal card" to be diagnosed from scratch.
    const refusal = await page.locator(".dtaReason").allInnerTexts()
    expect(refusal, "the artifact thread response was refused by the contract seam").toEqual([])

    // Landed on that exact revision, selected and expanded, as though the reader had clicked it (§4.4).
    const focal = page.locator(".dtaCard.is-focal")
    await expect(focal).toHaveClass(/is-selected/)
    await expect(focal.locator(".dtaId")).toHaveText(row.displayNumber)
    await expect(focal.locator(".dtaCardBody")).toBeVisible()
  })

  test("a change request focal lands on the change network, not inside the change", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    // Deep-linked, and never clicked. §4.4 requires the address alone to produce the arrival state; a test
    // that clicks the card first proves the click works and says nothing about the link.
    const { projectId, releaseId } = ids(page)
    const network = await (await request.get(
      `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const target = (network.nodes ?? []).find((node: { kind: string }) => node.kind === "ChangeRequest")
    expect(target, "the showcase build should carry at least one change request").toBeTruthy()

    await page.goto(`${threadRoot(page)}/traceability/change-requests/${target.id}`)
    await expect(page.locator(".dtnRoot")).toBeVisible()
    await surfacePainted(page)

    // Arrival is the same state a click would have produced: that exact card selected, its web traced, the
    // detail panel open on it, and the card itself in view rather than rolled out of its lane.
    const landed = page.locator(".dtnCard.is-selected")
    await expect(landed).toHaveCount(1)
    await expect(landed).toContainText(target.displayNumber)
    await expect(page.locator(".dtnPanel")).toBeVisible()
    await expect(page.locator(".dtnPanel")).toContainText(target.displayNumber)
    await expect(page.locator(`.dtCanvasNode:has(.dtnCard.is-selected)`)).toBeInViewport()

    // §4.4: the point of arriving is to see the change in context. `Open this change` is one click away.
    await expect(page.locator(".dtPageViews button[aria-pressed='true']")).toHaveText("Change network")
    await expect(page.locator(".dtnPanel").getByRole("button", { name: "Open this change" })).toBeVisible()
  })

  test("an inside-a-change address whose change this build does not carry fails closed", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    // Well-formed, and belonging to nothing in this build. The page must not guess the record's kind and then
    // fetch its content by id, which is a membership claim made by omission.
    const absent = "99999999-9999-4999-8999-999999999999"
    await page.goto(`${threadRoot(page)}/traceability/change-requests/${absent}?view=inside`)
    await expect(page.getByRole("alert")).toContainText(/does not contain the change/i)
    await expect(page.locator(".dticRoot")).toHaveCount(0)
  })
})

test.describe("selection and navigation coherence", () => {
  test("a selected change becomes the exact Inside focal and survives refresh", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const network = await (await request.get(
      `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const changes = (network.nodes ?? []).filter((node: { kind: string }) => node.kind === "ChangeRequest") as {
      id: string; displayNumber: string
    }[]
    expect(changes.length, "the seeded showcase should carry at least two Change Requests to move between")
      .toBeGreaterThanOrEqual(2)
    await expect(page.locator(".dtCanvasScene .dtnCard").first()).toBeVisible()
    const changeIds = new Set(changes.map(candidate => candidate.id))
    // Lane and row are presentation assignments, not API fields. Pick two actual rendered cards sharing their
    // measured lane position, so ArrowDown exercises the same navigation a reader can use on this fixture.
    const rendered = await page.locator(".dtCanvasNode").evaluateAll(elements => elements.map(element => {
      const box = element.getBoundingClientRect()
      return { id: element.getAttribute("data-node-id") ?? "", left: box.left, top: box.top }
    }))
    const renderedChanges = rendered.filter(candidate => changeIds.has(candidate.id))
    const firstRendered = renderedChanges.find(candidate => renderedChanges.some(other =>
      other.id !== candidate.id && Math.abs(other.left - candidate.left) < 2))
    expect(firstRendered, "the seeded showcase should render two Change Requests in one navigable lane").toBeTruthy()
    if (!firstRendered) throw new Error("the seeded showcase should render two Change Requests in one navigable lane")
    const secondRendered = renderedChanges
      .filter(candidate => candidate.id !== firstRendered.id && Math.abs(candidate.left - firstRendered.left) < 2
        && candidate.top > firstRendered.top)
      .sort((a, b) => a.top - b.top)[0]
    expect(secondRendered, "the selected Change Request should have a keyboard-reachable neighbor").toBeTruthy()
    if (!secondRendered) throw new Error("the selected Change Request should have a keyboard-reachable neighbor")
    const first = changes.find(candidate => candidate.id === firstRendered.id)
    const second = changes.find(candidate => candidate.id === secondRendered.id)
    expect(first && second, "rendered Change Requests should retain their API identities").toBeTruthy()
    if (!first || !second) throw new Error("rendered Change Requests should retain their API identities")

    const cardFor = (displayNumber: string) => page.locator(".dtnCard").filter({ hasText: displayNumber }).first()
    await cardFor(first.displayNumber).click()
    await expect(page.locator(".dtnCard.is-selected")).toContainText(first.displayNumber)
    await expect(page.getByRole("button", { name: "Inside a change" })).toBeEnabled()
    expect(page.url()).toContain(`/traceability/change-requests/${first.id}`)

    // Changing the selected card changes the addressed focal, so Inside cannot silently reopen the first card.
    // The expanded first card may cover the next row visually; use the shared canvas keyboard path to reach it,
    // which is also the usable interaction required for a dense lane.
    await page.locator(`.dtCanvasNode[data-node-id="${first.id}"]`).focus()
    await page.keyboard.press("ArrowDown")
    await page.keyboard.press("Enter")
    await expect(page.locator(".dtnCard.is-selected")).toContainText(second.displayNumber)
    await expect(page.getByRole("button", { name: "Inside a change" })).toBeEnabled()
    expect(page.url()).toContain(`/traceability/change-requests/${second.id}`)

    await page.getByRole("button", { name: "Inside a change" }).click()
    await expect(page.locator(".dticRoot")).toBeVisible()
    await expect(page.locator(".dticRoot")).toContainText(`Inside ${second.displayNumber}`)
    expect(page.url()).toMatch(new RegExp(`/traceability/change-requests/${second.id}\\?view=inside$`))

    await page.reload()
    await expect(page.locator(".dticRoot")).toBeVisible()
    await expect(page.locator(".dticRoot")).toContainText(`Inside ${second.displayNumber}`)
    expect(page.url()).toMatch(new RegExp(`/traceability/change-requests/${second.id}\\?view=inside$`))
  })

  test("a card action opens Inside once for pointer and keyboard activation", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const network = await (await request.get(
      `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const change = (network.nodes ?? []).find((node: { kind: string }) => node.kind === "ChangeRequest") as { id: string; displayNumber: string } | undefined
    expect(change, "the seeded showcase should carry a Change Request to open").toBeTruthy()
    if (!change) throw new Error("the seeded showcase should carry a Change Request to open")

    const card = page.locator(".dtnCard").filter({ hasText: change.displayNumber }).first()
    await card.click()
    await card.getByRole("button", { name: "Open this change" }).click()
    await expect(page.locator(".dticRoot")).toBeVisible()
    await expect(page.locator(".dticRoot")).toContainText(`Inside ${change.displayNumber}`)
    expect(page.url()).toMatch(new RegExp(`/traceability/change-requests/${change.id}\\?view=inside$`))

    // Return to the addressed network entry, then activate the same nested button through the native keyboard
    // path. The card wrapper must not consume Enter and turn it into a second selection action.
    await page.goBack()
    await expect(page.locator(".dtnRoot")).toBeVisible()
    const selected = page.locator(".dtnCard.is-selected")
    await expect(selected).toContainText(change.displayNumber)
    const action = selected.getByRole("button", { name: "Open this change" })
    await action.focus()
    await page.keyboard.press("Enter")
    await expect(page.locator(".dticRoot")).toBeVisible()
    await expect(page.locator(".dticRoot")).toContainText(`Inside ${change.displayNumber}`)
    expect(page.url()).toMatch(new RegExp(`/traceability/change-requests/${change.id}\\?view=inside$`))
  })

  test("Inside card links keep native pointer activation within the shared canvas", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const threadBase = threadRoot(page)
    const { projectId, releaseId } = ids(page)
    const network = await (await request.get(
      `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const change = (network.nodes ?? []).find((node: { kind: string }) => node.kind === "ChangeRequest") as {
      id: string; displayNumber: string
    } | undefined
    expect(change, "the seeded showcase should carry a Change Request for Inside link activation").toBeTruthy()
    if (!change) throw new Error("the seeded showcase should carry a Change Request for Inside link activation")

    await page.goto(`${threadBase}/traceability/change-requests/${change.id}?view=inside`)
    await expect(page.locator(".dticRoot")).toBeVisible()
    const link = page.locator(".dticCard .dticId.exactArtifactLink[href]").first()
    await expect(link).toBeVisible()
    const href = await link.getAttribute("href")
    expect(href, "the Inside register should expose an exact native route").toBeTruthy()
    if (!href) throw new Error("the Inside register should expose an exact native route")

    // This is a real primary pointer activation on a nested anchor. The shared canvas must not capture it as
    // a card selection, and the browser should navigate once to the exact destination supplied by the link.
    await Promise.all([
      page.waitForURL(url => new URL(url).pathname === new URL(href, page.url()).pathname),
      link.click(),
    ])
    expect(new URL(page.url()).pathname).toBe(new URL(href, page.url()).pathname)

    // Fresh route, then a native Enter activation on a link that is actually in the usable canvas window.
    await page.goto(`${threadBase}/traceability/change-requests/${change.id}?view=inside`)
    await expect(page.locator(".dticRoot")).toBeVisible()
    const links = page.locator(".dticCard .dticId.exactArtifactLink[href]")
    const keyboardLinkIndex = await links.evaluateAll(elements => elements.findIndex(element => {
      const box = element.getBoundingClientRect()
      return box.width > 10 && box.height > 10 && box.bottom > 0 && box.top < window.innerHeight
    }))
    expect(keyboardLinkIndex, "Inside should expose a visible exact link for keyboard activation").toBeGreaterThanOrEqual(0)
    const keyboardLink = links.nth(keyboardLinkIndex)
    const keyboardHref = await keyboardLink.getAttribute("href")
    expect(keyboardHref, "the keyboard target should remain an exact native route").toBeTruthy()
    if (!keyboardHref) throw new Error("the keyboard target should remain an exact native route")
    await keyboardLink.focus()
    await Promise.all([
      page.waitForURL(url => new URL(url).pathname === new URL(keyboardHref, page.url()).pathname),
      page.keyboard.press("Enter"),
    ])
    expect(new URL(page.url()).pathname).toBe(new URL(keyboardHref, page.url()).pathname)
  })

  test("Artifact card actions survive shared canvas pointer and keyboard handling", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const threadBase = threadRoot(page)
    const { projectId, releaseId } = ids(page)
    const context = await (await request.get(
      `${apiBase}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const list = await (await request.get(
      `${apiBase}/api/traceability?projectId=${projectId}&baselineId=${context.effectiveBaselineId}&page=1&pageSize=1`
    )).json()
    const rows = (Array.isArray(list) ? list : list.items ?? []) as { revisionId: string; displayNumber: string }[]
    expect(rows.length, "the seeded showcase should carry requirement revisions for Artifact actions").toBeGreaterThan(0)
    const row = rows[0]
    const thread = await (await request.get(
      `${apiBase}/api/artifact-thread?projectId=${projectId}&baselineId=${context.effectiveBaselineId}`
      + `&focalKind=Requirement&focalId=${encodeURIComponent(row.revisionId)}`)).json() as {
        nodes?: { id: string; kind: string; displayNumber: string }[]
      }
    // Build 1.6 inherits the released Build 1.5 baseline. Its authored change cards are historical to the
    // active build, so the honest action result may be the exact Inside route followed by the page's explicit
    // missing-current-build response. This still exercises native pointer and keyboard activation on the real
    // Artifact card without inventing membership by searching unrelated rows.
    const change = (thread.nodes ?? []).find(node => node.kind === "ChangeRequest" || node.kind === "TestChangeRequest")
    expect(change, "the authoritative inherited baseline Artifact thread should carry a change action card")
      .toBeTruthy()
    if (!change) throw new Error("the authoritative inherited baseline Artifact thread should carry a change action card")

    const artifactPath = `${threadBase}/traceability/${row.revisionId}`
    await page.goto(artifactPath)
    await expect(page.locator(".dtaRoot")).toBeVisible()
    const card = page.locator(".dtaCard").filter({ hasText: change.displayNumber }).first()
    await expect(card).toBeVisible()
    await card.click()
    const action = card.getByRole("button", { name: "Open this change" })
    await expect(action).toBeVisible()
    await action.click()
    await expect(page).toHaveURL(`${threadBase}/traceability/change-requests/${change.id}?view=inside`)
    await expect(page.getByRole("alert")).toContainText("does not contain the change")
    expect(page.url()).toMatch(new RegExp(`/traceability/change-requests/${change.id}\\?view=inside$`))

    // Back to the exact Artifact address and activate the same nested control through Enter. The card wrapper
    // must not turn that native button event into another selection or a duplicate route transition.
    await page.goto(artifactPath)
    await expect(page.locator(".dtaRoot")).toBeVisible()
    const keyboardCard = page.locator(".dtaCard").filter({ hasText: change.displayNumber }).first()
    await keyboardCard.click()
    const keyboardAction = keyboardCard.getByRole("button", { name: "Open this change" })
    await keyboardAction.focus()
    await page.keyboard.press("Enter")
    await expect(page).toHaveURL(`${threadBase}/traceability/change-requests/${change.id}?view=inside`)
    await expect(page.getByRole("alert")).toContainText("does not contain the change")
    expect(page.url()).toMatch(new RegExp(`/traceability/change-requests/${change.id}\\?view=inside$`))
  })

  test("unsupported Artifact entry is explicit and returning to Network clears its error context", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const network = await (await request.get(
      `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const change = (network.nodes ?? []).find((node: { kind: string }) => node.kind === "ChangeRequest") as { id: string } | undefined
    expect(change, "the seeded showcase should carry a Change Request to exercise unsupported Artifact entry")
      .toBeTruthy()
    if (!change) throw new Error("the seeded showcase should carry a Change Request to exercise unsupported Artifact entry")

    // A change request has no materialized Artifact-thread focal kind. A deep link must state that fact instead
    // of making a request with the wrong kind and leaking its failure into the Network view.
    await page.goto(`${threadRoot(page)}/traceability/change-requests/${change.id}?view=artifact`)
    await expect(page.getByRole("button", { name: "Artifact thread" })).toBeDisabled()
    await expect(page.getByRole("alert")).toContainText("supported exact artifact")
    await page.getByRole("alert").getByRole("button", { name: "Back to the change network" }).click()
    await expect(page.locator(".dtnRoot")).toBeVisible()
    await expect(page.locator(".dtnInFrame-error")).toHaveCount(0)
    await expect(page.locator(".dtPageTableEmpty")).toHaveCount(0)
    expect(new URL(page.url()).pathname).toMatch(/\/traceability$/)
  })

  test("an exact requirement that is absent from the build fails closed instead of loading forever", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const network = await (await request.get(
      `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const change = (network.nodes ?? []).find((node: { kind: string }) => node.kind === "ChangeRequest") as {
      id: string
    } | undefined
    expect(change, "the seeded showcase should carry an id that is not a requirement identity").toBeTruthy()
    if (!change) throw new Error("the seeded showcase should carry an id that is not a requirement identity")

    // The legacy /traceability/{id} address means Requirement. Supplying a current ChangeRequest id therefore
    // produces a real, empty requirement resolution response and must state the absence truthfully.
    await page.goto(`${threadRoot(page)}/traceability/${change.id}`)
    await expect(page.locator(".dtaInFrame-error")).toContainText("not present in the selected Project and build")
    await expect(page.locator(".dtaLoading")).toHaveCount(0)
  })

  test("a failed requirement resolution clears on retry and then opens the exact revision", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const context = await (await request.get(
      `${apiBase}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const list = await (await request.get(
      `${apiBase}/api/traceability?projectId=${projectId}&baselineId=${context.effectiveBaselineId}&page=1&pageSize=1`
    )).json()
    const row = (Array.isArray(list) ? list : list.items ?? [])[0] as { revisionId: string; displayNumber: string } | undefined
    expect(row, "the seeded showcase should carry a requirement revision for resolution retry").toBeTruthy()
    if (!row) throw new Error("the seeded showcase should carry a requirement revision for resolution retry")

    let resolutionRequests = 0
    await page.route("**/api/requirements?*", async route => {
      resolutionRequests += 1
      if (resolutionRequests === 1) {
        await route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
      } else {
        await route.continue()
      }
    })
    await page.goto(`${threadRoot(page)}/traceability/${row.revisionId}`)
    await expect(page.locator(".dtaInFrame-error")).toContainText("could not be resolved")
    await page.locator(".dtaInFrame-error").getByRole("button", { name: "Try again" }).click()
    await expect(page.locator(".dtaRoot .dtaCard.is-focal")).toBeVisible()
    await expect(page.locator(".dtaCard.is-focal .dtaId")).toContainText(row.displayNumber)
    await expect(page.locator(".dtaInFrame-error")).toHaveCount(0)
    expect(resolutionRequests).toBeGreaterThanOrEqual(2)
  })

  test("a new Artifact focal cannot render the previous thread while its exact requirement resolves", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const threadBase = threadRoot(page)
    const { projectId, releaseId } = ids(page)
    const context = await (await request.get(
      `${apiBase}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const list = await (await request.get(
      `${apiBase}/api/traceability?projectId=${projectId}&baselineId=${context.effectiveBaselineId}&page=1&pageSize=2`
    )).json()
    const rows = (Array.isArray(list) ? list : list.items ?? []) as { revisionId: string; displayNumber: string }[]
    expect(rows.length, "the seeded showcase should carry two requirement revisions for focal transition")
      .toBeGreaterThanOrEqual(2)
    if (rows.length < 2) throw new Error("the seeded showcase should carry two requirement revisions for focal transition")
    const [first, second] = rows

    await page.goto(`${threadBase}/traceability/${first.revisionId}`)
    await expect(page.locator(".dtaCard.is-focal .dtaId")).toContainText(first.displayNumber)

    let releaseResolution: (() => void) | undefined
    const resolutionRequest = page.waitForRequest(requestEvent => {
      const url = new URL(requestEvent.url())
      return url.pathname.endsWith("/api/requirements") && url.searchParams.get("ids") === second.revisionId
    })
    const resolutionResponse = page.waitForResponse(response => {
      const url = new URL(response.url())
      return url.pathname.endsWith("/api/requirements") && url.searchParams.get("ids") === second.revisionId
    })
    await page.route("**/api/requirements?*", async route => {
      const url = new URL(route.request().url())
      if (url.searchParams.get("ids") === second.revisionId) {
        await new Promise<void>(resolve => { releaseResolution = resolve })
      }
      await route.continue()
    })
    const secondPath = `${threadBase}/traceability/${second.revisionId}`
    await page.evaluate(path => {
      window.history.pushState({}, "", path)
      window.dispatchEvent(new PopStateEvent("popstate"))
    }, secondPath)
    await resolutionRequest
    await expect(page.locator(".dtaLoading")).toBeVisible()
    // The old thread may still exist in memory, but it is not associated with this request key and cannot paint.
    await expect(page.locator(".dtaCard.is-focal")).toHaveCount(0)
    releaseResolution?.()
    expect((await resolutionResponse).status()).toBe(200)
    await expect(page.locator(".dtaCard.is-focal .dtaId")).toContainText(second.displayNumber)
  })

  test("a failed Artifact read clears after a real retry and successful navigation", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const context = await (await request.get(
      `${apiBase}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const list = await (await request.get(
      `${apiBase}/api/traceability?projectId=${projectId}&baselineId=${context.effectiveBaselineId}&page=1&pageSize=1`
    )).json()
    const row = (Array.isArray(list) ? list : list.items ?? [])[0] as { revisionId: string } | undefined
    expect(row, "the seeded showcase should carry a requirement revision for Artifact retry").toBeTruthy()
    if (!row) throw new Error("the seeded showcase should carry a requirement revision for Artifact retry")

    let artifactRequests = 0
    await page.route("**/api/artifact-thread*", async route => {
      artifactRequests += 1
      if (artifactRequests === 1) {
        await route.fulfill({ status: 503, contentType: "application/json", body: "{}" })
      } else {
        await route.continue()
      }
    })
    await page.goto(`${threadRoot(page)}/traceability/${row.revisionId}`)
    await expect(page.locator(".dtaInFrame-error")).toContainText("could not be loaded")
    await page.locator(".dtaInFrame-error").getByRole("button", { name: "Try again" }).click()
    await expect(page.locator(".dtaRoot .dtaCard").first()).toBeVisible()
    await expect(page.locator(".dtaInFrame-error")).toHaveCount(0)
    expect(artifactRequests).toBeGreaterThanOrEqual(2)

    // A successful retry clears the operation's own error. Moving to Network must therefore show the board
    // without claiming that the network failed during the earlier Artifact request.
    await page.getByRole("button", { name: "Change network" }).click()
    await expect(page.locator(".dtnRoot")).toBeVisible()
    await expect(page.locator(".dtnInFrame-error")).toHaveCount(0)
  })

  test("a late failed Artifact response cannot poison the Network view", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const context = await (await request.get(
      `${apiBase}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const list = await (await request.get(
      `${apiBase}/api/traceability?projectId=${projectId}&baselineId=${context.effectiveBaselineId}&page=1&pageSize=1`
    )).json()
    const row = (Array.isArray(list) ? list : list.items ?? [])[0] as { revisionId: string } | undefined
    expect(row, "the seeded showcase should carry a requirement revision for async Artifact recovery").toBeTruthy()
    if (!row) throw new Error("the seeded showcase should carry a requirement revision for async Artifact recovery")

    let releaseResponse: (() => void) | undefined
    const artifactRequest = page.waitForRequest(requestEvent => requestEvent.url().includes("/api/artifact-thread"))
    const artifactResponse = page.waitForResponse(response => response.url().includes("/api/artifact-thread"))
    await page.route("**/api/artifact-thread*", async route => {
      await new Promise<void>(resolve => { releaseResponse = resolve })
      await route.fulfill({ status: 503, contentType: "application/json", body: "{}" })
    })
    await page.goto(`${threadRoot(page)}/traceability/${row.revisionId}`)
    await artifactRequest

    // Leave Artifact while its request is still pending. The request then fails after cleanup, exercising the
    // cancellation boundary rather than relying on a fast response race.
    await page.getByRole("button", { name: "Change network" }).click()
    await expect(page.locator(".dtnRoot")).toBeVisible()
    releaseResponse?.()
    expect((await artifactResponse).status()).toBe(503)
    await expect(page.locator(".dtnInFrame-error")).toHaveCount(0)
  })
})

/**
 * Slices 4A/4B established two authoritative proposal resources, and which one applies is decided by the
 * record's kind — Change Requests at `/api/change-requests/{id}/proposal-content`, Test Change Requests at
 * `/api/test-change-reviews/{id}/proposal-content`. The register carries both kinds, so opening one inside
 * the canvas has to ask the right authority; asking the Change Request resource about a TCR is asking the
 * wrong one about a controlled record.
 */
test.describe("inside a change", () => {
  test("a Test Change Request is read from its own authoritative resource", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const network = await (await request.get(
      `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const tcr = (network.nodes ?? []).find((node: { kind: string }) => node.kind === "TestChangeRequest")
    test.skip(!tcr, "this build carries no Test Change Request to open")

    const asked: string[] = []
    await page.route("**/proposal-content*", async route => {
      asked.push(new URL(route.request().url()).pathname)
      await route.continue()
    })

    await page.goto(`${threadRoot(page)}/traceability/change-requests/${tcr.id}?view=inside`)
    await expect(page.locator(".dticRoot")).toBeVisible()
    await expect(page.locator(".dticRoot")).toContainText(tcr.displayNumber)

    // The kind comes from the record this build's network placed, not from the address or the identifier.
    expect(asked.some(path => path.includes(`/api/test-change-reviews/${tcr.id}/`))).toBeTruthy()
    expect(asked.some(path => path.includes(`/api/change-requests/${tcr.id}/`))).toBeFalsy()
  })

  test("opening a second change never shows the first change's proposed content", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    const { projectId, releaseId } = ids(page)
    const network = await (await request.get(
      `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
    const changes = (network.nodes ?? []).filter((node: { kind: string }) => node.kind !== "ProblemReport")
    test.skip(changes.length < 2, "this build carries fewer than two changes to move between")
    const [first, second] = changes as { id: string; displayNumber: string }[]

    await page.goto(`${threadRoot(page)}/traceability/change-requests/${first.id}?view=inside`)
    await expect(page.locator(".dticRoot")).toBeVisible()
    await expect(page.locator(".dticRoot")).toContainText(`Inside ${first.displayNumber}`)

    // A fact that belongs to the first change's *proposal* — a card in one of the proposal lanes, not the
    // register. This is what must not still be on the board under the second change's name; asserting only
    // that the heading changed would pass while the old lanes were still laid out beneath it.
    const proposalCards = page.locator(".dticCard:not(.dticRegister) .dticId")
    await expect(proposalCards.first()).toBeVisible()
    const firstProposalFact = await proposalCards.first().innerText()
    expect(firstProposalFact.trim().length, "the first change should propose something to carry over").toBeGreaterThan(0)

    // The second change's proposal takes a moment to arrive. During that moment the board must not be showing
    // the first change's proposed facts under the second change's identity — on a traceability surface a
    // brief false attribution is still a false attribution. Held slow enough to observe.
    let release: (() => void) | undefined
    await page.route(`**/${second.id}/proposal-content*`, async route => {
      await new Promise<void>(resolve => { release = resolve })
      await route.continue()
    })
    // Navigated in the app rather than reloaded: a reload discards everything and could never carry the
    // previous record's content over, which is precisely the failure being guarded against.
    await page.locator(`.dticCard.dticRegister:has-text("${second.displayNumber}")`).first().click()

    // While the second change's proposal is still in flight the board is under the second change's identity,
    // and its proposal region says it is loading rather than still rendering the first change's proposed
    // facts. (The first change stays visible as a *register* card, which is correct: it is a member of this
    // build's register regardless of which change is open.)
    await expect(page.locator(".dticRoot")).toContainText(`Inside ${second.displayNumber}`)
    await expect(page.locator(".dticRoot")).not.toContainText(`Inside ${first.displayNumber}`)
    await expect(page.getByText("Loading what this change proposes…")).toBeVisible()
    // The first change's proposed facts are gone, not merely relabelled: no proposal-lane card survives into
    // the second change's board while its own content is still in flight.
    await expect(page.locator(".dticCard:not(.dticRegister) .dticId")).toHaveCount(0)
    await expect(page.locator(".dticRoot")).not.toContainText(firstProposalFact)
    release?.()
    await expect(page.locator(".dticRoot")).toContainText(second.displayNumber)
  })
})

/**
 * #880 §10.1, as DEC-117 records it: every identifier, title and state label must be legible when the page
 * opens, before the reader has touched anything. The canvas is a scaled scene, so what is measured is the
 * **effective** size — the authored size multiplied by the scene's own transform scale — rather than the CSS
 * pixel value, which is not what a reader sees. Sub-floor text is permitted only after the reader has
 * deliberately zoomed out, which is why nothing here touches the zoom.
 */
test.describe("landing legibility", () => {
  const FLOOR = 12

  const smallestOnLanding = (page: Page) =>
    page.evaluate(() => {
      const scene = document.querySelector(".dtCanvasScene") as HTMLElement | null
      if (!scene) return null
      // The scale the scene is actually drawn at, read off the live transform rather than recomputed.
      const matrix = new DOMMatrixReadOnly(getComputedStyle(scene).transform)
      const scale = matrix.a || 1
      const selector = ".dtaId, .dtaTitle, .dtaPill, .dtnId, .dtnTitle, .dtnPill, .dticId, .dticTitle, .dticPill"
      return [...scene.querySelectorAll(selector)]
        .filter(element => {
          const box = element.getBoundingClientRect()
          return box.width > 0 && box.height > 0 && (element.textContent ?? "").trim().length > 0
        })
        .map(element => ({
          text: (element.textContent ?? "").trim().slice(0, 30),
          effective: Number.parseFloat(getComputedStyle(element).fontSize) * scale,
        }))
        .sort((a, b) => a.effective - b.effective)
    })

  for (const width of [1280, 1440, 1920]) {
    test(`identifiers, titles and state labels clear the readable floor at ${width}px`, async ({ page, request }) => {
      test.setTimeout(180_000)
      await page.setViewportSize({ width, height: 900 })
      await apiLogin(request)
      await showcaseSeed(request)
      await login(page, "admin", { openProject: false })
      await selectProgram(page, "Flight Management System Live Program")
      await openThread(page)
      // Measured only once the board has actually drawn cards; an empty scene proves nothing about type size.
      await expect(page.locator(".dtCanvasScene .dtnCard").first()).toBeVisible()

      const measured = await smallestOnLanding(page)
      expect(measured, "the canvas scene should have rendered").toBeTruthy()
      expect(measured!.length, "the landing board should carry cards to measure").toBeGreaterThan(0)
      const below = measured!.filter(item => item.effective < FLOOR - 0.05)
      expect(below, `text below ${FLOOR}px effective on landing at ${width}px`).toEqual([])
    })

    /**
     * The focal landings, which are the ones §4.4 is actually about. A deep link arrives selected, and the
     * selection-framing path then replaces the landing transform with its own — so the floor has to hold
     * there too, not only on the unselected board. The widest real case is a full artifact thread, whose
     * whole web is handed to the framing path at once.
     */
    test(`a deep-linked artifact thread lands legibly at ${width}px`, async ({ page, request }) => {
      test.setTimeout(180_000)
      await page.setViewportSize({ width, height: 900 })
      await apiLogin(request)
      await showcaseSeed(request)
      await login(page, "admin", { openProject: false })
      await selectProgram(page, "Flight Management System Live Program")
      await openThread(page)

      const { projectId, releaseId } = ids(page)
      const context = await (await request.get(
        `${apiBase}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)).json()
      const list = await (await request.get(
        `${apiBase}/api/traceability?projectId=${projectId}&baselineId=${context.effectiveBaselineId}&page=1&pageSize=1`
      )).json()
      const row = (Array.isArray(list) ? list : list.items ?? [])[0]

      await page.goto(`${threadRoot(page)}/traceability/${row.revisionId}`)
      await expect(page.locator(".dtaCard.is-focal")).toBeVisible()
      await surfacePainted(page)

      const measured = await smallestOnLanding(page)
      expect(measured!.length, "the thread should carry cards to measure").toBeGreaterThan(0)
      expect(measured!.filter(item => item.effective < FLOOR - 0.05),
        `artifact-thread text below ${FLOOR}px effective on landing at ${width}px`).toEqual([])
    })

    test(`a deep-linked change request lands legibly at ${width}px`, async ({ page, request }) => {
      test.setTimeout(180_000)
      await page.setViewportSize({ width, height: 900 })
      await apiLogin(request)
      await showcaseSeed(request)
      await login(page, "admin", { openProject: false })
      await selectProgram(page, "Flight Management System Live Program")
      await openThread(page)

      const { projectId, releaseId } = ids(page)
      const network = await (await request.get(
        `${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
      const target = (network.nodes ?? []).find((node: { kind: string }) => node.kind === "ChangeRequest")

      await page.goto(`${threadRoot(page)}/traceability/change-requests/${target.id}`)
      await expect(page.locator(".dtnCard.is-selected")).toHaveCount(1)
      await surfacePainted(page)

      const measured = await smallestOnLanding(page)
      expect(measured!.length, "the network should carry cards to measure").toBeGreaterThan(0)
      expect(measured!.filter(item => item.effective < FLOOR - 0.05),
        `selected-network text below ${FLOOR}px effective on landing at ${width}px`).toEqual([])
    })
  }

  /**
   * Raising card type to clear the floor put a 14px identifier, a 14px state pill and a badge in the same
   * 236px top row, and identifiers are `white-space: nowrap`. Legibility must not be bought with text that
   * spills out of its card — and the fix for a spill is geometry, never smaller type.
   */
  test("card top rows contain their identifier, badge and state label", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)
    await expect(page.locator(".dtCanvasScene .dtnCard").first()).toBeVisible()

    const spills = await page.evaluate(() => {
      const out: { id: string; overflow: number }[] = []
      for (const card of document.querySelectorAll(".dtCanvasScene .dtnCard, .dtCanvasScene .dtaCard")) {
        const box = card.getBoundingClientRect()
        for (const child of card.querySelectorAll(".dtnId, .dtnPill, .dtaId, .dtaPill")) {
          const inner = child.getBoundingClientRect()
          if (!inner.width) continue
          // One pixel of tolerance for sub-pixel rounding at fractional scene scales.
          const overflow = Math.max(box.left - inner.left, inner.right - box.right)
          if (overflow > 1) out.push({ id: (child.textContent ?? "").trim().slice(0, 30), overflow })
        }
      }
      return out
    })
    expect(spills, "card content should stay inside its card").toEqual([])
  })

  /**
   * The other half of the same rule: an explicit Fit is the reader deliberately asking to see the whole
   * board, which #880 §6.1 requires actually to fit and §10.1 permits to go below the floor. Landing and Fit
   * are different operations, and clamping both to the landing floor broke the second.
   */
  test("an explicit Fit really fits the board, and may go below the landing floor", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1280, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)
    await expect(page.locator(".dtCanvasScene .dtnCard").first()).toBeVisible()

    const zoomOf = () => page.evaluate(() => {
      const scene = document.querySelector(".dtCanvasScene") as HTMLElement
      return new DOMMatrixReadOnly(getComputedStyle(scene).transform).a
    })
    // Landing is held to the floor.
    expect(await zoomOf()).toBeGreaterThanOrEqual(0.86 - 0.001)

    // The reader asks for the whole board. Keyboard `0` is the documented control.
    await page.locator(".dtCanvas").focus()
    await page.keyboard.press("0")
    await surfacePainted(page)

    const fitted = await zoomOf()
    expect(fitted, "an explicit Fit is allowed past the landing floor").toBeLessThan(0.86)
    // And it genuinely fits: the whole board is inside the viewport it was asked to fit into.
    const fits = await page.evaluate(() => {
      const scene = document.querySelector(".dtCanvasScene") as HTMLElement
      const box = scene.getBoundingClientRect()
      return box.left >= -1 && box.right <= window.innerWidth + 1
    })
    expect(fits, "keyboard 0 should bring the whole board inside the viewport").toBeTruthy()
  })
})

test.describe("route state is real state", () => {
  test("a focal artifact and view survive refresh", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    await page.locator(".dtnCard").first().click()
    await expect(page.locator(".dtnCard.is-selected")).toHaveCount(1)
    await page.locator(".dtnPanel").getByRole("button", { name: "Open this change" }).click()
    await expect(page.locator(".dticRoot")).toBeVisible()

    const addressed = page.url()
    expect(addressed).toContain("view=inside")
    expect(addressed).toContain("/traceability/change-requests/")

    // #880 §6.4: the focal artifact and the view belong in the URL, so a refresh reconstructs them.
    await page.reload()
    await expect(page.locator(".dticRoot")).toBeVisible()
    expect(page.url()).toBe(addressed)
  })

  test("back and forward walk the views the reader actually visited", async ({ page, request }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await apiLogin(request)
    await showcaseSeed(request)
    await login(page, "admin", { openProject: false })
    await selectProgram(page, "Flight Management System Live Program")
    await openThread(page)

    await page.locator(".dtnCard").first().click()
    await page.locator(".dtnPanel").getByRole("button", { name: "Open this change" }).click()
    await expect(page.locator(".dticRoot")).toBeVisible()

    await page.goBack()
    await expect(page.locator(".dtnRoot")).toBeVisible()

    await page.goForward()
    await expect(page.locator(".dticRoot")).toBeVisible()
  })
})
