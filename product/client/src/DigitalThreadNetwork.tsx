import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import DigitalThreadCanvas from "./DigitalThreadCanvas"
import DigitalThreadTable, {
  type DigitalThreadTableColumn,
  type DigitalThreadTableRow,
  type ThreadRepresentation,
} from "./DigitalThreadTable"
import { usePanelDock } from "./digitalThreadPanelDock"
import ExactArtifactLink from "./ExactArtifactLink"
import { type CanvasEdge, type CanvasNode, compactLanes, trace } from "./digitalThreadGeometry"
import { stateLabel } from "./presentation"
import { traceRelationLabel, traceRelationLabelFor } from "./tracePresentation"
import {
  OFF_LADDER,
  laneModel,
  levelLaneLabel,
  offLadderLevels,
  type NetworkNode,
  type NetworkProjection,
  assignRows,
  badgeOf,
  badgeTintFor,
  groupOf,
  isSuspectEdge,
  laneOf,
  pillFor,
  resolveDock,
} from "./changeNetworkPresentation"
import "./DigitalThreadNetwork.css"

/** Where the detail panel sits. `auto` picks the side with less linked content. */
export type PanelDock = "auto" | "left" | "right" | "bottom"

/** `auto` resolved to a real side. */
export type ResolvedDock = Exclude<PanelDock, "auto">

// Derived from what the panel actually renders, so the reserved free area is the panel plus its margins and
// not a guess with slack in it. Slack reads as correctness — the board simply never uses the spare band — while
// hiding whether the real rule holds. Right/left: 300px wide at a 16px offset. Bottom: 150px (#880 §6.6) at an
// 18px offset. The remainder in each case is the gap between the panel edge and the nearest card.
const PANEL_WIDTH = 300 + 16 + 14
const PANEL_HEIGHT = 150 + 18 + 16

export type DigitalThreadNetworkProps = {
  projection: NetworkProjection | null
  loading?: boolean
  error?: string | null
  onRetry?: () => void
  /**
   * The Project ladder, when the page already knows it.
   *
   * Only used before the projection arrives. Without it the skeleton falls back to the default ladder, which
   * carries Customer and Interface; FMS configures neither, so the first paint would show seven lanes and the
   * response would collapse it to five — the structural jump the loading rule exists to prevent. The page has
   * this from Project context already, so it is passed rather than fetched again.
   */
  orderedLevels?: readonly string[]
  /**
   * The record the address names, which the board must arrive already showing.
   *
   * #880 §4.4 is explicit that a deep link lands in the same state as if the reader had clicked the card
   * themselves: selected, its whole web traced, the detail panel open and its lane rolled into view. Merely
   * drawing the named card somewhere on the board is not arrival. Adopted only when this build's projection
   * actually contains it, so a record this build does not carry stays unselected rather than being guessed at.
   */
  focalId?: string
  /**
   * Selection owned by the Digital Thread page, so a network selection can become the exact Inside focal.
   * Omit it for the standalone/uncontrolled presentation used by older callers.
   */
  selectedId?: string | null
  onSelect?: (id: string | null) => void
  /** Exact route for a record, when the current workspace can open it. Absent renders non-openable. */
  hrefFor?: (node: NetworkNode) => string | undefined
  /** Opens the change inside its own view. Slice 4 supplies this. */
  onOpenChange?: (node: NetworkNode) => void
  buildLabel?: string
  representation?: ThreadRepresentation
}

type NetworkTableRow = DigitalThreadTableRow & { node: NetworkNode }

/**
 * The build change network: every change request in a build and every typed relation between them.
 *
 * Selecting a record traces its whole directed web — every hop down and every hop up — and pushes everything
 * untraced back rather than hiding it. The detail panel then docks where it will not cover a linked record.
 */
