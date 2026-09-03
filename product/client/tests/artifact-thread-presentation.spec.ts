import { expect, test } from "@playwright/test"
import { parseArtifactThread, type ArtifactThread } from "../src/artifactThreadContract"
import {
  artifactThreadCanvasModel,
  badgeOf,
  evidenceSize,
  groupOf,
  identityLabel,
  isUnnumbered,
  metaLine,
  shortHash,
  suspectEndpoints,
  suspectRelations,
} from "../src/artifactThreadPresentation"

/**
 * Presentation for the artifact thread.
 *
 * Every fixture below is fed through `parseArtifactThread` rather than hand-built as a typed object. A typed
 * literal would let these tests assert against a thread the contract might never admit, which would make the
 * whole seam decorative. What they mostly prove is that presentation stays a reading of served facts: it does
 * not name a level the response withheld, invent an identifier an execution does not have, decide suspectness,
 * or reorder lanes the server placed.
 */

const PROJECT = "5f6e1b0a-1c2d-4e3f-8a9b-0c1d2e3f4a5b"
const BASELINE = "a1b2c3d4-e5f6-4708-9a0b-1c2d3e4f5061"
const BUILD = "b2c3d4e5-f607-4819-a0b1-2c3d4e5f6172"
const PR = "77777777-7777-4777-8777-777777777777"
const CR = "55555555-5555-4555-8555-555555555555"
const TCR = "66666666-6666-4666-8666-666666666666"
const REQ = "11111111-1111-4111-8111-111111111111"
const CASE = "22222222-2222-4222-8222-222222222222"
const PROC = "33333333-3333-4333-8333-333333333333"
const EXE = "44444444-4444-4444-8444-444444444444"
const EVIDENCE = "88888888-8888-4888-8888-888888888888"
const HASH = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"

type Raw = Record<string, unknown>

const node = (over: Raw): Raw => ({ title: null, state: null, level: null, isFocal: false, ...over })

const edge = (fromId: string, fromKind: string, toId: string, toKind: string, relation: string,
  isSuspect = false): Raw => ({ fromId, fromKind, toId, toKind, relation, isSuspect })

/** A thread carrying every supported kind, so lane placement and badges are exercised together. */
const fullResponse = (over: Raw = {}): Raw => ({
  projectId: PROJECT,
  baselineId: BASELINE,
  buildId: BUILD,
  focalKind: "Requirement",
  focalId: REQ,
  verification: { isApplicable: true, reason: null },
  nodes: [
    node({ id: PR, kind: "ProblemReport", lane: 0, displayNumber: "PR-00003.00", title: "Discontinuity", state: "Open" }),
    node({ id: CR, kind: "ChangeRequest", lane: 1, displayNumber: "SRCR-00039.00", title: "Routing", state: "Approved" }),
    node({ id: TCR, kind: "TestChangeRequest", lane: 1, displayNumber: "HLRTPCR-000009.00", title: "Regression", state: "InReview" }),
    node({ id: REQ, kind: "Requirement", lane: 2, displayNumber: "HLR-000075.02", title: "Advance on trigger", state: "Effective", level: "HighLevel", isFocal: true, revision: 2 }),
    node({ id: CASE, kind: "Case", lane: 3, displayNumber: "HLRTC-000118.00", title: "Advance case", state: "Approved", level: "HighLevel", revision: 0 }),
    node({ id: PROC, kind: "Procedure", lane: 4, displayNumber: "HLRTP-000120.00", title: "Pre-release procedure", state: "Approved", level: "HighLevel", revision: 0 }),
    node({
      id: EXE, kind: "Execution", lane: 5, displayNumber: null, title: "test.engineer", state: "Pass",
      outcome: "Pass", executedBy: "test.engineer", executedAt: "2026-08-14T09:00:00+00:00",
      recordedAt: "2026-08-14T09:05:00+00:00",
      evidence: [{ id: EVIDENCE, fileName: "run.json", contentType: "application/json", size: 20480, sha256: HASH, uploadedBy: "test.engineer", uploadedAt: "2026-08-14T09:06:00+00:00" }],
    }),
    node({ id: BUILD, kind: "Build", lane: 5, displayNumber: "FMS-1.5.0", title: "Released baseline", state: "Released" }),
  ],
  edges: [
    edge(PR, "ProblemReport", CR, "ChangeRequest", "resolved by"),
    edge(CR, "ChangeRequest", REQ, "Requirement", "authored"),
    edge(REQ, "Requirement", PROC, "Procedure", "verified by", true),
    edge(CASE, "Case", PROC, "Procedure", "run by"),
    edge(TCR, "TestChangeRequest", PROC, "Procedure", "authored"),
    edge(PROC, "Procedure", EXE, "Execution", "produced"),
    edge(EXE, "Execution", BUILD, "Build", "evidence for"),
  ],
  ...over,
})

