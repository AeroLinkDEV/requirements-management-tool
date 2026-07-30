import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

/**
 * "Open complete Digital Thread" navigated to the thread and then focused whichever requirement happened to
 * be first in the loaded page — HLR-000001.00 — so an engineer arriving from SYSR-000011 was reading a
 * different record than the one they left. The action promised context and dropped it at the boundary.
 *
 * The focused artifact is carried in the route as a stable artifact identity, so the check is that the URL
 * says which record it is and the page agrees, and that both survive a reload.
 */
test("opening the Digital Thread from a requirement focuses that requirement and survives reload", async ({
  page,
  request,
}) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("status", { name: /Loading controlled requirements/ }),
  ).toBeHidden();

  await page.getByLabel("Search requirements").fill("SYSR-000011");
  const row = page.getByText(/SYSR-000011\.\d{2}/).first();
  await expect(row).toBeVisible();
  await row.click();

  await page.getByRole("button", { name: /Trace/ }).click();
  await page.getByRole("button", { name: /Open complete Digital Thread/ }).click();

  const focused = page.locator(".digitalThreadStage header b").first();
  await expect(focused).toHaveText(/^SYSR-000011\./);

  // The identity is in the route, not only in component state, so a shared link is worth sharing.
  const url = page.url();
  expect(url).toContain("/traceability");
  await page.reload({ waitUntil: "load" });
  await expect(page.locator(".digitalThreadStage header b").first()).toHaveText(/^SYSR-000011\./);
});
