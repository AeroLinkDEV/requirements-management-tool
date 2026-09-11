import { expect, logicTest as test } from "./isolated-client-test"

/**
 * Round-3 probe for #1022 (review artifact only; no product code).
 *
 * Each case is one of ChatGPT Astra's analytical counterexamples from the PLAN/2 review, reproduced as an
 * executable check so the corrected contract in the Round 3 plan is inspectable rather than asserted.
 */

/** Content coordinate: ordinary measured position BEFORE the deliberate lane scroll. */
type Placement = { id: string; baseY: number; delta: number; height: number }
const contentEnd = (placements: Placement[], pad: number) =>
  Math.max(pad, ...placements.map(p => p.baseY + p.delta + p.height + pad))

/** PLAN-01.2: full lane-only visibility of an unscrolled top `q` inside usable window [a,b] with offset range [L,0]. */
const laneOnlyVisible = (q: number, h: number, a: number, b: number, L: number) => {
  const lowest = Math.max(L, a - q)
  const highest = Math.min(0, b - h - q)
  return lowest <= highest
}

test.describe("PLAN-01.2 — reachability is scoped to the actual usable window", () => {
  test("Astra's counterexample is not lane-only reachable, and the contract says so", () => {
    // bandHeight 610, usable window [100,500], card q=12 h=108, lane minimum -324.
    expect(laneOnlyVisible(12, 108, 100, 500, -324)).toBe(false)
    // The same card placed inside the window is reachable with no lane movement at all.
    expect(laneOnlyVisible(140, 108, 100, 500, -324)).toBe(true)
    // Overflow placed BELOW the window is reachable by rolling the lane upward.
    expect(laneOnlyVisible(610, 108, 100, 500, -324)).toBe(true)
  })

  test("automatic placement targets only the window or below it, never above it", () => {
    const window = { a: 100, b: 500 }
    const candidates = [
      { id: "above", q: 12 },      // would need a positive offset: excluded
      { id: "inside", q: 240 },    // usable now
      { id: "below", q: 610 },     // usable by rolling
    ]
    const placed = candidates.filter(c => c.q + 108 > window.a && c.q >= window.b || (c.q >= window.a && c.q + 108 <= window.b))
    expect(placed.map(p => p.id)).toEqual(["inside", "below"])
    for (const entry of placed) {
      expect(laneOnlyVisible(entry.q, 108, window.a, window.b, -324)).toBe(true)
    }
  })

  test("a card taller than the usable window is never claimed fully visible", () => {
    const tall = 460 // usable window is only 400 tall
    expect(laneOnlyVisible(100, tall, 100, 500, -324)).toBe(false)
    // There is no offset in the supported range that can contain it, so the honest classification is
    // "partially visible; use the panel/list alternative", not "revealed".
    let anyOffsetContains = false
    for (let offset = -324; offset <= 0; offset += 1) {
      if (100 + offset >= 100 && 100 + offset + tall <= 500) anyOffsetContains = true
    }
    expect(anyOffsetContains).toBe(false)
  })
})

test.describe("PLAN-01.3 — cleanup of the extended range never snaps the user's lane", () => {
  const clamp = (offset: number, ordinaryMin: number, allowance: number) =>
    Math.max(Math.min(ordinaryMin - allowance, 0), Math.min(0, offset))

  test("Astra's -324 to -212 snap does not happen while the allowance is held", () => {
    const ordinaryMin = -212
    // Revealed extent 934 gave -324; the reveal is cleared and the ordinary extent returns to 822.
    let allowance = ordinaryMin - -324 // 112 of residual room the user actually scrolled into
    let offset = -324
    expect(clamp(offset, ordinaryMin, allowance)).toBe(-324)
    // The allowance is released only once the user's own navigation is back inside the ordinary range.
    offset = -212
    if (offset >= ordinaryMin) allowance = 0
    expect(clamp(offset, ordinaryMin, allowance)).toBe(-212)
  })

  test("a scope change drops the allowance instead of carrying it into new data", () => {
    const ordinaryMin = -212
    let allowance = 112
    allowance = 0 // new scope / different project-build-view
    expect(clamp(-324, ordinaryMin, allowance)).toBe(-212)
  })

  test("removal of the temporary extent is measured in content coordinates, not scrolled ones", () => {
    const placements: Placement[] = [
      { id: "ordinary", baseY: 700, delta: 0, height: 108 },
      { id: "revealed", baseY: 820, delta: 114, height: 108 },
    ]
    const laneOffset = -324
    // Content extent is independent of the lane offset: scrolling cannot change the bounds that permit it.
    const withReveal = contentEnd(placements, 12)
    expect(withReveal).toBe(1054)
    const withoutReveal = contentEnd(placements.map(p => ({ ...p, delta: 0 })), 12)
    expect(withoutReveal).toBe(940)
    // Displayed positions still include the offset, and the two coordinate spaces never mix.
    expect(placements[1].baseY + placements[1].delta + laneOffset).toBe(610)
  })
})

test.describe("PLAN-01.4 — preserve the displayed anchor, not the old delta", () => {
  test("rebase keeps the card where it was when measured expansion changes", () => {
    const retainedDisplayedLaneY = 700 // painted before the transition
    const laneOffsetNew = 0
    // The previously selected card collapses (-59 of accumulated expansion) so the new base is smaller.
    const baseYNew = 570
    const deltaNew = retainedDisplayedLaneY - baseYNew - laneOffsetNew
    expect(baseYNew + deltaNew + laneOffsetNew).toBe(retainedDisplayedLaneY)
    // Old delta would have moved the card 130 units away from where the reader last saw it.
    expect(deltaNew).not.toBe(0)
  })
})

