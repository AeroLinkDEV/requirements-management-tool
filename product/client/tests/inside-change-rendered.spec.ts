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
    // A Case package has its own envelope: one review emits one artifact kind, so a Procedure envelope can
    // never contain a Case item and a fixture that mixed them proved nothing about production.
    await page.goto(fixture("case"))
    const retire = page.locator('.dticProposal:has-text("HLRTC-00050.00")')

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

  test("loading holds the five-lane frame, and content arriving does not move it", async ({ page }) => {
    await page.goto(fixture("loading"))

    // The whole conceptual frame stands while the payload is unknown. Asserting only that one lane head
    // exists would pass on a board that had collapsed to the register lane alone.
    const heads = page.locator(".dtCanvasLaneHead")
    await expect(heads).toHaveCount(5)
    const before = await heads.allTextContents()
    await expect(page.locator(".dticLoading")).toBeVisible()

    // Identify the canvas element so we can prove it was updated rather than rebuilt.
    await page.evaluate(() => document.querySelector(".dtCanvas")?.setAttribute("data-probe", "same"))

    // Flip to loaded in the same React root — no remount.
    await page.evaluate(() => (window as unknown as { __loadInsideChange: () => void }).__loadInsideChange())

    await expect(page.locator(".dticLoading")).toHaveCount(0)
    // Same DOM element: the canvas was never torn down, so transform, lane offsets and selection survive.
    await expect(page.locator(".dtCanvas")).toHaveAttribute("data-probe", "same")

    // The prohibited behaviour is the other direction: collapsing to the register lane while the payload is
    // unknown and then expanding as it lands. Contracting to the lanes that genuinely have content once the
    // answer is in is exactly what §5.2 asks for, so the lane set may shrink here — it may not have started
    // small. What must hold is that every lane still shown kept its identity and its order.
    // Lane headings carry a record count, which legitimately changes as content lands, so compare the
    // labels themselves.
    const label = (text: string) => text.replace(/[0-9]+$/, "")
    const after = (await heads.allTextContents()).map(label)
    expect(after.length).toBeLessThanOrEqual(before.length)
    expect(before.map(label).filter(head => after.includes(head))).toEqual(after)
    // Concretely: the full conceptual frame while unknown, then only the lanes that genuinely have content.
    expect(before.map(label)).toEqual([
      "CHANGE REQUEST",
      "PROPOSED SYSTEM REQUIREMENTS",
      "ALLOCATED HLRs",
      "SYSTEM PROCEDURES",
      "EFFECT ON THE BUILD",
    ])
    // Every lane now has content, so nothing compacts here — the point is that the set never grew.
    expect(after).toEqual(before.map(label))
  })

  test("a genuinely empty lane still compacts once the content is known", async ({ page }) => {
    // The Case package has no executions and no baseline records, so those two lanes are genuinely empty and
    // are dropped. Compaction still works; it is only deferred until the answer is in.
    await page.goto(fixture("case"))
    const heads = page.locator(".dtCanvasLaneHead")
    // Only the register and the proposed content have records: coverage, executions and baselines are all
    // genuinely empty for this package, so those three lanes are dropped rather than shown blank.
    await expect(heads).toHaveCount(2)
    await expect(heads.nth(0)).toContainText("CHANGE REQUEST")
    await expect(heads.nth(1)).toContainText("PROPOSED CASES AND PROCEDURES")
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

test.describe("panel placement", () => {
  test("the panel offers bottom, right and auto, and auto picks the emptier side", async ({ page }) => {
    await page.goto(fixture("requirement"))
    await page.locator('.dticProposal:has-text("SR-00010.01")').click()

    const panel = page.locator(".dticPanel")
    await expect(panel).toHaveClass(/dticPanel-bottom/)

    await panel.getByRole("button", { name: "Right" }).click()
    await expect(panel).toHaveClass(/dticPanel-right/)

    // Auto counts where the links sit. This record's only link is downstream, so the panel takes the
    // emptier left side rather than covering what the highlighted edge points at.
    await panel.getByRole("button", { name: "Auto" }).click()
    await expect(panel).toHaveClass(/dticPanel-left/)
  })

  test("the panel lists the whole traced web, marking deeper hops", async ({ page }) => {
    await page.goto(fixture("requirement"))
    await page.locator('.dticProposal:has-text("SR-00010.01")').click()

    // Two columns: upstream and downstream. Each lists the whole traced web, not just the first hop.
    await expect(page.locator(".dticPanelCol")).toHaveCount(2)
    const rows = page.locator(".dticRel button")
    await expect(rows.first()).toBeVisible()
    // A direct link says so; a deeper one would carry its hop count and a dashed border instead.
    await expect(page.locator(".dticRel").first()).toContainText("DIRECT")
  })
})

test.describe("lanes three and four", () => {
  test("a requirement change shows covering artifacts with the server's coverage state", async ({ page }) => {
    await page.goto(fixture("requirement"))

    const covering = page.locator('.dticTrace:has-text("HLRTP-00090.00")')
    await expect(covering).toBeVisible()
    // The state is the server's word, shown as a word and not only as a colour.
    await expect(covering).toContainText("Suspect")
    await expect(covering).toHaveClass(/is-suspect/)
  })

  test("coverage joins on the exact requirement revision, not the artifact", async ({ page }) => {
    await page.goto(fixture("requirement"))
    // The covering artifact sits in lane 3 and the requirement it covers in lane 2. The edge between them
    // exists only because the server recorded coverage against that exact revision id.
    await page.locator('.dticAllocation:has-text("HLR-00020.00")').click()

    // Its covering artifact is in the traced web; the unrelated retire-cascade target is not.
    await expect(page.locator('.dticTrace:has-text("HLRTP-00090.00")')).not.toHaveClass(/is-untraced/)
    await expect(page.locator('.dticAllocation:has-text("HLR-00021.00")')).toHaveClass(/is-untraced/)
  })

  test("a verification package shows executions rather than covering artifacts", async ({ page }) => {
    await page.goto(fixture("verification"))

    // Lane 3 for a test change is EXECUTIONS: what happened when the procedure was run.
    const run = page.locator('.dticTrace:has-text("verification.engineer")')
    await expect(run).toBeVisible()
    await expect(run).toContainText("Pass")
    await expect(run).toContainText("Sequencing behaved as specified.")
  })

  test("the build lane names the candidate baseline and the one it supersedes", async ({ page }) => {
    await page.goto(fixture("requirement"))

    await expect(page.locator('.dticTrace:has-text("SW-91.00.00")')).toContainText("Candidate baseline")
    const predecessor = page.locator('.dticTrace:has-text("SW-90.00.00")')
    await expect(predecessor).toContainText("Predecessor baseline")
    await expect(predecessor).toHaveClass(/is-predecessor/)
  })

  test("all five lanes are populated once the content is known", async ({ page }) => {
    await page.goto(fixture("requirement"))
    // §5.2's five lanes, none of them impossible to fill any more.
    const heads = page.locator(".dtCanvasLaneHead")
    await expect(heads).toHaveCount(5)
  })
})
