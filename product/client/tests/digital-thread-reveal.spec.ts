import { expect, logicTest as test } from "./isolated-client-test"
import {
  contentPositionsForNodes,
  geometryFor,
  laneScrollMinimum,
  planReveal,
  positionsForNodes,
  type CanvasNode,
  type RevealWindow,
} from "../src/digitalThreadGeometry"

/**
 * Lane-local reveal geometry for #1022.
 *
 * The pure rules the canvas relies on: only out-of-view linked cards are displaced, residents never move,
 * a placed card is fully inside the usable window or fully below it, and the lane's scroll bound is derived
 * from the same window requirement that the reachability assertion uses.
 */

const GEOMETRY = geometryFor(2)
const BAND = 610
const PAD = GEOMETRY.pad
const lane = (count: number, laneIndex = 0): CanvasNode[] =>
  Array.from({ length: count }, (_, row) => ({ id: `n${row}`, lane: laneIndex, row }))
const contentTop = (nodes: CanvasNode[], id: string) => contentPositionsForNodes(nodes, GEOMETRY).get(id)!
/** Full lane-only visibility of content top q inside the displayed window [top,bottom] with range [L,0]. */
const laneReachable = (q: number, h: number, window: { top: number; bottom: number }, L: number) =>
  Math.max(L, window.top - q) <= Math.min(0, window.bottom - h - q)

