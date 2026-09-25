import { expect, logicTest as test } from "./isolated-client-test"
import { artifactTraceGroups, parseInspectorThread } from '../src/artifactTraceInspectorModel'
import {
  artifactThreadUrl,
  parseArtifactThread,
} from "../src/artifactThreadContract"
import { readRecordedCodeRelationship, recordedCodeSourceHref } from "../src/recordedCodeRelationship"

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

test('inspector groups preserve direct direction, upstream causes and hop-qualified execution context', () => {
  const groups = artifactTraceGroups(parsed())
  expect(groups.incoming.map(edge => edge.fromId)).toEqual([CHANGE_REQUEST])
  expect(groups.outgoing.map(edge => edge.toId)).toEqual([CASE_REVISION])
  expect(groups.outgoing[0].isSuspect).toBe(true)
  expect(groups.upstream.indirect.map(node => node.id)).toEqual([PROBLEM_REPORT])
  expect(groups.downstream.indirect.map(node => node.id)).toEqual([PROCEDURE_REVISION, EXECUTION, BUILD])
  expect(groups.downstream.distance.get(EXECUTION)).toBe(3)
  // The TCR feeding a downstream Procedure is not an upstream cause of the focal requirement.
  expect(groups.upstream.distance.has(TEST_CHANGE_REQUEST)).toBe(false)
})

test('inspector refuses an internally valid graph for another focal revision and malformed payloads', () => {
  expect(parseInspectorThread(response(), CASE_REVISION).ok).toBe(false)
  expect(parseInspectorThread({ nodes: [] }, SYSTEM_REVISION).ok).toBe(false)
  expect(parseInspectorThread(response(), SYSTEM_REVISION).ok).toBe(true)
})

test("a production-shaped response carrying every supported kind is accepted", () => {
  const thread = parsed()
  expect(thread.nodes.map(node => node.kind).sort()).toEqual([
    "Build", "Case", "ChangeRequest", "Execution", "ProblemReport", "Procedure", "Requirement", "TestChangeRequest",
  ])
  expect(thread.edges).toHaveLength(7)
})

test("recorded Code references preserve their exact target and completeness without becoming accepted evidence", () => {
  const reference = {
    id: '99999999-9999-4999-8999-999999999999', relationshipKind: 'MergeRequest', version: 2,
    isActive: true, releaseId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', releaseVersion: '1.6',
    meaning: 'RelatedContext', recordedBy: 'reviewer', recordedAt: '2026-09-21T12:00:00Z',
    targetKind: 'RequirementRevision', targetIdentityId: SYSTEM_REVISION,
    targetOwnerIdentityId: '99999999-9999-4999-8999-999999999998', targetRevisionNumber: 3,
    targetStableIdentity: `RequirementRevision:${SYSTEM_REVISION}`, targetDisplaySnapshot: 'SYSR-97001.03',
    instanceBaseUrl: 'https://gitlab.example', remoteProjectId: 42, repositoryPathSnapshot: 'aerolink/source',
    mergeRequestIid: 12, mergeRequestUrlSnapshot: 'https://gitlab.example/aerolink/source/-/merge_requests/12',
    mergeRequestTitleSnapshot: 'Stored title',
  }
  const body = response({
    recordedCodeReferencesComplete: false,
    nodes: response().nodes.map(node => node.id === SYSTEM_REVISION
      ? { ...node, recordedCodeReferences: [reference] }
      : node),
  })
  const result = parseArtifactThread(body)
  expect(result.ok).toBe(true)
  if (!result.ok) return
  const exact = result.thread.nodes.find(node => node.id === SYSTEM_REVISION)!
  expect(result.thread.recordedCodeReferencesComplete).toBe(false)
  expect(exact.recordedCodeReferences).toEqual([reference])
  expect(exact.evidence).toEqual([])
})

const exactFileReference = (overrides: Record<string, unknown> = {}) => ({
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', relationshipKind: 'File', version: 2, isActive: true,
  releaseId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', releaseVersion: '1.6', meaning: 'RelatedContext',
  recordedBy: 'reviewer', recordedAt: '2026-09-21T12:00:00Z', targetKind: 'ProblemReportRevision',
  targetIdentityId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', targetOwnerIdentityId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  targetRevisionNumber: 1, targetStableIdentity: 'ProblemReportRevision:cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  targetDisplaySnapshot: 'PR-97001.01', instanceBaseUrl: 'https://gitlab.example', remoteProjectId: 42,
  repositoryPathSnapshot: 'aerolink/source', sourceSnapshotId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  sourceSelectionEventId: null, commitSha: 'a'.repeat(40), path: 'src/route.c', startLine: 3, endLine: 12,
  ...overrides,
})

