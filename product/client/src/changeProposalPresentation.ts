/**
 * Presentation for the inside-a-change view (#880 §5.2).
 *
 * Lane labels are level-aware and ladder-driven: what sits below a System change is whatever the project
 * configures below System, not a hard-coded "HLR". Nothing here parses an identifier for meaning — the server
 * states `level` and `kind`, and deciding either from a display-number prefix would rebuild lifecycle meaning
 * in the browser, which the projection exists to prevent.
 */

import { DEFAULT_ORDERED_LEVELS } from "./changeNetworkPresentation"

/** One downstream target of a proposed item, as `/api/change-requests/{id}/proposal-content` returns it. */
export type AllocationTarget = {
  id: string
  displayNumber: string
  level: string
  statement: string
  /** True when the target is itself only proposed in another change request, rather than in the build today. */
  isProposed: boolean
  /**
   * The exact revision a materialized target is at; null for a proposal, which has no controlled revision.
   *
   * This is what lane 3 joins on. The artifact id above answers "which requirement", this answers "which
   * revision of it", and coverage is recorded against the second.
   */
  revisionId?: string | null
  linkType?: string | null
  changeRequestId?: string | null
  changeRequestDisplayNumber?: string | null
}

/**
 * Why a proposed item has nothing allocated below it. **Stated by the server**, never derived here.
 *
 * The client used to work this out from a null `baseRevisionId` and the change request's overall rebase flag.
 * Both were wrong: an unresolvable base is a gap in the record, not proof that a later revision carries the
 * allocation, and one stale item strands the whole change request without saying anything about its siblings.
 */
export type DownstreamDisposition =
  | "Allocated"
  | "TargetNotYetCreated"
  | "NoAllocationRecorded"
  | "BehindTarget"
  | "BaseRevisionUnresolved"

export type ProposalItem = {
  id: string
  displayNumber: string
  level: string
  kind: string
  statement: string
  /** Null, never empty, when there is no before text. See `diffFor`. */
  supersededStatement?: string | null
  supersededRevision?: number | null
  baseRevisionId?: string | null
  allocatedDownstream: AllocationTarget[]
  disposition: DownstreamDisposition
  /** The highest revision number this base number has. A number, not a lifecycle judgement. */
  latestRevision?: number | null
  /** Active | Superseded | Retired — travels with the number so a bare maximum cannot imply the record is live. */
  latestRevisionState?: string | null
}

export type RequirementProposalContent = {
  ownerKind: "ChangeRequest"
  changeRequestId: string
  projectId: string
  displayNumber: string
  items: ProposalItem[]
  /** Lane 3: what covers the requirement revisions this change allocates to. */
  covering: CoveringArtifact[]
  /** Lane 4: the candidate baseline this content sits in, and the one it supersedes. */
  buildEffect: BaselineEffect[]
}

/**
 * The body of a proposed verification artifact.
 *
 * Structured, because a procedure is not a sentence. Flattening these into one statement to reuse the
 * requirement card would destroy what a reviewer reads it by — and the domain itself refuses a software
 * Procedure proposal that has no environment/setup.
 */
export type VerificationArtifactContent = {
  title: string
  objective: string
  preconditions: string
  steps: string
  expectedResult: string
  environmentSetup?: string
  testData?: string
  orderedSteps?: string
  expectedObservations?: string
  cleanup?: string
  toolingAutomation?: string
}

/** One requirement revision a verification proposal names. Membership of a list states its meaning. */
export type RequirementCoverageTarget = {
  revisionId: string
  artifactId: string
  displayNumber: string
  level: string
  statement: string
}

/**
 * An exact parent of a verification proposal.
 *
 * `kind` is null when unresolved, and that is not the same as the gap's `expectedKind`: what the package
 * expected to find and what the object actually is are different claims. Nothing may be rendered from the
 * expectation as though it were identity.
 */
export type VerificationParentTarget = {
  revisionId: string
  kind: "Requirement" | "Case" | null
  resolved: boolean
  displayNumber?: string | null
  level?: string | null
  artifactId?: string | null
}

/** A recorded relationship the server could not resolve. Never dropped, never given invented identity. */
export type ProposalReferenceGap = {
  /** Null when the stored list itself could not be parsed — there is no identity to name. */
  revisionId: string | null
  role: "ExactParent" | "AddedCoverage" | "RemovedCoverage"
  expectedKind: "Requirement" | "Case"
  reason: "UnresolvedReference" | "MalformedReferenceList"
}