/** Parses, or fails the test with the contract's own reason rather than a bare undefined later on. */
const read = (raw: Raw): ArtifactThread => {
  const parsed = parseArtifactThread(raw)
  if (!parsed.ok) throw new Error(`fixture rejected by the contract: ${parsed.reason}`)
  return parsed.thread
}

const find = (thread: ArtifactThread, id: string) => thread.nodes.find(candidate => candidate.id === id)!

test.describe("card identity", () => {
  test("each kind carries its own badge, and a requirement's comes from its served level", () => {
    const thread = read(fullResponse())

    expect(badgeOf(find(thread, PR))).toBe("PR")
    expect(badgeOf(find(thread, CASE))).toBe("TC")
    expect(badgeOf(find(thread, PROC))).toBe("TP")
    expect(badgeOf(find(thread, EXE))).toBe("EX")
    expect(badgeOf(find(thread, BUILD))).toBe("BLD")
    expect(badgeOf(find(thread, REQ))).toBe("HLR")
  })

  test("a change request is not badged with a level the response never stated", () => {
    const thread = read(fullResponse())

    // The projection states `level: null` for both change-request kinds. The prototype illustrates them as
    // SYS / HLR / LLR, but that badge could only come from the identifier prefix — the one derivation #880
    // forbids outright. `SRCR-` must not become `SYS`.
    expect(find(thread, CR).level).toBeNull()
    expect(badgeOf(find(thread, CR))).toBe("CR")
    expect(badgeOf(find(thread, TCR))).toBe("TCR")
  })

  test("a change request and a test change request stay distinct kinds in one lane", () => {
    const thread = read(fullResponse())

    expect(find(thread, CR).kind).toBe("ChangeRequest")
    expect(find(thread, TCR).kind).toBe("TestChangeRequest")
    expect(find(thread, CR).lane).toBe(find(thread, TCR).lane)
    expect(badgeOf(find(thread, CR))).not.toBe(badgeOf(find(thread, TCR)))
  })

  test("exact revision identities are shown as served, never rounded to an artifact", () => {
    const thread = read(fullResponse())

    expect(find(thread, REQ).displayNumber).toBe("HLR-000075.02")
    expect(identityLabel(find(thread, REQ))).toBe("HLR-000075.02")
    expect(metaLine(find(thread, REQ))).toBe("Revision 02")
  })

  test("an execution with no display number never gains a synthetic one", () => {
    const thread = read(fullResponse())
    const execution = find(thread, EXE)

    expect(execution.displayNumber).toBeNull()
    expect(isUnnumbered(execution)).toBe(true)

    const label = identityLabel(execution)
    // Not EXE-1234, not a GUID fragment, not a certification number this record does not have.
    expect(label).toBe("Pass run")
    expect(label).not.toContain(EXE.slice(0, 8))
    expect(label).not.toMatch(/EXE-|\d{4}/)

    // What it is identified by instead: the facts the run actually recorded.
    expect(metaLine(execution)).toContain("test.engineer")
    expect(metaLine(execution)).toContain("1 evidence file")
  })

  test("a build keeps the server's own build identity", () => {
    expect(identityLabel(find(read(fullResponse()), BUILD))).toBe("FMS-1.5.0")
  })
})

