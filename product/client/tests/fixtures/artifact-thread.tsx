/**
 * Test-only mount for `DigitalThreadArtifact`.
 *
 * The component is not on any route until slice 6, so rendered behaviour would otherwise be unverifiable — and
 * a pure-logic spec cannot prove a card carries a class, an edge is drawn between two lane-5 records, a hop
 * badge appears, or a malformed response leaves an empty board rather than a partial one.
 *
 * Every scenario is a **raw server response**, not a pre-built typed object, so each assertion runs through the
 * slice-5B0 contract seam exactly as production will. A fixture that handed the component an already-typed
 * thread would prove the view works on data the contract may never actually admit.
 *
 * The scenario is chosen by `?case=` so one fixture serves every rendered assertion.
 */
import { createRoot } from "react-dom/client"
// The product stylesheet, because the card typography is written against its tokens. Without it every
// `font: var(--weight-strong) 11.5px …` shorthand is invalid at computed-value time and silently falls back to
// the 16px default — so a fixture that omitted it would measure type the product never renders.
import "../../src/index.css"
import DigitalThreadArtifact from "../../src/DigitalThreadArtifact"

const PROJECT = "5f6e1b0a-1c2d-4e3f-8a9b-0c1d2e3f4a5b"
const BASELINE = "a1b2c3d4-e5f6-4708-9a0b-1c2d3e4f5061"
const BUILD = "b2c3d4e5-f607-4819-a0b1-2c3d4e5f6172"

const PR = "77777777-7777-4777-8777-777777777777"
const CR = "55555555-5555-4555-8555-555555555555"
const TCR = "66666666-6666-4666-8666-666666666666"
const SYS_REQ = "11111111-1111-4111-8111-111111111111"
const HLR_REQ = "1a1a1a1a-1a1a-4a1a-8a1a-1a1a1a1a1a1a"
const CASE = "22222222-2222-4222-8222-222222222222"
const PROCEDURE = "33333333-3333-4333-8333-333333333333"
const EXECUTION = "44444444-4444-4444-8444-444444444444"
const RETEST = "4b4b4b4b-4b4b-4b4b-8b4b-4b4b4b4b4b4b"
const EVIDENCE = "88888888-8888-4888-8888-888888888888"

/** A real SHA-256 shape. The hash is the fact the evidence record exists to carry, so it is asserted whole. */
const HASH = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
const HASH_2 = "2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae"

type Node = Record<string, unknown>
type Edge = Record<string, unknown>

const node = (over: Node): Node => ({ title: null, state: null, level: null, isFocal: false, ...over })

const edge = (fromId: string, fromKind: string, toId: string, toKind: string, relation: string,
  isSuspect = false): Edge => ({ fromId, fromKind, toId, toKind, relation, isSuspect })

const problemReport = node({
  id: PR, kind: "ProblemReport", lane: 0, displayNumber: "PR-00003.00",
  title: "Route discontinuity remains after waypoint deletion", state: "Open",
})

const changeRequest = node({
  id: CR, kind: "ChangeRequest", lane: 1, displayNumber: "SRCR-00039.00",
  title: "Oceanic round-robin routing", state: "Approved",
})

const testChangeRequest = node({
  id: TCR, kind: "TestChangeRequest", lane: 1, displayNumber: "HLRTPCR-000009.00",
  title: "Advisory regression procedure", state: "InReview",
})

const systemRequirement = (isFocal: boolean) => node({
  id: SYS_REQ, kind: "Requirement", lane: 2, displayNumber: "SYSR-000100.01",
  title: "Sequence oceanic waypoints in round-robin order.", state: "Effective", level: "System",
  isFocal, artifactId: "99999999-9999-4999-8999-999999999999", revision: 1,
})

