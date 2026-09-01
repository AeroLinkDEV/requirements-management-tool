import { expect, test } from "@playwright/test"
import {
  MIN_ZOOM,
  compactLanes,
  frameNodes,
  type CanvasEdge,
  type CanvasNode,
  anchorInLane,
  geometryFor,
  isVisible,
  offsetToReveal,
  laneHeight,
  layout,
  minimumZoom,
  rescaleOffsets,
  syncTargets,
  tierFor,
  trace,
  windowHeight,
  zoomAbout,
} from "../src/digitalThreadGeometry"

// The canvas geometry is pure, so it is asserted directly rather than through a rendered board. These are the
// behaviours the design review settled; the browser journeys cover them again once there is a page to drive.

const FRAME = { x: 0, y: 0, width: 1280, height: 684 }
const LANES = [6, 8, 9, 7, 7]

test.describe("digital thread canvas geometry", () => {
  test("density tiers change the row pitch, not the type size", () => {
    expect(tierFor(1.2)).toBe(2)
    expect(tierFor(0.7)).toBe(1)
    expect(tierFor(0.4)).toBe(0)
    expect(geometryFor(2).rowPitch).toBe(112)
    expect(geometryFor(1).rowPitch).toBe(86)
    expect(geometryFor(0).rowPitch).toBe(54)
    // The anchor sits at the card's middle so an expanded card cannot drag its edges with it.
    expect(geometryFor(2).anchor).toBe(41)
  })

  test("pulling back fills the freed space with records instead of background", () => {
    const detailed = windowHeight(FRAME, 1)
    const pulledBack = windowHeight(FRAME, 0.7)
    expect(pulledBack).toBeGreaterThan(detailed)

    const visibleAt = (zoom: number) =>
      Math.floor(windowHeight(FRAME, zoom) / geometryFor(tierFor(zoom)).rowPitch)
    expect(visibleAt(0.7)).toBeGreaterThan(visibleAt(1))
  })

  test("zooming out stops once everything legible is shown", () => {
    const floor = minimumZoom(FRAME, LANES)
    expect(floor).toBeGreaterThanOrEqual(MIN_ZOOM)

    // At the floor the whole board fits: full width, and the tallest lane inside its window.
    const atFloor = layout(LANES, FRAME, floor)
    const tallest = Math.max(...LANES.map(count => laneHeight(count, atFloor.tier)))
    expect(atFloor.sceneWidth * floor).toBeLessThanOrEqual(FRAME.width)
    expect(tallest).toBeLessThanOrEqual(windowHeight(FRAME, floor) + 2)

    // And the clamp holds: repeated zoom-out cannot go below it.
    let transform = { x: 0, y: 0, zoom: 1 }
    for (let i = 0; i < 30; i += 1) {
      transform = zoomAbout(transform, 640, 342, 0.81, floor)
    }
    expect(transform.zoom).toBeCloseTo(floor, 3)
  })

  test("a lane taller than its window can roll, and the clamp keeps its content inside", () => {
    const result = layout(LANES, FRAME, 1)
    const rollable = result.laneMinimums.filter(minimum => minimum < -1)
    expect(rollable.length).toBeGreaterThan(0)
    result.laneMinimums.forEach((minimum, lane) => {
      expect(minimum).toBeLessThanOrEqual(0)
      if (result.laneHeights[lane] > result.bandHeight) expect(minimum).toBeLessThan(0)
    })
  })

  test("lanes follow the anchor's links, and a lane with nothing linked holds its position", () => {
    const nodes: CanvasNode[] = [
      { id: "sys-1", lane: 1, row: 0 },
      { id: "sys-2", lane: 1, row: 4 },
      { id: "hlr-1", lane: 2, row: 6 },
      { id: "unrelated", lane: 3, row: 5 },
    ]
    const edges: CanvasEdge[] = [{ from: "sys-2", to: "hlr-1", label: "allocates to" }]
    const result = layout([0, 2, 7, 6, 0], FRAME, 1)
    const offsets = [0, 0, 0, 0, 0]

    const targets = syncTargets(
      "sys-2",
      nodes,
      edges,
      result.geometry,
      offsets,
      result.laneMinimums,
      5,
      1,
    )
    // The linked lane moves so hlr-1 rises to meet sys-2.
    expect(targets[2]).toBeLessThan(0)
    // The lane with no link to the anchor stays exactly where it was, rather than drifting for no reason.
    expect(targets[3]).toBe(0)
    // The scrubbed lane is driven by the pointer, not by the sync.
    expect(targets[1]).toBe(0)
  })

  test("selection fetches back a linked record that had been rolled out of its lane", () => {
    // The case camera framing cannot cover. The linked record is not merely off-centre: its lane has been
    // rolled so far that the record is outside the lane window entirely. Panning or zooming the board moves
    // every lane together and can never bring it back — only rolling that lane can. #880 §6.4 requires the
    // same routine to run on selection, and a selection that only reframed would leave the reader looking at
    // a highlighted edge pointing into an empty band.
    const nodes: CanvasNode[] = [
      { id: "sys-2", lane: 1, row: 4 },
      { id: "hlr-1", lane: 2, row: 6 },
    ]
    const edges: CanvasEdge[] = [{ from: "sys-2", to: "hlr-1", label: "allocates to" }]
    const result = layout([0, 5, 40, 0, 0], FRAME, 1)

    // Roll lane 2 a long way, so hlr-1 is well outside its window.
    const rolledAway = [0, 0, -1400, 0, 0]
    const before = 6 * result.geometry.rowPitch + result.geometry.pad + rolledAway[2]
    expect(isVisible(before, result.geometry, result.bandHeight)).toBe(false)

    // Selecting sys-2 runs the sync across every lane (exceptLane -1, as selection does).
    const targets = syncTargets(
      "sys-2",
      nodes,
      edges,
      result.geometry,
      rolledAway,
      result.laneMinimums,
      5,
      -1,
    )

    const after = 6 * result.geometry.rowPitch + result.geometry.pad + targets[2]
    expect(targets[2]).not.toBe(rolledAway[2])
    expect(isVisible(after, result.geometry, result.bandHeight)).toBe(true)
  })

  test("the anchor is the record nearest the middle of the rolled lane", () => {
    const nodes: CanvasNode[] = [
      { id: "a", lane: 0, row: 0 },
      { id: "b", lane: 0, row: 3 },
      { id: "c", lane: 0, row: 9 },
    ]
    const result = layout([10], FRAME, 1)
    const middleRow = Math.round(result.bandHeight / 2 / result.geometry.rowPitch)
    const anchor = anchorInLane(nodes, 0, result.geometry, [0], result.bandHeight)
    expect(anchor).not.toBeNull()
    const expected = nodes.reduce((best, node) =>
      Math.abs(node.row - middleRow) < Math.abs(best.row - middleRow) ? node : best,
    )
    expect(anchor?.id).toBe(expected.id)
  })

  test("a density change preserves each lane's relative scroll position", () => {
    // A register lane deep enough to still overflow at compact density, so there is a range on both sides.
    const deep = [6, 30, 9, 7, 7]
    const detailed = layout(deep, FRAME, 1)
    const compact = layout(deep, FRAME, 0.7)
    const lane = 1
    expect(detailed.laneHeights[lane]).toBeGreaterThan(detailed.bandHeight)
    expect(compact.laneHeights[lane]).toBeGreaterThan(compact.bandHeight)

    const halfway = detailed.laneMinimums[lane] / 2
    const offsets = detailed.laneMinimums.map((_, index) => (index === lane ? halfway : 0))
    const rescaled = rescaleOffsets(offsets, detailed, compact)

    const before = halfway / (detailed.laneHeights[lane] - detailed.bandHeight)
    const after = rescaled[lane] / (compact.laneHeights[lane] - compact.bandHeight)
    expect(after).toBeCloseTo(before, 5)
    rescaled.forEach((offset, index) => {
      expect(offset).toBeLessThanOrEqual(0)
      expect(offset).toBeGreaterThanOrEqual(compact.laneMinimums[index] - 0.001)
    })
  })

  test("a lane that stops overflowing after a density change is returned to its top", () => {
    // Pulling back can make a lane fit entirely. There is then nothing to scroll, so any carried offset must
    // collapse rather than leaving the lane scrolled past content that is now fully visible.
    const detailed = layout(LANES, FRAME, 1)
    const compact = layout(LANES, FRAME, 0.7)
    const lane = detailed.laneMinimums.findIndex(minimum => minimum < -1)
    expect(lane).toBeGreaterThanOrEqual(0)
    expect(compact.laneHeights[lane]).toBeLessThanOrEqual(compact.bandHeight)

    const offsets = detailed.laneMinimums.map((minimum, index) => (index === lane ? minimum : 0))
    expect(rescaleOffsets(offsets, detailed, compact)[lane]).toBe(0)
  })

  test("the trace follows direction and does not leak into a sibling", () => {
    // root and sibling both reach shared, but neither is traceable to the other through it.
    const edges: CanvasEdge[] = [
      { from: "pr", to: "root", label: "resolved by" },
      { from: "root", to: "child", label: "allocates to" },
      { from: "child", to: "grandchild", label: "allocates to" },
      { from: "root", to: "shared", label: "verified by" },
      { from: "sibling", to: "shared", label: "verified by" },
    ]

    const web = trace("root", edges)
    expect(web.down).toContain("child")
    expect(web.down).toContain("grandchild")
    expect(web.down).toContain("shared")
    expect(web.up).toContain("pr")
    expect(web.nodes.has("sibling")).toBe(false)
    expect(web.hops.get("grandchild")).toBe(2)

    // From the far end the chain is reachable upstream, several hops deep.
    const upward = trace("grandchild", edges)
    expect(upward.up).toContain("root")
    expect(upward.up).toContain("pr")
    expect(upward.hops.get("pr")).toBe(3)
  })
})