test.describe("evidence", () => {
  test("the immutable facts survive into presentation, hash included", () => {
    const [file] = find(read(fullResponse()), EXE).evidence

    expect(file.id).toBe(EVIDENCE)
    expect(file.fileName).toBe("run.json")
    expect(file.contentType).toBe("application/json")
    expect(file.size).toBe(20480)
    expect(file.sha256).toBe(HASH)
    expect(file.uploadedBy).toBe("test.engineer")
    expect(file.uploadedAt).toBe("2026-08-14T09:06:00+00:00")
  })

  test("the abbreviation is a prefix of the real hash, never a rewriting of it", () => {
    expect(HASH.startsWith(shortHash(HASH))).toBe(true)
    expect(shortHash(HASH)).toHaveLength(12)
  })

  test("a size reads at a glance without claiming precision the byte count lacks", () => {
    expect(evidenceSize(900)).toBe("900 B")
    expect(evidenceSize(20480)).toBe("20 KB")
    expect(evidenceSize(5 * 1024 * 1024)).toBe("5.0 MB")
  })
})

test.describe("suspectness is read, never decided", () => {
  test("only the server-stated edge produces suspect endpoints", () => {
    const thread = read(fullResponse())
    const endpoints = suspectEndpoints(thread.edges)

    // The suspect edge is REQ -> PROC. Both of its endpoints, and nothing else.
    expect([...endpoints].sort()).toEqual([REQ, PROC].sort())
    expect(endpoints.has(CASE)).toBe(false)
    expect(endpoints.has(EXE)).toBe(false)
  })

  test("wording that mentions suspect does not make an edge suspect", () => {
    const thread = read(fullResponse({
      edges: [
        // Says the word, states false. An earlier defect class searched relation text for it.
        edge(CR, "ChangeRequest", REQ, "Requirement", "suspect applicability review", false),
        edge(REQ, "Requirement", PROC, "Procedure", "verified by", true),
      ],
    }))

    const endpoints = suspectEndpoints(thread.edges)
    expect(endpoints.has(CR)).toBe(false)
    expect(suspectRelations(CR, thread.edges)).toHaveLength(0)
    expect(suspectRelations(PROC, thread.edges)).toHaveLength(1)
  })

  test("the relation text is carried through exactly as the server stated it", () => {
    const thread = read(fullResponse())
    const relations = thread.edges.map(item => item.relation)

    expect(relations).toContain("verified by")
    expect(relations).toContain("run by")
    expect(relations).toContain("produced")
    expect(relations).toContain("evidence for")
    expect(relations).toContain("resolved by")
    // The canvas model hands the relation to the edge label without rewriting it.
    const model = artifactThreadCanvasModel(thread)
    expect(model.edges.find(item => item.to === PROC && item.from === REQ)?.label).toBe("verified by")
  })
})

