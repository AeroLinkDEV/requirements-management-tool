import { expect, test } from "@playwright/test"
import {
  laneModel,
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
import { compactLanes } from "../src/digitalThreadGeometry"

// Lane, badge and dock are pure, so they are asserted directly. The dock rule in particular is the behaviour
// the design review pushed hardest on: a detail panel must never come to rest on a linked record.

const node = (over: Partial<NetworkNode> & { id: string }): NetworkNode => ({
  kind: "ChangeRequest",
  displayNumber: over.id,
  ...over,
})

test.describe("change network presentation", () => {
  test("lane and badge come from the server-stated kind and level, never the identifier", () => {
    const m = laneModel(["System", "HighLevel", "LowLevel"])
    expect(laneOf(node({ id: "PR-1", kind: "ProblemReport" }), m)).toBe(m.problemLane)
    expect(laneOf(node({ id: "d", kind: "TestChangeRequest" }), m)).toBe(m.verificationLane)
    // The ladder runs left to right in the order the project configured.
    expect(laneOf(node({ id: "a", level: "System" }), m)).toBeLessThan(laneOf(node({ id: "b", level: "HighLevel" }), m))
    expect(laneOf(node({ id: "b", level: "HighLevel" }), m)).toBeLessThan(laneOf(node({ id: "c", level: "LowLevel" }), m))

    // A display number that looks like another level must not move the card. The projection is the authority.
    const misleading = node({ id: "x", displayNumber: "LLRCR-00061.00", level: "System" })
    expect(laneOf(misleading, m)).toBe(m.laneForLevel.get("System"))
    expect(badgeOf(misleading)).toBe("SYS")

    // Interface / ICD is a ladder layer above System, so it gets its own lane to the left of it.
    const iface = node({ id: "i", level: "Interface" })
    const withIface = laneModel(["Interface", "System", "HighLevel"])
    expect(laneOf(iface, withIface)).toBeLessThan(laneOf(node({ id: "s", level: "System" }), withIface))
    expect(badgeOf(iface)).toBe("IFC")
    // It still answers the System chip: that chip means system-level change control.
    expect(groupOf(iface)).toBe("sys")
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

test.describe("configurable ladder layers", () => {
  test("the ladder order comes from the projection, not from the client", () => {
    // Customer and Interface are both layers a project can configure above System, and their relative order
    // is the project's to state. The client renders whatever the ladder policy sends.
    const model = laneModel(["Customer", "Interface", "System", "HighLevel", "LowLevel"])
    expect(model.labels[0]).toBe("PROBLEM REPORT")
    expect(model.labels[1]).toBe("CUSTOMER CHANGE")
    expect(model.labels[2]).toBe("INTERFACE / ICD CHANGE")
    expect(model.labels[model.labels.length - 1]).toBe("VERIFICATION CHANGE")

    // A project that orders them the other way gets that, with no client change.
    const swapped = laneModel(["Interface", "Customer", "System"])
    expect(swapped.labels[1]).toBe("INTERFACE / ICD CHANGE")
    expect(swapped.labels[2]).toBe("CUSTOMER CHANGE")

    // A level this client has never heard of still gets a lane and a readable label.
    const future = laneModel(["System", "SubSystem"])
    expect(future.labels).toContain("SUB SYSTEM CHANGE")
  })

  test("a project without a layer never shows its lane", () => {
    // FMS configures no Customer or Interface layer, so the projection returns no records at those levels and
    // compaction removes the lanes. Naming them in the vocabulary costs an unconfigured project nothing.
    const model = laneModel(["Customer", "Interface", "System", "HighLevel", "LowLevel"])
    const nodes = [
      node({ id: "pr", kind: "ProblemReport" }),
      node({ id: "sys", level: "System" }),
      node({ id: "hlr", level: "HighLevel" }),
    ]
    const placed = nodes.map(item => ({ id: item.id, lane: laneOf(item, model), row: 0 }))
    const compacted = compactLanes(model.labels, placed)
    expect(compacted.lanes).toEqual(["PROBLEM REPORT", "SYSTEM CHANGE", "SOFTWARE HLR CHANGE"])
    expect(compacted.lanes).not.toContain("CUSTOMER CHANGE")
    expect(compacted.lanes).not.toContain("INTERFACE / ICD CHANGE")
  })

  test("a project that configures a higher layer gets its lane, above System", () => {
    const model = laneModel(["Customer", "Interface", "System", "HighLevel"])
    const nodes = [
      node({ id: "cus", level: "Customer" }),
      node({ id: "ifc", level: "Interface" }),
      node({ id: "sys", level: "System" }),
    ]
    const placed = nodes.map(item => ({ id: item.id, lane: laneOf(item, model), row: 0 }))
    const compacted = compactLanes(model.labels, placed)
    expect(compacted.lanes).toEqual(["CUSTOMER CHANGE", "INTERFACE / ICD CHANGE", "SYSTEM CHANGE"])

    // Higher in the ladder means further left: these layers derive down into System.
    const laneById = new Map(compacted.nodes.map(item => [item.id, item.lane]))
    expect(laneById.get("cus")).toBeLessThan(laneById.get("ifc") as number)
    expect(laneById.get("ifc")).toBeLessThan(laneById.get("sys") as number)

    // All of them still answer the System chip, so filtering never hides a configured higher layer.
    expect(groupOf(node({ id: "cus", level: "Customer" }))).toBe("sys")
    expect(groupOf(node({ id: "ifc", level: "Interface" }))).toBe("sys")
  })
})
