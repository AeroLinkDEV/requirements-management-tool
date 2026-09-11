/**
 * Presentation for the build change network.
 *
 * Lane, tone and badge come from the server-stated `kind` and `level`. Nothing here parses an identifier: a
 * display number is for people to read, and deciding a record's level from its prefix would rebuild lifecycle
 * meaning in the browser, which the projection exists to prevent.
 */

/** One exact node of the server projection, as returned by /api/change-requests/network. */
/**
 * The verification-identity facts a test-change node carries, when it is one.
 *
 * `displayNumber` above is a label for a reader. Whether this record holds a governed number is
 * `hasControlledNumber`, stated by the server rather than inferred from the label's prefix or wording — a
 * prefix match cannot tell a controlled request from an assessment whose label happens to mention one.
 */
export type NetworkVerification = {
  hasControlledNumber: boolean
  controlledNumber?: string | null
  controlledRevision?: number | null
  outcome: string
  artifactKind: string
  discipline: string
  originKind: string
  originReferenceId: string
  sourceDisplayNumber?: string | null
}

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
  /** Present on TestChangeRequest nodes only. */
  verification?: NetworkVerification | null
}

export type NetworkEdge = {
  fromId: string
  fromKind: string
  toId: string
  toKind: string
  relation: string
  provenance: { kind: string; sourceId?: string | null; isLive?: boolean; status?: string | null }[]
  /**
   * Whether this relationship is suspect, as the server states it.
   *
   * Required, not optional: the projection states it on every edge, so the client never has to read absence as
   * a value. It is never derived here — relation and provenance carry display vocabulary, and deciding
   * lifecycle state by looking for a word inside them would make any wording containing "suspect" a suspect
   * edge and hide a genuinely suspect one whose wording does not.
   *
   * On the change-network board it is currently always false, and that is the truth rather than a stub: a
   * suspect lifecycle exists only for requirement-trace and case-procedure links, and this board renders
   * neither. Requirement-trace suspectness becomes reachable in the artifact thread (§5.3, slice 5).
   */
  isSuspect: boolean
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
  caseLane: number
  procedureLane: number
}

/**
 * Build the lane set strictly from the project's configured ladder.
 *
 * The ladder is the authority on which levels a project has, so it decides how many lanes there are. A level
 * it does not configure gets no lane, even when records carrying that level exist. FMS once seeded Interface
 * change requests into Build 1.6 while its ladder configures [System, HighLevel, LowLevel]; those seeds were
 * retired at source (#889), and inventing a lane here would still present a ladder step the project does not
 * have. If an unconfigured level ever appears again — through a project that genuinely configures it or
 * operator-authored content — the mechanism stands ready.
 *
 * They are not drawn, but they are not concealed either: `OFF_LADDER` marks them so the caller can state how
 * many exist and why they are absent. Omitting them silently would leave the canvas quietly claiming a build
 * holds fewer change requests than it does.
 */
export const OFF_LADDER = -1

export const laneModel = (orderedLevels: readonly string[] = DEFAULT_ORDERED_LEVELS): LaneModel => {
  const levels = orderedLevels.length ? [...orderedLevels] : [...DEFAULT_ORDERED_LEVELS]
  const laneForLevel = new Map<string, number>()
  levels.forEach((level, index) => laneForLevel.set(level, index + 1))
  return {
    labels: ["PROBLEM REPORT", ...levels.map(levelLaneLabel), "TEST CASE CHANGES", "TEST PROCEDURE CHANGES", "UNCLASSIFIED VERIFICATION CHANGE"],
    laneForLevel,
    problemLane: 0,
    caseLane: levels.length + 1,
    procedureLane: levels.length + 2,
    verificationLane: levels.length + 3,
  }
}

/**
 * Which lane a projection node belongs to, or `OFF_LADDER` when its level is not configured for the project.
 */
export const laneOf = (node: NetworkNode, model: LaneModel = laneModel()): number => {
  if (node.kind === "ProblemReport") return model.problemLane
  if (node.kind === "TestChangeRequest") return node.level === "Case" ? model.caseLane : node.level === "Procedure" ? model.procedureLane : model.verificationLane
  return model.laneForLevel.get(node.level ?? "") ?? OFF_LADDER
}

/** Records the ladder has no lane for, grouped by level, so the caller can say what is missing and why. */
export const offLadderLevels = (
  nodes: readonly NetworkNode[],
  model: LaneModel = laneModel(),
): { level: string; count: number }[] => {
  const counts = new Map<string, number>()
  for (const node of nodes) {
    if (laneOf(node, model) !== OFF_LADDER) continue
    const level = node.level ?? "Unknown"
    counts.set(level, (counts.get(level) ?? 0) + 1)
  }
  return [...counts.entries()]
    .map(([level, count]) => ({ level, count }))
    .sort((a, b) => a.level.localeCompare(b.level))
}

/**
 * How a verification record's recorded outcome reads (#1016 S13A).
 *
 * The outcome and the lifecycle state answer different questions — what the assessment concluded, and how far
 * that conclusion has got — and one must never be derived from the other. A recorded no-change conclusion in
 * Draft is not an approved no-change conclusion, so both are shown, separately, in the caller's own layout.
 */
export const outcomeLabel = (outcome?: string | null): string | undefined => {
  if (outcome === "Pending") return "Pending assessment"
  if (outcome === "ChangeRequired") return "Change required"
  if (outcome === "NoChangeRequired") return "No change required"
  return undefined
}

