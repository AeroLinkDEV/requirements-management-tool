import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react"
import {
  EDGE_LAYER_OVERHANG,
  type CanvasEdge,
  type CanvasFrame,
  type CanvasRect,
  type CanvasNode,
  type FrameIntent,
  type LayoutResult,
  anchorInLane,
  clampOffsets,
  edgeIdentity,
  edgePath,
  offsetToReveal,
  fitTransform,
  frameNodes,
  isVisible,
  laneAt,
  layout,
  minimumZoom,
  MIN_ZOOM,
  nodePosition,
  placeEdgeLabels,
  rescaleOffsets,
  stepTowards,
  syncTargets,
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
  nodes,
  edges,
  renderCard,
  laneCount,
  laneNotice,
  selectedId = null,
  onSelect,
  onHover,
  frameInset,
  tracedEdges,
  frameIds,
  framingIntent = "selection",
  landingId = null,
  onFramingNeedsRoom,
  ariaLabel = "Digital Thread canvas",
}: DigitalThreadCanvasProps) {
  const viewportRef = useRef<HTMLDivElement | null>(null)
  const sceneRef = useRef<HTMLDivElement | null>(null)
  const edgeLayerRef = useRef<SVGSVGElement | null>(null)
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
    // Reserve the strip that was actually rendered, with a small measured breathing gap. A fixed 54px band
    // left most of the canvas empty at desktop widths and pushed the first lane heading away from its controls.
    const top = Math.max(40, Math.ceil(controlBottom - rect.top + 4))
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

    const result = layout(counts, box, transform.current.zoom)
    const previous = geometryRef.current
    if (previous && previous.tier !== result.tier) {
      offsets.current = rescaleOffsets(offsets.current, previous, result)
      targets.current = offsets.current.slice()
    }
    geometryRef.current = result
    while (offsets.current.length < lanes.length) offsets.current.push(0)
    offsets.current = clampOffsets(offsets.current, result.laneMinimums)

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
        band.classList.toggle("is-rollable", (result.laneMinimums[lane] ?? 0) < -1)
      }
      const head = scene.querySelector<HTMLElement>(`[data-lane-head="${lane}"]`)
      if (head) head.style.left = `${lane * geometry.lanePitch}px`
    }

    const positions = new Map<string, { x: number; y: number }>()
    for (const node of nodes) {
      const position = nodePosition(node, geometry, offsets.current)
      positions.set(node.id, position)
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
      // The density rules exempt the selected card from compaction, and they key off this class on the node
      // element. Without it the exemption silently never applied and a selected card compacted with the rest.
      card.classList.toggle("is-selected", selectedId === node.id)
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
        height: Math.max(geometry.cardHeight, card.scrollHeight),
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
    // Edge labels rest hidden and appear on a traced edge, or once the board is zoomed past 1.05 (#880 §6.7).
    // At the default fit the canvas stays calm; a reader who has selected something, or leaned in, gets the
    // relation words.
    for (const { path, dot, leader, label, edge } of edgeRefs.current) {
      const from = positions.get(edge.from)
      const to = positions.get(edge.to)
      if (!from || !to) continue
      path.setAttribute("d", edgePath(from, to, geometry))
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

      path.style.opacity = inWindow ? "" : "0.06"
      dot.style.opacity = path.style.opacity
      if (label) {
        // An intra-lane edge bows into the gutter beside its lane, so its label follows it there. Taking the
        // midpoint of the two endpoints would put the word in the middle of the lane, on top of the very cards
        // the edge is drawn between.
        const position = labelPositions.get(edgeIdentity(edge.from, edge.to))
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
        label.style.opacity = inWindow && (traced || labelsAtRest) ? "" : "0"
      }
    }
  }, [counts, frame, lanes.length, nodes, selectedId, trailingOverhang, tracedEdges])

  const settle = useCallback(() => {
    if (animation.current !== null) return
    const tick = () => {
      const stepped = stepTowards(offsets.current, targets.current)
      offsets.current = stepped.offsets
      paint()
      animation.current = stepped.moving || scrubbing.current ? requestAnimationFrame(tick) : null
    }
    animation.current = requestAnimationFrame(tick)
  }, [paint])

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
    (target: { selectedId: string; wanted: string[]; intent: FrameIntent; key: string } | null): boolean => {
      if (!target) return false
      const box = frame()
      const result = geometryRef.current
      if (!box || !result) return false

      // Read every requested card's actual layout height before choosing a camera. Wrapped identifiers and
      // state pills can make a direct card taller than its nominal tier height; using only the selected card's
      // scrollHeight leaves another direct record partly under the panel after a trace selection.
      const cardHeights = new Map<string, number>()
      for (const node of nodes) {
        const measured = cardRefs.current.get(node.id)?.scrollHeight
        if (measured && Number.isFinite(measured)) cardHeights.set(node.id, measured)
      }

      // Roll every lane to bring the selected record's directed story into its own windows, before framing
      // (#880 §6.4: "the same routine runs on selection"). Panning the camera cannot do this job: a lane
      // scrolls independently, so a linked record can sit outside its lane window no matter where the camera
      // is, and framing alone would centre on a card the reader still cannot see. The offsets are applied at
      // once rather than animated into place so the two-pass framing below measures where the cards landed.
      const synced = syncTargets(
        target.selectedId,
        nodes,
        edges,
        result.geometry,
        offsets.current,
        result.laneMinimums,
        counts.length,
        -1,
      )
      offsets.current = [...synced]
      targets.current = [...synced]

      const fits = (transform: { x: number; y: number; zoom: number }, ids: readonly string[]): boolean => {
        const settled = layout(counts, box, transform.zoom)
        const wanted = new Set(ids)
        for (const node of nodes) {
          if (!wanted.has(node.id)) continue
          const { x, y } = nodePosition(node, settled.geometry, offsets.current)
          const left = x * transform.zoom + transform.x
          const right = left + settled.geometry.laneWidth * transform.zoom
          const measuredHeight = Math.max(
            settled.geometry.cardHeight,
            node.id === target.selectedId ? selectedCardHeight ?? 0 : 0,
            cardHeights.get(node.id) ?? 0,
          )
          const top = y * transform.zoom + transform.y
          const bottom = top + measuredHeight * transform.zoom
          // Direct cards need both an actual lane-window position and complete x/y containment. A card that is
          // merely in the same scene but rolled out or sitting beneath the dock is not reachable evidence.
          if (!isVisible(y, settled.geometry, settled.bandHeight)) return false
          if (left < box.x - 1 || right > box.x + box.width + 1 || top < box.y - 1 || bottom > box.y + box.height + 1) return false
        }
        return true
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
      const direct = new Set<string>([target.selectedId])
      for (const edge of edges) {
        if (edge.from === target.selectedId) direct.add(edge.to)
        if (edge.to === target.selectedId) direct.add(edge.from)
      }
      if (!fits(next, [...direct])) onFramingNeedsRoom?.()

      sceneRef.current?.classList.add("is-easing")
      transform.current = next
      paint()
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
    const observer = typeof ResizeObserver === "function" ? new ResizeObserver(measure) : null
    observer?.observe(element)
    window.addEventListener("resize", measure)
    return () => {
      timers.forEach(window.clearTimeout)
      observer?.disconnect()
      window.removeEventListener("resize", measure)
    }
  }, [applyFraming, countsKey, land])

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
  }, [applyFraming, framing])

  useEffect(
    () => () => {
      if (easeTimer.current !== null) window.clearTimeout(easeTimer.current)
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
      const rollable = lane >= 0 && (result.laneMinimums[lane] ?? 0) < -1
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
          offsets.current[lane] = Math.max(
            result.laneMinimums[lane] ?? 0,
            Math.min(0, start.offset + dy / transform.current.zoom),
          )
          targets.current[lane] = offsets.current[lane]
          const anchor = anchorInLane(
            nodes,
            lane,
            result.geometry,
            offsets.current,
            result.bandHeight,
          )
          if (anchor) {
            targets.current = syncTargets(
              anchor.id,
              nodes,
              edges,
              result.geometry,
              offsets.current,
              result.laneMinimums,
              lanes.length,
              lane,
            )
          }
          settle()
          return
        }
        transform.current = { ...transform.current, x: start.tx + dx, y: start.ty + dy }
        paint()
      }
      const up = () => {
      element.classList.remove("is-panning", "is-rolling", "is-idle")
        scrubbing.current = false
        if (!start.moved) onSelect?.(card?.dataset.nodeId ?? null)
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
      const revealed = offsetToReveal(
        node.row,
        result.geometry,
        result.bandHeight,
        offsets.current[node.lane] ?? 0,
      )
      // Never past what the lane can actually roll, or the lane would scroll off its own content.
      targets.current[node.lane] = Math.max(result.laneMinimums[node.lane] ?? 0, revealed)
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
      const { x } = nodePosition(node, result.geometry, offsets.current)
      const left = x * transform.current.zoom + transform.current.x
      const right = left + result.geometry.laneWidth * transform.current.zoom
      const margin = 16
      if (left < box.x + margin) transform.current.x += box.x + margin - left
      else if (right > box.x + box.width - margin) transform.current.x -= right - (box.x + box.width - margin)
      paint()
    },
    [frame, paint, settle],
  )

  /** Arrow navigation within a lane, revealing the card it moves to. */
  const moveWithinLane = useCallback(
    (node: CanvasNode, delta: number) => {
      const bucket = byLane.get(node.lane)
      if (!bucket?.length) return
      const index = bucket.findIndex((candidate: CanvasNode) => candidate.id === node.id)
      const next = bucket[Math.min(bucket.length - 1, Math.max(0, index + delta))]
      if (!next || next.id === node.id) return
      setRoving(current => ({ ...current, [node.lane]: next.id }))
      reveal(next)
      cardRefs.current.get(next.id)?.focus({ preventScroll: true })
    },
    [byLane, reveal],
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
    if (applyFraming(target)) framedFor.current = target.key
  }

  const fitStory = () => {
    if (!framing) return
    const target = { ...framing, intent: "story" as FrameIntent, key: `${framing.key}|fit-story` }
    if (applyFraming(target)) framedFor.current = target.key
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
              aria-pressed={selectedId === node.id}
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
                onSelect?.(selectedId === node.id ? null : node.id)
              }}
              ref={element => {
                if (element) cardRefs.current.set(node.id, element)
                else cardRefs.current.delete(node.id)
              }}
              onMouseEnter={() => onHover?.(node.id)}
              onMouseLeave={() => onHover?.(null)}
            >
              {renderCard(node)}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
