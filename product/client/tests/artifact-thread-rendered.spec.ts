import { expect, test, type Page } from "@playwright/test"

/**
 * Rendered behaviour of the artifact thread.
 *
 * These drive the real component in a real browser through a test-only fixture, because the pure-logic spec
 * beside them cannot prove what a reader sees: that six lanes actually land on screen, that an edge inside the
 * final lane is drawn beside its lane rather than across it, that an untraced record recedes instead of
 * vanishing, that the detail panel comes to rest off a linked record, or that a malformed response leaves an
 * empty board rather than a convincing partial one.
 *
 * The fixture is test-only — no production route is added, and page integration remains slice-6 work.
 */

const fixture = (scenario: string) => `/tests/fixtures/artifact-thread.html?case=${scenario}`

/**
 * Waits for the board to have settled after its deferred re-measures and the landing reframe.
 *
 * Waits on the canvas frame rather than the scene inside it: a refused response draws no lanes at all, so the
 * scene legitimately has no size, and requiring it to be visible would make the fail-closed case unassertable.
 */
const settled = async (page: Page) => {
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(700)
}

const open = async (page: Page, scenario: string) => {
  await page.goto(fixture(scenario))
  await settled(page)
}

const laneNames = (page: Page) => page.locator(".dtCanvasLaneHead").allInnerTexts()

const cardTransforms = (page: Page) =>
  page.locator("[data-node-id]").evaluateAll(elements =>
    elements.map(element => (element as HTMLElement).style.transform).join("|"))

/**
 * Every card's scene position, as numbers.
 *
 * Compared numerically with a tolerance rather than as transform strings: rolling one lane eases the others
 * into alignment (§6.4), and that animation can still be converging by a fraction of a pixel when the
 * assertion runs. A string comparison turns that tail into a failure that says nothing about the behaviour.
 */
const cardPositions = async (page: Page) =>
  page.locator("[data-node-id]").evaluateAll(elements =>
    elements.map(element => {
      const match = /translate\((-?[\d.]+)px,\s*(-?[\d.]+)px\)/.exec((element as HTMLElement).style.transform)
      return { x: Number(match?.[1] ?? NaN), y: Number(match?.[2] ?? NaN) }
    }))

/** True when every card sits within `tolerance` of where it was. */
const positionsMatch = (
  before: { x: number; y: number }[],
  after: { x: number; y: number }[],
  tolerance = 2,
) =>
  before.length === after.length &&
  before.every((card, index) =>
    Math.abs(card.x - after[index].x) <= tolerance && Math.abs(card.y - after[index].y) <= tolerance)

/**
 * Rolls one lane by dragging its band.
 *
 * The drag has to be aimed carefully. A band taller than the viewport reports a box that extends past it, so
 * dragging from `box.y + box.height` lands outside the page and the pointer sequence does nothing at all —
 * which is how an earlier version of these tests passed while nothing actually rolled. The grab points are
 * therefore clamped to the band's visible intersection with the canvas, and taken in the band's side gutter so
 * the press lands on the band rather than on a card.
 */
const rollLane = async (page: Page) => {
  const canvas = (await page.locator(".dtCanvas").boundingBox())!
  const bands = await page.locator(".dtCanvasBand.is-rollable").all()

  // The rollable band with the most of itself on screen. Zoomed in, several bands sit wholly outside the
  // viewport — an earlier version grabbed a fixed lane index that happened to be one of them, so the pointer
  // sequence went nowhere and the test proved nothing while still passing.
  let best: { x: number; top: number; bottom: number } | null = null
  let bestArea = 0
  for (const band of bands) {
    const box = await band.boundingBox()
    if (!box) continue
    const left = Math.max(box.x, canvas.x)
    const right = Math.min(box.x + box.width, canvas.x + canvas.width)
    const top = Math.max(box.y, canvas.y) + 12
    const bottom = Math.min(box.y + box.height, canvas.y + canvas.height) - 12
    const area = Math.max(0, right - left) * Math.max(0, bottom - top)
    // The grab must land in the band's side gutter rather than on a card, and inside the viewport.
    const x = box.x + 4
    if (area > bestArea && bottom - top > 80 && x > canvas.x && x < canvas.x + canvas.width) {
      bestArea = area
      best = { x, top, bottom }
    }
  }
  if (!best) throw new Error("no rollable band is reachable inside the canvas")

  await page.mouse.move(best.x, best.bottom)
  await page.mouse.down()
  await page.mouse.move(best.x, best.top, { steps: 14 })
  await page.mouse.up()
  await page.waitForTimeout(500)
}

