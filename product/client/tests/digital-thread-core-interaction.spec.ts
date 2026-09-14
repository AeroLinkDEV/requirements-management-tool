import { expect, renderedTest as test } from "./isolated-client-test"
import { waitForCanvasSettled } from "./digital-thread-rendered-helpers"

/**
 * Core #1022 interaction, in the real shared canvas.
 *
 * Hover is stationary emphasis; selection is persistent and owns the thread; the floating preview target is
 * gone; eligible clipped/offscreen linked cards are revealed over background in their own lane. The deeper
 * view-by-view journeys stay in their own files.
 */

const open = async (page: import("@playwright/test").Page, scenario: string) => {
  await page.goto(`/tests/fixtures/change-network.html?case=${scenario}`)
  await waitForCanvasSettled(page)
}

const transformOf = async (scene: import("@playwright/test").Locator) =>
  /transform:[^;]*/.exec((await scene.getAttribute("style")) ?? "")?.[0] ?? ""

/**
 * Comparable evidence capture.
 *
 * Opt-in through `AEROLINK_1022_EVIDENCE`, so the same retained spec that proves the behaviour can also write
 * the screenshots a reviewer needs, with the fixture, viewport and code revision all implied by the run. No
 * evidence is written during ordinary qualification.
 */
const shoot = async (page: import("@playwright/test").Page, name: string) => {
  const directory = process.env.AEROLINK_1022_EVIDENCE
  if (!directory) return
  await page.screenshot({ path: `${directory}/${name}.png` })
}

test("a tall partially visible card stays painted and its native tail action becomes reachable by rolling", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto("/tests/fixtures/digital-thread-contract.html?case=tall")
  const card = page.locator('[data-node-id="link"]')
  const action = card.getByRole("button", { name: "Native tail action" })
  await expect(card).toBeVisible()
  await page.waitForTimeout(700)
  const frame = await usableFrame(page)
  const rect = (await card.boundingBox())!
  expect(rect.y).toBeLessThan(frame.bottom)
  expect(rect.y + rect.height).toBeGreaterThan(frame.bottom)
  await expect(card).not.toHaveClass(/is-offscreen/)
  await expect(action).toHaveAttribute("tabindex", "-1")
  const x = rect.x + rect.width / 2
  const y = Math.max(frame.top + 20, rect.y + 20)
  expect(await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.closest('[data-node-id]')?.getAttribute('data-node-id'), { x, y })).toBe("link")
  await shoot(page, "tall-card-partial-visible")
  const band = (await page.locator('[data-band="1"]').boundingBox())!
  // The canvas wheel zooms. Rolling uses the lane's exposed strip, clear of the card's own hit target.
  for (let i = 0; i < 4; i++) {
    await page.mouse.move(band.x + 5, frame.bottom - 20)
    await page.mouse.down()
    await page.mouse.move(band.x + 5, frame.top + 40, { steps: 8 })
    await page.mouse.up()
  }
  await expect.poll(async () => action.getAttribute("tabindex")).not.toBe("-1")
  await expect(card).not.toHaveClass(/is-offscreen/)
  await action.click()
  await expect(card.getByRole("button", { name: "Action activated" })).toBeVisible()
  await expect(page.locator('.dtCanvasNode[aria-pressed="true"]')).toHaveCount(0)
  await shoot(page, "tall-card-native-tail-reached")
})

test("StrictMode arrival clears easing and keeps a revealed Artifact card pointer accessible", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/artifact-thread.html?case=hlr&strict=1")
  const scene = page.locator(".dtCanvasScene")
  await expect(page.locator(".dtaPanel")).toBeVisible()
  await expect(scene).not.toHaveClass(/is-easing/)
  await page.locator('.dtaRel button:has-text("SRCR-00039.00")').first().click()
  await expect(scene).not.toHaveClass(/is-easing/)
  const card = page.locator('.dtaCard:has-text("SRCR-00039.00")')
  await card.click()
  await expect(card).toHaveClass(/is-selected/)
  await card.getByRole("button", { name: "Open this change" }).click()
  await shoot(page, "strictmode-native-action")
})

test("continuation affordances stay beside the inspector in every dock", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto("/tests/fixtures/artifact-thread.html?case=hlr&long=1")
  await expect(page.locator(".dtaPanel")).toBeVisible()
  for (const mode of ["Bottom", "Right", "Auto"]) {
    await page.locator(".dtaPanelTools").getByRole("button", { name: mode, exact: true }).click()
    await expect(page.locator(".dtCanvasScene")).not.toHaveClass(/is-easing/)
    const panel = (await page.locator(".dtaPanel").boundingBox())!
    for (const affordance of await page.locator(".dtCanvasPlacementNotice:visible, .dtCanvasContinuation:visible").all()) {
      const r = (await affordance.boundingBox())!
      expect(r.x >= panel.x + panel.width || r.x + r.width <= panel.x ||
        r.y >= panel.y + panel.height || r.y + r.height <= panel.y, `${mode} inspector content remains unobscured`).toBe(true)
    }
    await expect(page.locator(".dtCanvasOffscreen")).toHaveCount(0)
    await shoot(page, `affordances-${mode.toLowerCase()}`)
  }
})

test("genuine dense overflow remains keyboard reachable without replacing selection", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "dense")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await waitForCanvasSettled(page)
  await expect(page.locator('.dtCanvasContinuation[data-dir="down"]:visible')).not.toHaveCount(0)
  const tail = page.locator('[data-node-id="case-34"]')
  // Nineteen emphasized Case cards cannot share this lane window. Wrapper focus is deliberate navigation.
  await tail.focus()
  await waitForCanvasSettled(page)
  await expect(tail).toBeFocused()
  await expect(tail).not.toHaveClass(/is-offscreen/)
  const r = (await tail.boundingBox())!, frame = await usableFrame(page)
  expect(r.y).toBeGreaterThanOrEqual(frame.top - 1)
  expect(r.y + r.height).toBeLessThanOrEqual(frame.bottom + 1)
  await expect(root).toHaveAttribute('aria-pressed', 'true')
})

test("Bottom long-text identity and Right dense relationships scroll to their actual ends", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 1000 })
  await page.goto('/tests/fixtures/change-network.html?case=dense&long=1')
  await page.locator('[data-node-id="pr-5"]').click()
  await page.locator('.dtnPanelTools').getByRole('button', { name: 'Bottom', exact: true }).click()
  await page.locator('.dtnPanel').evaluate(panel => {
    const sizes = [...panel.querySelectorAll<HTMLElement>('*')].map(e => [e, parseFloat(getComputedStyle(e).fontSize)] as const)
    for (const [element, size] of sizes) element.style.fontSize = `${size * 1.25}px`
  })
  await waitForCanvasSettled(page)
  const identity = page.locator('.dtnPanelIdentityCol')
  expect(await identity.evaluate(e => e.scrollHeight > e.clientHeight)).toBe(true)
  await identity.hover()
  await page.mouse.wheel(0, 1000)
  await expect.poll(() => identity.evaluate(e => e.scrollHeight - e.clientHeight - e.scrollTop)).toBeLessThanOrEqual(1)
  await shoot(page, 'bottom-long-text-scrolled-end')
  await page.goto('/tests/fixtures/artifact-thread.html?case=dense&long=1')
  await page.locator('.dtaPanelTools').getByRole('button', { name: 'Right', exact: true }).click()
  await waitForCanvasSettled(page)
  const relationships = page.locator('.dtaRel').last()
  expect(await relationships.evaluate(e => e.scrollHeight > e.clientHeight)).toBe(true)
  await relationships.hover()
  await page.mouse.wheel(0, 2000)
  await expect.poll(() => relationships.evaluate(e => e.scrollHeight - e.clientHeight - e.scrollTop)).toBeLessThanOrEqual(1)
  const list = (await relationships.boundingBox())!
  const tail = relationships.locator('button').last()
  const last = (await tail.boundingBox())!
  expect(last.y).toBeGreaterThanOrEqual(list.y - 1)
  expect(last.y + last.height).toBeLessThanOrEqual(list.y + list.height + 1)
  await tail.click({ trial: true })
  await shoot(page, 'right-dense-relationships-scrolled-end')
})

test("touch lane exploration reaches dense overflow without replacing selection or moving the camera", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, 'dense')
  await page.locator('[data-node-id="pr-5"]').click()
  await waitForCanvasSettled(page)
  const tail = page.locator('[data-node-id="case-34"]')
  await tail.focus() // Enter the crowded lane through its supported keyboard route.
  await waitForCanvasSettled(page)
  const before = (await tail.boundingBox())!
  const band = await page.locator('.dtCanvasBand').evaluateAll((bands, x) => {
    const rect = bands.map(band => band.getBoundingClientRect()).find(r => r.left <= x && r.right >= x)!
    return { x: rect.x, width: rect.width }
  }, before.x + before.width / 2)
  const frame = await usableFrame(page)
  const point = { x: band.x + 4, y: frame.top + 25 }
  const camera = await page.locator('.dtCanvasScene').evaluate(e => getComputedStyle(e).transform)
  const session = await page.context().newCDPSession(page)
  await session.send('Emulation.setTouchEmulationEnabled', { enabled: true })
  await session.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [point] })
  for (let step = 1; step <= 8; step++) {
    await session.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: point.x, y: point.y + step * 15 }] })
  }
  await session.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] })
  await expect.poll(async () => (await tail.boundingBox())!.y).toBeGreaterThan(before.y + 20)
  await expect(page.locator('.dtCanvasScene')).toHaveCSS('transform', camera)
  await expect(page.locator('[data-node-id="pr-5"]')).toHaveAttribute('aria-pressed', 'true')
  await session.detach()
})

