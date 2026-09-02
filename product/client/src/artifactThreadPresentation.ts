/**
 * Presentation for the artifact thread (#880 §5.3).
 *
 * Everything here is a reading of facts the server already stated through the slice-5B0 seam: the lane of a
 * node, its kind, whether a relationship is suspect, and what an execution actually recorded. Nothing infers a
 * kind from an identifier prefix, a lane from a display number, or suspectness from wording — the seam refuses
 * those at the contract boundary and this module must not reintroduce them one layer up.
 *
 * The canvas geometry, trace walk, rolling and density all come from `digitalThreadGeometry`, shared with the
 * change network and inside-a-change. This module only decides what a card says and which lane row it sits in.
 */

import {
  ARTIFACT_THREAD_LANES,
  ARTIFACT_THREAD_NODE_KINDS,
  type ArtifactThread,
  type ArtifactThreadEdge,
  type ArtifactThreadEvidence,
  type ArtifactThreadNode,
  type ArtifactThreadNodeKind,
} from "./artifactThreadContract"
import { type CanvasEdge, type CanvasNode, compactLanes } from "./digitalThreadGeometry"
import { type Pill } from "./changeNetworkPresentation"

export type { Pill } from "./changeNetworkPresentation"

/**
 * State pills, shared with the change network rather than redefined.
 *
 * One state vocabulary means `Approved` cannot come out teal on one view and grey on another, and it keeps the
 * amber Suspect treatment identical wherever a suspect fact can appear.
 */
export { pillFor } from "./changeNetworkPresentation"

/**
 * The short square badge. Read from the server's `kind`, and for the artifact families that carry one, `level`.
 *
 * A change request gets `CR` rather than a level badge because this projection states `level: null` for
 * `ChangeRequest` and `TestChangeRequest` nodes. The canonical prototype illustrates them as SYS / HLR / LLR,
 * but that badge would have to come from the identifier prefix, which is the one derivation #880 forbids
 * outright. A truthful `CR` is better than a level the response never claimed.
 */
export const badgeOf = (node: ArtifactThreadNode): string => {
  switch (node.kind) {
    case "ProblemReport":
      return "PR"
    case "ChangeRequest":
      return "CR"
    case "TestChangeRequest":
      return "TCR"
    case "Case":
      return "TC"
    case "Procedure":
      return "TP"
    case "Execution":
      return "EX"
    case "Build":
      return "BLD"
    default:
      return levelBadge(node.level)
  }
}

const levelBadge = (level: string | null | undefined): string => {
  switch (level) {
    case "HighLevel":
      return "HLR"
    case "LowLevel":
      return "LLR"
    case "Interface":
      return "IFC"
    case "Customer":
      return "CUS"
    default:
      return "SYS"
  }
}

/**
 * Filter groups behind the toolbar chips, mirroring the prototype's thread grouping.
 *
 * The prototype puts cases, procedures, executions and test change requests together under `ver`, and the
 * build with the system group. Requirements answer their own level, with layers above System answering the
 * System chip for the same reason the change network gives: the chip means system-level control, and a project
 * configuring Customer or Interface should not find those records vanish when it is used.
 *
 * `pr` and `cr` have no chip of their own, so a problem report or change request dims whenever any chip is
 * active. That matches the prototype, and a change request cannot answer a level chip here in any case.
 */
export const groupOf = (node: ArtifactThreadNode): string => {
  switch (node.kind) {
    case "ProblemReport":
      return "pr"
    case "ChangeRequest":
      return "cr"
    case "TestChangeRequest":
    case "Case":
    case "Procedure":
    case "Execution":
      return "ver"
    case "Build":
      return "sys"
    default:
      return node.level === "HighLevel" ? "hlr" : node.level === "LowLevel" ? "llr" : "sys"
  }
}

/** Badge tints, keyed on the filter group so a card reads its family before its text is legible. */
export const badgeTintFor = (node: ArtifactThreadNode): Pill => {
  const group = groupOf(node)
  if (group === "pr") return { background: "#eef1f6", color: "#566579" }
  if (group === "cr") return { background: "#f1eef6", color: "#5b5079" }
  if (group === "ver") return { background: "#e8f4ef", color: "#28735f" }
  if (group === "sys") return { background: "#dff3ee", color: "#176f68" }
  return { background: "#e7effb", color: "#3569a8" }
}

/**
 * What a card shows where an identifier would go.
 *
 * A `TestExecution` has no controlled display number, and `displayNumber` is null for exactly that reason. The
 * card is identified by what the run actually recorded — its outcome — rather than by a manufactured
 * `EXE-1234`, a GUID fragment, or a synthetic certification number. Inventing one would put an identifier on a
 * certification surface that the controlled record does not have.
 */
export const identityLabel = (node: ArtifactThreadNode): string => {
  if (node.displayNumber) return node.displayNumber
  if (node.kind === "Execution") return node.outcome ? `${node.outcome} run` : "Recorded run"
  return "Unnumbered record"
}

/** True when this record carries no controlled display number, so callers can render it as prose, not an id. */
export const isUnnumbered = (node: ArtifactThreadNode): boolean => node.displayNumber === null

/**
 * The concise meta line, at the density tier where meta is shown.
 *
 * Each family says the thing a reader following a certification trail is actually looking for: a revision for a
 * controlled artifact, who ran it and when for an execution, and the recorded state elsewhere.
 */
export const metaLine = (node: ArtifactThreadNode): string => {
  if (node.kind === "Execution") {
    const parts = [node.executedBy, node.executedAt ? shortTimestamp(node.executedAt) : null].filter(Boolean)
    const run = parts.length ? parts.join(" · ") : "Run recorded"
    return node.evidence.length
      ? `${run} · ${node.evidence.length} evidence file${node.evidence.length === 1 ? "" : "s"}`
      : run
  }
  if (node.revision !== null && node.revision !== undefined) {
    return `Revision ${String(node.revision).padStart(2, "0")}`
  }
  return node.state ?? ""
}