test.describe("the six-lane model on screen", () => {
  test("a fully populated thread draws all six lanes, result and build sharing the last", async ({ page }) => {
    await open(page, "hlr")

    const names = (await laneNames(page)).map(text => text.split("\n")[0].trim())
    expect(names).toEqual([
      "PROBLEM REPORT",
      "CHANGE REQUEST",
      "REQUIREMENT",
      "TEST CASE",
      "PROCEDURE",
      "RESULT · BUILD",
    ])

    // Both a result and a build sit in the final lane, and both are drawn.
    await expect(page.locator('.dtaCard:has-text("Pass run")')).toBeVisible()
    await expect(page.locator('.dtaCard:has-text("FMS-1.5.0")')).toBeVisible()
  })

  /**
   * This asserted that every lane was horizontally on screen at the automatic landing. #880 §10.1 — and the
   * product ruling on this PR — make automatic landing legibility authoritative, and the two cannot both hold:
   * a six-lane thread is 1716 scene units, so showing every lane at 1280px caps the landing zoom at 0.714,
   * which renders card type at roughly 10px. The narrow supersession is that automatic landing no longer has
   * to fit the board horizontally. Everything else this was protecting still does hold, and is asserted here:
   * every lane exists, in order, and every one of them is reachable.
   */
  test("every lane is present and reachable when the thread first opens", async ({ page }) => {
    await open(page, "hlr")

    const heads = await page.locator(".dtCanvasLaneHead").all()
    expect(heads.length).toBe(6)

    // Landing is legible, which is what the horizontal fit was traded for.
    const tier = await page.locator(".dtCanvasScene").getAttribute("data-tier")
    expect(tier, "an automatic landing opens in the detailed tier").toBe("2")

    // Every lane is reachable: the board pans, and the last lane comes into view without the reader zooming.
    const viewport = (await page.locator(".dtCanvas").boundingBox())!
    const lastHead = page.locator(".dtCanvasLaneHead").last()
    const before = (await lastHead.boundingBox())!
    await page.mouse.move(viewport.x + viewport.width - 60, viewport.y + viewport.height / 2)
    await page.mouse.down()
    await page.mouse.move(viewport.x + 60, viewport.y + viewport.height / 2, { steps: 12 })
    await page.mouse.up()
    await page.waitForTimeout(300)
    const after = (await lastHead.boundingBox())!
    expect(after.x, "panning brings the far lanes in").toBeLessThan(before.x)
    expect(after.x + after.width).toBeLessThanOrEqual(viewport.x + viewport.width + 1)
  })

  test("an explicit Fit shows the whole board, which automatic landing no longer has to", async ({ page }) => {
    await open(page, "hlr")

    // §6.1: the reader asking for the whole board gets the whole board, and may go below the landing floor
    // to get it — that is the deliberate zoom-out §10.1 permits.
    await page.locator(".dtCanvas").focus()
    await page.keyboard.press("0")
    await page.waitForTimeout(400)

    const viewport = (await page.locator(".dtCanvas").boundingBox())!
    for (const head of await page.locator(".dtCanvasLaneHead").all()) {
      const box = (await head.boundingBox())!
      expect(box.x).toBeGreaterThanOrEqual(viewport.x - 1)
      expect(box.x + box.width).toBeLessThanOrEqual(viewport.x + viewport.width + 1)
    }
  })

  test("a System chain drops the Test Case lane rather than showing it empty", async ({ page }) => {
    await open(page, "system")

    const names = (await laneNames(page)).map(text => text.split("\n")[0].trim())
    expect(names).toEqual([
      "PROBLEM REPORT",
      "CHANGE REQUEST",
      "REQUIREMENT",
      "PROCEDURE",
      "RESULT · BUILD",
    ])
    expect(names).not.toContain("TEST CASE")
  })

  test("an edge inside the final lane is drawn beside it, not across it", async ({ page }) => {
    await open(page, "execution")

    const geometry = await page.evaluate(() => {
      const svg = document.querySelector(".dtCanvasEdges")!
      const cards = [...document.querySelectorAll<HTMLElement>("[data-node-id]")]
      const xOf = (element: HTMLElement) =>
        Number(/translate\((-?[\d.]+)px/.exec(element.style.transform)?.[1] ?? NaN)
      const lastLaneX = Math.max(...cards.map(xOf))
      const paths = [...svg.querySelectorAll("path")].map(path => path.getAttribute("d") ?? "")
      const labels = [...svg.querySelectorAll("text")].map(text => ({
        text: text.textContent, x: Number(text.getAttribute("x")),
      }))
      return { lastLaneX, paths, labels, viewBox: svg.getAttribute("viewBox") }
    })

    // `retest of` joins two runs that share the final lane. Its path must start and end at the lane's right
    // edge and bow outward, rather than starting at the card's left edge and sweeping over the lane.
    const retest = geometry.labels.find(label => label.text === "retest of")
    expect(retest).toBeTruthy()
    expect(retest!.x).toBeGreaterThan(geometry.lastLaneX)

    // And the reserved overhang must actually contain it: the viewBox has to reach past the label.
    const [left, , width] = (geometry.viewBox ?? "").split(" ").map(Number)
    expect(left + width).toBeGreaterThan(retest!.x)
  })
})

test.describe("exact identity", () => {
  test("the focal artifact is the one selected, expanded and named", async ({ page }) => {
    await open(page, "hlr")

    const focal = page.locator(".dtaCard.is-focal")
    await expect(focal).toHaveClass(/is-selected/)
    await expect(focal.locator(".dtaId")).toHaveText("HLR-000075.02")
    // Expanded in place: the body rows are on the card itself, not only in the panel.
    await expect(focal.locator(".dtaCardBody")).toBeVisible()
    await expect(focal.locator(".dtaKv")).toContainText(["HighLevel", "02"])
    await expect(page.locator(".dtaPanel")).toContainText("FOCAL RECORD")
  })

  test("each supported focal kind opens its own thread on its own record", async ({ page }) => {
    for (const [scenario, identity] of [
      ["hlr", "HLR-000075.02"],
      ["case", "HLRTC-000118.00"],
      ["procedure", "HLRTP-000120.00"],
      ["build", "FMS-1.5.0"],
    ] as const) {
      await open(page, scenario)
      await expect(page.locator(".dtaCard.is-focal .dtaId")).toHaveText(identity)
    }

    // An execution is the fifth kind, and is identified without a controlled number.
    await open(page, "execution")
    await expect(page.locator(".dtaCard.is-focal .dtaId")).toHaveText("Pass run")
  })

  test("an execution's card carries no invented identifier", async ({ page }) => {
    await open(page, "execution")

    const focal = page.locator(".dtaCard.is-focal")
    const identity = focal.locator(".dtaId")
    await expect(identity).toHaveText("Pass run")
    // Rendered as prose, not dressed as a link to a controlled record that does not exist.
    await expect(identity).toHaveClass(/is-unnumbered/)
    await expect(identity).not.toHaveAttribute("href", /./)
    await expect(focal).not.toContainText(/EXE-\d/)

    // Identified instead by what the run recorded.
    await expect(focal.locator(".dtaCardBody")).toContainText("test.engineer")
    await expect(focal.locator(".dtaCardBody")).toContainText("Pass")
  })
})

test.describe("evidence", () => {
  test("the identity and the whole hash survive into what the reader sees", async ({ page }) => {
    await open(page, "execution")

    const hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
    // The card abbreviates, but never rewrites: the abbreviation is a prefix of the real hash.
    const card = page.locator(".dtaCard.is-focal .dtaEvidenceRow").first()
    await expect(card).toContainText("round-robin-run.json")
    await expect(card).toContainText("application/json")
    await expect(card.locator("code")).toHaveAttribute("title", hash)

    // The panel carries the hash whole, because that is what a reviewer verifying a file works from.
    const panel = page.locator(".dtaPanelEvidence")
    await expect(panel.locator("code").first()).toHaveText(hash)
    await expect(panel).toContainText("test.engineer")
  })

  test("evidence is a fact on the result, not a lane of its own", async ({ page }) => {
    await open(page, "execution")

    const names = (await laneNames(page)).map(text => text.split("\n")[0].trim())
    expect(names).not.toContain("EVIDENCE")
    expect(names.some(name => name.includes("RESULT"))).toBe(true)
  })
})

test.describe("suspectness", () => {
  test("a server-stated suspect link looks different from a settled one, and says so", async ({ page }) => {
    await open(page, "hlr")

    // The suspect coverage link is HLR-000075.02 -> HLRTP-000120.00; the case-to-procedure link is not.
    const suspectCard = page.locator('.dtCanvasNode:has(.dtaCard:has-text("HLRTP-000120.00"))')
    const settledCard = page.locator('.dtCanvasNode:has(.dtaCard:has-text("HLRTC-000118.00"))')

    await expect(suspectCard.locator(".dtaCard")).toHaveClass(/is-suspect/)
    await expect(settledCard.locator(".dtaCard")).not.toHaveClass(/is-suspect/)

    // Never colour alone: the word travels with the card, and is actually rendered. `toHaveText` was the
    // original assertion here and it passes on a `display: none` element, which is how the word ended up
    // hidden at the landing tier with this test still green.
    await expect(suspectCard.locator(".dtaSuspectFlag b")).toBeVisible()
    await expect(suspectCard.locator(".dtaSuspectFlag b")).toHaveText("SUSPECT")
    // The short token is what fits beside a controlled identifier at every tier; the full phrase travels with
    // it for assistive technology.
    await expect(suspectCard.locator(".dtaSuspectFlag .dtaVisuallyHidden")).toHaveText(/Suspect link/)
    await expect(settledCard.locator(".dtaSuspectFlag")).toHaveCount(0)

    // And the edge itself is dashed rather than resting.
    await expect(page.locator("path.dtCanvasEdge.is-suspect")).toHaveCount(1)
  })

  test("the expanded card explains the suspect link rather than leaving the colour to carry it", async ({ page }) => {
    await open(page, "hlr")

    const note = page.locator(".dtaCard.is-focal .dtaNote")
    await expect(note).toContainText("verified by")
    await expect(note).toContainText("stated suspect by the server")
  })

  test("the Suspect chip selects endpoints of a suspect relationship", async ({ page }) => {
    await open(page, "hlr")
    await page.locator(".dtaSuspectChip").click()

    // The suspect edge is requirement -> procedure, so those two survive the filter and the case does not.
    await expect(page.locator('.dtaCard:has-text("HLRTP-000120.00")')).not.toHaveClass(/is-filtered/)
    await expect(page.locator('.dtaCard:has-text("HLRTC-000118.00")')).toHaveClass(/is-filtered/)
  })
})

test.describe("the trace the server returned", () => {
  test("the whole directed web highlights, and untraced records recede rather than disappear", async ({ page }) => {
    await open(page, "system")

    // Everything in a System chain is reachable from the focal requirement, up and down.
    await expect(page.locator(".dtaCard.is-untraced")).toHaveCount(0)
    // Hop badges are drawn on the traced records, so distance is readable as text, not only as position.
    expect(await page.locator(".dtaHop").count()).toBeGreaterThan(0)
  })

  test("the trace does not walk sideways into a record that merely shares a procedure", async ({ page }) => {
    await open(page, "hlr")

    // The case reaches the procedure, and so does the focal requirement. Reaching the case from the
    // requirement would mean going downstream then upstream — the sideways pivot §6.5 rules out.
    const sibling = page.locator('.dtaCard:has-text("HLRTC-000118.00")')
    await expect(sibling).toHaveClass(/is-untraced/)
    // Receded, not removed: it is still on the board as context.
    await expect(sibling).toBeVisible()

    // The panel lists the whole traced web, and the sibling is not in it.
    await expect(page.locator(".dtaPanel")).not.toContainText("HLRTC-000118.00")
  })

  test("the panel lists every hop, with the relation word on the direct ones", async ({ page }) => {
    await open(page, "hlr")

    const panel = page.locator(".dtaPanel")
    // Each direct row speaks in the listed record's own direction (#925 V5): the System parent
    // allocates to the focal HLR, the authoring change authored it, and the covering procedure
    // verifies it.
    await expect(panel).toContainText("ALLOCATES TO")
    await expect(panel).toContainText("AUTHORED")
    await expect(panel).toContainText("VERIFIES")
    // A suspect direct link is marked in the row as well as on the board.
    await expect(panel.locator(".dtaRel button.is-suspect")).toContainText("SUSPECT")
    // Deeper hops are counted rather than given a relation they do not have.
    await expect(panel).toContainText("2 HOPS")
  })

  test("a panel row re-centres the board on that record", async ({ page }) => {
    await open(page, "hlr")
    await page.locator('.dtaRel button:has-text("SRCR-00039.00")').first().click()
    await page.waitForTimeout(500)

    await expect(page.locator('.dtaCard:has-text("SRCR-00039.00")')).toHaveClass(/is-selected/)
  })
})

test.describe("the branching hierarchy story (#925 F5/V5)", () => {
  const evidenceDir = process.env.AEROLINK_V5_EVIDENCE

  test("System above, two LLR children with their verification below, sibling kept out", async ({ page }) => {
    await open(page, "branching")
    const panel = page.locator(".dtaPanel")

    // The focal HLR's traced story: its System parent and authoring change upstream, both LLR children
    // and its own covering procedure downstream. Direct rows speak in the listed record's direction.
    await expect(panel).toContainText("ALLOCATES TO")
    await expect(panel).toContainText("AUTHORED")
    await expect(panel).toContainText("DERIVED FROM")
    await expect(panel).toContainText("VERIFIES")
    await expect(panel).toContainText("LLR-000075.01")
    await expect(panel).toContainText("LLR-000175.01")

    // No sideways leak: the sibling HLR shares the System parent and is not in the directed web. It
    // recedes on the board rather than disappearing.
    await expect(panel).not.toContainText("HLR-000076.01")
    await expect(page.locator('.dtaCard:has-text("HLR-000076.01")')).toHaveClass(/is-untraced/)
    await expect(page.locator('.dtaCard:has-text("HLR-000076.01")')).toBeVisible()

    // The owner-mandated connector words render on the story's own edges, with the derivation phrase
    // appearing once per LLR child. SVG text exposes textContent, not innerText.
    const labels = await page.locator(".dtCanvasEdgeLabel").evaluateAll(elements =>
      elements.map(element => element.textContent))
    expect(labels).toContain("allocates to")
    expect(labels).toContain("verified by")
    expect(labels.filter(text => text === "source of")).toHaveLength(2)

    // Deeper records carry hop counts instead of a relation they do not have.
    await expect(panel).toContainText("2 HOPS")
    if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/artifact-branching-hlr.png`, fullPage: false })

    // Jumping to the System parent re-centres the story: both HLR children — the focal and its sibling —
    // are genuinely downstream of it, together with both LLRs and their verification.
    await page.locator('.dtaRel button:has-text("SYSR-000100.01")').first().click()
    await expect(page.locator('.dtaCard:has-text("SYSR-000100.01")')).toHaveClass(/is-selected/)
    await expect(panel).toContainText("HLR-000076.01")
    await expect(panel).toContainText("LLR-000075.01")
    await expect(panel).toContainText("LLR-000175.01")
    await expect(panel).toContainText("HLR-000075.02")
    if (evidenceDir) await page.screenshot({ path: `${evidenceDir}/artifact-branching-system.png`, fullPage: false })
  })
})

test.describe("states the canvas must tell apart", () => {
  test("a malformed response draws nothing at all, and says why", async ({ page }) => {
    await open(page, "invalid")

    const alert = page.locator('.dtaInFrame-error[role="alert"]')
    await expect(alert).toContainText("could not be shown")
    await expect(alert).toContainText("incomplete trace as a complete one")
    await expect(alert.locator(".dtaReason")).toContainText("Unsupported artifact thread node kind")

    // The eight well-formed records beside the bad one are NOT drawn. A partial trace shown as a whole one is
    // a false negative about traceability, which is worse than showing nothing.
    await expect(page.locator("[data-node-id]")).toHaveCount(0)
    await expect(page.locator(".dtaCard")).toHaveCount(0)
  })

  test("a transport failure is a different state from a malformed one", async ({ page }) => {
    await open(page, "error")

    await expect(page.locator('[role="alert"]')).toContainText("could not be loaded")
    await expect(page.locator(".dtaReason")).toHaveCount(0)
    // The canvas frame stays mounted, so a retry does not cost the reader their view.
    await expect(page.locator(".dtCanvas")).toBeVisible()
  })

  test("an unconnected artifact renders as a normal card, not as empty containers", async ({ page }) => {
    await open(page, "solitary")

    await expect(page.locator(".dtaCard")).toHaveCount(1)
    await expect(page.locator(".dtaCard.is-focal .dtaId")).toHaveText("SYSR-000100.01")
    await expect(page.locator(".dtaSolitary")).toContainText("No recorded relationships")
    // One lane, holding the record. Not six lanes of nothing.
    expect(await page.locator(".dtCanvasLaneHead").count()).toBe(1)
    await expect(page.locator(".dtaPanel .dtaRelEmpty").first()).toContainText("No recorded relationships")
  })

  test("a level with no verification discipline states the reason and invents nothing", async ({ page }) => {
    await open(page, "no-verification")

    await expect(page.locator(".dtaApplicability")).toContainText("no verification discipline")

    // No empty Test Case or Procedure containers stand in for the explanation.
    const names = (await laneNames(page)).map(text => text.split("\n")[0].trim())
    expect(names).not.toContain("TEST CASE")
    expect(names).not.toContain("PROCEDURE")
    // The requirement truth is kept whole.
    await expect(page.locator(".dtaCard.is-focal .dtaId")).toHaveText("CUS-000004.00")
    await expect(page.locator(".dtaCard.is-focal")).toContainText("Customer")
  })

  test("a filter that empties a lane leaves the lane in place and says so", async ({ page }) => {
    await open(page, "hlr")
    await page.locator(".dtaSearch input").fill("HLRTP-000120.00")
    await page.waitForTimeout(300)

    // Collapsing under a filter would slide every other lane sideways mid-search.
    expect(await page.locator(".dtCanvasLaneHead").count()).toBe(6)
    await expect(page.locator(".dtCanvasBandNotice").first()).toContainText("No records match")
  })
})

test.describe("shared canvas behaviour", () => {
  test("the board zooms, changes density tier and refits", async ({ page }) => {
    await open(page, "dense")
    const scene = page.locator(".dtCanvasScene")
    const zoomOf = async () =>
      Number(/scale\(([\d.]+)\)/.exec((await scene.getAttribute("style")) ?? "")?.[1] ?? NaN)

    await page.locator(".dtCanvas").focus()
    const fit = await zoomOf()

    await page.keyboard.press("=")
    await page.keyboard.press("=")
    await page.waitForTimeout(200)
    const zoomedIn = await zoomOf()
    expect(zoomedIn).toBeGreaterThan(fit)

    for (let index = 0; index < 6; index += 1) await page.keyboard.press("-")
    await page.waitForTimeout(200)
    const out = await zoomOf()
    expect(out).toBeLessThan(zoomedIn)

    // Zooming out stops where everything legible is already shown, so it clamps rather than continuing (§6.2).
    // A six-lane thread on this viewport lands on that floor already, which is why the fit is the floor here.
    for (let index = 0; index < 6; index += 1) await page.keyboard.press("-")
    await page.waitForTimeout(200)
    expect(await zoomOf()).toBe(out)
    expect(out).toBeGreaterThanOrEqual(0.58)

    // `0` refits the board, and is explicitly *not* held to the landing floor: §6.1 asks it to fit the whole
    // board and §10.1 permits that to be sub-floor, because the reader asked for it. It therefore need not
    // return to the landing zoom, and on a wide board it deliberately does not.
    await page.keyboard.press("0")
    await page.waitForTimeout(300)
    const refit = await zoomOf()
    expect(refit).toBeGreaterThan(out)
    const fitsWidth = await page.evaluate(() => {
      const scene = document.querySelector(".dtCanvasScene") as HTMLElement
      const canvas = document.querySelector(".dtCanvas") as HTMLElement
      const box = scene.getBoundingClientRect()
      const host = canvas.getBoundingClientRect()
      return box.left >= host.left - 1 && box.right <= host.right + 1
    })
    expect(fitsWidth, "an explicit Fit puts the whole board inside the canvas").toBeTruthy()
    void fit
  })

  test("a density tier drops card content instead of shrinking it", async ({ page }) => {
    await open(page, "dense")
    await page.locator(".dtCanvas").focus()
    for (let index = 0; index < 6; index += 1) await page.keyboard.press("-")
    await page.waitForTimeout(300)

    const tier = Number(await page.locator(".dtCanvasScene").getAttribute("data-tier"))
    expect(tier).toBeLessThan(2)
    // The unselected cards shed their meta line; the selected one keeps its detail at every tier.
    const hidden = await page
      .locator('.dtCanvasNode:not(.is-selected) [data-density="meta"]')
      .first()
      .isVisible()
    expect(hidden).toBe(false)
  })

  test("dragging a lane band rolls that lane", async ({ page }) => {
    await open(page, "dense")

    await expect(page.locator(".dtCanvasBand.is-rollable").first()).toBeVisible()
    const before = await cardTransforms(page)

    await rollLane(page)

    // Rolling changes the lane offset and therefore its cards positions.
    expect(await cardTransforms(page)).not.toBe(before)
  })

  test("selecting a record brings its linked records into their own lanes", async ({ page }) => {
    await open(page, "dense")

    // Ten runs and nine procedures do not all fit their lane windows at once, so a linked record can sit
    // rolled out of view no matter where the camera is. Selecting must roll the lanes to fetch them (§6.4).
    await page.locator('.dtaCard:has-text("HLRTP-000104.00")').click()
    await page.waitForTimeout(700)

    // Its covering case and the run it produced are both in view, in their own lanes.
    for (const identity of ["HLRTC-000104.00", "HLRTP-000104.00"]) {
      const card = page.locator(`.dtCanvasNode:has(.dtaCard:has-text("${identity}"))`).first()
      await expect(card).not.toHaveClass(/is-offscreen/)
    }
    // A focus-triggered lane animation must not repaint the previous selection after pointer activation.
    await expect(page.locator('.dtCanvasNode[aria-pressed="true"]')).toHaveClass(/is-selected/)
  })

  test("the detail panel never comes to rest on a directly linked record", async ({ page }) => {
    // The check `checks.js` makes in the prototype, ported: side-picking plus reframing must leave every
    // direct link of the selection outside the panel's rectangle, in each dock mode.
    await open(page, "hlr")

    for (const mode of ["Bottom", "Right", "Auto"]) {
      await page.locator(`.dtaPanelTools button:has-text("${mode}")`).click()
      await page.waitForTimeout(600)

      const panel = (await page.locator(".dtaPanel").boundingBox())!

      /**
       * Every direct link the panel names must be **drawn** and clear of the panel.
       *
       * The prototype's `checks.js` treats an absent direct link as a failure, and so does this. Skipping a
       * link that is not currently drawn would let the guarantee be satisfied by hiding the record instead of
       * fitting it — the same failure wearing a different face, and the one that slipped through once the
       * canvas began fading cards outside the free frame horizontally.
       */
      const canvas = (await page.locator(".dtCanvas").boundingBox())!
      const names = await page.locator(".dtaRel button:not(.is-far) > span > span").allInnerTexts()
      expect(names.length).toBeGreaterThan(0)
      for (const name of names) {
        const card = page.locator(`.dtCanvasNode:has(.dtaCard:has-text("${name}"))`).first()
        expect(await card.count(), `${name} is a direct link and must be on the board`).toBeGreaterThan(0)
        await expect(card, `${name} is hidden rather than fitted beside the ${mode} panel`)
          .not.toHaveClass(/is-offscreen/)

        const box = (await card.boundingBox())!
        const clear =
          box.x + box.width <= panel.x + 1 ||
          box.x >= panel.x + panel.width - 1 ||
          box.y + box.height <= panel.y + 1 ||
          box.y >= panel.y + panel.height - 1
        expect(clear, `${name} is underneath the ${mode} panel`).toBe(true)
        // And inside the board's own area, so "clear of the panel" cannot be met by being off-canvas.
        expect(box.x, `${name} starts outside the canvas`).toBeGreaterThanOrEqual(canvas.x - 1)
        expect(box.x + box.width, `${name} ends outside the canvas`)
          .toBeLessThanOrEqual(canvas.x + canvas.width + 1)
      }
    }
  })
})

test.describe("keyboard access", () => {
  /**
   * Tab across the lanes of a board that no longer fits.
   *
   * #880 §10.1 makes the automatic landing legible rather than width-fitting, so a six-lane thread lands wider
   * than the viewport and the far lanes start outside the free frame — the same frame `paint()` uses, which
   * already excludes whatever a docked panel covers. A card outside it is drawn at opacity 0. The tab stop and
   * the fade are therefore the same question, and answering them differently put a `tabIndex=0` on an
   * invisible card: §6.9's focus trap exactly. Every lane must stay reachable, and every stop must be visible
   * by the time focus rests on it.
   */
  test("tabbing across lanes never rests focus on a card the canvas has hidden", async ({ page }) => {
    await open(page, "hlr")

    const canvas = (await page.locator(".dtCanvas").boundingBox())!
    await page.locator('[data-node-id][tabindex="0"]').first().focus()

    const lanes = new Set<string>()
    for (let hop = 0; hop < 8; hop += 1) {
      const focused = page.locator("[data-node-id]:focus")
      await expect(focused).toHaveCount(1)
      // Revealed, not merely remembered: the card focus landed on is drawn and inside the canvas.
      await expect(focused).not.toHaveClass(/is-offscreen/)
      const box = (await focused.boundingBox())!
      expect(box.x, "a focused card starts inside the canvas").toBeGreaterThanOrEqual(canvas.x - 1)
      expect(box.x + box.width, "a focused card ends inside the canvas")
        .toBeLessThanOrEqual(canvas.x + canvas.width + 1)
      expect(box.y).toBeGreaterThanOrEqual(canvas.y - 1)
      expect(box.y + box.height).toBeLessThanOrEqual(canvas.y + canvas.height + 1)
      lanes.add((await focused.getAttribute("data-node-id"))!)

      await page.keyboard.press("Tab")
      await page.waitForTimeout(250)
      if ((await page.locator("[data-node-id]:focus").count()) === 0) break
    }

    // And Tab really did cross lanes rather than sitting on one card.
    expect(lanes.size, "Tab should reach more than one lane").toBeGreaterThan(1)
  })

  test("cards are reachable and activate, and each lane holds one tab stop", async ({ page }) => {
    await open(page, "hlr")

    const stops = await page.locator('[data-node-id][tabindex="0"]').count()
    // One stop per lane: Tab crosses lanes, the arrows walk within one.
    expect(stops).toBeGreaterThan(0)
    expect(stops).toBeLessThanOrEqual(6)

    const first = page.locator('[data-node-id][tabindex="0"]').first()
    await first.focus()
    await page.keyboard.press("Enter")
    await page.waitForTimeout(400)
    await expect(page.locator(".dtaCard.is-selected")).toHaveCount(1)
  })

  test("arrow keys move within a lane and the lane rolls to keep the focused card visible", async ({ page }) => {
    await open(page, "dense")

    const lane = page.locator('[data-node-id][tabindex="0"]').last()
    await lane.focus()
    for (let index = 0; index < 4; index += 1) {
      await page.keyboard.press("ArrowDown")
      await page.waitForTimeout(120)
    }

    const focused = page.locator("[data-node-id]:focus")
    await expect(focused).toHaveCount(1)
    // Focus never lands on a card rolled out of its window, which is how a keyboard reader gets lost.
    await expect(focused).not.toHaveClass(/is-offscreen/)
  })

  test("the traced web is announced, not only drawn", async ({ page }) => {
    await open(page, "hlr")

    const live = page.locator('.dtaVisuallyHidden[aria-live="polite"]')
    await expect(live).toContainText("HLR-000075.02")
    await expect(live).toContainText("upstream")
    await expect(live).toContainText("downstream")
    // A status dot is not readable as colour alone, so the suspect count is spoken too.
    await expect(live).toContainText("suspect relationship")
  })

  test("the canvas keeps its group role and names the artifact the thread is about", async ({ page }) => {
    await open(page, "hlr")

    const canvas = page.locator('[role="group"].dtCanvas')
    await expect(canvas).toHaveAttribute("aria-label", /Artifact thread for HLR-000075\.02/)
  })
})

/**
 * The five blockers from the `CHANGES_REQUIRED` review of `f34b4948`, each asserted at the level the defect
 * actually lived at. Four of them were invisible to the suite as it stood: three because nothing exercised the
 * behaviour, and one because the assertion tested the DOM rather than what a reader sees.
 */
test.describe("suspect meaning survives the density tiers", () => {
  test("the suspect word is visible at the tier a six-lane thread lands on", async ({ page }) => {
    await open(page, "hlr")

    // A six-lane thread used to land at roughly 0.642, in tier 1. Automatic landings are now held to the
    // §10.1 legibility floor, so it lands in tier 2 instead. The guarantee is unchanged and is what matters:
    // the suspect *word* survives whatever tier the reader is in, or a suspect record arrives carrying only
    // amber. Tier 1 and tier 0 are covered by the two tests that follow.
    await expect(page.locator(".dtCanvasScene")).toHaveAttribute("data-tier", "2")

    const suspect = page.locator('.dtCanvasNode:has(.dtaCard:has-text("HLRTP-000120.00"))')
    await expect(suspect.locator(".dtaCard")).not.toHaveClass(/is-selected/)
    await expect(suspect.locator(".dtaSuspectFlag b")).toBeVisible()

    // Its own state pill is truthful and is not the suspect signal: suspectness is a fact about the link.
    await expect(suspect.locator(".dtaPill")).toHaveText("Approved")
  })

  /**
   * The tier that hides the meta row on unselected cards. It used to be the landing tier, so landing covered
   * it; now that landing is held to the legibility floor it is reached by the reader zooming out, which is
   * exactly the case §10.1 permits — and the case where the suspect word matters most, because there is less
   * else on the card.
   */
  test("the suspect word survives the compact tier the reader zooms out to", async ({ page }) => {
    await open(page, "hlr")
    await page.locator(".dtCanvas").focus()
    for (let index = 0; index < 3; index += 1) await page.keyboard.press("-")
    await page.waitForTimeout(400)

    await expect(page.locator(".dtCanvasScene")).toHaveAttribute("data-tier", "1")
    await expect(
      page.locator('.dtCanvasNode:has(.dtaCard:has-text("HLRTP-000120.00")) .dtaSuspectFlag b'),
    ).toBeVisible()
  })

  test("it stays visible at the detailed tier too", async ({ page }) => {
    await open(page, "hlr")
    await page.locator(".dtCanvas").focus()
    for (let index = 0; index < 4; index += 1) await page.keyboard.press("=")
    await page.waitForTimeout(400)

    await expect(page.locator(".dtCanvasScene")).toHaveAttribute("data-tier", "2")
    await expect(
      page.locator('.dtCanvasNode:has(.dtaCard:has-text("HLRTP-000120.00")) .dtaSuspectFlag b'),
    ).toBeVisible()
  })

  test("it is still visible at the dense tier, to a sighted reader", async ({ page }) => {
    // The `crowded` board exists for this: the zoom floor stops zooming out once everything already fits, so
    // on an ordinary thread tier 0 cannot be reached at all. A lane that still overflows its window at the
    // hard floor is what makes the dense tier reachable and therefore assertable.
    await open(page, "crowded")
    await page.locator(".dtCanvas").focus()
    for (let index = 0; index < 14; index += 1) await page.keyboard.press("-")
    await page.waitForTimeout(600)

    await expect(page.locator(".dtCanvasScene")).toHaveAttribute("data-tier", "0")

    // An unselected suspect card that is actually on screen. The assertion is visibility to a sighted reader,
    // not merely the presence of accessible text — status carrying the "never colour alone" rule cannot be
    // satisfied by screen-reader text alone.
    const flag = await page.evaluate(() => {
      const canvas = document.querySelector(".dtCanvas")!.getBoundingClientRect()
      const cards = [...document.querySelectorAll(".dtCanvasNode:not(.is-offscreen)")].filter(node => {
        const box = node.getBoundingClientRect()
        return node.querySelector(".dtaCard.is-suspect:not(.is-selected)") &&
          box.right > canvas.x && box.left < canvas.right &&
          box.bottom > canvas.y && box.top < canvas.bottom
      })
      const word = cards[0]?.querySelector(".dtaSuspectFlag b")
      const box = word?.getBoundingClientRect()
      return {
        suspectCardsOnScreen: cards.length,
        text: word?.textContent ?? null,
        rendered: !!box && box.width > 0 && box.height > 0,
      }
    })

    expect(flag.suspectCardsOnScreen).toBeGreaterThan(0)
    expect(flag.text).toBe("SUSPECT")
    expect(flag.rendered).toBe(true)
  })

  test("the suspect word is never inside a density-gated container", async ({ page }) => {
    // The rule the live #880 correction states directly: status text carrying §7/§9 must not live in a
    // `data-density` container, because every one of those is hidden at some tier. Asserted structurally so a
    // future refactor cannot quietly move it back into one.
    await open(page, "hlr")

    const gated = await page.evaluate(() =>
      [...document.querySelectorAll(".dtaSuspectFlag")].some(flag => flag.closest("[data-density]") !== null))

    expect(gated).toBe(false)
  })
})

test.describe("the loading frame", () => {
  test("lane bands and headers are up before any card arrives", async ({ page }) => {
    await open(page, "loading")

    // #880 §6.8: the frame renders immediately with counts unknown and cards fade in. Never a message over a
    // discarded canvas — the board must not jump into existence when the response lands.
    expect(await page.locator(".dtCanvasLaneHead").count()).toBe(6)
    await expect(page.locator(".dtCanvasBand").first()).toBeVisible()
    await expect(page.locator(".dtaLoading")).toContainText("Loading the artifact thread")

    // Counts are unknown, so no lane claims a number, and no card is drawn yet.
    await expect(page.locator(".dtCanvasLaneHead em")).toHaveCount(0)
    await expect(page.locator("[data-node-id]")).toHaveCount(0)
  })

  test("a refused response replaces the frame rather than leaving a skeleton up", async ({ page }) => {
    // The loading frame is for content that is still coming. A contract fault means it never will, so the
    // board must not sit there implying six lanes are about to fill.
    await open(page, "invalid")
    expect(await page.locator(".dtCanvasLaneHead").count()).toBe(0)
  })
})

test.describe("a rolled lane survives a re-render", () => {
  test("hovering a card does not undo a manual roll", async ({ page }) => {
    await open(page, "dense")
    // The helper rolls a lane carrying covering records, so it is a lane the selection-sync routine has an
    // opinion about. A lane with nothing linked to the selection was never at risk, and rolling one of those
    // would have proved nothing.
    await rollLane(page)
    // Let the cross-lane easing of §6.4 finish before sampling, or the baseline is a moving target.
    await page.waitForTimeout(900)
    const rolled = await cardPositions(page)
    // The roll must actually have moved something, or the rest of this asserts nothing.
    expect(rolled.some(card => card.y < 0)).toBe(true)

    // The event is dispatched rather than driven through the pointer because the assertion is about React
    // state causing a re-render, not about pointer actionability: zoomed in, cards sit past the viewport edge,
    // so a real hover is a race. This is the same `onHover` path the canvas wires to `setHoveredId`.
    await page.evaluate(() => {
      const card = document.querySelector("[data-node-id]")!
      card.dispatchEvent(new MouseEvent("mouseover", { bubbles: true }))
      card.dispatchEvent(new MouseEvent("mouseenter", { bubbles: true }))
    })
    await page.waitForTimeout(700)

    // The roll is a deliberate act by the reader and must outlive a render that changed nothing about the
    // board (#880 §6.3).
    expect(positionsMatch(rolled, await cardPositions(page))).toBe(true)
  })

  test("changing the selection still syncs the lanes", async ({ page }) => {
    // The guard above must not have bought roll persistence by disabling the selection-driven sync of §6.4.
    await open(page, "dense")
    await rollLane(page)
    await page.waitForTimeout(900)
    expect((await cardPositions(page)).some(card => card.y < 0)).toBe(true)
    const framedBefore = await page.locator(".dtCanvasScene").getAttribute("style")

    // A case the roll has left in view, because a reader can only select what they can see — and because
    // syncing other lanes onto a record that is itself outside its window would align them to something
    // nobody can see. The covering procedure shares its number, so the pair is derivable.
    const identity = await page.evaluate(() => {
      const card = [...document.querySelectorAll(".dtCanvasNode:not(.is-offscreen)")]
        .filter(node => {
          // Comfortably inside its band, not clinging to the top edge. The sync aligns the covering procedure
          // to the selected case's own height, so anchoring on a card at the very edge would land its partner
          // exactly on the visibility threshold and assert nothing but rounding.
          const y = Number(/translate\([^,]+,\s*(-?[\d.]+)px\)/
            .exec((node as HTMLElement).style.transform)?.[1] ?? NaN)
          return y > 60
        })
        .map(node => node.querySelector(".dtaId")?.textContent ?? "")
        .find(text => text.startsWith("HLRTC-"))
      return card ?? null
    })
    expect(identity).toBeTruthy()
    const partner = identity!.replace("HLRTC-", "HLRTP-")

    // Selected from the keyboard rather than the pointer: it is deterministic while the board is still easing,
    // and it re-exercises the §6.9 path at the same time.
    await page.locator(`.dtCanvasNode:has(.dtaCard:has-text("${identity}"))`).first().focus()
    await page.keyboard.press("Enter")
    await page.waitForTimeout(900)

    await expect(page.locator(`.dtaCard.is-selected:has-text("${identity}")`)).toHaveCount(1)

    // The outcome §6.4 promises, in its own words: the records linked to the anchor sit at the anchor's
    // height. Asserting alignment rather than "something moved", because rolling a lane already eases the
    // others into place, so a lane can legitimately be where it needs to be already.
    const aligned = await page.evaluate(([anchorId, partnerId]) => {
      const yOf = (identity: string) => {
        const node = [...document.querySelectorAll<HTMLElement>("[data-node-id]")]
          .find(candidate => candidate.textContent?.includes(identity))
        if (!node) return null
        return {
          y: Number(/translate\([^,]+,\s*(-?[\d.]+)px\)/.exec(node.style.transform)?.[1] ?? NaN),
          offscreen: node.classList.contains("is-offscreen"),
        }
      }
      return { anchor: yOf(anchorId), partner: yOf(partnerId) }
    }, [identity!, partner])

    expect(aligned.anchor).not.toBeNull()
    expect(aligned.partner).not.toBeNull()
    expect(
      Math.abs(aligned.anchor!.y - aligned.partner!.y),
      `${partner} should sit at ${identity}'s height: ` +
      `anchor ${aligned.anchor!.y}, partner ${aligned.partner!.y} (offscreen: ${aligned.partner!.offscreen})`,
    ).toBeLessThanOrEqual(4)

    // And the camera reframed onto the new selection, which is the direct evidence that the persistence guard
    // did not swallow a real selection change.
    expect(await page.locator(".dtCanvasScene").getAttribute("style")).not.toBe(framedBefore)
  })

  test("re-docking the panel still reframes the board", async ({ page }) => {
    // The framing guard keys on what is being framed, and the free area is part of that. Leaving the dock out
    // of that key meant switching sides skipped the reframe, and the panel settled on a linked record — the
    // §6.6 failure the whole mechanism exists to prevent.
    //
    // Driven at 1920 deliberately. Since the §10.1 landing floor stopped the board zooming out to fit, a side
    // dock at 1280 cannot leave room for this thread's direct links, so the panel correctly refuses the side
    // and stays at the bottom — no dock change, and therefore nothing for a reframe to do. The width where
    // the side *is* honoured is where this guard can actually be observed.
    await page.setViewportSize({ width: 1920, height: 900 })
    await open(page, "hlr")
    const before = await page.locator(".dtCanvasScene").getAttribute("style")

    await page.locator(".dtaPanelTools button:text-is('Right')").click()
    await page.waitForTimeout(700)

    await expect(page.locator(".dtaPanel")).toHaveClass(/dtaPanel-right/)
    expect(await page.locator(".dtCanvasScene").getAttribute("style")).not.toBe(before)
  })

  test("a side dock that cannot hold the direct links gives way to one that can", async ({ page }) => {
    // The other half of the same rule, at a width where the side cannot be honoured. §6.6 outranks the dock
    // preference: rather than a linked record vanishing to keep the panel on the right, the panel moves.
    await open(page, "hlr")

    await page.locator(".dtaPanelTools button:text-is('Right')").click()
    await page.waitForTimeout(700)

    await expect(page.locator(".dtaPanel")).toHaveClass(/dtaPanel-bottom/)
    // And the direct links are drawn, which is the thing the dock moved to protect.
    const names = await page.locator(".dtaRel button:not(.is-far) > span > span").allInnerTexts()
    expect(names.length).toBeGreaterThan(0)
    for (const name of names) {
      await expect(page.locator(`.dtCanvasNode:has(.dtaCard:has-text("${name}"))`).first())
        .not.toHaveClass(/is-offscreen/)
    }
  })
})

