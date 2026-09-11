import { expect, test, type Page } from "@playwright/test"

/**
 * #1016 S04. "Downstream Assessments" in every state the queue renders, not just the populated one.
 *
 * Two things are under correction here and both are collection-level, so both need a collection to check
 * them against:
 *
 *   1. The heading. Four separate `<h2>`s existed, one per state, and they said "Downstream change
 *      assessments". A test that visits only the populated queue proves one of the four.
 *   2. The description. It claimed every row was awaiting a downstream conclusion. The mixed scenario below
 *      contains a Superseded row and two Complete rows, which is exactly the collection that made the old
 *      sentence false, so the assertion is against data that would have caught it.
 *
 * Mounted against the real component (`fixtures/downstream-assessments-queue.tsx`). This is presentation
 * evidence; the queue's server projection is unchanged by #1016 and is not under test here.
 */

const HEADING = "Downstream Assessments"
const DESCRIPTION = "Approved upstream changes and their HLR engineering conclusions, pending and recorded."

const open = async (page: Page, scenario: string) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto(`/tests/fixtures/downstream-assessments-queue.html?scenario=${scenario}`)
  return page.locator(".downstreamQueue")
}

// The heading and the description are rendered by each of the four states, and each one used to carry its own
// copy of the old wording. Asserting the state attribute alongside them is what stops this passing because
// the page happened to fall through to a different branch than the one named.
const statesUnderTest = ["mixed", "empty", "failed", "loading"] as const

for (const scenario of statesUnderTest) {
  test(`the ${scenario} queue is headed "${HEADING}" and describes itself truthfully`, async ({ page }) => {
    const queue = await open(page, scenario)
    const state = scenario === "mixed" ? "rows" : scenario === "failed" ? "error" : scenario
    await expect(queue).toHaveAttribute("data-queue-state", state)

    await expect(queue.getByRole("heading", { level: 2, name: HEADING })).toBeVisible()
    // The old heading, in the exact words it used. Asserting the new one alone would still pass if a fifth
    // state kept the old copy.
    await expect(queue).not.toContainText("Downstream change assessments")
    await expect(queue.locator("header p").last()).toHaveText(DESCRIPTION)
    // The specific untruth: this queue does not hold only rows awaiting a conclusion.
    await expect(queue).not.toContainText(/awaiting/i)
  })
}

test("a mixed collection shows pending and dispositioned rows under the same heading", async ({ page }) => {
  const queue = await open(page, "mixed")
  const rows = queue.locator(".downstreamAssessment")
  await expect(rows).toHaveCount(4)

  // This is the collection the description has to be true of. If every row were undecided, the corrected
  // sentence would be indistinguishable from the wrong one.
  await expect(rows.nth(0)).toContainText("HLR Assessment Required")
  await expect(rows.nth(1)).toContainText("HLR Assessment Superseded")
  await expect(rows.nth(2)).toContainText("HLR Assessment Complete – No HLRCR Required")
  await expect(rows.nth(3)).toContainText("HLR Assessment Complete – HLRCR Created")

  await expect(queue.locator("header p").last()).toHaveText(DESCRIPTION)
  await page.screenshot({ path: "test-results/downstream-assessments-mixed.png", fullPage: true })
})

test("an empty queue says so in the corrected terminology", async ({ page }) => {
  const queue = await open(page, "empty")
  await expect(queue.locator(".downstreamAssessment")).toHaveCount(0)
  // The empty-state sentence carries the term too, and carried the old one before this change.
  await expect(queue.locator(".downstreamHelp"))
    .toHaveText("No HLR Downstream Assessments are currently recorded.")
})

test("a failed load keeps the heading, states the failure, and can be retried", async ({ page }) => {
  const queue = await open(page, "failed")

  // The failure is announced rather than left as an empty queue, which would read as "nothing to assess".
  const alert = queue.getByRole("alert")
  await expect(alert).toBeVisible()
  await expect(alert).toContainText("could not be read")
  await expect(queue).not.toContainText("No HLR Downstream Assessments are currently recorded.")

  // The retry presentation is not decoration: the fixture answers the second read, so a working control
  // reaches the rows and a control that only looks like one does not.
  await queue.getByRole("button", { name: "Retry loading assessments" }).click()
  await expect(queue.locator(".downstreamAssessment")).toHaveCount(4)
  await expect(queue.getByRole("heading", { level: 2, name: HEADING })).toBeVisible()
})

test("the loading state says what it is loading, in the corrected terminology", async ({ page }) => {
  const queue = await open(page, "loading")
  await expect(queue.locator(".downstreamHelp")).toHaveText("Loading HLR assessments…")
  // Not an empty queue while it waits: "nothing recorded" and "not read yet" are different facts.
  await expect(queue).not.toContainText("No HLR Downstream Assessments are currently recorded.")
})