export type VerificationProposalItem = {
  id: string
  displayNumber: string
  level: string
  artifactKind: "Case" | "Procedure"
  kind: string
  /** Null on a Retire: it proposes no successor body. */
  proposedContent: VerificationArtifactContent | null
  supersededRevision?: number | null
  baseRevisionId?: string | null
  supersededContent?: VerificationArtifactContent | null
  /** The complete successor coverage selection: retained + added − removed. */
  finalCoverage: RequirementCoverageTarget[]
  addedCoverage: RequirementCoverageTarget[]
  removedCoverage: RequirementCoverageTarget[]
  parentKind: string
  exactParents: VerificationParentTarget[]
  referenceGaps: ProposalReferenceGap[]
}

export type VerificationProposalContent = {
  ownerKind: "TestChangeRequest"
  ownerId: string
  projectId: string
  releaseId: string
  displayNumber: string
  discipline: string
  artifactKind: "Case" | "Procedure"
  items: VerificationProposalItem[]
  /** Lane 3: recorded runs of the exact predecessor revisions this package changes. */
  executions: VerificationExecution[]
  /** Lane 4: the candidate baseline this package's build carries, and the one it supersedes. */
  buildEffect: BaselineEffect[]
}

/**
 * What a change proposes, discriminated by the aggregate it came from.
 *
 * Two resources, two shapes. The client knows the record kind from the network projection and asks the right
 * one; it never probes both or falls back across aggregates.
 */
export type ProposalContent = RequirementProposalContent | VerificationProposalContent

export const isVerificationContent = (
  content: ProposalContent,
): content is VerificationProposalContent => content.ownerKind === "TestChangeRequest"

/** The badge on a proposal card. Introduce / Modify / Retire, in the wording the design settled on. */
export const badgeForKind = (kind: string): string => {
  switch (kind) {
    case "Introduce":
      return "NEW"
    case "Modify":
      return "MOD"
    case "Retire":
      return "RET"
    default:
      return "—"
  }
}

/** The word beneath the badge. `stateLabel` in presentation.ts owns state wording; this is operation wording. */
export const operationLabel = (kind: string): string =>
  kind === "Introduce" ? "Introduce" : kind === "Modify" ? "Modify" : kind === "Retire" ? "Retire" : kind

/**
 * The plural noun a level is known by in a lane header.
 *
 * A level the ladder configures but this table does not name still reads sensibly — `MyLayer` becomes
 * "MY LAYER REQUIREMENTS" — so a project configuring a new layer needs no client change.
 */
export const LEVEL_NOUNS: Record<string, string> = {
  Customer: "CUSTOMER REQUIREMENTS",
  Interface: "INTERFACE REQUIREMENTS",
  System: "SYSTEM REQUIREMENTS",
  HighLevel: "HLRs",
  LowLevel: "LLRs",
}

export const levelNoun = (level: string): string =>
  LEVEL_NOUNS[level] ?? `${level.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toUpperCase()} REQUIREMENTS`

/** The verification vocabulary for a level, which differs by tier rather than by name. */
const VERIFICATION_NOUNS: Record<string, string> = {
  System: "SYSTEM PROCEDURES",
  HighLevel: "HLR CASES AND PROCEDURES",
  LowLevel: "LLR CASES AND PROCEDURES",
}

export const verificationNoun = (level: string): string =>
  VERIFICATION_NOUNS[level] ?? `${levelNoun(level)} PROCEDURES`

/**
 * The level directly below this one on the project's ladder, or null at the bottom.
 *
 * This is what makes the "ALLOCATED …" lane label correct for a project whose ladder is not the default one.
 * A System change in a project configuring `[Customer, System, HighLevel, LowLevel]` allocates to HLRs; in a
 * project configuring `[System, LowLevel]` the same change allocates to LLRs, and the header must say so.
 */
export const levelBelow = (
  level: string,
  orderedLevels: readonly string[] = DEFAULT_ORDERED_LEVELS,
): string | null => {
  const index = orderedLevels.indexOf(level)
  if (index < 0 || index + 1 >= orderedLevels.length) return null
  return orderedLevels[index + 1]
}

export type InsideLaneLabels = {
  register: string
  proposed: string
  allocated: string
  verification: string
  effect: string
}

/**
 * Lane headers for a change of a given level, per §5.2.
 *
 * A test change reads in verification vocabulary throughout: it proposes cases and procedures, and what sits
 * "below" it is the requirements it covers rather than a further ladder step. That inversion is why this is a
 * branch rather than a lookup.
 */
