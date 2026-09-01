import { useCallback, useEffect, useLayoutEffect, useRef } from "react"
import {
  type CanvasEdge,
  type CanvasFrame,
  type CanvasNode,
  type LayoutResult,
  anchorInLane,
  clampOffsets,
  edgePath,
  fitTransform,
  frameNodes,
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
  selectedId?: string | null
  onSelect?: (id: string | null) => void
  onHover?: (id: string | null) => void
  /** Area the board may use, in viewport pixels. Shrink it when a detail panel is docked. */
  frameInset?: { right?: number; left?: number; bottom?: number }
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
  selectedId = null,
  onSelect,
  onHover,
  frameInset,
  ariaLabel = "Digital Thread canvas",
}: DigitalThreadCanvasProps) {
  const viewportRef = useRef<HTMLDivElement | null>(null)
  const sceneRef = useRef<HTMLDivElement | null>(null)
  const edgeLayerRef = useRef<SVGSVGElement | null>(null)
  const cardRefs = useRef(new Map<string, HTMLDivElement>())
  const edgeRefs = useRef<{ path: SVGPathElement; dot: SVGCircleElement; edge: CanvasEdge }[]>([])

  const transform = useRef({ x: 0, y: 0, zoom: 1 })
  const offsets = useRef<number[]>([])
  const targets = useRef<number[]>([])
  const geometryRef = useRef<LayoutResult | null>(null)
  const frameSignature = useRef("")
  const animation = useRef<number | null>(null)
  const scrubbing = useRef(false)

  const counts = lanes.map((_, lane) =>
    laneCount ? laneCount(lane) : nodes.filter(node => node.lane === lane).length,
  )
  const countsKey = counts.join(",")

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
      width: rect.width - (frameInset?.left ?? 0) - (frameInset?.right ?? 0),
      height: rect.height - (frameInset?.bottom ?? 0),
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
      card.classList.toggle(
        "is-offscreen",
        !isVisible(position.y, geometry, bandHeight) && selectedId !== node.id,
      )
    }

    const svg = edgeLayerRef.current
    if (svg) {
      svg.setAttribute("width", String(result.sceneWidth + 52))
      svg.setAttribute("height", String(bandHeight + 82))
      svg.setAttribute("viewBox", `-26 -56 ${result.sceneWidth + 52} ${bandHeight + 82}`)
      svg.style.left = "-26px"
      svg.style.top = "-56px"
    }
    for (const { path, dot, edge } of edgeRefs.current) {
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
      path.style.opacity = inWindow ? "" : "0.06"
      dot.style.opacity = path.style.opacity
    }
  }, [counts, frame, lanes.length, nodes, selectedId])

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

  const fit = useCallback(() => {
    const box = frame()
    if (!box) return
    transform.current = fitTransform(box, counts)
    paint()
  }, [counts, frame, paint])

  // Lay out once the frame is real, and again whenever it changes size.
  useLayoutEffect(() => {
    const element = viewportRef.current
    if (!element) return undefined
    const measure = () => {
      const rect = element.getBoundingClientRect()
      if (rect.width < 100) return
      const signature = `${Math.round(rect.width)}x${Math.round(rect.height)}x${countsKey}`
      if (signature === frameSignature.current) return
      frameSignature.current = signature
      fit()
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
  }, [countsKey, fit])

  useEffect(() => {
    paint()
  }, [paint])

  /**
   * Reframe onto the selection and its direct links.
   *
   * This is the half of the panel rule that side-picking cannot do on its own: the board moves into the area
   * the panel is not covering, so a linked record cannot end up underneath it. It also gives the panel's
   * relation rows somewhere to go — clicking one selects that record and the board comes to it.
   */
  useEffect(() => {
    if (!selectedId) return
    const box = frame()
    const result = geometryRef.current
    if (!box || !result) return
    const linked = new Set<string>([selectedId])
    for (const edge of edges) {
      if (edge.from === selectedId) linked.add(edge.to)
      else if (edge.to === selectedId) linked.add(edge.from)
    }
    const next = frameNodes(
      [...linked],
      nodes,
      counts,
      box,
      offsets.current,
      selectedId,
    )
    if (!next) return
    sceneRef.current?.classList.add("is-easing")
    transform.current = next
    paint()
    const timer = window.setTimeout(() => sceneRef.current?.classList.remove("is-easing"), 420)
    return () => window.clearTimeout(timer)
  }, [counts, edges, frame, nodes, paint, selectedId])

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

  const onKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLDivElement>) => {
      const box = frame()
      if (!box) return
      if (event.key === "0") {
        fit()
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
    [counts, fit, frame, onSelect, paint],
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
        if (!(event.target as HTMLElement).closest("[data-node-id]")) fit()
      }}
    >
      <div className="dtCanvasScene" ref={sceneRef}>
        <div className="dtCanvasBands">
          {lanes.map((title, lane) => (
            <div className="dtCanvasBand" data-band={lane} key={`band-${title}`}>
              <i className="dtCanvasFadeTop" />
              <i className="dtCanvasFadeBottom" />
            </div>
          ))}
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
                  if (dot) edgeRefs.current.push({ path: element, dot, edge })
                }}
              />
              <circle r="3" className={`dtCanvasEdgeDot${edge.kind ? ` is-${edge.kind}` : ""}`} />
            </g>
          ))}
        </svg>
        <div className="dtCanvasNodes">
          {nodes.map(node => (
            <div
              className="dtCanvasNode"
              key={node.id}
              data-node-id={node.id}
              tabIndex={0}
              role="button"
              aria-pressed={selectedId === node.id}
              onKeyDown={event => {
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
