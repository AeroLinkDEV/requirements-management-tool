import { expect, test, type Route } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from "./auth";

/**
 * Comment state in the Requirements Explorer has exactly one legitimate owner at a time: the latest
 * read or mutation issued for the requirement the inspector is actually showing. Three defects share
 * that rule, and #805 was the first of them to be observed: open() selects a requirement before its
 * responses arrive, so the comment form can be used while that requirement's own earlier comment read
 * is still in flight — and when a successful POST's fresh read lands, the stale pre-create read can
 * still overwrite it afterwards, returning the inspector to "Discussion 0" while the server holds the
 * comment.
 *
 * These tests hold the stale responses on the wire and release them on command, so the ordering is
 * deterministic: the old code fails these on demand, the corrected code passes them on demand.
 */

const selectRequirement = async (page: import("@playwright/test").Page, number: string) => {
  await page.getByLabel("Search requirements").fill(number);
  await expect(page.getByText(new RegExp(`${number}\\.\\d{2}`)).first()).toBeVisible({ timeout: 30_000 });
  await page.getByText(new RegExp(`${number}\\.\\d{2}`)).first().click();
};

const openExplorer = async (page: import("@playwright/test").Page) => {
  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden();
};

/**
 * Holds the comments GET for ONE specific requirement until released on command, so a test can decide
 * exactly which stale response arrives late. The returned `held` promise resolves with the intercepted
 * URL once that read is on the wire, proving the hold is real before the test proceeds — a hold that
 * never captured anything would let the leak assertions pass vacuously.
 */
const holdRequirementCommentRead = async (page: import("@playwright/test").Page, requirementId: string) => {
  let release!: () => void;
  let capture!: (url: string) => void;
  const held = new Promise<string>((resolve) => { capture = resolve; });
  const releasePromise = new Promise<void>((resolve) => { release = resolve; });
  let captured = false;
  await page.route("**/api/enterprise-requirements/*/comments", async (route: Route) => {
    // Mutations and every other read pass through untouched; only the pinned requirement's first read
    // is frozen in time, with its body captured before anything else can change it.
    if (route.request().method() !== "GET" || captured || !route.request().url().includes(`/${requirementId}/comments`)) {
      return route.fallback();
    }
    captured = true;
    capture(route.request().url());
    const response = await route.fetch();
    await releasePromise;
    await route.fulfill({ response });
  });
  return { release: () => release(), held };
};

test("a successful comment stays visible after creation and across a reselect", async ({ page, request }) => {
  test.setTimeout(180_000);
  await apiLogin(request);
  const stamp = Date.now();
  const commentText = `Coverage confirmed against the released baseline ${stamp}.`;

  await openExplorer(page);
  await selectRequirement(page, "SYSR-000150");
  await page.getByRole("tab", { name: /Discussion/ }).click();
  await page
    .getByPlaceholder("Add an attributable comment. Use @username to mention someone.")
    .fill(commentText);
  await page.getByRole("button", { name: "Add comment" }).click();

  const comment = page.locator("article").filter({ hasText: commentText });
  await expect(comment).toBeVisible({ timeout: 30_000 });
  // Controlled attribution stays server-authoritative and rendered.
  await expect(comment.locator("b")).toHaveText("admin");
  await expect(page.getByRole("tab", { name: /Discussion/ })).toContainText("1");

  // The surrounding workspace settles — including reopening the inspector, which refetches everything.
  await page.getByRole("button", { name: "Close requirement inspector" }).click();
  await selectRequirement(page, "SYSR-000150");
  await page.getByRole("tab", { name: /Discussion/ }).click();
  await expect(comment).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("tab", { name: /Discussion/ })).toContainText("1");
});

