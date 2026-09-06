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

/** The reason a camera moved. Landing keeps the DEC-117 readable floor; reader requested fits may pull back. */
export type FrameIntent = "landing" | "selection" | "story" | "board"

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
/** A measured border-box gap keeps an expanded card from touching the next governed record. */
export const MEASURED_CARD_GAP = 4

/** Below this the identifiers stop being legible however much more fits, so zooming out stops here. */
export const MIN_ZOOM = 0.58
export const MAX_ZOOM = 2.2
/**
 * Measured compact selection floor. A deliberate selection may use the compact tier, while arrival remains at
 * LANDING_MIN_ZOOM. The authored 14px card identifiers therefore remain 11.34px at this floor; the browser
 * regression checks this rendered size instead of treating the number as a policy by itself.
 */
export const READABLE_SELECTION_MIN_ZOOM = 0.81

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
  /** Maximum band height available in this viewport, before nominal card counts shorten the bands. */
  availableBandHeight: number
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
  const availableBandHeight = windowHeight(frame, zoom)
  const bandHeight = Math.min(availableBandHeight, tallest)
  return {
    tier,
    geometry,
    laneHeights,
    bandHeight,
    availableBandHeight,
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

/**
 * Positions after measured card expansion has been accounted for.
 *
 * A selected card keeps its detailed body while the lane remains compactly pitched. When that body is taller
 * than the active row pitch, subsequent cards need the measured extra height or their governed identifiers can
 * sit under the selected card. The adjustment is per lane and follows row order; ordinary cards keep the tier's
 * authored pitch, while a wrapped card contributes only its actual excess. Keeping this in one helper lets the
 * paint, framing and collision passes agree about the same card rectangles.
 */
export const positionsForNodes = (
  nodes: readonly CanvasNode[],
  geometry: CanvasGeometry,
  offsets: readonly number[],
  measuredHeights?: ReadonlyMap<string, number>,
): Map<string, { x: number; y: number }> => {
  const result = new Map<string, { x: number; y: number }>()
  const byLane = new Map<number, CanvasNode[]>()
  for (const node of nodes) {
    const bucket = byLane.get(node.lane)
    if (bucket) bucket.push(node)
    else byLane.set(node.lane, [node])
  }
  for (const bucket of byLane.values()) {
    bucket.sort((a, b) => a.row - b.row)
    let extra = 0
    for (const node of bucket) {
      const base = nodePosition(node, geometry, offsets)
      result.set(node.id, { x: base.x, y: base.y + extra })
      const measured = measuredHeights?.get(node.id) ?? geometry.cardHeight
      const excess = Math.max(0, measured - geometry.rowPitch)
      extra += excess + (excess > 0 ? MEASURED_CARD_GAP : 0)
    }
  }
  return result
}

/** Use spare viewport room for measured cards before requiring a lane to roll. */
export const layoutWithMeasuredCards = (
  result: LayoutResult,
  nodes: readonly CanvasNode[],
  measuredHeights?: ReadonlyMap<string, number>,
): LayoutResult => {
  if (!measuredHeights?.size) return result
  const positions = positionsForNodes(nodes, result.geometry, [], measuredHeights)
  const laneHeights = [...result.laneHeights]
  for (const node of nodes) {
    const position = positions.get(node.id)
    if (!position) continue
    const measured = measuredHeights.get(node.id) ?? result.geometry.cardHeight
    const height = Number.isFinite(measured) ? Math.max(result.geometry.cardHeight, measured) : result.geometry.cardHeight
    laneHeights[node.lane] = Math.max(laneHeights[node.lane] ?? 0, position.y + height + result.geometry.pad)
  }
  const bandHeight = Math.min(result.availableBandHeight, Math.max(result.geometry.cardHeight, ...laneHeights))
  return {
    ...result,
    laneHeights,
    bandHeight,
    laneMinimums: laneHeights.map(height => Math.min(0, bandHeight - height)),
  }
}

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
  measuredHeights?: ReadonlyMap<string, number>,
): number[] => {
  const anchor = nodes.find(node => node.id === anchorId)
  const targets = offsets.slice()
  if (!anchor) return targets
  const positions = positionsForNodes(nodes, geometry, offsets, measuredHeights)
  const anchorY = (positions.get(anchor.id)?.y ?? nodePosition(anchor, geometry, offsets).y) - geometry.pad
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
    const averageRow = linked.reduce((sum, node) => {
      const position = positions.get(node.id) ?? nodePosition(node, geometry, offsets)
      return sum + position.y - (offsets[lane] ?? 0) - geometry.pad
    }, 0) / linked.length
    targets[lane] = Math.max(
      minimums[lane] ?? 0,
      Math.min(0, anchorY - averageRow),
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
  options: {
    intent?: FrameIntent
    /** Actual expanded card height in scene units, measured from the rendered card. */
    selectedCardHeight?: number
    /** Actual rendered heights for direct story cards, keyed by governed node identity. */
    cardHeights?: ReadonlyMap<string, number>
  } = {},
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
    onlyDrawn = true,
  ): { x: number; y: number; width: number; height: number } | null => {
    const positions = positionsForNodes(nodes, result.geometry, offsets, options.cardHeights)
    let x0 = Infinity
    let y0 = Infinity
    let x1 = -Infinity
    let y1 = -Infinity
    for (const node of nodes) {
      if (!wanted.has(node.id)) continue
      const { x, y } = positions.get(node.id) ?? nodePosition(node, result.geometry, offsets)
      if (onlyDrawn && node.id !== selectedId && !isVisible(y, result.geometry, result.bandHeight)) continue
      x0 = Math.min(x0, x)
      x1 = Math.max(x1, x + result.geometry.laneWidth)
      y0 = Math.min(y0, y)
      // Expanded cards are measured by the canvas. A fixed allowance here made a detailed selected card
      // overlap the panel after content or density changed; callers pass the actual rendered height.
      const measuredHeight = options.cardHeights?.get(node.id)
      const selectedHeight = node.id === selectedId
        ? options.selectedCardHeight ?? measuredHeight ?? result.geometry.cardHeight
        : measuredHeight ?? result.geometry.cardHeight
      y1 = Math.max(y1, y + Math.max(result.geometry.cardHeight, selectedHeight))
    }
    if (x0 > x1) return onlyDrawn ? measure(result, false) : null
    return { x: x0, y: y0, width: x1 - x0, height: y1 - y0 }
  }

  const first = layoutWithMeasuredCards(layout(laneCounts, frame, 1), nodes, options.cardHeights)
  const want = measure(first)
  if (!want) return null
  const pad = 46
  const intent = options.intent ?? (programmatic ? "landing" : "board")
  const floor = intent === "landing" ? LANDING_MIN_ZOOM : intent === "selection" ? READABLE_SELECTION_MIN_ZOOM : MIN_ZOOM
  const zoom = Math.max(
    floor,
    minimumZoom(frame, laneCounts),
    Math.min(maxZoom, Math.min((frame.width - pad * 2) / (want.width + 52), (frame.height - pad * 2) / (want.height + 52))),
  )

  const settled = layoutWithMeasuredCards(layout(laneCounts, frame, zoom), nodes, options.cardHeights)
  const room = measure(settled)
  // Clamp against the same records that can actually be drawn. Including every rolled-out row here pushes the
  // camera below the viewport while trying to contain cards the lane has intentionally hidden.
  const core = room
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

export interface EdgeCurve {
  x1: number
  y1: number
  c1x: number
  c1y: number
  c2x: number
  c2y: number
  x2: number
  y2: number
}

const edgeCurve = (
  from: { x: number; y: number },
  to: { x: number; y: number },
  geometry: CanvasGeometry,
  bendOffset = 0,
): EdgeCurve => {
  const y1 = from.y + geometry.anchor
  const y2 = to.y + geometry.anchor
  if (isIntraLane(from, to)) {
    const edge = from.x + geometry.laneWidth
    const bow = edge + INTRA_LANE_BOW
    return { x1: edge, y1, c1x: bow, c1y: y1 + bendOffset, c2x: bow, c2y: y2 + bendOffset, x2: edge, y2 }
  }
  const backwards = to.x < from.x
  const x1 = backwards ? from.x : from.x + geometry.laneWidth
  const x2 = backwards ? to.x + geometry.laneWidth : to.x
  const bend = Math.max(30, Math.abs(x2 - x1) * 0.42) * (backwards ? -1 : 1)
  return { x1, y1, c1x: x1 + bend, c1y: y1 + bendOffset, c2x: x2 - bend, c2y: y2 + bendOffset, x2, y2 }
}

/** Build one bounded cubic that passes through a measured free frame slot at its midpoint. */
const edgeCurveViaWaypoint = (
  from: { x: number; y: number },
  to: { x: number; y: number },
  geometry: CanvasGeometry,
  waypoint: { x: number; y: number },
): EdgeCurve => {
  const base = edgeCurve(from, to, geometry)
  // P(.5) = (P0 + 3C1 + 3C2 + P3) / 8. Equal controls make the connector pass exactly through the selected
  // waypoint while retaining one cubic path and the same source/target attachment points.
  return {
    ...base,
    c1x: (8 * waypoint.x - base.x1 - base.x2) / 6,
    c1y: (8 * waypoint.y - base.y1 - base.y2) / 6,
    c2x: (8 * waypoint.x - base.x1 - base.x2) / 6,
    c2y: (8 * waypoint.y - base.y1 - base.y2) / 6,
  }
}

const curvePoint = (curve: EdgeCurve, t: number): { x: number; y: number } => {
  const inverse = 1 - t
  return {
    x: inverse * inverse * inverse * curve.x1 + 3 * inverse * inverse * t * curve.c1x +
      3 * inverse * t * t * curve.c2x + t * t * t * curve.x2,
    y: inverse * inverse * inverse * curve.y1 + 3 * inverse * inverse * t * curve.c1y +
      3 * inverse * t * t * curve.c2y + t * t * t * curve.y2,
  }
}

const curveTangent = (curve: EdgeCurve, t: number): { x: number; y: number } => ({
  x: 3 * (1 - t) * (1 - t) * (curve.c1x - curve.x1) + 6 * (1 - t) * t * (curve.c2x - curve.c1x) +
    3 * t * t * (curve.x2 - curve.c2x),
  y: 3 * (1 - t) * (1 - t) * (curve.c1y - curve.y1) + 6 * (1 - t) * t * (curve.c2y - curve.c1y) +
    3 * t * t * (curve.y2 - curve.c2y),
})

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
  route?: EdgeCurve,
): string => {
  const curve = route ?? edgeCurve(from, to, geometry)
  return `M${curve.x1} ${curve.y1} C${curve.c1x} ${curve.c1y},${curve.c2x} ${curve.c2y},${curve.x2} ${curve.y2}`
}

