/**
 * Presentation for the inside-a-change view (#880 §5.2).
 *
 * Lane labels are level-aware and ladder-driven: what sits below a System change is whatever the project
 * configures below System, not a hard-coded "HLR". Nothing here parses an identifier for meaning — the server
 * states `level` and `kind`, and deciding either from a display-number prefix would rebuild lifecycle meaning
 * in the browser, which the projection exists to prevent.
 */

import { DEFAULT_ORDERED_LEVELS } from "./changeNetworkPresentation"

/** One downstream target of a proposed item, as returned by /api/change-requests/{id}/proposal-content. */
export type AllocationTarget = {
  id: string
  displayNumber: string
  level: string
  statement: string
  /** True when the target is itself only proposed in another change request, rather than in the build today. */
  isProposed: boolean
  linkType?: string | null
  changeRequestId?: string | null
  changeRequestDisplayNumber?: string | null
}

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
}

export type ProposalContent = {
  changeRequestId: string
  projectId: string
  displayNumber: string
  items: ProposalItem[]
}

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
 * Why a proposal's downstream lane is empty — the three cases are different facts and must not look alike.
 *
 * `behindTarget` is the one that matters and the one #880 §8.5 did not anticipate. A materialized requirement
 * may only name an *active* parent revision, so once a proposal's target revises, nothing is permitted to hang
 * off the revision that proposal superseded. Its downstream lane is then empty because the allocation moved to
 * a later revision, not because no allocation exists. Rendering that identically to a genuine absence tells
 * the reader coverage is missing when it is merely elsewhere.
 *
 * `notYetExisting` is the Introduce case: nothing can allocate to a requirement that is not in the build.
 */
export type DownstreamState = "allocated" | "none" | "behindTarget" | "notYetExisting"

export const downstreamState = (item: ProposalItem, rebaseRequired = false): DownstreamState => {
  if (item.allocatedDownstream.length > 0) return "allocated"
  if (item.kind === "Introduce") return "notYetExisting"
  // A Modify or Retire whose base revision did not resolve, in a change request the server has flagged for
  // rebase, is behind its target rather than genuinely unallocated.
  if (rebaseRequired || item.baseRevisionId === null || item.baseRevisionId === undefined) return "behindTarget"
  return "none"
}

/** The sentence a lane shows in place of cards, so an empty lane still says something true. */
export const downstreamNotice = (state: DownstreamState): string | null => {
  switch (state) {
    case "notYetExisting":
      return "Nothing allocates to this yet — it does not exist in the build until the change is applied."
    case "behindTarget":
      return "This proposal is behind its target. What is allocated hangs off a later revision, so nothing is shown against the revision it supersedes."
    case "none":
      return "Nothing is allocated below this requirement."
    default:
      return null
  }
}

/**
 * Lane contents for one opened change, with structurally empty lanes dropped and the gap closed (§5.2).
 *
 * Dropping happens here rather than in the canvas so the labels and the rows can never disagree about which
 * lane is which. A lane holding only a notice is kept: it has something to say.
 */
export type InsideLane = { label: string; key: string; count: number }

export const compactInsideLanes = (
  labels: InsideLaneLabels,
  counts: { register: number; proposed: number; allocated: number; verification: number; effect: number },
  keepAllocatedNotice = false,
): InsideLane[] =>
  [
    { label: labels.register, key: "register", count: counts.register },
    { label: labels.proposed, key: "proposed", count: counts.proposed },
    {
      label: labels.allocated,
      key: "allocated",
      count: counts.allocated || (keepAllocatedNotice ? 1 : 0),
    },
    { label: labels.verification, key: "verification", count: counts.verification },
    { label: labels.effect, key: "effect", count: counts.effect },
  ].filter(lane => lane.count > 0)
