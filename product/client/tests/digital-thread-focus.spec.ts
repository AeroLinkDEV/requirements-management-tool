import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from "./auth";

/**
 * Entering the Digital Thread from somewhere else, and what must survive the journey.
 *
 * These predate #880 and asserted against the page it replaced — the fixed lifecycle-path strip, the stacked
 * layer boxes, the `/trace` and `/traceability/path` reads that backed them. The page is gone; the behaviours
 * they were protecting are not, so they are rewritten against the replacement rather than dropped:
 *
 * - `Open Digital Thread` lands on the exact record the reader left, and it survives a reload (§4.4, §6.4).
 *   The original defect was navigating to the thread and focusing whichever requirement happened to load
 *   first, so an engineer arriving from SYSR-000011 was reading a different record than the one they left.
 * - A change request lands in the change network, in context, rather than inside itself (§4.4).
 * - A focal artifact this build does not contain fails closed rather than showing unrelated content.
 * - A failed read keeps the canvas frame and offers a retry rather than discarding the view (§6.8).
 */

test("opening the Digital Thread from a requirement focuses that requirement and survives reload", async ({
  page,
  request,
}) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  await showcaseSeed(request);
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

  await page.getByRole("tab", { name: /Trace/ }).click();
  await page.getByRole("button", { name: /Open complete Digital Thread/ }).click();

  // A requirement opens the artifact thread, landing on that exact revision selected and expanded — as
  // though the reader had clicked the card themselves.
  await expect(page.locator(".dtaRoot")).toBeVisible();
  const focal = page.locator(".dtaCard.is-focal");
  await expect(focal.locator(".dtaId")).toHaveText(/^SYSR-000011\./);
  await expect(focal).toHaveClass(/is-selected/);

  // The identity is in the route, not only in component state, so a shared link is worth sharing.
  const url = page.url();
  expect(url).toContain("/traceability");
  await page.reload({ waitUntil: "load" });
  await expect(page.locator(".dtaCard.is-focal .dtaId")).toHaveText(/^SYSR-000011\./);
  expect(page.url()).toBe(url);
});

test("a change request opens in the change network, in context rather than inside itself", async ({
  page,
  request,
}) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  await login(page, "admin", { openProject: false });

  const response = await request.get(`${apiBase}/api/change-requests?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&page=1&pageSize=5`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const listed = await response.json() as { items: { id: string; displayNumber: string }[] };
  const change = listed.items[0];

  await page.goto(new URL(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/traceability/change-requests/${change.id}`, page.url()).toString(), { waitUntil: "load" });

  // §4.4 lands a change request on the network deliberately: the point of arriving is to see the change in
  // the context of everything around it, with `Open this change` one click away.
  await expect(page.locator(".dtnRoot")).toBeVisible();
  await expect(page.locator(".dtPageViews button[aria-pressed='true']")).toHaveText("Change network");

  // Landing is arrival, not merely presence: the named card is already the selected one, with its panel open,
  // exactly as if the reader had clicked it (§4.4). Nothing is clicked before this is asserted.
  await expect(page.locator(".dtnCard.is-selected")).toHaveCount(1);
  await expect(page.locator(".dtnCard.is-selected")).toContainText(change.displayNumber);

  // And `Open this change` is the one click away that §4.4 promises.
  await page.locator(".dtnPanel").getByRole("button", { name: "Open this change" }).click();
  await expect(page.locator(".dticRoot")).toBeVisible();
  expect(page.url()).toContain("view=inside");
});

test("a focal change request this build does not contain fails closed", async ({ page, request }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  await login(page, "admin", { openProject: false });

  // An identifier that is well-formed and belongs to nothing in this build. The page must not present a
  // thread for it, and must not quietly show some other record's instead.
  const absent = "99999999-9999-4999-8999-999999999999";
  await page.goto(new URL(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/traceability/change-requests/${absent}`, page.url()).toString(), { waitUntil: "load" });

  await expect(page.locator(".dtnRoot")).toBeVisible();
  // Nothing is selected, because the named record is not in this build's network — rather than the board
  // silently selecting a different change request.
  await expect(page.locator(".dtnCard.is-selected")).toHaveCount(0);
  await expect(page.locator(".dtaCard")).toHaveCount(0);
});

test("a failed read keeps the canvas frame and offers a retry", async ({ page, request }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  await login(page, "admin", { openProject: false });

  // The network read is what the change network is built from. #880 §6.8 requires the failure to render
  // inside the frame with a retry, rather than replacing the canvas and discarding the reader's view.
  await page.route("**/api/change-requests/network?*", route =>
    route.fulfill({ status: 500, json: { error: "unavailable" } }));

  await page.goto(new URL(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/traceability`, page.url()).toString(), { waitUntil: "load" });

  await expect(page.getByRole("alert")).toContainText(/could not be loaded/i);
  await expect(page.getByRole("button", { name: "Try again" })).toBeVisible();
  // The frame is still mounted behind the message.
  await expect(page.locator(".dtCanvas")).toBeVisible();
});
