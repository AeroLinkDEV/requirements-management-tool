import { useCallback, useMemo, useRef, useState } from "react"
import DigitalThreadCanvas from "./DigitalThreadCanvas"
import ExactArtifactLink from "./ExactArtifactLink"
import { type CanvasEdge, type CanvasNode, compactLanes, trace } from "./digitalThreadGeometry"
import { stateLabel } from "./presentation"
import { type NetworkNode, badgeOf, badgeTintFor, levelBadge, pillFor } from "./changeNetworkPresentation"
import {
  TYPE_FILTERS,
  TYPE_LABELS,
  type AllocationTarget,
  type ProposalContent,
  type ProposalItem,
  type ProposalReferenceGap,
  type RequirementCoverageTarget,
  type TypeFilter,
  type VerificationArtifactContent,
  type VerificationProposalItem,
  badgeForKind,
  diffFor,
  downstreamNotice,
  insideEdges,
  insideLaneLabels,
  isVerificationContent,
  matchesType,
  operationLabel,
} from "./changeProposalPresentation"
import "./DigitalThreadInsideChange.css"

/**
 * One record in the verification or build lane.
 *
 * These two lanes are read from the rooted trace rather than from proposal content — #880 §8.5 records that
 * `/api/change-requests/{id}/trace` already carries the verification and baseline-membership facts — so they
 * arrive as props rather than being fetched here. Absent means the lane is dropped, not that it is empty.
 */
export type InsideTraceRecord = {
  id: string
  displayNumber: string
  title?: string | null
  state?: string | null
  badge: string
  /**
   * The records this one covers, as the trace recorded them. Absent or empty draws no coverage edge at all,
   * which is the honest rendering: the alternative — assuming it covers everything in the lane beside it —
   * would make a verification artifact that covers one requirement indistinguishable from one that covers ten.
   */
  coversIds?: readonly string[]
}

export type DigitalThreadInsideChangeProps = {
  /** The opened change request, as the register knows it. */
  opened: NetworkNode
  /** Every change request in the build, for the rollable lane-0 register. */
  register: readonly NetworkNode[]
  content: ProposalContent | null
  /** Covering verification artifacts (lane 3), from the rooted trace. */
  verification?: readonly InsideTraceRecord[]
  /** Candidate-baseline selection and predecessor baseline (lane 4), from the rooted trace. */
  effect?: readonly InsideTraceRecord[]
  /** The project's configured ladder, highest layer first. Drives the level-aware lane labels. */
  orderedLevels?: readonly string[]
  /**
   * Whether the server has flagged the change request as a whole for rebase.
   *
   * A status about the change request, shown in the toolbar. It deliberately drives nothing per item: one
   * stale item strands the whole change request, and reading this as though every empty item were stale is
   * exactly the inference #880 §8.5.1 removed. Per-item meaning comes from each item's own `disposition`.
   */
  rebaseRequired?: boolean
  loading?: boolean
  error?: string | null
  onRetry?: () => void
  hrefFor?: (record: { id: string; displayNumber: string }) => string | undefined
  /** Opens a different change request in place, from the lane-0 register. */
  onOpenChange?: (node: NetworkNode) => void
  onBackToNetwork?: () => void
}

type Card =
  | { kind: "register"; node: NetworkNode }
  | { kind: "proposal"; item: ProposalItem }
  | { kind: "verification"; item: VerificationProposalItem }
  | { kind: "allocation"; target: AllocationTarget }
  | { kind: "coverage"; target: RequirementCoverageTarget }
  | { kind: "trace"; record: InsideTraceRecord }

/**
 * Inside one change: what it proposes, what that allocates to, what verifies it, and what it does to the build.
 *
 * The register stays on lane 0 and stays rollable, so moving between changes never means going back out to the
 * network first. Clicking another change opens it in place.
 */
