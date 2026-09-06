/**
 * Controlled browser fixture for #925 V5 collision and directed-story proof.
 *
 * This is deliberately a test fixture, never a product seed. It describes one complete branch (Problem Report
 * -> Change Request -> System -> HLR -> two LLRs, with a Case/Procedure on each branch) plus a sibling HLR.
 * The sibling shares the System parent but is not reachable from the focal HLR's directed trace. The two
 * response shapes below let a temporary acceptance harness exercise either the artifact-thread or network
 * endpoint without manufacturing relationships in the product client.
 */
import type { ArtifactThread, ArtifactThreadEdge, ArtifactThreadNode } from "../../src/artifactThreadContract"
import type { NetworkProjection } from "../../src/changeNetworkPresentation"

export const V5_FIXTURE_IDS = {
  project: "92592592-9259-4925-8925-925925925925",
  baseline: "925b925b-925b-4925-8925-925b925b925b",
  build: "925c925c-925c-4925-8925-925c925c925c",
  problemReport: "925d925d-925d-4925-8925-925d925d925d",
  changeRequest: "925e925e-925e-4925-8925-925e925e925e",
  system: "925f925f-925f-4925-8925-925f925f925f",
  hlr: "92609260-9260-4926-8926-926092609260",
  siblingHlr: "92619261-9261-4926-8926-926192619261",
  llrA: "92629262-9262-4926-8926-926292629262",
  llrB: "92639263-9263-4926-8926-926392639263",
  caseA: "92649264-9264-4926-8926-926492649264",
  caseB: "92659265-9265-4926-8926-926592659265",
  procedureA: "92669266-9266-4926-8926-926692669266",
  procedureB: "92679267-9267-4926-8926-926792679267",
} as const

const ids = V5_FIXTURE_IDS

const artifactNode = (
  over: Partial<ArtifactThreadNode> & Pick<ArtifactThreadNode, "id" | "kind" | "lane">,
): ArtifactThreadNode => ({
  displayNumber: null,
  title: null,
  state: "Effective",
  level: null,
  isFocal: false,
  evidence: [],
  ...over,
})

const artifactEdge = (
  fromId: string,
  fromKind: ArtifactThreadEdge["fromKind"],
  toId: string,
  toKind: ArtifactThreadEdge["toKind"],
  relation: string,
  isSuspect = false,
): ArtifactThreadEdge => ({ fromId, fromKind, toId, toKind, relation, isSuspect })

export const V5_ARTIFACT_THREAD_FIXTURE: ArtifactThread = {
  projectId: ids.project,
  baselineId: ids.baseline,
  buildId: ids.build,
  focalKind: "Requirement",
  focalId: ids.hlr,
  verification: { isApplicable: true, reason: null },
  nodes: [
    artifactNode({ id: ids.problemReport, kind: "ProblemReport", lane: 0, displayNumber: "PR-925.00", title: "Route branch correction", state: "Open" }),
    artifactNode({ id: ids.changeRequest, kind: "ChangeRequest", lane: 1, displayNumber: "SRCR-925.00", title: "Correct branch allocation", state: "Approved" }),
    artifactNode({ id: ids.system, kind: "Requirement", lane: 2, displayNumber: "SYSR-925.01", title: "Route records through the approved ladder", level: "System", artifactId: ids.system, revision: 1 }),
    artifactNode({ id: ids.hlr, kind: "Requirement", lane: 2, displayNumber: "HLR-925.01", title: "Select each eligible route branch", level: "HighLevel", artifactId: ids.hlr, revision: 1, isFocal: true }),
    artifactNode({ id: ids.siblingHlr, kind: "Requirement", lane: 2, displayNumber: "HLR-926.01", title: "Reject duplicate branches", level: "HighLevel", artifactId: ids.siblingHlr, revision: 1 }),
    artifactNode({ id: ids.llrA, kind: "Requirement", lane: 2, displayNumber: "LLR-925.01", title: "Advance branch A", level: "LowLevel", artifactId: ids.llrA, revision: 1 }),
    artifactNode({ id: ids.llrB, kind: "Requirement", lane: 2, displayNumber: "LLR-925.02", title: "Advance branch B", level: "LowLevel", artifactId: ids.llrB, revision: 1 }),
    artifactNode({ id: ids.caseA, kind: "Case", lane: 3, displayNumber: "LLRTC-925.01", title: "Branch A functional case", level: "LowLevel", artifactId: ids.caseA, revision: 0 }),
    artifactNode({ id: ids.caseB, kind: "Case", lane: 3, displayNumber: "LLRTC-925.02", title: "Branch B functional case", level: "LowLevel", artifactId: ids.caseB, revision: 0 }),
    artifactNode({ id: ids.procedureA, kind: "Procedure", lane: 4, displayNumber: "LLRTP-925.01", title: "Branch A procedure", level: "LowLevel", artifactId: ids.procedureA, revision: 0 }),
    artifactNode({ id: ids.procedureB, kind: "Procedure", lane: 4, displayNumber: "LLRTP-925.02", title: "Branch B procedure", level: "LowLevel", artifactId: ids.procedureB, revision: 0 }),
    artifactNode({ id: ids.build, kind: "Build", lane: 5, displayNumber: "FMS-925.0", title: "Controlled branch build", state: "Released" }),
  ],
  edges: [
    artifactEdge(ids.problemReport, "ProblemReport", ids.changeRequest, "ChangeRequest", "resolved by"),
    artifactEdge(ids.changeRequest, "ChangeRequest", ids.system, "Requirement", "authored"),
    artifactEdge(ids.system, "Requirement", ids.hlr, "Requirement", "allocates to"),
    artifactEdge(ids.system, "Requirement", ids.siblingHlr, "Requirement", "allocates to"),
    artifactEdge(ids.hlr, "Requirement", ids.llrA, "Requirement", "source of"),
    artifactEdge(ids.hlr, "Requirement", ids.llrB, "Requirement", "source of"),
    artifactEdge(ids.llrA, "Requirement", ids.caseA, "Case", "verified by"),
    artifactEdge(ids.llrB, "Requirement", ids.caseB, "Case", "verified by"),
    artifactEdge(ids.caseA, "Case", ids.procedureA, "Procedure", "run by"),
    artifactEdge(ids.caseB, "Case", ids.procedureB, "Procedure", "run by"),
    artifactEdge(ids.procedureA, "Procedure", ids.build, "Build", "evidence for"),
    artifactEdge(ids.procedureB, "Procedure", ids.build, "Build", "evidence for"),
  ],
}

