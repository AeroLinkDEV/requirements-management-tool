/**
 * The client seam for the slice 5A artifact-thread read (`GET /api/artifact-thread`).
 *
 * This is a contract adapter, not a view. It exists so the shared canvas can later consume a validated,
 * typed thread without having to ask what a field means, which lane a node belongs to, whether a
 * relationship is suspect, or how to label an execution. Those are contract questions and they are
 * answered here, once.
 *
 * It deliberately does not re-derive anything the server already decided. The server owns the traversal
 * (#880 §6.5: two fixed-direction walks, no sideways pivot), the lane of every node, the relationship
 * vocabulary, and whether a link is suspect. Nothing here infers a kind from an identifier prefix, a lane
 * from a display number, or suspectness from wording — each of those was a real defect class during slice
 * 5A review, and the seam is the wrong place to reintroduce them.
 */

/** The five §4.4 entry kinds, exactly as `ArtifactThreadFocalKind` names them. */
export const ARTIFACT_THREAD_FOCAL_KINDS = [
  'Requirement',
  'Case',
  'Procedure',
  'Execution',
  'Build',
] as const

export type ArtifactThreadFocalKind = (typeof ARTIFACT_THREAD_FOCAL_KINDS)[number]

/**
 * The six lanes, in the order the canonical prototype defines them.
 *
 * Six, not seven: a result and a build share the final lane, so lane 5 legitimately holds two node kinds
 * and carries an edge whose endpoints sit in one lane. Indexes are the server's `ArtifactThreadLane`
 * constants and are never recomputed here.
 */
export const ARTIFACT_THREAD_LANES = [
  'PROBLEM REPORT',
  'CHANGE REQUEST',
  'REQUIREMENT',
  'TEST CASE',
  'PROCEDURE',
  'RESULT · BUILD',
] as const

export type ArtifactThreadLane = 0 | 1 | 2 | 3 | 4 | 5

/**
 * The node kinds the server may return.
 *
 * `ChangeRequest` and `TestChangeRequest` share the Change Request lane and are **not** interchangeable: a
 * `TestChangeReview` is a different aggregate, and `ChangeRequestType` has no Test member.
 */
export const ARTIFACT_THREAD_NODE_KINDS = [
  'ProblemReport',
  'ChangeRequest',
  'TestChangeRequest',
  'Requirement',
  'Case',
  'Procedure',
  'Execution',
  'Build',
] as const

export type ArtifactThreadNodeKind = (typeof ARTIFACT_THREAD_NODE_KINDS)[number]

/**
 * The lane each kind belongs to, mirroring the server's placement.
 *
 * Held as data so a kind/lane disagreement is a detectable contract fault rather than something the client
 * quietly resolves in the server's favour or its own.
 */
export const ARTIFACT_THREAD_KIND_LANE: Readonly<Record<ArtifactThreadNodeKind, ArtifactThreadLane>> = {
  ProblemReport: 0,
  ChangeRequest: 1,
  TestChangeRequest: 1,
  Requirement: 2,
  Case: 3,
  Procedure: 4,
  Execution: 5,
  Build: 5,
}

/** One immutable evidence file recorded beneath an execution. The hash is the point of the record. */
export type ArtifactThreadEvidence = {
  id: string
  fileName: string
  contentType: string
  size: number
  sha256: string
  uploadedBy: string
  uploadedAt: string
}

/**
 * One exact node. Identity is always an exact revision, execution or build, and is preserved verbatim.
 *
 * `displayNumber` is nullable on purpose: `TestExecution` has no controlled number in this domain, and
 * manufacturing one would invent a certification identifier the record does not have.
 */
export type ArtifactThreadNode = {
  id: string
  kind: ArtifactThreadNodeKind
  lane: ArtifactThreadLane
  displayNumber: string | null
  title: string | null
  state: string | null
  level: string | null
  isFocal: boolean
  artifactId?: string | null
  revision?: number | null
  outcome?: string | null
  executedBy?: string | null
  executedAt?: string | null
  recordedAt?: string | null
  evidence?: readonly ArtifactThreadEvidence[] | null
}