test("unselected hover prepares an entirely left and above linked card without camera movement", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, 'reveal')
  const canvas = (await page.locator('.dtCanvas').boundingBox())!
  const pan = async (dx: number, dy = 0) => {
    await page.mouse.move(canvas.x + canvas.width - 8, canvas.y + 65)
    await page.mouse.down()
    await page.mouse.move(canvas.x + canvas.width - 8 + dx, canvas.y + 65 + dy, { steps: 10 })
    await page.mouse.up()
    await waitForCanvasSettled(page)
  }
  const link = page.locator('[data-node-id="pr-5"]')
  await pan(0, -280)
  const initial = (await link.boundingBox())!
  await pan(-initial.x - initial.width - 10)
  const before = (await link.boundingBox())!
  expect(before.x + before.width).toBeLessThanOrEqual(canvas.x)
  expect(before.y + before.height).toBeLessThan(canvas.y + 80)
  await expect(page.locator('.dtCanvasNode[aria-pressed=true]')).toHaveCount(0)
  await expect(page.locator('.dtnPanel')).toHaveCount(0)
  const scene = page.locator('.dtCanvasScene')
  const camera = await scene.evaluate(e => getComputedStyle(e).transform)
  await page.locator('[data-node-id="hlr-127"]').hover()
  const toolbar = (await page.locator('.dtCanvasControls').boundingBox())!
  await expect.poll(async () => (await link.boundingBox())!.y).toBeGreaterThanOrEqual(toolbar.y + toolbar.height + 37)
  const prepared = (await link.boundingBox())!
  expect(prepared.x).toBeCloseTo(before.x, 1)
  expect(prepared.y + prepared.height).toBeLessThan(canvas.y + canvas.height)
  await expect(scene).toHaveCSS('transform', camera)
  await expect(page.locator('.dtCanvasNode[aria-pressed=true]')).toHaveCount(0)
  await expect(page.locator('.dtnPanel')).toHaveCount(0)
  await shoot(page, 'left-above-true-unselected-hover')
})

/**
 * Pan the background, deliberately.
 *
 * Dragging near the canvas edge lands on the offscreen action strip or a lane band, so the gesture either does
 * nothing or rolls a lane — a "pan" that moves the camera by zero and makes a return assertion pass vacuously.
 * This picks a real gutter: just left of the first lane band, vertically between the toolbar and the strip.
 */
const panBackground = async (
  page: import("@playwright/test").Page,
  dx: number,
  dy = 0,
) => {
  const { x: gutterX, y } = await gutterPoint(page)
  await page.mouse.move(gutterX, y)
  await page.mouse.down()
  await page.mouse.move(gutterX + dx, y + dy)
  await page.mouse.up()
  await page.waitForTimeout(500)
}

/** A point that is genuinely between lanes (or outside them) and inside the canvas, suitable for panning. */
/**
 * The real usable space inside the canvas.
 *
 * Measure occupied DOM regions after the transaction. Do not read a product-computed usable-frame result:
 * toolbar, authored heading reserve, actual inspector and selected controls independently bound the space.
 */
const usableFrame = async (page: import("@playwright/test").Page) => {
  const canvasBox = (await page.locator(".dtCanvas").boundingBox())!
  const controls = await page.locator(".dtCanvasControls").boundingBox()
  const headingOffset = await page.locator(".dtCanvasLaneHead").first().evaluate(element =>
    Math.max(0, -parseFloat(getComputedStyle(element).top)))
  const top = Math.max(canvasBox.y + 40, (controls?.y ?? canvasBox.y) + (controls?.height ?? 38) + headingOffset + 8)
  const panelLocator = page.locator(".dtnPanel-bottom, .dticPanel-bottom, .dtaPanel-bottom")
  const panel = await panelLocator.count() ? await panelLocator.boundingBox() : null
  const bottom = panel ? panel.y - 12 : canvasBox.y + canvasBox.height
  return { canvasBox, top, bottom }
}

const gutterPoint = async (page: import("@playwright/test").Page) => {
  const canvasBox = (await page.locator(".dtCanvas").boundingBox())!
  /**
   * A real gutter between two lanes, chosen from bands that are on screen *now*.
   *
   * Band-relative coordinates go stale as soon as the board is panned, and a point that is no longer a gutter
   * either rolls a lane or falls outside the canvas — which is how a "pan" can silently move nothing.
   */
  const rects = await page.locator(".dtCanvasBand").evaluateAll(elements => elements.map(element => {
    const rect = element.getBoundingClientRect()
    return { x: rect.x, width: rect.width }
  }))
  let gutterX: number | null = null
  // Prefer clearly outside the band area entirely: right of the last band, else left of the first. This cannot
  // be stale or accidentally inside a lane, unlike a computed gap between two bands.
  const rightmost = rects.reduce((max, rect) => Math.max(max, rect.x + rect.width), canvasBox.x)
  const leftmost = rects.reduce((min, rect) => Math.min(min, rect.x), canvasBox.x + canvasBox.width)
  if (rightmost + 12 < canvasBox.x + canvasBox.width - 4) gutterX = rightmost + 12
  else if (leftmost - 12 > canvasBox.x + 4) gutterX = leftmost - 12
  for (let index = 0; index + 1 < rects.length; index += 1) {
    if (gutterX !== null) break
    const left = rects[index].x + rects[index].width
    const right = rects[index + 1].x
    const midpoint = left + (right - left) / 2
    if (right - left > 8 && midpoint > canvasBox.x + 4 && midpoint < canvasBox.x + canvasBox.width - 4) {
      gutterX = midpoint
      break
    }
  }
  if (gutterX === null) gutterX = canvasBox.x + canvasBox.width / 2
  return { x: gutterX, y: canvasBox.y + canvasBox.height / 2 }
}

test("hover emphasises without moving the camera or the source card", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await open(page, "hover")
  const root = page.locator('[data-node-id="pr-5"]')
  const scene = page.locator(".dtCanvasScene")
  const camera = await scene.getAttribute("style")
  const box = (await root.boundingBox())!
  await root.hover()
  await page.waitForTimeout(650)
  await expect(scene).toHaveAttribute("style", camera!)
  await shoot(page, "network-hover-stationary")
  const after = (await root.boundingBox())!
  expect(Math.abs(after.x - box.x)).toBeLessThanOrEqual(1)
  expect(Math.abs(after.y - box.y)).toBeLessThanOrEqual(1)
  await expect(page.locator(".dtCanvasHoverTarget")).toHaveCount(0)
})

test("hover emphasis ends without a popup and without a camera restore", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await open(page, "hover")
  const root = page.locator('[data-node-id="pr-5"]')
  const scene = page.locator(".dtCanvasScene")
  const camera = await scene.getAttribute("style")
  await root.hover()
  await page.waitForTimeout(650)
  await page.mouse.move(2, 2)
  await page.waitForTimeout(650)
  await expect(scene).toHaveAttribute("style", camera!)
  await expect(page.locator(".dtCanvasHoverTarget")).toHaveCount(0)
})

test("the real card is the click target and selection is persistent", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await open(page, "hover")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await shoot(page, "network-selected")
  await expect(page.locator(".dtCanvasHoverTarget")).toHaveCount(0)
  // Pointing at another reachable card, and waiting past the old dwell, must not replace the thread.
  const other = page.locator('.dtCanvasNode:not(.is-offscreen)').nth(4)
  await other.hover()
  await page.waitForTimeout(650)
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await expect(page.locator(".dtCanvasNode.is-selected")).toHaveAttribute("data-node-id", "pr-5")
})

test("a dense thread keeps its last emphasized record reachable through wrapper focus", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.emulateMedia({ reducedMotion: "reduce" })
  await open(page, "dense")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.focus()
  await root.press("Enter")
  await expect(root).toHaveAttribute("aria-pressed", "true")
  const tail = page.locator('[data-node-id="case-34"]')
  await tail.focus()
  await waitForCanvasSettled(page)
  await expect(tail).not.toHaveClass(/is-offscreen/)
  await tail.click({ trial: true })
  await expect(root).toHaveAttribute("aria-pressed", "true")
})

/**
 * CORE-01 integration proof.
 *
 * The lane-local reveal can push a linked card further down its lane than the lane's ordinary extent. The
 * reader must be able to scroll into that extra range by dragging, and clearing the selection must neither
 * move the camera nor clamp their lane back to the ordinary bound.
 */
