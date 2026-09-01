import { expect, test } from "@playwright/test"
import {
  badgeForKind,
  diffFor,
  insideEdges,
  downstreamNotice,
  downstreamState,
  insideLaneLabels,
  levelBelow,
  levelNoun,
  matchesType,
  type ProposalItem,
} from "../src/changeProposalPresentation"

// Lane labels, diffs and the downstream-emptiness rule are pure, so they are asserted directly. The rule that
// earns the most attention here is the last one: an empty downstream lane has three different causes, and
// #880 §8.5.1 records why collapsing them would misstate the record.

const item = (over: Partial<ProposalItem> & { id: string; kind: string }): ProposalItem => ({
  displayNumber: over.id,
  level: "System",
  statement: "The FMS shall sequence oceanic waypoints in round-robin order.",
  allocatedDownstream: [],
  ...over,
})

test("badges read as the operation, not as a state", () => {
  expect(badgeForKind("Introduce")).toBe("NEW")
  expect(badgeForKind("Modify")).toBe("MOD")
  expect(badgeForKind("Retire")).toBe("RET")
})

test("the allocated lane is named from the ladder, not from a fixed level below System", () => {
  // The default ladder puts HighLevel below System.
  expect(levelBelow("System")).toBe("HighLevel")
  // A project that does not configure HighLevel allocates straight to LLRs, and the header must say so.
  expect(levelBelow("System", ["System", "LowLevel"])).toBe("LowLevel")
  // Nothing sits below the bottom of the ladder.
  expect(levelBelow("LowLevel")).toBeNull()
})

test("lane labels follow the level of the change being opened", () => {
  const system = insideLaneLabels("System", false)
  expect(system.proposed).toBe("PROPOSED SYSTEM REQUIREMENTS")
  expect(system.allocated).toBe("ALLOCATED HLRs")
  expect(system.verification).toBe("SYSTEM PROCEDURES")

  const high = insideLaneLabels("HighLevel", false)
  expect(high.proposed).toBe("PROPOSED HLRs")
  expect(high.allocated).toBe("ALLOCATED LLRs")
  expect(high.verification).toBe("HLR CASES AND PROCEDURES")
})

test("a test change reads in verification vocabulary, and what sits below it is what it covers", () => {
  const labels = insideLaneLabels("System", true)
  expect(labels.proposed).toBe("PROPOSED CASES AND PROCEDURES")
  expect(labels.allocated).toBe("REQUIREMENTS COVERED")
  expect(labels.verification).toBe("EXECUTIONS")
})

test("a level the label table does not name still reads sensibly", () => {
  // A project configuring a layer this client has never heard of must not produce an empty or broken header.
  expect(levelNoun("SafetyCase")).toBe("SAFETY CASE REQUIREMENTS")
})

test("the register lane header echoes the active type filter", () => {
  expect(insideLaneLabels("System", false, "all").register).toBe("CHANGE REQUEST")
  expect(insideLaneLabels("System", false, "hlr").register).toBe("CHANGE REQUEST · HLR")
})

test("the type filter groups layers above System under the SYS chip", () => {
  const interfaceChange = { kind: "ChangeRequest", level: "Interface" }
  // A project with the Interface layer must not find those records vanish when the System chip is used.
  expect(matchesType(interfaceChange, "sys")).toBe(true)
  expect(matchesType(interfaceChange, "hlr")).toBe(false)
  expect(matchesType({ kind: "ChangeRequest", level: "HighLevel" }, "hlr")).toBe(true)
  expect(matchesType({ kind: "TestChangeRequest", level: "System" }, "test")).toBe(true)
  expect(matchesType({ kind: "TestChangeRequest", level: "System" }, "sys")).toBe(false)
  expect(matchesType({ kind: "ChangeRequest", level: "System" }, "all")).toBe(true)
})

