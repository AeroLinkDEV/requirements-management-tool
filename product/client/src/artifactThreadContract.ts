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
  evidence: readonly ArtifactThreadEvidence[]
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

/**
 * Raised while reading a response and converted into a reported failure.
 *
 * Contained entirely within `parseArtifactThread`: it never escapes, and callers still branch on the
 * returned result rather than catching. It exists so each field check reads as one line instead of a
 * pyramid of early returns.
 */
class ArtifactThreadContractError extends Error {}

const fail = (reason: string): never => {
  throw new ArtifactThreadContractError(reason)
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const isNodeKind = (value: unknown): value is ArtifactThreadNodeKind =>
  typeof value === 'string' && (ARTIFACT_THREAD_NODE_KINDS as readonly string[]).includes(value)

/** A required identity or label. Empty is treated as absent, because an empty controlled id is not one. */
const requiredText = (value: unknown, what: string): string =>
  typeof value === 'string' && value.length > 0 ? value : fail(`${what} was missing or not text.`)

/**
 * A field the seam publishes as `string | null`.
 *
 * An absent field reads as null, which is what the wire does with an omitted optional. A value of the wrong
 * type is refused rather than coerced: quietly turning a number into null would let a caller trust a typed
 * field that never held what the server sent.
 */
const optionalText = (value: unknown, what: string): string | null =>
  value === null || value === undefined ? null
    : typeof value === 'string' ? value
      : fail(`${what} was present but not text.`)

const optionalNumber = (value: unknown, what: string): number | null =>
  value === null || value === undefined ? null
    : typeof value === 'number' && Number.isFinite(value) ? value
      : fail(`${what} was present but not a number.`)

const requiredBoolean = (value: unknown, what: string): boolean =>
  typeof value === 'boolean' ? value : fail(`${what} was missing or not a boolean.`)

const readEvidence = (raw: unknown, nodeId: string): ArtifactThreadEvidence[] => {
  if (raw === null || raw === undefined) return []
  if (!Array.isArray(raw)) fail(`Evidence on node ${nodeId} was not a list.`)
  return (raw as unknown[]).map(candidate => {
    if (!isRecord(candidate)) return fail(`An evidence record on node ${nodeId} was not an object.`)
    const size = candidate.size
    return {
      id: requiredText(candidate.id, `An evidence record on node ${nodeId} had no identity`),
      fileName: requiredText(candidate.fileName, `Evidence on node ${nodeId} had no file name`),
      contentType: requiredText(candidate.contentType, `Evidence on node ${nodeId} had no content type`),
      // The hash is the reason the record exists, so it is required rather than optional here.
      sha256: requiredText(candidate.sha256, `Evidence on node ${nodeId} carried no SHA-256`),
      uploadedBy: requiredText(candidate.uploadedBy, `Evidence on node ${nodeId} named no uploader`),
      uploadedAt: requiredText(candidate.uploadedAt, `Evidence on node ${nodeId} carried no upload time`),
      size: typeof size === 'number' && Number.isFinite(size)
        ? size
        : fail(`Evidence on node ${nodeId} carried no size.`),
    }
  })
}

const readNode = (candidate: unknown): ArtifactThreadNode => {
  if (!isRecord(candidate)) return fail('An artifact thread node was not an object.')

  const id = requiredText(candidate.id, 'An artifact thread node identity')

  // Never inferred from an identifier prefix: the kind is a server statement or it is a contract fault.
  const kind = candidate.kind
  // Returned, not merely called: a never-returning call narrows the union only in return position.
  if (!isNodeKind(kind)) return fail(`Unsupported artifact thread node kind: ${String(kind)}.`)

  const lane = candidate.lane
  if (typeof lane !== 'number' || !Number.isInteger(lane) || lane < 0 || lane >= ARTIFACT_THREAD_LANES.length)
    fail(`Artifact thread node ${id} named lane ${String(lane)}, which is not one of the six.`)

  // A kind sitting in the wrong lane is a disagreement between two server statements. Placing it by either
  // one would hide that, so it is reported instead.
  if (ARTIFACT_THREAD_KIND_LANE[kind] !== lane)
    fail(`Artifact thread node ${id} is a ${kind} in lane ${lane}, not lane ${ARTIFACT_THREAD_KIND_LANE[kind]}.`)

  return {
    id,
    kind,
    lane: lane as ArtifactThreadLane,
    displayNumber: optionalText(candidate.displayNumber, `The display number on node ${id}`),
    title: optionalText(candidate.title, `The title on node ${id}`),
    state: optionalText(candidate.state, `The state on node ${id}`),
    level: optionalText(candidate.level, `The level on node ${id}`),
    isFocal: requiredBoolean(candidate.isFocal, `The focal flag on node ${id}`),
    artifactId: optionalText(candidate.artifactId, `The artifact id on node ${id}`),
    revision: optionalNumber(candidate.revision, `The revision on node ${id}`),
    outcome: optionalText(candidate.outcome, `The outcome on node ${id}`),
    executedBy: optionalText(candidate.executedBy, `The actor on node ${id}`),
    executedAt: optionalText(candidate.executedAt, `The execution time on node ${id}`),
    recordedAt: optionalText(candidate.recordedAt, `The recorded time on node ${id}`),
    evidence: readEvidence(candidate.evidence, id),
  }
}

/**
 * Validates a raw response and returns it typed, or says why it cannot.
 *
 * `ok: true` is a promise that every field this seam publishes actually held what its type says. Casting a
 * raw object into the typed shape would make that promise unbacked, and the whole point of the seam is that
 * later canvas code can rely on it without re-checking.
 *
 * Deliberately small. The checks are the ones the contract makes reliable — a closed kind vocabulary, a
 * fixed lane set, a known kind-to-lane placement, edges whose endpoints exist and agree about what they
 * are, and exactly one focal node that is the artifact requested. Anything beyond that belongs to the
 * server, not to a schema framework in the browser.
 */
export const parseArtifactThread = (raw: unknown): ArtifactThreadParse => {
  try {
    if (!isRecord(raw)) fail('The artifact thread response was not an object.')
    const body = raw as Record<string, unknown>

    if (!Array.isArray(body.nodes)) fail('The artifact thread response carried no nodes array.')
    if (!Array.isArray(body.edges)) fail('The artifact thread response carried no edges array.')

    const projectId = requiredText(body.projectId, 'The artifact thread project')
    const baselineId = requiredText(body.baselineId, 'The artifact thread baseline')
    const buildId = optionalText(body.buildId, 'The artifact thread build')
    const focalId = requiredText(body.focalId, 'The artifact thread focal identity')

    const focalKind = body.focalKind
    if (typeof focalKind !== 'string'
      || !(ARTIFACT_THREAD_FOCAL_KINDS as readonly string[]).includes(focalKind))
      fail(`The artifact thread named an unsupported focal kind: ${String(focalKind)}.`)

    const verification = body.verification
    if (!isRecord(verification)) fail('The artifact thread response carried no verification statement.')
    const applicability = {
      isApplicable: requiredBoolean(
        (verification as Record<string, unknown>).isApplicable, 'The verification applicability'),
      reason: optionalText((verification as Record<string, unknown>).reason, 'The verification reason'),
    }

    const nodes = (body.nodes as unknown[]).map(readNode)

    // Two nodes sharing an identity would make the endpoint lookup below ambiguous, and one of them would
    // silently win. Exact identity is the thing this contract is built on, so the collision is reported.
    const byId = new Map<string, ArtifactThreadNode>()
    for (const node of nodes) {
      if (byId.has(node.id)) fail(`Artifact thread node ${node.id} appeared more than once.`)
      byId.set(node.id, node)
    }

    const focal = nodes.filter(node => node.isFocal)
    if (focal.length === 0) fail('The artifact thread did not contain its own focal node.')
    if (focal.length > 1)
      fail(`The artifact thread named ${focal.length} focal nodes; exactly one is expected.`)
    if (focal[0].id !== focalId)
      fail('The artifact thread focal node is not the artifact that was requested.')
    // The focal kind is stated twice, at the top level and on the node. Both are server statements, so a
    // disagreement between them is a fault rather than something to resolve in favour of either.
    if (focal[0].kind !== focalKind)
      fail(`The artifact thread was requested as a ${focalKind} but its focal node is a ${focal[0].kind}.`)

    const edges = (body.edges as unknown[]).map(candidate => {
      if (!isRecord(candidate)) return fail('An artifact thread edge was not an object.')
      const fromId = requiredText(candidate.fromId, 'An artifact thread edge source')
      const toId = requiredText(candidate.toId, 'An artifact thread edge target')

      // An endpoint is never synthesized. An edge to a node that is not on the board describes a thread the
      // response did not actually return.
      const from = byId.get(fromId) ?? fail(`An artifact thread edge referred to missing node ${fromId}.`)
      const to = byId.get(toId) ?? fail(`An artifact thread edge referred to missing node ${toId}.`)

      // The endpoint kinds are bound to the nodes they name. An edge claiming its source is a Case while
      // that node says Procedure leaves two contradictory server statements in a validated result, which is
      // precisely the ambiguity this seam exists to remove.
      if (candidate.fromKind !== from.kind)
        fail(`An artifact thread edge called ${fromId} a ${String(candidate.fromKind)}, but that node is a ${from.kind}.`)
      if (candidate.toKind !== to.kind)
        fail(`An artifact thread edge called ${toId} a ${String(candidate.toKind)}, but that node is a ${to.kind}.`)

      return {
        fromId,
        fromKind: from.kind,
        toId,
        toKind: to.kind,
        relation: requiredText(candidate.relation, `The relation on the edge ${fromId} to ${toId}`),
        isSuspect: requiredBoolean(candidate.isSuspect,
          `The server-stated suspect flag on the edge ${fromId} to ${toId}`),
      }
    })

    return {
      ok: true,
      thread: {
        projectId,
        baselineId,
        buildId,
        focalKind: focalKind as ArtifactThreadFocalKind,
        focalId,
        nodes,
        edges,
        verification: applicability,
      },
    }
  } catch (error) {
    if (error instanceof ArtifactThreadContractError) return { ok: false, reason: error.message }
    throw error
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
