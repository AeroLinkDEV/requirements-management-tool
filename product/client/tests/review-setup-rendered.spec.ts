import { expect, test, type Page } from "@playwright/test"

/**
 * #1016 S02. What the configured review-setup panel actually renders, and what it actually sends.
 *
 * The panel showed stored enum names for the authority a stage requires, hid the stage's own name and
 * purpose inside the accessible name — so a filled row read as a bare person and a role — and stated the
 * configured policy three times, once in warning styling.
 *
 * These mount the real `ChangeRequestWorkspace` against server-shaped payloads (`fixtures/review-setup.tsx`).
 * That is evidence about the component and the command it emits. It is not evidence about the application's
 * routing, and nothing here should be read as such.
 */

type RecordedCall = { method: string; url: string; body: unknown }
declare global {
  interface Window {
    __apiCalls: RecordedCall[]
    __unexpectedMutations: RecordedCall[]
  }
}

const openPanel = async (page: Page, scenario?: string) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto(`/tests/fixtures/review-setup.html${scenario ? `?scenario=${scenario}` : ""}`)
  await page.getByRole("button", { name: "Configure & Submit Review" }).click()
  return page.locator(".approverSetup")
}

test("a configured review row reads as a stage, an authority and a person", async ({ page }) => {
  const panel = await openPanel(page)
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
  const panel = await openPanel(page)
  const row = panel.locator(".approverRow").first()
  const heading = row.locator(".configuredStageHeading")
  const select = row.getByRole("combobox")

  await expect(select).toHaveAttribute("aria-label", "Systems review · Review · System Engineer")

  // A candidate's own Program role uses the person vocabulary — "System Test Engineer" — which is a
  // different question from the authority the stage requires, and the server words them differently too.
  await expect(select.locator("option")).toContainText(["Choose System Engineer", "Dana Systems", "Ravi Test"])
  await expect(select.locator("option").nth(2)).toContainText("System Test Engineer")

  await select.selectOption({ label: "Dana Systems · System Engineer" })

  // The regression this exists for: selection used to replace the only visible statement of the stage.
  await expect(heading).toBeVisible()
  await expect(heading).toContainText("Systems review")
  await expect(heading).toContainText("System Engineer")

  // Display never becomes identity — the value is the canonical account username the submit endpoint
  // resolves an approver by, not the label the reader saw.
  expect(await select.inputValue()).toBe("dana.systems")
})

test("the row is reachable and operable from the keyboard, and keeps saying what it is", async ({ page }) => {
  const panel = await openPanel(page)
  const row = panel.locator(".approverRow").first()
  const select = row.getByRole("combobox")

  // Reached by tabbing, not by calling focus(). Programmatic focus proves the element can hold focus; it
  // says nothing about whether a keyboard user can get to it, which is the part that was in doubt once the
  // stage text moved out of the accessible name and into visible content.
  await page.locator("body").click({ position: { x: 2, y: 2 } })
  let reached = false
  for (let press = 0; press < 40 && !reached; press += 1) {
    await page.keyboard.press("Tab")
    reached = await select.evaluate(node => node === document.activeElement)
  }
  expect(reached, "the first configured stage selector was not reachable by Tab").toBe(true)

  // Operated by keystroke. A native select moves its selection on ArrowDown and raises change, which is the
  // path a keyboard user actually takes; selectOption() would set the value without any of it.
  await page.keyboard.press("ArrowDown")
  await expect(select).not.toHaveValue("")
  expect(await select.inputValue()).toBe("dana.systems")

  // Still focused, and the stage is still legible while it is being operated.
  await expect(select).toBeFocused()
  await expect(row.locator(".configuredStageHeading")).toContainText("Systems review")
})