export default function DigitalThreadNetwork({
  projection,
  loading = false,
  error = null,
  onRetry,
  orderedLevels,
  focalId,
  selectedId: selectedIdProp,
  onSelect: onSelectProp,
  hrefFor,
  onOpenChange,
  buildLabel,
  representation = "map",
}: DigitalThreadNetworkProps) {
  const [uncontrolledSelectedId, setUncontrolledSelectedId] = useState<string | null>(null)
  const selectedId = selectedIdProp === undefined ? uncontrolledSelectedId : selectedIdProp
  const setSelectedId = useCallback(
    (id: string | null) => {
      if (selectedIdProp === undefined) setUncontrolledSelectedId(id)
      onSelectProp?.(id)
    },
    [onSelectProp, selectedIdProp],
  )
  useEffect(() => {
    if (selectedIdProp !== undefined) return
    if (!focalId) return
    // Membership decides. A well-formed id that belongs to nothing in this build must not select a card, and
    // must not select some *other* card either — the board says nothing rather than something wrong.
    setUncontrolledSelectedId(projection?.nodes.some(node => node.id === focalId) ? focalId : null)
  }, [focalId, projection, selectedIdProp])
  const [hoveredId, setHoveredId] = useState<string | null>(null)
  const [dockPreference, setDockPreference] = useState<PanelDock>("bottom")
  const [query, setQuery] = useState("")
  const [groups, setGroups] = useState<Set<string>>(new Set())
  const liveRegion = useRef<HTMLDivElement | null>(null)
  const canvasViewRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    const element = canvasViewRef.current
    if (!element) return
    if (representation === "table") {
      element.setAttribute("aria-hidden", "true")
      element.setAttribute("inert", "")
    } else {
      element.removeAttribute("aria-hidden")
      element.removeAttribute("inert")
    }
  }, [representation])

  // Memoised because `?? []` would hand every memo below a fresh array on each render and defeat all of them.
  const nodes = useMemo(() => projection?.nodes ?? [], [projection])
  const edges = useMemo(() => projection?.edges ?? [], [projection])
  const byId = useMemo(() => new Map(nodes.map(node => [node.id, node])), [nodes])

  /**
   * Lanes and rows, with structurally empty lanes dropped so none is ever displayed empty (#880 §5.2).
   *
   * Compaction is on real emptiness only. A lane emptied by the filter chips keeps its place — collapsing it
   * would slide every other lane sideways while the reader is mid-search.
   */
  // The projection is authoritative once it lands; the caller-supplied ladder only holds the frame until then.
  const model = useMemo(
    () => laneModel(projection?.orderedLevels ?? orderedLevels),
    [orderedLevels, projection?.orderedLevels],
  )

  // Records at a level this project does not configure get no lane. They are counted so the canvas can say
  // how many exist rather than quietly showing a smaller build than there is.
  const offLadder = useMemo(() => offLadderLevels(nodes, model), [model, nodes])

  /**
   * Structural compaction runs only once the content is known (#880 §6.8).
   *
   * While a build is still loading there are no nodes, so compacting would drop every lane and then put them
   * back as the response lands — the whole board jumping under the reader at the moment they start looking at
   * it. The lane bands and headers render immediately with counts unknown, and cards fade into them.
   */
  const contentKnown = !loading || nodes.length > 0

  const { lanes, canvasNodes } = useMemo(() => {
    if (!contentKnown) return { lanes: [...model.labels], canvasNodes: [] as CanvasNode[] }
    const rows = assignRows(nodes, model)
    const placed: CanvasNode[] = nodes
      .filter(node => laneOf(node, model) !== OFF_LADDER)
      .map(node => ({
        id: node.id,
        lane: laneOf(node, model),
        row: rows.get(node.id) ?? 0,
      }))
    const compacted = compactLanes(model.labels, placed)
    return { lanes: compacted.lanes, canvasNodes: compacted.nodes }
  }, [contentKnown, model, nodes])

  const canvasEdges = useMemo<CanvasEdge[]>(
    () =>
      edges.map(edge => ({
        from: edge.fromId,
        to: edge.toId,
        label: traceRelationLabel(edge.relation),
        kind: isSuspectEdge(edge) ? "suspect" : "",
      })),
    [edges],
  )

  /** The focus is what the reader is looking at: a selection if there is one, otherwise a hover. */
  const focusId = selectedId ?? hoveredId
  const web = useMemo(
    () => (focusId ? trace(focusId, canvasEdges) : null),
    [canvasEdges, focusId],
  )

  const selected = selectedId ? byId.get(selectedId) ?? null : null
  const directLinks = useMemo(
    () => (selectedId ? edges.filter(edge => edge.fromId === selectedId || edge.toId === selectedId) : []),
    [edges, selectedId],
  )

  const matchesFilters = useCallback(
    (node: NetworkNode): boolean => {
      // Level chips only. Suspect is a property of a relationship, not of these records, and no relation
      // this board draws can carry it.
      if (groups.size && !groups.has(groupOf(node))) return false
      if (query) {
        const haystack = `${node.displayNumber} ${node.title ?? ""}`.toLowerCase()
        if (!haystack.includes(query.toLowerCase())) return false
      }
      return true
    },
    [groups, query],
  )

  const tableRows = useMemo<NetworkTableRow[]>(
    () => canvasNodes
      .map(canvasNode => byId.get(canvasNode.id))
      .filter((node): node is NetworkNode => Boolean(node))
      .filter(matchesFilters)
      .map(node => ({ id: node.id, label: node.displayNumber, node })),
    [byId, canvasNodes, matchesFilters],
  )

  const tableRelations = useCallback(
    (node: NetworkNode, direction: "upstream" | "downstream") => {
      const relationEdges = edges.filter(edge =>
        direction === "upstream" ? edge.toId === node.id : edge.fromId === node.id)
      if (!relationEdges.length) return <em>None recorded</em>
      return relationEdges.map(edge => {
        const relatedId = direction === "upstream" ? edge.fromId : edge.toId
        const related = byId.get(relatedId)
        if (!related) return null
        const hop = web?.hops.get(related.id)
        return (
          <span key={`${edge.fromId}:${edge.toId}:${edge.relation}`}>
            <ExactArtifactLink href={hrefFor?.(related)}>{related.displayNumber}</ExactArtifactLink>
            <small>
              {traceRelationLabelFor(edge.relation, edge.fromId === related.id)}
              {hop && hop > 1 ? ` · ${hop} hops from selected` : ""}
            </small>
          </span>
        )
      })
    },
    [byId, edges, hrefFor, web],
  )

  const tableColumns = useMemo<readonly DigitalThreadTableColumn<NetworkTableRow>[]>(
    () => [
      {
        key: "change",
        label: "Change",
        render: row => (
          <>
            <ExactArtifactLink href={hrefFor?.(row.node)}>{row.node.displayNumber}</ExactArtifactLink>
            <span>{row.node.title ?? "Untitled change"}</span>
          </>
        ),
      },
      { key: "level", label: "Level", render: row => row.node.level ?? "Unclassified" },
      { key: "state", label: "State", render: row => stateLabel(row.node.state ?? undefined) },
      { key: "upstream", label: "Upstream", render: row => tableRelations(row.node, "upstream") },
      { key: "downstream", label: "Downstream", render: row => tableRelations(row.node, "downstream") },
      {
        key: "trace",
        label: "Trace context",
        render: row => {
          if (!selectedId) return <em>No record selected</em>
          const hop = web?.hops.get(row.id)
          return row.id === selectedId ? "Selected record" : hop ? `${hop} hop${hop === 1 ? "" : "s"}` : "Outside selected trace"
        },
      },
    ],
    [hrefFor, selectedId, tableRelations, web],
  )

  const tableTruncatedMessage = useMemo(() => {
    const messages: string[] = []
    if (offLadder.length) {
      messages.push(`${offLadder.reduce((sum, item) => sum + item.count, 0)} record${offLadder.reduce((sum, item) => sum + item.count, 0) === 1 ? " is" : "s are"} not shown because the project ladder does not configure ${offLadder.map(item => levelLaneLabel(item.level).toLowerCase()).join(" and ")}.`)
    }
    if (projection?.truncated) messages.push("This build carries more records than the network returns. Some changes and their links are not shown.")
    return messages.length ? messages.join(" ") : null
  }, [offLadder, projection?.truncated])

  /**
   * Which side the panel takes. It counts where the selected record's direct links actually are and docks on
   * the emptier one, so the panel is never covering the thing the highlighted edge points at.
   */
  const preferredDock: ResolvedDock = useMemo(() => {
    if (dockPreference !== "auto") return dockPreference
    return selected ? resolveDock(selected, directLinks, byId, model) : "right"
  }, [byId, directLinks, dockPreference, model, selected])

  // Non-occlusion outranks the preference (§6.6), the same contract the artifact thread keeps: when a side
  // cannot leave room for the selection and its direct links at the landing floor, the panel moves rather
  // than a linked record being hidden.
  const { dock, reportNeedsRoom } = usePanelDock(preferredDock, `${selectedId ?? ""}:${dockPreference}`)

  // The canvas must not lay records out under the panel, so the frame it may use shrinks by the dock.
  const frameInset = useMemo(
    () =>
      selectedId
        ? dock === "bottom"
          ? { bottom: PANEL_HEIGHT }
          : dock === "left"
            ? { left: PANEL_WIDTH }
            : { right: PANEL_WIDTH }
        : undefined,
    [dock, selectedId],
  )

  useEffect(() => {
    if (!selected || !liveRegion.current) return
    const up = directLinks.filter(edge => edge.toId === selected.id).length
    const down = directLinks.filter(edge => edge.fromId === selected.id).length
    liveRegion.current.textContent =
      `${selected.displayNumber}, ${stateLabel(selected.state ?? undefined)}. ` +
      `${up} upstream and ${down} downstream direct links.`
  }, [directLinks, selected])

  /**
   * A lane that holds records but is showing none of them, because the chips or the search hid them all.
   *
   * Per lane, not per board: with the SYS chip active the System lane is full while HLR and LLR are empty, so
   * a board-wide message would never appear and those two lanes would sit blank with nothing to explain them.
   */
  const laneNotice = useCallback(
    (lane: number): string | null => {
      const inLane = canvasNodes.filter(node => node.lane === lane)
      if (!inLane.length) return null
      const visible = inLane.filter(node => {
        const record = byId.get(node.id)
        return record ? matchesFilters(record) : false
      })
      return visible.length ? null : "No records match"
    },
    [byId, canvasNodes, matchesFilters],
  )

  const renderCard = useCallback(
    (canvasNode: CanvasNode) => {
      const node = byId.get(canvasNode.id)
      if (!node) return null
      const pill = pillFor(node.state)
      const tint = badgeTintFor(node)
      const hop = web?.hops.get(node.id)
      const traced = web?.nodes.has(node.id) ?? false
      const classes = [
        "dtnCard",
        selectedId === node.id ? "is-selected" : "",
        web && !traced ? "is-untraced" : "",
        matchesFilters(node) ? "" : "is-filtered",
      ]
        .filter(Boolean)
        .join(" ")
      return (
        <div className={classes}>
          {traced && hop ? (
            <span className="dtnHop" title={`${hop} hop${hop === 1 ? "" : "s"} from the selected record`}>
              {hop}
            </span>
          ) : null}
          <div className="dtnCardTop">
            <span className="dtnBadge" style={{ background: tint.background, color: tint.color }}>
              {badgeOf(node)}
            </span>
            <ExactArtifactLink href={hrefFor?.(node)} className="dtnId">
              {node.displayNumber}
            </ExactArtifactLink>
            <span className="dtnPill" style={{ background: pill.background, color: pill.color }}>
              {stateLabel(node.state ?? undefined)}
            </span>
          </div>
          <div className="dtnTitle" data-density="title">
            {node.title}
          </div>
          <div className="dtnMeta" data-density="meta">
            <span>{node.buildVersion ? `Build ${node.buildVersion}` : "No target build"}</span>
          </div>

          {/* The selected card expands in place (#880 §6.5), showing only rows it actually has. A record with
              no revision or no build simply does not show that row; nothing here is invented to fill the box,
              and the panel remains the place for the whole traced web. */}
          {selectedId === node.id ? (
            <div className="dtnCardBody">
              {node.level ? (
                <div className="dtnKv">
                  <i>Level</i>
                  <b>{node.level}</b>
                </div>
              ) : null}
              {node.revision !== null && node.revision !== undefined ? (
                <div className="dtnKv">
                  <i>Revision</i>
                  <b>{String(node.revision).padStart(2, "0")}</b>
                </div>
              ) : null}
              {node.buildVersion ? (
                <div className="dtnKv">
                  <i>In build</i>
                  <b>{node.buildVersion}</b>
                </div>
              ) : null}
              {node.kind !== "ProblemReport" && onOpenChange ? (
                <div className="dtnCardActs">
                  <button
                    type="button"
                    onClick={event => {
                      event.stopPropagation()
                      onOpenChange(node)
                    }}
                  >
                    Open this change
                  </button>
                </div>
              ) : null}
            </div>
          ) : null}
        </div>
      )
    },
    [byId, hrefFor, matchesFilters, onOpenChange, selectedId, web],
  )

  return (
    <div className="dtnRoot">
      <div className="dtnToolbar">
        <div className="dtnFilters" role="group" aria-label="Filter by level">
          {[
            ["sys", "System"],
            ["hlr", "HLR"],
            ["llr", "LLR"],
            ["ver", "Test"],
            // No Suspect chip here, and not because today's data happens to hold none. A suspect lifecycle
            // governs RequirementTrace and CaseProcedure links; this board renders ProblemReportResolution,
            // change-to-upstream-change and CoveredByTestChangeRequest between ChangeRequest,
            // TestChangeRequest and ProblemReport nodes, none of which those lifecycles attach to. The chip
            // could only ever return nothing, so it is absent rather than dead. See #880 section 10.2.
          ].map(([key, label]) => (
            <button
              type="button"
              key={key}
              aria-pressed={groups.has(key)}
              className={groups.has(key) ? "is-on" : ""}
              onClick={() =>
                setGroups(current => {
                  const next = new Set(current)
                  if (next.has(key)) next.delete(key)
                  else next.add(key)
                  return next
                })
              }
            >
              {label}
            </button>
          ))}
        </div>
        <label className="dtnSearch">
          <span className="dtnVisuallyHidden">Find an identifier</span>
          <input
            type="search"
            value={query}
            placeholder="Find an identifier"
            onChange={event => setQuery(event.target.value)}
          />
        </label>
      </div>

      {representation === "map" && offLadder.length ? (
        <p className="dtnTruncated" role="status">
          {offLadder
            .map(item => `${item.count} ${levelLaneLabel(item.level).toLowerCase()}`)
            .join(" and ")}{" "}
          {offLadder.reduce((sum, item) => sum + item.count, 0) === 1 ? "record is" : "records are"} not shown:
          this project&rsquo;s requirement ladder does not configure that level.
        </p>
      ) : null}

      {representation === "map" && projection?.truncated ? (
        <p className="dtnTruncated" role="status">
          This build carries more records than the network returns. Some change requests and their links are
          not shown.
        </p>
      ) : null}

      <div className="dtnStage">
        <div
          className={`dtnCanvasView${representation === "table" ? " is-hidden" : ""}`}
          ref={canvasViewRef}
        >
          <DigitalThreadCanvas
            lanes={lanes}
            nodes={canvasNodes}
            edges={canvasEdges}
            renderCard={renderCard}
            laneNotice={laneNotice}
            selectedId={selectedId}
            onSelect={setSelectedId}
            onHover={setHoveredId}
            frameInset={frameInset}
            onFramingNeedsRoom={reportNeedsRoom}
            tracedEdges={web?.edges}
            ariaLabel="Change network for this build"
          />
        </div>
        {representation === "table" ? (
          <DigitalThreadTable
            ariaLabel="Change network table"
            caption="Changes and their typed relationships in this build"
            columns={tableColumns}
            rows={tableRows}
            availableCount={canvasNodes.length}
            selectedId={selectedId}
            onSelect={setSelectedId}
            loading={loading}
            error={error}
            onRetry={onRetry}
            emptyMessage={`No change requests in ${buildLabel ?? "this build"}.`}
            selectionMessage="No record selected. Select a row to trace its relationships."
            truncatedMessage={tableTruncatedMessage}
            reservedInset={frameInset}
          />
        ) : null}
        {representation === "map" && loading ? <div className="dtnLoading">Loading the change network…</div> : null}

        {/* Every state below sits inside the frame rather than replacing it (#880 §6.8). Swapping the canvas
            out for a message discards the transform, the zoom and the selection, so recovering from a failed
            refresh would cost the reader the view they had built up. */}
        {representation === "map" && error ? (
          <div className="dtnInFrame dtnInFrame-error" role="alert">
            <b>The change network could not be loaded.</b>
            <p>{error}</p>
            {onRetry ? (
              <button type="button" onClick={onRetry}>
                Try again
              </button>
            ) : null}
          </div>
        ) : null}

        {representation === "map" && !loading && !error && projection && !nodes.length ? (
          <div className="dtnInFrame" role="status">
            <b>No change requests in {buildLabel ?? "this build"}.</b>
            <p>Change requests appear here as soon as one targets this build.</p>
          </div>
        ) : null}

        {/* Every lane empty at once earns a board-level line as well, because at that point the reader is
            looking at a board with nothing on it and needs the way out, not one label per lane. */}
        {representation === "map" && !loading && !error && nodes.length > 0 && !nodes.some(matchesFilters) ? (
          <div className="dtnInFrame" role="status">
            <b>No records match.</b>
            <p>Clear a filter chip or the search box to bring records back.</p>
          </div>
        ) : null}
      </div>

      <div className="dtnVisuallyHidden" aria-live="polite" ref={liveRegion} />

      {selected ? (
        <aside className={`dtnPanel dtnPanel-${dock}`} aria-label={`Detail for ${selected.displayNumber}`}>
          <div className="dtnPanelTools">
            {(["bottom", "right", "auto"] as PanelDock[]).map(option => (
              <button
                type="button"
                key={option}
                aria-pressed={dockPreference === option}
                className={dockPreference === option ? "is-on" : ""}
                onClick={() => setDockPreference(option)}
              >
                {option === "bottom" ? "Bottom" : option === "right" ? "Right" : "Auto"}
              </button>
            ))}
            <button type="button" onClick={() => setSelectedId(null)} aria-label="Close detail">
              ×
            </button>
          </div>
          <div className="dtnPanelGrid">
            <div>
              <p className="dtnEyebrow">SELECTED RECORD</p>
              <div className="dtnPanelIdentity">
                <ExactArtifactLink href={hrefFor?.(selected)}>{selected.displayNumber}</ExactArtifactLink>
                <span
                  className="dtnPill"
                  style={{ background: pillFor(selected.state).background, color: pillFor(selected.state).color }}
                >
                  {stateLabel(selected.state ?? undefined)}
                </span>
              </div>
              <h3>{selected.title}</h3>
            </div>
            {(["up", "down"] as const).map(direction => {
              const set = direction === "up" ? web?.up : web?.down
              const title = direction === "up" ? "UPSTREAM" : "DOWNSTREAM"
              const rows = Array.from(set ?? [])
                .map(id => ({ node: byId.get(id), hop: web?.hops.get(id) ?? 1 }))
                .filter((row): row is { node: NetworkNode; hop: number } => Boolean(row.node))
                .sort((a, b) => a.hop - b.hop || a.node.displayNumber.localeCompare(b.node.displayNumber))
              const relationFor = (id: string) => {
                // The edge between the listed record and the selection — not merely any edge touching
                // the listed record, which could name a relationship it has with a third record.
                const edge = directLinks.find(item =>
                  (item.fromId === id && item.toId === selected.id) ||
                  (item.toId === id && item.fromId === selected.id))
                // The listed record speaks in its own direction: an upstream parent reads "allocates
                // to" toward the selection, a downstream child reads "allocated from" (#925 V5).
                return edge ? traceRelationLabelFor(edge.relation, edge.fromId === id) : undefined
              }
              return (
                <div className="dtnPanelCol" key={direction}>
                  <div className="dtnRelHead">
                    <p className="dtnEyebrow">{title}</p>
                    <span>{rows.length} records, all hops</span>
                  </div>
                  <div className="dtnRel">
                    {rows.length ? (
                      rows.map(({ node, hop }) => (
                        <button
                          type="button"
                          key={node.id}
                          className={hop > 1 ? "is-far" : ""}
                          onClick={() => setSelectedId(node.id)}
                        >
                          <span>
                            <small>
                              {hop === 1
                                ? (relationFor(node.id) ?? "linked").toUpperCase()
                                : `${hop} HOPS`}
                            </small>
                            <span>{node.displayNumber}</span>
                          </span>
                          <em aria-hidden="true">›</em>
                        </button>
                      ))
                    ) : (
                      <p className="dtnRelEmpty">No recorded relationships</p>
                    )}
                  </div>
                </div>
              )
            })}
            <div className="dtnPanelActions">
              {selected.kind !== "ProblemReport" && onOpenChange ? (
                <button type="button" className="is-primary" onClick={() => onOpenChange(selected)}>
                  Open this change
                </button>
              ) : null}
            </div>
          </div>
        </aside>
      ) : null}
    </div>
  )
}
