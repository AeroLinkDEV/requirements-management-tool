/**
 * The Digital Thread canvas: pan, zoom, zoom-driven density, lane rolling, and cross-lane sync.
 *
 * This is deliberately framework-free and imperative. Pan and zoom update a transform on every pointer move
 * and every wheel tick; routing that through React state would re-render the whole board per frame and lose
 * the 60fps the interaction depends on. React owns what the cards contain (see DigitalThreadCanvas.tsx); this
 * owns where they are.
 *
 * Ported from the reviewed prototype at design/digital-thread/prototype/Main.dc.html. The constants below are
 * the outcome of five review rounds rather than arbitrary values, so they carry their reasoning: change them
 * against the prototype, not against taste.
 */

export interface CanvasNode {
  /** Stable artifact identity, not a revision id or display number. */
  id: string
  /** Index into `lanes`. The server states the kind; the caller maps kind to lane, never an identifier prefix. */
  lane: number
  /** Position within the lane, in rows. Fractional rows are allowed for deliberate offsets. */
  row: number
}

export interface CanvasEdge {
  from: string
  to: string
  label: string
  /** `suspect` and `retire` render dashed; everything else is a settled relation. */
  kind?: 'suspect' | 'retire' | ''
}

export interface CanvasGeometry {
  /** Card width, and the lane width the bands are drawn to. */
  laneWidth: number
  /** Distance between lane origins: card width plus gutter. */
  lanePitch: number
  /** Vertical distance between rows at the active density tier. */
  rowPitch: number
  /** Card height at the active density tier. */
  cardHeight: number
  /** Where an edge attaches, measured from the card's top. Fixed per tier so expanding a card cannot move it. */
  anchor: number
  /** Padding between a lane band's top and its first card. */
  pad: number
}

/** 2 detailed, 1 compact, 0 dense. Lower tiers drop card content so more records fit the same lane. */
export type DensityTier = 0 | 1 | 2

export interface CanvasFrame {
  /** The area the board may occupy, in viewport pixels. Shrinks when a detail panel is docked. */
  x: number
  y: number
  width: number
  height: number
}

const TIERS: Record<DensityTier, { rowPitch: number; cardHeight: number }> = {
  2: { rowPitch: 112, cardHeight: 82 },
  1: { rowPitch: 86, cardHeight: 62 },
  0: { rowPitch: 54, cardHeight: 38 },
}

const LANE_WIDTH = 236
const LANE_PITCH = 296
const LANE_PAD = 12

/** Below this the identifiers stop being legible however much more fits, so zooming out stops here. */
export const MIN_ZOOM = 0.58
export const MAX_ZOOM = 2.2

/** Eased follow for lanes tracking a scrub anchor. Slow enough to read as motion, fast enough not to lag. */
const SYNC_LERP = 0.18

/** Reserved for the hint strip, so the bottom row of cards is never under it. */
const FRAME_CHROME = 74

export const tierFor = (zoom: number): DensityTier => (zoom >= 0.86 ? 2 : zoom >= 0.62 ? 1 : 0)

export const geometryFor = (tier: DensityTier): CanvasGeometry => ({
  laneWidth: LANE_WIDTH,
  lanePitch: LANE_PITCH,
  rowPitch: TIERS[tier].rowPitch,
  cardHeight: TIERS[tier].cardHeight,
  anchor: Math.round(TIERS[tier].cardHeight / 2),
  pad: LANE_PAD,
})

/** Content height of one lane, in scene units, at the given tier. */
export const laneHeight = (count: number, tier: DensityTier): number => {
  const { rowPitch, cardHeight } = TIERS[tier]
  return count ? (count - 1) * rowPitch + cardHeight + LANE_PAD * 2 : cardHeight
}

/**
 * The visible height of a lane, in scene units. Derived from the frame rather than fixed, so pulling back
 * grows the window in scene terms and fills the freed space with records instead of background.
 */
export const windowHeight = (frame: CanvasFrame, zoom: number): number =>
  Math.max(180, (frame.height - FRAME_CHROME) / (zoom || 1))

export const sceneWidth = (laneCount: number): number => (laneCount - 1) * LANE_PITCH + LANE_WIDTH

/**
 * The zoom at which everything legible is already shown, so pulling back further reveals nothing. Binary
 * search because the fit predicate steps at tier boundaries: decreasing zoom shrinks the row pitch, which
 * makes it easier, so the predicate is monotone and the search is valid.
 */
export const minimumZoom = (frame: CanvasFrame, laneCounts: readonly number[]): number => {
  const width = sceneWidth(laneCounts.length)
  const fits = (zoom: number): boolean => {
    const tier = tierFor(zoom)
    const tallest = laneCounts.length
      ? Math.max(...laneCounts.map(count => laneHeight(count, tier)))
      : 0
    return width * zoom <= frame.width - 56 && tallest <= windowHeight(frame, zoom)
  }
  if (fits(MAX_ZOOM)) return MAX_ZOOM
  let low = MIN_ZOOM
  let high = MAX_ZOOM
  for (let i = 0; i < 22; i += 1) {
    const mid = (low + high) / 2
    if (fits(mid)) low = mid
    else high = mid
  }
  return Math.max(MIN_ZOOM, low)
}

