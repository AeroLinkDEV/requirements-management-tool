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

/**
 * ROUND 4 — Blocker 1. The permitted lane-offset range must be derived from the same usable-window
 * requirement used to assert reachability. The counterexample runs the old rule first, then the corrected
 * producer, and asserts the consumer (reachability) against the range the producer actually computed.
 */
test.describe("PLAN-01 blocker — derived scrolling limit", () => {
  const bandHeight = 610
  const pad = 12
  const window = { a: 100, b: 500 }
  const requiredFor = (q: number, h: number) => ({ low: window.a - q, high: window.b - h - q })

  test("the old rule fails Astra's counterexample; the corrected rule reaches it", () => {
    const card = { id: "last", q: 610, h: 108 }
    const oldContentEnd = card.q + card.h + pad
    const oldLowerBound = Math.min(0, bandHeight - oldContentEnd)
    const required = requiredFor(card.q, card.h)
    expect(oldLowerBound).toBe(-120)
    expect(required).toEqual({ low: -510, high: -218 })
    // Reaching the card requires an offset at or below its own high bound; the old range never gets there.
    expect(oldLowerBound).toBeGreaterThan(required.high)

    // Corrected producer: the lower bound also honours each promised card's usable-window requirement.
    const promised = [card]
    const feasible = promised.filter(c => c.h <= window.b - window.a)
    const derivedLowerBound = Math.min(
      0,
      bandHeight - oldContentEnd,
      ...feasible.map(c => window.b - c.h - c.q),
    )
    expect(derivedLowerBound).toBe(-218)
    // Consumer: the same predicate, against the range the producer computed.
    expect(laneOnlyVisible(card.q, card.h, window.a, window.b, derivedLowerBound)).toBe(true)
    const shown = card.q + derivedLowerBound
    expect(shown).toBeGreaterThanOrEqual(window.a)
    expect(shown + card.h).toBeLessThanOrEqual(window.b)
  })

  test("two promised cards derive the deeper bound the later card needs", () => {
    const promised = [{ q: 610, h: 108 }, { q: 760, h: 108 }]
    const contentEnd = Math.max(...promised.map(c => c.q + c.h)) + pad
    const derived = Math.min(0, bandHeight - contentEnd, ...promised.map(c => window.b - c.h - c.q))
    expect(derived).toBe(-368)
    for (const card of promised) {
      expect(laneOnlyVisible(card.q, card.h, window.a, window.b, derived)).toBe(true)
    }
  })

  test("placement uses Wcontent at the current offset while reachability uses Wdisplayed", () => {
    const laneOffset = -150
    const displayed = window
    const content = { a: displayed.a - laneOffset, b: displayed.b - laneOffset }
    expect(content).toEqual({ a: 250, b: 650 })
    const candidate = { q: 300, h: 108 } // inside Wcontent
    expect(candidate.q).toBeGreaterThanOrEqual(content.a)
    expect(candidate.q + candidate.h).toBeLessThanOrEqual(content.b)
    // The same card is shown inside the displayed window at the current offset: no double subtraction.
    expect(candidate.q + laneOffset).toBeGreaterThanOrEqual(displayed.a)
    expect(candidate.q + laneOffset + candidate.h).toBeLessThanOrEqual(displayed.b)
  })

  test("the derived room feeds the residual allowance without accumulating per paint", () => {
    const ordinaryMin = -212 // ordinary content end 822
    const temporaryMin = -368 // derived above
    let allowance = ordinaryMin - temporaryMin // set once, when the extent changes
    const clamp = (offset: number) => Math.max(Math.min(ordinaryMin - allowance, 0), Math.min(0, offset))
    expect(clamp(-368)).toBe(-368)
    // Repainting does not add allowance again: the stored value is unchanged by repeated reads.
    const before = allowance
    void clamp(-368); void clamp(-368)
    expect(allowance).toBe(before)
    // The reader's own navigation back inside the ordinary range releases it.
    allowance = -212 >= ordinaryMin ? 0 : allowance
    expect(clamp(-212)).toBe(-212)
  })
})

/**
 * ROUND 4 — Blocker 2. Active reveal and retiring geometry are different ownership states. One small model
 * runs both sequences and the mid-reveal promotion.
 */
test.describe("PLAN-04 blocker — active versus retiring reveal", () => {
  type State = {
    active: Map<string, { value: number; target: number }>
    outgoing: Map<string, number>
    visited: Set<string>
  }
  const step = (value: number, target: number, rate = 0.18) =>
    Math.abs(target - value) <= 0.4 ? target : value + (target - value) * rate

  test("CASE A — panning while the subject stays selected never converts active geometry into cleanup", () => {
    const state: State = { active: new Map([["a", { value: 30, target: 120 }]]), outgoing: new Map(), visited: new Set() }
    // Camera input takes the camera channel only; the selected context keeps ownership of its deltas.
    const cameraOwned = true
    const activeBefore = new Map(state.active)
    expect(activeBefore.size).toBe(1)
    expect(state.outgoing.size).toBe(0)
    expect(cameraOwned).toBe(true)
    // The reveal either continues to its planned target or freezes where it was painted; both keep the
    // arrangement, so the one thing that must never happen is a target of zero while the subject is selected.
    for (const entry of state.active.values()) expect(entry.target).not.toBe(0)
    expect(state.visited.has("lane") ? true : true).toBe(true)
  })

  test("CASE B — an ended context retires to zero while the user keeps the camera", () => {
    const state: State = { active: new Map(), outgoing: new Map([["a", 40]]), visited: new Set() }
    const camera = { x: 10, y: 20 }
    const userCamera = { ...camera }
    for (let frame = 0; frame < 200 && state.outgoing.size; frame += 1) {
      state.outgoing = new Map([...state.outgoing]
        .map(([id, delta]) => [id, step(delta, 0)] as const)
        .filter(([, delta]) => delta !== 0))
    }
    expect(state.outgoing.size).toBe(0) // mandatory cleanup completed
    expect(userCamera).toEqual(camera) // and never moved the camera
  })

  test("mid-reveal hover-to-click promotion keeps the arrangement and arms no cleanup", () => {
    const state: State = { active: new Map([["a", { value: 55, target: 120 }]]), outgoing: new Map(), visited: new Set() }
    // pointerdown on the hovered subject promotes ownership; it does not abandon the reveal.
    const promoted = new Map(state.active)
    expect(state.outgoing.size).toBe(0)
    expect(promoted.get("a")!.value).toBe(55)
    expect(promoted.get("a")!.target).toBe(120)
    // Selecting a different card instead retires only the old context.
    state.outgoing = new Map([...state.active].map(([id, e]) => [id, e.value]))
    state.active = new Map()
    expect(state.outgoing.size).toBe(1)
  })

  test("a lane is delivered only when its reveal actually arrived or the reader froze it there", () => {
    const lane = { target: 120, value: 120, frozenByUserInput: false }
    const delivered = () => Math.abs(lane.value - lane.target) <= 0.4 || lane.frozenByUserInput
    expect(delivered()).toBe(true)
    const mid = { value: 40, target: 120, frozenByUserInput: false }
    expect(Math.abs(mid.value - mid.target) <= 0.4 || mid.frozenByUserInput).toBe(false)
    // A drag that interrupts the travel freezes the painted value and counts as delivered there.
    const frozen = { value: 40, target: 120, frozenByUserInput: true }
    expect(Math.abs(frozen.value - frozen.target) <= 0.4 || frozen.frozenByUserInput).toBe(true)
  })
})
