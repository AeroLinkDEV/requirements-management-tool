import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

/**
 * Test Procedures rendered all 75 rows as "authored by test.author". The existing people-not-accounts guard
 * visits each primary route and scans only what is visible on arrival, and this tab is one across — so a
 * surface with 75 offending rows sat behind a passing test.
 *
 * The tab is opened here rather than trusted to be visible, which is the whole point of the gap.
 */
test("test procedure authorship names a person rather than the account that signed in", async ({
  page,
  request,
}) => {
  test.setTimeout(180_000);
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "ASSURANCE");
  // Asked of the Test Procedure Explorer, which is where a procedure is read. The change request page used to
  // carry a procedure library and this was asked of that; the library moved rather than the question.
  await page.getByRole("link", { name: "System Test Procedure Explorer" }).click();
  await expect(page.getByRole("heading", { name: "Test Procedure Explorer" })).toBeVisible({ timeout: 30_000 });

  await page.getByLabel("Find a procedure").fill("SYSTP-000001");
  const row = page.locator(".procedureRow").filter({ hasText: "SYSTP-000001" }).first();
  await expect(row).toBeVisible({ timeout: 30_000 });
  await row.click();

  const inspector = page.locator(".requirementInspector");
  await expect(inspector).toBeVisible({ timeout: 30_000 });
  await expect(inspector.getByText("Ethan Brooks")).toBeVisible();
  // The account stays reachable for anyone reconciling against the identity provider, just not as the label.
  await expect(inspector.locator(".personName").first()).toHaveAttribute("title", "test.author");
  await expect(inspector).not.toContainText("test.author");
});
