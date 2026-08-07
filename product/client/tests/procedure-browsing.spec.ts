import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, selectProgram } from "./auth";

/**
 * The verification workspace rendered every procedure it was given. The software HLR side holds 160 of them, so
 * finding one meant scrolling past the rest, and the client received far more than it could show.
 *
 * Software HLR is driven deliberately rather than the smaller System inventory, and it must remain isolated
 * from the LLR procedures that live beside it.
 */
test("the procedure workspace pages, filters and deep-links instead of rendering everything", async ({ page, request }) => {
  test.setTimeout(240_000);
  await page.setViewportSize({ width: 1440, height: 450 });
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");

  const workspaces = await (await page.request.get(`${apiBase}/api/workspaces`)).json();
  const fms = workspaces.find((x: { program: { name: string } }) => x.program.name === "Flight Management System Live Program");
  const projectId = fms.projects[0].project.id;
  const all = await (await page.request.get(
    `${apiBase}/api/test-procedures?projectId=${projectId}&scope=HighLevelSoftware&pageSize=1`)).json();
  expect(all.totalCount, "this only means something at showcase volume").toBeGreaterThanOrEqual(150);

  // Asked of the Test Procedure Explorer. This browsing behaviour was built on the change request page, which
  // used to carry a procedure library; the library moved here, and the filters came with it rather than being
  // dropped — this is now the only place procedures are browsed, so it had to be the most capable one.
  await page.getByRole("button", { name: /Search & navigate/ }).click();
  const palette = page.getByRole("dialog", { name: "Quick navigation" });
  await palette.getByPlaceholder(/Search pages/).fill("Software HLR Test Procedure Explorer");
  await palette.getByRole("link", { name: /Software HLR Test Procedure Explorer/ }).click();
  await expect(page.getByRole("heading", { name: "Test Procedure Explorer" })).toBeVisible({ timeout: 30_000 });

  // The whole point: hundreds of records, a bounded number of them on the page.
  const rows = page.locator(".procedureRow");
  await expect(rows.first()).toBeVisible({ timeout: 30_000 });
  const rendered = await rows.count();
  expect(rendered, `${rendered} of ${all.totalCount} procedures rendered at once`).toBeLessThanOrEqual(25);
  await expect(page.locator(".pager")).toContainText(`of ${all.totalCount.toLocaleString()}`, { timeout: 30_000 });

  // Filtering narrows the set and the count, and is reflected in the address.
  await page.getByLabel("Procedure state").selectOption("Approved");
  await expect(page).toHaveURL(/procedureState=Approved/, { timeout: 30_000 });
  const approvedTotal = (await (await page.request.get(
    `${apiBase}/api/test-procedures?projectId=${projectId}&scope=HighLevelSoftware&state=Approved&pageSize=1`)).json()).totalCount;
  await expect(page.locator(".pager")).toContainText(`of ${approvedTotal.toLocaleString()}`, { timeout: 30_000 });

  // A filtered worklist survives being reloaded, which is what makes it worth sharing.
  await page.reload({ waitUntil: "load" });
  await expect(page.getByLabel("Procedure state")).toHaveValue("Approved", { timeout: 30_000 });
  await expect(page.locator(".pager")).toContainText(`of ${approvedTotal.toLocaleString()}`, { timeout: 30_000 });

  // Paging is reachable, moves the list, and is in the address.
  const firstNumber = await rows.first().locator("b").first().textContent();
  await page.getByRole("button", { name: /Next/ }).click();
  await expect(page).toHaveURL(/procedurePage=2/, { timeout: 30_000 });
  await expect(rows.first().locator("b").first()).not.toHaveText(firstNumber ?? "", { timeout: 30_000 });

  // Back returns to the previous page of the same filtered list rather than leaving it. Choosing a page is
  // somewhere the reader went; typing in the search box is not, which is why only one of them pushes.
  await page.goBack();
  await expect(page).not.toHaveURL(/procedurePage=2/, { timeout: 30_000 });
  await expect(page.getByLabel("Procedure state")).toHaveValue("Approved", { timeout: 30_000 });
  await expect(rows.first().locator("b").first()).toHaveText(firstNumber ?? "", { timeout: 30_000 });

  // A search matching nothing says so, and says something different from having no procedures at all.
  await page.getByLabel("Find a procedure").fill("no-procedure-has-this-number");
  await expect(page.getByText("No procedure matches that. Clear the search or the filters to see the rest.")).toBeVisible({ timeout: 30_000 });
});

/**
 * A search result must not be overwritten by the reply to the search that preceded it.
 *
 * Changing a filter starts a second request while the first is still in flight, and nothing ordered the
 * replies. The unfiltered query is by far the slower one — it scans every procedure's coverage back to the
 * effective baseline — so the narrow filtered reply routinely arrived first and was then buried by the broad
 * reply behind it. The reader typed a search, saw the procedure they wanted, and watched the whole list they
 * had just filtered away come back over the top of it, with their search term still in the box.
 *
 * The race is real but timing-dependent, so it is not left to chance here: the unfiltered reply is held back
 * until the filtered one has been delivered, which makes the stale reply land last every single run.
 */
test("a slow unfiltered reply cannot bury the search result that overtook it", async ({ page, request }) => {
  test.setTimeout(240_000);
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");

  let filteredDelivered: () => void = () => {};
  const filteredHasLanded = new Promise<void>(resolve => { filteredDelivered = resolve; });
  await page.route("**/api/test-procedures?*", async route => {
    const searching = new URL(route.request().url()).searchParams.get("search");
    // The unfiltered reply is the one that used to win. It is made to lose, deterministically.
    if (!searching) await filteredHasLanded;
    await route.continue();
    if (searching) filteredDelivered();
  });

  await page.getByRole("button", { name: /Search & navigate/ }).click();
  const palette = page.getByRole("dialog", { name: "Quick navigation" });
  await palette.getByPlaceholder(/Search pages/).fill("System Test Procedure Explorer");
  await palette.getByRole("link", { name: /System Test Procedure Explorer/ }).click();

  await page.getByLabel("Find a procedure").fill("SYSTP-000001");
  const rows = page.locator(".procedureRow");
  await expect(rows).toHaveCount(1, { timeout: 30_000 });

  // The held reply is released by the assertion above having been satisfied; the list must not move.
  await expect(rows).toHaveCount(1, { timeout: 15_000 });
  await expect(page.getByLabel("Find a procedure")).toHaveValue("SYSTP-000001");
  await expect(rows.first().locator("b").first()).toHaveText(/^SYSTP-000001\./);
});