export interface LayoutResult {
  tier: DensityTier
  geometry: CanvasGeometry
  /** Content height per lane. */
  laneHeights: number[]
  /** Visible height shared by every band: the window, or the tallest lane when everything already fits. */
  bandHeight: number
  /** Most negative offset each lane may take. Zero means the lane cannot roll. */
  laneMinimums: number[]
  sceneWidth: number
}

export const layout = (
  laneCounts: readonly number[],
  frame: CanvasFrame,
  zoom: number,
): LayoutResult => {
  const tier = tierFor(zoom)
  const geometry = geometryFor(tier)
  const laneHeights = laneCounts.map(count => laneHeight(count, tier))
  const tallest = laneHeights.length ? Math.max(...laneHeights) : geometry.cardHeight
  const bandHeight = Math.min(windowHeight(frame, zoom), tallest)
  return {
    tier,
    geometry,
    laneHeights,
    bandHeight,
    laneMinimums: laneHeights.map(height => Math.min(0, bandHeight - height)),
    sceneWidth: sceneWidth(laneCounts.length),
  }
}

export const clampOffsets = (offsets: readonly number[], minimums: readonly number[]): number[] =>
  minimums.map((minimum, lane) => Math.max(minimum, Math.min(0, offsets[lane] ?? 0)))

export const nodePosition = (
  node: CanvasNode,
  geometry: CanvasGeometry,
  offsets: readonly number[],
): { x: number; y: number } => ({
  x: node.lane * geometry.lanePitch,
  y: node.row * geometry.rowPitch + geometry.pad + (offsets[node.lane] ?? 0),
})

/** A card is drawn only while it is inside its lane's window; outside it fades and stops taking pointers. */
export const isVisible = (y: number, geometry: CanvasGeometry, bandHeight: number): boolean =>
  y > -geometry.cardHeight + 6 && y < bandHeight - 12

/** The lane under a scene x, or -1 when the point is in a gutter. */
export const laneAt = (sceneX: number, laneCount: number, geometry: CanvasGeometry): number => {
  const lane = Math.round(sceneX / geometry.lanePitch)
  if (lane < 0 || lane >= laneCount) return -1
  const left = lane * geometry.lanePitch - 14
  return sceneX >= left && sceneX <= left + geometry.laneWidth + 28 ? lane : -1
}

/** The record nearest the middle of a lane. While scrubbing, this is what the other lanes follow. */
export const anchorInLane = (
  nodes: readonly CanvasNode[],
  lane: number,
  geometry: CanvasGeometry,
  offsets: readonly number[],
  bandHeight: number,
): CanvasNode | null => {
  const middle = bandHeight / 2
  let best: CanvasNode | null = null
  let bestDistance = Number.POSITIVE_INFINITY
  for (const node of nodes) {
    if (node.lane !== lane) continue
    const centre = node.row * geometry.rowPitch + geometry.pad + (offsets[lane] ?? 0) + geometry.anchor
    const distance = Math.abs(centre - middle)
    if (distance < bestDistance) {
      bestDistance = distance
      best = node
    }
  }
  return best
}

/**
 * Where every other lane wants to sit so that the records linked to `anchorId` line up with it.
 *
 * A lane with nothing linked to the anchor holds its position rather than drifting proportionally: moving it
 * would assert a relationship the data does not carry, and it also makes the lanes look busier than the
 * change actually is.
 */
export const syncTargets = (
  anchorId: string,
  nodes: readonly CanvasNode[],
  edges: readonly CanvasEdge[],
  geometry: CanvasGeometry,
  offsets: readonly number[],
  minimums: readonly number[],
  laneCount: number,
  exceptLane: number,
): number[] => {
  const anchor = nodes.find(node => node.id === anchorId)
  const targets = offsets.slice()
  if (!anchor) return targets
  const anchorY = anchor.row * geometry.rowPitch + (offsets[anchor.lane] ?? 0)
  for (let lane = 0; lane < laneCount; lane += 1) {
    if (lane === exceptLane || lane === anchor.lane) {
      targets[lane] = offsets[lane] ?? 0
      continue
    }
    const linked = nodes.filter(
      node =>
        node.lane === lane &&
        edges.some(
          edge =>
            (edge.from === anchorId && edge.to === node.id) ||
            (edge.to === anchorId && edge.from === node.id),
        ),
    )
    if (!linked.length) {
      targets[lane] = offsets[lane] ?? 0
      continue
    }
    const averageRow = linked.reduce((sum, node) => sum + node.row, 0) / linked.length
    targets[lane] = Math.max(
      minimums[lane] ?? 0,
      Math.min(0, anchorY - averageRow * geometry.rowPitch),
    )
  }
  return targets
}