test.describe("the six-lane model", () => {
  test("a fully populated thread occupies all six lanes in prototype order", () => {
    const model = artifactThreadCanvasModel(read(fullResponse()))

    expect(model.lanes).toEqual([
      "PROBLEM REPORT",
      "CHANGE REQUEST",
      "REQUIREMENT",
      "TEST CASE",
      "PROCEDURE",
      "RESULT · BUILD",
    ])
  })

  test("a result and a build share the final lane rather than taking one each", () => {
    const thread = read(fullResponse())
    const model = artifactThreadCanvasModel(thread)

    const execution = model.nodes.find(item => item.id === EXE)!
    const build = model.nodes.find(item => item.id === BUILD)!
    expect(execution.lane).toBe(build.lane)
    expect(execution.lane).toBe(model.lanes.length - 1)
    expect(model.lanes[execution.lane]).toBe("RESULT · BUILD")
    // Distinct rows: sharing a lane is not sharing a position.
    expect(execution.row).not.toBe(build.row)
  })

  test("the execution sits above the build, as the prototype orders that lane", () => {
    const model = artifactThreadCanvasModel(read(fullResponse()))
    const execution = model.nodes.find(item => item.id === EXE)!
    const build = model.nodes.find(item => item.id === BUILD)!

    expect(execution.row).toBeLessThan(build.row)
  })

  test("the final lane carries an edge whose endpoints share it", () => {
    const model = artifactThreadCanvasModel(read(fullResponse()))
    const lanes = new Map(model.nodes.map(item => [item.id, item.lane]))
    const intra = model.edges.find(item => lanes.get(item.from) === lanes.get(item.to))

    expect(intra?.label).toBe("evidence for")
  })

  test("a System chain bypasses Test Case, and the lane is dropped rather than shown empty", () => {
    const model = artifactThreadCanvasModel(read(fullResponse({
      focalId: REQ,
      nodes: [
        node({ id: PR, kind: "ProblemReport", lane: 0, displayNumber: "PR-00003.00", state: "Open" }),
        node({ id: CR, kind: "ChangeRequest", lane: 1, displayNumber: "SRCR-00039.00", state: "Approved" }),
        node({ id: REQ, kind: "Requirement", lane: 2, displayNumber: "SYSR-000100.01", state: "Effective", level: "System", isFocal: true, revision: 1 }),
        node({ id: PROC, kind: "Procedure", lane: 4, displayNumber: "SYSTP-000040.00", state: "Approved", level: "System", revision: 0 }),
        node({ id: BUILD, kind: "Build", lane: 5, displayNumber: "FMS-1.5.0", state: "Released" }),
      ],
      edges: [edge(REQ, "Requirement", PROC, "Procedure", "verified by")],
    })))

    expect(model.lanes).toEqual([
      "PROBLEM REPORT",
      "CHANGE REQUEST",
      "REQUIREMENT",
      "PROCEDURE",
      "RESULT · BUILD",
    ])
    expect(model.lanes).not.toContain("TEST CASE")
    // The remaining lanes close the gap: the procedure takes the index the case vacated.
    expect(model.nodes.find(item => item.id === PROC)!.lane).toBe(3)
    expect(model.nodes.find(item => item.id === BUILD)!.lane).toBe(4)
  })

  test("an artifact with no relationships is a one-node thread, not an empty board", () => {
    const model = artifactThreadCanvasModel(read(fullResponse({
      nodes: [node({ id: REQ, kind: "Requirement", lane: 2, displayNumber: "SYSR-000100.01", state: "Effective", level: "System", isFocal: true, revision: 1 })],
      edges: [],
    })))

    expect(model.nodes).toHaveLength(1)
    expect(model.lanes).toEqual(["REQUIREMENT"])
    expect(model.nodes[0].lane).toBe(0)
  })

  test("lane membership is the server's, never recomputed from the kind", () => {
    const thread = read(fullResponse())
    const model = artifactThreadCanvasModel(thread)
    const laneOf = new Map(model.nodes.map(item => [item.id, item.lane]))

    // Compaction may renumber a lane, but never move a record into a different lane's company.
    expect(laneOf.get(CR)).toBe(laneOf.get(TCR))
    expect(laneOf.get(EXE)).toBe(laneOf.get(BUILD))
    expect(laneOf.get(REQ)).not.toBe(laneOf.get(CASE))
  })

  test("rows are stable when the server returns the same thread in a different order", () => {
    const forward = fullResponse()
    const reversed = fullResponse({ nodes: [...(forward.nodes as Raw[])].reverse() })

    const a = artifactThreadCanvasModel(read(forward))
    const b = artifactThreadCanvasModel(read(reversed))

    const rows = (model: typeof a) =>
      [...model.rows.entries()].sort(([left], [right]) => left.localeCompare(right))
    expect(rows(b)).toEqual(rows(a))
  })
})

test.describe("filter groups", () => {
  test("families group as the prototype's thread does", () => {
    const thread = read(fullResponse())

    expect(groupOf(find(thread, REQ))).toBe("hlr")
    expect(groupOf(find(thread, CASE))).toBe("ver")
    expect(groupOf(find(thread, PROC))).toBe("ver")
    expect(groupOf(find(thread, EXE))).toBe("ver")
    expect(groupOf(find(thread, TCR))).toBe("ver")
    expect(groupOf(find(thread, BUILD))).toBe("sys")
    expect(groupOf(find(thread, PR))).toBe("pr")
  })

  test("a layer above System answers the System chip rather than vanishing", () => {
    const thread = read(fullResponse({
      nodes: [node({ id: REQ, kind: "Requirement", lane: 2, displayNumber: "CUS-000004.00", state: "Effective", level: "Customer", isFocal: true, revision: 0 })],
      edges: [],
    }))

    expect(groupOf(find(thread, REQ))).toBe("sys")
  })
})

