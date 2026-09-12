import { expect, test, type Locator } from "@playwright/test"
import {
  assignRows,
  badgeOf,
  controlledIdentityLabel,
  isUnnumberedAssessment,
  laneModel,
  type NetworkNode,
} from "../src/changeNetworkPresentation"

/**
 * #1016 S13A. A verification package that holds no controlled number, and one that does.
 *
 * A package raised to assess an approved change is deliberately unnumbered until somebody concludes that
 * verification work is required, so "no controlled number" is an ordinary current state rather than a fault.
 * The trace node used to be labelled by formatting a revision onto an empty base number — `.00`, a suffix
 * attached to nothing — which reads as a corrupt identifier and says nothing about which record it is.
 *
 * Two facts are kept apart here and must stay apart: a label a person can read, and whether a governed number
 * exists. The second is answered by the server, never by parsing the first.
 */

const assessment = (over: Partial<NetworkNode> = {}): NetworkNode => ({
  id: "asmt-a",
  kind: "TestChangeRequest",
  level: "Procedure",
  displayNumber: "Unnumbered assessment",
  verification: {
    hasControlledNumber: false,
    outcome: "Pending",
    artifactKind: "Procedure",
    discipline: "System",
    originKind: "ChangeRequest",
    originReferenceId: "sys-9",
    sourceDisplayNumber: "SRCR-00039.00",
  },
  ...over,
})

const controlled = (over: Partial<NetworkNode> = {}): NetworkNode => ({
  id: "tcr-9",
  kind: "TestChangeRequest",
  level: "Procedure",
  displayNumber: "SYSTPCR-000012.00",
  verification: {
    hasControlledNumber: true,
    controlledNumber: "SYSTPCR-000012",
    controlledRevision: 0,
    outcome: "ChangeRequired",
    artifactKind: "Procedure",
    discipline: "System",
    originKind: "ChangeRequest",
    originReferenceId: "sys-9",
    sourceDisplayNumber: "SRCR-00039.00",
  },
  ...over,
})

test("an unnumbered assessment does not wear the badge of a controlled test change request", () => {
  // The badge is how a reader scanning the board counts raised verification work. An assessment nobody has
  // concluded is not raised work, and calling it TCR would inflate that count with records that do not exist
  // as controlled requests.
  expect(badgeOf(assessment())).not.toBe("TCR")
  expect(badgeOf(controlled())).toBe("TCR")

  // And the answer comes from the server's statement, not from reading the label.
  expect(isUnnumberedAssessment(assessment())).toBe(true)
  expect(isUnnumberedAssessment(controlled())).toBe(false)
})

test("a node that carries no verification metadata is treated as controlled, not as an assessment", () => {
  // Compatibility: a response from before this field existed must not have every verification node
  // reclassified as an assessment on the strength of an absent property.
  const legacy: NetworkNode = { id: "tcr-legacy", kind: "TestChangeRequest", displayNumber: "SYSTPCR-000001.00" }
  expect(isUnnumberedAssessment(legacy)).toBe(false)
  expect(badgeOf(legacy)).toBe("TCR")
})

test("the source number is carried as context and never becomes the record's own identity", () => {
  const node = assessment()
  // Present, and in a field of its own.
  expect(node.verification?.sourceDisplayNumber).toBe("SRCR-00039.00")
  // The source travels separately, not inside the identifier. Two reasons, both real: a source number
  // standing where this record's own number belongs gets read as its identity, and a card's identifier row
  // is narrow — an earlier draft of this correction put the source in the label and it spilled out of six
  // cards on the board, which the landing legibility journey caught.
  expect(node.displayNumber).not.toContain("SRCR-00039.00")
  expect(node.displayNumber).toBe("Unnumbered assessment")
  // And no controlled number is invented for it.
  expect(node.verification?.controlledNumber ?? null).toBeNull()
  expect(node.verification?.controlledRevision ?? null).toBeNull()
})

test("two unnumbered assessments from one source stay distinct and stably ordered", () => {
  // They share a label by design: both are assessments of the same approved change. That is exactly why the
  // label cannot be the key, and why ordering needs the stable id behind it.
  const first = assessment({ id: "asmt-a" })
  const second = assessment({ id: "asmt-b" })
  expect(first.displayNumber).toBe(second.displayNumber)

  const rows = assignRows([second, first], laneModel(["System"]))
  expect(rows.size, "neither record may be dropped for sharing a label").toBe(2)
  expect(rows.get("asmt-a")).not.toBe(rows.get("asmt-b"))

  // Stable across input order: the same two records must land in the same two rows however they arrive.
  const reversed = assignRows([first, second], laneModel(["System"]))
  expect(reversed.get("asmt-a")).toBe(rows.get("asmt-a"))
  expect(reversed.get("asmt-b")).toBe(rows.get("asmt-b"))
})