/** One recorded relationship. `relation` and `isSuspect` are server statements, carried unchanged. */
export type ArtifactThreadEdge = {
  fromId: string
  fromKind: ArtifactThreadNodeKind
  toId: string
  toKind: ArtifactThreadNodeKind
  relation: string
  isSuspect: boolean
}

/** Whether the thread's levels have a verification discipline at all, and if not, the server's reason. */
export type ArtifactThreadVerification = { isApplicable: boolean; reason: string | null }

export type ArtifactThread = {
  projectId: string
  baselineId: string
  buildId: string | null
  focalKind: ArtifactThreadFocalKind
  focalId: string
  nodes: readonly ArtifactThreadNode[]
  edges: readonly ArtifactThreadEdge[]
  verification: ArtifactThreadVerification
}

/** What the read needs. `baselineId` is required because §8.2 makes these views build-scoped. */
export type ArtifactThreadRequest = {
  projectId: string
  baselineId: string
  focalKind: ArtifactThreadFocalKind
  focalId: string
  /** Narrows to one exact build. Omitted, an Execution or Build focal anchors its own recorded build. */
  buildId?: string | null
}

export const artifactThreadUrl = (request: ArtifactThreadRequest): string => {
  const query = new URLSearchParams({
    projectId: request.projectId,
    baselineId: request.baselineId,
    focalKind: request.focalKind,
    focalId: request.focalId,
  })
  // Appended only when present: an absent build is a different request from an empty one, and the server
  // resolves the difference.
  if (request.buildId) query.set('buildId', request.buildId)
  return `/api/artifact-thread?${query.toString()}`
}

/**
 * The outcome of reading a response.
 *
 * A failure is reported, never repaired. Dropping malformed records and presenting the remainder would
 * describe a partial trace as a complete one, which is the one thing a certification reader must not be
 * shown.
 */
export type ArtifactThreadParse =
  | { ok: true; thread: ArtifactThread }
  | { ok: false; reason: string }

const invalid = (reason: string): ArtifactThreadParse => ({ ok: false, reason })

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const isNodeKind = (value: unknown): value is ArtifactThreadNodeKind =>
  typeof value === 'string' && (ARTIFACT_THREAD_NODE_KINDS as readonly string[]).includes(value)

/**
 * Validates a raw response and returns it typed, or says why it cannot.
 *
 * Deliberately small. The checks here are the ones the contract makes reliable — a closed kind vocabulary,
 * a fixed lane set, a known kind-to-lane placement, edges whose endpoints are present, and exactly one
 * focal node. Anything beyond that belongs to the server, not to a schema framework in the browser.
 */