test.describe("verification applicability", () => {
  test("a level with no discipline is stated, with no verification records invented", () => {
    const thread = read(fullResponse({
      focalId: REQ,
      verification: {
        isApplicable: false,
        reason: "The Customer level has no verification discipline, so this thread has no test case, procedure or result.",
      },
      nodes: [
        node({ id: CR, kind: "ChangeRequest", lane: 1, displayNumber: "SRCR-00039.00", state: "Approved" }),
        node({ id: REQ, kind: "Requirement", lane: 2, displayNumber: "CUS-000004.00", title: "Sequence the filed route.", state: "Effective", level: "Customer", isFocal: true, revision: 0 }),
      ],
      edges: [edge(CR, "ChangeRequest", REQ, "Requirement", "authored")],
    }))

    expect(thread.verification.isApplicable).toBe(false)
    expect(thread.verification.reason).toContain("no verification discipline")

    // The requirement truth is preserved whole, and no Case, Procedure or Execution is fabricated to fill in.
    expect(find(thread, REQ).displayNumber).toBe("CUS-000004.00")
    expect(thread.nodes.some(item => item.kind === "Case")).toBe(false)
    expect(thread.nodes.some(item => item.kind === "Procedure")).toBe(false)
    expect(thread.nodes.some(item => item.kind === "Execution")).toBe(false)

    // And no empty structural lane is manufactured to explain the absence.
    const model = artifactThreadCanvasModel(thread)
    expect(model.lanes).toEqual(["CHANGE REQUEST", "REQUIREMENT"])
  })
})

test.describe("within-lane ordering is fully deterministic", () => {
  /** Two runs of one procedure recorded at the same instant: equal kind, equal sort key, distinct identity. */
  const tiedRuns = (order: "forward" | "reverse") => {
    const runs = [
      node({
        id: "aaaaaaaa-0000-4000-8000-000000000001", kind: "Execution", lane: 5, displayNumber: null,
        title: "first.runner", state: "Pass", outcome: "Pass", executedBy: "first.runner",
        executedAt: "2026-08-14T09:00:00+00:00", recordedAt: "2026-08-14T09:00:00+00:00", evidence: [],
      }),
      node({
        id: "bbbbbbbb-0000-4000-8000-000000000002", kind: "Execution", lane: 5, displayNumber: null,
        title: "second.runner", state: "Fail", outcome: "Fail", executedBy: "second.runner",
        executedAt: "2026-08-14T09:00:00+00:00", recordedAt: "2026-08-14T09:00:00+00:00", evidence: [],
      }),
    ]
    return fullResponse({
      focalKind: "Procedure",
      focalId: PROC,
      nodes: [
        node({ id: PROC, kind: "Procedure", lane: 4, displayNumber: "HLRTP-000120.00", state: "Approved", level: "HighLevel", isFocal: true, revision: 0 }),
        ...(order === "forward" ? runs : [...runs].reverse()),
      ],
      edges: [
        edge(PROC, "Procedure", "aaaaaaaa-0000-4000-8000-000000000001", "Execution", "produced"),
        edge(PROC, "Procedure", "bbbbbbbb-0000-4000-8000-000000000002", "Execution", "produced"),
      ],
    })
  }

  test("equal sort keys still land in the same rows whichever order the server sent them", () => {
    // `executedAt` is not unique — two runs written in one transaction, or brought in by an import, share it.
    // Without an identity tie-breaker the comparator returns 0 and a stable sort leaves them in the order the
    // server's dictionary happened to enumerate, which is the non-determinism the ordering exists to remove.
    const forward = artifactThreadCanvasModel(read(tiedRuns("forward")))
    const reverse = artifactThreadCanvasModel(read(tiedRuns("reverse")))

    const rows = (model: typeof forward) =>
      [...model.rows.entries()].sort(([left], [right]) => left.localeCompare(right))

    expect(rows(reverse)).toEqual(rows(forward))
    // And the order is the exact-identity one, not whichever arrived first.
    expect(forward.rows.get("aaaaaaaa-0000-4000-8000-000000000001")).toBe(0)
    expect(forward.rows.get("bbbbbbbb-0000-4000-8000-000000000002")).toBe(1)
  })

  test("the two runs keep distinct rows rather than collapsing", () => {
    const model = artifactThreadCanvasModel(read(tiedRuns("forward")))
    const first = model.nodes.find(item => item.id === "aaaaaaaa-0000-4000-8000-000000000001")!
    const second = model.nodes.find(item => item.id === "bbbbbbbb-0000-4000-8000-000000000002")!

    expect(first.lane).toBe(second.lane)
    expect(first.row).not.toBe(second.row)
  })
})