/**
 * The record a verification package was raised from, labelled as source context rather than as its identity.
 *
 * Interpreted by the origin discriminator, not by reading the source number's prefix: a Case-change origin
 * and a Case-assessment origin are different kinds of reference and neither is automatically a Case package.
 */
export const sourceContextLabel = (verification?: NetworkVerification | null): string | undefined => {
  const source = verification?.sourceDisplayNumber
  if (!source) return undefined
  const kind = verification?.originKind === "ProblemReport" ? "Problem Report"
    : verification?.originKind === "ChangeRequest" ? "change request"
      : verification?.originKind === "CaseChange" ? "case change"
        : verification?.originKind === "CaseAssessment" ? "case assessment"
          : verification?.originKind === "CaseReview" ? "case review"
            : undefined
  return kind ? `Assessing ${kind} ${source}` : `Assessing ${source}`
}

/** True when a test-change node holds no controlled number, from the server's answer rather than its label. */
export const isUnnumberedAssessment = (node: NetworkNode): boolean =>
  node.kind === "TestChangeRequest" && node.verification?.hasControlledNumber === false

/** The short square badge on a card. Says the level, which the identifier alone does not reliably carry. */
export const badgeOf = (node: NetworkNode): string => {
  if (node.kind === "ProblemReport") return "PR"
  // An assessment raised against an approved change is not a controlled test change request and must not
  // wear its badge: a reader scanning for TCRs would count work that has not been raised. The node kind,
  // its edges and its id are unchanged — this is the badge only.
  if (node.kind === "TestChangeRequest") return isUnnumberedAssessment(node) ? "ASMT" : "TCR"
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
      return { background: "#f3ebe2", color: "#765313" }
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

/**
 * True when the server has stated this edge is suspect.
 *
 * A plain read of a served fact, deliberately. An earlier version searched the relation and provenance status
 * text for the word "suspect", which reconstructed lifecycle meaning from display vocabulary in the browser:
 * it made any relation whose wording happened to contain the word read as suspect, and left a genuinely
 * suspect edge looking settled whenever its wording did not. Suspect state and relation vocabulary are
 * separate facts and are kept separate.
 */
export const isSuspectEdge = (edge: NetworkEdge): boolean => edge.isSuspect

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
    if (lane === OFF_LADDER) continue
    const bucket = perLane.get(lane)
    if (bucket) bucket.push(node)
    else perLane.set(lane, [node])
  }
  for (const bucket of perLane.values()) {
    bucket
      .slice()
      // Labels are no longer unique: two assessments raised from one source share a label by design. The
      // stable id is the tie-breaker, so the board keeps a deterministic order and neither row is dropped.
      .sort((a, b) => a.displayNumber.localeCompare(b.displayNumber) || a.id.localeCompare(b.id))
      .forEach((node, index) => rows.set(node.id, index))
  }
  return rows
}

/**
 * Whether a view's relation vocabulary can carry suspect lifecycle at all.
 *
 * A view capability, not a data accident. The change network renders ProblemReportResolution,
 * change-to-upstream-change and CoveredByTestChangeRequest between ChangeRequest, TestChangeRequest and
 * ProblemReport nodes. A suspect lifecycle governs only `RequirementTrace` and `CaseProcedure` links, so none
 * of those relations can ever be suspect — the answer is structural, not "zero today". A view that decided
 * this by counting suspect edges in the current response would show the control whenever a fixture happened
 * to contain one and hide it otherwise, which is a control that flickers rather than one that means something.
 *
 * The artifact thread (#880 §5.3) is the first planned view that renders requirement revisions and their
 * traces, so it is the first that may set this true. See #880 §10.2.
 */
export const supportsSuspectRelations = (relations: readonly string[]): boolean =>
  relations.some(relation => SUSPECT_CAPABLE_RELATIONS.has(relation))

/** The relation kinds an exact-link suspect lifecycle can attach to, mirroring `ExactLinkKind` on the server. */
export const SUSPECT_CAPABLE_RELATIONS: ReadonlySet<string> = new Set([
  "RequirementTrace",
  "CaseProcedure",
])

/**
 * The records a Suspect filter keeps: those at either end of a server-stated suspect relationship.
 *
 * Presentation filtering over a fact the server already decided, not lifecycle derivation. Nothing here reads
 * node state, an identifier prefix, relation wording, provenance wording, or a revision comparison — only
 * `edge.isSuspect`, which the projection states.
 */
export const suspectEndpointIds = (edges: readonly NetworkEdge[]): Set<string> => {
  const endpoints = new Set<string>()
  for (const edge of edges) {
    if (!edge.isSuspect) continue
    endpoints.add(edge.fromId)
    endpoints.add(edge.toId)
  }
  return endpoints
}

/**
 * The short level badge for a record whose level the server has stated.
 *
 * Shared so no view invents its own. An inline ternary in the inside-a-change view previously fell back to
 * `SYS` for anything it did not recognise, so a Customer or Interface record read as System — the exact
 * regression the ladder work in slice 3 existed to prevent. A level this table does not name still reads as
 * itself rather than as something else.
 */
export const levelBadge = (level: string | null | undefined): string => {
  switch (level) {
    case "HighLevel":
      return "HLR"
    case "LowLevel":
      return "LLR"
    case "Interface":
      return "IFC"
    case "Customer":
      return "CUS"
    case "System":
      return "SYS"
    default:
      return (level ?? "").slice(0, 3).toUpperCase() || "—"
  }
}
