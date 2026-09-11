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
