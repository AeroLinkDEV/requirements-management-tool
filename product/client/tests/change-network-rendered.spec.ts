import { expect, test, type Page } from "@playwright/test"

/**
 * Rendered behaviour of the change network.
 *
 * Added while fixing #905 inside the slice 5B PR. Until now this view had pure-logic coverage only, so a defect
 * that is entirely about rendered geometry had nowhere to be caught: `badgeOf` returns three-letter badges and
 * `.dtnBadge` fixed the box to 20px, so the letters overflowed and landed on the identifier beside them. No
 * presentation spec can see that — it needs a browser and a box measurement.
 *
 * The fixture is test-only. Page integration remains slice-6 work.
 */

const settled = async (page: Page) => {
  await expect(page.locator(".dtCanvas")).toBeVisible()
  await page.waitForTimeout(700)
}

test.describe("card badges", () => {
  test("every badge fits its own box, including the three-letter ones", async ({ page }) => {
    await page.goto("/tests/fixtures/change-network.html")
    await settled(page)

    const badges = await page.locator(".dtnBadge").evaluateAll(elements =>
      elements.map(element => {
        const badge = element as HTMLElement
        return {
          text: badge.textContent ?? "",
          // `scrollWidth` exceeding the laid-out width is precisely the overflow: the text is wider than the
          // box, and with `place-items: center` and visible overflow it spills out on both sides.
          clientWidth: badge.clientWidth,
          scrollWidth: badge.scrollWidth,
        }
      }),
    )

    expect(badges.length).toBeGreaterThan(0)
    // Every badge `badgeOf` can return is on this board, so none of them may be skipped by the fixture.
    const rendered = badges.map(badge => badge.text).sort()
    expect(rendered).toEqual(["CUS", "HLR", "IFC", "LLR", "PR", "SYS", "TCR"])

    for (const badge of badges) {
      expect(badge.scrollWidth, `${badge.text} overflows its box`).toBeLessThanOrEqual(badge.clientWidth)
    }
  })

  test("a badge never overlaps the identifier beside it", async ({ page }) => {
    await page.goto("/tests/fixtures/change-network.html")
    await settled(page)

    const overlaps = await page.locator(".dtnCardTop").evaluateAll(rows =>
      rows
        .map(row => {
          const badge = row.querySelector(".dtnBadge")?.getBoundingClientRect()
          const id = row.querySelector(".dtnId")?.getBoundingClientRect()
          if (!badge || !id) return null
          // A one-pixel tolerance for sub-pixel layout; anything beyond that is the collision.
          return badge.right > id.left + 1 ? `${row.textContent}` : null
        })
        .filter(Boolean),
    )

    expect(overlaps).toEqual([])
  })
})

/**
 * #880 §6.6 is a shared-canvas guarantee, not an artifact-thread one, and the canonical prototype's
 * `checks.js` exercises it in network mode too: for every dock, the selected record and **every direct link**
 * must be inside the panel-free frame.
 *
 * It needs asserting here in its own right. Since §10.1 stopped the board zooming out past the legibility
 * floor, a side dock can no longer always leave room — and the correct answer is that the panel moves, never
 * that a linked record is hidden. Wiring that fail-safe into one view and not the others is exactly how the
 * defect survived, so each view that renders the panel proves it.
 */
test.describe("the detail panel never rests on a directly linked record", () => {
  for (const mode of ["Bottom", "Right", "Auto"]) {
    test(`with the panel docked ${mode}`, async ({ page }) => {
      test.setTimeout(120_000)
      await page.goto("/tests/fixtures/change-network.html")
      await settled(page)

      // Select a card the board is actually drawing, and one that has direct links to check — a card with
      // none would let this pass without exercising anything.
      const drawn = page.locator(`.dtCanvasNode:not(.is-offscreen):has(.dtnCard)`)
      const total = await drawn.count()
      let chosen = 0
      for (; chosen < Math.min(total, 8); chosen += 1) {
        await drawn.nth(chosen).click()
        await page.waitForTimeout(500)
        if (await page.locator(".dtnRel button:not(.is-far)").count()) break
      }
      expect(chosen, "no drawn card had a direct link to check").toBeLessThan(Math.min(total, 8))

      await page.locator(`.dtnPanelTools button:text-is("${mode}")`).click()
      await page.waitForTimeout(700)

      const panel = (await page.locator(".dtnPanel").boundingBox())!
      const canvas = (await page.locator(".dtCanvas").boundingBox())!
      const names = await page.locator(".dtnRel button:not(.is-far) > span > span").allInnerTexts()
      expect(names.length, "the selected record should have direct links to check").toBeGreaterThan(0)

      for (const name of names) {
        const card = page.locator(`.dtCanvasNode:has(.dtnCard:has-text("${name}"))`).first()
        expect(await card.count(), `${name} is a direct link and must be on the board`).toBeGreaterThan(0)
        // Absent or faded is not "clear of the panel": it is the same failure by another route.
        await expect(card, `${name} is hidden rather than fitted beside the ${mode} panel`)
          .not.toHaveClass(/is-offscreen/)

        const box = (await card.boundingBox())!
        const clear =
          box.x + box.width <= panel.x + 1 ||
          box.x >= panel.x + panel.width - 1 ||
          box.y + box.height <= panel.y + 1 ||
          box.y >= panel.y + panel.height - 1
        expect(clear, `${name} is underneath the ${mode} panel`).toBe(true)
        expect(box.x).toBeGreaterThanOrEqual(canvas.x - 1)
        expect(box.x + box.width).toBeLessThanOrEqual(canvas.x + canvas.width + 1)
      }
    })
  }
})
