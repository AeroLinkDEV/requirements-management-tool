import { expect, test, type Page } from "@playwright/test";
import { chooseCategory, login, selectProgram } from "./auth";

/**
 * The state header is the answer to "where is this report, and what do I do next".
 *
 * These journeys hold it to the domain's transition policy rather than to whatever the component
 * happens to render. The expected sets below are
 * `ProblemReportTransitionPolicy.AllowedTargets` written out: if the policy changes, one of these fails
 * and the header has to be brought back in line, which is the point. A spec that only asserted "a
 * backward menu exists" would pass no matter which states it offered, and offering a state the policy
 * refuses is precisely the failure that matters on a controlled record.
 *
 * Rejection is deliberately never asserted as a plain transition. It requires a disposition, so the only
 * control that may perform it is the one that opens the disposition dialog.
 */

const header = (page: Page) => page.getByRole("region", { name: "Problem Report lifecycle" });

const openProblemReports = async (page: Page) => {
  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: "load" });
};

const createDraft = async (page: Page, title: string) => {
  await page.getByRole("button", { name: "+ Record problem" }).click();
  const dialog = page.getByRole("dialog", { name: "Record a problem" });
  await dialog.getByLabel("Title").fill(title);
  await dialog
    .getByRole("textbox", { name: "Problem Description paragraph 1" })
    .fill("The state header must name the state this report is actually in.");
  await chooseCategory(dialog, "Code Issue — Functional Impact");
  await dialog.getByRole("button", { name: "Save Draft PR" }).click();
  await expect(page.getByRole("heading", { name: title })).toBeVisible();
};

/**
 * Moves the selected report one state forward through the header's primary action, and proves the move
 * landed by reading the marked step — not by finding the target's name somewhere in the rail, which is
 * printed for every state whether or not the report is in it.
 */
const moveForward = async (page: Page, target: string) => {
  const action = header(page).getByRole("button", { name: new RegExp(`Move to ${target}`) });
  await expect(action).toBeEnabled();
  await action.click();
  // Forward edges on the happy path carry no rationale requirement, so no dialog is expected.
  const currentStep = header(page).getByRole("list").locator('[aria-current="step"]');
  await expect(currentStep).toContainText(target);
};

test("the rail names the current state, and only the states the policy allows", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  const title = `State header rail ${Date.now()}`;
  await createDraft(page, title);

  const rail = header(page).getByRole("list");
  // All seven canonical states are named, and Rejected is not among them: it is terminal and off-path,
  // so it never occupies a rail position.
  await expect(rail.getByRole("listitem")).toHaveCount(7);
  for (const state of [
    "Draft",
    "Ready for SCCB",
    "Open",
    "Implementing",
    "Verifying",
    "Waiting for SQA to Close",
    "Closed",
  ]) {
    await expect(rail.getByRole("listitem").filter({ hasText: state }).first()).toBeVisible();
  }
  await expect(rail.getByRole("listitem").filter({ hasText: "Rejected" })).toHaveCount(0);

  // The current step is marked for assistive technology and spelled out in text, not by colour alone.
  const currentStep = rail.locator('[aria-current="step"]');
  await expect(currentStep).toHaveCount(1);
  await expect(currentStep).toContainText("Draft");
  await expect(currentStep).toContainText("Current");
  await expect(header(page)).toContainText("step 1 of 7");

  // AllowedTargets(Draft) is [ReadyForSccb, Rejected]. So: one forward action, no backward menu, and a
  // reject control that is not a plain transition.
  await expect(header(page).getByRole("button", { name: /Move to Ready for SCCB/ })).toBeVisible();
  await expect(header(page).getByText("Move backward")).toHaveCount(0);
  await expect(header(page).getByRole("button", { name: "Reject…" })).toBeVisible();
  await expect(header(page).getByRole("button", { name: "Rejected" })).toHaveCount(0);
});

test("backward targets are offered by name, matching the policy for the current state", async ({
  page,
}) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  const title = `State header backward ${Date.now()}`;
  await createDraft(page, title);

  await moveForward(page, "Ready for SCCB");
  await expect(header(page)).toContainText("step 2 of 7");

  // AllowedTargets(ReadyForSccb) is [Open, Draft, Rejected]: Open forward, Draft backward.
  await expect(header(page).getByRole("button", { name: /Move to Open/ })).toBeVisible();
  const menu = header(page).locator("details.prBackward");
  await expect(menu).toBeVisible();
  await menu.locator("summary").click();
  const earlier = menu.getByRole("group", { name: "Earlier states" });
  await expect(earlier.getByRole("button")).toHaveCount(1);
  // Named, so the reader chooses the state. This used to act on whichever of Draft or Verifying came
  // first in availableTransitions, which is list order, not a decision.
  await expect(earlier.getByRole("button", { name: /^Draft/ })).toBeVisible();

  await moveForward(page, "Open");
  await expect(header(page)).toContainText("step 3 of 7");
  // AllowedTargets(Open) is [Implementing, ReadyForSccb, Draft, Rejected]: two backward targets now.
  await expect(header(page).getByRole("button", { name: /Move to Implementing/ })).toBeVisible();
  await menu.locator("summary").click();
  await expect(earlier.getByRole("button")).toHaveCount(2);
  await expect(earlier.getByRole("button", { name: /^Ready for SCCB/ })).toBeVisible();
  await expect(earlier.getByRole("button", { name: /^Draft/ })).toBeVisible();
});

test("a backward move collects its rationale before it is performed", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  const title = `State header rationale ${Date.now()}`;
  await createDraft(page, title);
  await moveForward(page, "Ready for SCCB");

  const menu = header(page).locator("details.prBackward");
  await menu.locator("summary").click();
  await menu.getByRole("button", { name: /^Draft/ }).click();

  // RequiresRationale(ReadyForSccb -> Draft) is true, so the dialog must appear and name the target.
  const dialog = page.getByRole("dialog", { name: "Backward Problem Report transition" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("heading")).toContainText("Draft");
  await dialog.getByLabel("Rationale").fill("The SCCB asked for the containment section first.");
  await dialog.getByRole("button", { name: /Move to Draft/ }).click();

  await expect(header(page)).toContainText("step 1 of 7");
  const currentStep = header(page).getByRole("list").locator('[aria-current="step"]');
  await expect(currentStep).toContainText("Draft");
});

test("the state and its next action stay visible on every tab", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  const title = `State header tabs ${Date.now()}`;
  await createDraft(page, title);

  // The header belongs to the record, not to the Record tab. It used to be the last section of that
  // tab, so a reader on Code or History could not see the state or reach the next action at all.
  for (const tab of ["Code", "Record", "History"]) {
    await page.getByRole("navigation", { name: "Problem Report sections" }).getByRole("button", { name: new RegExp(`^${tab}`) }).click();
    await expect(header(page)).toBeVisible();
    await expect(header(page)).toContainText("Draft");
    await expect(header(page).getByRole("button", { name: /Move to Ready for SCCB/ })).toBeVisible();
  }
});