export interface CanvasRect { x: number; y: number; width: number; height: number }

export interface EdgeLabelCandidate {
  key: string
  label: string
  from: { x: number; y: number }
  to: { x: number; y: number }
  /** Actual SVG text bounds in scene units when the caller can measure them. */
  width?: number
  height?: number
}

export interface EdgeLabelPlacement {
  x: number
  y: number
  anchorX: number
  anchorY: number
  leader: boolean
  /** Which bounded search supplied this placement; useful when a dense frame is diagnosed in the browser. */
  placement: "local" | "path" | "overflow" | "reroute"
  exhausted: boolean
  /** False only when the measured frame has no collision-free on-line slot at all. */
  available: boolean
  /** A checked reroute shared by the rendered edge and its label when normal slots are occupied. */
  route?: EdgeCurve
}

/**
 * Exact open-segment/AABB intersection used for neutral leaders.
 *
 * Sampling points made a short card or an existing phrase easy to skip entirely. Liang-Barsky clipping gives
 * the complete parameter interval where the segment is inside the rectangle; endpoint contact is allowed so a
 * leader may leave a card edge without treating that attachment as a collision.
 */
export const segmentIntersectsRect = (
  from: { x: number; y: number },
  to: { x: number; y: number },
  rect: CanvasRect,
): boolean => {
  const left = rect.x
  const right = rect.x + rect.width
  const top = rect.y
  const bottom = rect.y + rect.height
  const dx = to.x - from.x
  const dy = to.y - from.y
  const epsilon = 1e-7
  let lower = 0
  let upper = 1

  const clip = (origin: number, delta: number, minimum: number, maximum: number): boolean => {
    if (Math.abs(delta) < epsilon) return origin > minimum && origin < maximum
    const first = (minimum - origin) / delta
    const second = (maximum - origin) / delta
    const near = Math.min(first, second)
    const far = Math.max(first, second)
    lower = Math.max(lower, near)
    upper = Math.min(upper, far)
    return lower < upper
  }

  if (!clip(from.x, dx, left, right) || !clip(from.y, dy, top, bottom)) return false
  // A leader touching an obstacle at either endpoint is an attachment, not a crossing through its interior.
  return upper > epsilon && lower < 1 - epsilon
}