test("a stage nobody can fill stays visible and blocks submission", async ({ page }) => {
  const panel = await openPanel(page, "unstaffed")
  const rows = panel.locator(".approverRow")

  // The unfillable stage is still a row, and still says which authority is missing. Dropping it would let
  // the submission look complete while a required authority went unsigned.
  const unstaffed = rows.nth(1).locator(".configuredStageHeading")
  await expect(unstaffed).toBeVisible()
  await expect(unstaffed).toContainText("Approval")
  await expect(unstaffed).toContainText("System Engineering Lead")

  // Nobody to choose: the placeholder is the only option, so no eligible person is invented for the row.
  const emptySelect = rows.nth(1).getByRole("combobox")
  await expect(emptySelect.locator("option")).toHaveCount(1)
  expect(await emptySelect.inputValue()).toBe("")

  const submit = panel.getByRole("button", { name: /Submit for Review/ })
  await expect(submit).toBeDisabled()

  // Filling the stage that *can* be filled does not make the other one satisfied.
  await rows.nth(0).getByRole("combobox").selectOption({ label: "Dana Systems · System Engineer" })
  await expect(submit).toBeDisabled()
  await expect(panel.locator(".reviewerActions")).toContainText("1 reviewer selected")

  // And nothing was sent. A refusal that still posts is not a refusal.
  await submit.click({ force: true })
  expect(await page.evaluate(() => window.__apiCalls)).toEqual([])
})

test("submitting sends canonical identities and the configured mode, not display labels", async ({ page }) => {
  const panel = await openPanel(page)
  const rows = panel.locator(".approverRow")
  await rows.nth(0).getByRole("combobox").selectOption({ label: "Dana Systems · System Engineer" })
  await rows.nth(1).getByRole("combobox").selectOption({ label: "Mira Lead · System Engineering Lead" })

  const submit = panel.getByRole("button", { name: /Submit for Review/ })
  await expect(submit).toBeEnabled()
  await submit.click()

  // The outgoing command, not the selected value. The value proves what the control holds; this proves what
  // the component actually asked the server to do with it.
  await expect.poll(async () => (await page.evaluate(() => window.__apiCalls)).length).toBe(1)
  const [call] = await page.evaluate(() => window.__apiCalls)
  expect(call.method).toBe("POST")
  expect(call.url).toBe("/api/change-requests/00000000-0000-0000-0000-0000000000c1/submit")

  const body = call.body as { approvers: { userId: string }[]; mode: string; expectedVersion: number }
  // Canonical account usernames, in configured stage order. The submit endpoint resolves an approver by
  // UserName, so a display label arriving here would name nobody.
  expect(body.approvers.map(approver => approver.userId)).toEqual(["dana.systems", "mira.lead"])
  expect(body.mode).toBe("Sequential")
  expect(body.expectedVersion).toBe(3)

  // Nothing else was written on the way.
  expect(await page.evaluate(() => window.__unexpectedMutations)).toEqual([])
})

test("a Parallel policy is presented as parallel, and submits as parallel", async ({ page }) => {
  const panel = await openPanel(page, "parallel")

  // Neutral statement of the policy the project set, and no sequential wording anywhere: nothing about an
  // order, a first reviewer, or one-at-a-time activation, all of which would be false here.
  await expect(panel.locator(".reviewModePolicy")).toContainText("Parallel review mode")
  await expect(panel).not.toContainText(/in order/i)
  await expect(panel).not.toContainText(/one at a time/i)
  await expect(panel).not.toContainText(/Sequential/)

  // The position badge is the tell: a numbered ladder would assert an execution order the policy does not have.
  const rows = panel.locator(".approverRow")
  await expect(rows.nth(0).locator("> span").first()).toHaveText("•")
  await expect(rows.nth(1).locator("> span").first()).toHaveText("•")
  await expect(panel.locator(".reviewerActions")).toContainText("Parallel authority path")

  // Stage identity is unchanged by the mode.
  await expect(rows.nth(0).locator(".configuredStageHeading")).toContainText("Systems review")

  await rows.nth(0).getByRole("combobox").selectOption({ label: "Dana Systems · System Engineer" })
  await rows.nth(1).getByRole("combobox").selectOption({ label: "Mira Lead · System Engineering Lead" })
  await panel.getByRole("button", { name: /Submit for Review/ }).click()

  await expect.poll(async () => (await page.evaluate(() => window.__apiCalls)).length).toBe(1)
  const [call] = await page.evaluate(() => window.__apiCalls)
  expect((call.body as { mode: string }).mode).toBe("Parallel")

  await page.screenshot({ path: "test-results/review-setup-parallel.png", fullPage: true })
})

test("routine configured policy is stated once, and not as a warning", async ({ page }) => {
  const panel = await openPanel(page)

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
  const panel = await openPanel(page)
  const heading = panel.locator(".approverRow").nth(1).locator(".configuredStageHeading")
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