export const insideLaneLabels = (
  level: string,
  isTestChange: boolean,
  activeType: TypeFilter = "all",
  orderedLevels: readonly string[] = DEFAULT_ORDERED_LEVELS,
): InsideLaneLabels => {
  const register = activeType === "all" ? "CHANGE REQUEST" : `CHANGE REQUEST · ${TYPE_LABELS[activeType]}`
  if (isTestChange) {
    return {
      register,
      proposed: "PROPOSED CASES AND PROCEDURES",
      allocated: "REQUIREMENTS COVERED",
      verification: "EXECUTIONS",
      effect: "EFFECT ON THE BUILD",
    }
  }
  const below = levelBelow(level, orderedLevels)
  return {
    register,
    proposed: `PROPOSED ${levelNoun(level)}`,
    // At the bottom of the ladder nothing is allocated below, so the lane is named for what it would hold and
    // then dropped as empty rather than being labelled for a ladder step the project does not have.
    allocated: below ? `ALLOCATED ${levelNoun(below)}` : "ALLOCATED DOWNSTREAM",
    verification: verificationNoun(level),
    effect: "EFFECT ON THE BUILD",
  }
}

export type TypeFilter = "all" | "sys" | "hlr" | "llr" | "test"

export const TYPE_LABELS: Record<TypeFilter, string> = {
  all: "All",
  sys: "SYS",
  hlr: "HLR",
  llr: "LLR",
  test: "TEST",
}

export const TYPE_FILTERS: TypeFilter[] = ["all", "sys", "hlr", "llr", "test"]

/**
 * Whether a register record answers the active type chip.
 *
 * Layers above System answer the SYS chip, matching `groupOf` in the network view: the chip means system-level
 * change control, and a project configuring Customer or Interface should not find those records vanish when it
 * is used.
 */
export const matchesType = (
  record: { kind?: string | null; level?: string | null },
  filter: TypeFilter,
): boolean => {
  if (filter === "all") return true
  if (record.kind === "TestChangeRequest") return filter === "test"
  if (filter === "test") return false
  if (record.level === "HighLevel") return filter === "hlr"
  if (record.level === "LowLevel") return filter === "llr"
  return filter === "sys"
}

export type Diff = { before: string; after: string }

/**
 * The before/after for a proposal card, or null when there is none to show.
 *
 * Only a Modify gets a diff. The server sends `supersededStatement` as null rather than empty when no before
 * text exists, and the distinction is load-bearing: an empty string rendered as a diff would show every word
 * of the statement as added and assert the author rewrote the requirement from scratch. A null here means the
 * card shows its statement plainly instead.
 */
export const diffFor = (item: ProposalItem): Diff | null => {
  if (item.kind !== "Modify") return null
  const before = item.supersededStatement
  if (before === null || before === undefined) return null
  return { before, after: item.statement }
}

/**
 * The sentence an item shows when nothing is allocated below it.
 *
 * Keyed on the server's stated disposition. The client used to decide this itself, reading a null
 * `baseRevisionId` as "behind its target, so the allocation hangs off a later revision" — a claim about
 * traceability that an unresolvable base does not support — and reading the change request's overall rebase
 * flag as proof that every empty item was stale. Both are gone; the server states it per item.
 *
 * The BehindTarget wording says only what the disposition proves: the proposal names an older revision than
 * the Project holds. It does not claim an allocation exists on the later one, because nothing looked there.
 */
export const downstreamNotice = (item: ProposalItem): string | null => {
  switch (item.disposition) {
    case "Allocated":
      return null
    case "TargetNotYetCreated":
      return "Nothing allocates to this yet — it does not exist in the build until the change is applied."
    case "BehindTarget": {
      const named = item.supersededRevision
      const latest = item.latestRevision
      const state = item.latestRevisionState ? ` (${item.latestRevisionState.toLowerCase()})` : ""
      return named !== null && named !== undefined && latest !== null && latest !== undefined
        ? `This proposal targets revision ${pad(named)}; the requirement is now at revision ${pad(latest)}${state}. Nothing is shown against the revision it names.`
        : "This proposal targets an older revision than the requirement now has. Nothing is shown against the revision it names."
    }
    case "BaseRevisionUnresolved":
      return "The revision this proposal names could not be resolved, so what sits below it is unknown."
    case "NoAllocationRecorded":
      return "Nothing is allocated below this requirement."
    default:
      return null
  }
}

const pad = (revision: number): string => String(revision).padStart(2, "0")

/**
 * Where the downstream explanation belongs.
 *
 * Not in the downstream lane. That lane is shared by every proposed item in the change, so "why is this
 * empty" has no single answer there: one item can be behind its target while another simply has nothing
 * below it. The sentence is per-item, so it renders on the item card, and the lane itself is dropped when
 * genuinely empty like any other. An earlier draft of this module carried a lane-level helper for it; it
 * could never have been correct, and the component rightly never used it.
 */

/**
 * One verification artifact covering an exact requirement revision — lane 3 for a requirement change.
 *
 * Served by the requirement proposal resource, which reads the single coverage definition the release gate
 * and the requirements workspace already use. `coverageState` is the server's; the browser never decides it.
 */