/**
 * The framing guard must notice a real board change, not only a change of selection.
 *
 * Suppressing re-frames on identity-only renders is what stops a hover discarding a rolled lane. Taken too
 * far it also suppresses genuine graph updates, and then §6.4 leaves a newly linked record outside its lane
 * window and §6.6 can leave it under the panel — the guard would have bought roll persistence by breaking the
 * two rules it exists to serve.
 */
test.describe("a graph change still re-syncs and re-frames", () => {
  const FAR_RUN = 'e2000000-0000-4000-8000-000000000023'

  test("re-pointing the selected record's link brings the new record into view", async ({ page }) => {
    await open(page, "relink")

    // Selection is a procedure, not the thread's focal record, so the view supplies no `frameIds`. That
    // leaves the framing signature as the only thing that can notice this change.
    await expect(page.locator(".dtaCard.is-selected .dtaId")).toHaveText("HLRTP-000300.00")

    const before = await page.evaluate(id => {
      const node = document.querySelector(`[data-node-id="${id}"]`)!
      return {
        offscreen: node.classList.contains("is-offscreen"),
        lanes: [...document.querySelectorAll(".dtCanvasLaneHead")].length,
        selected: document.querySelector(".dtaCard.is-selected .dtaId")?.textContent,
      }
    }, FAR_RUN)

    // It starts rolled well out of its lane window.
    expect(before.offscreen).toBe(true)

    await page.locator("#relink").click()
    await page.waitForTimeout(1200)

    const after = await page.evaluate(id => {
      const node = document.querySelector(`[data-node-id="${id}"]`)!
      return {
        offscreen: node.classList.contains("is-offscreen"),
        lanes: [...document.querySelectorAll(".dtCanvasLaneHead")].length,
        selected: document.querySelector(".dtaCard.is-selected .dtaId")?.textContent,
      }
    }, FAR_RUN)

    // Nothing the old guard looked at changed: same selection, same lanes, same per-lane counts. Only which
    // record the selection links to. The newly linked run must nonetheless be synced into its lane window.
    expect(after.selected).toBe(before.selected)
    expect(after.lanes).toBe(before.lanes)
    expect(after.offscreen).toBe(false)
  })
})

