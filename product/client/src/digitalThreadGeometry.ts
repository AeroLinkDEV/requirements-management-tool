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
  // Detailed tier grew to hold a wrapped top row at the 14px landing type (#880 §10.1): identifier and a
  // long state label on separate lines above a two-line title, rather than one row that overflows.
  2: { rowPitch: 138, cardHeight: 108 },
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
/**
 * The lowest zoom the board is allowed to *land* at.
 *
 * #880 §10.1, as DEC-117 records it, requires every identifier, title and state label to be legible when the
 * page opens, before the reader has touched anything; text may fall below the readable floor only as a
 * consequence of the reader deliberately zooming out, where shedding detail is the point.
 *
 * Fitting the whole board into the viewport and landing legibly are in direct tension: a seven-lane board is
 * 2012 scene units wide, so width-fit alone lands near 0.61 at 1280px and renders card text at roughly
 * two-thirds of its authored size. Legibility wins. The board lands in the detailed tier at a zoom where the
 * authored card type clears the readable floor, and a board wider than the viewport is panned — which is an
 * ordinary canvas affordance, and cheaper for a reader than text they cannot read. Double-click still calls
 * `fit()` for a reader who wants the whole board at once.
 *
 * This is the landing floor only. `MIN_ZOOM` remains the floor for zooming the reader performs themselves.
 */
export const LANDING_MIN_ZOOM = 0.86