test.describe("digital thread canvas framing and lanes", () => {
  test("a structurally empty lane is dropped and the remaining lanes close the gap", () => {
    const lanes = ["A", "B", "C", "D"]
    // Nothing in lane 1 or lane 3.
    const nodes = [
      { id: "a", lane: 0, row: 0 },
      { id: "c", lane: 2, row: 0 },
    ]
    const compacted = compactLanes(lanes, nodes)
    expect(compacted.lanes).toEqual(["A", "C"])
    expect(compacted.nodes.map(node => node.lane)).toEqual([0, 1])

    // A board where every lane holds something is returned untouched.
    const full = compactLanes(lanes, lanes.map((_, lane) => ({ id: String(lane), lane, row: 0 })))
    expect(full.lanes).toEqual(lanes)
  })

  test("framing keeps every real record inside the area the panel is not covering", () => {
    const lanes = [4, 4, 4]
    // A right-docked panel takes 330px, so the board may only use what is left.
    const free = { x: 0, y: 0, width: 1280 - 330, height: 684 }
    const nodes = [
      { id: "sel", lane: 0, row: 1 },
      { id: "near", lane: 1, row: 1 },
      { id: "far", lane: 2, row: 3 },
    ]
    const offsets = [0, 0, 0]

    const transform = frameNodes(["sel", "near", "far"], nodes, lanes, free, offsets, "sel")
    expect(transform).not.toBeNull()
    if (!transform) return

    const geometry = layout(lanes, free, transform.zoom).geometry
    for (const node of nodes) {
      const x = node.lane * geometry.lanePitch * transform.zoom + transform.x
      const right = x + geometry.laneWidth * transform.zoom
      const y = (node.row * geometry.rowPitch + geometry.pad) * transform.zoom + transform.y
      const bottom = y + geometry.cardHeight * transform.zoom
      // Nothing linked may sit outside the free area — that is the panel-occlusion rule, geometrically.
      expect(x).toBeGreaterThanOrEqual(free.x - 1)
      expect(right).toBeLessThanOrEqual(free.x + free.width + 1)
      expect(y).toBeGreaterThanOrEqual(free.y - 1)
      expect(bottom).toBeLessThanOrEqual(free.y + free.height + 1)
    }
  })
})

