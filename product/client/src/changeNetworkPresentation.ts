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
  /** The project's configured ladder, highest layer first. The client never decides this order. */
  orderedLevels?: string[]
}

/**
 * The lane vocabulary for a project, built from the ladder the projection states.
 *
 * The requirement ladder is configured per project — Customer and Interface / ICD are both layers that can sit
 * above System — so the client must not hold a fixed order. `orderedLevels` comes from the server's ladder
 * policy, highest layer first, and the lanes follow it: problem reports feed the ladder from the left,
 * verification change closes it on the right.
 *
 * A project that has not configured a layer simply produces no records at that level, and the canvas drops
 * structurally empty lanes. So a new layer needs no client change at all.
 */
export const LEVEL_LANE_LABELS: Record<string, string> = {
  Customer: "CUSTOMER CHANGE",
  Interface: "INTERFACE / ICD CHANGE",
  System: "SYSTEM CHANGE",
  HighLevel: "SOFTWARE HLR CHANGE",
  LowLevel: "SOFTWARE LLR CHANGE",
}

/** The ladder used when a projection has not stated one. Matches the levels this product ships today. */
export const DEFAULT_ORDERED_LEVELS = ["Customer", "Interface", "System", "HighLevel", "LowLevel"] as const

export const levelLaneLabel = (level: string): string =>
  LEVEL_LANE_LABELS[level] ??
  `${level.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toUpperCase()} CHANGE`

export type LaneModel = {
  labels: string[]
  /** Lane index for a level key, and the fixed lanes either side of the ladder. */
  laneForLevel: Map<string, number>
  problemLane: number
  verificationLane: number
  /** Levels carried by records but absent from the project ladder. Empty is the healthy case. */
  offLadderLevels: string[]
}

/**
 * Build the lane set from the project's ladder, plus any level the records actually carry.
 *
 * The two can disagree. FMS configures `[System, HighLevel, LowLevel]` yet its showcase seeds eight Interface
 * change requests into Build 1.6 — real controlled records at a level the ladder does not list. Folding those
 * into a ladder lane would file them under a level they are not, and dropping them would be worse: a
 * traceability view that omits change requests present in the build states something false about the build.
 *
 * So an off-ladder level gets its own lane, at the head of the ladder because nothing in the ladder derives
 * into it, and `offLadderLevels` names them so the caller can say plainly that they sit outside it.
 */
export const laneModel = (
  orderedLevels: readonly string[] = DEFAULT_ORDERED_LEVELS,
  presentLevels: readonly string[] = [],
): LaneModel => {
  const ladder = orderedLevels.length ? [...orderedLevels] : [...DEFAULT_ORDERED_LEVELS]
  const offLadder = [...new Set(presentLevels)].filter(level => level && !ladder.includes(level)).sort()
  const levels = [...offLadder, ...ladder]
  const laneForLevel = new Map<string, number>()
  levels.forEach((level, index) => laneForLevel.set(level, index + 1))
  return {
    labels: ["PROBLEM REPORT", ...levels.map(levelLaneLabel), "VERIFICATION CHANGE"],
    laneForLevel,
    problemLane: 0,
    verificationLane: levels.length + 1,
    offLadderLevels: offLadder,
  }
}

/** Which lane a projection node belongs to, from the server-stated kind and level. */
export const laneOf = (node: NetworkNode, model: LaneModel = laneModel()): number => {
  if (node.kind === "ProblemReport") return model.problemLane
  if (node.kind === "TestChangeRequest") return model.verificationLane
  // A level the model has never seen still gets the head of the ladder rather than being folded into the
  // nearest one: a record the server returned must stay visible, and under its own level.
  return model.laneForLevel.get(node.level ?? "") ?? 1
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

/**
 * Filter groups behind the toolbar chips. Mirrors the lane vocabulary rather than the identifier prefix.
 *
 * Interface groups with System: the System chip means system-level change control, and a project that has the
 * Interface layer should not find its Interface records vanish when that chip is used.
 */
export const groupOf = (node: NetworkNode): string => {
  if (node.kind === "ProblemReport") return "pr"
  if (node.kind === "TestChangeRequest") return "ver"
  // Layers above System answer the System chip: it means system-level change control, and a project with the
  // Interface or Customer layer should not find those records vanish when it is used.
  if (node.level === "HighLevel") return "hlr"
  if (node.level === "LowLevel") return "llr"
  return "sys"
}

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
  const group = groupOf(node)
  if (group === "pr") return { background: "#eef1f6", color: "#566579" }
  if (group === "ver") return { background: "#e8f4ef", color: "#28735f" }
  if (group === "sys") return { background: "#dff3ee", color: "#176f68" }
  return { background: "#e7effb", color: "#3569a8" }
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
  model: LaneModel = laneModel(),
): "left" | "right" => {
  const lane = laneOf(selected, model)
  let right = 0
  let left = 0
  for (const edge of directLinks) {
    const other = byId.get(edge.fromId === selected.id ? edge.toId : edge.fromId)
    if (!other) continue
    if (laneOf(other, model) > lane) right += 1
    else if (laneOf(other, model) < lane) left += 1
  }
  return right >= left ? "left" : "right"
}

/**
 * Rows within a lane, ordered by display number so the board is stable across reloads. The projection returns
 * nodes sorted already; this keeps the ordering explicit rather than depending on it.
 */
export const assignRows = (
  nodes: readonly NetworkNode[],
  model: LaneModel = laneModel(),
): Map<string, number> => {
  const rows = new Map<string, number>()
  const perLane = new Map<number, NetworkNode[]>()
  for (const node of nodes) {
    const lane = laneOf(node, model)
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
