import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login } from "./auth";

test("enterprise workspace supports discovery, collaboration, saved views, bulk governance, and import preview", async ({
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
  await page.getByRole("link", { name: "System Requirements" }).click();
  await expect(
    page.getByRole("heading", { name: "Requirements Workspace" }),
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
  await page.getByRole("button", { name: "×" }).click();
  await page.getByRole("button", { name: "☆ Save view" }).click();
  await page.getByLabel("View name").fill(viewName);
  await page.getByRole("button", { name: "Save view", exact: true }).click();
  await expect(page.getByText(viewName)).toBeVisible();
  await page.getByLabel("Search requirements").fill("SYSR-000001");
  await page
    .locator('.reqTable article input[type="checkbox"]')
    .first()
    .check();
  await page.locator('.reqTable article input[type="checkbox"]').nth(1).check();
  await page.getByPlaceholder("Classification tag").fill("Coverage Review");
  await page.getByRole("button", { name: "Preview bulk change" }).click();
  await expect(
    page.getByRole("heading", { name: "Review before commit" }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Commit attributable change" })
    .click();
  await expect(page.getByText("Coverage Review").first()).toBeVisible();
  await page.getByRole("button", { name: "⚙ Schemas" }).click();
  await expect(
    page.getByRole("heading", { name: "Artifact schemas" }),
  ).toBeVisible();
  await expect(
    page.getByText("High-Level Software Requirement", { exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Done" }).click();
  await page.getByRole("button", { name: "⇧ Import requirements" }).click();
  await page
    .getByLabel("Requirements import file")
    .setInputFiles({
      name: "requirements.csv",
      mimeType: "text/csv",
      buffer: Buffer.from(
        "Identifier,Level,Statement,Rationale,VerificationMethod\nHLR-00999999,HighLevel,The FMS software shall retain a governed import preview.,Enterprise onboarding,Test",
      ),
    });
  await page.getByRole("button", { name: "Validate & preview" }).click();
  await expect(
    page.locator(".importIdentity").getByText("1", { exact: true }),
  ).toBeVisible();
  await expect(
    page.locator(".importIdentity").getByText("valid", { exact: true }),
  ).toBeVisible();
  await expect(page.getByText("HLR-00999999")).toBeVisible();
});
