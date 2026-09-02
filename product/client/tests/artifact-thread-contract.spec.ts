import { expect, test } from "@playwright/test"
import {
  ARTIFACT_THREAD_LANES,
  artifactThreadFocalNode,
  artifactThreadLaneGroups,
  artifactThreadUrl,
  parseArtifactThread,
} from "../src/artifactThreadContract"

/**
 * The client seam for the slice 5A artifact-thread read.
 *
 * These are contract tests, not view tests. What they mostly prove is that the seam refuses to think for
 * the server: it does not recompute a lane, re-derive a kind, recalculate suspectness, invent an execution
 * identifier, or quietly discard a record it cannot understand. Each of those was a real defect class
 * during 5A review, and this is the layer where they would come back.
 */

const PASS_HASH = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"

const PROJECT = "5f6e1b0a-1c2d-4e3f-8a9b-0c1d2e3f4a5b"
const BASELINE = "a1b2c3d4-e5f6-4708-9a0b-1c2d3e4f5061"
const BUILD = "b2c3d4e5-f607-4819-a0b1-2c3d4e5f6172"

const SYSTEM_REVISION = "11111111-1111-4111-8111-111111111111"
const CASE_REVISION = "22222222-2222-4222-8222-222222222222"
const PROCEDURE_REVISION = "33333333-3333-4333-8333-333333333333"
const EXECUTION = "44444444-4444-4444-8444-444444444444"
const CHANGE_REQUEST = "55555555-5555-4555-8555-555555555555"
const TEST_CHANGE_REQUEST = "66666666-6666-4666-8666-666666666666"
const PROBLEM_REPORT = "77777777-7777-4777-8777-777777777777"
const EVIDENCE = "88888888-8888-4888-8888-888888888888"

/** A production-shaped response carrying every supported kind, in the server's own vocabulary. */
const response = (overrides: Record<string, unknown> = {}) => ({
  projectId: PROJECT,
  baselineId: BASELINE,
  buildId: null,
  focalKind: "Requirement",
  focalId: SYSTEM_REVISION,
  verification: { isApplicable: true, reason: null },
  nodes: [
    { id: PROBLEM_REPORT, kind: "ProblemReport", lane: 0, displayNumber: "PR-97001.00", title: "Waypoints sequenced out of order", state: "Open", level: null, isFocal: false },
    { id: CHANGE_REQUEST, kind: "ChangeRequest", lane: 1, displayNumber: "SRCR-97001.00", title: "Oceanic sequencing", state: "Approved", level: null, isFocal: false },
    { id: TEST_CHANGE_REQUEST, kind: "TestChangeRequest", lane: 1, displayNumber: "SYSTPCR-97050.00", title: "Sequencing procedure change", state: "Approved", level: null, isFocal: false },
    { id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: "SR-97001.01", title: "The FMS shall sequence oceanic waypoints.", state: "Active", level: "System", isFocal: true, artifactId: "99999999-9999-4999-8999-999999999999", revision: 1 },
    { id: CASE_REVISION, kind: "Case", lane: 3, displayNumber: "HLRTC-97001.00", title: "Oceanic sequencing case", state: "Approved", level: "HighLevel", isFocal: false },
    { id: PROCEDURE_REVISION, kind: "Procedure", lane: 4, displayNumber: "HLRTP-97001.00", title: "Filed order procedure", state: "Approved", level: "HighLevel", isFocal: false },
    {
      id: EXECUTION, kind: "Execution", lane: 5, displayNumber: null, title: "test.engineer", state: "Pass",
      level: null, isFocal: false, outcome: "Pass", executedBy: "test.engineer",
      executedAt: "2026-08-14T09:00:00+00:00", recordedAt: "2026-08-14T09:05:00+00:00",
      evidence: [{ id: EVIDENCE, fileName: "oceanic-run.json", contentType: "application/json", size: 2048, sha256: PASS_HASH, uploadedBy: "test.engineer", uploadedAt: "2026-08-14T09:06:00+00:00" }],
    },
    { id: BUILD, kind: "Build", lane: 5, displayNumber: "FMS-7.0.0", title: "Released baseline", state: "Recorded", level: null, isFocal: false },
  ],
  edges: [
    { fromId: PROBLEM_REPORT, fromKind: "ProblemReport", toId: CHANGE_REQUEST, toKind: "ChangeRequest", relation: "resolved by", isSuspect: false },
    { fromId: CHANGE_REQUEST, fromKind: "ChangeRequest", toId: SYSTEM_REVISION, toKind: "Requirement", relation: "authored", isSuspect: false },
    { fromId: TEST_CHANGE_REQUEST, fromKind: "TestChangeRequest", toId: PROCEDURE_REVISION, toKind: "Procedure", relation: "authored", isSuspect: false },
    { fromId: SYSTEM_REVISION, fromKind: "Requirement", toId: CASE_REVISION, toKind: "Case", relation: "verified by", isSuspect: true },
    { fromId: CASE_REVISION, fromKind: "Case", toId: PROCEDURE_REVISION, toKind: "Procedure", relation: "run by", isSuspect: false },
    { fromId: PROCEDURE_REVISION, fromKind: "Procedure", toId: EXECUTION, toKind: "Execution", relation: "produced", isSuspect: false },
    { fromId: EXECUTION, fromKind: "Execution", toId: BUILD, toKind: "Build", relation: "evidence for", isSuspect: false },
  ],
  ...overrides,
})

