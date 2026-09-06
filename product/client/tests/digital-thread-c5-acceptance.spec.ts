import { expect, test, type Page } from "@playwright/test"
import { V5_FIXTURE_IDS as ids } from "./fixtures/digital-thread-v5"

const open = async (page: Page, view: string, width = 1440, density = "comfortable") => {
  await page.setViewportSize({ width, height: 900 })
  await page.emulateMedia({ reducedMotion: "reduce" })
  await page.goto(`/tests/fixtures/digital-thread-c5.html?view=${view}&density=${density}`)
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await expect(page.locator(".dtCanvasScene")).toHaveAttribute("data-tier", /[012]/)
}

// Compare rendered rectangles, including dimmed context cards. Testing only highlighted cards missed the
// original verified-by label painted underneath a sibling. Off-viewport labels are assessed when reachable.
async function collisions(page: Page) {
  return page.locator(".dtCanvas").evaluate(canvas => {
    const overlaps = (a: DOMRect, b: DOMRect) =>
      Math.min(a.right, b.right) - Math.max(a.left, b.left) > 1 &&
      Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 1
    const visible = (element: Element) => {
      for (let parent: Element | null = element; parent; parent = parent.parentElement) {
        const style = getComputedStyle(parent)
        if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity) === 0) return false
      }
      const rect = element.getBoundingClientRect()
      return rect.width > 0 && rect.height > 0
    }
    const viewport = canvas.getBoundingClientRect()
    const labels = [...canvas.querySelectorAll(".dtCanvasEdgeLabel")].filter(element =>
      visible(element) && overlaps(element.getBoundingClientRect(), viewport))
    const obstacles = [...document.querySelectorAll(
      ".dtCanvasNode, .dtCanvasLaneHead, .dtCanvasControls, .dtnPanel, .dtaPanel, .dticPanel",
    )].filter(visible)
    const failures: string[] = []
    labels.forEach((label, index) => {
      const rect = label.getBoundingClientRect()
      for (const obstacle of obstacles) {
        if (overlaps(rect, obstacle.getBoundingClientRect())) failures.push(
          `${label.textContent} overlaps ${obstacle.className}: ${obstacle.textContent?.trim().slice(0, 65)}`,
        )
      }
      for (const other of labels.slice(index + 1)) {
        if (overlaps(rect, other.getBoundingClientRect())) failures.push(`${label.textContent} overlaps label ${other.textContent}`)
      }
    })
    return failures
  })
}

async function controlsDoNotOverlap(page: Page) {
  const failures = () => page.locator("body").evaluate(() => {
    const overlaps = (a: DOMRect, b: DOMRect) =>
      Math.min(a.right, b.right) - Math.max(a.left, b.left) > 1 &&
      Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 1
    const visible = (element: Element) => element.getBoundingClientRect().width > 0 && getComputedStyle(element).visibility !== "hidden"
    const failures: string[] = []
    for (const panel of document.querySelectorAll(".dtnPanel, .dtaPanel, .dticPanel")) {
      const controls = [...panel.querySelectorAll("button, a")].filter(visible)
      controls.forEach((control, i) => controls.slice(i + 1).forEach(other => {
        if (overlaps(control.getBoundingClientRect(), other.getBoundingClientRect())) failures.push(`${control.textContent} / ${other.textContent}`)
      }))
      // Lists may scroll independently, but their viewport must fit inside the bottom inspector. Moving its
      // tools into a new row must not simply move the defect into clipped list content below the panel edge.
      if (panel.matches(".dtnPanel-bottom, .dtaPanel-bottom, .dticPanel-bottom")) {
        const bounds = panel.getBoundingClientRect()
        for (const list of panel.querySelectorAll(".dtnRel, .dtaRel, .dticRel")) {
          if (visible(list) && list.getBoundingClientRect().bottom > bounds.bottom - 1) failures.push(`${list.className} extends below bottom inspector`)
        }
      }
    }
    const toolbar = document.querySelector(".dtCanvasControls")!.getBoundingClientRect()
    for (const head of document.querySelectorAll(".dtCanvasLaneHead")) {
      if (visible(head) && overlaps(toolbar, head.getBoundingClientRect())) failures.push(
        `Canvas controls bottom ${toolbar.bottom} / ${head.textContent} top ${head.getBoundingClientRect().top}`,
      )
    }
    return failures
  })
  await expect.poll(failures).toEqual([])
}