/**
 * Recovery from a viewport that had not settled when the selection arrived.
 *
 * The canvas refuses to lay out from a rect under 320x240, because a freshly mounted panel or a preview
 * reports a fraction of its real size and laying out from that leaves the board wrongly zoomed in a corner.
 * A selection made before that point therefore has no frame to be framed into, and the repair is not `fit()`:
 * that moves the camera but never rolls a lane, so a directly linked record stays outside its window.
 */
test.describe("a selection made before the viewport settled", () => {
  const FAR_RUN = "e2000000-0000-4000-8000-000000000023"

  test("is synchronised and framed once a real frame arrives", async ({ page }) => {
    await open(page, "unsettled")

    // The host starts at 280px wide, below the threshold `frame()` accepts, so no framing can have run.
    const before = await page.evaluate(() => {
      const canvas = document.querySelector(".dtCanvas")?.getBoundingClientRect()
      return { width: Math.round(canvas?.width ?? 0) }
    })
    expect(before.width).toBeLessThan(320)

    await page.locator("#settle").click()
    await page.waitForTimeout(1500)

    const after = await page.evaluate(id => {
      const node = document.querySelector(`[data-node-id="${id}"]`)!
      const canvas = document.querySelector(".dtCanvas")!.getBoundingClientRect()
      const card = node.getBoundingClientRect()
      const panel = document.querySelector(".dtaPanel")?.getBoundingClientRect() ?? null
      return {
        width: Math.round(canvas.width),
        offscreen: node.classList.contains("is-offscreen"),
        selected: document.querySelector(".dtaCard.is-selected .dtaId")?.textContent ?? null,
        // Clear of the docked panel, per §6.6.
        clearOfPanel: !panel || card.bottom <= panel.top + 1 || card.top >= panel.bottom - 1 ||
          card.right <= panel.left + 1 || card.left >= panel.right - 1,
      }
    }, FAR_RUN)

    expect(after.width).toBeGreaterThan(320)
    // The selection survives the settle...
    expect(after.selected).toBe("HLRTP-000300.00")
    // ...and its directly linked record has been rolled into its lane window, not left where it started.
    // Without the fix this stays `is-offscreen`: the framing key was consumed while the frame was refused,
    // and the resize path only called `fit()`, which cannot roll a lane.
    expect(after.offscreen).toBe(false)
    expect(after.clearOfPanel).toBe(true)
  })
})
