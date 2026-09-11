import { expect, renderedTest as test } from "./isolated-client-test"

/**
 * Core #1022 interaction, in the real shared canvas.
 *
 * Hover is stationary emphasis; selection is persistent and owns the thread; the floating preview target is
 * gone; only out-of-view linked cards are displaced, in their own lane. These run in the retained fast lane
 * beside the geometry spec, and the deeper view-by-view journeys stay in their own files.
 */

const open = async (page: import("@playwright/test").Page, scenario: string) => {
  await page.goto(`/tests/fixtures/change-network.html?case=${scenario}`)
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(700)
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
  await expect(page.locator(".dtCanvasHoverTarget")).toHaveCount(0)
  // Pointing at another reachable card, and waiting past the old dwell, must not replace the thread.
  const other = page.locator('.dtCanvasNode:not(.is-offscreen)').nth(4)
  await other.hover()
  await page.waitForTimeout(650)
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await expect(page.locator(".dtCanvasNode.is-selected")).toHaveAttribute("data-node-id", "pr-5")
})

test("a dense thread keeps every record reachable through its explicit reveal action", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.emulateMedia({ reducedMotion: "reduce" })
  await open(page, "dense")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.focus()
  await root.press("Enter")
  await expect(root).toHaveAttribute("aria-pressed", "true")
  const reveal = page.getByRole("navigation", { name: "Connected records outside view" })
    .locator("button:not([hidden])").last()
  await expect(reveal).toBeVisible()
  const identifier = (await reveal.textContent())!.replace(/^Show /, "")
  await reveal.click()
  await expect(page.locator(".dtCanvasNode").filter({ has: page.locator(".dtnId", { hasText: identifier }) }))
    .not.toHaveClass(/is-offscreen/)
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
  await open(page, "dense")
  const root = page.locator('[data-node-id="pr-5"]')
  await root.click()
  await expect(root).toHaveAttribute("aria-pressed", "true")
  await page.waitForTimeout(900)

  const band = page.locator(".dtCanvasBand.is-rollable").first()
  await expect(band).toBeVisible()
  const bandBox = (await band.boundingBox())!
  const canvasBox = (await page.locator(".dtCanvas").boundingBox())!
  const grabX = bandBox.x + 4
  const top = Math.max(bandBox.y, canvasBox.y) + 12
  const bottom = Math.min(bandBox.y + bandBox.height, canvasBox.y + canvasBox.height) - 12
  expect(bottom - top).toBeGreaterThan(80)

  // A card in the lane being rolled, so its movement measures that lane (not the camera).
  const probe = page.locator(".dtCanvasNode").filter({ has: page.locator(".dtnCard") }).nth(1)
  const laneOfProbe = await probe.evaluate(node =>
    Number(/translate\((-?[\d.]+)px/.exec((node as HTMLElement).style.transform)?.[1] ?? NaN))
  const sameLane = page.locator(".dtCanvasNode").filter({ has: page.locator(".dtnCard") }).filter({
    hasNot: page.locator("nothing"),
  })
  const yOfLane = () => sameLane.evaluateAll((nodes, laneX) => {
    const inLane = nodes.filter(node => {
      const x = Number(/translate\((-?[\d.]+)px/.exec((node as HTMLElement).style.transform)?.[1] ?? NaN)
      return Math.abs(x - laneX) <= 1
    })
    return inLane.reduce((sum, node) => {
      const y = Number(/translate\([^,]+,\s*(-?[\d.]+)px\)/.exec((node as HTMLElement).style.transform)?.[1] ?? NaN)
      return sum + (Number.isFinite(y) ? y : 0)
    }, 0) / Math.max(1, inLane.length)
  }, laneOfProbe)

  const before = await yOfLane()
  // The camera is the transform. The scene's width/height legitimately change with the tray's reserved
  // space, so comparing the whole style attribute would confuse layout space with camera movement.
  const transformOf = async () =>
    /transform:[^;]*/.exec((await page.locator(".dtCanvasScene").getAttribute("style")) ?? "")?.[0] ?? ""
  const cameraBefore = await transformOf()
  await page.mouse.move(grabX, (top + bottom) / 2)
  await page.mouse.down()
  await page.mouse.move(grabX, (top + bottom) / 2 - 140, { steps: 8 })
  await page.mouse.up()
  await page.waitForTimeout(300)
  const scrolled = await yOfLane()
  expect(Math.abs(scrolled - before), "the lane did not scroll").toBeGreaterThan(20)
  // Scrolling a lane is not a camera move.
  expect(await transformOf()).toBe(cameraBefore)

  // Clearing removes the temporary reveal but must leave the reader's lane position and camera where they are.
  await page.locator(".dtCanvas").click({ position: { x: 8, y: 8 } })
  await page.waitForTimeout(700)
  expect(await transformOf()).toBe(cameraBefore)
  const cleared = await yOfLane()
  expect(Math.abs(cleared - scrolled), "the lane snapped after clear").toBeLessThanOrEqual(4)
})