export default function DigitalThreadInsideChange({
  opened,
  register,
  content,
  verification = [],
  effect = [],
  orderedLevels,
  rebaseRequired = false,
  loading = false,
  error = null,
  onRetry,
  hrefFor,
  onOpenChange,
  onBackToNetwork,
}: DigitalThreadInsideChangeProps) {
  const [typeFilter, setTypeFilter] = useState<TypeFilter>("all")
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [hoveredId, setHoveredId] = useState<string | null>(null)
  const liveRegion = useRef<HTMLDivElement | null>(null)

  const isTestChange = opened.kind === "TestChangeRequest"
  const labels = useMemo(
    () => insideLaneLabels(opened.level ?? "System", isTestChange, typeFilter, orderedLevels),
    [isTestChange, opened.level, orderedLevels, typeFilter],
  )

  /**
   * The two proposal shapes, kept apart.
   *
   * A requirement change proposes statements and allocates downward to requirements; a verification package
   * proposes cases and procedures and names the requirements they cover. They share an envelope and nothing
   * else, so the component branches on `ownerKind` rather than pretending one shape can render both.
   */
  const requirementItems = useMemo(
    () => (content && !isVerificationContent(content) ? content.items : []),
    [content],
  )
  const verificationItems = useMemo(
    () => (content && isVerificationContent(content) ? content.items : []),
    [content],
  )
  const itemCount = requirementItems.length + verificationItems.length

  /**
   * The register, filtered by the type chip.
   *
   * The opened change is kept whatever the chip says. Filtering it out of its own view would leave the reader
   * looking at proposal content with nothing on lane 0 to say which change it belongs to.
   */
  const registerNodes = useMemo(
    () => register.filter(node => node.id === opened.id || matchesType(node, typeFilter)),
    [opened.id, register, typeFilter],
  )

/**
   * Downstream records reached only through a Retire, so the cascade can be drawn dashed along its real path.
   *
   * A record reachable from both a Retire and a live proposal is deliberately excluded: dashing it would say
   * the artifact itself is being retired, which is a claim about its lifecycle rather than about this path.
   */
  const retireCascadeIds = useMemo(() => {
    const retiring = new Set<string>()
    const live = new Set<string>()
    for (const item of requirementItems)
      for (const target of item.allocatedDownstream)
        (item.kind === "Retire" ? retiring : live).add(target.id)
    for (const item of verificationItems)
      for (const target of item.finalCoverage)
        (item.kind === "Retire" ? retiring : live).add(target.revisionId)
    for (const id of live) retiring.delete(id)
    return retiring
  }, [requirementItems, verificationItems])

  /** Lane 2 for a requirement change: what its proposals allocate to, de-duplicated. */
  const allocations = useMemo(() => {
    const seen = new Map<string, AllocationTarget>()
    for (const item of requirementItems) {
      for (const target of item.allocatedDownstream) {
        if (!seen.has(target.id)) seen.set(target.id, target)
      }
    }
    return [...seen.values()]
  }, [requirementItems])

  /**
   * Lane 2 for a verification package: the requirements the successor covers, de-duplicated.
   *
   * `finalCoverage`, not `addedCoverage`. The added delta alone would show only what this proposal newly
   * drives and silently drop everything it retains, telling the reader that retained coverage had gone.
   */
  const coverage = useMemo(() => {
    const seen = new Map<string, RequirementCoverageTarget>()
    for (const item of verificationItems) {
      for (const target of item.finalCoverage) {
        if (!seen.has(target.revisionId)) seen.set(target.revisionId, target)
      }
    }
    return [...seen.values()]
  }, [verificationItems])

  /**
   * Why each item has nothing below it, keyed by item.
   *
   * The sentence comes from the server's per-item disposition. Nothing is inferred here from a null base
   * revision or from the change request's overall rebase flag — one stale item strands the whole change
   * request without making its siblings stale, and an unresolvable base is a gap in the record rather than
   * proof that a later revision carries the allocation.
   *
   * It belongs on the item card rather than in the downstream lane, because that lane is shared by every
   * proposed item and "why is this empty" has no single answer there.
   */
  const downstreamNotices = useMemo(() => {
    const notices = new Map<string, string>()
    for (const item of requirementItems) {
      const notice = downstreamNotice(item)
      if (notice) notices.set(item.id, notice)
    }
    return notices
  }, [requirementItems])

  const cards = useMemo(() => {
    const byId = new Map<string, Card>()
    for (const node of registerNodes) byId.set(node.id, { kind: "register", node })
    for (const item of requirementItems) byId.set(item.id, { kind: "proposal", item })
    for (const item of verificationItems) byId.set(item.id, { kind: "verification", item })
    for (const target of allocations) byId.set(target.id, { kind: "allocation", target })
    for (const target of coverage) byId.set(target.revisionId, { kind: "coverage", target })
    for (const record of verification) byId.set(record.id, { kind: "trace", record })
    for (const record of effect) byId.set(record.id, { kind: "trace", record })
    return byId
  }, [allocations, coverage, effect, registerNodes, requirementItems, verification, verificationItems])

  const { lanes, canvasNodes } = useMemo(() => {
    const placed: CanvasNode[] = []
    const laneLabels = [labels.register, labels.proposed, labels.allocated, labels.verification, labels.effect]

    registerNodes.forEach((node, row) => placed.push({ id: node.id, lane: 0, row }))
    requirementItems.forEach((item, row) => placed.push({ id: item.id, lane: 1, row }))
    verificationItems.forEach((item, row) => placed.push({ id: item.id, lane: 1, row }))
    allocations.forEach((target, row) => placed.push({ id: target.id, lane: 2, row }))
    coverage.forEach((target, row) => placed.push({ id: target.revisionId, lane: 2, row }))
    verification.forEach((record, row) => placed.push({ id: record.id, lane: 3, row }))
    effect.forEach((record, row) => placed.push({ id: record.id, lane: 4, row }))

    const compacted = compactLanes(laneLabels, placed)
    return { lanes: compacted.lanes, canvasNodes: compacted.nodes }
  }, [allocations, coverage, effect, labels, registerNodes, requirementItems, verification, verificationItems])

  /**
   * Edges: the opened change to each proposal, each proposal to what it allocates to, and each allocation to
   * what covers it. A retirement and everything it cascades to is drawn dashed.
   */
  const canvasEdges = useMemo<CanvasEdge[]>(() => {
    const present = new Set(canvasNodes.map(node => node.id))
    return insideEdges(opened.id, requirementItems, verificationItems, verification)
      .filter(edge => present.has(edge.from) && present.has(edge.to))
  }, [canvasNodes, opened.id, requirementItems, verification, verificationItems])

  /**
   * The focus is what the reader is looking at: a selection if there is one, otherwise a hover.
   *
   * Selection owns the trace once it exists, so moving the pointer afterwards cannot pull the web away from
   * the record the reader deliberately chose (#880 §6.5).
   */
  const focusId = selectedId ?? hoveredId
  const selectedCard = selectedId ? cards.get(selectedId) ?? null : null

  /**
   * The traced web, walked over the real edges this view drew.
   *
   * `trace` is the same directed traversal the change network uses — not a second graph algorithm, and never
   * an inference from lane adjacency or from an identifier.
   */
  const web = useMemo(() => (focusId ? trace(focusId, canvasEdges) : null), [canvasEdges, focusId])

  /**
   * Which side the panel takes, and how much frame it costs the board.
   *
   * The same non-occlusion principle as the network: the canvas must not lay records out underneath the
   * panel, so the frame it may use shrinks by the docked edge. Bottom by default, matching §6.6.
   */
  const frameInset = useMemo(
    () => (selectedId ? { bottom: PANEL_HEIGHT } : undefined),
    [selectedId],
  )

  const renderCard = useCallback(
    (canvasNode: CanvasNode) => {
      const card = cards.get(canvasNode.id)
      if (!card) return null

      // Untraced records recede rather than vanish, so the shape of the change stays readable around what
      // the reader selected. The hop badge says how far a record is from the focus.
      const hop = web?.hops.get(canvasNode.id)
      const traced = web?.nodes.has(canvasNode.id) ?? false
      const traceClass = web && !traced ? " is-untraced" : ""
      const hopBadge =
        traced && hop ? (
          <span className="dticHop" title={`${hop} hop${hop === 1 ? "" : "s"} from the selected record`}>
            {hop}
          </span>
        ) : null

      if (card.kind === "register") {
        const node = card.node
        const isOpen = node.id === opened.id
        const tint = badgeTintFor(node)
        const pill = pillFor(node.state)
        return (
          <div className={`dticCard dticRegister${isOpen ? " is-open" : ""}${traceClass}`}>
            {hopBadge}
            <div className="dticTop">
              <span className="dticBadge" style={{ background: tint.background, color: tint.color }}>
                {badgeOf(node)}
              </span>
              <ExactArtifactLink href={hrefFor?.(node)} className="dticId">
                {node.displayNumber}
              </ExactArtifactLink>
              <span className="dticPill" style={{ background: pill.background, color: pill.color }}>
                {stateLabel(node.state ?? undefined)}
              </span>
            </div>
            <div className="dticTitle" data-density="title">
              {node.title}
            </div>
            {isOpen ? <p className="dticMeta">Open in this view</p> : null}
          </div>
        )
      }

      if (card.kind === "proposal") {
        const item = card.item
        const diff = diffFor(item)
        const notice = downstreamNotices.get(item.id)
        return (
          <div className={`dticCard dticProposal${item.kind === "Retire" ? " is-retire" : ""}${traceClass}`}>
            {hopBadge}
            <div className="dticTop">
              <span className={`dticBadge dticOp dticOp-${item.kind.toLowerCase()}`}>
                {badgeForKind(item.kind)}
              </span>
              {/* A proposal is not a materialized artifact. Its id is a RequirementChange id, so an exact
                  artifact link built from it would point at something that does not exist yet. The identifier
                  is shown plainly; only a materialized record gets a link. */}
              <span className="dticId">{item.displayNumber}</span>
              <span className="dticOpWord">{operationLabel(item.kind)}</span>
            </div>
            <div className="dticTitle" data-density="title">
              {item.statement}
            </div>
            {diff ? (
              <div className="dticDiff" data-density="meta">
                <del>{diff.before}</del>
                <ins>{diff.after}</ins>
              </div>
            ) : null}
            {notice ? (
              <p className="dticNotice" data-density="meta">
                {notice}
              </p>
            ) : null}
          </div>
        )
      }

      if (card.kind === "verification") {
        const item = card.item
        const content = item.proposedContent
        const before = item.supersededContent
        return (
          <div className={`dticCard dticProposal${item.kind === "Retire" ? " is-retire" : ""}${traceClass}`}>
            {hopBadge}
            <div className="dticTop">
              <span className={`dticBadge dticOp dticOp-${item.kind.toLowerCase()}`}>
                {badgeForKind(item.kind)}
              </span>
              {/* A proposed case or procedure is not a controlled artifact yet, so no exact link is built. */}
              <span className="dticId">{item.displayNumber}</span>
              <span className="dticOpWord">{item.artifactKind}</span>
            </div>
            <div className="dticTitle" data-density="title">
              {content ? content.title : item.displayNumber}
            </div>

            {/* Structured verification content, not a flattened sentence. A procedure reviewer reads it by
                these parts, and the domain refuses a software Procedure proposal that has no environment. */}
            {content ? (
              <dl className="dticVerification" data-density="meta">
                {verificationRows(content).map(row => (
                  <div key={row.label}>
                    <dt>{row.label}</dt>
                    <dd>{row.value}</dd>
                  </div>
                ))}
              </dl>
            ) : (
              // A Retire proposes no successor body. Rendering an empty one would read as a procedure
              // emptied of its steps rather than one being withdrawn.
              <p className="dticNotice" data-density="meta">
                This {item.artifactKind.toLowerCase()} is being retired. No successor content is proposed.
              </p>
            )}

            {/* A Modify shows what it changes against the exact predecessor 4A resolves — every field, not
                just the steps. Comparing only `steps` reported "nothing changed" for a proposal that reworked
                the objective, the environment or the expected observations. */}
            {before && content && item.kind === "Modify"
              ? (() => {
                  const changes = verificationChanges(before, content)
                  if (!changes.length) return null
                  return (
                    <div className="dticDiff" data-density="meta">
                      {changes.map(row => (
                        <div className="dticDiffRow" key={row.label}>
                          <i>{row.label}</i>
                          <del>{row.before || "—"}</del>
                          <ins>{row.after || "—"}</ins>
                        </div>
                      ))}
                    </div>
                  )
                })()
              : null}

            {/* Exact parents, kept out of the coverage lists. For a software Procedure the parent is a Case
                revision; calling it requirement coverage would assert a relationship nobody recorded. An
                unresolved parent states no kind, because nothing located it. */}
            {item.exactParents.length ? (
              <p className="dticMeta" data-density="meta">
                {item.parentKind} from{" "}
                {item.exactParents
                  .map(parent =>
                    parent.resolved && parent.displayNumber
                      ? `${parent.displayNumber} (${parent.kind})`
                      : "an unresolved reference",
                  )
                  .join(", ")}
              </p>
            ) : null}

            {item.removedCoverage.length ? (
              <p className="dticNotice" data-density="meta">
                Stops covering {item.removedCoverage.map(x => x.displayNumber).join(", ")}.
              </p>
            ) : null}

            {item.referenceGaps.length ? (
              <p className="dticGap" data-density="meta">
                {gapNotice(item.referenceGaps)}
              </p>
            ) : null}
          </div>
        )
      }

      if (card.kind === "coverage") {
        const target = card.target
        const inCascade = retireCascadeIds.has(target.revisionId)
        return (
          <div className={`dticCard dticAllocation${inCascade ? " is-retire-cascade" : ""}${traceClass}`}>
            {hopBadge}
            <div className="dticTop">
              <span className="dticBadge dticLevel">{levelBadge(target.level)}</span>
              <ExactArtifactLink href={hrefFor?.({ id: target.artifactId, displayNumber: target.displayNumber })} className="dticId">
                {target.displayNumber}
              </ExactArtifactLink>
            </div>
            <div className="dticTitle" data-density="title">
              {target.statement}
            </div>
            <p className="dticMeta" data-density="meta">
              Covered by this package
            </p>
          </div>
        )
      }

      if (card.kind === "allocation") {
        const target = card.target
        const inCascade = retireCascadeIds.has(target.id)
        return (
          <div
            className={`dticCard dticAllocation${target.isProposed ? " is-proposed" : ""}${inCascade ? " is-retire-cascade" : ""}${traceClass}`}
          >
            {hopBadge}
            <div className="dticTop">
              {/* Level vocabulary comes from the shared helper, which knows the configured ladder. An inline
                  ternary here previously fell back to SYS, so an Interface or Customer record read as System. */}
              <span className="dticBadge dticLevel">{levelBadge(target.level)}</span>
              {target.isProposed ? (
                // Proposed downstream: the id belongs to another change request's proposal, not to a
                // controlled artifact, so it is rendered as text rather than as an exact link.
                <span className="dticId">{target.displayNumber || "Unnamed proposal"}</span>
              ) : (
                <ExactArtifactLink href={hrefFor?.(target)} className="dticId">
                  {target.displayNumber}
                </ExactArtifactLink>
              )}
              {/* Proposed and existing must never look alike: only one of them is in the build today. */}
              {target.isProposed ? <span className="dticProposedTag">Proposed</span> : null}
            </div>
            <div className="dticTitle" data-density="title">
              {target.statement}
            </div>
            <p className="dticMeta" data-density="meta">
              {target.isProposed
                ? `Under review in ${target.changeRequestDisplayNumber ?? "another change request"}`
                : target.linkType === "DerivedFrom"
                  ? "Derived from this requirement"
                  : "Allocated from this requirement"}
            </p>
          </div>
        )
      }

      const record = card.record
      const pill = pillFor(record.state)
      return (
        <div className={`dticCard dticTrace${record.state === "Suspect" ? " is-suspect" : ""}${traceClass}`}>
          {hopBadge}
          <div className="dticTop">
            <span className="dticBadge dticLevel">{record.badge}</span>
            <ExactArtifactLink href={hrefFor?.(record)} className="dticId">
              {record.displayNumber}
            </ExactArtifactLink>
            <span className="dticPill" style={{ background: pill.background, color: pill.color }}>
              {stateLabel(record.state ?? undefined)}
            </span>
          </div>
          <div className="dticTitle" data-density="title">
            {record.title}
          </div>
        </div>
      )
    },
    [cards, downstreamNotices, hrefFor, opened.id, retireCascadeIds, web],
  )

  const handleSelect = useCallback(
    (id: string | null) => {
      setSelectedId(id)
      if (!id) return
      const card = cards.get(id)
      // Clicking another change request on lane 0 opens it in place, rather than merely selecting it.
      if (card?.kind === "register" && card.node.id !== opened.id && onOpenChange) {
        onOpenChange(card.node)
        return
      }
      if (liveRegion.current && card) {
        // Exhaustive over the card union, so a new card kind cannot silently announce nothing.
        const label = ((): string => {
          switch (card.kind) {
            case "proposal":
              return `${card.item.displayNumber}, ${operationLabel(card.item.kind)}`
            case "verification":
              return `${card.item.displayNumber}, ${operationLabel(card.item.kind)} ${card.item.artifactKind}`
            case "allocation":
              return `${card.target.displayNumber}, ${card.target.isProposed ? "proposed" : "in the build"}`
            case "coverage":
              return `${card.target.displayNumber}, covered by this package`
            case "trace":
              return `${card.record.displayNumber}, ${stateLabel(card.record.state ?? undefined)}`
            case "register":
              return card.node.displayNumber
          }
        })()
        liveRegion.current.textContent = label
      }
    },
    [cards, onOpenChange, opened.id],
  )

  return (
    <div className="dticRoot">
      <div className="dticToolbar">
        {onBackToNetwork ? (
          <button type="button" className="dticBack" onClick={onBackToNetwork}>
            ‹ Back to the network
          </button>
        ) : null}
        <div className="dticTypes" role="group" aria-label="Filter the change register by type">
          {TYPE_FILTERS.map(filter => (
            <button
              type="button"
              key={filter}
              aria-pressed={typeFilter === filter}
              className={typeFilter === filter ? "is-on" : ""}
              onClick={() => setTypeFilter(filter)}
            >
              {TYPE_LABELS[filter]}
            </button>
          ))}
        </div>
        <p className="dticOpened">
          Inside <b>{opened.displayNumber}</b>
          {rebaseRequired ? <span className="dticRebase">Behind its target — rebase required</span> : null}
        </p>
      </div>

      <div className="dticStage">
        <DigitalThreadCanvas
          lanes={lanes}
          nodes={canvasNodes}
          edges={canvasEdges}
          renderCard={renderCard}
          selectedId={selectedId}
          onSelect={handleSelect}
          onHover={setHoveredId}
          tracedEdges={web?.edges}
          frameInset={frameInset}
          ariaLabel={`Inside ${opened.displayNumber}`}
        />
        {loading ? <div className="dticLoading">Loading what this change proposes…</div> : null}

        {/* Failure renders inside the frame rather than replacing it (#880 §6.8). Swapping the canvas out for
            a message discards the transform, the zoom, the lane offsets and the selection, so recovering from
            a failed refresh would cost the reader the view they had built. */}
        {error ? (
          <div className="dticInFrame dticInFrame-error" role="alert">
            <b>This change could not be opened.</b>
            <p>{error}</p>
            {onRetry ? (
              <button type="button" onClick={onRetry}>
                Try again
              </button>
            ) : null}
          </div>
        ) : null}

        {!loading && !error && content && itemCount === 0 ? (
          <div className="dticInFrame" role="status">
            <b>{emptyHeading(content)}</b>
          </div>
        ) : null}
      </div>

      {selectedCard ? (
        <aside className="dticPanel" aria-label={`Detail for ${panelTitle(selectedCard)}`}>
          <div className="dticPanelTools">
            <button type="button" onClick={() => setSelectedId(null)} aria-label="Close detail">
              ×
            </button>
          </div>
          <p className="dticEyebrow">SELECTED RECORD</p>
          <div className="dticPanelIdentity">{panelTitle(selectedCard)}</div>
          {panelRows(selectedCard).map(row => (
            <div className="dticKv" key={row.label}>
              <i>{row.label}</i>
              <b>{row.value}</b>
            </div>
          ))}
          <p className="dticPanelWeb">
            {web ? `${web.up.size} upstream and ${web.down.size} downstream, all hops.` : ""}
          </p>
        </aside>
      ) : null}

      <div className="dticVisuallyHidden" aria-live="polite" ref={liveRegion} />
    </div>
  )
}

