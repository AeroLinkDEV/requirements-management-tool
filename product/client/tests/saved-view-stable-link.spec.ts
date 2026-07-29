import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, selectProgram } from "./auth";

/**
 * "Copy stable link" produced `{enterprise control path}?enterpriseView={id}`. Nothing reads
 * `enterpriseView`, and the router resolves `?view=` only on the Requirements routes, so the advertised
 * stable link reopened Enterprise Control and applied nothing.
 *
 * The existing coverage asserted the button was visible. This follows the link it produces to the workspace
 * it claims to open, which is the only assertion that could have caught it.
 *
 * The view is created through the API rather than the save dialog: the dialog is not what broke, and driving
 * it here would make a link test fail for reasons that have nothing to do with links.
 */
test("the copied saved-view link reopens the view and applies its filters", async ({ page, request }) => {
  test.setTimeout(180_000);
  const viewName = `Stable link view ${Date.now()}`;
  const search = "SYSR-000123";

  await apiLogin(request);
  await page.context().grantPermissions(["clipboard-write"]);
  await login(page);
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");

  const workspaces = await (await page.request.get(`${apiBase}/api/workspaces`)).json();
  const fms = workspaces.find((x: { program: { name: string } }) => x.program.name === "Flight Management System Live Program");
  const projectId = fms.projects[0].project.id;

  const created = await page.request.post(`${apiBase}/api/enterprise-requirements/views`, {
    data: {
      projectId,
      name: viewName,
      isShared: true,
      queryJson: JSON.stringify({ search, level: "System", sort: "identifier" }),
      columnsJson: '["identifier","statement","level"]',
    },
  });
  expect(created.ok(), "the saved view must be created before its link can be followed").toBe(true);

  await page.goto(new URL(root + "/enterprise-control", page.url()).toString(), { waitUntil: "load" });
  await page.getByRole("button", { name: /Query builder/ }).click();

  const row = page.locator("article", { hasText: viewName }).first();
  await expect(row).toBeVisible({ timeout: 20_000 });
  await row.getByRole("button", { name: "Copy stable link" }).click();

  // The confirmation states the exact URL, so the test follows what a reader would paste.
  const confirmation = page.getByText(/Stable view link copied: /);
  await expect(confirmation).toBeVisible();
  // The banner wraps the URL in a tick and a dismiss glyph, so take the address rather than the line.
  const copied = (((await confirmation.textContent()) ?? "").match(/https?:\/\/\S*?view=[0-9a-f-]{36}/i) ?? [""])[0];
  expect(copied, "the link must address the Requirements route the router can resolve").toContain("/systems/requirements?view=");

  await page.goto(copied, { waitUntil: "load" });
  await expect(page.getByLabel("Search requirements")).toHaveValue(search, { timeout: 30_000 });

  // A stable link is handed to somebody else, so the case that matters is the one where the reader has no
  // session yet: sign in from the link and still arrive at the view it names, not at a dashboard.
  // Signed in on the link itself rather than through the helper, which navigates to the root first and would
  // discard the very thing under test.
  await page.context().clearCookies();
  await page.goto(copied, { waitUntil: "load" });
  await page.getByLabel("Username").fill("admin");
  await page.getByLabel("Password").fill("AeroLink!2026");
  await page.getByRole("button", { name: /Sign in securely/ }).click();
  await expect(page.getByLabel("Search requirements")).toHaveValue(search, { timeout: 30_000 });
});