test("a controlled package at revision 00 keeps its complete identity", () => {
  // The correction must not reach records that are numbered. Revision 00 is a legitimate controlled revision
  // and reads exactly as it always did.
  const node = controlled()
  expect(node.displayNumber).toBe("SYSTPCR-000012.00")
  expect(node.verification?.controlledNumber).toBe("SYSTPCR-000012")
  expect(node.verification?.controlledRevision).toBe(0)
})

test("the rendered board shows both assessments, neither as a bare revision nor as a TCR", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=verification-identity")

  const cards = page.locator(".dtCanvasNode")
  // Four records plus the unusable-metadata record the adapter refusal case needs.
  await expect(cards).toHaveCount(5)

  // The defect, in the words it appeared in: a revision suffix attached to nothing.
  await expect(page.locator(".dtnId", { hasText: /^\.\d{2}$/ })).toHaveCount(0)

  // Both assessments are drawn, and both say what they are.
  const assessments = page.locator(".dtnId", { hasText: "Unnumbered assessment" })
  await expect(assessments).toHaveCount(2)

  // The controlled package is still shown by its number.
  await expect(page.locator(".dtnId", { hasText: "SYSTPCR-000012.00" })).toHaveCount(1)

  // Badges: one TCR for the controlled package, and the assessments not counted among them.
  await expect(page.locator(".dtCanvasNode", { has: page.locator("text=SYSTPCR-000012.00") })).toHaveCount(1)
  // Two records read as controlled here: the numbered package, and the one whose metadata is present but
  // unusable — which is not an assessment either, and is deliberately not reclassified as one.
  const badges = await page.locator(".dtnBadge").allTextContents()
  expect(badges.filter(badge => badge === "TCR")).toHaveLength(2)
})

test("an unnumbered card shows its source as context, and the inspector separates every fact", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=verification-identity")

  // The card: source context in the secondary line, labelled as what it is assessing, and never appended to
  // the identifier above it.
  // Addressed by stable id, not by label — which is the point: both assessments carry the same label, and
  // selecting one must never be ambiguous with the other.
  const cards = page.locator(".dtCanvasNode")
  const assessment = page.locator('[data-node-id="asmt-a"]')
  await expect(assessment).toBeVisible()
  await expect(assessment.locator(".dtnSource")).toContainText("Assessing change request SRCR-00039.00")
  await expect(assessment.locator(".dtnId")).toHaveText("Unnumbered assessment")

  // Nothing spills out of its card — the failure an earlier version of this label caused.
  const spills = await page.evaluate(() => {
    const out: string[] = []
    for (const card of document.querySelectorAll(".dtCanvasScene .dtnCard")) {
      const box = card.getBoundingClientRect()
      for (const child of card.querySelectorAll(".dtnId, .dtnPill, .dtnSource")) {
        const inner = child.getBoundingClientRect()
        if (!inner.width) continue
        if (Math.max(box.left - inner.left, inner.right - box.right) > 1) out.push(child.textContent ?? "")
      }
    }
    return out
  })
  expect(spills, "card content must stay inside its card").toEqual([])

  // The inspector: four facts, separately labelled, none derived from another.
  // Activated by keyboard, as the canvas's own specs do: selecting a card reframes the scene, so a pointer
  // click on a sibling afterwards races the pan. Focus and Enter select without depending on where the
  // canvas has moved to.
  const select = async (id: string) => {
    const card = page.locator(`[data-node-id="${id}"]`)
    await card.focus()
    await page.keyboard.press("Enter")
    await expect(card).toHaveAttribute("aria-pressed", "true")
  }
  await select("asmt-a")
  const facts = page.locator(".dtnVerificationFacts")
  await expect(facts).toBeVisible()
  await expect(facts).toContainText("Controlled number")
  await expect(facts).toContainText("None recorded")
  await expect(facts).toContainText("Assessing change request SRCR-00039.00")
  await expect(facts).toContainText("Pending assessment")

  // A recorded no-change conclusion in Draft must not read as approved evidence. Both facts, side by side.
  await select("asmt-b")
  await expect(facts).toContainText("No change required")
  await expect(facts).toContainText("Draft")
  await expect(facts).not.toContainText("Approved")

  // Selecting the second record shows that record's own source, not the previous selection's.
  await expect(facts).toContainText("Problem Report PR-00004321.00")
  await expect(facts).not.toContainText("SRCR-00039.00")

  // The numbered package keeps its controlled identity in the same panel.
  await expect(cards.filter({ hasText: "Unnumbered assessment" })).toHaveCount(2)
  await select("tcr-9")
  await expect(facts).toContainText("SYSTPCR-000012.00")
  await expect(facts).toContainText("Change required")

  await page.screenshot({ path: "test-results-s13a/verification-identity-inspector.png", fullPage: true })
})