test("an older in-flight comment read cannot overwrite a successful creation", async ({ page, request }) => {
  test.setTimeout(180_000);
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  const stamp = Date.now();
  const commentText = `Stale read must not erase this correction ${stamp}.`;

  // The explorer auto-opens the first requirement; freeze that read mid-flight, exactly the shape the
  // defect needs: a pre-create snapshot that resolves only after newer comment state has been shown.
  const listResponse = await request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/enterprise-requirements/workspace?projectId=${showcase.projectId}&level=System&page=1&pageSize=5&sort=identifier`);
  expect(listResponse.ok(), await listResponse.text()).toBeTruthy();
  const autoSelected = (await listResponse.json()).items[0];
  const { release: releaseStaleRead, held } = await holdRequirementCommentRead(page, autoSelected.id);
  await openExplorer(page);
  // Prove the hold is real before anything else — a hold that never captured would make the final
  // assertion pass vacuously.
  await expect(held).resolves.toContain(`/${autoSelected.id}/comments`);
  await selectRequirement(page, "SYSR-000150");

  await page.getByRole("tab", { name: /Discussion/ }).click();
  await page
    .getByPlaceholder("Add an attributable comment. Use @username to mention someone.")
    .fill(commentText);
  await page.getByRole("button", { name: "Add comment" }).click();

  // The post-create read is authoritative and has landed. The count is deliberately not asserted here:
  // this requirement is shared with other journeys, so only state this journey owns is assertable.
  const comment = page.locator("article").filter({ hasText: commentText });
  await expect(comment).toBeVisible({ timeout: 30_000 });
  await expect(comment).toHaveCount(1);

  // Now the pre-create read — a zero-comment snapshot for the auto-selected requirement — arrives.
  releaseStaleRead();
  await page.waitForTimeout(300);

  // The newer controlled truth survives it. This is the assertion #805 was about.
  await expect(comment).toBeVisible({ timeout: 30_000 });
  await expect(comment).toHaveCount(1);
});

test("a stale read for a previously selected requirement cannot leak onto the current one", async ({ page, request }) => {
  test.setTimeout(180_000);
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  const stamp = Date.now();
  const commentOnA = `Belongs exclusively to the first requirement ${stamp}.`;

  // Give the auto-selected requirement a real comment, then freeze its read mid-flight.
  const listResponse = await request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/enterprise-requirements/workspace?projectId=${showcase.projectId}&level=System&page=1&pageSize=5&sort=identifier`);
  expect(listResponse.ok(), await listResponse.text()).toBeTruthy();
  const firstRequirement = (await listResponse.json()).items[0];
  const detailResponse = await request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/enterprise-requirements/${firstRequirement.id}`);
  const revisionId = (await detailResponse.json()).history[0].id;
  const created = await request.post(`${process.env.AEROLINK_E2E_API_BASE}/api/enterprise-requirements/${firstRequirement.id}/comments`, {
    data: { revisionId, body: commentOnA, mentions: [] },
  });
  expect(created.ok(), await created.text()).toBeTruthy();

  const { release: releaseStaleRead, held } = await holdRequirementCommentRead(page, firstRequirement.id);
  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(page.getByRole("heading", { name: "System Requirements Explorer" })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden();
  // Prove the hold is real: the pinned requirement's read is the one frozen on the wire, so the leak
  // assertion cannot pass for the wrong reason.
  await expect(held).resolves.toContain(`/${firstRequirement.id}/comments`);
  // Let the auto-selected requirement's remaining fetches settle, so the only thing still in flight is
  // the held read this test controls.
  await page.waitForTimeout(500);

  // While the first requirement's comment read is held, move to a different requirement entirely.
  await selectRequirement(page, "SYSR-000002");
  await page.waitForTimeout(500);
  await page.getByRole("tab", { name: /Discussion/ }).click();
  await expect(page.getByRole("tab", { name: /Discussion/ })).toContainText("0", { timeout: 30_000 });

  // Release the first requirement's stale read and prove no cross-requirement leakage.
  releaseStaleRead();
  await page.waitForTimeout(300);
  await expect(page.getByText(commentOnA)).toHaveCount(0);
  await expect(page.getByRole("tab", { name: /Discussion/ })).toContainText("0");
});
