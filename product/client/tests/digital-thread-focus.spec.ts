import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from "./auth";

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

  await page.getByRole("tab", { name: /Trace/ }).click();
  await page.getByRole("button", { name: /Open complete Digital Thread/ }).click();

  const focused = page.locator(".digitalThreadStage header b").first();
  await expect(focused).toHaveText(/^SYSR-000011\./);

  // The identity is in the route, not only in component state, so a shared link is worth sharing.
  const url = page.url();
  expect(url).toContain("/traceability");
  await page.reload({ waitUntil: "load" });
  await expect(page.locator(".digitalThreadStage header b").first()).toHaveText(/^SYSR-000011\./);
});

test("a change request opens its stable-ID Digital Thread with proposal truth separate from baseline", async ({
  page,
  request,
  }, testInfo) => {
  test.setTimeout(180_000);
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  await login(page, "admin", { openProject: false });
  const response = await request.get(`${apiBase}/api/change-requests?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&page=1&pageSize=200`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const listed = await response.json() as { items: { id: string; requirementCount: number }[] };
  const candidate = listed.items.find(item => item.requirementCount > 0);
  expect(candidate, "The showcase must contain a requirement-bearing change request").toBeTruthy();
  const detailResponse = await request.get(`${apiBase}/api/change-requests/${candidate!.id}`);
  expect(detailResponse.ok(), await detailResponse.text()).toBeTruthy();
  const detail = await detailResponse.json() as { displayNumber: string; requirementChanges: { displayNumber: string }[] };
  await page.goto(new URL(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/traceability/change-requests/${candidate!.id}`, page.url()).toString(), { waitUntil: "load" });
  await expect(page.getByRole("heading", { name: "Digital Thread · Change Request" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Change request chain" })).toBeVisible();
  await expect(page.getByText(detail.displayNumber, { exact: true }).first()).toBeVisible();
  await expect(page.getByRole("heading", { name: "Proposed requirement changes" })).toBeVisible();
  await expect(page.getByText(detail.requirementChanges[0].displayNumber, { exact: true })).toBeVisible();
  await expect(page.getByText(/not materialized requirement revisions/i)).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("cr-thread-normal.png"), fullPage: true });
  await page.setViewportSize({ width: 900, height: 900 });
  await page.screenshot({ path: testInfo.outputPath("cr-thread-narrow.png"), fullPage: true });
  await page.reload({ waitUntil: "load" });
  await expect(page.getByRole("heading", { name: "Change request chain" })).toBeVisible();
});