export const fitTransform = (
  frame: CanvasFrame,
  laneCounts: readonly number[],
  /**
   * Whether this is a landing or a Fit the reader asked for.
   *
   * A landing is held to the legibility floor. An explicit Fit — keyboard `0`, double-clicking empty canvas —
   * is the reader deliberately choosing to see the whole board, which is precisely the case §10.1 permits to
   * go below the floor, and the case §6.1 requires actually to fit.
   */
  landing = true,
): { x: number; y: number; zoom: number } => {
  const width = sceneWidth(laneCounts.length)
  const zoom = Math.max(
    landing ? LANDING_MIN_ZOOM : MIN_ZOOM,
    Math.max(minimumZoom(frame, laneCounts), Math.min(1.05, (frame.width - 56) / width)),
  )
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

/**
 * Drop lanes holding nothing and close the gap, so no empty lane is ever displayed (#880 acceptance).
 *
 * This is structural emptiness only — a lane with no records at all. A lane emptied by a filter deliberately
 * stays put: collapsing it would shift every other lane sideways and lose the reader's place mid-search.
 */
export const compactLanes = <T extends { lane: number }>(
  lanes: readonly string[],
  nodes: readonly T[],
): { lanes: string[]; nodes: T[] } => {
  const used = new Set(nodes.map(node => node.lane))
  if (used.size >= lanes.length) return { lanes: lanes.slice(), nodes: nodes.slice() }
  const remap = new Map<number, number>()
  lanes.forEach((_, lane) => {
    if (used.has(lane)) remap.set(lane, remap.size)
  })
  return {
    lanes: lanes.filter((_, lane) => used.has(lane)),
    nodes: nodes.map(node => ({ ...node, lane: remap.get(node.lane) ?? 0 })),
  }
}

/**
 * A transform that frames `ids` inside the free area.
 *
 * Measured twice on purpose. Choosing a zoom changes the lane window and can cross a density tier, both of
 * which move the very records being framed — so the box is taken again once the zoom has settled, and the pan
 * is then clamped to the real card rectangles. Skipping that second measure was the cause of two defects
 * during the design review: the camera framed records and then slid them out from under itself.
 */
export const frameNodes = (
  ids: readonly string[],
  nodes: readonly CanvasNode[],
  laneCounts: readonly number[],
  frame: CanvasFrame,
  offsets: readonly number[],
  selectedId: string | null,
  maxZoom = 1.12,
  /**
   * Whether this framing is something the product is doing to the reader, or something they asked for.
   *
   * Programmatic framing — a deep link arriving, a selection being traced — is a landing, and #880 §10.1
   * holds landings to the legibility floor. A reader who has explicitly asked to see the whole board is a
   * different case, and is allowed the wider view.
   */
  programmatic = true,
): { x: number; y: number; zoom: number } | null => {
  const wanted = new Set(ids)
  /**
   * The box around the wanted records, measured over the ones actually on the board.
   *
   * A lane taller than its window rolls, so a wanted record can sit outside that window and not be drawn at
   * all. Measuring it anyway stretched the box far above the visible band, and because the pan is then clamped
   * to that box, the whole scene was pushed down out of the frame — a thirty-record lane landed with its board
   * below the viewport. The selected card is included regardless, matching the canvas, which never hides it.
   *
   * `onlyDrawn: false` is the fallback for when nothing is currently drawn, so framing still has something to
   * aim at rather than giving up.
   */
  const measure = (
    result: LayoutResult,
    room: boolean,
    onlyDrawn = true,
  ): { x: number; y: number; width: number; height: number } | null => {
    let x0 = Infinity
    let y0 = Infinity
    let x1 = -Infinity
    let y1 = -Infinity
    for (const node of nodes) {
      if (!wanted.has(node.id)) continue
      const { x, y } = nodePosition(node, result.geometry, offsets)
      if (onlyDrawn && node.id !== selectedId && !isVisible(y, result.geometry, result.bandHeight)) continue
      x0 = Math.min(x0, x)
      x1 = Math.max(x1, x + result.geometry.laneWidth)
      y0 = Math.min(y0, y)
      y1 = Math.max(y1, y + result.geometry.cardHeight + (room && node.id === selectedId ? 132 : 0))
    }
    if (x0 > x1) return onlyDrawn ? measure(result, room, false) : null
    return { x: x0, y: y0, width: x1 - x0, height: y1 - y0 }
  }

  const first = layout(laneCounts, frame, 1)
  const want = measure(first, true)
  if (!want) return null
  const pad = 46
  const zoom = Math.max(
    programmatic ? LANDING_MIN_ZOOM : MIN_ZOOM,
    minimumZoom(frame, laneCounts),
    Math.min(maxZoom, Math.min((frame.width - pad * 2) / (want.width + 52), (frame.height - pad * 2) / (want.height + 52))),
  )

  const settled = layout(laneCounts, frame, zoom)
  const room = measure(settled, true)
  const core = measure(settled, false)
  if (!room || !core) return null

  let x = frame.x + (frame.width - (room.width + 52) * zoom) / 2 - (room.x - 26) * zoom
  let y = frame.y + (frame.height - (room.height + 52) * zoom) / 2 - (room.y - 26) * zoom

  // Keep every real card inside the free area. The expanded body may overflow; a record never does.
  const margin = 12
  const clamp = (start: number, length: number, low: number, high: number, offset: number): number => {
    const from = start * zoom + offset
    const to = from + length * zoom
    if (length * zoom <= high - low - margin * 2) {
      if (from < low + margin) return offset + (low + margin - from)
      if (to > high - margin) return offset - (to - (high - margin))
      return offset
    }
    return offset + (low + margin - from)
  }
  y = clamp(core.y, core.height, frame.y, frame.y + frame.height, y)
  x = clamp(core.x, core.width, frame.x, frame.x + frame.width, x)
  return { x, y, zoom }
}

/**
 * True when both endpoints sit in the same lane.
 *
 * The artifact thread's final lane holds both a result and a build (#880 §5.3), so it carries edges whose
 * endpoints share a lane: an execution's `evidence for` link to its build, and a `retest of` link between two
 * runs. Every other view is strictly lane-to-lane, which is why this case needed naming rather than assuming.
 */
export const isIntraLane = (from: { x: number }, to: { x: number }): boolean => Math.abs(to.x - from.x) < 1

/** How far an intra-lane edge bulges into the gutter beside its lane. */
const INTRA_LANE_BOW = 46

/**
 * Room the edge layer must leave to the right of the last lane.
 *
 * The final lane can carry an intra-lane edge — the artifact thread's execution-to-build link — whose bow and
 * label sit past the board's own width. Sizing the layer to the board alone clipped both, so the overhang is
 * stated here and the canvas reads it rather than the two drifting apart.
 */
export const EDGE_LAYER_OVERHANG = INTRA_LANE_BOW + 30

/**
 * The cubic path for an edge, routed from the source's right edge to the target's left edge.
 *
 * An intra-lane edge is routed differently, as a bow out into the right-hand gutter and back. Treating it as a
 * backwards edge — which is what an equal x otherwise reads as — sent the curve out of the card's left side and
 * back into the target's right side, sweeping straight across the lane and over every card between them. The
 * gutter bow says "these two are linked" without crossing the records it is drawn beside.
 */
export const edgePath = (
  from: { x: number; y: number },
  to: { x: number; y: number },
  geometry: CanvasGeometry,
): string => {
  const y1 = from.y + geometry.anchor
  const y2 = to.y + geometry.anchor
  if (isIntraLane(from, to)) {
    const edge = from.x + geometry.laneWidth
    const bow = edge + INTRA_LANE_BOW
    return `M${edge} ${y1} C${bow} ${y1},${bow} ${y2},${edge} ${y2}`
  }
  const backwards = to.x < from.x
  const x1 = backwards ? from.x : from.x + geometry.laneWidth
  const x2 = backwards ? to.x + geometry.laneWidth : to.x
  const bend = Math.max(30, Math.abs(x2 - x1) * 0.42) * (backwards ? -1 : 1)
  return `M${x1} ${y1} C${x1 + bend} ${y1},${x2 - bend} ${y2},${x2} ${y2}`
}

/**
 * The full directed trace from one record: every hop downstream and every hop upstream.
 *
 * Directed on purpose. An undirected walk would leak sideways into a sibling change that merely shares a
 * procedure, which is not something the record is traceable to.
 */
/**
 * How an edge is identified wherever one is looked up by its endpoints — the traced set the canvas reads, and
 * the set `trace` builds. One definition, so the two can never drift into disagreeing about the same edge.
 */
export const edgeKey = (from: string, to: string): string => `${from}>${to}`

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
          touched.add(edgeKey(edge.from, edge.to))
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

/**
 * The lane offset that brings a row into its lane's visible window, or the current offset when it already is.
 *
 * Keyboard focus needs this: a lane rolls independently, so the card arrow-navigation moves to can sit outside
 * the window. Moving focus to a card nobody can see is how a keyboard user ends up lost, and #880 §6.9 requires
 * the lane to roll and keep the focused card visible.
 *
 * Offsets are zero-or-negative — a lane rolls its content upward — so the result is clamped at 0 to stop a
 * short lane being pulled below its own first row.
 */
export const offsetToReveal = (
  row: number,
  geometry: CanvasGeometry,
  bandHeight: number,
  currentOffset: number,
): number => {
  const y = row * geometry.rowPitch + geometry.pad + currentOffset
  if (isVisible(y, geometry, bandHeight)) return currentOffset
  // Above the window: bring the row to the top of the band. Below: bring it to the bottom.
  const desired =
    y <= 0
      ? currentOffset - y + geometry.pad
      : currentOffset - (y - (bandHeight - geometry.cardHeight - 12))
  return Math.min(0, desired)
}

/**
 * Which side a detail panel should take, from the lanes the selected record's direct links occupy.
 *
 * Generic over lane numbers rather than over any view's node type, so every view can share the one rule
 * (#880 §6.6 mechanism 1) instead of each re-deriving it. The panel docks on the emptier side, which is what
 * stops it coming to rest on top of the record a highlighted edge points at.
 *
 * Ties go left, matching the change network: a record with work on both sides is more often read left to
 * right, so the left gutter is the less costly place to spend.
 */
export const resolveDockByLane = (
  selectedLane: number,
  linkedLanes: readonly number[],
): "left" | "right" => {
  let right = 0
  let left = 0
  for (const lane of linkedLanes) {
    if (lane > selectedLane) right += 1
    else if (lane < selectedLane) left += 1
  }
  return right >= left ? "left" : "right"
}
