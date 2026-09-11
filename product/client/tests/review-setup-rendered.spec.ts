import { expect, test } from "@playwright/test"

/**
 * #1016 S02. What the configured review-setup panel actually renders.
 *
 * The panel showed stored enum names for the authority a stage requires, hid the stage's own name and
 * purpose inside the accessible name — so a filled row read as a bare person and a role — and stated the
 * configured policy three times, once in warning styling.
 */

test("a configured review row reads as a stage, an authority and a person", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/review-setup.html")

  await page.getByRole("button", { name: "Configure & Submit Review" }).click()
  const panel = page.locator(".approverSetup")
  await expect(panel.getByRole("heading", { name: "Configure review authority" })).toBeVisible()

  // Readable authority, and no stored enum name anywhere in view. Both halves matter: the formatter falls
  // back to returning the key, so a missing label looks exactly like a correct one.
  await expect(panel).toContainText("System Engineer")
  await expect(panel).not.toContainText("SystemEngineer")
  await expect(panel).not.toContainText("SystemEngineeringLead")
  await expect(panel).not.toContainText("SystemTestEngineer")

  const rows = panel.locator(".approverRow")
  const firstHeading = rows.nth(0).locator(".configuredStageHeading")
  await expect(firstHeading).toBeVisible()
  await expect(firstHeading).toContainText("Systems review")
  await expect(firstHeading).toContainText("Review")

  // Approval is not Review. Combining them into one lane elsewhere does not make them one fact here.
  const secondHeading = rows.nth(1).locator(".configuredStageHeading")
  await expect(secondHeading).toContainText("Approval")
  await expect(secondHeading).toContainText("System Engineering Lead")

  await page.screenshot({ path: "test-results/review-setup-after.png", fullPage: true })
})

test("the stage still says what it is after somebody is selected", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/review-setup.html")
  await page.getByRole("button", { name: "Configure & Submit Review" }).click()

  const row = page.locator(".approverSetup .approverRow").first()
  const heading = row.locator(".configuredStageHeading")
  const select = row.getByRole("combobox")

  // The accessible name carries the whole stage, and keyboard reaches the control.
  await expect(select).toHaveAttribute("aria-label", "Systems review · Review · System Engineer")
  await select.focus()
  await expect(select).toBeFocused()

  // A candidate's own Program role uses the person vocabulary — "System Test Engineer" — which is a
  // different question from the authority the stage requires, and the server words them differently too.
  await expect(select.locator("option")).toContainText(["Choose System Engineer", "Dana Systems", "Ravi Test"])
  await expect(select.locator("option").nth(2)).toContainText("System Test Engineer")

  await select.selectOption({ label: "Dana Systems · System Engineer" })

  // The regression this exists for: selection used to replace the only visible statement of the stage.
  await expect(heading).toBeVisible()
  await expect(heading).toContainText("Systems review")
  await expect(heading).toContainText("System Engineer")

  // Display never becomes identity — the value is the canonical userId, not the label.
  expect(await select.inputValue()).toBe("00000000-0000-0000-0000-0000000000u1")
})

test("routine configured policy is stated once, and not as a warning", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto("/tests/fixtures/review-setup.html")
  await page.getByRole("button", { name: "Configure & Submit Review" }).click()
  const panel = page.locator(".approverSetup")

  await expect(panel.getByText(/is the active policy for this submission/)).toHaveCount(1)

  // Warning presentation is for a condition needing attention. A panel whose ordinary state is amber
  // teaches people to read past amber, and the duplicate policy sentence sat in exactly that styling.
  for (const text of await panel.locator(".reviewerWarning").allTextContents()) {
    expect(text).not.toMatch(/requires the configured rows/)
  }

  // The mode is still stated, and still neutral.
  await expect(panel.locator(".reviewModePolicy")).toContainText("Sequential")
})

test("a long stage name stays readable across supported widths", async ({ page }) => {
  await page.goto("/tests/fixtures/review-setup.html")
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.getByRole("button", { name: "Configure & Submit Review" }).click()

  const heading = page.locator(".approverSetup .approverRow").nth(1).locator(".configuredStageHeading")
  await expect(heading).toContainText("airworthiness and certification approval")

  // The requirement is that the name is never clipped. Whether it needs a second line depends on the width:
  // at 1440 this one fits, and asserting it must wrap there would be asserting the viewport, not the rule.
  const clipped = async () => heading.evaluate(node => node.scrollWidth - node.clientWidth)
  expect(await clipped(), "the heading must not be clipped at 1440").toBeLessThanOrEqual(1)

  // And at the narrower supported width, where the row has less to give it.
  await page.setViewportSize({ width: 1024, height: 900 })
  await expect(heading).toContainText("Systems engineering independent airworthiness and certification approval")
  expect(await clipped(), "the heading must not be clipped at 1024").toBeLessThanOrEqual(1)

  // Whether it takes one line or two is the browser's business and the width's; what must hold is that the
  // whole name is present and none of it is cut off. An earlier version of this test asserted a second line
  // and was really asserting the viewport — it failed because the name happened to fit.
  const font = await heading.locator("b").evaluate(node => getComputedStyle(node).fontSize)
  expect(font, "the name must not be shrunk to fit").toBe("12px")
})