for (const density of ["comfortable", "compact"]) {
  for (const width of [1280, 1440, 1920]) {
    test(`branching network labels stay readable at ${width} ${density}`, async ({ page }, testInfo) => {
      await open(page, "network", width, density)
      const panel = page.locator(".dtnPanel")
      await expect(panel).toContainText("HLRCR-925.00")
      await page.getByRole("button", { name: "Fit entire story", exact: true }).click()
      for (const phrase of ["resolved by", "allocates to", "verified by"]) {
        const label = page.locator(".dtCanvasEdgeLabel").filter({ hasText: new RegExp(`^${phrase}$`) }).first()
        await expect(label).toBeVisible()
        await expect(label).toBeInViewport({ ratio: 1 })
      }
      await expect.poll(() => collisions(page)).toEqual([])
      await controlsDoNotOverlap(page)
      await expect(panel).toContainText("PR-925.00")
      await expect(panel).toContainText("LLRCR-925.02")
      await expect(panel).not.toContainText("HLRCR-926.00")
      if (width === 1440) await testInfo.attach(`network-${density}`, { body: await page.screenshot(), contentType: "image/png" })
    })
  }
}

test("artifact full story retains both verification branches and excludes the sibling", async ({ page }, testInfo) => {
  await open(page, "artifact", 1920)
  const panel = page.locator(".dtaPanel")
  await expect(panel).toContainText("PR-925.00")
  for (const identity of ["LLRTP-925.01", "LLRTP-925.02", "FMS-925.0"]) await expect(panel).toContainText(identity)
  await expect(panel).not.toContainText("HLR-926.01")
  const cardOverlaps = () => page.locator(".dtCanvasNode:not(.is-offscreen)").evaluateAll(nodes => {
    const collisions: string[] = []
    nodes.forEach((node, i) => nodes.slice(i + 1).forEach(other => {
      const a = node.getBoundingClientRect()
      const b = other.getBoundingClientRect()
      if (Math.min(a.right, b.right) - Math.max(a.left, b.left) > 1 &&
          Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 1) {
        collisions.push(`${node.textContent?.slice(0, 45)} overlaps ${other.textContent?.slice(0, 45)}`)
      }
    }))
    return collisions
  })
  await expect.poll(cardOverlaps).toEqual([])
  // At a wide viewport the arrival may already fit the entire story. Move the camera first so a broken Fit
  // handler cannot pass simply because its starting transform happens to be the desired one.
  await page.getByRole("button", { name: "Zoom in", exact: true }).click()
  await page.getByRole("button", { name: "Zoom in", exact: true }).click()
  const beforeFit = await page.locator(".dtCanvasScene").getAttribute("style")
  await page.getByRole("button", { name: "Fit entire story", exact: true }).click()
  await expect(page.locator(".dtCanvasScene")).not.toHaveAttribute("style", beforeFit!)
  const verificationLabels = page.locator(".dtCanvasEdgeLabel").filter({ hasText: /^verified by$/ })
  await expect(verificationLabels).toHaveCount(2)
  for (const label of await verificationLabels.all()) {
    await expect(label).toBeVisible()
    await expect(label).toBeInViewport({ ratio: 1 })
  }
  await expect(page.locator(".dtCanvasPlacementNotice")).toBeHidden()
  await expect.poll(() => collisions(page)).toEqual([])
  const fitted = await page.locator(".dtCanvasScene").getAttribute("style")
  await page.locator(`[data-node-id="${ids.hlr}"]`).hover()
  await page.getByText("Synthetic C5 acceptance fixture", { exact: true }).hover()
  await expect(page.locator(".dtCanvasScene")).toHaveAttribute("style", fitted!)
  await testInfo.attach("artifact-branching-story", { body: await page.screenshot(), contentType: "image/png" })
})

test("clearing and reselecting the arrival focal uses selection framing instead of replaying landing", async ({ page }) => {
  await open(page, "network", 1280)
  const scale = page.getByLabel("Current canvas scale")
  await expect(scale).toHaveText("86% · Detailed")
  const focal = page.locator(`[data-node-id="${ids.hlr}"]`)
  await focal.press("Enter")
  await expect(page.locator(".dtnPanel")).toHaveCount(0)
  await focal.press("Enter")
  await expect(page.locator(".dtnPanel")).toBeVisible()
  await expect(scale).toHaveText("81% · Compact")
  await expect(page.locator(".dtCanvasScene")).toHaveAttribute("data-tier", "1")
})