/**
 * #1016 S13A-01. The rendered link, through the page's own adapter and the production router.
 *
 * `DigitalThreadPage` rebuilds an identity with `exactCardIdentity` before handing it to
 * `exactTraceArtifactPath`, and that rebuild dropped the verification facts — so the router fell back to
 * reading the identifier's prefix even though the node had stated its discipline, and an unnumbered System
 * assessment addressed the software workspace. Asserting the href a rendered card actually carries is the
 * only way to catch that: a direct call to the router passes either way.
 */
test("a rendered unnumbered card links to its own discipline, through the page's adapter", async ({ page, context }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=verification-identity")

  const link = page.locator('[data-node-id="asmt-a"] .dtnId')
  await expect(link).toBeVisible()
  const href = await link.getAttribute("href") ?? ""

  // The System assessment addresses the System workspace, and carries its own id and full scope.
  expect(href).toContain("/system-verification/change-requests/asmt-a")
  expect(href).not.toContain("/software-verification/")
  expect(href).toContain("/programs/program-a/projects/")
  expect(href).toContain("kind=Procedure")

  // Its same-label sibling addresses its own record, not this one.
  const sibling = await page.locator('[data-node-id="asmt-b"] .dtnId').getAttribute("href") ?? ""
  expect(sibling).toContain("asmt-b")
  expect(sibling).not.toBe(href)

  // The numbered package is unchanged.
  expect(await page.locator('[data-node-id="tcr-9"] .dtnId').getAttribute("href") ?? "")
    .toContain("/system-verification/change-requests/tcr-9")

  // Present-but-unusable metadata, through the same adapter and the same router (R4-E02). Its label is
  // `SYSTPCR-000099.00`, whose prefix the legacy derivation would route confidently — so a link here would
  // mean `exactCardIdentity` had turned `{}` into absent metadata on the way through, and the record had
  // been addressed from its label after all. No link is the discriminating result.
  const unusable = page.locator('[data-node-id="asmt-empty"] .dtnId')
  await expect(unusable).toBeVisible()
  await expect(unusable).toHaveText("SYSTPCR-000099.00")
  expect(await unusable.getAttribute("href"), "unusable metadata must not produce an exact link").toBeNull()
  await expect(page.locator('[data-node-id="asmt-empty"] [data-exact-artifact-link="unresolved"]'))
    .toBeVisible()

  // Supported new-tab activation reaches the declared destination.
  const opened = context.waitForEvent("page")
  await link.click({ modifiers: ["ControlOrMeta"] })
  const newTab = await opened
  await newTab.waitForURL(url => url.pathname.includes("/change-requests/asmt-a"))
  expect(new URL(newTab.url()).pathname + new URL(newTab.url()).search).toBe(href)
  await newTab.close()

  // And ordinary activation reaches the same one. This is the assertion the packet previously claimed from a
  // Ctrl-click alone, which proves only the native path: a plain click is the one an onOpen override could
  // divert, so it has to be performed rather than inferred.
  await link.click()
  await expect.poll(() => new URL(page.url()).pathname + new URL(page.url()).search).toBe(href)

  // What this does not establish: these are the fixture's own synthetic record ids. Reaching the address
  // proves the adapter and the router agree on it; it says nothing about a backend resolving that record.
})

test("a missing controlled revision is reported, not rendered as .00", () => {
  // An actual zero is a real controlled revision.
  expect(controlledIdentityLabel({
    hasControlledNumber: true, controlledNumber: "SYSTPCR-000012", controlledRevision: 0,
    outcome: "ChangeRequired", artifactKind: "Procedure", discipline: "System",
    originKind: "ChangeRequest", originReferenceId: "sys-9",
  })).toBe("SYSTPCR-000012.00")

  // A missing one is not. Printing ".00" here would invent the precise part of an identifier a reader relies
  // on, for a record whose revision identity was never recorded.
  for (const revision of [undefined, null]) {
    expect(controlledIdentityLabel({
      hasControlledNumber: true, controlledNumber: "SYSTPCR-000012",
      controlledRevision: revision as number | undefined,
      outcome: "ChangeRequired", artifactKind: "Procedure", discipline: "System",
      originKind: "ChangeRequest", originReferenceId: "sys-9",
    })).toBe("SYSTPCR-000012 · revision not recorded")
  }

  // No controlled number at all: taken from the authoritative fact, never from a revision counter.
  expect(controlledIdentityLabel({
    hasControlledNumber: false, controlledRevision: 3,
    outcome: "Pending", artifactKind: "Procedure", discipline: "System",
    originKind: "ChangeRequest", originReferenceId: "sys-9",
  })).toBe("None recorded")
})