const highLevelRequirement = (isFocal: boolean) => node({
  id: HLR_REQ, kind: "Requirement", lane: 2, displayNumber: "HLR-000075.02",
  title: "Advance to next eligible waypoint on trigger.", state: "Effective", level: "HighLevel",
  isFocal, artifactId: "9a9a9a9a-9a9a-4a9a-8a9a-9a9a9a9a9a9a", revision: 2,
})

const testCase = (isFocal: boolean) => node({
  id: CASE, kind: "Case", lane: 3, displayNumber: "HLRTC-000118.00",
  title: "Round-robin advance — functional case", state: "Approved", level: "HighLevel",
  isFocal, artifactId: "9b9b9b9b-9b9b-4b9b-8b9b-9b9b9b9b9b9b", revision: 0,
})

const procedure = (isFocal: boolean) => node({
  id: PROCEDURE, kind: "Procedure", lane: 4, displayNumber: "HLRTP-000120.00",
  title: "Mandatory Build 1.6 pre-release procedure", state: "Approved", level: "HighLevel",
  isFocal, artifactId: "9c9c9c9c-9c9c-4c9c-8c9c-9c9c9c9c9c9c", revision: 0,
})

const evidenceFile = {
  id: EVIDENCE, fileName: "round-robin-run.json", contentType: "application/json", size: 20480,
  sha256: HASH, uploadedBy: "test.engineer", uploadedAt: "2026-08-14T09:06:00+00:00",
}

const execution = (isFocal: boolean) => node({
  id: EXECUTION, kind: "Execution", lane: 5, displayNumber: null, title: "test.engineer", state: "Pass",
  isFocal, outcome: "Pass", executedBy: "test.engineer", executedAt: "2026-08-14T09:00:00+00:00",
  recordedAt: "2026-08-14T09:05:00+00:00", evidence: [evidenceFile],
})

const build = (isFocal: boolean) => node({
  id: BUILD, kind: "Build", lane: 5, displayNumber: "FMS-1.5.0", title: "Released baseline",
  state: "Released", isFocal,
})

const thread = (over: Record<string, unknown>) => ({
  projectId: PROJECT,
  baselineId: BASELINE,
  buildId: BUILD,
  verification: { isApplicable: true, reason: null },
  ...over,
})

/** The full six-lane thread, rooted on an HLR requirement, carrying one server-stated suspect coverage link. */
const highLevelThread = (focal: string, focalKind: string) => thread({
  focalKind,
  focalId: focal,
  nodes: [
    problemReport,
    changeRequest,
    testChangeRequest,
    systemRequirement(false),
    highLevelRequirement(focal === HLR_REQ),
    testCase(focal === CASE),
    procedure(focal === PROCEDURE),
    execution(focal === EXECUTION),
    build(focal === BUILD),
  ],
  edges: [
    edge(PR, "ProblemReport", CR, "ChangeRequest", "resolved by"),
    edge(CR, "ChangeRequest", HLR_REQ, "Requirement", "authored"),
    edge(SYS_REQ, "Requirement", HLR_REQ, "Requirement", "allocated from"),
    // Server-stated suspect. The artifact thread is the first view whose vocabulary can carry one (#880 §10.2).
    edge(HLR_REQ, "Requirement", PROCEDURE, "Procedure", "verified by", true),
    edge(CASE, "Case", PROCEDURE, "Procedure", "run by"),
    edge(TCR, "TestChangeRequest", PROCEDURE, "Procedure", "authored"),
    edge(PROCEDURE, "Procedure", EXECUTION, "Execution", "produced"),
    // Both endpoints sit in lane 5: the intra-lane edge the RESULT · BUILD lane requires.
    edge(EXECUTION, "Execution", BUILD, "Build", "evidence for"),
  ],
})

