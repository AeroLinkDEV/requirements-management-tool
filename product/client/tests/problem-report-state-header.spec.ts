import { expect, test, type Page } from "@playwright/test";
import { chooseCategory, login, selectProgram } from "./auth";

/**
 * The state header is the answer to "where is this report, and what do I do next".
 *
 * Two things it must get right, and they are different:
 *
 * 1. **Position.** The rail is `ProblemReportTransitionPolicy.CanonicalStates` and the marked step is
 *    the report's state. That is fixed, so it is asserted exactly.
 * 2. **Classification.** Whatever the server offers has to land in the right control: a state earlier
 *    on the rail belongs in the backward menu, a later one is the primary action, and `Rejected` is
 *    never a plain transition because it requires a disposition.
 *
 * What these journeys deliberately do **not** assert is *which* transitions are offered at a given
 * state. `AllowedTargets` is the state graph; it is not authorization. `ReadyForSccb -> Open` is an
 * SCCB opening and is restricted to `SccbOpeningRoles`, so a signed-in actor without one of those
 * roles is correctly offered no forward action there at all. An earlier version of this spec asserted
 * the graph edge as though it were an offered action and failed against a correct header — the exact
 * confusion `problemReportLifecycle.ts` warns about. The server decides what is offered; the header is
 * only responsible for putting what it is given in the right place.
 *
 * Each journey names its own record with a run-unique stamp and isolates it with the queue's search,
 * so nothing depends on queue position or on records other journeys left behind.
 */

const header = (page: Page) => page.getByRole("region", { name: "Problem Report lifecycle" });
const rail = (page: Page) => header(page).getByRole("list");
const currentStep = (page: Page) => rail(page).locator('[aria-current="step"]');

const openProblemReports = async (page: Page) => {
  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: "load" });
};

/** Creates a Draft and leaves the queue filtered to it alone, so the pane cannot be showing another. */
const createIsolatedDraft = async (page: Page, title: string) => {
  await page.getByRole("button", { name: "+ Record problem" }).click();
  const dialog = page.getByRole("dialog", { name: "Record a problem" });
  await dialog.getByLabel("Title").fill(title);
  await dialog
    .getByRole("textbox", { name: "Problem Description paragraph 1" })
    .fill("The state header must name the state this report is actually in.");
  await chooseCategory(dialog, "Code Issue — Functional Impact");
  await dialog.getByRole("button", { name: "Save Draft PR" }).click();
  await expect(page.getByRole("heading", { name: title })).toBeVisible();

  await page.getByLabel("Search").fill(title);
  await page.getByRole("button", { name: "Apply filters" }).click();
  await expect(page.locator(".prList > button")).toHaveCount(1, { timeout: 30_000 });
  await page.locator(".prList > button").first().click();
  await expect(page.locator(".prDetail h2")).toHaveText(title);
};

const RAIL_LABELS = [
  "Draft",
  "Ready for SCCB",
  "Open",
  "Implementing",
  "Verifying",
  "Waiting for SQA to Close",
  "Closed",
];

test("the rail is the canonical lifecycle, and marks where this report is", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  await createIsolatedDraft(page, `State header rail ${Date.now()}`);

  await expect(rail(page).getByRole("listitem")).toHaveCount(RAIL_LABELS.length);
  for (const label of RAIL_LABELS) {
    await expect(rail(page).getByRole("listitem").filter({ hasText: label }).first()).toBeVisible();
  }
  // Rejected is terminal and off-path: it never occupies a rail position, because a rejected report
  // did not progress along the lifecycle to get there.
  await expect(rail(page).getByRole("listitem").filter({ hasText: "Rejected" })).toHaveCount(0);

  // Marked for assistive technology and spelled out in text, never by colour alone.
  await expect(currentStep(page)).toHaveCount(1);
  await expect(currentStep(page)).toContainText("Draft");
  await expect(currentStep(page)).toContainText("Current");
  await expect(header(page)).toContainText("step 1 of 7");
});