for (const view of ["network", "artifact", "inside"]) {
  test(`${view} inspector dock controls remain separate from actions and content`, async ({ page }, testInfo) => {
    await open(page, view, 1280, "compact")
    if (view === "inside") await page.locator(".dtCanvasNode").filter({ hasText: "SYSR-00076.02" }).click()
    const panel = page.locator(".dtnPanel, .dtaPanel, .dticPanel")
    await expect(panel).toBeVisible()
    for (const dock of ["Bottom", "Right", "Auto"]) {
      await panel.getByRole("button", { name: dock, exact: true }).click()
      await controlsDoNotOverlap(page)
      await expect.poll(() => collisions(page)).toEqual([])
      if (dock === "Bottom") await testInfo.attach(`${view}-bottom-compact`, { body: await page.screenshot(), contentType: "image/png" })
    }
    if (view === "network") {
      await panel.getByRole("button", { name: "Open this change", exact: true }).click()
      await expect(page.getByLabel("Open change activations")).toHaveText("1")
    }
    expect(await page.locator(".dtCanvas").evaluate(element => element.scrollTop)).toBe(0)
  })
}

test("Inside identifier search preserves the opened record and truthful no-match across Map and Table", async ({ page }) => {
  await open(page, "inside")
  const search = page.getByRole("searchbox", { name: "Find an identifier inside this change", exact: true })
  await search.fill("SRCR-000000000031.00")
  await expect(page.getByText(/No records match/)).toHaveCount(0)
  const identity = page.locator(".dtCanvasNode").getByRole("link", { name: "SRCR-000000000031.00", exact: true })
  await expect(identity).toBeVisible()
  expect((await identity.boundingBox())!.width).toBeGreaterThan(100)
  expect(await identity.evaluate(element => {
    const range = document.createRange()
    range.selectNodeContents(element)
    return range.getBoundingClientRect().width <= element.getBoundingClientRect().width + 1
  })).toBe(true)
  // This word occurs in the statement, but this control promises identifier search.
  await search.fill("sequence")
  await expect(page.getByText(/No records match/).first()).toBeVisible()
  await expect(identity).toBeVisible()
  await page.getByRole("button", { name: "Fixture Table", exact: true }).click()
  await expect(page.getByRole("table")).toContainText("SRCR-000000000031.00")
  await expect(page.getByText(/No records match/).last()).toBeVisible()
  await page.getByRole("button", { name: "Fixture Map", exact: true }).click()
  await search.fill("")
  await expect(page.getByText(/No records match/)).toHaveCount(0)
  await expect(page.locator(".dtCanvasNode").filter({ hasText: "SYSR-00151.00" })).toBeVisible()
})

test("Inside preserves explicit missing-base and target states alongside known before and after text", async ({ page }) => {
  await open(page, "inside", 1920)
  const newCard = page.locator(".dtCanvasNode").filter({ hasText: "SYSR-00151.00" })
  await newCard.click()
  await expect(page.locator(".dticPanel")).toContainText("Target not yet created")
  await expect(newCard.locator(".dticOp")).toHaveText("NEW")
  const unresolved = page.locator(".dtCanvasNode").filter({ hasText: "SYSR-00075.02" })
  await newCard.press("ArrowDown")
  await expect(unresolved).toBeFocused()
  await unresolved.press("Enter")
  await expect(page.locator(".dticPanel")).toContainText("Base revision unresolved")
  await expect(unresolved.locator("del, ins")).toHaveCount(0)
  const known = page.locator(".dtCanvasNode").filter({ hasText: "SYSR-00076.02" })
  await unresolved.press("ArrowDown")
  await expect(known).toBeFocused()
  await known.press("Enter")
  await expect(known.locator("del")).toHaveText("The FMS shall sequence the entered route.")
  await expect(known.locator("ins")).toHaveText("The FMS shall sequence the selected route.")
  await expect(known.locator(".dticOp")).toHaveText("MOD")
  const retired = page.locator(".dtCanvasNode").filter({ hasText: "SYSR-00077.01" })
  // A sibling may roll outside the lane when the selected Modify expands. Use the supported native keyboard
  // path to reveal it, rather than assuming every untraced sibling remains concurrently pointer-visible.
  await known.press("ArrowDown")
  await expect(retired).toBeFocused()
  await retired.press("Enter")
  await expect(retired.locator(".dticOp")).toHaveText("RET")
  await expect(page.locator(".dticPanel")).toContainText("Retire")
})
