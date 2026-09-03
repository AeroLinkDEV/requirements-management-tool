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