test("rejecting is never a plain transition", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  await createIsolatedDraft(page, `State header reject ${Date.now()}`);

  // It requires a disposition, so the only control that may perform it is the one that collects one.
  // A plain `Rejected` transition button sitting beside that control is the duplicate this replaced.
  await expect(header(page).getByRole("button", { name: "Reject…" })).toBeVisible();
  await expect(header(page).getByRole("button", { name: "Rejected", exact: true })).toHaveCount(0);
  await expect(header(page).getByRole("button", { name: /Move to Rejected/ })).toHaveCount(0);
});

test("offers are classified by where they sit on the rail", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  await createIsolatedDraft(page, `State header classify ${Date.now()}`);

  // From Draft the only forward offer is Ready for SCCB, and there is nothing behind it.
  await expect(header(page).getByRole("button", { name: /Move to Ready for SCCB/ })).toBeVisible();
  await expect(header(page).locator("details.prBackward")).toHaveCount(0);

  await header(page).getByRole("button", { name: /Move to Ready for SCCB/ }).click();
  await expect(currentStep(page)).toContainText("Ready for SCCB");
  await expect(header(page)).toContainText("step 2 of 7");

  // Now something is behind it, and every entry in the menu must name a state that really is earlier
  // on the rail — the old control guessed a target from the order of availableTransitions instead.
  const menu = header(page).locator("details.prBackward");
  await expect(menu).toBeVisible();
  await menu.locator("summary").click();
  const earlier = menu.getByRole("group", { name: "Earlier states" });
  const offered = await earlier.getByRole("button").allInnerTexts();
  expect(offered.length).toBeGreaterThan(0);
  const currentIndex = RAIL_LABELS.indexOf("Ready for SCCB");
  for (const entry of offered) {
    const label = entry.replace(/…$/, "").trim();
    expect(RAIL_LABELS).toContain(label);
    expect(RAIL_LABELS.indexOf(label)).toBeLessThan(currentIndex);
  }

  // Whatever forward action is offered — the server may withhold it, since opening an SCCB report is
  // role-restricted — it must name a state ahead of this one, never behind it.
  const forward = header(page).getByRole("button", { name: /^Move to / });
  for (const text of await forward.allInnerTexts()) {
    const label = text.replace(/^Move to /, "").replace(/[…→]\s*$/, "").trim();
    expect(RAIL_LABELS.indexOf(label)).toBeGreaterThan(currentIndex);
  }
});

test("a backward move collects its rationale before it is performed", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  await createIsolatedDraft(page, `State header rationale ${Date.now()}`);

  await header(page).getByRole("button", { name: /Move to Ready for SCCB/ }).click();
  await expect(currentStep(page)).toContainText("Ready for SCCB");

  const menu = header(page).locator("details.prBackward");
  await menu.locator("summary").click();
  await menu.getByRole("button", { name: /^Draft/ }).click();

  // RequiresRationale(ReadyForSccb -> Draft) is true, so the dialog must appear and name the target.
  const dialog = page.getByRole("dialog", { name: "Backward Problem Report transition" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("heading")).toContainText("Draft");
  await dialog.getByLabel("Rationale").fill("The SCCB asked for the containment section first.");
  await dialog.getByRole("button", { name: /Move to Draft/ }).click();

  await expect(currentStep(page)).toContainText("Draft");
  await expect(header(page)).toContainText("step 1 of 7");
});

test("the state and its next action stay visible on every tab", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  await createIsolatedDraft(page, `State header tabs ${Date.now()}`);

  // The header belongs to the record, not to the Record tab. It used to be the last section of that
  // tab, so a reader on Code or History could not see the state or reach the next action at all.
  for (const tab of ["Code", "Record", "History"]) {
    await page
      .getByRole("navigation", { name: "Problem Report sections" })
      .getByRole("button", { name: new RegExp(`^${tab}`) })
      .click();
    await expect(header(page)).toBeVisible();
    await expect(currentStep(page)).toContainText("Draft");
    await expect(header(page).getByRole("button", { name: /Move to Ready for SCCB/ })).toBeVisible();
  }
});
