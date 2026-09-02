import { expect, test } from "@playwright/test"

/**
 * Rendered behaviour of the inside-a-change view.
 *
 * These drive the real component in a real browser through a test-only fixture, because the pure-logic specs
 * beside them cannot prove what a reader sees: that a card carries a class, that a hop badge appears, that an
 * error leaves the canvas mounted, or that a populated field survived to the DOM. The fixture is test-only —
 * no production route is added, and page integration remains slice-6 work.
 */

const fixture = (scenario: string) => `/tests/fixtures/inside-change.html?case=${scenario}`

test.describe("requirement proposals", () => {
  test("a Modify shows its badge and a truthful before/after", async ({ page }) => {
    await page.goto(fixture("requirement"))
    const card = page.locator('.dticProposal:has-text("SR-00010.01")')
    await expect(card).toBeVisible()

    await expect(card.locator(".dticOp-modify")).toHaveText("MOD")
    // The before text is the exact superseded statement, and the after is what the proposal says.
    await expect(card.locator(".dticDiff del")).toContainText("in the order entered")
    await expect(card.locator(".dticDiff ins")).toContainText("round-robin")
  })

  test("a behind-target item states both revisions and claims nothing about the newer one", async ({ page }) => {
    await page.goto(fixture("requirement"))
    const notice = page.locator('.dticProposal:has-text("SR-00012.01") .dticNotice')

    await expect(notice).toContainText("revision 01")
    await expect(notice).toContainText("revision 02")
    // It must not assert that the later revision carries the allocation; nothing looked there.
    await expect(notice).not.toContainText("hangs off")
  })

  test("a retirement is marked RET and its cascade target is dashed", async ({ page }) => {
    await page.goto(fixture("requirement"))

    const retire = page.locator('.dticProposal:has-text("SR-00011.00")')
    await expect(retire.locator(".dticOp-retire")).toHaveText("RET")
    await expect(retire).toHaveClass(/is-retire/)

    // HLR-00021.00 is reached only through the Retire, so the cascade shows on its path.
    await expect(page.locator('.dticAllocation:has-text("HLR-00021.00")')).toHaveClass(/is-retire-cascade/)
    // HLR-00020.00 hangs off a live Modify and must not be dashed as retired.
    await expect(page.locator('.dticAllocation:has-text("HLR-00020.00")')).not.toHaveClass(/is-retire-cascade/)
  })

  test("a proposal identifier is not an exact artifact link", async ({ page }) => {
    await page.goto(fixture("requirement"))
    const id = page.locator('.dticProposal:has-text("SR-00010.01") .dticId')

    // A proposal id belongs to a RequirementChange, not to a controlled artifact.
    await expect(id).toHaveText("SR-00010.01")
    await expect(id).not.toHaveAttribute("href", /./)
  })
})