test("the Table carries the same source and outcome, with state kept separate", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=verification-identity&view=table")

  // The accessible representation must not be the poorer one: a reader here needs the same two facts the Map
  // shows, in the same words, from the same formatters.
  const table = page.locator(".dtThreadTable")
  await expect(table).toBeVisible()
  await expect(table.locator("thead th")).toContainText(
    ["Select", "Change", "Level", "Source", "Outcome", "State"])

  // Read by column, and each row found by its own Change cell rather than by position — a change request row
  // also names the assessments that verify it, so matching on row text would select the wrong row, and row
  // order is not this test's subject.
  const rows = table.locator("tbody tr")
  const rowWhereChange = (text: string, nth = 0) =>
    rows.filter({ has: page.locator("td:nth-child(2)", { hasText: text }) }).nth(nth)
  const cell = (row: ReturnType<typeof rowWhereChange>, column: number) => row.locator("td").nth(column)
  const source = (row: ReturnType<typeof rowWhereChange>) => cell(row, 3)
  const outcome = (row: ReturnType<typeof rowWhereChange>) => cell(row, 4)
  const state = (row: ReturnType<typeof rowWhereChange>) => cell(row, 5)

  // The change request itself has no verification facts, and borrows none.
  const changeRequest = rowWhereChange("SRCR-00039.00")
  await expect(source(changeRequest)).toHaveText("—")
  await expect(outcome(changeRequest)).toHaveText("—")

  // The pending assessment: source, outcome and lifecycle state in three separate cells.
  const pending = rowWhereChange("Unnumbered assessment", 0)
  await expect(source(pending)).toHaveText("Assessing change request SRCR-00039.00")
  await expect(outcome(pending)).toHaveText("Pending assessment")
  await expect(state(pending)).toHaveText("Draft")

  // The concluded one. "No change required" is what it concluded; "Draft" is how far that has got. Both are
  // visible, because a written conclusion is not a signed one — and its source is its own.
  const concluded = rowWhereChange("Unnumbered assessment", 1)
  await expect(source(concluded)).toContainText("Assessing Problem Report PR-00004321.00")
  await expect(outcome(concluded)).toHaveText("No change required")
  await expect(state(concluded)).toHaveText("Draft")
  await expect(state(concluded)).not.toHaveText("Approved")

  // The numbered package keeps its own identity, outcome and state.
  const numbered = rowWhereChange("SYSTPCR-000012.00")
  await expect(outcome(numbered)).toHaveText("Change required")
  await expect(state(numbered)).toHaveText("In review")

  // And the unusable-metadata record borrows nothing either: present but unreadable is not a source.
  const unusable = rowWhereChange("SYSTPCR-000099.00")
  await expect(source(unusable)).toHaveText("—")
  await expect(outcome(unusable)).toHaveText("—")

  // A record with no verification metadata leaves the new cells empty rather than borrowing another's.
  await page.goto("/tests/fixtures/change-network.html?view=table")
  const legacyRow = page.locator(".dtThreadTable tbody tr").filter({ hasText: "LLRTPCR-000009.00" }).first()
  await expect(legacyRow.locator("td").nth(3)).toHaveText("—")
  await expect(legacyRow.locator("td").nth(4)).toHaveText("—")
})

/**
 * #1016 S13A. The four facts have to be reachable and readable, not merely present in the DOM.
 *
 * The inspector's identity column scrolls when the panel is docked to the bottom, so a capture of the
 * unscrolled state shows the outcome clipped and the lifecycle state below the fold. That is the panel's
 * existing behaviour, not a defect — but "in the DOM" is not "a reader can read it", and `toContainText`
 * cannot tell those apart.
 *
 * Measurement here never moves anything. A helper that scrolls the target into view before measuring it
 * proves only that the browser can scroll, which is not the claim.
 */