test.describe("keyboard reveal", () => {
  const geometry = geometryFor(2)
  const band = 400

  test("a row already in the window does not move its lane", () => {
    // Nothing to fetch, so the lane must hold still — rolling a settled lane under a keyboard user is its own
    // kind of disorientation.
    expect(offsetToReveal(1, geometry, band, 0)).toBe(0)
  })

  test("a row below the window rolls the lane up to reach it", () => {
    // Row 20 sits far below a 400px band, so the lane must roll (a negative offset) to bring it in.
    const offset = offsetToReveal(20, geometry, band, 0)
    expect(offset).toBeLessThan(0)

    const y = 20 * geometry.rowPitch + geometry.pad + offset
    expect(y).toBeGreaterThan(-geometry.cardHeight)
    expect(y).toBeLessThan(band)
  })

  test("a row above the window rolls the lane back down to reach it", () => {
    // The lane has already been rolled a long way; row 0 is now off the top.
    const rolled = -1200
    const offset = offsetToReveal(0, geometry, band, rolled)
    expect(offset).toBeGreaterThan(rolled)

    const y = 0 * geometry.rowPitch + geometry.pad + offset
    expect(y).toBeGreaterThan(-geometry.cardHeight)
    expect(y).toBeLessThan(band)
  })

  test("a lane is never rolled below its own first row", () => {
    // Offsets are zero-or-negative. A short lane must not be pulled into positive territory chasing row 0.
    expect(offsetToReveal(0, geometry, band, 0)).toBeLessThanOrEqual(0)
  })
})
