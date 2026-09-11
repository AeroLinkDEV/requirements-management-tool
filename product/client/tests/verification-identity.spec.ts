import { expect, test } from "@playwright/test"
import {
  assignRows,
  badgeOf,
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
  await expect(cards).toHaveCount(4)

  // The defect, in the words it appeared in: a revision suffix attached to nothing.
  await expect(page.locator(".dtnId", { hasText: /^\.\d{2}$/ })).toHaveCount(0)

  // Both assessments are drawn, and both say what they are.
  const assessments = page.locator(".dtnId", { hasText: "Unnumbered assessment" })
  await expect(assessments).toHaveCount(2)

  // The controlled package is still shown by its number.
  await expect(page.locator(".dtnId", { hasText: "SYSTPCR-000012.00" })).toHaveCount(1)

  // Badges: one TCR for the controlled package, and the assessments not counted among them.
  await expect(page.locator(".dtCanvasNode", { has: page.locator("text=SYSTPCR-000012.00") })).toHaveCount(1)
  const badges = await page.locator(".dtnBadge").allTextContents()
  expect(badges.filter(badge => badge === "TCR")).toHaveLength(1)
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

  // Ordinary activation and a supported new tab reach the same exact record and scope.
  const opened = context.waitForEvent("page")
  await link.click({ modifiers: ["ControlOrMeta"] })
  const newTab = await opened
  await newTab.waitForURL(url => url.pathname.includes("/change-requests/asmt-a"))
  expect(new URL(newTab.url()).pathname + new URL(newTab.url()).search).toBe(href)
  await newTab.close()
})
