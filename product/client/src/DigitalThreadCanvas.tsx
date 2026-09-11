import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react"
import {
  EDGE_LAYER_OVERHANG,
  type CanvasEdge,
  type CanvasFrame,
  type CanvasRect,
  type CanvasNode,
  type FrameIntent,
  type LayoutResult,
  trace,
  clampOffsets,
  edgeIdentity,
  edgePath,
  offsetToReveal,
  fitTransform,
  frameNodes,
  isVisible,
  laneAt,
  layout,
  layoutWithMeasuredCards,
  minimumZoom,
  MIN_ZOOM,
  nodePosition,
  planReveal,
  positionsForNodes,
  placeEdgeLabels,
  READABLE_SELECTION_MIN_ZOOM,
  rescaleOffsets,
  contentWindow,
  displayedWindowForLane,
  effectiveLaneLimits,
  stepTowards,
  wheelFactor,
  zoomAbout,
} from "./digitalThreadGeometry"
import "./DigitalThreadCanvas.css"

export type { CanvasEdge, CanvasNode } from "./digitalThreadGeometry"

export type DigitalThreadCanvasProps = {
  lanes: readonly string[]
  nodes: readonly CanvasNode[]
  edges: readonly CanvasEdge[]
  /** Card contents. The canvas owns position and visibility; the caller owns everything inside the card. */
  renderCard: (node: CanvasNode) => React.ReactNode
  /** Count shown beside a lane heading, when the lane holds more than one record. */
  laneCount?: (lane: number) => number
  /**
   * A sentence a lane shows in place of cards, when it has records but none the reader can currently see.
   *
   * A lane emptied by a filter keeps its place and says so (#880 §6.8). Collapsing it would slide every other
   * lane sideways mid-search, and leaving it silently blank would read as a lane with nothing in it, which is
   * a different fact from a lane whose records the filter is hiding.
   */
  laneNotice?: (lane: number) => string | null
  selectedId?: string | null
  onSelect?: (id: string | null) => void
  onHover?: (id: string | null) => void
  /** Area the board may use, in viewport pixels. Shrink it when a detail panel is docked. */
  frameInset?: { right?: number; left?: number; bottom?: number }
  /**
   * The edges of the currently traced web, keyed `from>to`.
   *
   * The canvas owns edge appearance for every view, so the traced treatment lives here rather than being
   * rebuilt per view. Undefined means no trace is active and every edge rests; an empty set means a trace is
   * active and reaches no edge, which is a different picture and must not read as the resting one.
   */
  tracedEdges?: ReadonlySet<string>
  /**
   * The records the camera should frame when the selection changes. A view passes the complete directed trace
   * here when the selected story is the question the reader opened.
   *
   * The traversal remains directed and cycle-safe in `trace`; the canvas only lays out the exact set the view
   * supplied. Keeping this set explicit prevents a generic one-hop fallback from hiding the far side of a story.
   */
  frameIds?: readonly string[]
  /** How the current framing request should be interpreted. Deep-link arrival keeps the readable landing floor. */
  framingIntent?: FrameIntent
  /** The first selected record supplied by a view on deep-link/arrival. Later user selections use the compact floor. */
  landingId?: string | null
  /**
   * The free area this dock leaves cannot hold the selection and its direct links at the legibility floor.
   *
   * The view answers by re-docking the panel somewhere that can. §6.6 requires every direct link to be drawn
   * and clear of the panel; when the two cannot both hold on this side, the panel is what moves.
   */
  onFramingNeedsRoom?: () => void
  ariaLabel?: string
  /**
   * Stable navigation scope (view, project, build, representation and any deliberately chosen exact
   * baseline). Deliberately not derived from nodes or edges: a refreshed payload or a re-pointed edge must
   * not become a new arrival, and a derived fallback would silently merge or split scopes.
   */
  scopeKey?: string
}

/**
 * A card owns its links and controls. The canvas must not turn their pointer or keyboard activation into a
 * selection, pan, or lane roll as the event bubbles through the shared viewport.
 */
const nestedControl = (target: EventTarget | null): boolean => {
  const element = target instanceof Element ? target : null
  return Boolean(element?.closest("a,button,input,select,textarea,summary,[role='link'],[role='checkbox'],[role='radio']"))
}


/**
 * The canvas shell: lanes of cards that pan, zoom, change density with zoom, roll independently, and follow
 * one another's links.
 *
 * React renders the cards once per data change. Everything positional — transform, lane offsets, edge
 * geometry, per-card visibility — is written straight to the DOM, because pan and zoom update on every
 * pointer move and re-rendering the board per frame would cost the interaction its smoothness.
 */
