import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, selectProgram } from "./auth";

/**
 * The verification workspace rendered every procedure it was given. The software side holds 440 of them, so
 * finding one meant scrolling past the rest, and the client received far more than it could show.
 *
 * Software is driven deliberately rather than System: the system side has 75 procedures, which is unpleasant
 * but survivable, and a check that only ever saw 75 would have reported the workspace healthy.
 */
test("the procedure workspace pages, filters and deep-links instead of rendering everything", async ({ page, request }) => {
  test.setTimeout(240_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  await login(page);
  await selectProgram(page, "Flight Management System Live Program");

  const workspaces = await (await page.request.get(`${apiBase}/api/workspaces`)).json();
  const fms = workspaces.find((x: { program: { name: string } }) => x.program.name === "Flight Management System Live Program");
  const projectId = fms.projects[0].project.id;
  const all = await (await page.request.get(
    `${apiBase}/api/test-procedures?projectId=${projectId}&scope=Software&pageSize=1`)).json();
  expect(all.totalCount, "this only means something at showcase volume").toBeGreaterThanOrEqual(400);

  // Reached through the command palette, which is how the software workspace is addressable.
  await page.getByRole("button", { name: /Search & navigate/ }).click();
  const palette = page.getByRole("dialog", { name: "Quick navigation" });
  await palette.getByPlaceholder(/Search pages/).fill("Software Verification");
  await palette.getByRole("link", { name: /Software Verification/ }).click();
  await expect(page.getByRole("heading", { name: "Verification & Evidence" })).toBeVisible({ timeout: 30_000 });
  await page.getByRole("button", { name: /Test procedures/ }).click();

  // The whole point: hundreds of records, a bounded number of them on the page.
  const rows = page.locator(".procedureRow");
  await expect(rows.first()).toBeVisible({ timeout: 30_000 });
  const rendered = await rows.count();
  expect(rendered, `${rendered} of ${all.totalCount} procedures rendered at once`).toBeLessThanOrEqual(25);
  await expect(page.getByText(`${all.totalCount} procedures`)).toBeVisible();

  // Filtering narrows the set and the count, and is reflected in the address.
  await page.getByLabel("Procedure state filter").selectOption("Approved");
  await expect(page).toHaveURL(/procedureState=Approved/, { timeout: 30_000 });
  const approvedTotal = (await (await page.request.get(
    `${apiBase}/api/test-procedures?projectId=${projectId}&scope=Software&state=Approved&pageSize=1`)).json()).totalCount;
  await expect(page.getByText(`${approvedTotal} procedures`)).toBeVisible({ timeout: 30_000 });

  // A filtered worklist survives being reloaded, which is what makes it worth sharing.
  await page.reload({ waitUntil: "load" });
  await expect(page.getByLabel("Procedure state filter")).toHaveValue("Approved", { timeout: 30_000 });
  await expect(page.getByText(`${approvedTotal} procedures`)).toBeVisible({ timeout: 30_000 });

  // Paging is reachable, moves the list, and is in the address.
  const firstNumber = await rows.first().locator("b").first().textContent();
  await page.getByRole("button", { name: "Next" }).click();
  await expect(page).toHaveURL(/procedurePage=2/, { timeout: 30_000 });
  await expect(rows.first().locator("b").first()).not.toHaveText(firstNumber ?? "", { timeout: 30_000 });

  // Back returns to the previous page of the same filtered list rather than leaving it. Choosing a page is
  // somewhere the reader went; typing in the search box is not, which is why only one of them pushes.
  await page.goBack();
  await expect(page).not.toHaveURL(/procedurePage=2/, { timeout: 30_000 });
  await expect(page.getByLabel("Procedure state filter")).toHaveValue("Approved", { timeout: 30_000 });
  await expect(rows.first().locator("b").first()).toHaveText(firstNumber ?? "", { timeout: 30_000 });

  // A search matching nothing says so, and says something different from having no procedures at all.
  await page.getByLabel("Find a procedure").fill("no-procedure-has-this-number");
  await expect(page.getByText("No procedure matches these filters")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText("Clear the search or the filters to see the rest.")).toBeVisible();
});