const parsed = (overrides: Record<string, unknown> = {}) => {
  const result = parseArtifactThread(response(overrides))
  if (!result.ok) throw new Error(`expected a valid thread, got: ${result.reason}`)
  return result.thread
}

test("a production-shaped response carrying every supported kind is accepted", () => {
  const thread = parsed()
  expect(thread.nodes.map(node => node.kind).sort()).toEqual([
    "Build", "Case", "ChangeRequest", "Execution", "ProblemReport", "Procedure", "Requirement", "TestChangeRequest",
  ])
  expect(thread.edges).toHaveLength(7)
})

test("there are exactly six lanes and the last one is RESULT · BUILD", () => {
  expect(ARTIFACT_THREAD_LANES).toHaveLength(6)
  expect(ARTIFACT_THREAD_LANES[5]).toBe("RESULT · BUILD")
})

test("an execution and a build stay together in the final lane", () => {
  const groups = artifactThreadLaneGroups(parsed())
  const last = groups[groups.length - 1]

  // Six lanes, not seven: splitting result and build apart would contradict the prototype's lane model and
  // strand the intra-lane edge between them.
  expect(last.lane).toBe(5)
  expect(last.label).toBe("RESULT · BUILD")
  expect(last.nodes.map(node => node.kind).sort()).toEqual(["Build", "Execution"])
})

test("a change request and a test change request stay distinct in the change request lane", () => {
  const groups = artifactThreadLaneGroups(parsed())
  const changeLane = groups.find(group => group.lane === 1)!

  // A TestChangeReview is a different aggregate and ChangeRequestType has no Test member. Sharing a lane
  // is a layout fact; it must not become an identity fact.
  expect(changeLane.nodes.map(node => node.kind).sort()).toEqual(["ChangeRequest", "TestChangeRequest"])
})

test("exact identities survive normalization unchanged", () => {
  const thread = parsed()
  const requirement = thread.nodes.find(node => node.kind === "Requirement")!

  // Coverage and provenance are recorded per revision, so a revision identity that drifted here would
  // silently reattach evidence to the wrong version of a controlled artifact.
  expect(requirement.id).toBe(SYSTEM_REVISION)
  expect(requirement.artifactId).toBe("99999999-9999-4999-8999-999999999999")
  expect(requirement.revision).toBe(1)
  expect(thread.nodes.map(node => node.id)).toEqual([
    PROBLEM_REPORT, CHANGE_REQUEST, TEST_CHANGE_REQUEST, SYSTEM_REVISION,
    CASE_REVISION, PROCEDURE_REVISION, EXECUTION, BUILD,
  ])
})

