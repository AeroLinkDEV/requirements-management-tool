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
  await openNavigationGroup(page, "VERIFICATION");
  await page.getByRole("link", { name: "System Verification" }).click();
  await expect(page.getByRole("heading", { name: "Verification & Evidence" })).toBeVisible({ timeout: 30_000 });

  await page.getByRole("button", { name: /Test procedures/ }).click();
  const rows = page.locator(".procedureRow");
  await expect(rows.first()).toBeVisible();

  await expect(rows.first().getByText("Ethan Brooks")).toBeVisible();
  // The account stays reachable for anyone reconciling against the identity provider, just not as the label.
  await expect(rows.first().locator(".personName")).toHaveAttribute("title", "test.author");
  await expect(page.locator(".procedureRow", { hasText: "authored by test.author" })).toHaveCount(0);
});