/**
 * A timestamp shortened to the date, in the browser's own locale.
 *
 * Deliberately not reformatted into a fixed pattern: the repository already treats an execution time as local
 * to the reader, and re-stamping it here would quietly assert a timezone the record does not carry.
 */
export const shortTimestamp = (value: string): string => {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleDateString()
}

/** A file size a reader can judge at a glance, without pretending to more precision than the byte count has. */
export const evidenceSize = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/**
 * The head of a SHA-256, for a card or a row where the whole hash will not fit.
 *
 * Only ever a shortening for display. The full hash is rendered in the detail panel and carried in the DOM, so
 * a reviewer verifying a file never has to work from the abbreviation.
 */
export const shortHash = (sha256: string): string => sha256.slice(0, 12)

/** Every evidence file beneath the executions in a thread, in the order the server returned them. */
export const threadEvidence = (thread: ArtifactThread): ArtifactThreadEvidence[] =>
  thread.nodes.flatMap(node => [...node.evidence])

/**
 * Records that are an endpoint of at least one server-stated suspect relationship.
 *
 * This is what the Suspect chip selects, per #880 §6.7 — not records in a Suspect lifecycle state, which these
 * node families do not have. The set is computed from `edge.isSuspect` and from nothing else: the browser may
 * read the served fact, and may never decide suspectness itself.
 */
export const suspectEndpoints = (edges: readonly ArtifactThreadEdge[]): Set<string> => {
  const endpoints = new Set<string>()
  for (const edge of edges) {
    if (!edge.isSuspect) continue
    endpoints.add(edge.fromId)
    endpoints.add(edge.toId)
  }
  return endpoints
}

/**
 * The suspect relationships touching one record, so the expanded card can say what is unsettled and why.
 *
 * Suspect must never be conveyed by colour alone (#880 §7). This is the text half of that rule.
 */
export const suspectRelations = (
  id: string,
  edges: readonly ArtifactThreadEdge[],
): ArtifactThreadEdge[] => edges.filter(edge => edge.isSuspect && (edge.fromId === id || edge.toId === id))

/** Kind order within a lane: the reading order the prototype puts lane 5 in, result before build. */
const KIND_ORDER = new Map<ArtifactThreadNodeKind, number>(
  ARTIFACT_THREAD_NODE_KINDS.map((kind, index) => [kind, index]),
)

/**
 * A stable sort key for a node within its lane.
 *
 * The server assembles its nodes into a dictionary, so their arrival order is an implementation detail rather
 * than a contract. Ordering here explicitly means the board does not silently reshuffle between two reads of
 * the same thread. An execution sorts by when it ran, because that is the sequence a reviewer reads a run
 * history in and it has no display number to sort by.
 */
const sortKey = (node: ArtifactThreadNode): string =>
  node.kind === "Execution"
    ? node.executedAt ?? node.recordedAt ?? node.id
    : node.displayNumber ?? node.id

/**
 * Lanes, rows and edges for the shared canvas.
 *
 * Structurally empty lanes are dropped and the rest close up, so no empty lane is ever displayed — the
 * prototype filters unused lanes and re-indexes the remainder, and `compactLanes` is that same rule shared with
 * the other two views. A System thread that legitimately bypasses the Test Case lane therefore shows five
 * lanes, not six with a hole in it.
 *
 * Lane membership is the server's `lane`, never recomputed. An artifact with no relationships is a legitimate
 * one-node thread and comes back as a single lane holding one card.
 */
export const artifactThreadCanvasModel = (
  thread: ArtifactThread,
): { lanes: string[]; nodes: CanvasNode[]; edges: CanvasEdge[]; rows: Map<string, number> } => {
  const rows = new Map<string, number>()
  const perLane = new Map<number, ArtifactThreadNode[]>()
  for (const node of thread.nodes) {
    const bucket = perLane.get(node.lane)
    if (bucket) bucket.push(node)
    else perLane.set(node.lane, [node])
  }
  for (const bucket of perLane.values()) {
    bucket.sort(
      (a, b) =>
        (KIND_ORDER.get(a.kind) ?? 0) - (KIND_ORDER.get(b.kind) ?? 0) ||
        sortKey(a).localeCompare(sortKey(b)) ||
        // Exact identity, last. `sortKey` is not unique — two executions of one procedure can share an
        // `executedAt`, which is ordinary for runs written in the same transaction or brought in by an
        // import. Without this the comparator returns 0 for them and a stable sort leaves them in the order
        // the server's dictionary happened to enumerate, which is exactly the non-determinism the ordering
        // exists to remove. The id is unique by contract, so this always decides.
        a.id.localeCompare(b.id),
    )
    bucket.forEach((node, row) => rows.set(node.id, row))
  }

  const placed: CanvasNode[] = thread.nodes.map(node => ({
    id: node.id,
    lane: node.lane,
    row: rows.get(node.id) ?? 0,
  }))
  const compacted = compactLanes([...ARTIFACT_THREAD_LANES], placed)

  return {
    lanes: compacted.lanes,
    nodes: compacted.nodes,
    edges: thread.edges.map(edge => ({
      from: edge.fromId,
      to: edge.toId,
      label: edge.relation,
      // Suspect is carried through from the server statement, so the dashed amber edge and the card note are
      // both reading the same fact rather than two independent guesses at it.
      kind: edge.isSuspect ? "suspect" : "",
    })),
    rows,
  }
}