const networkNode = (
  id: string,
  displayNumber: string,
  level: string | null,
  kind = "ChangeRequest",
): NetworkProjection["nodes"][number] => ({
  id,
  kind,
  displayNumber,
  title: `Controlled ${displayNumber} branch record`,
  state: "Approved",
  projectId: ids.project,
  buildId: ids.build,
  buildVersion: "FMS-925.0",
  revision: 1,
  level,
  artifactId: id,
})

const networkEdge = (fromId: string, fromKind: string, toId: string, toKind: string, relation: string) => ({
  fromId,
  fromKind,
  toId,
  toKind,
  relation,
  provenance: [{ kind: "ControlledTestFixture", sourceId: fromId, isLive: true, status: "Fixture" }],
  isSuspect: false,
})

/** Network is a separate change-request projection: its nodes are changes, not artifact-thread records. */
const networkNodes = [
  networkNode(ids.problemReport, "PR-925.00", null, "ProblemReport"),
  networkNode(ids.system, "SYSCR-925.00", "System"),
  networkNode(ids.hlr, "HLRCR-925.00", "HighLevel"),
  networkNode(ids.siblingHlr, "HLRCR-926.00", "HighLevel"),
  networkNode(ids.llrA, "LLRCR-925.01", "LowLevel"),
  networkNode(ids.llrB, "LLRCR-925.02", "LowLevel"),
  networkNode(ids.caseA, "HLRTPCR-925.01", null, "TestChangeRequest"),
]

export const V5_NETWORK_PROJECTION_FIXTURE: NetworkProjection = {
  projectId: ids.project,
  releaseId: ids.baseline,
  truncated: false,
  orderedLevels: ["System", "HighLevel", "LowLevel"],
  nodes: networkNodes,
  edges: [
    networkEdge(ids.problemReport, "ProblemReport", ids.system, "ChangeRequest", "ProblemReportResolution"),
    networkEdge(ids.system, "ChangeRequest", ids.hlr, "ChangeRequest", "Upstream"),
    networkEdge(ids.system, "ChangeRequest", ids.siblingHlr, "ChangeRequest", "Upstream"),
    networkEdge(ids.hlr, "ChangeRequest", ids.llrA, "ChangeRequest", "Upstream"),
    networkEdge(ids.hlr, "ChangeRequest", ids.llrB, "ChangeRequest", "Upstream"),
    networkEdge(ids.hlr, "ChangeRequest", ids.caseA, "TestChangeRequest", "CoveredByTestChangeRequest"),
  ],
}

/** Stable raw response for a temporary HTTP harness. Keep it disposable and outside product seed data. */
export const V5_ARTIFACT_THREAD_RESPONSE = V5_ARTIFACT_THREAD_FIXTURE
export const V5_NETWORK_PROJECTION_RESPONSE = V5_NETWORK_PROJECTION_FIXTURE
