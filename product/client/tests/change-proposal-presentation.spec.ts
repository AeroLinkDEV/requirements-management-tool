import { expect, test } from "@playwright/test"
import { levelBadge } from "../src/changeNetworkPresentation"
import {
  badgeForKind,
  diffFor,
  insideEdges,
  downstreamNotice,
  insideLaneLabels,
  levelBelow,
  levelNoun,
  matchesType,
  type ProposalItem,
  type RequirementCoverageTarget,
  type VerificationProposalItem,
} from "../src/changeProposalPresentation"

// Lane labels, diffs and the downstream-emptiness rule are pure, so they are asserted directly. The rule that
// earns the most attention here is the last one: an empty downstream lane has three different causes, and
// #880 §8.5.1 records why collapsing them would misstate the record.

const item = (over: Partial<ProposalItem> & { id: string; kind: string }): ProposalItem => ({
  displayNumber: over.id,
  level: "System",
  statement: "The FMS shall sequence oceanic waypoints in round-robin order.",
  allocatedDownstream: [],
  disposition: "Allocated",
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

test("the downstream sentence comes from the server disposition, not from a null base", () => {
  // Allocated says nothing, because there is nothing to explain.
  expect(downstreamNotice(item({ id: "SR-5", kind: "Modify", disposition: "Allocated" }))).toBeNull()

  // Nothing can allocate to a requirement that is not in the build yet.
  expect(downstreamNotice(item({ id: "SR-6", kind: "Introduce", disposition: "TargetNotYetCreated" }))).toContain(
    "does not exist in the build",
  )

  // A genuine absence, in its own words.
  expect(downstreamNotice(item({ id: "SR-8", kind: "Modify", disposition: "NoAllocationRecorded" }))).toBe(
    "Nothing is allocated below this requirement.",
  )

  // A data gap is not staleness. The old client read a null baseRevisionId as "behind its target, so the
  // allocation hangs off a later revision" — a traceability claim an unresolvable base does not support.
  const gap = downstreamNotice(
    item({ id: "SR-7", kind: "Modify", disposition: "BaseRevisionUnresolved", baseRevisionId: null }),
  )
  expect(gap).toContain("could not be resolved")
  expect(gap).not.toContain("later revision")
})

test("behind-target states both revisions and claims nothing about where the allocation sits", () => {
  const notice = downstreamNotice(
    item({
      id: "SR-9",
      kind: "Modify",
      disposition: "BehindTarget",
      supersededRevision: 1,
      latestRevision: 2,
      latestRevisionState: "Active",
    }),
  )

  // The fact: targets 01, requirement is now at 02. Not a claim that 02 carries the allocation, because
  // nothing looked there.
  expect(notice).toContain("revision 01")
  expect(notice).toContain("revision 02")
  expect(notice).toContain("active")
  expect(notice).not.toContain("hangs off")
})

test("a stale item cannot contaminate a sibling, because each carries its own disposition", () => {
  // One stale item strands the whole change request. Deciding this from a change-request-wide flag, as the
  // client used to, would mark every empty item in it as behind its target.
  const stale = item({ id: "SR-10", kind: "Modify", disposition: "BehindTarget", supersededRevision: 1, latestRevision: 2 })
  const sibling = item({ id: "SR-11", kind: "Introduce", disposition: "TargetNotYetCreated" })

  expect(downstreamNotice(stale)).toContain("revision 02")
  expect(downstreamNotice(sibling)).toContain("does not exist in the build")
  expect(downstreamNotice(sibling)).not.toContain("revision 02")
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

  const edges = insideEdges("SRCR-1", items, [], covering)
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

test.describe("verification proposals are verification content", () => {
  const vItem = (
    over: Partial<VerificationProposalItem> & { id: string; kind: string },
  ): VerificationProposalItem => ({
    displayNumber: over.id,
    level: "System",
    artifactKind: "Procedure",
    proposedContent: null,
    finalCoverage: [],
    addedCoverage: [],
    removedCoverage: [],
    parentKind: "Allocated",
    exactParents: [],
    referenceGaps: [],
    ...over,
  })

  const covered = (revisionId: string, displayNumber: string): RequirementCoverageTarget => ({
    revisionId,
    artifactId: `art-${revisionId}`,
    displayNumber,
    level: "System",
    statement: "s",
  })

  test("a verification proposal links to the exact revision it covers, by revision identity", () => {
    const items = [
      vItem({
        id: "tp-1",
        kind: "Modify",
        finalCoverage: [covered("rev-a", "SR-1.00"), covered("rev-b", "SR-2.00")],
      }),
    ]

    const edges = insideEdges("TCR-1", [], items)

    // The edge target is the revision id, never the display number and never lane adjacency.
    const covers = edges.filter(edge => edge.label === "covers")
    expect(covers.map(edge => edge.to).sort()).toEqual(["rev-a", "rev-b"])
    expect(covers.every(edge => edge.from === "tp-1")).toBe(true)
    // And the package links to its own proposal.
    expect(edges.some(edge => edge.from === "TCR-1" && edge.to === "tp-1")).toBe(true)
  })

  test("final coverage is what the lane shows, not the added delta", () => {
    // A Modify that retains A and B, drops C and adds D leaves the successor covering A, B and D. A lane fed
    // the added delta alone would show D and tell the reader that A and B had stopped being covered.
    const item = vItem({
      id: "tp-2",
      kind: "Modify",
      finalCoverage: [covered("a", "SR-A.00"), covered("b", "SR-B.00"), covered("d", "SR-D.00")],
      addedCoverage: [covered("d", "SR-D.00")],
      removedCoverage: [covered("c", "SR-C.00")],
    })

    const covers = insideEdges("TCR-1", [], [item]).filter(edge => edge.label === "covers")

    expect(covers.map(edge => edge.to).sort()).toEqual(["a", "b", "d"])
    expect(covers.map(edge => edge.to)).not.toContain("c")
  })

  test("a retired verification proposal draws its cascade dashed and proposes no successor body", () => {
    const item = vItem({
      id: "tp-3",
      kind: "Retire",
      proposedContent: null,
      finalCoverage: [covered("a", "SR-A.00")],
    })

    const edges = insideEdges("TCR-1", [], [item])

    expect(edges.every(edge => edge.kind === "retire")).toBe(true)
    expect(edges.map(edge => edge.label)).toEqual(["retire", "retire cascade"])
    expect(item.proposedContent).toBeNull()
  })
})

test("level badges follow the configured ladder rather than falling back to System", () => {
  // The regression this guards: an inline ternary that returned SYS for anything it did not recognise, so a
  // Customer or Interface record read as System.
  expect(levelBadge("HighLevel")).toBe("HLR")
  expect(levelBadge("LowLevel")).toBe("LLR")
  expect(levelBadge("System")).toBe("SYS")
  expect(levelBadge("Interface")).toBe("IFC")
  expect(levelBadge("Customer")).toBe("CUS")
  // A level this table does not name reads as itself, never as System.
  expect(levelBadge("SafetyCase")).not.toBe("SYS")
})