export type CoveringArtifact = {
  requirementRevisionId: string
  artifactId: string
  artifactRevisionId: string
  displayNumber: string
  title: string
  level: string
  artifactKind: string
  artifactState: string
  coverageState: string
}

/**
 * One recorded run of a procedure revision — lane 3 for a verification package.
 *
 * A test change's lane 3 is EXECUTIONS, not covering artifacts: a requirement change asks "what verifies
 * this?", a test change asks "what happened when this was run?".
 */
export type VerificationExecution = {
  id: string
  procedureRevisionId: string
  outcome: string
  executedBy: string
  executedAt: string
  determination: string
}

/** One baseline this change's content sits in — lane 4, from real candidate/predecessor baseline records. */
export type BaselineEffect = {
  baselineId: string
  displayNumber: string
  name: string
  state: string
  isPredecessor: boolean
}

export type InsideEdge = { from: string; to: string; label: string; kind: "" | "retire" | "suspect" }

/**
 * Every edge inside one change: the change to each proposal, each proposal to what it allocates to, and each
 * covering record to what it actually covers.
 *
 * The coverage rule is the reason this is a function rather than a loop in the component. Pairing every
 * allocation with every verification artifact produces a fuller-looking picture that is a lie: it makes a
 * requirement three procedures cover indistinguishable from one nothing covers, and asserts relationships the
 * record never held. #880 §8.6 forbids exactly that. A covering record that names nothing draws no edge.
 */
export const insideEdges = (
  openedId: string,
  items: readonly ProposalItem[],
  verificationItems: readonly VerificationProposalItem[] = [],
  covering: readonly CoveringArtifact[] = [],
  executions: readonly VerificationExecution[] = [],
): InsideEdge[] => {
  const edges: InsideEdge[] = []
  for (const item of items) {
    const retiring = item.kind === "Retire"
    edges.push({
      from: openedId,
      to: item.id,
      label: operationLabel(item.kind).toLowerCase(),
      kind: retiring ? "retire" : "",
    })
    for (const target of item.allocatedDownstream) {
      // A materialized target is identified on the canvas by its exact revision, because that is what lane 3
      // joins to. A proposal has no controlled revision and keeps its own identity.
      edges.push({
        from: item.id,
        to: target.revisionId ?? target.id,
        label: retiring ? "retire cascade" : "allocates to",
        kind: retiring ? "retire" : "",
      })
    }
  }

  // A verification proposal covers requirement revisions. The edge is drawn to the revision the record names,
  // by its exact revision identity — never by display number and never by lane adjacency.
  for (const item of verificationItems) {
    const retiring = item.kind === "Retire"
    edges.push({
      from: openedId,
      to: item.id,
      label: operationLabel(item.kind).toLowerCase(),
      kind: retiring ? "retire" : "",
    })
    for (const target of item.finalCoverage) {
      edges.push({
        from: item.id,
        to: target.revisionId,
        label: retiring ? "retire cascade" : "covers",
        kind: retiring ? "retire" : "",
      })
    }
  }

  // Lane 2 to lane 3, joined on the exact requirement revision the server recorded coverage against. Not a
  // display number, not lane adjacency, and not the artifact id — coverage is recorded per revision, so two
  // revisions of one requirement can and must be told apart.
  //
  // The state travels on the edge because it is a fact about the relationship. One procedure revision can
  // cover two requirements with different coverage states; hanging that on the procedure card would make the
  // node arbitrarily settled or suspect depending on which row was read last.
  for (const record of covering) {
    edges.push({
      from: record.requirementRevisionId,
      to: record.artifactRevisionId,
      label: record.coverageState === "Suspect" ? "suspect coverage" : "covered by",
      kind: record.coverageState === "Suspect" ? "suspect" : "",
    })
  }

  // For a verification package, lane 3 is what was run. The edge is drawn from the exact predecessor revision
  // the execution names, so a run of a different revision of the same procedure is never shown as evidence
  // for this one.
  for (const execution of executions) {
    edges.push({
      from: execution.procedureRevisionId,
      to: execution.id,
      label: "executed",
      kind: "",
    })
  }
  return edges
}

/**
 * One card per exact verification artifact revision, from rows the server returns per coverage link.
 *
 * A single procedure revision can cover several requirement revisions, so the coverage read returns it once
 * per link. Placing every row on the board would put the same revision on the canvas more than once under one
 * node id, and keying a map by that id would let the last row silently overwrite the others — taking its
 * coverage state with it. The relationships are kept whole as edges; this is only the node set.
 */
export const coveringNodes = (covering: readonly CoveringArtifact[]): CoveringArtifact[] => {
  const byRevision = new Map<string, CoveringArtifact>()
  for (const record of covering) {
    if (!byRevision.has(record.artifactRevisionId)) byRevision.set(record.artifactRevisionId, record)
  }
  return [...byRevision.values()]
}