export default function DigitalThreadCanvas({
  lanes,
  nodes: sourceNodes,
  edges,
  renderCard,
  laneCount,
  laneNotice,
  selectedId: pinnedId = null,
  onSelect: onPin,
  onHover,
  frameInset: inspectorInset,
  tracedEdges: suppliedTracedEdges,
  frameIds: suppliedFrameIds,
  framingIntent = "selection",
  landingId = null,
  onFramingNeedsRoom: requestDockRoom,
  ariaLabel = "Digital Thread canvas",
  scopeKey = "canvas",
}: DigitalThreadCanvasProps) {
  /**
   * Unselected hover emphasis.
   *
   * Hover is a temporary, visual-only emphasis with its own lane-local reveal. It is a different thing from
   * `pinnedId`: a selection persists until the reader clears it or selects another record, and no hover can
   * ever replace it. The old floating duplicate target and its camera restore are gone with this split.
   */
  const [hoverId, setHoverId] = useState<string | null>(null)
  const emphasisId = pinnedId ?? hoverId
  // A temporary emphasis must not permanently redock the pinned inspector. Oversized stories retain reveal
  // actions; pinning can then request the normal persistent dock fallback.
  const onFramingNeedsRoom = pinnedId ? requestDockRoom : undefined
  // Framing, expansion and the tray reservation belong to a persistent selection only. Hover emphasis is a
  // visual treatment: it must never resize the source card or move its neighbours, so it never owns them.
  const selectedId = pinnedId
  const frameInset = { ...inspectorInset, bottom: (inspectorInset?.bottom ?? 0) + (selectedId ? 64 : 0) }
  const story = useMemo(() => emphasisId ? trace(emphasisId, edges) : null, [emphasisId, edges])
  /**
   * Canonical rows, never re-ordered.
   *
   * The previous model moved every traced record to the top of its lane (`arrangeStory`) so a story could be
   * seen at once. #1022 supersedes that: only an out-of-view linked card is displaced, and it is displaced by
   * a temporary lane-local delta rather than by renumbering rows other readers' layouts depend on.
   */
  const nodes = sourceNodes
  const tracedEdges = story?.edges ?? suppliedTracedEdges
  const frameIds = useMemo(() => story ? [...story.nodes] : suppliedFrameIds, [story, suppliedFrameIds])
  const previewTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  /** Temporary per-card displacement, in content coordinates. Keyed by node id. */
  const revealDeltas = useRef<Map<string, number>>(new Map())
  const revealTargets = useRef<Map<string, number>>(new Map())
  /** Lanes the reader owns for the current context. Automatic reveal never re-plans them. */
  const frozenLanes = useRef<Set<number>>(new Set())
  /** Lanes that have already had a usable exposure in this context: their arrangement is not re-planned. */
  const visitedLanes = useRef<Set<number>>(new Set())
  /** The emphasis subject the ownership sets belong to; a different subject starts a new context. */
  const subjectOwnership = useRef<string | null | undefined>(undefined)
  /** Effective per-lane scroll minimum for the arrangement as displayed and as it is heading. */
  const limitsRef = useRef<Map<number, number>>(new Map())
  /** The single resolved floor every consumer uses: effective limit, allowance and deepest extent combined. */
  const floorsRef = useRef<number[]>([])
  /** Lanes whose planned reveal has actually arrived; only these count as delivered/visited. */
  const deliveredLanes = useRef<Set<number>>(new Set())
  /** Last measured card heights, so keyboard navigation uses the same geometry as paint. */
  const measuredHeightsRef = useRef<Map<string, number>>(new Map())
  /** The deepest scroll extent this scope has needed per lane, so cleanup cannot snap the reader's lane. */
  const deepestMinimum = useRef<number[]>([])
  /** The lane windows the last plan was built against, so an unchanged view is not re-planned. */
  const revealSignature = useRef("")
  const sourceSignature = sourceNodes.map(node => `${node.id}:${node.lane}:${node.row}`).join("|")
  /** Traced relationships are a real planning input: a re-pointed edge must not leave a stale arrangement. */
  const edgesKey = useMemo(
    () => edges.map(edge => `${edge.from}>${edge.to}:${edge.label}`).join("|"),
    [edges],
  )
  useEffect(() => {
    if (previewTimer.current !== null) clearTimeout(previewTimer.current)
    previewTimer.current = null
    setHoverId(null)
  }, [pinnedId, sourceSignature, scopeKey])
  // A different scope is a different navigation context: nothing temporary may survive it.
  useEffect(() => {
    frozenLanes.current = new Set()
    deepestMinimum.current = []
    revealSignature.current = ""
  }, [scopeKey])
  const clearPreviewTimer = () => {
    if (previewTimer.current !== null) clearTimeout(previewTimer.current)
    previewTimer.current = null
  }
  /**
   * Hover emphasis ends.
   *
   * It removes the temporary emphasis and lets the reveal deltas return to their ordinary rows. It never
   * restores a saved camera or lane snapshot: the reader's own navigation during the hover is theirs.
   */
  const exitHover = () => {
    clearPreviewTimer()
    setHoverId(null)
    onHover?.(null)
  }
  const onSelect = useCallback((id: string | null) => {
    if (previewTimer.current !== null) clearTimeout(previewTimer.current)
    previewTimer.current = null
    setHoverId(null)
    onHover?.(null)
    onPin?.(id)
  }, [onHover, onPin])
  const viewportRef = useRef<HTMLDivElement | null>(null)
  const sceneRef = useRef<HTMLDivElement | null>(null)
  const edgeLayerRef = useRef<SVGSVGElement | null>(null)
  const offscreenRefs = useRef(new Map<string, HTMLButtonElement>())
  const cardRefs = useRef(new Map<string, HTMLDivElement>())
  const edgeRefs = useRef<
    {
      path: SVGPathElement
      dot: SVGCircleElement
      leader: SVGLineElement | null
      label: SVGTextElement | null
      edge: CanvasEdge
    }[]
  >([])

  const transform = useRef({ x: 0, y: 0, zoom: 1 })
  /**
   * The card that carries the tab stop in each lane (#880 §6.9).
   *
   * A roving tab index, not a tabbable card per record. With hundreds of cards in a build, making every one a
   * tab stop turns Tab into an unusable crawl and lets focus land on cards rolled out of their lane window.
   * One stop per lane means Tab moves between lanes and the arrows move within one, which is the contract.
   */
  const [roving, setRoving] = useState<Record<number, string>>({})
  // paint() runs outside React on every pointer move, so it reads these rather than the closed-over state.
  const rovingRef = useRef<Record<number, string>>({})
  const byLaneRef = useRef<Map<number, CanvasNode[]>>(new Map())
  const offsets = useRef<number[]>([])
  const targets = useRef<number[]>([])
  const geometryRef = useRef<LayoutResult | null>(null)
  const frameSignature = useRef("")
  const animation = useRef<number | null>(null)
  /** Starts the shared motion loop from code that runs before `settle` exists (paint plans the reveal). */
  const kickMotion = useRef<() => void>(() => {})
  const reflowFrame = useRef<number | null>(null)
  const scrubbing = useRef(false)
  /** The framing key the selection effect last acted on, so a re-render alone cannot reset a rolled lane. */
  const framedFor = useRef<string | null>(null)
  /** The latest framing request, so the resize path can retry one that arrived before the frame was real. */
  const framingRef = useRef<{ selectedId: string; wanted: string[]; key: string; intent: FrameIntent } | null>(null)
  /** Arrival survives measurement retries, but focal -> close -> focal is still a user return, not a new landing. */
  const landingState = useRef({
    id: landingId,
    lastSelected: selectedId,
    seen: Boolean(selectedId && framingIntent === "landing" && selectedId === landingId),
    consumed: false,
    selection: selectedId && framingIntent === "landing" && selectedId === landingId ? selectedId : null,
  })

  // A measured panel inset can cause more than one framing pass for the same arrival. Keep that pass at the
  // landing floor until the reader chooses another record, then never mistake a return to the focal record for a
  // deep link. These refs track identity, not presentation state, and avoid a render-triggering state update.
  if (landingState.current.id !== landingId) {
    landingState.current = {
      id: landingId,
      lastSelected: selectedId,
      seen: Boolean(selectedId && framingIntent === "landing" && selectedId === landingId),
      consumed: false,
      selection: selectedId && framingIntent === "landing" && selectedId === landingId ? selectedId : null,
    }
  } else if (landingState.current.lastSelected !== selectedId) {
    const previous = landingState.current.lastSelected
    if (selectedId === null) {
      if (landingState.current.seen) landingState.current.consumed = true
      landingState.current.selection = null
    } else if (
      previous === null &&
      selectedId === landingId &&
      framingIntent === "landing" &&
      !landingState.current.consumed
    ) {
      landingState.current.seen = true
      landingState.current.selection = selectedId
    } else {
      landingState.current.consumed = true
      landingState.current.selection = null
    }
    landingState.current.lastSelected = selectedId
  }
  const easeTimer = useRef<number | null>(null)
  const zoomReadoutRef = useRef<HTMLOutputElement | null>(null)

  const measuredCounts = lanes.map((_, lane) =>
    laneCount ? laneCount(lane) : nodes.filter(node => node.lane === lane).length,
  )
  const countsKey = measuredCounts.join(",")
  // Card identities determine which DOM elements the geometry observer must watch. Keeping this separate from
  // the full node array avoids resubscribing when a projection recreates equivalent records while still
  // covering cards entering or leaving the canvas.
  const cardIdsKey = nodes.map(node => node.id).join(",")

  /**
   * The same numbers, but with an identity that only changes when the numbers do.
   *
   * `counts` feeds `paint`, and `paint` feeds the selection-framing effect. Rebuilt inline it was a fresh
   * array on every render, so both were too, and the effect below re-ran for any state change at all —
   * including hover, which every view routes into React state. That effect rewrites the lane offsets, so
   * moving the pointer across the board silently threw away a lane the reader had rolled by hand, against
   * #880 §6.3 and §6.4. Keying on the joined counts is enough: two boards with the same per-lane totals are
   * interchangeable everywhere this value is used.
   */
  // eslint-disable-next-line react-hooks/exhaustive-deps -- countsKey is the value identity of measuredCounts
  const counts = useMemo(() => measuredCounts, [countsKey])

  /**
   * Room kept to the right of the board for an intra-lane edge inside the **final** lane.
   *
   * Such an edge bows into the gutter beside its lane. Anywhere but the last lane that gutter is board the fit
   * already accounts for, so nothing is needed; in the last lane it is past the board's own width, and
   * centring a board that does not include it left the curve and its label hanging off the viewport. The
   * artifact thread's RESULT · BUILD lane is the case this exists for — an execution's `evidence for` link to
   * its build, and a `retest of` link between two runs.
   *
   * Derived from the edges the canvas was handed rather than declared by the caller, so a view that grows one
   * of these links later cannot forget to ask for the space.
   */
  const trailingOverhang = useMemo(() => {
    const lastLane = lanes.length - 1
    const laneById = new Map(nodes.map(node => [node.id, node.lane]))
    const needsRoom = edges.some(
      edge =>
        laneById.get(edge.from) === lastLane && laneById.get(edge.to) === lastLane,
    )
    return needsRoom ? EDGE_LAYER_OVERHANG : 0
  }, [edges, lanes.length, nodes])

  /**
   * What the camera is being asked to frame, and a signature of it built entirely from values.
   *
   * The framing effect must run whenever the thing being framed actually changes, and must not run when only
   * React identities changed. Those are different questions, and answering the second with `nodes`/`edges`
   * array identity is what let a hover discard a reader's lane roll.
   *
   * So the signature names the real inputs: which record is selected, which records are wanted in shot, and
   * **where each of those sits** — its lane and row. That makes it notice the cases an identity check cannot
   * distinguish from noise and a selection check misses entirely: the same selection re-pointed from one
   * linked record to another, or a linked record moved to a different row while the per-lane counts stay the
   * same. Both are real board changes that must re-sync and re-frame, or §6.4 leaves the newly linked record
   * outside its lane window and §6.6 leaves it under the panel.
   *
   * `countsKey`, the frame insets and the trailing overhang are in it too, because the geometry and the free
   * area are equally part of what "framed" means.
   */
  const framing = useMemo(() => {
    if (!selectedId) return null

    // `landingId` identifies the record the view selected on arrival. The focal identity alone is insufficient:
    // a reader can deliberately return to that same record after stepping through another card. The identity ref
    // persists through measurement retries, preserving the initial/deep-link distinction without a user density
    // setting.
    const intent: FrameIntent = framingIntent === "landing" && landingState.current.selection === selectedId
      ? "landing"
      : framingIntent === "landing" ? "selection" : framingIntent

    const linked = new Set<string>([selectedId])
    for (const edge of edges) {
      if (edge.from === selectedId) linked.add(edge.to)
      else if (edge.to === selectedId) linked.add(edge.from)
    }
    // A caller-supplied set wins, but the selection is always in it: framing a set that omits the record the
    // reader just selected would move the board off the very thing it is about.
    const wanted = frameIds?.length ? new Set<string>([selectedId, ...frameIds]) : linked

    // Sorted so the signature does not change merely because the projection returned its nodes in a new order.
    const placement = nodes
      .filter(node => wanted.has(node.id))
      .map(node => `${node.id}@${node.lane}:${node.row}`)
      .sort()
      .join(",")

    return {
      selectedId,
      wanted: [...wanted],
      intent,
      key:
        `${selectedId}|${countsKey}|${placement}` +
        `|${intent}|${frameInset?.left ?? 0},${frameInset?.right ?? 0},${frameInset?.bottom ?? 0},${trailingOverhang}`,
    }
  }, [
    countsKey,
    edges,
    frameIds,
    framingIntent,
    frameInset?.left,
    frameInset?.right,
    frameInset?.bottom,
    nodes,
    selectedId,
    trailingOverhang,
  ])

  /**
   * The frame can be measured before it has settled — inside a preview or a freshly mounted panel the first
   * rect is a fraction of the real size. Laying out from that leaves the board wrongly zoomed and clumped in
   * a corner, so a nonsense rect is refused and the caller re-measures once it is real.
   */
  const frame = useCallback((): CanvasFrame | null => {
    const element = viewportRef.current
    if (!element) return null
    const rect = element.getBoundingClientRect()
    if (rect.width < 320 || rect.height < 240) return null
    const width = rect.width - (frameInset?.left ?? 0) - (frameInset?.right ?? 0)
    const controls = element.querySelector<HTMLElement>(".dtCanvasControls")
    // The toolbar may wrap at a narrow width or under a larger text setting. Its rendered bottom, rather than a
    // fixed constant, is the start of the actual drawing frame; the small breathing gap keeps headings readable.
    const controlBottom = controls?.getBoundingClientRect().bottom ?? rect.top + 38
    // Headings extend above the card scene. Reserve that authored offset as well as the toolbar, so a frame
    // clamped to its first card cannot put its heading back underneath the controls.
    const heading = element.querySelector<HTMLElement>(".dtCanvasLaneHead")
    const headingOffset = heading ? Math.max(0, -(parseFloat(getComputedStyle(heading).top) || 0)) : 0
    // The viewport frame must not depend on the current camera: Fit measures it before changing zoom.
    const top = Math.max(40, Math.ceil(controlBottom - rect.top + headingOffset + 8))
    const height = rect.height - top - (frameInset?.bottom ?? 0)
    if (width < 240 || height < 180) return null
    return {
      x: frameInset?.left ?? 0,
      y: top,
      width,
      height,
    }
  }, [frameInset?.left, frameInset?.right, frameInset?.bottom])

  /** Write current geometry to the DOM: transform, band sizes, card positions, edge paths. */
  const paint = useCallback(() => {
    const box = frame()
    const scene = sceneRef.current
    if (!box || !scene) return

    const rawResult = layout(counts, box, transform.current.zoom)
    // Measure at the destination density. A restored camera can change the tier; measuring the previous
    // tier's shorter cards first would clamp saved lane offsets before the full card height returns.
    scene.dataset.tier = String(rawResult.tier)
    // Selected and wrapped cards can add real scene height. Extend the lane's rolling extent from the same
    // measurements used for positions so a shifted final card remains reachable by keyboard and scrub.
    const measuredCardHeights = new Map<string, number>()
    for (const node of nodes) {
      const card = cardRefs.current.get(node.id)
      // Toggle before measuring so selection's expanded body is included in this settled pass. offsetHeight is
      // the rendered border box; scrollHeight omits the border and left a small overlap at the next row.
      card?.classList.toggle("is-selected", selectedId === node.id)
      const height = card?.offsetHeight || card?.scrollHeight
      if (height && Number.isFinite(height)) measuredCardHeights.set(node.id, height)
    }
    const result = layoutWithMeasuredCards(rawResult, nodes, measuredCardHeights)
    const previous = geometryRef.current
    if (previous && previous.tier !== result.tier) {
      offsets.current = rescaleOffsets(offsets.current, previous, result)
      targets.current = offsets.current.slice()
    }
    geometryRef.current = result
    while (offsets.current.length < lanes.length) offsets.current.push(0)
    /**
     * Allowance-aware clamp.
     *
     * The lane's extent shrinks the moment temporary reveal deltas are removed. Clamping to the shrunken
     * minimum would snap a reader who scrolled into the temporarily extended range, so the deepest extent
     * this scope has needed is remembered. It is released as soon as the reader's own offset is back inside
     * the ordinary range, and dropped on a scope change. In-memory only: never persisted layout.
     */
    while (deepestMinimum.current.length < lanes.length) deepestMinimum.current.push(0)

    /**
     * Lane-local reveal plan.
     *
     * Computed from content coordinates and the camera's usable window, and only when the situation it
     * describes has actually changed — never on every paint, pointer move or ordinary re-render. Lanes the
     * reader owns are excluded, and a lane the camera cannot show vertically prepares against its full band
     * so its first arrival is useful.
     */
    /**
     * First useful exposure.
     *
     * The plan is recomputed when the subject changes, when the traced relationships or measured heights
     * change, when the tier changes, or when an *unvisited* lane becomes usable — never for ordinary lane or
     * vertical camera movement, and never for a lane the reader has already had in front of them (visited) or
     * has taken ownership of (frozen).
     */
    if (subjectOwnership.current !== emphasisId) {
      subjectOwnership.current = emphasisId
      frozenLanes.current = new Set()
      visitedLanes.current = new Set()
      revealSignature.current = ""
    }
    const displayedWindow = displayedWindowForLane(result.bandHeight, box, transform.current)
    const usable = new Set<number>()
    for (let lane = 0; lane < lanes.length; lane += 1) {
      const laneLeft = lane * result.geometry.lanePitch * transform.current.zoom + transform.current.x
      const laneRight = laneLeft + result.geometry.laneWidth * transform.current.zoom
      if (displayedWindow && laneRight > box.x && laneLeft < box.x + box.width) usable.add(lane)
    }
    const contentWindows = new Map<number, { top: number; bottom: number }>()
    for (let lane = 0; lane < lanes.length; lane += 1) {
      contentWindows.set(lane, displayedWindow
        ? contentWindow(displayedWindow, offsets.current[lane] ?? 0)
        : { top: 0, bottom: result.bandHeight })
    }
    const measuredSignature = nodes.map(node => `${node.id}:${Math.round(measuredCardHeights.get(node.id) ?? 0)}`).join("|")
    const windowArrival = [...usable].some(lane => !visitedLanes.current.has(lane))
    const revealKey = `${scopeKey}|${emphasisId ?? ""}|${result.tier}|${measuredSignature}|${edgesKey}|${windowArrival ? "arrival" : "stable"}`
    if (revealKey !== revealSignature.current) {
      revealSignature.current = revealKey
      const plan = planReveal({
        nodes,
        geometry: result.geometry,
        laneOffsets: offsets.current,
        measuredHeights: measuredCardHeights,
        storyIds: story?.nodes ?? new Set<string>(),
        subjectId: emphasisId ?? null,
        windowByLane: contentWindows,
        // Only lanes whose reveal has actually arrived (or that the reader owns) keep their arrangement. A
        // lane that was merely *scheduled* is still incoming, so its planned targets survive a replan rather
        // than being replaced by whatever intermediate values happen to be displayed.
        frozenLanes: new Set([...frozenLanes.current, ...deliveredLanes.current]),
        existing: revealTargets.current,
        bandHeight: result.bandHeight,
      })
      revealTargets.current = plan.deltas
      kickMotion.current()
    }
    /**
     * Effective limits for the arrangement as displayed and as it is heading.
     *
     * Union of both, so the range stays open while temporary geometry is still moving and a reader who
     * scrolled into the extended range is never clamped back by a return that has not finished.
     */
    const limitsFor = (deltas: ReadonlyMap<string, number>) => effectiveLaneLimits({
      nodes,
      geometry: result.geometry,
      bandHeight: result.bandHeight,
      measuredHeights: measuredCardHeights,
      deltas,
      // The predicate solves for a lane offset, so it takes the window in displayed coordinates while the
      // cards' effective tops stay in content coordinates.
      displayedWindowByLane: new Map(lanes.map((_, lane) => [
        lane,
        displayedWindow ?? { top: 0, bottom: result.bandHeight },
      ])),
    })
    const currentLimits = limitsFor(revealDeltas.current)
    const targetLimits = limitsFor(revealTargets.current)
    limitsRef.current = new Map([...currentLimits].map(([lane, limits]) => [
      lane,
      Math.min(limits.minimum, targetLimits.get(lane)?.minimum ?? limits.minimum),
    ]))

    /**
     * One resolved floor, used by every consumer.
     *
     * It combines the effective limit of the displayed arrangement with the retained allowance, so paint,
     * rollability, pointer scrolling and explicit reveal can never disagree about how far a lane may move.
     * The rule is stated once here: a lane whose reader-owned offset is back inside the effective range
     * releases the extra room; otherwise the deepest extent this scope has needed is kept.
     */
    floorsRef.current = result.laneMinimums.map((minimum, lane) => {
      const effective = Math.min(minimum, limitsRef.current.get(lane) ?? minimum)
      const deep = Math.min(deepestMinimum.current[lane] ?? effective, effective)
      deepestMinimum.current[lane] = (offsets.current[lane] ?? 0) >= effective ? effective : deep
      return deepestMinimum.current[lane]
    })
    offsets.current = clampOffsets(offsets.current, floorsRef.current)
    measuredHeightsRef.current = measuredCardHeights

    const { geometry, bandHeight } = result
    scene.style.transform = `translate(${transform.current.x}px,${transform.current.y}px) scale(${transform.current.zoom})`
    scene.style.width = `${result.sceneWidth + trailingOverhang}px`
    scene.style.height = `${bandHeight}px`
    scene.dataset.tier = String(result.tier)
    scene.dataset.zoom = String(Math.round(transform.current.zoom * 100))
    if (zoomReadoutRef.current) {
      const tierLabel = result.tier === 2 ? "Detailed" : result.tier === 1 ? "Compact" : "Dense"
      zoomReadoutRef.current.textContent = `${Math.round(transform.current.zoom * 100)}% · ${tierLabel}`
    }

    for (let lane = 0; lane < lanes.length; lane += 1) {
      const band = scene.querySelector<HTMLElement>(`[data-band="${lane}"]`)
      if (band) {
        band.style.height = `${bandHeight}px`
        band.style.left = `${lane * geometry.lanePitch - 14}px`
        band.style.width = `${geometry.laneWidth + 28}px`
        band.classList.toggle("is-rollable", (floorsRef.current[lane] ?? 0) < -1)
      }
      const head = scene.querySelector<HTMLElement>(`[data-lane-head="${lane}"]`)
      if (head) head.style.left = `${lane * geometry.lanePitch}px`
    }

    // Selected cards keep their expanded body. Read the actual rendered heights before positioning the lane so
    // a wrapped identity cannot cover the next direct card; the same measured map is consumed by framing and
    // label obstacles below.
    const positions = positionsForNodes(nodes, geometry, offsets.current, measuredCardHeights, revealDeltas.current)
    for (const node of nodes) {
      const position = positions.get(node.id) ?? nodePosition(node, geometry, offsets.current)
      const card = cardRefs.current.get(node.id)
      if (!card) continue
      card.style.transform = `translate(${position.x}px,${position.y}px)`
      card.style.width = `${geometry.laneWidth}px`
      /**
       * A card is drawn while it is inside its lane's window *and* inside the area the board actually has.
       *
       * The horizontal half of this is new, and it is the same rule rather than a second one. `box` already
       * excludes whatever a docked detail panel is covering, so a card outside it horizontally is a card the
       * reader cannot use — and leaving it drawn is precisely the §6.6 failure of a linked record sitting
       * underneath the panel. Since the §10.1 landing floor forbids zooming out to make a wide web fit beside
       * the panel, some cards genuinely cannot be brought into that area, and the honest treatment is the one
       * a rolled-out card already gets: faded, not tabbable, not pretending to be readable.
       */
      const left = position.x * transform.current.zoom + transform.current.x
      const right = left + geometry.laneWidth * transform.current.zoom
      // Wholly inside, not merely overlapping: a card straddling the panel edge is still a card the panel is
      // covering, and §6.6 admits no partial version of that.
      const inFrame = left >= box.x - 1 && right <= box.x + box.width + 1
      const top = position.y * transform.current.zoom + transform.current.y
      const bottom = top + (card.offsetHeight || geometry.cardHeight) * transform.current.zoom
      const fullyVisible = inFrame && top >= box.y - 1 && bottom <= box.y + box.height + 1 && isVisible(position.y, geometry, bandHeight)
      const indicator = offscreenRefs.current.get(node.id)
      if (indicator) {
        const filtered = Boolean(card.querySelector(".is-filtered"))
        indicator.hidden = fullyVisible && !filtered
        indicator.disabled = filtered
        indicator.textContent = `${filtered ? "Excluded by filters:" : "Show"} ${card.querySelector(".dtnId, .dticId, .dtaId, .exactArtifactLink, strong")?.textContent ?? "connected record"}`
      }
      card.classList.toggle(
        "is-offscreen",
        (!isVisible(position.y, geometry, bandHeight) || !inFrame) && selectedId !== node.id,
      )
      const offscreen = card.classList.contains("is-offscreen")
      // Descendant links/buttons are real native actions, but an offscreen card must not remain a hidden tab
      // target. Remember each authored tabindex and restore it when lane rolling reveals the card again.
      card.querySelectorAll<HTMLElement>("a,button,input,select,textarea,summary,[role='link']").forEach(control => {
        if (offscreen) {
          if (control.dataset.dtOriginalTabIndex === undefined) {
            control.dataset.dtOriginalTabIndex = control.getAttribute("tabindex") ?? ""
          }
          control.tabIndex = -1
        } else if (control.dataset.dtOriginalTabIndex !== undefined) {
          const original = control.dataset.dtOriginalTabIndex
          if (original) control.setAttribute("tabindex", original)
          else control.removeAttribute("tabindex")
          delete control.dataset.dtOriginalTabIndex
        }
      })
    }

    // Tab stops are authored here, from the positions just written, because a lane rolls under the pointer
    // without React re-rendering. React's tabIndex is the starting point; a card that has since been rolled
    // out of its window must lose the stop, or a keyboard user tabs into something faded out and unreachable
    // by eye. Opacity and pointer-events do not remove an element from the tab order — only tabindex does.
    for (const [lane, bucket] of byLaneRef.current) {
      /**
       * Drawn means vertically inside the lane window *and* horizontally inside the free frame, the same
       * two-part rule the fade above uses. Using only the vertical half let Tab land on a card the canvas had
       * hidden horizontally — a stop at opacity 0, which is the focus trap §6.9 forbids.
       */
      const drawn = bucket.filter(candidate => {
        const position = positions.get(candidate.id)
        if (!position) return false
        const left = position.x * transform.current.zoom + transform.current.x
        const right = left + geometry.laneWidth * transform.current.zoom
        return isVisible(position.y, geometry, bandHeight)
          && left >= box.x - 1 && right <= box.x + box.width + 1
      })
      const remembered = rovingRef.current[lane]
      const stop =
        (remembered && drawn.some(candidate => candidate.id === remembered) ? remembered : null) ??
        drawn[0]?.id ??
        // A lane entirely outside the free frame keeps a stop rather than losing it: dropping it would make
        // that lane unreachable by keyboard, and `onFocus` reveals the card before focus rests on it.
        bucket[0]?.id ??
        null
      for (const candidate of bucket) {
        const card = cardRefs.current.get(candidate.id)
        if (card) card.tabIndex = candidate.id === stop ? 0 : -1
      }
    }

    const svg = edgeLayerRef.current
    if (svg) {
      // The right margin carries the intra-lane overhang as well as the usual bleed: an edge inside the final
      // lane bows past the board's own width, and sizing this to the board alone clipped the curve and its
      // label off the end of the canvas.
      const width = result.sceneWidth + 26 + EDGE_LAYER_OVERHANG
      svg.setAttribute("width", String(width))
      svg.setAttribute("height", String(bandHeight + 82))
      svg.setAttribute("viewBox", `-26 -56 ${width} ${bandHeight + 82}`)
      svg.style.left = "-26px"
      svg.style.top = "-56px"
    }
    // Label obstacles come from the rendered cards, including dimmed context cards. This is intentionally
    // measured after positions/classes are written, so a selected card's expanded body is an actual obstacle.
    const cardObstacles = nodes.flatMap(node => {
      const card = cardRefs.current.get(node.id)
      const position = positions.get(node.id)
      if (!card || !position || card.classList.contains("is-offscreen")) return []
      return [{
        x: position.x,
        y: position.y,
        width: geometry.laneWidth,
        height: Math.max(geometry.cardHeight, card.offsetHeight || card.scrollHeight),
      }]
    })
    const labelsAtRest = transform.current.zoom > 1.05
    const currentZoom = transform.current.zoom || 1
    const shownEdge = (entry: (typeof edgeRefs.current)[number]): boolean => {
      if (!entry.label) return false
      const from = positions.get(entry.edge.from)
      const to = positions.get(entry.edge.to)
      if (!from || !to) return false
      const traced = tracedEdges?.has(edgeIdentity(entry.edge.from, entry.edge.to)) ?? false
      const inWindow = (position: { x: number; y: number }) => {
        const y = position.y + geometry.anchor
        return y > -20 && y < bandHeight + 20
      }
      const inHorizontalWindow = (position: { x: number; y: number }) => {
        const left = position.x * currentZoom + transform.current.x
        const right = left + geometry.laneWidth * currentZoom
        return right > box.x - 20 && left < box.x + box.width + 20
      }
      // Only visible labels take placement slots. Dimmed cards remain obstacles above, while untraced/resting
      // labels that the next loop hides must not make a crowded frame appear exhausted.
      return (traced || labelsAtRest) && inWindow(from) && inWindow(to) &&
        (inHorizontalWindow(from) || inHorizontalWindow(to))
    }
    const labelCandidates = edgeRefs.current
      .filter(shownEdge)
      .sort((a, b) => {
        const aTraced = tracedEdges?.has(edgeIdentity(a.edge.from, a.edge.to)) ?? false
        const bTraced = tracedEdges?.has(edgeIdentity(b.edge.from, b.edge.to)) ?? false
        return Number(bTraced) - Number(aTraced)
      })
      .map(entry => {
        const bounds = (() => {
          try {
            return entry.label?.getBBox()
          } catch {
            return undefined
          }
        })()
        return {
          key: edgeIdentity(entry.edge.from, entry.edge.to),
          label: entry.edge.label,
          from: positions.get(entry.edge.from)!,
          to: positions.get(entry.edge.to)!,
          // SVG gives us the real rendered text width in scene units. A character-count estimate is too wide
          // for the narrow gutter between two cards and turns a valid connector slot into false exhaustion.
          width: bounds && Number.isFinite(bounds.width) && bounds.width > 0 ? bounds.width : undefined,
          height: bounds && Number.isFinite(bounds.height) && bounds.height > 0 ? bounds.height : undefined,
        }
      })
    const viewportRect = viewportRef.current?.getBoundingClientRect()
    const zoom = currentZoom
    const toSceneRect = (rect: DOMRect): CanvasRect | null => {
      if (!viewportRect) return null
      return {
        x: (rect.left - viewportRect.left - transform.current.x) / zoom,
        y: (rect.top - viewportRect.top - transform.current.y) / zoom,
        width: rect.width / zoom,
        height: rect.height / zoom,
      }
    }
    // Labels are SVG scene coordinates. Convert the free frame and every untransformed/DOM-measured obstacle to
    // that same coordinate space before collision testing; mixing viewport pixels with scene units lets labels
    // appear clear in one pan position and land over a card in another.
    const sceneFrame: CanvasRect = {
      x: (box.x - transform.current.x) / zoom,
      y: (box.y - transform.current.y) / zoom,
      width: box.width / zoom,
      height: box.height / zoom,
    }
    const domObstacles = [
      ...Array.from(scene.querySelectorAll<HTMLElement>(".dtCanvasLaneHead")),
      scene.querySelector<HTMLElement>(".dtCanvasControls"),
    ].flatMap(element => {
      const rect = element?.getBoundingClientRect()
      const converted = rect ? toSceneRect(rect) : null
      return converted ? [converted] : []
    })
    const labelPositions = placeEdgeLabels(labelCandidates, geometry, [...cardObstacles, ...domObstacles], sceneFrame)
    // A completely occupied frame is a layout shortfall, not permission to paint a colliding midpoint. Ask the
    // owning view to re-dock its inspector, using the same measured-room recovery as direct cards; the current
    // placement remains explicitly marked exhausted until that repaint supplies a real free slot.
    const placementNotice = viewportRef.current?.querySelector<HTMLElement>(".dtCanvasPlacementNotice")
    if (placementNotice) {
      const unavailable = [...labelPositions.values()].some(position => !position.available)
      placementNotice.hidden = !unavailable
      placementNotice.textContent = unavailable
        ? "A relation label cannot fit without covering other content. Enlarge the canvas to show it on its connector."
        : ""
    }
    // Edge labels rest hidden and appear on a traced edge, or once the board is zoomed past 1.05 (#880 §6.7).
    // At the default fit the canvas stays calm; a reader who has selected something, or leaned in, gets the
    // relation words.
    for (const { path, dot, leader, label, edge } of edgeRefs.current) {
      const from = positions.get(edge.from)
      const to = positions.get(edge.to)
      if (!from || !to) continue
      const position = labelPositions.get(edgeIdentity(edge.from, edge.to))
      path.setAttribute("d", edgePath(from, to, geometry, position?.route))
      const backwards = to.x <= from.x
      dot.setAttribute("cx", String(backwards ? to.x + geometry.laneWidth : to.x))
      dot.setAttribute("cy", String(to.y + geometry.anchor))
      const inWindow =
        from.y + geometry.anchor > -20 &&
        from.y + geometry.anchor < bandHeight + 20 &&
        to.y + geometry.anchor > -20 &&
        to.y + geometry.anchor < bandHeight + 20

      // A trace is active only when the caller passes a set. Undefined leaves every edge at rest, which is a
      // different state from a trace that reaches nothing.
      const traced = tracedEdges?.has(edgeIdentity(edge.from, edge.to)) ?? false
      const traceActive = tracedEdges !== undefined
      path.classList.toggle("is-traced", traced)
      dot.classList.toggle("is-traced", traced)
      // Untraced edges recede while a trace is active rather than disappearing: the reader keeps the shape of
      // the build around what they selected.
      path.classList.toggle("is-untraced", traceActive && !traced)
      dot.classList.toggle("is-untraced", traceActive && !traced)

      path.style.opacity = inWindow || traced ? "" : "0.06"
      dot.style.opacity = path.style.opacity
      if (label) {
        // An intra-lane edge bows into the gutter beside its lane, so its label follows it there. Taking the
        // midpoint of the two endpoints would put the word in the middle of the lane, on top of the very cards
        // the edge is drawn between.
        if (position) {
          label.setAttribute("x", String(position.x))
          label.setAttribute("y", String(position.y))
          label.dataset.edgePlacement = position.exhausted ? "exhausted" : "clear"
        }
        if (leader) {
          leader.setAttribute("x1", String(position?.anchorX ?? 0))
          leader.setAttribute("y1", String(position?.anchorY ?? 0))
          leader.setAttribute("x2", String(position?.x ?? 0))
          leader.setAttribute("y2", String(position?.y ?? 0))
          leader.style.opacity = inWindow && (traced || labelsAtRest) && position?.leader ? "" : "0"
        }
        // Labels that were outside the current horizontal window have no placement entry. Keep them hidden until
        // the same shownEdge filter admits them; otherwise the SVG's initial coordinates can leak a stale phrase
        // into the frame after a lane roll or dock transition.
        label.style.opacity = inWindow && (traced || labelsAtRest) && position?.available === true ? "" : "0"
      }
    }
  }, [counts, emphasisId, frame, lanes.length, nodes, onFramingNeedsRoom, scopeKey, selectedId, sourceSignature, story, trailingOverhang, tracedEdges])

  // A lane's animation can outlive the render that started it (focus is followed by selection).
  // Paint the committed selection rather than letting an older tick restore stale card visibility.
  const committedPaint = useRef(paint)
  useLayoutEffect(() => { committedPaint.current = paint }, [paint])

  // ResizeObserver callbacks run during the browser's resize notification phase. Defer the repaint to the next
  // frame so writing corrected card positions cannot trigger a resize-observer loop. This also coalesces a font
  // loading event with the card resize notifications it causes.
  const schedulePaint = useCallback(() => {
    if (reflowFrame.current !== null) return
    reflowFrame.current = requestAnimationFrame(() => {
      reflowFrame.current = null
      committedPaint.current()
    })
  }, [])

  const settle = useCallback(() => {
    /**
     * Step the temporary per-card displacements toward their plan.
     *
     * Entries whose target is zero are dropped the moment they arrive, so retiring geometry actually
     * finishes rather than being frozen somewhere off its ordinary row — the cleanup is mandatory and does
     * not depend on the camera channel or on any later pointer gesture.
     */
    const stepReveal = (snap = false): boolean => {
      const keys = new Set([...revealDeltas.current.keys(), ...revealTargets.current.keys()])
      const next = new Map<string, number>()
      let moving = false
      for (const id of keys) {
        const current = revealDeltas.current.get(id) ?? 0
        const target = revealTargets.current.get(id) ?? 0
        if (snap) {
          if (target !== 0) next.set(id, target)
          continue
        }
        const delta = target - current
        if (Math.abs(delta) <= 0.4) {
          if (target !== 0) next.set(id, target)
          continue
        }
        moving = true
        next.set(id, current + delta * 0.22)
      }
      revealDeltas.current = next
      /**
       * A lane is delivered when its planned displacements have actually arrived (or the reader froze them
       * there). Scheduling alone must never count: an incoming reveal that is still moving is not yet
       * "visited", so the next plan may not replace it with the values it happens to be passing through.
       */
      if (!moving) {
        const byLane = new Map<number, boolean>()
        for (const [id, target] of revealTargets.current) {
          const node = nodes.find(candidate => candidate.id === id)
          const lane = node?.lane
          if (lane === undefined) continue
          byLane.set(lane, (byLane.get(lane) ?? true) && Math.abs((next.get(id) ?? 0) - target) <= 0.4)
        }
        for (const [lane, arrived] of byLane) {
          if (arrived) {
            deliveredLanes.current.add(lane)
            visitedLanes.current.add(lane)
          }
        }
      }
      return moving
    }
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      if (animation.current !== null) cancelAnimationFrame(animation.current)
      animation.current = null
      offsets.current = [...targets.current]
      stepReveal(true)
      committedPaint.current()
      return
    }
    if (animation.current !== null) return
    const tick = () => {
      const stepped = stepTowards(offsets.current, targets.current)
      offsets.current = stepped.offsets
      const revealMoving = stepReveal()
      committedPaint.current()
      animation.current = stepped.moving || revealMoving || scrubbing.current ? requestAnimationFrame(tick) : null
    }
    animation.current = requestAnimationFrame(tick)
  }, [])
  kickMotion.current = settle

  /**
   * Land the board.
   *
   * Two callers with different rules, so they are two functions rather than one with a hidden meaning.
   * `land()` is what the product does on arrival and on a re-fit the reader did not ask for, and #880 §10.1
   * holds it to the legibility floor. `fitAll()` is the reader explicitly asking to see the whole board —
   * keyboard `0`, or double-clicking empty canvas — and may pull back past that floor into the compact and
   * dense tiers, because shedding detail is exactly what they asked for.
   */
  const land = useCallback(() => {
    const box = frame()
    if (!box) return
    transform.current = fitTransform(box, counts)
    paint()
  }, [counts, frame, paint])

  const fitAll = useCallback(() => {
    const box = frame()
    if (!box) return
    // Fit the edge layer as well as the cards. The final lane's intra-lane connector deliberately extends into
    // the reserved overhang; leaving that space out of this explicit overview fit makes the scene's DOM box
    // protrude past the viewport even though every card appears to fit.
    const fitBox = trailingOverhang
      ? { ...box, width: Math.max(240, box.width - trailingOverhang) }
      : box
    transform.current = fitTransform(fitBox, counts, false)
    paint()
  }, [counts, frame, paint, trailingOverhang])

  /**
   * Reframe onto the selection and its direct links.
   *
   * This is the half of the panel rule that side-picking cannot do on its own: the board moves into the area
   * the panel is not covering, so a linked record cannot end up underneath it. It also gives the panel's
   * relation rows somewhere to go — clicking one selects that record and the board comes to it.
   *
   * Returns whether it actually ran. It cannot run before the host frame is real — `frame()` refuses a rect
   * that has not settled — and the caller uses that answer to decide whether the framing key has been dealt
   * with. Marking a key handled on a refused frame is how a focal record could land with a linked record still
   * rolled out of view: the retry path only ever called `fit()`, which moves the camera but does not roll a
   * lane, and no state change was pending to make the effect run again.
   */
  const applyFraming = useCallback(
    (
      target: { selectedId: string; wanted: string[]; intent: FrameIntent; key: string } | null,
      /** An explicit Fit is a reader command: it must never be swallowed by the automatic suitability rule. */
      explicit = false,
    ): boolean => {
      if (!target) return false
      const box = frame()
      if (!box || !geometryRef.current) return false

      // Read every requested card's actual layout height before choosing a camera. Wrapped identifiers and
      // state pills can make a direct card taller than its nominal tier height; the measured border box keeps
      // another direct record clear after a trace selection.
      const cardHeights = new Map<string, number>()
      for (const node of nodes) {
        const card = cardRefs.current.get(node.id)
        card?.classList.toggle("is-selected", target.selectedId === node.id)
        const measured = card?.offsetHeight || card?.scrollHeight
        if (measured && Number.isFinite(measured)) cardHeights.set(node.id, measured)
      }
      const result = layoutWithMeasuredCards(layout(counts, box, transform.current.zoom), nodes, cardHeights)

      /**
       * Reader-owned lane positions are not overridden.
       *
       * Selection no longer aligns every lane onto the anchor (#880 §6.4 is superseded by #1022): an
       * out-of-view linked record is brought to a useful height in its own lane by a temporary per-card
       * delta, so no lane is scrolled on the reader's behalf.
       */
      const floors = result.laneMinimums.map((minimum, lane) =>
        Math.min(minimum, deepestMinimum.current[lane] ?? minimum))
      offsets.current = clampOffsets(offsets.current, floors)
      targets.current = offsets.current.slice()

      /**
       * Leave a suitable view alone.
       *
       * Framing exists for the cases that need it. When the selected record is already wholly inside the
       * free frame, inside its lane window and above the readable floor, the camera does not move at all.
       */
      const selectedNode = nodes.find(node => node.id === target.selectedId)
      const heightOf = (id: string) => Math.max(result.geometry.cardHeight, cardHeights.get(id) ?? 0)
      if (selectedNode) {
        const position = positionsForNodes(nodes, result.geometry, offsets.current, cardHeights, revealDeltas.current)
          .get(selectedNode.id)
        if (position) {
          const zoom = transform.current.zoom
          const left = position.x * zoom + transform.current.x
          const top = position.y * zoom + transform.current.y
          const right = left + result.geometry.laneWidth * zoom
          const bottom = top + heightOf(selectedNode.id) * zoom
          const fullyVisible = isVisible(position.y, result.geometry, result.bandHeight) &&
            left >= box.x - 1 && right <= box.x + box.width + 1 &&
            top >= box.y - 1 && bottom <= box.y + box.height + 1
          if (!explicit && fullyVisible && zoom >= READABLE_SELECTION_MIN_ZOOM) return true
        }
      }

      // The selected card's expanded body is measured from the rendered DOM. The old fixed allowance made a
      // larger card overlap the panel and made a shorter card reserve unnecessary empty space.
      const selectedCardHeight = cardHeights.get(target.selectedId)
      const next = frameNodes(
        target.wanted,
        nodes,
        counts,
        box,
        offsets.current,
        target.selectedId,
        1.12,
        true,
        { intent: target.intent, selectedCardHeight, cardHeights },
      )
      if (!next) return false

      /**
       * The selection and every direct link must actually be drawn, wholly inside the free area.
       *
       * §6.6 is a guarantee, not a preference, and it survived the Option-A ruling untouched. Hiding a linked
       * record that will not fit satisfies "not underneath the panel" only by making it not present, which is
       * the same failure wearing a different face. When the free area this dock leaves cannot hold the selected
       * record and its direct links at the readable floor, the panel has to move rather than the record disappear — so the
       * canvas says so and the view re-docks. Reported rather than decided here: the canvas owns geometry,
       * the view owns where its own panel may go.
       */
      // Direct links are no longer required to be simultaneously drawn: #1022 accepts clearly indicated
      // off-screen links with a working reveal path, so the redock demand is limited to the selected record
      // itself being unusable.
      const selectedPosition = selectedNode
        ? positionsForNodes(nodes, layoutWithMeasuredCards(layout(counts, box, next.zoom), nodes, cardHeights).geometry,
            offsets.current, cardHeights, revealDeltas.current).get(selectedNode.id)
        : undefined
      if (selectedNode && selectedPosition) {
        const zoom = next.zoom
        const left = selectedPosition.x * zoom + next.x
        const top = selectedPosition.y * zoom + next.y
        const right = left + result.geometry.laneWidth * zoom
        const bottom = top + heightOf(selectedNode.id) * zoom
        const usable = left >= box.x - 1 && right <= box.x + box.width + 1 &&
          top >= box.y - 1 && bottom <= box.y + box.height + 1
        if (!usable) onFramingNeedsRoom?.()
      }

      sceneRef.current?.classList.add("is-easing")
      transform.current = next
      paint()
      // The new zoom can change row pitch and card height. Reconcile the anchor against that actual tier,
      // rather than leaving an expanded lower-row card beneath the dock after offsets have been rescaled.
      for (const node of nodes) {
        const card = cardRefs.current.get(node.id)
        const measured = card?.offsetHeight || card?.scrollHeight
        if (measured && Number.isFinite(measured)) cardHeights.set(node.id, measured)
      }
      const settledLayout = layoutWithMeasuredCards(layout(counts, box, next.zoom), nodes, cardHeights)
      offsets.current = clampOffsets(offsets.current, settledLayout.laneMinimums.map((minimum, lane) =>
        Math.min(minimum, deepestMinimum.current[lane] ?? minimum)))
      targets.current = offsets.current.slice()
      const settledFrame = frameNodes(target.wanted, nodes, counts, box, offsets.current, target.selectedId,
        next.zoom, true, { intent: target.intent, selectedCardHeight: cardHeights.get(target.selectedId), cardHeights })
      if (settledFrame) {
        transform.current = settledFrame
        paint()
        
      }
      if (easeTimer.current !== null) window.clearTimeout(easeTimer.current)
      easeTimer.current = window.setTimeout(() => {
        sceneRef.current?.classList.remove("is-easing")
        easeTimer.current = null
      }, 420)
      return true
    },
     [counts, edges, frame, nodes, onFramingNeedsRoom, paint],
  )

  // Read by the resize path, which must be able to retry a selection that arrived before the frame was real
  // without re-subscribing its observer every time the selection changes.
  framingRef.current = framing

  // Lay out once the frame is real, and again whenever it changes size.
  useLayoutEffect(() => {
    const element = viewportRef.current
    if (!element) return undefined
    const measure = () => {
      const rect = element.getBoundingClientRect()
      if (rect.width < 100) return
      const signature = `${Math.round(rect.width)}x${Math.round(rect.height)}x${countsKey}`
      if (signature !== frameSignature.current) {
        frameSignature.current = signature
        land()
      }

      // A selection can arrive while the host frame is still unsettled — a freshly mounted panel or a preview
      // reports a rect a fraction of its real size, and `frame()` refuses it. `fit()` alone is not the repair:
      // it moves the camera but does not roll a tall lane, so a directly linked record would stay outside its
      // window after the frame settled. Retried here because the settling resize needs no React state change,
      // so nothing else would run the framing effect again.
      const pending = framingRef.current
      if (pending && framedFor.current !== pending.key && applyFraming(pending)) {
        framedFor.current = pending.key
      }
    }
    measure()
    const timers = [window.setTimeout(measure, 50), window.setTimeout(measure, 350)]
    const observer = typeof ResizeObserver === "function"
      ? new ResizeObserver(entries => {
        // The viewport observer keeps the existing frame and dock behavior. Cards need a deferred paint of
        // their own: web-font substitution can change a selected card's border box without changing the frame.
        if (entries.some(entry => entry.target !== element)) schedulePaint()
        if (entries.some(entry => entry.target === element)) measure()
      })
      : null
    observer?.observe(element)
    cardRefs.current.forEach(card => observer?.observe(card))
    const fonts = document.fonts
    const onFontEvent = () => schedulePaint()
    fonts?.addEventListener("loadingdone", onFontEvent)
    fonts?.addEventListener("loadingerror", onFontEvent)
    // A font can finish between the initial measure and listener registration. The ready promise covers that
    // settled state, while loadingdone above handles later faces requested by a view.
    void fonts?.ready.then(onFontEvent, onFontEvent)
    window.addEventListener("resize", measure)
    return () => {
      timers.forEach(window.clearTimeout)
      observer?.disconnect()
      if (reflowFrame.current !== null) {
        window.cancelAnimationFrame(reflowFrame.current)
        reflowFrame.current = null
      }
      fonts?.removeEventListener("loadingdone", onFontEvent)
      fonts?.removeEventListener("loadingerror", onFontEvent)
      window.removeEventListener("resize", measure)
    }
  }, [applyFraming, cardIdsKey, countsKey, land, schedulePaint])

  useEffect(() => {
    paint()
  }, [paint])


  useEffect(() => {
    if (!framing) {
      framedFor.current = null
      return
    }
    if (framedFor.current === framing.key) return
    // Consumed only once the framing has actually applied. If the frame is not usable yet the key stays
    // pending, and the resize path retries it the moment a real rect arrives.
    if (applyFraming(framing)) framedFor.current = framing.key
  }, [applyFraming, framing, paint])

  useEffect(
    () => () => {
      if (easeTimer.current !== null) window.clearTimeout(easeTimer.current)
      if (previewTimer.current !== null) clearTimeout(previewTimer.current)
    },
    [],
  )

  useEffect(
    () => () => {
      if (animation.current !== null) cancelAnimationFrame(animation.current)
    },
    [],
  )

  const onWheel = useCallback(
    (event: React.WheelEvent<HTMLDivElement>) => {
      const box = frame()
      const element = viewportRef.current
      if (!box || !element) return
      event.preventDefault()
      if (event.shiftKey) {
        transform.current = { ...transform.current, x: transform.current.x - event.deltaY }
        paint()
        return
      }
      const rect = element.getBoundingClientRect()
      transform.current = zoomAbout(
        transform.current,
        event.clientX - rect.left,
        event.clientY - rect.top,
        wheelFactor(event.deltaY),
        minimumZoom(box, counts),
      )
      paint()
    },
    [counts, frame, paint],
  )

  const onPointerDown = useCallback(
    (event: React.PointerEvent<HTMLDivElement>) => {
      if (event.button !== 0) return
      if (previewTimer.current !== null) clearTimeout(previewTimer.current)
      previewTimer.current = null
      // A nested card action has its own click/default-action semantics. Returning before pointer capture keeps
      // the viewport from consuming its eventual pointerup as a card selection (F4).
      if (nestedControl(event.target)) return
      const element = viewportRef.current
      const result = geometryRef.current
      if (!element || !result) return
      const card = (event.target as HTMLElement).closest<HTMLElement>("[data-node-id]")
      const rect = element.getBoundingClientRect()
      const sceneX = (event.clientX - rect.left - transform.current.x) / transform.current.zoom
      const sceneY = (event.clientY - rect.top - transform.current.y) / transform.current.zoom
      const lane =
        card || sceneY < -10 || sceneY > result.bandHeight + 10
          ? -1
          : laneAt(sceneX, lanes.length, result.geometry)
      // Read the resolved bound here rather than trusting a stale capture: geometry can change during a
      // gesture, and the lane's roll range — including any retained allowance — must follow the arrangement
      // the reader can actually see.
      const laneFloor = (target: number) => floorsRef.current[target] ?? result.laneMinimums[target] ?? 0
      const rollable = lane >= 0 && laneFloor(lane) < -1
      const start = {
        x: event.clientX,
        y: event.clientY,
        tx: transform.current.x,
        ty: transform.current.y,
        offset: lane >= 0 ? (offsets.current[lane] ?? 0) : 0,
        moved: false,
      }
      element.setPointerCapture(event.pointerId)
      element.classList.add(card ? "is-idle" : rollable ? "is-rolling" : "is-panning")

      const move = (moveEvent: PointerEvent) => {
        const dx = moveEvent.clientX - start.x
        const dy = moveEvent.clientY - start.y
        if (!start.moved && Math.abs(dx) + Math.abs(dy) > 4) start.moved = true
        if (!start.moved) return
        if (rollable) {
          scrubbing.current = true
          // The reader now owns this lane for the current context: the reveal never re-plans it, so scrolling
          // through the revealed cards cannot be undone by the next paint.
          frozenLanes.current.add(lane)
          offsets.current[lane] = Math.max(
            laneFloor(lane),
            Math.min(0, start.offset + dy / transform.current.zoom),
          )
          targets.current[lane] = offsets.current[lane]
          // Deliberate lane scrolling no longer drags other lanes into alignment: #1022 keeps the reader's
          // camera and every other lane exactly where they are.
          settle()
          return
        }
        transform.current = { ...transform.current, x: start.tx + dx, y: start.ty + dy }
        // A deliberate vertical or diagonal camera move is exploration too: the thread's cards in the lanes
        // the reader can see stay where they are instead of being re-homed on the next horizontal move.
        if (Math.abs(dy) > 8) {
          for (const id of revealDeltas.current.keys()) {
            const node = nodes.find(candidate => candidate.id === id)
            if (node) frozenLanes.current.add(node.lane)
          }
        }
        paint()
      }
      const up = (upEvent: PointerEvent) => {
      element.classList.remove("is-panning", "is-rolling", "is-idle")
        scrubbing.current = false
        if (!start.moved && upEvent.type !== "pointercancel") onSelect?.(card?.dataset.nodeId ?? null)
        element.removeEventListener("pointermove", move)
        element.removeEventListener("pointerup", up)
        element.removeEventListener("pointercancel", up)
      }
      element.addEventListener("pointermove", move)
      element.addEventListener("pointerup", up)
      element.addEventListener("pointercancel", up)
    },
    [edges, lanes.length, nodes, onSelect, paint, settle],
  )

  /** Cards per lane in row order: the sequence the arrow keys walk. */
  const byLane = useMemo(() => {
    const map = new Map<number, CanvasNode[]>()
    for (const node of nodes) {
      const bucket = map.get(node.lane)
      if (bucket) bucket.push(node)
      else map.set(node.lane, [node])
    }
    for (const bucket of map.values()) bucket.sort((a, b) => a.row - b.row)
    return map
  }, [nodes])

  /** Cards in lane-then-row order: the sequence Tab and the arrows follow. */
  const domOrdered = useMemo(
    () => [...nodes].sort((a, b) => a.lane - b.lane || a.row - b.row),
    [nodes],
  )

  rovingRef.current = roving
  byLaneRef.current = byLane

  /** The card holding this lane's tab stop: the remembered one, else the lane's first. */
  const rovingFor = useCallback(
    (lane: number): string | undefined => {
      const bucket = byLane.get(lane)
      if (!bucket?.length) return undefined
      const remembered = roving[lane]
      return remembered && bucket.some((node: CanvasNode) => node.id === remembered) ? remembered : bucket[0].id
    },
    [byLane, roving],
  )

  /**
   * Arrow navigation within a lane, rolling the lane so the newly focused card is actually visible.
   *
   * Moving focus without rolling would leave a keyboard user on a card that is faded out and unreachable by
   * eye, which is the failure #880 §6.9 calls out.
   */
  /**
   * Bring one card fully into view: roll its lane, and pan the camera to its lane.
   *
   * Both halves are needed, and each was missing once. Rolling answers "is it inside its lane window";
   * since #880 §10.1 holds automatic landings to the legibility floor, a board can be wider than the
   * viewport, so the lane itself can sit outside the free frame and the camera has to travel as well. §6.9
   * is that focus never rests on a card the reader cannot see, and that has to hold however focus arrived —
   * by arrow within a lane, or by Tab across lanes.
   */
  const reveal = useCallback(
    (node: CanvasNode) => {
      const result = geometryRef.current
      if (!result) return
      // Explicitly revealing a record is deliberate navigation: the reader owns that lane from here on.
      frozenLanes.current.add(node.lane)
      const measuredHeights = new Map<string, number>()
      for (const candidate of nodes) {
        const card = cardRefs.current.get(candidate.id)
        const height = card?.offsetHeight || card?.scrollHeight
        if (height && Number.isFinite(height)) measuredHeights.set(candidate.id, height)
      }
      const measuredPosition = positionsForNodes(nodes, result.geometry, offsets.current, measuredHeights, revealDeltas.current).get(node.id)
      const revealed = offsetToReveal(
        node.row,
        result.geometry,
        result.bandHeight,
        offsets.current[node.lane] ?? 0,
        measuredPosition?.y,
      )
      // Never past what the lane can actually roll, or the lane would scroll off its own content.
      targets.current[node.lane] = Math.max(
        floorsRef.current[node.lane] ?? result.laneMinimums[node.lane] ?? 0,
        revealed,
      )
      // Setting the target is not moving the lane. The easing loop was only ever started by the pointer
      // scrub, so keyboard navigation set a target nothing consumed — rolling appeared to work only while
      // the card it moved to happened to need no roll at all.
      settle()

      const box = frame()
      // `.dtCanvas` is a transformed viewport, never a native document scrollport. Some browsers still retain a
      // programmatic scroll offset after focusing an offscreen descendant; clear that stale offset before the
      // camera correction below so keyboard reveal cannot leave a blank scene.
      viewportRef.current?.scrollTo({ top: 0, left: 0, behavior: "instant" as ScrollBehavior })
      if (!box) return
      const { x } = measuredPosition ?? nodePosition(node, result.geometry, offsets.current)
      const left = x * transform.current.zoom + transform.current.x
      const right = left + result.geometry.laneWidth * transform.current.zoom
      const margin = 16
      if (left < box.x + margin) transform.current.x += box.x + margin - left
      else if (right > box.x + box.width - margin) transform.current.x -= right - (box.x + box.width - margin)
      paint()
    },
    [frame, nodes, paint, settle],
  )

  /** Arrow navigation within a lane, revealing the card it moves to. */
  const moveWithinLane = useCallback(
    (node: CanvasNode, delta: number) => {
      const bucket = byLane.get(node.lane)
      if (!bucket?.length) return
      // Arrow keys walk the arrangement the reader can see, not the canonical row order: a temporarily
      // displaced card sits where it is painted, and moving focus through a different order would jump.
      const result = geometryRef.current
      const ordered = result
        ? [...bucket].sort((a, b) => {
            // The same measured heights paint used: walking an ordering built from different geometry is
            // exactly the "focus contradicts the display" failure the plan forbids.
            const positions = positionsForNodes(
              nodes, result.geometry, offsets.current, measuredHeightsRef.current, revealDeltas.current,
            )
            const ay = positions.get(a.id)?.y ?? 0
            const by = positions.get(b.id)?.y ?? 0
            return ay - by || a.row - b.row || a.id.localeCompare(b.id)
          })
        : bucket
      const index = ordered.findIndex((candidate: CanvasNode) => candidate.id === node.id)
      const next = ordered[Math.min(ordered.length - 1, Math.max(0, index + delta))]
      if (!next || next.id === node.id) return
      setRoving(current => ({ ...current, [node.lane]: next.id }))
      reveal(next)
      cardRefs.current.get(next.id)?.focus({ preventScroll: true })
    },
    [byLane, nodes, reveal],
  )

  const onKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLDivElement>) => {
      const box = frame()
      if (!box) return
      if (event.key === "0") {
        fitAll()
      } else if (event.key === "+" || event.key === "=" || event.key === "-") {
        transform.current = zoomAbout(
          transform.current,
          box.width / 2,
          box.height / 2,
          event.key === "-" ? 0.81 : 1.24,
          minimumZoom(box, counts),
        )
        paint()
      } else if (event.key === "Escape") {
        onSelect?.(null)
      } else {
        return
      }
      event.preventDefault()
    },
    [counts, fitAll, frame, onSelect, paint],
  )

  edgeRefs.current = []

  const fitSelection = () => {
    if (!framing) return
    const target = { ...framing, intent: "selection" as FrameIntent, key: `${framing.key}|fit-selection` }
    if (applyFraming(target, true)) framedFor.current = framing.key
  }

  const fitStory = () => {
    if (!framing) return
    const target = { ...framing, intent: "story" as FrameIntent, key: `${framing.key}|fit-story` }
    // The manual camera choice satisfies this selection's pending automatic framing too. Recording the
    // synthetic action key instead would replay the landing on the next hover/render.
    if (applyFraming(target, true)) framedFor.current = framing.key
  }

  return (
    <div
      className="dtCanvas"
      ref={viewportRef}
      role="group"
      aria-label={ariaLabel}
      tabIndex={0}
      onWheel={onWheel}
      onPointerDown={onPointerDown}
      onPointerLeave={exitHover}
      onFocusCapture={event => {
        if (!nestedControl(event.target)) return
        // Native focus remains native; only prevent the transformed wrapper from becoming its scroll owner.
        viewportRef.current?.scrollTo({ top: 0, left: 0, behavior: "instant" as ScrollBehavior })
      }}
      onKeyDown={onKeyDown}
      onDoubleClick={event => {
        if (!(event.target as HTMLElement).closest("[data-node-id]")) fitAll()
      }}
    >
      <div
        className="dtCanvasControls"
        role="toolbar"
        aria-label="Canvas framing controls"
        onPointerDown={event => event.stopPropagation()}
      >
        <button type="button" aria-label="Zoom out" title="Zoom out" onClick={() => {
          const box = frame()
          if (!box) return
          transform.current = zoomAbout(transform.current, box.width / 2, box.height / 2, 0.81, minimumZoom(box, counts))
          paint()
        }}>−</button>
        <output ref={zoomReadoutRef} aria-label="Current canvas scale">100% · Detailed</output>
        <button type="button" aria-label="Zoom in" title="Zoom in" onClick={() => {
          const box = frame()
          if (!box) return
          transform.current = zoomAbout(transform.current, box.width / 2, box.height / 2, 1.24, MIN_ZOOM)
          paint()
        }}>+</button>
        <span className="dtCanvasControlDivider" aria-hidden="true" />
        <button type="button" disabled={!framing} onClick={fitSelection}>Fit selected story</button>
        <button type="button" disabled={!framing} onClick={fitStory}>Fit entire story</button>
        <button type="button" onClick={fitAll} title="Fit the projected board; tall lanes remain independently scrollable">Fit board</button>
      </div>
      <div className="dtCanvasPlacementNotice" role="status" aria-live="polite" hidden />
      {story && <nav className="dtCanvasOffscreen" style={{ bottom: (inspectorInset?.bottom ?? 0) + 6 }} aria-label="Connected records outside view" onPointerDown={event => event.stopPropagation()}>
        {sourceNodes.filter(node => story.nodes.has(node.id)).map(({ id }) => <button key={id} type="button"
          ref={element => { if (element) offscreenRefs.current.set(id, element); else offscreenRefs.current.delete(id) }}
          onClick={() => { const node = nodes.find(candidate => candidate.id === id); if (node) reveal(node) }}>
          Show connected record
        </button>)}
      </nav>}
      <div className="dtCanvasScene" ref={sceneRef}>
        <div className="dtCanvasBands">
          {lanes.map((title, lane) => {
            const notice = laneNotice?.(lane) ?? null
            return (
              <div
                className={`dtCanvasBand${notice ? " is-filtered-empty" : ""}`}
                data-band={lane}
                key={`band-${title}`}
              >
                <i className="dtCanvasFadeTop" />
                <i className="dtCanvasFadeBottom" />
                {notice ? (
                  <p className="dtCanvasBandNotice" role="status">
                    {notice}
                  </p>
                ) : null}
              </div>
            )
          })}
          {lanes.map((title, lane) => (
            <div className="dtCanvasLaneHead" data-lane-head={lane} key={`head-${title}`}>
              {title}
              {counts[lane] > 1 ? <em>{counts[lane]}</em> : null}
            </div>
          ))}
        </div>
        <svg className="dtCanvasEdges" ref={edgeLayerRef} aria-hidden="true">
          {edges.map(edge => (
            <g key={`${edge.from}>${edge.to}>${edge.label}`}>
              <path
                fill="none"
                strokeLinecap="round"
                className={`dtCanvasEdge${edge.kind ? ` is-${edge.kind}` : ""}`}
                ref={element => {
                  if (!element) return
                  const dot = element.nextElementSibling as SVGCircleElement | null
                  const leader = dot?.nextElementSibling as SVGLineElement | null
                  const label = leader?.nextElementSibling as SVGTextElement | null
                  if (dot) edgeRefs.current.push({ path: element, dot, leader, label, edge })
                }}
              />
              <circle r="3" className={`dtCanvasEdgeDot${edge.kind ? ` is-${edge.kind}` : ""}`} />
              {edge.label ? (
                <>
                  <line className="dtCanvasEdgeLabelLeader" />
                  <text className="dtCanvasEdgeLabel" textAnchor="middle">
                    {edge.label}
                  </text>
                </>
              ) : null}
            </g>
          ))}
        </svg>
        <div className="dtCanvasNodes">
          {/* Rendered in lane then row order, because DOM order is tab order. The caller supplies nodes in
              whatever order its projection produced, and #880 §6.9 promises Tab moves between lanes in lane
              order — left to right along the ladder. Positions are written by the geometry pass, so ordering
              here costs nothing visually and makes the keyboard path deterministic. */}
          {domOrdered.map(node => (
            <div
              className="dtCanvasNode"
              key={node.id}
              data-node-id={node.id}
              // One tab stop per lane: Tab crosses lanes, the arrows walk within one. A card rolled out of its
              // lane window is never the stop, so focus cannot land somewhere the reader cannot see.
              tabIndex={rovingFor(node.lane) === node.id ? 0 : -1}
              role="button"
              aria-pressed={pinnedId === node.id}
              // Tab across lanes reveals too, not only arrows within one. A lane's stop can be outside the
              // free frame on a board wider than the viewport, and #880 §6.9 does not care how focus got
              // there: it must not rest on a card the reader cannot see. Revealing rather than dropping the
              // stop keeps every lane reachable by keyboard, which removing it would not.
              onFocus={event => {
                // A nested control has its own focus target. Revealing the parent card as that focus bubbles
                // through the canvas can move the lane between pointerdown and click, so preserve the native
                // control's activation geometry (F4).
                if (nestedControl(event.target)) return
                setRoving(current => ({ ...current, [node.lane]: node.id }))
                reveal(node)
              }}
              onKeyDown={event => {
                // Enter/Space on a nested button or link belongs to that control. Preventing the event here would
                // suppress its default activation and turn it into a card toggle instead (F4).
                if (nestedControl(event.target)) return
                if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                  event.preventDefault()
                  event.stopPropagation()
                  moveWithinLane(node, event.key === "ArrowDown" ? 1 : -1)
                  return
                }
                if (event.key !== "Enter" && event.key !== " ") return
                event.preventDefault()
                event.stopPropagation()
                // Activating a record selects it. Selecting the already-selected card is not an undocumented
                // toggle-off: clearing is the reader's explicit clear action (Escape or an empty-canvas click).
                onSelect?.(node.id)
              }}
              ref={element => {
                if (element) cardRefs.current.set(node.id, element)
                else cardRefs.current.delete(node.id)
              }}
              onPointerEnter={event => {
                // Once something is selected, no hover may replace or preview another thread — not even
                // after the dwell. The guard is on the persistent selection, not on the current emphasis.
                if (event.pointerType !== "mouse" || event.buttons || pinnedId || node.id === hoverId) return
                clearPreviewTimer()
                previewTimer.current = setTimeout(() => {
                  setHoverId(node.id)
                  onHover?.(node.id)
                }, 300)
              }}
              onPointerLeave={() => {
                clearPreviewTimer()
                // Leaving the card under the pointer ends only the temporary emphasis. It never touches the
                // camera, a persistent selection, or the reader's lane positions.
                setHoverId(current => (current === node.id ? null : current))
                onHover?.(null)
              }}
            >
              {renderCard(node)}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