test('a File relationship links to its exact stored commit, path, and ordered line range', () => {
  const reference = readRecordedCodeRelationship(exactFileReference())
  expect(reference).toBeDefined()
  expect(recordedCodeSourceHref(reference!)).toBe(
    `https://gitlab.example/aerolink/source/-/blob/${'a'.repeat(40)}/src/route.c#L3-L12`,
  )
})

test('malformed and unknown Code relationship snapshots fail closed before an external link is built', () => {
  const invalidSnapshots = [
    exactFileReference({ sourceSnapshotId: null }),
    exactFileReference({ commitSha: '../unsafe' }),
    exactFileReference({ path: '../unsafe.c' }),
    exactFileReference({ startLine: 12, endLine: 3 }),
    exactFileReference({ targetIdentityId: 'not-an-exact-id' }),
    exactFileReference({ targetRevisionNumber: null }),
    exactFileReference({ targetStableIdentity: 'ProblemReportRevision:another-snapshot' }),
    exactFileReference({ targetOwnerIdentityId: null }),
    exactFileReference({ targetKind: 'CurrentRequirement' }),
    exactFileReference({ relationshipKind: 'FutureGitRecord' }),
  ]
  for (const raw of invalidSnapshots) {
    expect(readRecordedCodeRelationship(raw)).toBeUndefined()
    expect(recordedCodeSourceHref(raw)).toBeUndefined()
  }
})