test("a revealed lane can be scrolled into its temporary range and clear does not snap it back", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto('/tests/fixtures/digital-thread-contract.html?case=range')
  await waitForCanvasSettled(page)
  const link = page.locator('[data-node-id="link"]')
  const probe = page.locator('[data-node-id="background"]')
  const canvas = page.locator('.dtCanvas')
  const scale = await page.locator('.dtCanvasScene').evaluate(e => new DOMMatrix(getComputedStyle(e).transform).a)
  const canonical = (await link.boundingBox())!
  const band = (await page.locator('[data-band="1"]').boundingBox())!
  const ordinaryMinimum = Math.min(0, (band.y + band.height - canonical.y - canonical.height) / scale)
  await page.locator('[data-node-id="subj"]').click()
  await waitForCanvasSettled(page)
  const displaced = (await link.boundingBox())!
  expect(displaced.y).toBeGreaterThan(canonical.y + 20)
  const probeBefore = (await probe.boundingBox())!.y
  const camera = await transformOf(page.locator('.dtCanvasScene'))
  const frame = await usableFrame(page)
  for (let i = 0; i < 8; i++) {
    await page.mouse.move(band.x + 4, frame.bottom - 15)
    await page.mouse.down()
    await page.mouse.move(band.x + 4, frame.top + 15, { steps: 12 })
    await page.mouse.up()
  }
  await waitForCanvasSettled(page)
  const probeScrolled = (await probe.boundingBox())!.y
  expect((probeScrolled - probeBefore) / scale).toBeLessThan(ordinaryMinimum - 5)
  const tail = link.getByRole('button', { name: 'Native tail action' })
  await expect(tail).not.toHaveAttribute('tabindex', '-1')
  await tail.click()
  await expect(link.getByRole('button', { name: 'Action activated' })).toBeVisible()
  await expect(page.locator('[data-node-id="subj"]')).toHaveAttribute('aria-pressed', 'true')
  await expect(canvas).toBeVisible()
  await page.keyboard.press('Escape')
  await page.waitForTimeout(1000)
  expect(await transformOf(page.locator('.dtCanvasScene'))).toBe(camera)
  expect((await probe.boundingBox())!.y).toBeCloseTo(probeScrolled, 0)
  await expect(page.locator('.dtCanvasNode[aria-pressed="true"]')).toHaveCount(0)
  await page.mouse.move(band.x + 4, frame.top + 20)
  await page.mouse.down()
  await page.mouse.move(band.x + 4, frame.top + 60, { steps: 4 })
  await page.mouse.up()
  expect(Math.abs((await probe.boundingBox())!.y - probeScrolled - 40)).toBeLessThanOrEqual(6)
  // Continued reader scrolling can return to canonical content; the retained allowance is no scroll trap.
  for (let i = 0; i < 8; i++) {
    await page.mouse.move(band.x + 4, frame.top + 15)
    await page.mouse.down()
    await page.mouse.move(band.x + 4, frame.bottom - 15, { steps: 12 })
    await page.mouse.up()
  }
  await expect.poll(async () => (await probe.boundingBox())!.y).toBeGreaterThan(probeScrolled + 50)
})

test("Inside a change: selected hover is inert and a selected record owns its thread", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await page.goto("/tests/fixtures/inside-change.html?case=requirement")
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(800)
  const scene = page.locator(".dtCanvasScene")
  const camera = await transformOf(scene)
  const card = page.locator(".dtCanvasNode:not(.is-offscreen)").first()
  await card.hover()
  await page.waitForTimeout(650)
  expect(await transformOf(scene)).toBe(camera)
  await expect(page.locator(".dtCanvasHoverTarget")).toHaveCount(0)
  await shoot(page, "inside-hover-stationary")
  await card.click()
  await expect(card).toHaveAttribute("aria-pressed", "true")
  await shoot(page, "inside-selected")
  const other = page.locator(".dtCanvasNode:not(.is-offscreen)").nth(3)
  await other.hover()
  await page.waitForTimeout(650)
  await expect(card).toHaveAttribute("aria-pressed", "true")
})

test("Artifact thread: selected hover leaves the arrival selection unchanged", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await page.goto("/tests/fixtures/artifact-thread.html?case=hlr")
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(800)
  await expect(page.locator(".dtCanvasNode.is-selected")).toHaveCount(1)
  const scene = page.locator(".dtCanvasScene")
  const camera = await transformOf(scene)
  const card = page.locator(".dtCanvasNode:not(.is-offscreen)").first()
  await card.hover()
  await page.waitForTimeout(650)
  expect(await transformOf(scene)).toBe(camera)
  await expect(page.locator(".dtCanvasHoverTarget")).toHaveCount(0)
  await shoot(page, "artifact-hover-stationary")
  await page.mouse.move(2, 2)
  await page.waitForTimeout(400)
  expect(await transformOf(scene)).toBe(camera)
})

for (const zoomSteps of [0, 1, 3]) for (const view of [
  { name: "Network", path: "change-network.html?case=hover", panel: ".dtnPanel" },
  { name: "Inside", path: "inside-change.html?case=requirement", panel: ".dticPanel" },
  { name: "Artifact", path: "artifact-thread.html?case=hlr", panel: ".dtaPanel" },
]) test(`${view.name}: quiet to unselected hover after ${zoomSteps} zoom steps changes emphasis without camera movement`, async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await page.goto(`/tests/fixtures/${view.path}`)
  if (view.name !== "Network") await expect(page.locator(view.panel)).toBeVisible()
  await page.locator(".dtCanvas").focus()
  await page.keyboard.press("Escape")
  await expect(page.locator(".dtCanvasNode.is-selected")).toHaveCount(0)
  await expect(page.locator(view.panel)).toHaveCount(0)
  await page.waitForTimeout(450)
  for (let i = 0; i < zoomSteps; i++) await page.getByRole("button", { name: "Zoom out", exact: true }).click()
  const tier = await page.locator(".dtCanvasScene").getAttribute("data-tier")
  const camera = await transformOf(page.locator(".dtCanvasScene"))
  const card = page.locator(".dtCanvasNode:not(.is-offscreen)").first()
  const before = (await card.boundingBox())!
  await shoot(page, `${view.name.toLowerCase()}-tier${tier}-quiet`)
  await card.hover()
  await expect(page.locator(".dtCanvasEdges path.is-traced").first()).toBeAttached()
  for (let index = 0; index < 8; index += 1) {
    expect(await transformOf(page.locator(".dtCanvasScene"))).toBe(camera)
    expect(await page.locator(".dtCanvasScene").getAttribute("data-tier")).toBe(tier)
    const now = (await card.boundingBox())!
    expect(Math.abs(now.y - before.y)).toBeLessThan(1)
    expect(Math.abs(now.x - before.x)).toBeLessThan(1)
    await page.waitForTimeout(50)
  }
  await expect(page.locator(".dtCanvasNode.is-selected")).toHaveCount(0)
  await expect(page.locator(view.panel)).toHaveCount(0)
  await shoot(page, `${view.name.toLowerCase()}-tier${tier}-true-unselected-hover`)
})

for (const promoted of [false, true]) test(`same-tier rendered ${promoted ? "promoted-subject" : "linked"} growth repairs only colliding temporary geometry and converges`, async ({ page }) => {
  // The authored canvas remains 700px high. Keep its 720px fixture root inside the document viewport so a
  // normal click on the external text-size control cannot scroll the document and contaminate screen y.
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto("/tests/fixtures/digital-thread-contract.html?case=growth")
  const linked = page.locator('[data-node-id="link"]')
  const subject = page.locator('[data-node-id="subj"]')
  await expect(subject).toBeVisible()
  await page.waitForTimeout(700)
  const before = (await linked.boundingBox())!
  await subject.hover()
  await page.waitForTimeout(600)
  const revealed = (await linked.boundingBox())!
  expect(before.y - revealed.y).toBeGreaterThan(100)
  await (promoted ? linked : subject).click({ position: { x: 6, y: 6 } })
  await page.waitForTimeout(900)
  const residents = page.locator('[data-node-id^="resident-"]')
  const residentBefore = await residents.evaluateAll(nodes => nodes.map(node => node.getBoundingClientRect().y))
  const tier = await page.locator(".dtCanvasScene").getAttribute("data-tier")
  const camera = await transformOf(page.locator(".dtCanvasScene"))
  const height = (await linked.boundingBox())!.height
  expect(await page.evaluate(() => window.scrollY)).toBe(0)
  await page.getByRole("button", { name: "Change text size" }).click()
  await expect.poll(async () => (await linked.boundingBox())!.height).toBeGreaterThan(height + 50)
  await page.waitForTimeout(700)
  const grown = (await linked.boundingBox())!
  expect(await page.evaluate(() => window.scrollY)).toBe(0)
  const neighbor = (await page.locator('[data-node-id="neighbor"]').boundingBox())!
  // A populated foreground pair replaces the obsolete background obstacle assertions.
  expect(grown.y + grown.height <= neighbor.y || grown.y >= neighbor.y + neighbor.height).toBe(true)
  const residentBoxes = await residents.evaluateAll(nodes => nodes.map(node => {
    const r = node.getBoundingClientRect(); return { top: r.top, bottom: r.bottom }
  }))
  expect(residentBoxes.some(box => grown.y < box.bottom && grown.y + grown.height > box.top),
    'foreground/background overlap is allowed and actually exercised').toBe(true)
  expect(await residents.evaluateAll(nodes => nodes.map(node => node.getBoundingClientRect().y))).toEqual(residentBefore)
  expect(await page.locator(".dtCanvasScene").getAttribute("data-tier")).toBe(tier)
  expect(await transformOf(page.locator(".dtCanvasScene"))).toBe(camera)
  await page.waitForTimeout(600)
  expect(Math.abs((await linked.boundingBox())!.y - grown.y)).toBeLessThan(1)
  await shoot(page, `same-tier-${promoted ? "promoted" : "linked"}-growth-reconciled`)
})

