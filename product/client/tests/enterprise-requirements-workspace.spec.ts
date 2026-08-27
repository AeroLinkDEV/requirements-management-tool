import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("requirements explorer shows truthful access-aware counts", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible();
  await expect(page.getByRole("status", {
    name: /Loading controlled requirements/,
  })).toBeHidden();
  await expect(page.getByText("150 requirements")).toBeVisible();
  await expect(page.locator(".confidence")).toContainText("Live counts · respects your access");
});

test("requirements stay read-only while controlled proposals and imports move into Changes", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  const commentText = `Please confirm coverage with @test.engineer before baseline ${Date.now()}.`;
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible();

  const loadingState = page.getByRole("status", {
    name: /Loading controlled requirements/,
  });
  await expect(loadingState).toBeHidden();
  await expect(page.getByText("150 requirements")).toBeVisible();

  await page.getByLabel("Search requirements").fill("SYSR-000150");
  await expect(page.getByText(/SYSR-000150\.\d{2}/)).toBeVisible();
  await page.getByText(/SYSR-000150\.\d{2}/).click();
  await expect(page.getByText("Controlled revision")).toHaveCount(0);
  await page.getByRole("button", { name: /Discussion/ }).click();
  await page
    .getByPlaceholder(
      "Add an attributable comment. Use @username to mention someone.",
    )
    .fill(commentText);
  await page.getByRole("button", { name: "Add comment" }).click();
  await expect(page.getByText(commentText)).toBeVisible();
  await page.getByRole("button", { name: "Close requirement inspector" }).click();
  await expect(page.getByText("Authoritative view", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Workspace tools", { exact: true })).toHaveCount(0);
  await page.getByLabel("Search requirements").fill("SYSR-000150");
  await page.getByRole("button", { name: /SYSR-000150\.\d{2}/ }).first().click();
  await page.getByRole("button", { name: "Trace & impact" }).click();
  await expect(page.getByRole("button", { name: "Open complete Digital Thread →" })).toBeVisible();
  await page.getByRole("button", { name: "Overview" }).click();
  await page.getByRole("button", { name: "Propose controlled change →" }).click();
  await expect(page.getByRole("heading", { name: "Create System Change Request" })).toBeVisible();
  await expect(page.getByText("Started from Requirements Explorer")).toBeVisible();
  await expect(page.locator('input[value*="SYSR-000150"]').first()).toBeVisible();
  await page.getByRole("button", { name: "Import into Draft SRCR" }).click();
  await page
    .getByLabel("Requirements import file")
    .setInputFiles({
      name: "requirements.csv",
      mimeType: "text/csv",
      buffer: Buffer.from(
        "Identifier,Level,Statement,Rationale,VerificationMethod\nHLR-999999,HighLevel,The FMS software shall retain a governed import preview.,Enterprise onboarding,Test",
      ),
    });
  await page.getByRole("button", { name: "Validate and preview" }).click();
  await expect(
    page.locator(".changeImportIdentity").getByText("1", { exact: true }),
  ).toBeVisible();
  await expect(
    page.locator(".changeImportIdentity").getByText("valid", { exact: true }),
  ).toBeVisible();
  await expect(page.getByText("HLR-999999")).toBeVisible();
});