test.describe("verification proposals", () => {
  test("every populated field survives to the DOM, including both members of each pair", async ({ page }) => {
    await page.goto(fixture("verification"))
    const rows = page.locator('.dticProposal:has-text("SYSTP-00030.01") .dticVerification')

    // steps AND orderedSteps; expectedResult AND expectedObservations. An earlier version collapsed each
    // pair with `||`, silently dropping one whenever both were populated.
    await expect(rows).toContainText("Steps")
    await expect(rows).toContainText("Ordered steps")
    await expect(rows).toContainText("Expected result")
    await expect(rows).toContainText("Expected observations")
    await expect(rows).toContainText("Environment")
    await expect(rows).toContainText("Tooling")
  })

  test("a Modify whose change is outside Steps still shows what changed", async ({ page }) => {
    await page.goto(fixture("verification"))
    const diff = page.locator('.dticProposal:has-text("SYSTP-00030.01") .dticDiff')

    // The objective and the environment differ; the steps do not. Comparing only steps reported no change.
    await expect(diff).toContainText("Objective")
    await expect(diff).toContainText("fixed-order")
    await expect(diff).toContainText("Environment")
    await expect(diff).toContainText("Desk check")
  })

  test("a retired Case says Case, and proposes no successor body", async ({ page }) => {
    await page.goto(fixture("verification"))
    const retire = page.locator('.dticProposal:has-text("SYSTP-00031.00")')

    // The wording follows the authoritative artifact kind rather than assuming "procedure".
    await expect(retire.locator(".dticNotice")).toContainText("case is being retired")
    await expect(retire.locator(".dticVerification")).toHaveCount(0)
  })

  test("a Case parent is shown as a parent, never as requirement coverage", async ({ page }) => {
    await page.goto(fixture("verification"))
    const card = page.locator('.dticProposal:has-text("SYSTP-00030.01")')

    await expect(card).toContainText("HLRTC-00040.00")
    await expect(card).toContainText("(Case)")
    // The coverage lane holds the requirement, not the Case.
    await expect(page.locator(".dticAllocation")).toContainText("SR-00010.01")
    await expect(page.locator(".dticAllocation")).not.toContainText("HLRTC-00040.00")
  })

  test("unresolved and malformed references are stated, without invented identity", async ({ page }) => {
    await page.goto(fixture("verification"))
    const gap = page.locator('.dticProposal:has-text("SYSTP-00030.01") .dticGap')

    await expect(gap).toContainText("could not be resolved")
    await expect(gap).toContainText("could not be read")
    // The unresolved parent claims no kind and no identity of its own.
    await expect(page.locator('.dticProposal:has-text("SYSTP-00030.01")')).toContainText("an unresolved reference")
  })
})

test.describe("frame behaviour", () => {
  test("an error renders inside the still-mounted canvas", async ({ page }) => {
    await page.goto(fixture("error"))

    // The canvas survives, so transform, lane offsets and selection survive with it.
    await expect(page.locator(".dtCanvas")).toBeVisible()
    await expect(page.locator(".dticInFrame-error")).toContainText("could not be opened")

    await page.locator(".dticInFrame-error button").click()
    await expect.poll(() => page.evaluate(() => document.body.dataset.retried)).toBe("yes")
    // Still mounted after retry.
    await expect(page.locator(".dtCanvas")).toBeVisible()
  })

  test("loading keeps the lane frame rather than collapsing to one lane", async ({ page }) => {
    await page.goto(fixture("loading"))

    await expect(page.locator(".dtCanvas")).toBeVisible()
    await expect(page.locator(".dticLoading")).toBeVisible()
    // The register lane is populated and the frame is present; the board must not be a single lane that
    // expands once content lands.
    await expect(page.locator(".dtCanvasLaneHead").first()).toBeVisible()
  })
})

test.describe("trace and interaction", () => {
  test("selecting a card traces its web and pushes the rest back", async ({ page }) => {
    await page.goto(fixture("requirement"))

    await page.locator('.dticProposal:has-text("SR-00010.01")').click()

    // Its allocated downstream is in the web; an unrelated proposal is not.
    await expect(page.locator('.dticAllocation:has-text("HLR-00020.00")')).not.toHaveClass(/is-untraced/)
    await expect(page.locator('.dticProposal:has-text("SR-00012.01")')).toHaveClass(/is-untraced/)
    // The hop badge says how far the linked record sits from the selection.
    await expect(page.locator('.dticAllocation:has-text("HLR-00020.00") .dticHop')).toHaveText("1")
    // And the panel opens on the selected record.
    await expect(page.locator(".dticPanel")).toContainText("SR-00010.01")
  })

  test("clicking another register entry asks the caller to open it in place", async ({ page }) => {
    await page.goto(fixture("requirement"))

    await page.locator('.dticRegister:has-text("SRCR-00101.00")').click()

    // The callback fires; no route is involved.
    await expect.poll(() => page.evaluate(() => document.body.dataset.openedChange)).toBe("SRCR-00101.00")
  })

  test("a card is reachable and activatable from the keyboard", async ({ page }) => {
    await page.goto(fixture("requirement"))

    const card = page.locator('.dtCanvasNode:has(.dticProposal:has-text("SR-00010.01"))')
    await card.focus()
    await page.keyboard.press("Enter")

    await expect(page.locator(".dticPanel")).toContainText("SR-00010.01")
  })
})