test("a small network story fits before exploration and survives horizontal away and back", async ({ page }) => {
  await page.setViewportSize({ width: 1100, height: 900 })
  await open(page, "hover")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await waitForCanvasSettled(page)
  const endpoint = page.locator('[data-node-id="case-34"]')
  const assertUsable = async () => {
    const { canvasBox, top, bottom } = await usableFrame(page)
    const r = (await endpoint.boundingBox())!
    expect(r.x).toBeGreaterThanOrEqual(canvasBox.x - 1)
    expect(r.x + r.width).toBeLessThanOrEqual(canvasBox.x + canvasBox.width + 1)
    expect(r.y).toBeGreaterThanOrEqual(top - 1)
    expect(r.y + r.height).toBeLessThanOrEqual(bottom + 1)
  }
  await assertUsable() // Automatic fitting is proved before any recovery or exploration.
  const scene = page.locator('.dtCanvasScene')
  const before = await transformOf(scene)
  await panBackground(page, -700)
  expect(await transformOf(scene)).not.toBe(before)
  await panBackground(page, 700)
  expect(await transformOf(scene)).toBe(before)
  await assertUsable()
  await expect(root).toHaveAttribute('aria-pressed', 'true')
})

test("manual vertical exploration survives horizontal away and back", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "dense")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(900)

  const band = page.locator(".dtCanvasBand.is-rollable").first()
  const canvasBox = (await page.locator(".dtCanvas").boundingBox())!
  const bandBox = (await band.boundingBox())!
  const grabX = bandBox.x + 4
  const top = Math.max(bandBox.y, canvasBox.y) + 12
  const bottom = Math.min(bandBox.y + bandBox.height, canvasBox.y + canvasBox.height) - 12
  const probeId = await page.evaluate(bandRect => {
    const nodes = [...document.querySelectorAll<HTMLElement>(".dtCanvasNode")]
      .filter(node => node.querySelector(".dtnCard"))
    const inside = nodes.find(node => {
      const rect = node.getBoundingClientRect()
      return rect.left >= bandRect.x - 2 && rect.right <= bandRect.x + bandRect.width + 2 &&
        rect.top > bandRect.y + 4 && rect.bottom < bandRect.y + bandRect.height - 4
    })
    return inside?.dataset.nodeId ?? null
  }, { x: bandBox.x, y: bandBox.y, width: bandBox.width, height: bandBox.height })
  expect(probeId, "no card belongs to the band being rolled").toBeTruthy()
  const probe = page.locator(`[data-node-id="${probeId}"]`)
  const yOf = async () => Number(/translate\([^,]+,\s*(-?[\d.]+)px\)/
    .exec((await probe.getAttribute("style")) ?? "")?.[1] ?? NaN)
  const cameraNow = async () => {
    const value = /transform:[^;]*/.exec((await page.locator(".dtCanvasScene").getAttribute("style")) ?? "")?.[0] ?? ""
    const [, x, y, zoom] = /translate\((-?[\d.]+)px,\s*(-?[\d.]+)px\)\s*scale\(([\d.]+)\)/.exec(value) ?? []
    return { x: Number(x), y: Number(y), zoom: Number(zoom) }
  }

  // The reader scrolls this lane deliberately.
  const beforeScroll = await yOf()
  await page.mouse.move(grabX, (top + bottom) / 2)
  await page.mouse.down()
  await page.mouse.move(grabX, (top + bottom) / 2 - 160)
  await page.mouse.up()
  await page.waitForTimeout(400)
  const scrolled = await yOf()
  expect(scrolled - beforeScroll, "the lane did not scroll").toBeLessThan(-30)

  // Away and back, purely horizontally, on the background.
  const cameraBefore = await cameraNow()
  await panBackground(page, -1000)
  /**
   * The outward gesture must actually do something, or the return assertion would pass vacuously: matching
   * a camera that never moved proves nothing. Assert the camera moved and the lane really left the view.
   */
  const cameraAway = await cameraNow()
  expect(Math.abs(cameraAway.x - cameraBefore.x), "the outward pan did not move the camera")
    .toBeGreaterThan(100)
  const awayBox = await probe.boundingBox()
  const laneOutside = !awayBox ||
    awayBox.x + awayBox.width <= canvasBox.x ||
    awayBox.x >= canvasBox.x + canvasBox.width
  expect(laneOutside, "the outward pan did not take the lane outside the usable region").toBe(true)

  await panBackground(page, 1000)

  // The camera comes back to where the reader left it, and the lane keeps the position they put it in.
  const cameraAfter = await cameraNow()
  expect(Math.abs(cameraAfter.x - cameraBefore.x)).toBeLessThanOrEqual(2)
  expect(Math.abs(cameraAfter.y - cameraBefore.y)).toBeLessThanOrEqual(2)
  expect(Math.abs(cameraAfter.zoom - cameraBefore.zoom)).toBeLessThanOrEqual(0.01)
  expect(Math.abs((await yOf()) - scrolled), "the manual lane position was not retained").toBeLessThanOrEqual(4)
  await expect(root).toHaveAttribute("aria-pressed", "true")
})

/**
 * Branch A of the reveal contract: enough safe space exists.
 *
 * A short lane's linked card sits above the current viewing height while the usable window below it is empty.
 * Hovering its neighbour must place the card inside that window automatically — no Show action, no vertical
 * hunt, and no camera movement. Fixture `?case=reveal` is a shared-canvas contract arrangement.
 */
test("a linked card is revealed automatically into available space without moving the camera", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=reveal")
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(800)

  // Pan the view down so the short lane's card is above the usable window and the space below it is free.
  const canvasBox = (await page.locator(".dtCanvas").boundingBox())!
  const gutter = { x: canvasBox.x + 24, y: canvasBox.y + canvasBox.height - 30 }
  await page.mouse.move(gutter.x, gutter.y)
  await page.mouse.down()
  await page.mouse.move(gutter.x, gutter.y - 280)
  await page.mouse.up()
  await page.waitForTimeout(600)

  const linked = page.locator('[data-node-id="pr-5"]')
  const subject = page.locator('[data-node-id="hlr-127"]')
  const scene = page.locator(".dtCanvasScene")
  const camera = await transformOf(scene)

  // Preconditions: the endpoint is not usable, and its lane has empty space inside the usable window.
  const before = await linked.boundingBox()
  const usable = {
    top: canvasBox.y + 40,
    bottom: canvasBox.y + canvasBox.height - 40,
  }
  const startsOutside = !before || before.y + before.height <= usable.top || before.y >= usable.bottom ||
    (await linked.getAttribute("class"))?.includes("is-offscreen") === true
  expect(startsOutside, "the fixture did not start with the linked card outside the usable region").toBe(true)

  await subject.hover()
  await page.waitForTimeout(700)

  // It arrives inside the usable region on its own: no Show action, and the camera never moved.
  await expect(linked, "the linked card was not revealed into available space")
    .not.toHaveClass(/is-offscreen/)
  const after = (await linked.boundingBox())!
  expect(after.y).toBeGreaterThanOrEqual(usable.top - 1)
  expect(after.y + after.height).toBeLessThanOrEqual(usable.bottom + 1)
  expect(await transformOf(scene)).toBe(camera)
  await expect(page.locator(".dtCanvasHoverTarget")).toHaveCount(0)

  // Leaving the hover retires the temporary contribution.
  await page.mouse.move(2, 2)
  await page.waitForTimeout(700)
  const retired = await linked.boundingBox()
  const backOutside = !retired || retired.y + retired.height <= usable.top || retired.y >= usable.bottom ||
    (await linked.getAttribute("class"))?.includes("is-offscreen") === true
  expect(backOutside, "hover exit did not retire the temporary placement").toBe(true)
  expect(await transformOf(scene)).toBe(camera)
})

/**
 * Selecting a record the reveal had just relocated.
 *
 * Retaining the numerical displacement is not enough: when a relocated card becomes the subject, the previous
 * selection collapses and this one expands, so the base layout it was measured against changes. The card must
 * stay where the reader last saw it rather than snapping back to its distant ordinary row.
 */
test("clicking a relocated linked card keeps it where the reader saw it", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=reveal")
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(800)

  // Same arrangement as the available-space test: pan down so the short lane's card starts above the window.
  const canvasBox = (await page.locator(".dtCanvas").boundingBox())!
  await panBackground(page, 0, -420)

  const root = page.locator('[data-node-id="pr-5"]')
  const subject = page.locator('[data-node-id="hlr-127"]')
  // Same precondition as the available-space test: the card must be outside the *usable* region, whether or not
  // the canvas also classes it as fully off-screen.
  const usableTop = canvasBox.y + 40
  const startBox = await root.boundingBox()
  const startsOutside = !startBox || startBox.y + startBox.height <= usableTop ||
    (await root.getAttribute("class"))?.includes("is-offscreen") === true
  expect(startsOutside, "the fixture no longer starts with the linked card out of view").toBe(true)
  await subject.click({ position: { x: 6, y: 6 } })
  await expect(subject).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(1000)
  await expect(root, "the reveal did not relocate the linked card into view")
    .not.toHaveClass(/is-offscreen/)

  const yOfTarget = async () => Number(/translate\([^,]+,\s*(-?[\d.]+)px\)/
    .exec((await root.getAttribute("style")) ?? "")?.[1] ?? NaN)
  const seenAt = await yOfTarget()
  // Click the card body rather than an inner identifier link, so this is a card selection.
  await root.click({ position: { x: 6, y: 6 } })
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(1000)
  const nowAt = await yOfTarget()
  expect(
    Math.abs(nowAt - seenAt),
    `the relocated card moved from ${seenAt.toFixed(1)} to ${nowAt.toFixed(1)} when it became the subject`,
  ).toBeLessThanOrEqual(8)
  expect(canvasBox.width).toBeGreaterThan(0)
})

