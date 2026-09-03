import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import DigitalThreadCanvas from "./DigitalThreadCanvas"
import ExactArtifactLink from "./ExactArtifactLink"
import { type CanvasNode, resolveDockByLane, trace } from "./digitalThreadGeometry"
import { usePanelDock } from "./digitalThreadPanelDock"
import { stateLabel } from "./presentation"
import { traceRelationLabel } from "./tracePresentation"
import {
  ARTIFACT_THREAD_LANES,
  type ArtifactThread,
  type ArtifactThreadEvidence,
  type ArtifactThreadNode,
  parseArtifactThread,
} from "./artifactThreadContract"
import {
  artifactThreadCanvasModel,
  badgeOf,
  badgeTintFor,
  evidenceSize,
  groupOf,
  identityLabel,
  isUnnumbered,
  metaLine,
  pillFor,
  shortHash,
  shortTimestamp,
  suspectEndpoints,
  suspectRelations,
} from "./artifactThreadPresentation"
import "./DigitalThreadArtifact.css"

/** Where the detail panel sits. `auto` picks the side with less linked content. */
export type PanelDock = "auto" | "left" | "right" | "bottom"
export type ResolvedDock = Exclude<PanelDock, "auto">

// The same reservations the change network makes, for the same reason: the free area is the panel plus its
// margins, measured from what the panel actually renders rather than padded with slack. Slack would read as
// correctness while hiding whether the non-occlusion rule holds.
const PANEL_WIDTH = 300 + 16 + 14
const PANEL_HEIGHT = 150 + 18 + 16

export type DigitalThreadArtifactProps = {
  /**
   * The raw `GET /api/artifact-thread` body, exactly as the server sent it.
   *
   * Raw on purpose. The slice-5B0 seam is the only thing allowed to decide what this response means, and
   * taking it pre-parsed would let a caller hand the canvas a hand-built object that never passed those
   * checks. Parsing here means the view cannot be reached except through the validated contract.
   */
  response: unknown
  loading?: boolean
  /** A transport or authorization failure, which is a different state from a malformed response. */
  error?: string | null
  onRetry?: () => void
  /** Exact route for a record, when the current workspace can open it. Absent renders non-openable. */
  hrefFor?: (node: ArtifactThreadNode) => string | undefined
  /** Exact route for an evidence file. Absent renders the identity without a link, never a dead one. */
  evidenceHref?: (evidence: ArtifactThreadEvidence) => string | undefined
  /** Opens a change request in the inside-a-change view. Slice 6 wires this to the page. */
  onOpenChange?: (node: ArtifactThreadNode) => void
  /** Selection to start on. Defaults to the thread's own focal artifact. */
  initialSelectedId?: string | null
}

const CHIPS: readonly (readonly [string, string])[] = [
  ["sys", "System"],
  ["hlr", "HLR"],
  ["llr", "LLR"],
  ["ver", "Test"],
]

/**
 * The artifact thread: one focal artifact's exact-revision chain across the six lanes of #880 §5.3.
 *
 * The server owns the trace. It performs the two fixed-direction walks of §6.5 and states which records are in
 * the thread, which lane each sits in, and which relationships are suspect. This view displays exactly that
 * graph and never fetches an extra node, walks sideways into a sibling, or completes a chain the response left
 * short — a trace view that quietly fills a gap is making a false claim about traceability.
 *
 * It is the first view whose relation vocabulary can carry a suspect lifecycle (§10.2), so it is the first to
 * offer the Suspect chip and the amber treatment, both driven from `edge.isSuspect` and never derived here.
 */
