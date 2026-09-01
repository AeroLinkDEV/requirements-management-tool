import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import DigitalThreadCanvas from "./DigitalThreadCanvas"
import ExactArtifactLink from "./ExactArtifactLink"
import { type CanvasEdge, type CanvasNode, compactLanes, trace } from "./digitalThreadGeometry"
import { stateLabel } from "./presentation"
import { traceRelationLabel } from "./tracePresentation"
import {
  laneModel,
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

const PANEL_WIDTH = 330
const PANEL_HEIGHT = 226

export type DigitalThreadNetworkProps = {
  projection: NetworkProjection | null
  loading?: boolean
  error?: string | null
  onRetry?: () => void
  /** Exact route for a record, when the current workspace can open it. Absent renders non-openable. */
  hrefFor?: (node: NetworkNode) => string | undefined
  /** Opens the change inside its own view. Slice 4 supplies this. */
  onOpenChange?: (node: NetworkNode) => void
  buildLabel?: string
}

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
  hrefFor,
  onOpenChange,
  buildLabel,
}: DigitalThreadNetworkProps) {
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [hoveredId, setHoveredId] = useState<string | null>(null)
  const [dockPreference, setDockPreference] = useState<PanelDock>("bottom")
  const [query, setQuery] = useState("")
  const [groups, setGroups] = useState<Set<string>>(new Set())
  const liveRegion = useRef<HTMLDivElement | null>(null)

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
  const model = useMemo(() => laneModel(projection?.orderedLevels), [projection?.orderedLevels])

  const { lanes, canvasNodes } = useMemo(() => {
    const rows = assignRows(nodes, model)
    const placed: CanvasNode[] = nodes.map(node => ({
      id: node.id,
      lane: laneOf(node, model),
      row: rows.get(node.id) ?? 0,
    }))
    const compacted = compactLanes(model.labels, placed)
    return { lanes: compacted.lanes, canvasNodes: compacted.nodes }
  }, [model, nodes])

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

  /**
   * Which side the panel takes. It counts where the selected record's direct links actually are and docks on
   * the emptier one, so the panel is never covering the thing the highlighted edge points at.
   */
  const dock: ResolvedDock = useMemo(() => {
    if (dockPreference !== "auto") return dockPreference
    return selected ? resolveDock(selected, directLinks, byId, model) : "right"
  }, [byId, directLinks, dockPreference, model, selected])

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

  const matchesFilters = useCallback(
    (node: NetworkNode): boolean => {
      if (groups.size) {
        const suspectOnly = groups.size === 1 && groups.has("suspect")
        const isSuspect = node.state === "Suspect"
        if (suspectOnly) {
          if (!isSuspect) return false
        } else if (!groups.has(groupOf(node)) && !(groups.has("suspect") && isSuspect)) {
          return false
        }
      }
      if (query) {
        const haystack = `${node.displayNumber} ${node.title ?? ""}`.toLowerCase()
        if (!haystack.includes(query.toLowerCase())) return false
      }
      return true
    },
    [groups, query],
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
        node.state === "Suspect" ? "is-suspect" : "",
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
        </div>
      )
    },
    [byId, hrefFor, matchesFilters, selectedId, web],
  )

  if (error) {
    return (
      <div className="dtnState dtnState-error" role="alert">
        <b>The change network could not be loaded.</b>
        <p>{error}</p>
        {onRetry ? (
          <button type="button" onClick={onRetry}>
            Try again
          </button>
        ) : null}
      </div>
    )
  }

  if (!loading && projection && !nodes.length) {
    return (
      <div className="dtnState">
        <b>No change requests in {buildLabel ?? "this build"}.</b>
        <p>Change requests appear here as soon as one targets this build.</p>
      </div>
    )
  }

  return (
    <div className="dtnRoot">
      <div className="dtnToolbar">
        <div className="dtnFilters" role="group" aria-label="Filter by level">
          {[
            ["sys", "System"],
            ["hlr", "HLR"],
            ["llr", "LLR"],
            ["ver", "Test"],
            ["suspect", "Suspect"],
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

      {projection?.truncated ? (
        <p className="dtnTruncated" role="status">
          This build carries more records than the network returns. Some change requests and their links are
          not shown.
        </p>
      ) : null}

      <div className="dtnStage">
        <DigitalThreadCanvas
          lanes={lanes}
          nodes={canvasNodes}
          edges={canvasEdges}
          renderCard={renderCard}
          selectedId={selectedId}
          onSelect={setSelectedId}
          onHover={setHoveredId}
          frameInset={frameInset}
          ariaLabel="Change network for this build"
        />
        {loading ? <div className="dtnLoading">Loading the change network…</div> : null}
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
              const relationFor = (id: string) =>
                directLinks.find(edge => edge.fromId === id || edge.toId === id)?.relation
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
                                ? traceRelationLabel(relationFor(node.id) ?? "linked").toUpperCase()
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