const PANEL_HEIGHT = 150 + 18 + 16

/** The identity a panel names, taken from whichever card kind is selected. */
const panelTitle = (card: Card): string => {
  switch (card.kind) {
    case "register":
      return card.node.displayNumber
    case "proposal":
    case "verification":
      return card.item.displayNumber
    case "allocation":
      return card.target.displayNumber || "Unnamed proposal"
    case "coverage":
      return card.target.displayNumber
    case "trace":
      return card.record.displayNumber
  }
}

/** Facts the selected record actually has. Nothing is invented to fill the panel. */
const panelRows = (card: Card): { label: string; value: string }[] => {
  switch (card.kind) {
    case "register":
      return [
        { label: "Level", value: card.node.level ?? "" },
        { label: "State", value: stateLabel(card.node.state ?? undefined) },
      ].filter(row => row.value)
    case "proposal":
      return [
        { label: "Operation", value: operationLabel(card.item.kind) },
        { label: "Level", value: card.item.level },
        { label: "Downstream", value: card.item.disposition },
      ]
    case "verification":
      return [
        { label: "Operation", value: operationLabel(card.item.kind) },
        { label: "Artifact", value: card.item.artifactKind },
        { label: "Covers", value: String(card.item.finalCoverage.length) },
      ]
    case "allocation":
      return [
        { label: "Level", value: card.target.level },
        { label: "In the build", value: card.target.isProposed ? "Proposed" : "Yes" },
      ]
    case "coverage":
      return [{ label: "Level", value: card.target.level }]
    case "trace":
      return [{ label: "State", value: stateLabel(card.record.state ?? undefined) }]
  }
}

