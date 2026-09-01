/**
 * Presentation for the build change network.
 *
 * Lane, tone and badge come from the server-stated `kind` and `level`. Nothing here parses an identifier: a
 * display number is for people to read, and deciding a record's level from its prefix would rebuild lifecycle
 * meaning in the browser, which the projection exists to prevent.
 */

/** One exact node of the server projection, as returned by /api/change-requests/network. */
export type NetworkNode = {
  id: string
  kind: string
  displayNumber: string
  title?: string | null
  state?: string | null
  projectId?: string | null
  buildId?: string | null
  buildVersion?: string | null
  revision?: number | null
  level?: string | null
  artifactId?: string | null
}

export type NetworkEdge = {
  fromId: string
  fromKind: string
  toId: string
  toKind: string
  relation: string
  provenance: { kind: string; sourceId?: string | null; isLive?: boolean; status?: string | null }[]
}

export type NetworkProjection = {
  projectId: string
  releaseId: string
  nodes: NetworkNode[]
  edges: NetworkEdge[]
  truncated: boolean
}

export const NETWORK_LANES = [
  "PROBLEM REPORT",
  "SYSTEM CHANGE",
  "SOFTWARE HLR CHANGE",
  "SOFTWARE LLR CHANGE",
  "VERIFICATION CHANGE",
] as const

export const LANE_PROBLEM = 0
export const LANE_SYSTEM = 1
export const LANE_HLR = 2
export const LANE_LLR = 3
export const LANE_VERIFICATION = 4

/**
 * Which lane a projection node belongs to.
 *
 * Interface changes are system-level change control and share the System lane. #880's five lanes do not name
 * them, so this keeps them visible with their own badge rather than hiding them or inventing a sixth lane;
 * the owner should confirm whether they deserve one.
 */
export const laneOf = (node: NetworkNode): number => {
  if (node.kind === "ProblemReport") return LANE_PROBLEM
  if (node.kind === "TestChangeRequest") return LANE_VERIFICATION
  switch (node.level) {
    case "HighLevel":
      return LANE_HLR
    case "LowLevel":
      return LANE_LLR
    default:
      return LANE_SYSTEM
  }
}

/** The short square badge on a card. Says the level, which the identifier alone does not reliably carry. */
export const badgeOf = (node: NetworkNode): string => {
  if (node.kind === "ProblemReport") return "PR"
  if (node.kind === "TestChangeRequest") return "TCR"
  switch (node.level) {
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

/** Filter groups behind the toolbar chips. Mirrors the lane vocabulary rather than the identifier prefix. */
export const groupOf = (node: NetworkNode): string =>
  ["pr", "sys", "hlr", "llr", "ver"][laneOf(node)] ?? "sys"

export type Pill = { background: string; color: string }

/**
 * State pills, from index.css tokens. Suspect is amber, and always carries its word as well as its colour —
 * status may never be conveyed by colour alone.
 */
export const pillFor = (state?: string | null): Pill => {
  switch (state) {
    case "Approved":
    case "Pass":
    case "SelectedForBaseline":
      return { background: "#e8f4ef", color: "#28735f" }
    case "InReview":
      return { background: "#e7effb", color: "#3569a8" }
    case "Suspect":
      return { background: "#fdefcf", color: "#8a5a00" }
    case "Released":
    case "Effective":
      return { background: "#dff3ee", color: "#176f68" }
    case "Deferred":
      return { background: "#f3ebe2", color: "#8a6a4a" }
    default:
      return { background: "#eef1f5", color: "#5f7080" }
  }
}

/** Badge tints, keyed on lane so a card reads its level before its text is legible. */
export const badgeTintFor = (node: NetworkNode): Pill => {
  switch (laneOf(node)) {
    case LANE_PROBLEM:
      return { background: "#eef1f6", color: "#566579" }
    case LANE_SYSTEM:
      return { background: "#dff3ee", color: "#176f68" }
    case LANE_VERIFICATION:
      return { background: "#e8f4ef", color: "#28735f" }
    default:
      return { background: "#e7effb", color: "#3569a8" }
  }
}

/** True when this edge is one the server has flagged as suspect. */
export const isSuspectEdge = (edge: NetworkEdge): boolean =>
  edge.provenance.some(fact => fact.status?.toLowerCase().includes("suspect") === true) ||
  edge.relation.toLowerCase().includes("suspect")

/**
 * Which side the detail panel takes for a selected record.
 *
 * It counts where the record's direct links actually sit and returns the emptier side, so the panel cannot
 * come to rest on top of a record the highlighted edge points at. That was the first defect the design review
 * found: a system change with all its work downstream had the panel land squarely on its verification change.
 *
 * Side-picking alone is not the whole rule — the caller must also shrink the canvas frame by the panel, so
 * the board reframes into what is left rather than merely being covered more tastefully.
 */
export const resolveDock = (
  selected: NetworkNode,
  directLinks: readonly NetworkEdge[],
  byId: ReadonlyMap<string, NetworkNode>,
): "left" | "right" => {
  const lane = laneOf(selected)
  let right = 0
  let left = 0
  for (const edge of directLinks) {
    const other = byId.get(edge.fromId === selected.id ? edge.toId : edge.fromId)
    if (!other) continue
    if (laneOf(other) > lane) right += 1
    else if (laneOf(other) < lane) left += 1
  }
  return right >= left ? "left" : "right"
}

/**
 * Rows within a lane, ordered by display number so the board is stable across reloads. The projection returns
 * nodes sorted already; this keeps the ordering explicit rather than depending on it.
 */
export const assignRows = (nodes: readonly NetworkNode[]): Map<string, number> => {
  const rows = new Map<string, number>()
  const perLane = new Map<number, NetworkNode[]>()
  for (const node of nodes) {
    const lane = laneOf(node)
    const bucket = perLane.get(lane)
    if (bucket) bucket.push(node)
    else perLane.set(lane, [node])
  }
  for (const bucket of perLane.values()) {
    bucket
      .slice()
      .sort((a, b) => a.displayNumber.localeCompare(b.displayNumber))
      .forEach((node, index) => rows.set(node.id, index))
  }
  return rows
}
