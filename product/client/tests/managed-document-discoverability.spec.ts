import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, selectProgram, showcaseSeed } from "./auth";

/**
 * A project-wide managed document must be reachable from every place AeroLink says it exists - My Work,
 * the stable open resolver behind notification links, and global search - and must land on the canonical
 * Project-level Documentation Center record without any software build in the address.
 */
test("managed document work, notification links and search all open the exact project-wide record", async ({ page, request }) => {
  test.setTimeout(240_000);
  const showcase = await showcaseSeed(request);
  await apiLogin(request, "software.lead");

  // The showcase holds a document whose technical review is active for software.lead.
  const work = await (await request.get(`${apiBase}/api/my-work?projectId=${showcase.projectId}`)).json();
  const review = work.tasks.find((item: { type: string }) => item.type === "Project document review");
  expect(review, "the showcase must hold an active managed-document review for software.lead").toBeTruthy();
  const documentId = review.id as string;
  const displayNumber = review.artifact as string;

  // The stable resolver behind managed-document notification links resolves without a build.
  const resolved = await request.get(`${apiBase}/open/managed-document/${documentId}`, { maxRedirects: 0 });
  expect(resolved.status()).toBe(302);
  const location = new URL(resolved.headers()["location"], `${apiBase}/`).pathname;
  expect(location).toMatch(new RegExp(`/programs/[0-9a-f-]+/projects/[0-9a-f-]+/documentation-center/${documentId}$`));
  expect(location).not.toContain("/releases/");

  // A copied or emailed URL renders the exact record after a hard refresh, with no build required.
  await login(page, "software.lead", { openProject: false });
  await page.goto(location, { waitUntil: "load" });
  await expect(page.locator(".mdIdentity").getByText(new RegExp(displayNumber.replace(".", "\\.")))).toBeVisible({ timeout: 30_000 });
  await expect(page.locator(".contextReleaseState")).toHaveText("Project-wide");
  expect(page.url()).toContain(`/documentation-center/${documentId}`);
  expect(page.url()).not.toContain("/releases/");
  await page.reload({ waitUntil: "load" });
  await expect(page.locator(".mdIdentity").getByText(new RegExp(displayNumber.replace(".", "\\.")))).toBeVisible({ timeout: 30_000 });

  // My Work names the same record and its task opens the same canonical address.
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");
  await page.goto(new URL(`${root}/my-work`, page.url()).toString(), { waitUntil: "load" });
  await expect(page.getByRole("heading", { name: "My Work" })).toBeVisible({ timeout: 30_000 });
  const reviewTask = page.locator("article").filter({ hasText: "Project document review" }).first();
  await expect(reviewTask).toBeVisible();
  await reviewTask.click();
  await expect(page).toHaveURL(new RegExp(`/documentation-center/${documentId}$`), { timeout: 30_000 });
  await expect(page.locator(".mdIdentity").getByText(new RegExp(displayNumber.replace(".", "\\.")))).toBeVisible();

  // Global search distinguishes the managed record and opens the same address from inside a build.
  await page.getByRole("button", { name: /Search & navigate/ }).click();
  const palette = page.getByRole("dialog", { name: "Quick navigation" });
  await palette.getByLabel("Search AeroLink").fill(displayNumber);
  await palette.getByRole("link", { name: new RegExp(displayNumber.replace(".", "\\.")) }).first().click();
  await expect(page).toHaveURL(new RegExp(`/documentation-center/${documentId}$`), { timeout: 30_000 });
});