test("only a Modify carries a diff", () => {
  expect(diffFor(item({ id: "SR-1", kind: "Introduce" }))).toBeNull()
  expect(diffFor(item({ id: "SR-2", kind: "Retire", supersededStatement: "Old text" }))).toBeNull()

  const diff = diffFor(item({ id: "SR-3", kind: "Modify", supersededStatement: "Old text" }))
  expect(diff?.before).toBe("Old text")
  expect(diff?.after).toContain("round-robin")
})

test("a Modify with no resolvable before text shows no diff rather than a diff from nothing", () => {
  // An empty before would render every word of the statement as added, asserting the author rewrote the
  // requirement from scratch. Null is the server's way of saying no before text exists.
  expect(diffFor(item({ id: "SR-4", kind: "Modify", supersededStatement: null }))).toBeNull()
})

test("an empty downstream lane distinguishes its three causes", () => {
  const allocated = item({
    id: "SR-5",
    kind: "Modify",
    baseRevisionId: "rev-1",
    allocatedDownstream: [
      { id: "h1", displayNumber: "HLR-1.00", level: "HighLevel", statement: "s", isProposed: false },
    ],
  })
  expect(downstreamState(allocated)).toBe("allocated")
  expect(downstreamNotice(downstreamState(allocated))).toBeNull()

  // Nothing can allocate to a requirement that is not in the build yet.
  expect(downstreamState(item({ id: "SR-6", kind: "Introduce" }))).toBe("notYetExisting")

  // The case #880 §8.5 missed: the allocation exists, but against a later revision than this proposal names.
  const stale = item({ id: "SR-7", kind: "Modify", baseRevisionId: null })
  expect(downstreamState(stale)).toBe("behindTarget")
  expect(downstreamNotice("behindTarget")).toContain("later revision")

  // A genuine absence, and it must not borrow either of the other two sentences.
  expect(downstreamState(item({ id: "SR-8", kind: "Modify", baseRevisionId: "rev-2" }))).toBe("none")
  expect(downstreamNotice("none")).toBe("Nothing is allocated below this requirement.")
})

test("a change request flagged for rebase reads as behind its target even with a resolved base", () => {
  const flagged = item({ id: "SR-9", kind: "Modify", baseRevisionId: "rev-3" })
  expect(downstreamState(flagged, true)).toBe("behindTarget")
})

test("coverage edges are drawn only where the record says what it covers", () => {
  const items = [
    item({ id: "SR-10", kind: "Modify", allocatedDownstream: [
      { id: "h1", displayNumber: "HLR-1.00", level: "HighLevel", statement: "s", isProposed: false },
      { id: "h2", displayNumber: "HLR-2.00", level: "HighLevel", statement: "s", isProposed: false },
    ] }),
  ]
  // Two allocations and two procedures. Pairing them all would produce four "covered by" edges and claim
  // coverage nothing recorded — a requirement three procedures cover would look the same as one nothing
  // covers. Only the one recorded link may be drawn.
  const covering = [{ id: "tp1", coversIds: ["h1"] }, { id: "tp2" }]

  const edges = insideEdges("SRCR-1", items, covering)
  const coverage = edges.filter(edge => edge.label === "covered by")

  expect(coverage).toHaveLength(1)
  expect(coverage[0]).toMatchObject({ from: "h1", to: "tp1" })
})

test("a retirement and its cascade are marked so they can be drawn dashed", () => {
  const items = [
    item({ id: "SR-11", kind: "Retire", allocatedDownstream: [
      { id: "h3", displayNumber: "HLR-3.00", level: "HighLevel", statement: "s", isProposed: false },
    ] }),
  ]

  const edges = insideEdges("SRCR-1", items)

  expect(edges.every(edge => edge.kind === "retire")).toBe(true)
  expect(edges.map(edge => edge.label)).toEqual(["retire", "retire cascade"])
})

test("the opened change links to every item it proposes", () => {
  const items = [
    item({ id: "SR-12", kind: "Introduce" }),
    item({ id: "SR-13", kind: "Modify" }),
  ]

  const edges = insideEdges("SRCR-9", items)

  expect(edges.filter(edge => edge.from === "SRCR-9")).toHaveLength(2)
  expect(edges.map(edge => edge.label)).toEqual(["introduce", "modify"])
})