test.describe("lane-local reveal", () => {
  for (const promoted of [false, true]) test(`same-tier growth reconciles a retained ${promoted ? "selected" : "linked"} collision without moving residents`, () => {
    const nodes: CanvasNode[] = [
      { id: "subject", lane: 0, row: 0 },
      { id: "resident-0", lane: 1, row: 0 },
      { id: "resident-2", lane: 1, row: 2 },
      { id: "linked", lane: 1, row: 8 },
    ]
    const input = {
      nodes, geometry: GEOMETRY, laneOffsets: [0, 0],
      storyIds: new Set(["subject", "linked"]), subjectId: "subject",
      windowByLane: new Map([[1, { top: 0, bottom: 400 }]]), bandHeight: 400,
    }
    const first = planReveal({ ...input, frozenLanes: new Set<number>() })
    expect(first.deltas.get("linked")).toBe(-992)
    if (promoted) input.subjectId = "linked"
    const measuredHeights = new Map([["linked", 200]])
    const grown = planReveal({ ...input, measuredHeights, existing: first.deltas, frozenLanes: new Set([1]) })
    const positions = contentPositionsForNodes(nodes, GEOMETRY, measuredHeights, grown.deltas)
    const top = positions.get("linked")!
    for (const id of ["resident-0", "resident-2"]) {
      expect(grown.deltas.has(id)).toBe(false)
      const resident = positions.get(id)!
      expect(top + 200 <= resident || top >= resident + 108, `grown linked card overlaps ${id}`).toBe(true)
    }
    expect([...planReveal({ ...input, measuredHeights, existing: grown.deltas, frozenLanes: new Set([1]) }).deltas])
      .toEqual([...grown.deltas])
  })

  test("a valid retained placement survives being panned entirely offscreen", () => {
    const nodes: CanvasNode[] = [{ id: "subject", lane: 0, row: 0 }, { id: "linked", lane: 1, row: 8 }]
    const existing = new Map([["linked", -992]])
    const result = planReveal({
      nodes, geometry: GEOMETRY, laneOffsets: [0, 0], storyIds: new Set(["subject", "linked"]),
      subjectId: "subject", windowByLane: new Map([[1, { top: 500, bottom: 900 }]]),
      frozenLanes: new Set([1]), existing, bandHeight: 900,
    })
    expect([...result.deltas]).toEqual([...existing])
  })
  test("displaces only out-of-view linked cards and leaves residents where they are", () => {
    const nodes = lane(12)
    const window: RevealWindow = { top: contentTop(nodes, "n0"), bottom: contentTop(nodes, "n3") + GEOMETRY.cardHeight }
    const plan = planReveal({
      nodes, geometry: GEOMETRY, laneOffsets: [0], storyIds: new Set(["n0", "n1", "n7"]),
      subjectId: "n0", windowByLane: new Map([[0, window]]), frozenLanes: new Set(), bandHeight: BAND,
    })
    // The subject and the already-visible linked card keep their positions; the out-of-view one moves.
    expect(plan.deltas.has("n0")).toBe(false)
    expect(plan.deltas.has("n1")).toBe(false)
    expect(plan.deltas.has("n7")).toBe(true)
    // Residents are never displaced even when they are linked to nothing.
    for (const id of ["n2", "n3", "n4", "n5", "n6", "n8"]) expect(plan.deltas.has(id)).toBe(false)
  })

  test("a partially visible linked card is never displaced during hover", () => {
    const nodes = lane(8)
    // n3 spans 426-534; this window starts at 466, so n3 is genuinely clipped by the window edge.
    const top = contentTop(nodes, "n3") + 40
    const window: RevealWindow = { top, bottom: top + 300 }
    expect(contentTop(nodes, "n3")).toBeLessThan(top)
    expect(contentTop(nodes, "n3") + GEOMETRY.cardHeight).toBeGreaterThan(top)
    const plan = planReveal({
      nodes, geometry: GEOMETRY, laneOffsets: [0], storyIds: new Set(["n3", "n7"]),
      subjectId: "n0", windowByLane: new Map([[0, window]]), frozenLanes: new Set(), bandHeight: BAND,
    })
    // It intersects the usable window, so it is on screen and must not be displaced; n7, wholly below, moves.
    expect(plan.deltas.has("n3")).toBe(false)
    expect(plan.deltas.has("n7")).toBe(true)
  })

  test("a placed card never overlaps a resident and is reachable through the derived scroll range", () => {
    const nodes = lane(12)
    const window: RevealWindow = { top: 100, bottom: 500 }
    const plan = planReveal({
      nodes, geometry: GEOMETRY, laneOffsets: [0], storyIds: new Set(["n7", "n8"]),
      subjectId: null, windowByLane: new Map([[0, window]]), frozenLanes: new Set(), bandHeight: BAND,
    })
    const placed = [...plan.deltas].map(([id, delta]) => ({ id, top: contentTop(nodes, id) + delta }))
    expect(placed.length).toBeGreaterThan(0)
    const residents = nodes.filter(node => !plan.deltas.has(node.id))
      .map(node => ({ id: node.id, top: contentTop(nodes, node.id), bottom: contentTop(nodes, node.id) + GEOMETRY.cardHeight }))
    for (const card of placed) {
      for (const resident of residents) {
        const overlaps = card.top < resident.bottom + 3 && resident.top < card.top + GEOMETRY.cardHeight + 3
        expect(overlaps, `${card.id} overlaps resident ${resident.id}`).toBe(false)
      }
    }
    // The producer's own extent feeds the bound, and the consumer's predicate agrees with it.
    const contentEnd = Math.max(...nodes.map(node => {
      const delta = plan.deltas.get(node.id) ?? 0
      return contentTop(nodes, node.id) + delta + GEOMETRY.cardHeight + PAD
    }))
    const promised = placed.map(card => ({ q: contentTop(nodes, card.id) + (plan.deltas.get(card.id) ?? 0), h: GEOMETRY.cardHeight }))
    const minimum = laneScrollMinimum({ bandHeight: BAND, contentEnd, window, promised })
    for (const card of promised) {
      expect(laneReachable(card.q, card.h, window, minimum), `${card.q} is not reachable at ${minimum}`).toBe(true)
    }
  })

  test("the counterexample: a band-only bound strands a below-window card, the derived bound does not", () => {
    const window = { top: 100, bottom: 500 }
    const card = { q: 610, h: 108 }
    const bandOnlyEnd = card.q + card.h + PAD
    const bandOnly = Math.min(0, BAND - bandOnlyEnd)
    expect(bandOnly).toBe(-120)
    // The card needs an offset at or below -218; -120 never gets there.
    expect(Math.max(bandOnly, window.top - card.q)).toBeGreaterThan(Math.min(0, window.bottom - card.h - card.q))
    const derived = laneScrollMinimum({ bandHeight: BAND, contentEnd: bandOnlyEnd, window, promised: [card] })
    expect(derived).toBe(-218)
    expect(laneReachable(card.q, card.h, window, derived)).toBe(true)
  })

  test("two promised cards derive the deeper bound the later one needs, and the bound is reachable", () => {
    const window = { top: 100, bottom: 500 }
    const promised = [{ q: 610, h: 108 }, { q: 760, h: 108 }]
    const contentEnd = Math.max(...promised.map(card => card.q + card.h)) + PAD
    const derived = laneScrollMinimum({ bandHeight: BAND, contentEnd, window, promised })
    expect(derived).toBe(-368)
    for (const card of promised) {
      expect(laneReachable(card.q, card.h, window, derived)).toBe(true)
    }
  })

  /**
   * Available-space reveal below a short lane's ordinary content.
   *
   * A short lane can sit entirely above the current viewing height while the usable window below it is empty.
   * The planner must search the window itself, not only the lane's previous content extent: otherwise it
   * reports "no room" and drops the card just below its ordinary end — still outside the window — while
   * hundreds of usable units sit unused. Detailed geometry: pitch 138, card height 108, pad 12.
   */
  test("a linked card is placed inside the usable window even when that space is beyond the lane's content", () => {
    const nodes: CanvasNode[] = [
      { id: "subject", lane: 0, row: 3 },
      { id: "link-1", lane: 1, row: 0 },
    ]
    // The camera shows only the lower part of the band: displayed-lane window [300, 610] at lane offset 0.
    const window: RevealWindow = { top: 300, bottom: 610 }
    const plan = planReveal({
      nodes,
      geometry: GEOMETRY,
      laneOffsets: [0, 0],
      storyIds: new Set(["subject", "link-1"]),
      subjectId: "subject",
      windowByLane: new Map([[0, { top: 0, bottom: BAND }], [1, window]]),
      frozenLanes: new Set(),
      bandHeight: BAND,
    })
    const placed = 12 + (plan.deltas.get("link-1") ?? 0)
    expect(plan.deltas.has("link-1"), "the linked card received no placement").toBe(true)
    // It must land wholly inside the usable window, not merely below the lane's ordinary end.
    expect(placed).toBeGreaterThanOrEqual(window.top)
    expect(placed + GEOMETRY.cardHeight).toBeLessThanOrEqual(window.bottom)
  })

  test("a frozen lane keeps its displayed arrangement for records still in the thread", () => {
    const nodes = lane(8)
    const existing = new Map([["n6", 276], ["n7", 276]])
    const plan = planReveal({
      nodes, geometry: GEOMETRY, laneOffsets: [0], storyIds: new Set(["n6", "n7"]),
      subjectId: null, windowByLane: new Map([[0, { top: 0, bottom: 400 }]]),
      frozenLanes: new Set([0]), existing, bandHeight: BAND,
    })
    // Reader-owned geometry is returned unchanged, not dropped into an implicit return-to-ordinary.
    expect(plan.deltas.get("n6")).toBe(276)
    expect(plan.deltas.get("n7")).toBe(276)
    expect(plan.cues.get(0)?.down).toBe(true)
  })

  test("a frozen lane still retires a card that has left the traced thread", () => {
    const nodes = lane(8)
    const plan = planReveal({
      nodes, geometry: GEOMETRY, laneOffsets: [0], storyIds: new Set(["n6"]),
      subjectId: null, windowByLane: new Map([[0, { top: 0, bottom: 400 }]]),
      frozenLanes: new Set([0]), existing: new Map([["n6", 276], ["n7", 276]]), bandHeight: BAND,
    })
    expect(plan.deltas.get("n6")).toBe(276)
    expect(plan.deltas.has("n7")).toBe(false)
  })

  test("a lane that is not frozen retires displacements whose ownership ended", () => {
    const nodes = lane(8)
    const plan = planReveal({
      nodes, geometry: GEOMETRY, laneOffsets: [0], storyIds: new Set(["n0"]),
      subjectId: "n0", windowByLane: new Map([[0, { top: 0, bottom: 400 }]]),
      frozenLanes: new Set(), existing: new Map([["n7", 120]]), bandHeight: BAND,
    })
    // n7 is no longer part of the story, so no target is emitted for it and the controller retires it to zero.
    expect(plan.deltas.has("n7")).toBe(false)
  })

  test("planning is idempotent and never mutates canonical rows", () => {
    const nodes = lane(10)
    const before = JSON.stringify(nodes)
    const input = {
      nodes, geometry: GEOMETRY, laneOffsets: [-120], storyIds: new Set(["n6", "n7"]),
      subjectId: null, windowByLane: new Map([[0, { top: 0, bottom: 400 }]]),
      frozenLanes: new Set<number>(), bandHeight: BAND,
    }
    const first = planReveal(input)
    const second = planReveal(input)
    expect([...second.deltas]).toEqual([...first.deltas])
    expect(JSON.stringify(nodes)).toBe(before)
  })

  test("deltas compose with the lane offset instead of being overwritten by it", () => {
    const nodes = lane(6)
    const deltas = new Map([["n5", -200]])
    const plain = positionsForNodes(nodes, GEOMETRY, [-100], undefined, deltas).get("n5")!
    const shifted = positionsForNodes(nodes, GEOMETRY, [-40], undefined, deltas).get("n5")!
    expect(shifted.y - plain.y).toBe(60)
    expect(contentPositionsForNodes(nodes, GEOMETRY, undefined, deltas).get("n5")).toBe(
      positionsForNodes(nodes, GEOMETRY, [0], undefined, deltas).get("n5")!.y,
    )
  })
})