/**
 * Manual input takes over an automatic camera move from the position actually displayed.
 *
 * The board eases toward a commanded destination. If a reader grabs it mid-flight, the freeze must happen at
 * what they can see — capturing the model's destination instead would snap the board forward the moment they
 * touched it. The assertion is therefore a delta: the camera should move by exactly the gesture, from wherever
 * it was painted when the gesture began, not from where the automatic move was heading.
 */
/**
 * Resolution note: an earlier version of this test reported "the pan is applied to the model but never painted"
 * because its matrix parser skipped one group too few, reading the scale as x and the translate x as y. The
 * instrumented run showed the truth — inline style `translate(397.095px, …)` after the pan, and samples moving
 * 175.213 -> 375.213, exactly the 200 px gesture. The product was correct; the measurement was not.
 */
test("a drag takes over an automatic camera move from the displayed position", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "dense")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(400)

  /**
   * The DISPLAYED transform, from computed style.
   *
   * While the retained CSS transition is running, the inline style holds the commanded destination — reading it
   * would measure where the board is going, not what the reader can see. Astra's requirement for this proof is
   * the displayed position, so the matrix is read from computed style.
   */
  const displayed = async () => {
    const matrix = await page.locator(".dtCanvasScene").evaluate(element =>
      window.getComputedStyle(element).transform)
    // matrix(a, b, c, d, e, f): the translate lives in e/f and the scale in a/d. Skipping one group too few here
    // reported the scale as x and the real x as y, which made a correct 200 px pan look like no movement at all.
    const [, a, , , , e, f] = /matrix\(([-\d.]+),\s*([-\d.]+),\s*([-\d.]+),\s*([-\d.]+),\s*([-\d.]+),\s*([-\d.]+)\)/
      .exec(matrix) ?? []
    return { zoom: Number(a), x: Number(e), y: Number(f) }
  }

  // Command an automatic move, then interrupt it without waiting for it to finish. The gesture is driven by
  // hand here rather than through the helper so the camera can be sampled at the exact moment of the press:
  // sampling earlier measures the ease's own advance, not the takeover. The grab point comes from the same
  // proven gutter calculation the panning helper uses.
  const grab = await gutterPoint(page)
  const gutterX = grab.x
  const gutterY = grab.y
  await page.getByRole("button", { name: "Fit entire story" }).click()

  // Prove automatic motion is genuinely in progress before interrupting it: two displayed samples a frame apart
  // must differ, or the "takeover" would be measured against an idle board.
  const moving = await expect.poll(async () => {
    const first = await displayed()
    await page.waitForTimeout(60)
    const second = await displayed()
    return Math.abs(second.x - first.x) > 1 || Math.abs(second.y - first.y) > 1 || Math.abs(second.zoom - first.zoom) > 0.005
  }, { timeout: 2_000 }).toBe(true)
  void moving

  await page.mouse.move(gutterX, gutterY)
  await page.mouse.down()
  const atPress = await displayed()

  // Hold without moving: the board must stay exactly where the reader grabbed it.
  await page.waitForTimeout(350)
  const duringHold = await displayed()
  expect(
    Math.abs(duringHold.x - atPress.x),
    `the board kept travelling while the pointer was held (${(duringHold.x - atPress.x).toFixed(1)} units)`,
  ).toBeLessThanOrEqual(3)
  expect(Math.abs(duringHold.zoom - atPress.zoom)).toBeLessThanOrEqual(0.01)
  expect(Math.abs(duringHold.y - atPress.y)).toBeLessThanOrEqual(3)

  await page.mouse.move(gutterX + 200, gutterY)
  await page.mouse.up()
  await page.waitForTimeout(200)
  const afterDrag = await displayed()

  // And the reader's gesture is then applied in full from that frozen position.
  const travelled = afterDrag.x - atPress.x

  /**
   * The gesture is applied in full from the frozen position.
   *
   * Resolution of the earlier "half travel" reading (100.6 px for a 200 px drag): it was a measurement fault,
   * not a product defect. The camera was sampled before the asynchronous gap spent locating the gutter, while
   * the retained ease was still running — so the sample predated the freeze and the difference mixed the
   * ease's own advance with the gesture. Sampling at the press (above) and measuring travel from there gives
   * the true property, which passes strictly. The press check above guards the severe failure — a jump toward
   * the commanded destination — because a frame-exact "no movement at the press" is not measurable from
   * Playwright while the ease is live.
   */
  expect(
    Math.abs(travelled - 200),
    `takeover travelled ${travelled.toFixed(1)} px for a 200 px gesture`,
  ).toBeLessThanOrEqual(10)
  // And it did not keep travelling toward the commanded destination afterwards.
  await page.waitForTimeout(600)
  const settled = await displayed()
  expect(Math.abs(settled.x - afterDrag.x)).toBeLessThanOrEqual(4)
  expect(Math.abs(settled.zoom - afterDrag.zoom)).toBeLessThanOrEqual(0.02)
})

/**
 * Reduced motion changes the journey, not the destination.
 *
 * With the preference set, the same selection must produce the same final geometry — the linked card inside
 * the usable window — while the board's transform is not being transitioned at all.
 */
test("reduced motion reaches the same arrangement without animating", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" })
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=reveal")
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(800)
  await panBackground(page, 0, -420)

  const { canvasBox, top: usableTop } = await usableFrame(page)
  const linked = page.locator('[data-node-id="pr-5"]')
  const startBox = await linked.boundingBox()
  const startsOutside = !startBox || startBox.y + startBox.height <= usableTop ||
    (await linked.getAttribute("class"))?.includes("is-offscreen") === true
  expect(startsOutside, "the reduced-motion case did not start with the card out of view").toBe(true)

  // The stylesheet must not transition the scene under this preference.
  const transition = await page.locator(".dtCanvasScene").evaluate(element =>
    window.getComputedStyle(element).transitionDuration)
  expect(transition === "0s" || transition === "0s, 0s").toBe(true)

  await page.locator('[data-node-id="hlr-127"]').click({ position: { x: 6, y: 6 } })
  await expect(linked, "reduced motion did not reach the same arrangement")
    .not.toHaveClass(/is-offscreen/)
  const after = (await linked.boundingBox())!
  expect(after.y).toBeGreaterThanOrEqual(usableTop - 1)
  expect(after.y + after.height).toBeLessThanOrEqual(canvasBox.y + canvasBox.height - 40 + 1)
})

/**
 * Hover-to-click promotion while the reveal is still moving.
 *
 * The press that selects the hovered subject must not be mistaken for abandoning the reveal: the arrangement
 * carries over, the linked card still arrives, and the exact subject is the one selected. A rebuild from the
 * ordinary rows would show as the card failing to arrive or flashing back out.
 */