/** The loaded-and-empty sentence, in the vocabulary of whichever aggregate was opened. */
const emptyHeading = (content: ProposalContent): string =>
  isVerificationContent(content)
    ? "This test change request proposes no cases or procedures yet."
    : "This change request proposes no requirement changes yet."


/** The parts of a verification body worth showing on a card, skipping the ones this artifact does not use. */
const VERIFICATION_FIELDS: { key: keyof VerificationArtifactContent; label: string }[] = [
  { key: "title", label: "Title" },
  { key: "objective", label: "Objective" },
  { key: "preconditions", label: "Preconditions" },
  { key: "steps", label: "Steps" },
  { key: "orderedSteps", label: "Ordered steps" },
  { key: "expectedResult", label: "Expected result" },
  { key: "expectedObservations", label: "Expected observations" },
  { key: "environmentSetup", label: "Environment" },
  { key: "testData", label: "Test data" },
  { key: "cleanup", label: "Cleanup" },
  { key: "toolingAutomation", label: "Tooling" },
]

/**
 * The parts of a verification body worth showing, one row per field the artifact actually uses.
 *
 * Each of the eleven fields stands on its own. An earlier version collapsed `orderedSteps || steps` and
 * `expectedObservations || expectedResult`, which silently discarded one of each pair whenever both were
 * populated — the reader would have seen a procedure that said less than the record holds. Genuinely empty
 * fields are omitted; populated ones are never merged.
 */
