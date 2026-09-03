import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react"
import {
  EDGE_LAYER_OVERHANG,
  type CanvasEdge,
  type CanvasFrame,
  type CanvasNode,
  type LayoutResult,
  anchorInLane,
  clampOffsets,
  edgeKey,
  edgePath,
  offsetToReveal,
  fitTransform,
  frameNodes,
  isIntraLane,
  isVisible,
  laneAt,
  layout,
  minimumZoom,
  nodePosition,
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
   * The records the camera should frame when the selection changes, instead of the selection and its direct
   * links.
   *
   * §6.6 frames the selection and one hop, which is right when a reader is stepping through a build: it keeps
   * the zoom close and the next hop large. It is wrong for the moment an artifact thread first opens, because
   * the view has selected the focal record on the reader's behalf and one hop is not the answer they asked
   * for — landing on a six-lane thread framed to three of its lanes puts the result and the build off-screen
   * before the reader has touched anything. A caller that knows the whole web is the answer passes it here.
   */
  frameIds?: readonly string[]
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
  onFramingNeedsRoom,
  ariaLabel = "Digital Thread canvas",
}: DigitalThreadCanvasProps) {
  const viewportRef = useRef<HTMLDivElement | null>(null)
  const sceneRef = useRef<HTMLDivElement | null>(null)
  const edgeLayerRef = useRef<SVGSVGElement | null>(null)
  const cardRefs = useRef(new Map<string, HTMLDivElement>())
  const edgeRefs = useRef<
    { path: SVGPathElement; dot: SVGCircleElement; label: SVGTextElement | null; edge: CanvasEdge }[]
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
  const framingRef = useRef<{ selectedId: string; wanted: string[]; key: string } | null>(null)
  const easeTimer = useRef<number | null>(null)

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
      key:
        `${selectedId}|${countsKey}|${placement}` +
        `|${frameInset?.left ?? 0},${frameInset?.right ?? 0},${frameInset?.bottom ?? 0},${trailingOverhang}`,
    }
  }, [
    countsKey,
    edges,
    frameIds,
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
    return {
      x: frameInset?.left ?? 0,
      y: 0,
      width: rect.width - (frameInset?.left ?? 0) - (frameInset?.right ?? 0) - trailingOverhang,
      height: rect.height - (frameInset?.bottom ?? 0),
    }
  }, [frameInset?.left, frameInset?.right, frameInset?.bottom, trailingOverhang])

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
    scene.style.width = `${result.sceneWidth}px`
    scene.style.height = `${bandHeight}px`
    scene.dataset.tier = String(result.tier)

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
    // Edge labels rest hidden and appear on a traced edge, or once the board is zoomed past 1.05 (#880 §6.7).
    // At the default fit the canvas stays calm; a reader who has selected something, or leaned in, gets the
    // relation words.
    const labelsAtRest = transform.current.zoom > 1.05
    for (const { path, dot, label, edge } of edgeRefs.current) {
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
      const traced = tracedEdges?.has(edgeKey(edge.from, edge.to)) ?? false
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
        const midX = isIntraLane(from, to)
          ? from.x + geometry.laneWidth + 30
          : (from.x + geometry.laneWidth + to.x) / 2
        const midY = (from.y + to.y) / 2 + geometry.anchor - 6
        label.setAttribute("x", String(midX))
        label.setAttribute("y", String(midY))
        label.style.opacity = inWindow && (traced || labelsAtRest) ? "" : "0"
      }
    }
  }, [counts, frame, lanes.length, nodes, selectedId, tracedEdges])

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
    transform.current = fitTransform(box, counts, false)
    paint()
  }, [counts, frame, paint])

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
    (target: { selectedId: string; wanted: string[] } | null): boolean => {
      if (!target) return false
      const box = frame()
      const result = geometryRef.current
      if (!box || !result) return false

      // Roll every lane to bring the selected record's linked records into their own windows, before framing
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

      /**
       * Frame the requested set, and fall back to the selection and one hop when it will not fit.
       *
       * Framing may no longer zoom out past the §10.1 landing floor to make a wide web fit, so on a narrow
       * viewport — or with the panel docked to a side — the whole traced web can be wider than the area the
       * panel leaves. Pinning it anyway pushes its far cards under the panel, which is exactly what §6.6
       * forbids. The wide-web framing is a landing convenience; non-occlusion is a guarantee, so when the two
       * cannot both hold the convenience gives way and the board frames the smaller set that does fit.
       */
      const fits = (transform: { x: number; zoom: number }, ids: readonly string[]): boolean => {
        const wanted = new Set(ids)
        for (const node of nodes) {
          if (!wanted.has(node.id)) continue
          const { x } = nodePosition(node, result.geometry, offsets.current)
          const left = x * transform.zoom + transform.x
          const right = left + result.geometry.laneWidth * transform.zoom
          if (left < box.x - 1 || right > box.x + box.width + 1) return false
        }
        return true
      }

      const hop = new Set<string>([target.selectedId])
      for (const edge of edges) {
        if (edge.from === target.selectedId) hop.add(edge.to)
        if (edge.to === target.selectedId) hop.add(edge.from)
      }

      let next = frameNodes(target.wanted, nodes, counts, box, offsets.current, target.selectedId)
      if (next && target.wanted.length > 1 && !fits(next, target.wanted)) {
        const narrower = frameNodes([...hop], nodes, counts, box, offsets.current, target.selectedId)
        if (narrower) next = narrower
      }
      if (!next) return false

      /**
       * The selection and every direct link must actually be drawn, wholly inside the free area.
       *
       * §6.6 is a guarantee, not a preference, and it survived the Option-A ruling untouched. Hiding a linked
       * record that will not fit satisfies "not underneath the panel" only by making it not present, which is
       * the same failure wearing a different face. When the free area this dock leaves cannot hold the
       * one-hop set at the legibility floor, the panel has to move rather than the record disappear — so the
       * canvas says so and the view re-docks. Reported rather than decided here: the canvas owns geometry,
       * the view owns where its own panel may go.
       */
      if (!fits(next, [...hop])) onFramingNeedsRoom?.()

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
    [counts, edges, frame, nodes, paint],
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
      cardRefs.current.get(next.id)?.focus()
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

  return (
    <div
      className="dtCanvas"
      ref={viewportRef}
      role="group"
      aria-label={ariaLabel}
      tabIndex={0}
      onWheel={onWheel}
      onPointerDown={onPointerDown}
      onKeyDown={onKeyDown}
      onDoubleClick={event => {
        if (!(event.target as HTMLElement).closest("[data-node-id]")) fitAll()
      }}
    >
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
                  const label = dot?.nextElementSibling as SVGTextElement | null
                  if (dot) edgeRefs.current.push({ path: element, dot, label, edge })
                }}
              />
              <circle r="3" className={`dtCanvasEdgeDot${edge.kind ? ` is-${edge.kind}` : ""}`} />
              {edge.label ? (
                <text className="dtCanvasEdgeLabel" textAnchor="middle">
                  {edge.label}
                </text>
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
              onFocus={() => {
                setRoving(current => ({ ...current, [node.lane]: node.id }))
                reveal(node)
              }}
              onKeyDown={event => {
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