/**
 * Place visible edge phrases beside their actual connector while avoiding rendered cards and earlier labels.
 * The caller supplies DOM-measured card obstacles; this helper deliberately knows nothing about a view's data
 * model, so Network, Inside and Artifact cannot quietly grow different collision rules.
 */
export const placeEdgeLabels = (
  labels: readonly EdgeLabelCandidate[],
  geometry: CanvasGeometry,
  obstacles: readonly CanvasRect[],
  frame: CanvasRect,
): Map<string, EdgeLabelPlacement> => {
  const placed: CanvasRect[] = []
  const result = new Map<string, EdgeLabelPlacement>()
  const intersects = (a: CanvasRect, b: CanvasRect): boolean =>
    a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y
  const leaderClear = (
    from: { x: number; y: number },
    to: { x: number; y: number },
    blocked: readonly CanvasRect[],
  ): boolean => {
    // A leader is a visual attachment to an existing edge, so a displaced phrase must not draw through another
    // controlled card or an earlier phrase. Exact clipping catches short obstacles between sample points while
    // allowing the leader to touch the connector and the label edge at its endpoints.
    return !blocked.some(obstacle => segmentIntersectsRect(from, to, obstacle))
  }
  for (const item of labels) {
    const intra = isIntraLane(item.from, item.to)
    const width = Math.max(28, item.width ?? (item.label.length * 4.2 + 6))
    const height = Math.max(12, item.height ?? 12)
    // Label coordinates and the SVG connector share this curve. If a crowded frame needs a reroute, the
    // placement carries its curve back to Canvas so the phrase never floats beside a path the reader cannot see.
    const connector = (bendOffset = 0): EdgeCurve => edgeCurve(item.from, item.to, geometry, bendOffset)
    let activeCurve = connector()
    const connectorPoint = (t: number): { x: number; y: number } => curvePoint(activeCurve, t)
    const connectorTangent = (t: number): { x: number; y: number } => curveTangent(activeCurve, t)
    const contains = (point: { x: number; y: number }, obstacle: CanvasRect): boolean =>
      point.x > obstacle.x && point.x < obstacle.x + obstacle.width &&
      point.y > obstacle.y && point.y < obstacle.y + obstacle.height
    // Expand obstacles while choosing the sample so the attachment has a small amount of breathing room from
    // a card boundary. Endpoints at a card edge remain valid once the sample has moved past this guard band.
    const pointClear = (point: { x: number; y: number }): boolean => !obstacles.some(obstacle =>
      contains(point, { x: obstacle.x - 2, y: obstacle.y - 2, width: obstacle.width + 4, height: obstacle.height + 4 }),
    )
    const rawAnchor = connectorPoint(0.5)
    let anchorT = 0.5
    if (!pointClear(rawAnchor)) {
      const candidates: number[] = []
      for (let step = 0; step <= 240; step += 1) {
        const t = step / 240
        if (pointClear(connectorPoint(t))) candidates.push(t)
      }
      anchorT = candidates.sort((a, b) => Math.abs(a - 0.5) - Math.abs(b - 0.5))[0] ?? anchorT
    }
    const anchor = connectorPoint(anchorT)
    const anchorX = anchor.x
    const anchorY = anchor.y
    const tangent = connectorTangent(anchorT)
    const tangentLength = Math.max(1, Math.hypot(tangent.x, tangent.y))
    const normalX = -tangent.y / tangentLength
    const normalY = tangent.x / tangentLength
    const moved = Math.abs(anchorT - 0.5) > 0.01
    const travel = anchorT >= 0.5 ? 1 : -1
    const alongX = (tangent.x / tangentLength) * travel
    const alongY = (tangent.y / tangentLength) * travel
    // Give a moved label room beside the obstacle it was moved around. The short neutral leader then runs in
    // free space instead of laying the text half over the card boundary.
    const baseX = anchor.x + (moved ? alongX * (width / 2 + 4) : 0)
    const baseY = anchor.y - 6 + (moved ? alongY * (height / 2 + 4) : 0)
    // Search outward from the true connector in bounded steps. The leader is only drawn after the segment has
    // passed the same obstacle test, so a farther slot remains attached to this edge rather than becoming a
    // free-floating midpoint. A dense crossing can use a different point on the same connector as a second
    // bounded search; this is still edge placement, and gives the phrase a real path out of an occupied gutter.
    const offsets = [0, -12, 12, -22, 22, -34, 34, -52, 52, -72, 72, -96, 96, -124, 124]
    type Candidate = { x: number; y: number; anchorX: number; anchorY: number; route?: EdgeCurve }
    const placedCandidate = (candidate: Candidate): Candidate | null => {
      const rect: CanvasRect = {
        x: candidate.x - width / 2,
        y: candidate.y - height + 2,
        width,
        height,
      }
      if (rect.x < frame.x + 2 || rect.x + rect.width > frame.x + frame.width - 2 ||
          rect.y < frame.y + 2 || rect.y + rect.height > frame.y + frame.height - 2) return null
      if (obstacles.some(obstacle => intersects(rect, obstacle)) || placed.some(previous => intersects(rect, previous))) return null
      const needsLeader = candidate.x !== candidate.anchorX || candidate.y !== candidate.anchorY - 6
      if (needsLeader && !leaderClear(
        { x: candidate.anchorX, y: candidate.anchorY },
        { x: candidate.x, y: candidate.y },
        [...obstacles, ...placed],
      )) return null
      placed.push(rect)
      return candidate
    }
    const candidateAt = (t: number, offset: number, route = activeCurve): Candidate => {
      const point = curvePoint(route, t)
      const direction = curveTangent(route, t)
      const directionLength = Math.max(1, Math.hypot(direction.x, direction.y))
      const candidateNormalX = -direction.y / directionLength
      const candidateNormalY = direction.x / directionLength
      return {
        x: point.x + candidateNormalX * offset,
        y: point.y - 6 + candidateNormalY * offset,
        anchorX: point.x,
        anchorY: point.y,
      }
    }
    const distanceToLine = (point: { x: number; y: number }, start: { x: number; y: number }, end: { x: number; y: number }): number => {
      const dx = end.x - start.x
      const dy = end.y - start.y
      const length = Math.hypot(dx, dy)
      return length < 1e-7
        ? Math.hypot(point.x - start.x, point.y - start.y)
        : Math.abs(dy * point.x - dx * point.y + end.x * start.y - end.y * start.x) / length
    }
    const splitCurve = (curve: EdgeCurve): [EdgeCurve, EdgeCurve] => {
      const midpoint = (a: { x: number; y: number }, b: { x: number; y: number }) => ({
        x: (a.x + b.x) / 2,
        y: (a.y + b.y) / 2,
      })
      const p0 = { x: curve.x1, y: curve.y1 }
      const p1 = { x: curve.c1x, y: curve.c1y }
      const p2 = { x: curve.c2x, y: curve.c2y }
      const p3 = { x: curve.x2, y: curve.y2 }
      const p01 = midpoint(p0, p1)
      const p12 = midpoint(p1, p2)
      const p23 = midpoint(p2, p3)
      const p012 = midpoint(p01, p12)
      const p123 = midpoint(p12, p23)
      const middle = midpoint(p012, p123)
      return [
        { x1: p0.x, y1: p0.y, c1x: p01.x, c1y: p01.y, c2x: p012.x, c2y: p012.y, x2: middle.x, y2: middle.y },
        { x1: middle.x, y1: middle.y, c1x: p123.x, c1y: p123.y, c2x: p23.x, c2y: p23.y, x2: p3.x, y2: p3.y },
      ]
    }
    const curveClear = (route: EdgeCurve, blocked: readonly CanvasRect[]): boolean => {
      const clear = (curve: EdgeCurve, depth: number): boolean => {
        const start = { x: curve.x1, y: curve.y1 }
        const end = { x: curve.x2, y: curve.y2 }
        const flat = Math.max(
          distanceToLine({ x: curve.c1x, y: curve.c1y }, start, end),
          distanceToLine({ x: curve.c2x, y: curve.c2y }, start, end),
        )
        // Exact segment/AABB checks are applied after the cubic is flat to sub-pixel tolerance. The depth cap
        // keeps a pathological route bounded while ensuring short obstacles cannot hide between coarse samples.
        if (flat <= 0.5 || depth >= 8) return !blocked.some(obstacle => segmentIntersectsRect(start, end, obstacle))
        const [left, right] = splitCurve(curve)
        return clear(left, depth + 1) && clear(right, depth + 1)
      }
      return clear(route, 0)
    }
    let chosen: Candidate | null = null
    let placement: EdgeLabelPlacement["placement"] = "local"
    for (const offset of offsets) {
      chosen = placedCandidate({
        x: baseX + (intra ? offset * 0.35 : normalX * offset),
        y: baseY + normalY * offset,
        anchorX,
        anchorY,
      })
      if (chosen) break
    }
    if (!chosen) {
      placement = "path"
      for (let step = 1; step < 40 && !chosen; step += 1) {
        const t = step / 40
        for (const offset of offsets) {
          chosen = placedCandidate(candidateAt(t, offset))
          if (chosen) break
        }
      }
    }
    if (!chosen) {
      // The ordinary slots are deliberately close to the connector. Only a genuinely dense frame reaches
      // this pass; widen the search along the same sampled path before reporting exhaustion, so the phrase can
      // still be attached with a neutral leader rather than falling back to a colliding midpoint.
      const widerOffsets = [
        ...offsets,
        -152, 152, -184, 184, -220, 220, -260, 260, -300, 300, -340, 340,
      ]
      for (let step = 0; step <= 80 && !chosen; step += 1) {
        const t = step / 80
        for (const offset of widerOffsets) {
          chosen = placedCandidate(candidateAt(t, offset))
          if (chosen) break
        }
      }
    }
    if (!chosen) {
      // A label may need to leave a crowded gutter entirely. Keep the fallback on the connector itself: sample
      // its path and a measured normal corridor, then accept only a free label box with a clear neutral leader.
      // This stays bounded by the usable frame height and avoids a board-wide annotation search.
      const sampleTs = Array.from({ length: 121 }, (_, index) => index / 120)
        .sort((a, b) => Math.abs(a - 0.5) - Math.abs(b - 0.5))
      placement = "overflow"
      const maxOffset = Math.max(124, Math.ceil(frame.height) + height)
      const overflowOffsets: number[] = [0]
      for (let offset = 8; offset <= maxOffset; offset += 8) {
        overflowOffsets.push(-offset, offset)
      }
      for (const t of sampleTs) {
        if (chosen) break
        for (const offset of overflowOffsets) {
          chosen = placedCandidate(candidateAt(t, offset))
          if (chosen) break
        }
      }
    }
    if (!chosen && !intra) {
      // Last resort: route this connector through a measured free slot in the frame. The slot is not a legend or
      // a second representation — it is the midpoint of the connector's checked cubic, so the phrase remains
      // on the connecting line. Both the cubic and its label box must clear every card and prior phrase before
      // this route is accepted. Keep the grid bounded to the frame; impossible duplicate edges are rejected by
      // the server before they reach this renderer and must not turn into an unbounded layout solver.
      placement = "reroute"
      const minX = frame.x + 2 + width / 2
      const maxX = frame.x + frame.width - 2 - width / 2
      const minY = frame.y + height
      const maxY = frame.y + frame.height - 2
      const columns = Math.min(10, Math.max(1, Math.floor((maxX - minX) / Math.max(width + 18, 32)) + 1))
      const rows = Math.min(10, Math.max(1, Math.floor((maxY - minY) / Math.max(height + 18, 30)) + 1))
      const blocked = [...obstacles, ...placed]
      for (let row = 0; row < rows && !chosen; row += 1) {
        const y = rows === 1 ? (minY + maxY) / 2 : minY + ((maxY - minY) * row) / (rows - 1)
        for (let column = 0; column < columns && !chosen; column += 1) {
          const x = columns === 1 ? (minX + maxX) / 2 : minX + ((maxX - minX) * column) / (columns - 1)
          const route = edgeCurveViaWaypoint(item.from, item.to, geometry, { x, y: y + 6 })
          if (!curveClear(route, blocked)) continue
          chosen = placedCandidate({ x, y, anchorX: x, anchorY: y + 6, route })
        }
      }
    }
    // A frame with no free label rectangle cannot satisfy both visibility and non-overlap. Keep that state
    // explicit for diagnostics; the caller can reserve more frame room and repaint instead of presenting a
    // colliding phrase as if it were clear. All normal and overflow placements above remain visible on-edge.
    if (chosen) {
      result.set(item.key, {
        x: chosen.x,
        y: chosen.y,
        anchorX: chosen.anchorX,
        anchorY: chosen.anchorY,
        leader: chosen.x !== chosen.anchorX || chosen.y !== chosen.anchorY - 6,
        placement,
        exhausted: false,
        available: true,
        route: chosen.route,
      })
    } else {
      result.set(item.key, {
        x: frame.x + frame.width / 2,
        y: frame.y + frame.height / 2,
        anchorX,
        anchorY,
        leader: false,
        placement: "overflow",
        exhausted: true,
        available: false,
      })
    }
  }
  return result
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
export const edgeIdentity = (from: string, to: string): string => `${from}>${to}`

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
          touched.add(edgeIdentity(edge.from, edge.to))
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
  /** Measured scene Y when an earlier expanded card shifted this row. */
  measuredY?: number,
): number => {
  const y = measuredY ?? (row * geometry.rowPitch + geometry.pad + currentOffset)
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