test("clicking during an incoming reveal keeps the arrangement and selects that subject", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=reveal")
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(800)
  await panBackground(page, 0, -420)

  const initialFrame = await usableFrame(page)
  const linked = page.locator('[data-node-id="pr-5"]')
  const subject = page.locator('[data-node-id="hlr-127"]')
  const camera = await transformOf(page.locator(".dtCanvasScene"))

  /**
   * Record the linked card's screen position every frame, with a marker at the moment of the click.
   *
   * Waiting a fixed 380 ms and then clicking does not by itself prove the reveal was still moving at
   * activation — it proves only that time passed. This samples the actual card, so "the arrangement was in
   * flight when the press landed" becomes a measurement instead of an assumption.
   */
  await page.evaluate(() => {
    const view = window as unknown as { __samples?: (number | string)[]; __sampling?: boolean }
    view.__samples = []
    view.__sampling = true
    const sample = () => {
      if (!view.__sampling) return
      const node = document.querySelector<HTMLElement>('[data-node-id="pr-5"]')
      const y = node?.getBoundingClientRect().y ?? Number.NaN
      view.__samples!.push(Number.isFinite(y) ? Math.round(y * 10) / 10 : null as unknown as number)
      requestAnimationFrame(sample)
    }
    requestAnimationFrame(sample)
  })

  await subject.hover()
  // Just past the hover dwell: the reveal has begun but has not finished.
  await page.waitForTimeout(380)
  await page.evaluate(() => { (window as unknown as { __samples?: unknown[] }).__samples!.push("CLICK") })
  await subject.click({ position: { x: 6, y: 6 } })
  await expect(subject).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(900)

  const trace = await page.evaluate(() => {
    const view = window as unknown as { __samples?: unknown[]; __sampling?: boolean }
    view.__sampling = false
    return view.__samples ?? []
  })
  const clickAt = trace.indexOf("CLICK")
  const beforeClick = trace.slice(0, clickAt).filter((value): value is number => typeof value === "number")
  expect(clickAt, "the click marker was not recorded").toBeGreaterThan(0)
  const travelBeforeClick = beforeClick.length
    ? Math.max(...beforeClick) - Math.min(...beforeClick)
    : 0
  expect(
    travelBeforeClick,
    `the reveal was not in flight when the press landed (card moved ${travelBeforeClick.toFixed(1)} px before it)`,
  ).toBeGreaterThan(2)

  await expect(linked, "the promoted click did not keep the reveal").not.toHaveClass(/is-offscreen/)
  const { top: usableTop, bottom: usableBottom } = await usableFrame(page)
  const arrived = (await linked.boundingBox())!
  expect(arrived.y).toBeGreaterThanOrEqual(usableTop - 1)
  expect(arrived.y + arrived.height).toBeLessThanOrEqual(usableBottom + 1)
  expect(usableBottom).toBeLessThan(initialFrame.bottom)
  const selectedBox = (await subject.boundingBox())!
  expect(selectedBox.y).toBeGreaterThanOrEqual(usableTop - 1)
  expect(selectedBox.y + selectedBox.height).toBeLessThanOrEqual(usableBottom + 1)
  /**
   * A click may take its bounded readability correction — here the tray's arrival changed the usable band, and
   * framing settled exactly on the documented 0.81 selection floor. What must hold is that the floor was
   * respected and the correction stayed bounded, not that the camera is bit-identical to the hover state.
   */
  const after = /translate\((-?[\d.]+)px,\s*(-?[\d.]+)px\)\s*scale\(([\d.]+)\)/
    .exec(await transformOf(page.locator(".dtCanvasScene")))
  const before = /translate\((-?[\d.]+)px,\s*(-?[\d.]+)px\)\s*scale\(([\d.]+)\)/.exec(camera)
  expect(after, "the camera transform became unreadable").toBeTruthy()
  const afterZoom = Number(after![3])
  expect(afterZoom, `the click-time correction went below the readable floor (zoom ${afterZoom})`)
    .toBeGreaterThanOrEqual(0.81 - 0.01)
  expect(Math.abs(Number(after![1]) - Number(before![1])), "the click-time pan was not bounded")
    .toBeLessThanOrEqual(400)
})

/**
 * A new selection made while the previous thread is still retiring.
 *
 * The old subject's temporary geometry is on its way out; the new subject must take over cleanly — exactly one
 * selected record, no interference from the retirement, and no stale callback restoring the previous camera or
 * subject afterwards.
 */
test("a new selection during cleanup replaces the old subject cleanly", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "dense")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(900)

  // A different, reachable card to become the new subject.
  // Clear, then select the new subject immediately — before the retirement has finished.
  /**
   * Prove an outgoing contribution is genuinely still retiring when the new selection lands.
   *
   * A traced card that the reveal had displaced is sampled every frame: it must still be moving after Escape and
   * before the new press, or "selection during cleanup" would be describing an idle board.
   */
  // A *displaced* card: only records with no intersection with the usable window receive a temporary
  // displacement, so an off-screen traced card is the one with something to retire.
  const retiringId = await page.locator(".dtCanvasNode.is-offscreen:has(.dtnCard:not(.is-untraced))")
    .first().getAttribute("data-node-id")
  expect(retiringId, "no traced card was available to observe the retirement").toBeTruthy()
  await page.evaluate(retiringIdValue => {
    const view = window as unknown as { __cleanup?: (number | string)[]; __cleanupSampling?: boolean }
    view.__cleanup = []
    view.__cleanupSampling = true
    const sample = () => {
      if (!view.__cleanupSampling) return
      const node = document.querySelector<HTMLElement>(`[data-node-id="${retiringIdValue}"]`)
      const y = node?.getBoundingClientRect().y ?? Number.NaN
      view.__cleanup!.push(Number.isFinite(y) ? Math.round(y * 10) / 10 : (null as unknown as number))
      requestAnimationFrame(sample)
    }
    requestAnimationFrame(sample)
  }, retiringId)

  await page.keyboard.press("Escape")
  await expect(page.locator('.dtCanvasNode[aria-pressed="true"]')).toHaveCount(0)
  await page.waitForTimeout(120)
  await page.evaluate(() => { (window as unknown as { __cleanup?: unknown[] }).__cleanup!.push("SELECT") })
  /**
   * Resolve the target *after* the clear, and take its id and box together.
   *
   * Capturing the id before the clear and the box after it pointed at two different elements: clearing changes
   * which cards are off-screen, so a positional locator re-resolves and the click lands on a neighbour.
   */
  const other = page.locator('.dtCanvasNode:not(.is-offscreen):has(.dtnCard)').nth(3)
  const otherId = await other.getAttribute("data-node-id")
  expect(otherId).toBeTruthy()
  const box = (await other.boundingBox())!
  await page.mouse.click(box.x + 6, box.y + 6)
  await page.waitForTimeout(900)

  const cleanupTrace = await page.evaluate(() => {
    const view = window as unknown as { __cleanup?: unknown[]; __cleanupSampling?: boolean }
    view.__cleanupSampling = false
    return view.__cleanup ?? []
  })
  const selectAt = cleanupTrace.indexOf("SELECT")
  const beforeSelect = cleanupTrace.slice(0, selectAt).filter((value): value is number => typeof value === "number")
  const retirementTravel = beforeSelect.length ? Math.max(...beforeSelect) - Math.min(...beforeSelect) : 0
  expect(
    retirementTravel,
    `no outgoing contribution was retiring when the new selection landed (moved ${retirementTravel.toFixed(1)} px)`,
  ).toBeGreaterThan(2)

  const pressed = page.locator('.dtCanvasNode[aria-pressed="true"]')
  const selected = page.locator(".dtCanvasNode.is-selected")
  await expect(pressed).toHaveCount(1)
  await expect(selected).toHaveCount(1)
  await expect(pressed).toHaveAttribute("data-node-id", otherId!)
  await expect(root).not.toHaveAttribute("aria-pressed", "true")
  // The retirement did not quietly restore the old subject after the new one arrived.
  await page.waitForTimeout(800)
  await expect(pressed).toHaveCount(1)
  await expect(pressed).toHaveAttribute("data-node-id", otherId!)
})

/**
 * Clearing while an automatic framing is still running.
 *
 * The old transition must not keep travelling once the selection is gone: the reader pressed Escape while
 * looking at a particular frame, and that frame is where the camera stays. Measured as *remaining travel
 * after the clear* — sampling before the clear would include the ease's own progress during Playwright's
 * sampling window and would not be a measurement of the clear at all.
 */
test("clearing during motion stops the camera where the reader saw it", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "dense")
  const cameraNumbers = async () => {
    const value = /transform:[^;]*/.exec((await page.locator(".dtCanvasScene").getAttribute("style")) ?? "")?.[0] ?? ""
    const [, x, y, zoom] = /translate\((-?[\d.]+)px,\s*(-?[\d.]+)px\)\s*scale\(([\d.]+)\)/.exec(value) ?? []
    return { x: Number(x), y: Number(y), zoom: Number(zoom) }
  }

  // Start an automatic move and clear immediately, without letting it finish.
  const card = page.locator('.dtCanvasNode:not(.is-offscreen):has(.dtnCard)').nth(2)
  const box = (await card.boundingBox())!
  await page.mouse.click(box.x + 6, box.y + 6)
  await page.keyboard.press("Escape")
  const atClear = await cameraNumbers()

  await expect(page.locator('.dtCanvasNode[aria-pressed="true"]')).toHaveCount(0)
  await page.waitForTimeout(700)
  const after = await cameraNumbers()
  expect(
    Math.abs(after.x - atClear.x),
    `the camera kept travelling after clear by ${(after.x - atClear.x).toFixed(1)} units`,
  ).toBeLessThanOrEqual(4)
  expect(Math.abs(after.y - atClear.y)).toBeLessThanOrEqual(4)
  expect(Math.abs(after.zoom - atClear.zoom)).toBeLessThanOrEqual(0.02)
})

/**
 * Arrow navigation still walks the lane it is in.
 *
 * The reveal changed the order the canvas walks (displayed position rather than canonical row), so this is the
 * regression the change could plausibly cause: Down must move focus to another card **in the same lane**, and
 * that card must be reachable by eye.
 */
test("Arrow Down moves focus within the same lane and keeps it visible", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "hover")
  // One tab stop per lane; the first lane holds a single card, so the second stop (the high-level lane with many
  // cards) is the one where Down has somewhere to go.
  const first = page.locator('.dtCanvasNode:not(.is-offscreen)[tabindex="0"]').nth(1)
  await first.focus()
  const before = await page.evaluate(() => {
    const active = document.activeElement as HTMLElement | null
    const x = Number(/translate\((-?[\d.]+)px/.exec(active?.style.transform ?? "")?.[1] ?? NaN)
    const y = Number(/translate\([^,]+,\s*(-?[\d.]+)px\)/.exec(active?.style.transform ?? "")?.[1] ?? NaN)
    return { id: active?.dataset.nodeId ?? null, x, y }
  })
  await page.keyboard.press("ArrowDown")
  await page.waitForTimeout(300)
  const after = await page.evaluate(() => {
    const active = document.activeElement as HTMLElement | null
    const x = Number(/translate\((-?[\d.]+)px/.exec(active?.style.transform ?? "")?.[1] ?? NaN)
    const y = Number(/translate\([^,]+,\s*(-?[\d.]+)px\)/.exec(active?.style.transform ?? "")?.[1] ?? NaN)
    return { id: active?.dataset.nodeId ?? null, x, y, offscreen: active?.classList.contains("is-offscreen") ?? false }
  })
  expect(after.id, "Arrow Down did not move focus").not.toBe(before.id)
  expect(Math.abs(after.x - before.x), "Arrow Down left the lane").toBeLessThanOrEqual(1)
  expect(after.y, "Arrow Down did not move down the lane").toBeGreaterThan(before.y)
  expect(after.offscreen, "focus landed on a card the reader cannot see").toBe(false)
})