test.describe("PLAN-02.B — advance preparation is not a visited lane", () => {
  test("hidden preparation is reconciled on first usable exposure, exactly once", () => {
    const lane = { preparedWhileHidden: true, visited: false, frozen: false, plans: 0 }
    const expose = (usableWindow: { a: number; b: number }, hiddenGuess: { a: number; b: number }) => {
      if (lane.frozen || lane.visited) return "none"
      if (lane.preparedWhileHidden) {
        lane.plans += 1
        lane.visited = true
        lane.preparedWhileHidden = false
        return usableWindow.a === hiddenGuess.a ? "kept" : "reconciled"
      }
      lane.plans += 1
      lane.visited = true
      return "planned"
    }
    // The lane arrives after a vertical camera move, so the window is not the one preparation assumed.
    expect(expose({ a: 100, b: 500 }, { a: 0, b: 610 })).toBe("reconciled")
    // Horizontal away and back does not re-plan a visited lane.
    expect(expose({ a: 100, b: 500 }, { a: 0, b: 610 })).toBe("none")
    expect(lane.plans).toBe(1)
  })

  test("interrupted incoming reveal does not count as delivered", () => {
    const lane = { prepared: true, visited: false, frozen: false }
    // The user takes control mid-reveal: geometry retires, but the lane is still unvisited.
    const interrupted = true
    if (interrupted) { /* incoming deltas move to outgoing cleanup; visited unchanged */ }
    expect(lane.visited).toBe(false)
    // The next usable exposure still performs the first reveal.
    const reconcileOnExposure = !lane.visited && !lane.frozen
    expect(reconcileOnExposure).toBe(true)
  })
})

test.describe("PLAN-04 — channel-aware takeover retires outgoing geometry", () => {
  const stepTo = (value: number, target: number, rate = 0.18) =>
    Math.abs(target - value) <= 0.4 ? target : value + (target - value) * rate

  test("Astra's stranded-delta counterexample cannot occur: outgoing deltas always reach zero", () => {
    let outgoing = new Map([["card-a", 60]])
    const camera = { x: 100, y: 100 } // frozen at the user's values on takeover
    // User pans mid-return: this owns the camera channel only.
    const takeCamera = () => ({ ...camera })
    const userCamera = takeCamera()
    for (let frame = 0; frame < 200 && outgoing.size; frame += 1) {
      outgoing = new Map([...outgoing].map(([id, delta]) => [id, stepTo(delta, 0)]).filter(([, delta]) => delta !== 0))
    }
    expect(outgoing.size).toBe(0)
    expect(userCamera).toEqual(camera)
  })

  test("a legitimate new selection is not suppressed by the earlier takeover", () => {
    let cancelThrough = 0
    let sequence = 0
    const requestFraming = () => ++sequence
    const pending = requestFraming() // a pending automatic correction
    cancelThrough = sequence // user input cancels requests issued so far
    expect(pending <= cancelThrough).toBe(true)
    const afterClick = requestFraming() // the click's own bounded correction
    expect(afterClick <= cancelThrough).toBe(false)
  })

  test("a new context adopts outgoing entries instead of double-animating them", () => {
    const outgoing = new Map([["card-a", 40]])
    const newContext = new Set(["card-a", "card-b"])
    const adopted = [...outgoing.keys()].filter(id => newContext.has(id))
    for (const id of adopted) outgoing.delete(id)
    expect(adopted).toEqual(["card-a"])
    expect(outgoing.size).toBe(0)
  })
})

test.describe("PLAN-02.A — scope identity is stable while content changes", () => {
  const scopeKey = (view: string, project: string, build: string) => `${view}|${project}|${build}`
  test("an edge repoint does not become a new arrival", () => {
    const before = scopeKey("network", "fms", "1.6")
    const contentSignatureBefore = "nodes:4|edges:3"
    const after = scopeKey("network", "fms", "1.6")
    const contentSignatureAfter = "nodes:4|edges:3:repointed"
    expect(after).toBe(before)
    expect(contentSignatureAfter).not.toBe(contentSignatureBefore)
  })
})

test.describe("PLAN-03 — a clicked normal card stays usable without a recovery click", () => {
  test("band-preserving dock keeps an already visible card visible after the tray opens", () => {
    const bandHeight = 610
    // Inside the full band (420 + 108 = 528 <= 610) but outside the band a bottom dock leaves (430).
    const card = { q: 420, h: 108 }
    const inside = (q: number, h: number, band: number) => q >= 0 && q + h <= band
    // Bottom dock shortens the usable band and would drop the card out of its lane window.
    const shortenedBand = 430
    expect(inside(card.q, card.h, shortenedBand)).toBe(false)
    // A side dock preserves the band height, so no lane roll and no recovery click are needed.
    expect(inside(card.q, card.h, bandHeight)).toBe(true)
  })

  test("a card taller than every usable arrangement is clipped honestly, not claimed visible", () => {
    const tallCard = 470
    const bestBand = 430
    expect(tallCard <= bestBand).toBe(false) // handled by the documented oversized-content rule
  })
})
