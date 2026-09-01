import { expect, test } from "@playwright/test"
import {
  LANE_HLR,
  LANE_LLR,
  LANE_PROBLEM,
  LANE_SYSTEM,
  LANE_VERIFICATION,
  type NetworkEdge,
  type NetworkNode,
  assignRows,
  badgeOf,
  groupOf,
  isSuspectEdge,
  laneOf,
  pillFor,
  resolveDock,
} from "../src/changeNetworkPresentation"
import { stateLabel } from "../src/presentation"

// Lane, badge and dock are pure, so they are asserted directly. The dock rule in particular is the behaviour
// the design review pushed hardest on: a detail panel must never come to rest on a linked record.

const node = (over: Partial<NetworkNode> & { id: string }): NetworkNode => ({
  kind: "ChangeRequest",
  displayNumber: over.id,
  ...over,
})

test.describe("change network presentation", () => {
  test("lane and badge come from the server-stated kind and level, never the identifier", () => {
    expect(laneOf(node({ id: "PR-1", kind: "ProblemReport" }))).toBe(LANE_PROBLEM)
    expect(laneOf(node({ id: "a", level: "System" }))).toBe(LANE_SYSTEM)
    expect(laneOf(node({ id: "b", level: "HighLevel" }))).toBe(LANE_HLR)
    expect(laneOf(node({ id: "c", level: "LowLevel" }))).toBe(LANE_LLR)
    expect(laneOf(node({ id: "d", kind: "TestChangeRequest" }))).toBe(LANE_VERIFICATION)

    // A display number that looks like another level must not move the card. The projection is the authority.
    const misleading = node({ id: "x", displayNumber: "LLRCR-00061.00", level: "System" })
    expect(laneOf(misleading)).toBe(LANE_SYSTEM)
    expect(badgeOf(misleading)).toBe("SYS")

    // Interface change control is system-level and shares that lane, with its own badge so it stays legible.
    expect(laneOf(node({ id: "i", level: "Interface" }))).toBe(LANE_SYSTEM)
    expect(badgeOf(node({ id: "i", level: "Interface" }))).toBe("IFC")
    expect(groupOf(node({ id: "i", level: "Interface" }))).toBe("sys")
  })

  test("the panel docks away from the links, so it cannot cover one", () => {
    const selected = node({ id: "sys", level: "System" })
    const map = new Map<string, NetworkNode>([
      [selected.id, selected],
      ["hlr", node({ id: "hlr", level: "HighLevel" })],
      ["ver", node({ id: "ver", kind: "TestChangeRequest" })],
      ["pr", node({ id: "pr", kind: "ProblemReport" })],
    ])
    const edge = (fromId: string, toId: string): NetworkEdge => ({
      fromId,
      fromKind: "ChangeRequest",
      toId,
      toKind: "ChangeRequest",
      relation: "Upstream",
      provenance: [],
    })

    // All the work runs downstream — the case that first put the panel on top of a verification change.
    expect(resolveDock(selected, [edge("sys", "hlr"), edge("sys", "ver")], map)).toBe("left")
    // Driven from upstream instead, the emptier side is the right.
    expect(resolveDock(selected, [edge("pr", "sys")], map)).toBe("right")
    // A record with no links at all still gets a deterministic side rather than throwing.
    expect(["left", "right"]).toContain(resolveDock(selected, [], map))
  })

  test("suspect is stated by the server and always carries its word", () => {
    const suspect: NetworkEdge = {
      fromId: "a",
      toId: "b",
      fromKind: "ChangeRequest",
      toKind: "ChangeRequest",
      relation: "Upstream",
      provenance: [{ kind: "AssessmentDerived", status: "Suspect link pending reassessment" }],
    }
    const settled: NetworkEdge = { ...suspect, provenance: [{ kind: "AuthorStated" }] }
    expect(isSuspectEdge(suspect)).toBe(true)
    expect(isSuspectEdge(settled)).toBe(false)

    // The pill is amber, and the text says so too — status is never colour alone.
    expect(pillFor("Suspect").color).toBe("#8a5a00")
    expect(stateLabel("Suspect")).toBe("Suspect")
    expect(stateLabel("InReview")).toBe("In review")
    expect(stateLabel("SelectedForBaseline")).toBe("Selected for baseline")
  })

  test("rows are per lane and stable, so the board does not reshuffle between loads", () => {
    const nodes = [
      node({ id: "2", displayNumber: "SRCR-00040.00", level: "System" }),
      node({ id: "1", displayNumber: "SRCR-00039.00", level: "System" }),
      node({ id: "3", displayNumber: "HLRCR-00127.00", level: "HighLevel" }),
    ]
    const rows = assignRows(nodes)
    expect(rows.get("1")).toBe(0)
    expect(rows.get("2")).toBe(1)
    // Each lane starts its own numbering, rather than continuing a single global sequence.
    expect(rows.get("3")).toBe(0)

    expect(assignRows(nodes.slice().reverse())).toEqual(rows)
  })
})