/**
 * A density change after the reader has been working in a lane.
 *
 * Changing tier changes every measured card height and the lane's extent at once, which is exactly when a
 * retained temporary arrangement could leave cards on top of each other. The check is the rendered one: no two
 * drawn cards in the same lane may overlap, and the selection survives.
 */
test("a density change after manual exploration protects every foreground pair", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "dense")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(600)

  // Explore a lane by hand so it is reader-owned, then change density underneath that arrangement.
  const { x: gutterX, y: gutterY } = await gutterPoint(page)
  const band = page.locator(".dtCanvasBand.is-rollable").first()
  const bandBox = (await band.boundingBox())!
  await page.mouse.move(bandBox.x + 4, bandBox.y + bandBox.height / 2)
  await page.mouse.down()
  await page.mouse.move(bandBox.x + 4, bandBox.y + bandBox.height / 2 - 200)
  await page.mouse.up()
  await page.waitForTimeout(300)
  void gutterX
  void gutterY

  const tierBefore = await page.locator(".dtCanvasScene").getAttribute("data-tier")
  await page.getByRole("button", { name: "Zoom out", exact: true }).click()
  await page.getByRole("button", { name: "Zoom out", exact: true }).click()
  await page.waitForTimeout(700)
  const tierAfter = await page.locator(".dtCanvasScene").getAttribute("data-tier")
  expect(tierAfter, "the density tier did not change, so nothing was reconciled").not.toBe(tierBefore)

  const collisionProof = await page.locator(".dtCanvasNode:has(.dtnCard:not(.is-untraced))").evaluateAll(nodes => {
    const byLane = new Map<number, { id: string; top: number; bottom: number }[]>()
    nodes.forEach(node => {
      const element = node as HTMLElement
      const rect = element.getBoundingClientRect()
      const lane = Math.round(rect.left)
      byLane.set(lane, [...(byLane.get(lane) ?? []), { id: element.dataset.nodeId ?? "", top: rect.top, bottom: rect.bottom }])
    })
    const overlaps: string[] = []
    let pairs = 0
    for (const cards of byLane.values()) {
      const sorted = [...cards].sort((a, b) => a.top - b.top)
      for (let index = 1; index < sorted.length; index += 1) {
        pairs++
        if (sorted[index].top < sorted[index - 1].bottom - 1) {
          overlaps.push(`${sorted[index - 1].id}/${sorted[index].id}`)
        }
      }
    }
    return { overlaps, pairs }
  })
  expect(collisionProof.pairs).toBeGreaterThan(0)
  expect(collisionProof.overlaps).toEqual([])
  await expect(root).toHaveAttribute("aria-pressed", "true")
})

/**
 * Scope is identity, content is not.
 *
 * The fixture mounts the same content under two navigation scopes plus a content-only refresh. A genuine scope
 * change must start a new navigation context (the reader's lane position is not carried into a different
 * project/build), while an equivalent refresh inside the same scope must keep it. Reloading the page could
 * demonstrate neither, because it resets everything regardless.
 */
test("a scope change resets navigation while a same-scope refresh keeps it", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto("/tests/fixtures/change-network.html?case=scope")
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(800)

  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(600)

  // Scroll the first lane by hand so the reader has a position worth preserving or discarding.
  const band = page.locator(".dtCanvasBand.is-rollable").first()
  const bandBox = (await band.boundingBox())!
  // The probe must live in the lane being scrolled: the first card in the document belongs to lane 0, which this
  // gesture does not touch, so measuring it would report zero movement no matter what the scope did.
  const probeId = await page.evaluate(bandRect => {
    const inside = [...document.querySelectorAll<HTMLElement>(".dtCanvasNode")].find(node => {
      const rect = node.getBoundingClientRect()
      return rect.left >= bandRect.x - 2 && rect.right <= bandRect.x + bandRect.width + 2 &&
        rect.top > bandRect.y + 4 && rect.bottom < bandRect.y + bandRect.height - 4
    })
    return inside?.dataset.nodeId ?? null
  }, { x: bandBox.x, y: bandBox.y, width: bandBox.width, height: bandBox.height })
  expect(probeId, "no card belongs to the lane being scrolled").toBeTruthy()
  const probe = page.locator(`[data-node-id="${probeId}"]`)
  await page.mouse.move(bandBox.x + 4, bandBox.y + bandBox.height / 2)
  await page.mouse.down()
  await page.mouse.move(bandBox.x + 4, bandBox.y + bandBox.height / 2 - 200)
  await page.mouse.up()
  await page.waitForTimeout(400)

  const laneY = async () => Number(
    /translate\([^,]+,\s*(-?[\d.]+)px\)/.exec((await probe.getAttribute("style")) ?? "")?.[1] ?? NaN,
  )
  const explored = await laneY()

  // Same scope, equivalent content: the reader's position survives.
  await page.locator("#content-refresh").click()
  await page.waitForTimeout(500)
  expect(
    Math.abs((await laneY()) - explored),
    "a same-scope content refresh discarded the reader's position",
  ).toBeLessThanOrEqual(4)

  // Genuine scope change: a new navigation context, so the previous scope's position is not inherited.
  await page.locator("#scope-flip").click()
  await page.waitForTimeout(900)
  expect(
    Math.abs((await laneY()) - explored),
    "a scope change inherited the previous scope's navigation state",
  ).toBeGreaterThan(4)
})

/**
 * The positive first-exposure case: a hidden lane whose endpoint is far below the window while usable space
 * above it is empty.
 *
 * This is the branch the dense fixtures cannot produce — with contiguous rows a lane's window is full whenever
 * the lane is longer than the window. The contract fixture spaces its rows deliberately (allowed by `CanvasNode`,
 * not emitted by any production adapter), so the reveal has real room to use. The reader pans to the lane and
 * the endpoint must be readable there without a second vertical action or a Show click, with the camera moved
 * only by their own gesture.
 */
test("a hidden lane's endpoint arrives at a useful height on first exposure", async ({ page }) => {
  // The fixture is six lanes wide; at its legible landing zoom the right-most lane starts outside the viewport,
  // which is what makes this a first-exposure case rather than a same-view reveal.
  await page.setViewportSize({ width: 1100, height: 900 })
  await page.goto("/tests/fixtures/digital-thread-contract.html")
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(700)

  const canvasBox = (await page.locator(".dtCanvas").boundingBox())!
  const linked = page.locator('[data-node-id="link"]')
  const subject = page.locator('[data-node-id="subj"]')
  const { top: usableTop, bottom: usableBottom } = await usableFrame(page)

  const startBox = await linked.boundingBox()
  const startsOutside = !startBox || startBox.y + startBox.height <= usableTop || startBox.y >= usableBottom ||
    (await linked.getAttribute("class"))?.includes("is-offscreen") === true
  expect(startsOutside, "the contract fixture did not start with the endpoint out of view").toBe(true)

  // The wide board lands centred, so the subject's own lane can start off-screen left; the reader pans to it
  // first (their navigation), which is also what keeps this test honest about who moves the camera.
  await panBackground(page, 700)

  /**
   * Select first, then pan.
   *
   * Moving the pointer to pan ends a hover, which retires the temporary arrangement — so this is the selected
   * exploration case the contract describes, not the hover case: the selection owns the reveal while the reader
   * pans to a lane the camera was not showing.
   */
  await subject.click({ position: { x: 6, y: 6 } })
  await expect(subject).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(700)

  // The reader pans to the lane. The endpoint must be readable where it arrives.
  await panBackground(page, -900)
  await expect(linked, "the endpoint was not readable on first exposure").not.toHaveClass(/is-offscreen/)
  const arrived = (await linked.boundingBox())!
  expect(arrived.y).toBeGreaterThanOrEqual(usableTop - 1)
  expect(arrived.y + arrived.height).toBeLessThanOrEqual(usableBottom + 1)
  expect(arrived.x).toBeGreaterThanOrEqual(canvasBox.x - 1)
  expect(arrived.x + arrived.width).toBeLessThanOrEqual(canvasBox.x + canvasBox.width + 1)
  // No Show click was needed: the explicit-action strip is not the path this proof uses.
  await expect(page.getByRole("button", { name: "Show link", exact: true })).toHaveCount(0)
})