export default function DigitalThreadArtifact({
  response,
  loading = false,
  error = null,
  onRetry,
  hrefFor,
  evidenceHref,
  onOpenChange,
  initialSelectedId,
}: DigitalThreadArtifactProps) {
  const [selectedId, setSelectedId] = useState<string | null>(initialSelectedId ?? null)
  const [hoveredId, setHoveredId] = useState<string | null>(null)
  const [dockPreference, setDockPreference] = useState<PanelDock>("bottom")
  const [query, setQuery] = useState("")
  const [groups, setGroups] = useState<Set<string>>(new Set())
  const liveRegion = useRef<HTMLDivElement | null>(null)

  /**
   * The validated thread, or the reason it could not be read.
   *
   * A contract fault is reported and the board stays empty. Rendering the records that did parse would present
   * a partial trace as a whole one, and a missing record in a certification thread is a false negative about
   * traceability — the one failure this surface must never produce quietly.
   */
  const parsed = useMemo(
    () => (response === null || response === undefined ? null : parseArtifactThread(response)),
    [response],
  )
  const thread: ArtifactThread | null = parsed?.ok ? parsed.thread : null
  const contractError = parsed && !parsed.ok ? parsed.reason : null

  const nodes = useMemo(() => thread?.nodes ?? [], [thread])
  const edges = useMemo(() => thread?.edges ?? [], [thread])
  const byId = useMemo(() => new Map(nodes.map(node => [node.id, node])), [nodes])

  /**
   * The board, and while it is still arriving, the frame it will arrive into.
   *
   * #880 §6.8 is explicit: lane bands and headers render immediately with their counts unknown and cards fade
   * in — never a spinner over a discarded frame. So a load with no response yet lays out the full six lanes
   * and no cards, rather than handing the canvas an empty lane set and overlaying a message on nothing.
   *
   * Structural compaction waits until the content is actually known. Compacting an empty board would drop
   * every lane and then put them back as the response landed, which is the jump this rule exists to stop. A
   * thread that turns out to bypass Test Case still closes that one lane on arrival, which is a single lane
   * settling rather than the whole board reappearing — and it is the honest maximum, because which lanes a
   * thread populates cannot be known before the server answers.
   */
  const contentKnown = !loading || thread !== null || contractError !== null

  const model = useMemo(
    () =>
      thread
        ? artifactThreadCanvasModel(thread)
        : {
            lanes: contentKnown ? [] : [...ARTIFACT_THREAD_LANES],
            nodes: [] as CanvasNode[],
            edges: [],
            rows: new Map<string, number>(),
          },
    [contentKnown, thread],
  )
  const laneOfNode = useMemo(
    () => new Map(model.nodes.map(node => [node.id, node.lane])),
    [model.nodes],
  )

  /** Records that are an endpoint of a server-stated suspect relationship. Never a lifecycle state here. */
  const suspect = useMemo(() => suspectEndpoints(edges), [edges])

  /**
   * Land on the focal artifact.
   *
   * A thread is a question asked about one record, so opening it with nothing selected would make the reader
   * find that record again before the view answers anything. Keyed on the focal identity so re-reading the
   * same thread does not throw away a selection the reader has since moved.
   */
  const focalId = thread?.focalId ?? null
  useEffect(() => {
    if (focalId) setSelectedId(initialSelectedId ?? focalId)
  }, [focalId, initialSelectedId])

  const focusId = selectedId ?? hoveredId
  const web = useMemo(
    () => (focusId ? trace(focusId, model.edges) : null),
    [focusId, model.edges],
  )

  /**
   * On landing, frame the whole traced web rather than the focal record and one hop.
   *
   * A thread opens with its focal record already selected, so the reader has not chosen anything yet and the
   * question they asked was about the whole chain. Framing one hop put the result and the build off the right
   * edge of a six-lane thread before they had touched the board. Once they select something themselves, the
   * canvas returns to §6.6's direct-link framing, which is the right behaviour for stepping through a trace.
   */
  const framedForFocal = useMemo(
    () => (selectedId && selectedId === focalId && web ? [...web.nodes] : undefined),
    [focalId, selectedId, web],
  )

  const selected = selectedId ? byId.get(selectedId) ?? null : null
  const directLinks = useMemo(
    () => (selectedId ? edges.filter(edge => edge.fromId === selectedId || edge.toId === selectedId) : []),
    [edges, selectedId],
  )

  /** The panel docks on the side holding fewer of the selected record's direct links, per #880 §6.6. */
  const preferredDock: ResolvedDock = useMemo(() => {
    if (dockPreference !== "auto") return dockPreference
    if (!selected) return "right"
    const lane = laneOfNode.get(selected.id) ?? 0
    const linkedLanes = directLinks
      .map(edge => laneOfNode.get(edge.fromId === selected.id ? edge.toId : edge.fromId))
      .filter((value): value is number => value !== undefined)
    return resolveDockByLane(lane, linkedLanes)
  }, [directLinks, dockPreference, laneOfNode, selected])

  // Non-occlusion outranks the preference: a linked record must not vanish to honour a side (§6.6).
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
    const suspectCount = suspectRelations(selected.id, edges).length
    liveRegion.current.textContent =
      `${identityLabel(selected)}, ${stateLabel(selected.state ?? undefined)}. ` +
      `${up} upstream and ${down} downstream direct links.` +
      (suspectCount
        ? ` ${suspectCount} suspect relationship${suspectCount === 1 ? "" : "s"}.`
        : "")
  }, [directLinks, edges, selected])

  const matchesFilters = useCallback(
    (node: ArtifactThreadNode): boolean => {
      if (groups.size) {
        // The Suspect chip selects endpoints of a suspect relationship, not a lifecycle state — these node
        // families have no Suspect state, so a state-based chip could only ever select nothing (#880 §10.2).
        const matched =
          groups.has(groupOf(node)) || (groups.has("suspect") && suspect.has(node.id))
        if (!matched) return false
      }
      if (query) {
        const haystack = `${identityLabel(node)} ${node.title ?? ""} ${node.state ?? ""}`.toLowerCase()
        if (!haystack.includes(query.toLowerCase())) return false
      }
      return true
    },
    [groups, query, suspect],
  )

  /** A lane holding records but showing none of them, because the chips or the search hid them all. */
  const laneNotice = useCallback(
    (lane: number): string | null => {
      const inLane = model.nodes.filter(node => node.lane === lane)
      if (!inLane.length) return null
      const visible = inLane.filter(node => {
        const record = byId.get(node.id)
        return record ? matchesFilters(record) : false
      })
      return visible.length ? null : "No records match"
    },
    [byId, matchesFilters, model.nodes],
  )

  const renderCard = useCallback(
    (canvasNode: CanvasNode) => {
      const node = byId.get(canvasNode.id)
      if (!node) return null
      const pill = pillFor(node.state)
      const tint = badgeTintFor(node)
      const hop = web?.hops.get(node.id)
      const traced = web?.nodes.has(node.id) ?? false
      const isSuspect = suspect.has(node.id)
      const classes = [
        "dtaCard",
        selectedId === node.id ? "is-selected" : "",
        isSuspect ? "is-suspect" : "",
        node.isFocal ? "is-focal" : "",
        web && !traced ? "is-untraced" : "",
        matchesFilters(node) ? "" : "is-filtered",
      ]
        .filter(Boolean)
        .join(" ")
      const relations = suspectRelations(node.id, edges)
      return (
        <div className={classes}>
          {traced && hop ? (
            <span className="dtaHop" title={`${hop} hop${hop === 1 ? "" : "s"} from the selected record`}>
              {hop}
            </span>
          ) : null}
          <div className="dtaCardTop">
            <span className="dtaBadge" style={{ background: tint.background, color: tint.color }}>
              {badgeOf(node)}
            </span>
            {/* An execution has no controlled number, so it is never dressed as a link to one. */}
            {isUnnumbered(node) ? (
              <span className="dtaId is-unnumbered">{identityLabel(node)}</span>
            ) : (
              <ExactArtifactLink href={hrefFor?.(node)} className="dtaId">
                {node.displayNumber}
              </ExactArtifactLink>
            )}
            {/* Suspect must never ride on colour alone (#880 §7, §9), and status text carrying that rule must
                not sit in a `data-density` container at all — every one of those is hidden at some tier. This
                lives in the card's top row, which no density rule touches, so the word is visible at the
                detailed, compact AND dense tiers.

                It cannot be folded into the state pill: suspectness is a fact about a relationship, so the
                pill truthfully reads the artifact's own lifecycle state and must keep doing so. The short
                visible token is what fits beside a long controlled identifier at the dense tier; the full
                sentence travels with it for assistive technology. */}
            {isSuspect ? (
              <span className="dtaSuspectFlag">
                <b aria-hidden="true">SUSPECT</b>
                <span className="dtaVisuallyHidden">
                  Suspect link — a relationship recorded against this record is suspect
                </span>
              </span>
            ) : null}
            <span className="dtaPill" style={{ background: pill.background, color: pill.color }}>
              <i>{stateLabel(node.state ?? undefined)}</i>
            </span>
          </div>
          <div className="dtaTitle" data-density="title">
            {node.title}
          </div>
          <div className="dtaMeta" data-density="meta">
            <span>{metaLine(node)}</span>
          </div>

          {selectedId === node.id ? (
            <div className="dtaCardBody">
              {node.level ? (
                <div className="dtaKv">
                  <i>Level</i>
                  <b>{node.level}</b>
                </div>
              ) : null}
              {node.revision !== null && node.revision !== undefined ? (
                <div className="dtaKv">
                  <i>Revision</i>
                  <b>{String(node.revision).padStart(2, "0")}</b>
                </div>
              ) : null}
              {node.outcome ? (
                <div className="dtaKv">
                  <i>Outcome</i>
                  <b>{node.outcome}</b>
                </div>
              ) : null}
              {node.executedBy ? (
                <div className="dtaKv">
                  <i>Executed by</i>
                  <b>{node.executedBy}</b>
                </div>
              ) : null}
              {node.executedAt ? (
                <div className="dtaKv">
                  <i>Executed</i>
                  <b>{shortTimestamp(node.executedAt)}</b>
                </div>
              ) : null}
              {node.recordedAt ? (
                <div className="dtaKv">
                  <i>Recorded</i>
                  <b>{shortTimestamp(node.recordedAt)}</b>
                </div>
              ) : null}

              {node.evidence.length ? (
                <div className="dtaEvidence">
                  <p className="dtaEyebrow">EVIDENCE</p>
                  {node.evidence.map(file => (
                    <div className="dtaEvidenceRow" key={file.id}>
                      {evidenceHref?.(file) ? (
                        <a href={evidenceHref(file)}>{file.fileName}</a>
                      ) : (
                        <span>{file.fileName}</span>
                      )}
                      <small>
                        {file.contentType} · {evidenceSize(file.size)}
                      </small>
                      {/* The hash is the reason the record exists, so the card carries it rather than a
                          sentence about it. The full value is in the panel and in the title attribute. */}
                      <code title={file.sha256}>{shortHash(file.sha256)}</code>
                    </div>
                  ))}
                </div>
              ) : null}

              {relations.length ? (
                <p className="dtaNote">
                  {relations.length === 1 ? "A relationship is" : `${relations.length} relationships are`}{" "}
                  recorded as suspect:{" "}
                  {relations
                    .map(edge => traceRelationLabel(edge.relation).toLowerCase())
                    .join(", ")}
                  . The link is stated suspect by the server, not inferred from this record&rsquo;s state.
                </p>
              ) : null}

              {(node.kind === "ChangeRequest" || node.kind === "TestChangeRequest") && onOpenChange ? (
                <div className="dtaCardActs">
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
    [byId, edges, evidenceHref, hrefFor, matchesFilters, onOpenChange, selectedId, suspect, web],
  )

  const focal = thread ? nodes.find(node => node.isFocal) ?? null : null
  const noVerification = thread ? !thread.verification.isApplicable : false

  return (
    <div className="dtaRoot">
      <div className="dtaToolbar">
        <div className="dtaFilters" role="group" aria-label="Filter the thread">
          {CHIPS.map(([key, label]) => (
            <button
              type="button"
              key={key}
              aria-pressed={groups.has(key)}
              className={groups.has(key) ? "is-on" : ""}
              onClick={() => toggle(setGroups, key)}
            >
              {label}
            </button>
          ))}
          {/* Offered here and nowhere else so far: RequirementTrace and CaseProcedure are the only two link
              kinds a suspect lifecycle governs, and this is the first view that draws them (#880 §10.2). */}
          <button
            type="button"
            key="suspect"
            aria-pressed={groups.has("suspect")}
            className={`dtaSuspectChip${groups.has("suspect") ? " is-on" : ""}`}
            onClick={() => toggle(setGroups, "suspect")}
          >
            Suspect
          </button>
        </div>
        <label className="dtaSearch">
          <span className="dtaVisuallyHidden">Find an identifier</span>
          <input
            type="search"
            value={query}
            placeholder="Find an identifier"
            onChange={event => setQuery(event.target.value)}
          />
        </label>
      </div>

      {/* A level with no verification discipline is a fact about the domain, not a broken thread. It is stated
          once, here, rather than drawn as empty Test Case / Procedure / Result containers — #880 drops
          structurally empty lanes, and a placeholder lane would invent a chain step this level cannot have. */}
      {noVerification ? (
        <p className="dtaApplicability" role="status">
          {thread?.verification.reason ??
            "This requirement level has no verification discipline, so this thread has no test case, procedure or result."}
        </p>
      ) : null}

      <div className="dtaStage">
        <DigitalThreadCanvas
          lanes={model.lanes}
          nodes={model.nodes}
          edges={model.edges}
          renderCard={renderCard}
          laneNotice={laneNotice}
          selectedId={selectedId}
          onSelect={setSelectedId}
          onHover={setHoveredId}
          frameInset={frameInset}
          tracedEdges={web?.edges}
          frameIds={framedForFocal}
          onFramingNeedsRoom={reportNeedsRoom}
          ariaLabel={
            focal ? `Artifact thread for ${identityLabel(focal)}` : "Artifact thread"
          }
        />
        {loading ? <div className="dtaLoading">Loading the artifact thread…</div> : null}

        {/* Every state below sits inside the frame rather than replacing it (#880 §6.8), so a failed refresh
            does not cost the reader the zoom, pan and selection they had built up. */}
        {error ? (
          <div className="dtaInFrame dtaInFrame-error" role="alert">
            <b>The artifact thread could not be loaded.</b>
            <p>{error}</p>
            {onRetry ? (
              <button type="button" onClick={onRetry}>
                Try again
              </button>
            ) : null}
          </div>
        ) : null}

        {/* A malformed response is its own state, and a loud one. The canvas stays empty rather than showing
            the records that happened to parse: a partial trace presented as a whole one is a false negative
            about traceability, which is worse than showing nothing. */}
        {contractError ? (
          <div className="dtaInFrame dtaInFrame-error" role="alert">
            <b>This artifact thread could not be shown.</b>
            <p>
              The response did not match the artifact-thread contract, so no part of it is drawn. Showing the
              records that could be read would present an incomplete trace as a complete one.
            </p>
            <p className="dtaReason">{contractError}</p>
            {onRetry ? (
              <button type="button" onClick={onRetry}>
                Try again
              </button>
            ) : null}
          </div>
        ) : null}

        {!loading && !error && !contractError && thread && nodes.length > 0 && !nodes.some(matchesFilters) ? (
          <div className="dtaInFrame" role="status">
            <b>No records match.</b>
            <p>Clear a filter chip or the search box to bring records back.</p>
          </div>
        ) : null}
      </div>

      {/* An artifact with nothing linked to it is a legitimate one-node thread, not an error and not an empty
          board. It renders as its own card, and this line says plainly that the absence is the answer. */}
      {!loading && !error && !contractError && thread && nodes.length === 1 ? (
        <p className="dtaSolitary" role="status">
          No recorded relationships. {focal ? identityLabel(focal) : "This record"} is not linked to any other
          controlled record in this build.
        </p>
      ) : null}

      <div className="dtaVisuallyHidden" aria-live="polite" ref={liveRegion} />

      {selected ? (
        <aside className={`dtaPanel dtaPanel-${dock}`} aria-label={`Detail for ${identityLabel(selected)}`}>
          <div className="dtaPanelTools">
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
          <div className="dtaPanelGrid">
            <div className="dtaPanelIdentityCol">
              <p className="dtaEyebrow">{selected.isFocal ? "FOCAL RECORD" : "SELECTED RECORD"}</p>
              <div className="dtaPanelIdentity">
                {isUnnumbered(selected) ? (
                  <span className="dtaPanelUnnumbered">{identityLabel(selected)}</span>
                ) : (
                  <ExactArtifactLink href={hrefFor?.(selected)}>{selected.displayNumber}</ExactArtifactLink>
                )}
                <span
                  className="dtaPill"
                  style={{
                    background: pillFor(selected.state).background,
                    color: pillFor(selected.state).color,
                  }}
                >
                  {stateLabel(selected.state ?? undefined)}
                </span>
              </div>
              <h3>{selected.title}</h3>
              {selected.evidence.length ? (
                <div className="dtaPanelEvidence">
                  {selected.evidence.map(file => (
                    <div key={file.id}>
                      {evidenceHref?.(file) ? (
                        <a href={evidenceHref(file)}>{file.fileName}</a>
                      ) : (
                        <span>{file.fileName}</span>
                      )}
                      {/* The whole hash, not the abbreviation: a reviewer verifying a file works from this. */}
                      <code>{file.sha256}</code>
                      <small>
                        {file.uploadedBy} · {shortTimestamp(file.uploadedAt)} · {evidenceSize(file.size)}
                      </small>
                    </div>
                  ))}
                </div>
              ) : null}
            </div>
            {(["up", "down"] as const).map(direction => {
              const set = direction === "up" ? web?.up : web?.down
              const title = direction === "up" ? "UPSTREAM" : "DOWNSTREAM"
              const rows = Array.from(set ?? [])
                .map(id => ({ node: byId.get(id), hop: web?.hops.get(id) ?? 1 }))
                .filter((row): row is { node: ArtifactThreadNode; hop: number } => Boolean(row.node))
                .sort(
                  (a, b) => a.hop - b.hop || identityLabel(a.node).localeCompare(identityLabel(b.node)),
                )
              const relationFor = (id: string) =>
                directLinks.find(edge => edge.fromId === id || edge.toId === id)
              return (
                <div className="dtaPanelCol" key={direction}>
                  <div className="dtaRelHead">
                    <p className="dtaEyebrow">{title}</p>
                    <span>{rows.length} records, all hops</span>
                  </div>
                  <div className="dtaRel">
                    {rows.length ? (
                      rows.map(({ node, hop }) => {
                        const link = relationFor(node.id)
                        return (
                          <button
                            type="button"
                            key={node.id}
                            className={`${hop > 1 ? "is-far" : ""}${link?.isSuspect ? " is-suspect" : ""}`}
                            onClick={() => setSelectedId(node.id)}
                          >
                            <span>
                              <small>
                                {hop === 1
                                  ? traceRelationLabel(link?.relation ?? "linked").toUpperCase()
                                  : `${hop} HOPS`}
                                {link?.isSuspect ? " · SUSPECT" : ""}
                              </small>
                              <span>{identityLabel(node)}</span>
                            </span>
                            <em aria-hidden="true">›</em>
                          </button>
                        )
                      })
                    ) : (
                      <p className="dtaRelEmpty">No recorded relationships</p>
                    )}
                  </div>
                </div>
              )
            })}
            <div className="dtaPanelActions">
              {(selected.kind === "ChangeRequest" || selected.kind === "TestChangeRequest") && onOpenChange ? (
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

const toggle = (set: (updater: (current: Set<string>) => Set<string>) => void, key: string): void =>
  set(current => {
    const next = new Set(current)
    if (next.has(key)) next.delete(key)
    else next.add(key)
    return next
  })