/** A System chain, which the verification architecture takes straight to a procedure with no Test Case. */
const systemThread = () => thread({
  focalKind: "Requirement",
  focalId: SYS_REQ,
  nodes: [
    problemReport,
    changeRequest,
    systemRequirement(true),
    node({
      id: PROCEDURE, kind: "Procedure", lane: 4, displayNumber: "SYSTP-000040.00",
      title: "Oceanic sequencing — system test procedure", state: "Approved", level: "System",
      artifactId: "9c9c9c9c-9c9c-4c9c-8c9c-9c9c9c9c9c9c", revision: 0,
    }),
    execution(false),
    build(false),
  ],
  edges: [
    edge(PR, "ProblemReport", CR, "ChangeRequest", "resolved by"),
    edge(CR, "ChangeRequest", SYS_REQ, "Requirement", "authored"),
    edge(SYS_REQ, "Requirement", PROCEDURE, "Procedure", "verified by"),
    edge(PROCEDURE, "Procedure", EXECUTION, "Execution", "produced"),
    edge(EXECUTION, "Execution", BUILD, "Build", "evidence for"),
  ],
})

/** Two runs in lane 5, the later one a recorded retest of the earlier: a second intra-lane relationship. */
const executionThread = () => thread({
  focalKind: "Execution",
  focalId: RETEST,
  nodes: [
    procedure(false),
    node({
      id: EXECUTION, kind: "Execution", lane: 5, displayNumber: null, title: "first.runner", state: "Fail",
      outcome: "Fail", executedBy: "first.runner", executedAt: "2026-08-10T09:00:00+00:00",
      recordedAt: "2026-08-10T09:05:00+00:00", evidence: [],
    }),
    node({
      id: RETEST, kind: "Execution", lane: 5, displayNumber: null, title: "test.engineer", state: "Pass",
      isFocal: true, outcome: "Pass", executedBy: "test.engineer",
      executedAt: "2026-08-14T09:00:00+00:00", recordedAt: "2026-08-14T09:05:00+00:00",
      evidence: [
        evidenceFile,
        {
          id: "8b8b8b8b-8b8b-4b8b-8b8b-8b8b8b8b8b8b", fileName: "console.log", contentType: "text/plain",
          size: 900, sha256: HASH_2, uploadedBy: "test.engineer", uploadedAt: "2026-08-14T09:07:00+00:00",
        },
      ],
    }),
    build(false),
  ],
  edges: [
    edge(PROCEDURE, "Procedure", EXECUTION, "Execution", "produced"),
    edge(PROCEDURE, "Procedure", RETEST, "Execution", "produced"),
    edge(RETEST, "Execution", EXECUTION, "Execution", "retest of"),
    edge(RETEST, "Execution", BUILD, "Build", "evidence for"),
  ],
})

/** A Customer requirement: a level the domain refuses to name a verification discipline for. */
const noVerificationThread = () => thread({
  focalKind: "Requirement",
  focalId: SYS_REQ,
  verification: {
    isApplicable: false,
    reason: "The Customer level has no verification discipline, so this thread has no test case, procedure or result.",
  },
  nodes: [
    problemReport,
    changeRequest,
    node({
      id: SYS_REQ, kind: "Requirement", lane: 2, displayNumber: "CUS-000004.00",
      title: "The aircraft shall sequence its filed oceanic route.", state: "Effective", level: "Customer",
      isFocal: true, artifactId: "99999999-9999-4999-8999-999999999999", revision: 0,
    }),
    build(false),
  ],
  edges: [
    edge(PR, "ProblemReport", CR, "ChangeRequest", "resolved by"),
    edge(CR, "ChangeRequest", SYS_REQ, "Requirement", "authored"),
  ],
})

/** An artifact with nothing recorded against it. A legitimate one-node thread, not an error. */
const solitaryThread = () => thread({
  focalKind: "Requirement",
  focalId: SYS_REQ,
  nodes: [systemRequirement(true)],
  edges: [],
})

/**
 * A response the contract refuses.
 *
 * The kind is not in the server's vocabulary, so the seam reports a fault. The point of the scenario is that
 * the eight well-formed records beside it are *not* drawn: a partial trace shown as a whole one is a false
 * negative about traceability.
 */
