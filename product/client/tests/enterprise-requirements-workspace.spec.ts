import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup } from "./auth";

test("requirements stay read-only while controlled proposals and imports move into Changes", async ({
  page,
  request,
}) => {
  test.setTimeout(60_000);
  const viewName = `System requirement 150 review ${Date.now()}`;
  const commentText = `Please confirm coverage with @test.engineer before baseline ${Date.now()}.`;
  await apiLogin(request);
  const seed = await request.post(`${apiBase}/api/showcase/seed`, {
    timeout: 45_000,
  });
  expect(seed.ok(), await seed.text()).toBeTruthy();
  await login(page);
  await page
    .locator(".program > select:not(.releaseSelector)")
    .selectOption({ label: "Flight Management System Live Program" });
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible();
  await expect(page.getByText("150 requirements")).toBeVisible();
  await page.getByLabel("Search requirements").fill("SYSR-00000150");
  await expect(page.getByText(/SYSR-00000150\.\d{2}/)).toBeVisible();
  await page.getByText(/SYSR-00000150\.\d{2}/).click();
  await expect(page.getByText("Controlled revision")).toBeVisible();
  await page.getByRole("button", { name: /Discussion/ }).click();
  await page
    .getByPlaceholder(
      "Add an attributable comment. Use @username to mention someone.",
    )
    .fill(commentText);
  await page.getByRole("button", { name: "Add comment" }).click();
  await expect(page.getByText(commentText)).toBeVisible();
  await page.getByRole("button", { name: "Close requirement inspector" }).click();
  await page.getByText("Workspace tools", { exact: true }).click();
  await page.getByRole("button", { name: "☆ Save view" }).click();
  await page.getByLabel("View name").fill(viewName);
  await page.getByRole("button", { name: "Save view", exact: true }).click();
  if ((await page.locator('.savedViews').getAttribute('open')) === null)
    await page.locator('.savedViews > summary').click();
  await expect(page.getByText(viewName)).toBeVisible();
  if ((await page.locator('.pageActionsMenu').getAttribute('open')) === null)
    await page.getByText("Workspace tools", { exact: true }).click();
  await page.getByRole("button", { name: /Schemas/ }).click();
  await expect(page.getByRole("heading", { name: "Artifact schemas" })).toBeVisible();
  await expect(page.getByText("High-Level Software Requirement", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Done" }).click();
  await page.getByLabel("Search requirements").fill("SYSR-00000150");
  await page.getByRole("button", { name: /SYSR-00000150\.\d{2}/ }).first().click();
  await page.getByRole("button", { name: "Trace & impact" }).click();
  await expect(page.getByRole("button", { name: "Open complete Digital Thread →" })).toBeVisible();
  await page.getByRole("button", { name: "Overview" }).click();
  await page.getByRole("button", { name: "Propose controlled change →" }).click();
  await expect(page.getByRole("heading", { name: "Create System Change Request" })).toBeVisible();
  await expect(page.getByText("Started from Requirements Explorer")).toBeVisible();
  await expect(page.locator('input[value*="SYSR-00000150"]').first()).toBeVisible();
  await page.getByRole("button", { name: "Import into Draft SCR" }).click();
  await page
    .getByLabel("Requirements import file")
    .setInputFiles({
      name: "requirements.csv",
      mimeType: "text/csv",
      buffer: Buffer.from(
        "Identifier,Level,Statement,Rationale,VerificationMethod\nHLR-00999999,HighLevel,The FMS software shall retain a governed import preview.,Enterprise onboarding,Test",
      ),
    });
  await page.getByRole("button", { name: "Validate and preview" }).click();
  await expect(
    page.locator(".changeImportIdentity").getByText("1", { exact: true }),
  ).toBeVisible();
  await expect(
    page.locator(".changeImportIdentity").getByText("valid", { exact: true }),
  ).toBeVisible();
  await expect(page.getByText("HLR-00999999")).toBeVisible();
});