test("four-direction continuation follows the usable boundary during selected exploration", async ({ page }) => {
  await page.setViewportSize({ width: 1100, height: 900 })
  await page.goto("/tests/fixtures/digital-thread-contract.html")
  const source = page.locator('[data-node-id="subj"]')
  await expect(source).toBeVisible()
  await page.waitForTimeout(700)
  await panBackground(page, 700)
  await source.click({ position: { x: 6, y: 6 } })
  await page.waitForTimeout(900)
  const assertCue = async (direction: string) => {
    const cue = page.locator(`.dtCanvasContinuation[data-dir="${direction}"]:not([hidden])`).first()
    await expect(cue).toBeVisible()
    const rect = (await cue.boundingBox())!
    const { canvasBox, top, bottom } = await usableFrame(page)
    expect(rect.y).toBeGreaterThanOrEqual(top - 1)
    expect(rect.y + rect.height).toBeLessThanOrEqual(bottom + 1)
    expect(rect.x).toBeGreaterThanOrEqual(canvasBox.x - 1)
    expect(rect.x + rect.width).toBeLessThanOrEqual(canvasBox.x + canvasBox.width + 1)
    if (direction === "up") expect(Math.abs(rect.y - top)).toBeLessThan(12)
    if (direction === "down") expect(Math.abs(rect.y + rect.height - bottom)).toBeLessThan(4)
    if (direction === "left") expect(rect.x - canvasBox.x).toBeLessThan(20)
    if (direction === "right") expect(canvasBox.x + canvasBox.width - rect.x - rect.width).toBeLessThan(20)
    await expect(source).toHaveAttribute("aria-pressed", "true")
    await shoot(page, `continuation-${direction}`)
  }
  expect((await page.locator('[data-node-id="link"]').boundingBox())!.x).toBeGreaterThan(1100)
  await assertCue("right")
  await panBackground(page, -1000)
  expect((await source.boundingBox())!.x + (await source.boundingBox())!.width).toBeLessThan(0)
  await assertCue("left")
  await panBackground(page, 0, 500)
  await assertCue("down")
  await panBackground(page, 0, -850)
  const remaining = (await page.locator('[data-node-id="link"]').boundingBox())!
  const { top: boundary } = await usableFrame(page)
  if (remaining.y + remaining.height >= boundary) {
    await panBackground(page, 0, boundary - remaining.y - remaining.height - 40)
  }
  await assertCue("up")
})

test("pointer identity, cancellation and unmount clean up the active gesture", async ({ page }) => {
  await page.goto("/tests/fixtures/digital-thread-contract.html?case=growth")
  const subject = page.locator('[data-node-id="subj"]')
  await expect(subject).toBeVisible()
  await subject.click({ position: { x: 6, y: 6 } })
  await expect(subject).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(900)
  const canvas = page.locator(".dtCanvas")
  const before = await transformOf(page.locator(".dtCanvasScene"))
  await canvas.dispatchEvent("pointerdown", { pointerId: 41, button: 0, clientX: 20, clientY: 300 })
  await canvas.dispatchEvent("pointermove", { pointerId: 42, clientX: 150, clientY: 400 })
  await canvas.dispatchEvent("pointerup", { pointerId: 42, clientX: 150, clientY: 400 })
  expect(await transformOf(page.locator(".dtCanvasScene"))).toBe(before)
  await expect(subject).toHaveAttribute("aria-pressed", "true")
  await canvas.dispatchEvent("pointercancel", { pointerId: 41 })
  await expect(subject).toHaveAttribute("aria-pressed", "true")
  await expect(canvas).not.toHaveClass(/is-panning|is-rolling|is-idle/)
  await canvas.dispatchEvent("pointerdown", { pointerId: 43, button: 0, clientX: 20, clientY: 300 })
  await page.getByRole("button", { name: "Toggle canvas" }).dispatchEvent("click")
  await expect(canvas).toHaveCount(0)
  await page.evaluate(() => {
    window.dispatchEvent(new PointerEvent("pointermove", { pointerId: 43, clientX: 250, clientY: 350 }))
    window.dispatchEvent(new PointerEvent("pointerup", { pointerId: 43, clientX: 250, clientY: 350 }))
  })
  await page.getByRole("button", { name: "Toggle canvas" }).click()
  await expect(subject).toHaveAttribute("aria-pressed", "true")
  // The stale pointer-up did not manufacture a clear after unmount.
  await page.waitForTimeout(900)
  await expect(subject).toHaveAttribute("aria-pressed", "true")
})

test("actual click framing interpolates all matrix axes with coherent edges and hit targets", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "dense")
  for (let i = 0; i < 3; i++) await page.getByRole("button", { name: "Zoom out", exact: true }).click()
  const root = page.locator('[data-node-id="pr-5"]')
  await page.evaluate(() => {
    type Sample = { t: number; axes: number[]; selected: boolean; hit: boolean; edgeError: number | null }
    const state = window as unknown as { clickSamples: Sample[] }
    state.clickSamples = []
    const start = performance.now()
    const sample = () => {
      const scene = document.querySelector<HTMLElement>(".dtCanvasScene")!
      const matrix = new DOMMatrixReadOnly(getComputedStyle(scene).transform)
      const card = document.querySelector<HTMLElement>('[data-node-id="pr-5"]')!
      const rect = card.getBoundingClientRect()
      const path = document.querySelector<SVGPathElement>(".dtCanvasEdge.is-traced")
      const point = path?.getPointAtLength(0)
      const edge = point && path ? new DOMPoint(point.x, point.y).matrixTransform(path.getScreenCTM()!) : null
      state.clickSamples.push({ t: performance.now() - start, axes: [matrix.a, matrix.b, matrix.c, matrix.d, matrix.e, matrix.f],
        selected: card.getAttribute("aria-pressed") === "true",
        hit: card.contains(document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2)),
        edgeError: edge ? Math.abs(edge.x - rect.right) : null })
      if (performance.now() - start < 1300) requestAnimationFrame(sample)
    }
    sample()
  })
  await root.click({ position: { x: 8, y: 8 } })
  await page.waitForTimeout(1400)
  const samples = await page.evaluate(() => (window as unknown as {
    clickSamples: { t: number; axes: number[]; selected: boolean; hit: boolean; edgeError: number | null }[]
  }).clickSamples)
  await testInfo.attach("displayed-click-matrices", { body: JSON.stringify(samples, null, 2), contentType: "application/json" })
  const initial = samples[0].axes[0]
  const final = samples.at(-1)!.axes[0]
  expect(final - initial, "click must actually trigger automatic framing").toBeGreaterThan(.15)
  const moving = samples.filter(sample => sample.axes[0] > initial + .001 && sample.axes[0] < final - .001)
  expect(moving.length).toBeGreaterThan(15)
  expect(moving.at(-1)!.t - moving[0].t, "the displayed click transition must be slower than the prior .4s path").toBeGreaterThan(550)
  for (let i = 1; i < samples.length; i++) {
    const current = samples[i]
    const prior = samples[i - 1]
    expect(current.axes.every(Number.isFinite)).toBe(true)
    expect(Math.abs(current.axes[0] - current.axes[3])).toBeLessThan(.0001)
    expect(Math.abs(current.axes[1]) + Math.abs(current.axes[2])).toBeLessThan(.0001)
    expect(current.axes[0]).toBeGreaterThanOrEqual(prior.axes[0] - .0001)
    expect(current.axes[0] - prior.axes[0], "no one-frame jump to the target").toBeLessThan(.06)
    if (current.selected) {
      expect(current.hit, "native card hit target follows the displayed card").toBe(true)
      if (current.edgeError !== null) expect(current.edgeError).toBeLessThan(2)
    }
  }
  await expect(root).toHaveAttribute("aria-pressed", "true")
})

test("manual input interrupts real click framing from its displayed position", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await open(page, "dense")
  for (let i = 0; i < 3; i++) await page.getByRole("button", { name: "Zoom out", exact: true }).click()
  const root = page.locator('[data-node-id="pr-5"]')
  const axes = () => page.locator(".dtCanvasScene").evaluate(element => {
    const m = new DOMMatrixReadOnly(getComputedStyle(element).transform)
    return [m.a, m.b, m.c, m.d, m.e, m.f]
  })
  await root.click({ position: { x: 8, y: 8 } })
  await expect.poll(async () => {
    const first = await axes()
    await page.waitForTimeout(50)
    const second = await axes()
    return second[0] - first[0] > .005
  }).toBe(true)
  const canvas = (await page.locator(".dtCanvas").boundingBox())!
  // Above the lane headings, to the right of the toolbar: a stable background point throughout zoom.
  const x = canvas.x + canvas.width - 8
  const y = canvas.y + 65
  expect(await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.closest(".dtCanvasBand, [data-node-id], button") === null, { x, y })).toBe(true)
  await page.mouse.move(x, y)
  await page.mouse.down()
  const pressed = await axes()
  await page.waitForTimeout(350)
  const held = await axes()
  for (let i = 0; i < 6; i++) expect(Math.abs(held[i] - pressed[i])).toBeLessThan(i < 4 ? .002 : 1)
  await page.mouse.move(x - 150, y)
  await page.mouse.up()
  const released = await axes()
  expect(Math.abs(released[4] - pressed[4] + 150)).toBeLessThan(2)
  await page.waitForTimeout(900)
  const delayed = await axes()
  for (let i = 0; i < 6; i++) expect(Math.abs(delayed[i] - released[i])).toBeLessThan(i < 4 ? .002 : 1)
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await testInfo.attach("click-motion-takeover", { body: JSON.stringify({ pressed, held, released, delayed }, null, 2), contentType: "application/json" })
})