export const parseArtifactThread = (raw: unknown): ArtifactThreadParse => {
  if (!isRecord(raw)) return invalid('The artifact thread response was not an object.')
  if (!Array.isArray(raw.nodes)) return invalid('The artifact thread response carried no nodes array.')
  if (!Array.isArray(raw.edges)) return invalid('The artifact thread response carried no edges array.')

  const focalKind = raw.focalKind
  if (typeof focalKind !== 'string'
    || !(ARTIFACT_THREAD_FOCAL_KINDS as readonly string[]).includes(focalKind))
    return invalid(`The artifact thread named an unsupported focal kind: ${String(focalKind)}.`)

  const verification = raw.verification
  if (!isRecord(verification) || typeof verification.isApplicable !== 'boolean')
    return invalid('The artifact thread response carried no verification applicability statement.')

  const nodes: ArtifactThreadNode[] = []
  for (const candidate of raw.nodes) {
    if (!isRecord(candidate)) return invalid('An artifact thread node was not an object.')
    const { id, kind, lane } = candidate
    if (typeof id !== 'string' || id.length === 0) return invalid('An artifact thread node had no identity.')

    // Never inferred from an identifier prefix: the kind is a server statement or it is a contract fault.
    if (!isNodeKind(kind)) return invalid(`Unsupported artifact thread node kind: ${String(kind)}.`)

    if (typeof lane !== 'number' || !Number.isInteger(lane) || lane < 0
      || lane >= ARTIFACT_THREAD_LANES.length)
      return invalid(`Artifact thread node ${id} named lane ${String(lane)}, which is not one of the six.`)

    // A kind sitting in the wrong lane is a disagreement between two server statements. Placing it by
    // either one would hide that, so it is reported instead.
    if (ARTIFACT_THREAD_KIND_LANE[kind] !== lane)
      return invalid(`Artifact thread node ${id} is a ${kind} in lane ${lane}, not lane ${ARTIFACT_THREAD_KIND_LANE[kind]}.`)

    nodes.push({
      ...(candidate as ArtifactThreadNode),
      kind,
      lane: lane as ArtifactThreadLane,
      displayNumber: typeof candidate.displayNumber === 'string' ? candidate.displayNumber : null,
    })
  }

  const focal = nodes.filter(node => node.isFocal)
  if (focal.length === 0) return invalid('The artifact thread did not contain its own focal node.')
  if (focal.length > 1)
    return invalid(`The artifact thread named ${focal.length} focal nodes; exactly one is expected.`)
  if (typeof raw.focalId === 'string' && focal[0].id !== raw.focalId)
    return invalid('The artifact thread focal node is not the artifact that was requested.')

  const byId = new Map(nodes.map(node => [node.id, node]))
  const edges: ArtifactThreadEdge[] = []
  for (const candidate of raw.edges) {
    if (!isRecord(candidate)) return invalid('An artifact thread edge was not an object.')
    const { fromId, toId, relation } = candidate
    if (typeof fromId !== 'string' || typeof toId !== 'string')
      return invalid('An artifact thread edge did not name both endpoints.')

    // An endpoint is never synthesized. An edge to a node that is not on the board describes a thread the
    // response did not actually return.
    const from = byId.get(fromId)
    const to = byId.get(toId)
    if (!from) return invalid(`An artifact thread edge referred to missing node ${fromId}.`)
    if (!to) return invalid(`An artifact thread edge referred to missing node ${toId}.`)

    if (typeof relation !== 'string' || relation.length === 0)
      return invalid(`The artifact thread edge ${fromId} to ${toId} carried no relation.`)
    if (typeof candidate.isSuspect !== 'boolean')
      return invalid(`The artifact thread edge ${fromId} to ${toId} carried no server-stated suspect flag.`)
    if (!isNodeKind(candidate.fromKind) || !isNodeKind(candidate.toKind))
      return invalid(`The artifact thread edge ${fromId} to ${toId} named an unsupported endpoint kind.`)

    edges.push(candidate as unknown as ArtifactThreadEdge)
  }

  return {
    ok: true,
    thread: {
      ...(raw as unknown as ArtifactThread),
      focalKind: focalKind as ArtifactThreadFocalKind,
      nodes,
      edges,
      verification: {
        isApplicable: verification.isApplicable,
        reason: typeof verification.reason === 'string' ? verification.reason : null,
      },
    },
  }
}

/** One lane's nodes, in the order the server returned them. */
export type ArtifactThreadLaneGroup = {
  lane: ArtifactThreadLane
  label: (typeof ARTIFACT_THREAD_LANES)[number]
  nodes: readonly ArtifactThreadNode[]
}

/**
 * Groups nodes by the lane the server placed them in.
 *
 * Empty lanes are dropped, matching the prototype, which filters unused lanes and re-indexes the rest. The
 * lane index is kept on each group so a caller can still tell which lane it is looking at after the drop.
 */
export const artifactThreadLaneGroups = (thread: ArtifactThread): ArtifactThreadLaneGroup[] =>
  ARTIFACT_THREAD_LANES
    .map((label, lane) => ({
      lane: lane as ArtifactThreadLane,
      label,
      nodes: thread.nodes.filter(node => node.lane === lane),
    }))
    .filter(group => group.nodes.length > 0)

/** The focal node, which `parseArtifactThread` has already proven to be present exactly once. */
export const artifactThreadFocalNode = (thread: ArtifactThread): ArtifactThreadNode =>
  thread.nodes.find(node => node.isFocal)!
