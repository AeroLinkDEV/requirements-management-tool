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
  displayNumber: "Test procedure assessment of SRCR-00039.00",
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
  // Present, and separately labelled.
  expect(node.verification?.sourceDisplayNumber).toBe("SRCR-00039.00")
  // The label mentions the source, which is the point — but it is not the source's number standing alone
  // where this record's own number belongs.
  expect(node.displayNumber).not.toBe("SRCR-00039.00")
  expect(node.displayNumber).toContain("SRCR-00039.00")
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
  const assessments = page.locator(".dtnId", { hasText: "Test procedure assessment of SRCR-00039.00" })
  await expect(assessments).toHaveCount(2)

  // The controlled package is still shown by its number.
  await expect(page.locator(".dtnId", { hasText: "SYSTPCR-000012.00" })).toHaveCount(1)

  // Badges: one TCR for the controlled package, and the assessments not counted among them.
  await expect(page.locator(".dtCanvasNode", { has: page.locator("text=SYSTPCR-000012.00") })).toHaveCount(1)
  const badges = await page.locator(".dtnBadge").allTextContents()
  expect(badges.filter(badge => badge === "TCR")).toHaveLength(1)
})