test("edge endpoint kinds and the authoritative relation survive unchanged", () => {
  const thread = parsed()

  // "allocated from" and "derived from" are different controlled claims, as are "verified by" and "run by".
  // Flattening any of them into one generic word would lose trace meaning the domain records.
  expect(thread.edges.map(edge => edge.relation)).toEqual([
    "resolved by", "authored", "authored", "verified by", "run by", "produced", "evidence for",
  ])
  const carried = thread.edges.find(edge => edge.fromId === TEST_CHANGE_REQUEST)!
  expect(carried.fromKind).toBe("TestChangeRequest")
  expect(carried.toKind).toBe("Procedure")
})

test("a suspect edge stays suspect because the server said so", () => {
  const thread = parsed()
  const coverage = thread.edges.find(edge => edge.toId === CASE_REVISION)!
  expect(coverage.isSuspect).toBe(true)
})

test("a settled edge stays settled even when its wording sounds alarming", () => {
  const thread = parsed({
    edges: [{
      fromId: SYSTEM_REVISION, fromKind: "Requirement", toId: CASE_REVISION, toKind: "Case",
      relation: "suspect carried-forward coverage under review", isSuspect: false,
    }],
  })

  // Suspectness is a server statement, never inferred from relation wording, lifecycle wording, an
  // identifier, a revision state or a lane. The relation is carried verbatim and the flag is not touched.
  const edge = thread.edges[0]
  expect(edge.relation).toBe("suspect carried-forward coverage under review")
  expect(edge.isSuspect).toBe(false)
})

test("evidence keeps its hash and every identity field", () => {
  const execution = parsed().nodes.find(node => node.kind === "Execution")!
  const evidence = execution.evidence![0]

  // The hash is why EvidenceRecord exists. Folding it into free text would drop exactly the immutability
  // facts a certification reviewer follows the thread to reach.
  expect(evidence).toEqual({
    id: EVIDENCE,
    fileName: "oceanic-run.json",
    contentType: "application/json",
    size: 2048,
    sha256: PASS_HASH,
    uploadedBy: "test.engineer",
    uploadedAt: "2026-08-14T09:06:00+00:00",
  })
})

test("an execution with no display number keeps none", () => {
  const execution = parsed().nodes.find(node => node.kind === "Execution")!

  // TestExecution has no controlled number in this domain, and the prototype's EXE-004821 is mockup text.
  // Deriving one from the GUID would put an identifier on a certification record that does not have one.
  expect(execution.displayNumber).toBeNull()
  expect(execution.outcome).toBe("Pass")
  expect(execution.executedBy).toBe("test.engineer")
})

test("an unconnected focal artifact is a valid one-node thread", () => {
  const thread = parsed({
    focalKind: "Procedure",
    focalId: PROCEDURE_REVISION,
    nodes: [{ id: PROCEDURE_REVISION, kind: "Procedure", lane: 4, displayNumber: "SYSTP-97009.00", title: "Unconnected procedure", state: "Draft", level: "System", isFocal: true }],
    edges: [],
  })

  // §6.8: an unconnected record still renders as a normal card. A seam that treated an empty edge list as
  // malformed would make the view unable to show it at all.
  expect(thread.nodes).toHaveLength(1)
  expect(artifactThreadFocalNode(thread).id).toBe(PROCEDURE_REVISION)
  expect(artifactThreadLaneGroups(thread).map(group => group.lane)).toEqual([4])
})

test("a level with no verification discipline keeps its reason and fabricates nothing", () => {
  const reason = "The Interface level has no verification discipline, so this thread has no test case, procedure or result."
  const thread = parsed({
    focalKind: "Requirement",
    focalId: SYSTEM_REVISION,
    verification: { isApplicable: false, reason },
    nodes: [{ id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: "IRS-97001.00", title: "The FMS shall expose waypoints on ARINC 429 label 310.", state: "Active", level: "Interface", isFocal: true }],
    edges: [],
  })

  expect(thread.verification.isApplicable).toBe(false)
  expect(thread.verification.reason).toBe(reason)
  expect(thread.nodes.filter(node => node.lane >= 3)).toEqual([])
})