test('supported target kinds keep their required exact revision and owner context', () => {
  const cases = [
    exactFileReference({
      targetKind: 'RequirementRevision',
      targetStableIdentity: 'RequirementRevision:cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    }),
    exactFileReference({
      targetKind: 'ChangeRequestRevision', targetOwnerIdentityId: null,
      targetStableIdentity: 'ChangeRequestRevision:cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    }),
    exactFileReference({
      targetKind: 'RequirementProposal', targetRevisionNumber: null,
      targetStableIdentity: 'RequirementProposal:cccccccc-cccc-4ccc-8ccc-cccccccccccc',
    }),
  ]

  for (const reference of cases) expect(readRecordedCodeRelationship(reference)).toBeDefined()
})

test('a malformed recorded Code snapshot refuses the artifact thread instead of dropping only that reference', () => {
  const body = response({
    nodes: response().nodes.map(node => node.id === SYSTEM_REVISION
      ? { ...node, recordedCodeReferences: [{ id: 'bad', targetKind: 'CurrentRequirement' }] }
      : node),
  })
  const result = parseArtifactThread(body)
  expect(result.ok).toBe(false)
  if (!result.ok) expect(result.reason).toContain('exact snapshot contract')
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

  // "authored" from a change request and from a test change request are different provenance, as are
  // "verified by" and "run by". Flattening any of them into one generic word would lose trace meaning
  // the domain records.
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
  const evidence = execution.evidence[0]

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
  expect(thread.nodes[0]).toMatchObject({ id: PROCEDURE_REVISION, lane: 4, isFocal: true })
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

/**
 * The seam publishes `ok: true` as a promise that every field it exposes held what its type says. These
 * prove the promise is backed rather than cast into existence — a caller must be able to trust the typed
 * result without re-checking it, which is the entire reason the seam exists.
 */

test("an edge whose endpoint kind contradicts the node it names is refused", () => {
  const result = parseArtifactThread(response({
    edges: [{
      // Names the Requirement node but calls it a Case. Both are server statements and they disagree.
      fromId: SYSTEM_REVISION, fromKind: "Case", toId: CASE_REVISION, toKind: "Case",
      relation: "verified by", isSuspect: false,
    }],
  }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("but that node is a Requirement")
})

test("an edge whose target kind contradicts its node is refused", () => {
  const result = parseArtifactThread(response({
    edges: [{
      fromId: SYSTEM_REVISION, fromKind: "Requirement", toId: EXECUTION, toKind: "Build",
      relation: "produced", isSuspect: false,
    }],
  }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("but that node is a Execution")
})

test("a focal node whose kind contradicts the requested focal kind is refused", () => {
  const result = parseArtifactThread(response({
    focalKind: "Requirement",
    focalId: CASE_REVISION,
    nodes: [{ id: CASE_REVISION, kind: "Case", lane: 3, displayNumber: null, title: null, state: null, level: null, isFocal: true }],
    edges: [],
  }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("requested as a Requirement but its focal node is a Case")
})

test("a response with no focal identity at all is refused rather than skipping the check", () => {
  const raw = response()
  delete (raw as Record<string, unknown>).focalId
  const result = parseArtifactThread(raw)

  // A missing focalId previously bypassed the identity comparison entirely and was then cast into the
  // returned object, so the seam claimed a validated thread it had not actually checked.
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("focal identity")
})

test("required top-level identities are validated, not assumed", () => {
  for (const field of ["projectId", "baselineId"]) {
    const raw = response()
    delete (raw as Record<string, unknown>)[field]
    const result = parseArtifactThread(raw)
    expect(result.ok).toBe(false)
  }
})

test("a malformed display number is refused rather than normalized to null", () => {
  const result = parseArtifactThread(response({
    nodes: [{ id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: 97001, title: null, state: null, level: null, isFocal: true }],
    edges: [],
  }))

  // Coercing it to null would hand back a legitimate-looking absence for a value the server did send,
  // making the typed field untrustworthy in exactly the case where it matters.
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("not text")
})

test("a malformed verification reason is refused rather than normalized to null", () => {
  const result = parseArtifactThread(response({ verification: { isApplicable: false, reason: 42 } }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("verification reason")
})

test("a missing verification applicability is refused", () => {
  const result = parseArtifactThread(response({ verification: { reason: null } }))
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("applicability")
})

test("evidence missing its hash is refused, not carried through untyped", () => {
  const result = parseArtifactThread(response({
    nodes: [{
      id: EXECUTION, kind: "Execution", lane: 5, displayNumber: null, title: null, state: "Pass", level: null, isFocal: true,
      evidence: [{ id: EVIDENCE, fileName: "oceanic-run.json", contentType: "application/json", size: 2048, uploadedBy: "test.engineer", uploadedAt: "2026-08-14T09:06:00+00:00" }],
    }],
    focalKind: "Execution",
    focalId: EXECUTION,
    edges: [],
  }))

  // The seam publishes sha256 as a string. Casting an object without one would let a caller read undefined
  // from a field its type says is always present.
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("SHA-256")
})

test("an execution with no evidence yields an empty list, not a missing field", () => {
  const thread = parsed({
    nodes: [{ id: EXECUTION, kind: "Execution", lane: 5, displayNumber: null, title: null, state: "Pass", level: null, isFocal: true }],
    focalKind: "Execution",
    focalId: EXECUTION,
    edges: [],
  })
  expect(thread.nodes[0].evidence).toEqual([])
})

test("a duplicated node identity is refused rather than silently deduplicated", () => {
  const result = parseArtifactThread(response({
    nodes: [
      { id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: "SR-97001.01", title: null, state: null, level: null, isFocal: true },
      { id: SYSTEM_REVISION, kind: "Case", lane: 3, displayNumber: "HLRTC-97001.00", title: null, state: null, level: null, isFocal: false },
    ],
    edges: [],
  }))

  // Exact identity is what this contract is built on. Letting one silently win would attach an edge to
  // whichever copy the map happened to keep.
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("more than once")
})

test("a missing focal flag is refused rather than read as false", () => {
  const result = parseArtifactThread(response({
    nodes: [{ id: SYSTEM_REVISION, kind: "Requirement", lane: 2, displayNumber: null, title: null, state: null, level: null }],
    edges: [],
  }))
  expect(result.ok).toBe(false)
})

test("a missing suspect flag is refused rather than read as settled", () => {
  const result = parseArtifactThread(response({
    edges: [{ fromId: SYSTEM_REVISION, fromKind: "Requirement", toId: CASE_REVISION, toKind: "Case", relation: "verified by" }],
  }))

  // Defaulting an absent flag to false would state "not suspect" on the client's own authority, which is
  // the one thing suspectness must never be.
  expect(result.ok).toBe(false)
  expect(result.ok === false && result.reason).toContain("suspect flag")
})