/** One eased step toward the sync targets. Returns the new offsets and whether anything is still moving. */
export const stepTowards = (
  offsets: readonly number[],
  targets: readonly number[],
): { offsets: number[]; moving: boolean } => {
  let moving = false
  const next = offsets.map((offset, lane) => {
    const delta = (targets[lane] ?? offset) - offset
    if (Math.abs(delta) <= 0.4) return targets[lane] ?? offset
    moving = true
    return offset + delta * SYNC_LERP
  })
  return { offsets: next, moving }
}

/** Zoom about a point, so the record under the cursor stays under it. */
export const zoomAbout = (
  transform: { x: number; y: number; zoom: number },
  pointerX: number,
  pointerY: number,
  factor: number,
  minimum: number,
): { x: number; y: number; zoom: number } => {
  const zoom = Math.max(minimum, Math.min(MAX_ZOOM, transform.zoom * factor))
  if (Math.abs(zoom - transform.zoom) < 1e-4) return transform
  const ratio = zoom / transform.zoom
  return {
    zoom,
    x: pointerX - (pointerX - transform.x) * ratio,
    y: pointerY - (pointerY - transform.y) * ratio,
  }
}

/** Wheel delta to zoom factor. Matches the prototype's feel; steeper reads as jumpy. */
export const wheelFactor = (deltaY: number): number => Math.pow(0.9988, deltaY)

/**
 * Fit reads the width. The vertical is a rolling window, so this deliberately does not try to squeeze a lane
 * of thirty records onto one screen — that is what rolling is for.
 */
export const fitTransform = (
  frame: CanvasFrame,
  laneCounts: readonly number[],
): { x: number; y: number; zoom: number } => {
  const width = sceneWidth(laneCounts.length)
  const zoom = Math.max(minimumZoom(frame, laneCounts), Math.min(1.05, (frame.width - 56) / width))
  const { bandHeight } = layout(laneCounts, frame, zoom)
  return {
    zoom,
    x: frame.x + (frame.width - width * zoom) / 2,
    y: frame.y + Math.max(34, (frame.height - bandHeight * zoom) / 2),
  }
}

/**
 * Preserve each lane's relative scroll position across a density change. Absolute offsets are meaningless
 * once the row pitch changes, so the fraction of the scrollable range is what carries over.
 */
export const rescaleOffsets = (
  offsets: readonly number[],
  previous: LayoutResult,
  next: LayoutResult,
): number[] =>
  offsets.map((offset, lane) => {
    const previousRange = (previous.laneHeights[lane] ?? 0) - previous.bandHeight
    const nextRange = (next.laneHeights[lane] ?? 0) - next.bandHeight
    const fraction = previousRange > 0 ? offset / previousRange : 0
    return Math.max(next.laneMinimums[lane] ?? 0, Math.min(0, fraction * Math.max(0, nextRange)))
  })

/** The cubic path for an edge, routed from the source's right edge to the target's left edge. */
export const edgePath = (
  from: { x: number; y: number },
  to: { x: number; y: number },
  geometry: CanvasGeometry,
): string => {
  const backwards = to.x <= from.x
  const x1 = backwards ? from.x : from.x + geometry.laneWidth
  const y1 = from.y + geometry.anchor
  const x2 = backwards ? to.x + geometry.laneWidth : to.x
  const y2 = to.y + geometry.anchor
  const bend = Math.max(30, Math.abs(x2 - x1) * 0.42) * (backwards ? -1 : 1)
  return `M${x1} ${y1} C${x1 + bend} ${y1},${x2 - bend} ${y2},${x2} ${y2}`
}

/**
 * The full directed trace from one record: every hop downstream and every hop upstream.
 *
 * Directed on purpose. An undirected walk would leak sideways into a sibling change that merely shares a
 * procedure, which is not something the record is traceable to.
 */
export const trace = (
  id: string,
  edges: readonly CanvasEdge[],
): { nodes: Set<string>; edges: Set<string>; hops: Map<string, number>; up: Set<string>; down: Set<string> } => {
  const hops = new Map<string, number>([[id, 0]])
  const touched = new Set<string>()
  const walk = (downstream: boolean): Set<string> => {
    const seen = new Set<string>([id])
    let frontier = [id]
    while (frontier.length) {
      const next: string[] = []
      for (const current of frontier) {
        for (const edge of edges) {
          const from = downstream ? edge.from : edge.to
          const to = downstream ? edge.to : edge.from
          if (from !== current) continue
          touched.add(`${edge.from}>${edge.to}`)
          if (seen.has(to)) continue
          seen.add(to)
          hops.set(to, (hops.get(current) ?? 0) + 1)
          next.push(to)
        }
      }
      frontier = next
    }
    seen.delete(id)
    return seen
  }
  const down = walk(true)
  const up = walk(false)
  return { nodes: new Set([id, ...down, ...up]), edges: touched, hops, up, down }
}