test("an unknown node kind is refused rather than guessed into a lane", () => {
  const result = parseArtifactThread(response({
    nodes: [{ id: SYSTEM_REVISION, kind: "Baseline", lane: 2, displayNumber: "SR-97001.01", title: null, state: null, level: null, isFocal: true }],
    edges: [],
  }))

  // Never resolved by identifier prefix. An unsupported kind means the client and server disagree about the
  // vocabulary, and rendering it anywhere would assert something the contract does not support.
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("Baseline")
})

test("a kind sitting in the wrong lane is refused rather than relocated", () => {
  const result = parseArtifactThread(response({
    nodes: [{ id: EXECUTION, kind: "Execution", lane: 4, displayNumber: null, title: null, state: "Pass", level: null, isFocal: true }],
    edges: [],
  }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("lane 5")
})

test("a lane outside the six is refused", () => {
  const result = parseArtifactThread(response({
    nodes: [{ id: SYSTEM_REVISION, kind: "Requirement", lane: 6, displayNumber: null, title: null, state: null, level: null, isFocal: true }],
    edges: [],
  }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("not one of the six")
})

test("an edge naming a node that is not on the board is refused, not silently dropped", () => {
  const result = parseArtifactThread(response({
    nodes: [{ id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: null, title: null, state: null, level: null, isFocal: true }],
    edges: [{ fromId: SYSTEM_REVISION, fromKind: "Requirement", toId: CASE_REVISION, toKind: "Case", relation: "verified by", isSuspect: false }],
  }))

  // Dropping the edge and returning the rest would present an incomplete trace as a complete one.
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain(CASE_REVISION)
})

test("a thread missing its own focal node is refused", () => {
  const result = parseArtifactThread(response({
    nodes: [{ id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: null, title: null, state: null, level: null, isFocal: false }],
    edges: [],
  }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("focal node")
})

test("a thread naming more than one focal node is refused", () => {
  const result = parseArtifactThread(response({
    nodes: [
      { id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: null, title: null, state: null, level: null, isFocal: true },
      { id: CASE_REVISION, kind: "Case", lane: 3, displayNumber: null, title: null, state: null, level: null, isFocal: true },
    ],
    edges: [],
  }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("2 focal nodes")
})

test("a focal node that is not the artifact requested is refused", () => {
  const result = parseArtifactThread(response({
    focalId: CASE_REVISION,
    nodes: [{ id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: null, title: null, state: null, level: null, isFocal: true }],
    edges: [],
  }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("not the artifact that was requested")
})

test("the request url carries the configuration the read requires", () => {
  const withoutBuild = artifactThreadUrl({
    projectId: PROJECT, baselineId: BASELINE, focalKind: "Requirement", focalId: SYSTEM_REVISION,
  })
  expect(withoutBuild).toBe(
    `/api/artifact-thread?projectId=${PROJECT}&baselineId=${BASELINE}&focalKind=Requirement&focalId=${SYSTEM_REVISION}`)

  // An absent build is a different request from a named one: omitted, an Execution or Build focal anchors
  // its own recorded build, so an empty parameter must not be sent in its place.
  expect(withoutBuild).not.toContain("buildId")
  expect(artifactThreadUrl({
    projectId: PROJECT, baselineId: BASELINE, focalKind: "Build", focalId: BUILD, buildId: BUILD,
  })).toContain(`buildId=${BUILD}`)
})

test("an empty lane is dropped rather than rendered as a placeholder", () => {
  const groups = artifactThreadLaneGroups(parsed({
    nodes: [
      { id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: null, title: null, state: null, level: null, isFocal: true },
      { id: BUILD, kind: "Build", lane: 5, displayNumber: "FMS-7.0.0", title: null, state: "Recorded", level: null, isFocal: false },
    ],
    edges: [],
  }))

  // The prototype filters unused lanes and re-indexes the rest, so structurally empty lanes are not shown.
  // The server lane index is kept on the group so the caller still knows which lane survived.
  expect(groups.map(group => group.lane)).toEqual([2, 5])
})