const verificationRows = (content: VerificationArtifactContent): { label: string; value: string }[] =>
  VERIFICATION_FIELDS.map(field => ({ label: field.label, value: (content[field.key] ?? "").trim() })).filter(
    row => row.value.length > 0,
  )

/**
 * What a Modify actually changes, field by field against its exact predecessor.
 *
 * Structured rather than a text diff: the record is structured, and comparing whole bodies as prose would
 * report a moved sentence as a rewrite. Only fields that differ are listed — an unchanged field repeated as
 * "before → after" is noise that hides the change.
 */
const verificationChanges = (
  before: VerificationArtifactContent,
  after: VerificationArtifactContent,
): { label: string; before: string; after: string }[] =>
  VERIFICATION_FIELDS.map(field => ({
    label: field.label,
    before: (before[field.key] ?? "").trim(),
    after: (after[field.key] ?? "").trim(),
  })).filter(row => row.before !== row.after)

/**
 * What a card says about relationships the record names but the server could not resolve.
 *
 * Shown rather than dropped: a traceability surface that omits a named reference reports an absence nobody
 * established. A malformed list names no identity at all, so it says so instead of inventing one.
 */
const gapNotice = (gaps: readonly ProposalReferenceGap[]): string => {
  const malformed = gaps.filter(gap => gap.reason === "MalformedReferenceList")
  const unresolved = gaps.filter(gap => gap.reason === "UnresolvedReference")
  const parts: string[] = []
  if (unresolved.length)
    parts.push(
      `${unresolved.length} recorded ${unresolved.length === 1 ? "reference" : "references"} could not be resolved in this project`,
    )
  if (malformed.length)
    parts.push(
      `${malformed.length} stored relationship ${malformed.length === 1 ? "list" : "lists"} could not be read`,
    )
  return `${parts.join("; ")}.`
}