/** Read-only. Is this value fully inside its own scroller, and a real line of text rather than a sliver? */
const readable = (facts: Locator, label: string) =>
  facts.locator("div").filter({ hasText: label }).locator("dd").evaluate(node => {
    const box = node.getBoundingClientRect()
    const scroller = node.closest(".dtnPanelIdentityCol")!.getBoundingClientRect()
    return box.height >= 12 && box.top >= scroller.top - 1 && box.bottom <= scroller.bottom + 1
  })

test("every verification fact is geometrically readable in each dock", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=verification-identity")

  const card = page.locator('[data-node-id="asmt-a"]')
  await card.focus()
  await page.keyboard.press("Enter")

  const panel = page.locator(".dtnPanel")
  const facts = page.locator(".dtnVerificationFacts")
  await expect(facts).toBeVisible()

  // Programmatic geometry coverage, labelled as such: each value is brought into view and then measured, so
  // this establishes that nothing is clipped or shrunk once it is on screen — not that a user can get there.
  for (const dock of ["Bottom", "Right", "Auto"]) {
    await panel.getByRole("button", { name: dock, exact: true }).click()
    await expect(panel.getByRole("button", { name: dock, exact: true })).toHaveAttribute("aria-pressed", "true")

    for (const label of ["Controlled number", "Source", "Assessment outcome", "Lifecycle state"]) {
      await facts.locator("div").filter({ hasText: label }).locator("dd").scrollIntoViewIfNeeded()
      expect(await readable(facts, label), `${label} must be readable with the panel docked ${dock}`).toBe(true)
    }

    await expect(page.locator(".dtnPanelIdentityCol h3")).toBeVisible()
    await expect(panel.getByRole("button", { name: "Close detail" })).toBeVisible()
  }
})

test("a keyboard user can reach and scroll the inspector to the facts below the fold", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=verification-identity")

  const card = page.locator('[data-node-id="asmt-a"]')
  await card.focus()
  await page.keyboard.press("Enter")

  const panel = page.locator(".dtnPanel")
  await panel.getByRole("button", { name: "Bottom", exact: true }).click()
  const column = page.locator(".dtnPanelIdentityCol")
  const facts = page.locator(".dtnVerificationFacts")
  await expect(facts).toBeVisible()

  // The premise: at rest, the last fact is below the fold. Without this the rest proves nothing.
  const scrolls = await column.evaluate(node => node.scrollHeight > node.clientHeight + 1)
  expect(scrolls, "the identity column must actually scroll for this to be the case under test").toBe(true)
  expect(await readable(facts, "Lifecycle state"), "the last fact should start below the fold").toBe(false)

  // Reached by tabbing, not by calling focus(): whether a keyboard user can get into the scroll context is
  // the part in question.
  let inside = false
  for (let press = 0; press < 40 && !inside; press += 1) {
    await page.keyboard.press("Tab")
    inside = await column.evaluate(node => node.contains(document.activeElement))
  }
  expect(inside, "keyboard navigation must reach the identity column").toBe(true)

  // A real key, on the real focus context. Nothing here moves the target for the measurement that follows.
  const before = await column.evaluate(node => node.scrollTop)
  await page.keyboard.press("End")
  await expect.poll(() => column.evaluate(node => node.scrollTop)).toBeGreaterThan(before)

  expect(await readable(facts, "Lifecycle state"),
    "the last fact must be readable after a keyboard scroll, with nothing scrolling it into view")
    .toBe(true)
})

test("a pointer user can scroll the inspector to the same facts", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=verification-identity")

  const card = page.locator('[data-node-id="asmt-a"]')
  await card.focus()
  await page.keyboard.press("Enter")
  await page.locator(".dtnPanel").getByRole("button", { name: "Bottom", exact: true }).click()

  const column = page.locator(".dtnPanelIdentityCol")
  const facts = page.locator(".dtnVerificationFacts")
  await expect(facts).toBeVisible()
  expect(await readable(facts, "Lifecycle state")).toBe(false)

  // An ordinary wheel over the panel, which is how a reader with a mouse would actually do it.
  const box = (await column.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.wheel(0, 400)
  await expect.poll(() => column.evaluate(node => node.scrollTop)).toBeGreaterThan(0)

  expect(await readable(facts, "Lifecycle state")).toBe(true)

  // Settled framing keeps the selected card readable; captured once, and not re-captured here.
  await page.mouse.move(4, 4)
  await page.waitForTimeout(700)
  await expect(card).toHaveAttribute("aria-pressed", "true")
  expect(await card.evaluate(node => Number(getComputedStyle(node).opacity))).toBeGreaterThan(0.85)
})