const invalidThread = () => {
  const base = highLevelThread(HLR_REQ, "Requirement")
  return { ...base, nodes: [...base.nodes, node({ id: RETEST, kind: "Widget", lane: 5, displayNumber: "W-1" })] }
}

/** A realistic fan-out: one requirement covered by several procedures, each with its own runs. */
const denseThread = () => {
  const nodes: Node[] = [problemReport, changeRequest, highLevelRequirement(true), systemRequirement(false)]
  const edges: Edge[] = [
    edge(PR, "ProblemReport", CR, "ChangeRequest", "resolved by"),
    edge(CR, "ChangeRequest", HLR_REQ, "Requirement", "authored"),
    edge(SYS_REQ, "Requirement", HLR_REQ, "Requirement", "allocated from"),
  ]
  for (let index = 0; index < 9; index += 1) {
    const suffix = String(index).padStart(2, "0")
    const caseId = `c0000000-0000-4000-8000-0000000000${suffix}`
    const procedureId = `d0000000-0000-4000-8000-0000000000${suffix}`
    const executionId = `e0000000-0000-4000-8000-0000000000${suffix}`
    nodes.push(node({
      id: caseId, kind: "Case", lane: 3, displayNumber: `HLRTC-0001${suffix}.00`,
      title: `Coverage case ${index + 1}`, state: "Approved", level: "HighLevel", revision: 0,
    }))
    nodes.push(node({
      id: procedureId, kind: "Procedure", lane: 4, displayNumber: `HLRTP-0001${suffix}.00`,
      title: `Coverage procedure ${index + 1}`, state: "Approved", level: "HighLevel", revision: 0,
    }))
    nodes.push(node({
      id: executionId, kind: "Execution", lane: 5, displayNumber: null, title: "test.engineer",
      state: index % 4 === 3 ? "Fail" : "Pass", outcome: index % 4 === 3 ? "Fail" : "Pass",
      executedBy: "test.engineer", executedAt: `2026-08-${String(index + 1).padStart(2, "0")}T09:00:00+00:00`,
      recordedAt: `2026-08-${String(index + 1).padStart(2, "0")}T09:05:00+00:00`, evidence: [evidenceFile],
    }))
    edges.push(edge(HLR_REQ, "Requirement", procedureId, "Procedure", "verified by", index % 3 === 0))
    edges.push(edge(caseId, "Case", procedureId, "Procedure", "run by"))
    edges.push(edge(procedureId, "Procedure", executionId, "Execution", "produced"))
    edges.push(edge(executionId, "Execution", BUILD, "Build", "evidence for"))
  }
  nodes.push(build(false))
  return thread({ focalKind: "Requirement", focalId: HLR_REQ, nodes, edges })
}

const scenario = new URLSearchParams(window.location.search).get("case") ?? "hlr"

const responses: Record<string, unknown> = {
  hlr: highLevelThread(HLR_REQ, "Requirement"),
  case: highLevelThread(CASE, "Case"),
  procedure: highLevelThread(PROCEDURE, "Procedure"),
  build: highLevelThread(BUILD, "Build"),
  system: systemThread(),
  execution: executionThread(),
  "no-verification": noVerificationThread(),
  solitary: solitaryThread(),
  invalid: invalidThread(),
  dense: denseThread(),
  loading: null,
  error: null,
}

// `in`, not `??`: the loading and failed scenarios deliberately carry a null response, and `??` treated that
// as an unknown scenario and fell back to the populated thread — so both states silently rendered a full board.
const response = scenario in responses ? responses[scenario] : responses.hlr

createRoot(document.getElementById("root")!).render(
  <DigitalThreadArtifact
    response={response}
    loading={scenario === "loading"}
    error={scenario === "error" ? "The server did not answer in time." : null}
    onRetry={() => undefined}
    hrefFor={node => (node.artifactId ? `/artifacts/${node.artifactId}` : undefined)}
    evidenceHref={file => `/api/evidence/${file.id}`}
    onOpenChange={() => undefined}
  />,
)
