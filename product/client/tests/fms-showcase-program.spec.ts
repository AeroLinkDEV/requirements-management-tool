import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login } from "./auth";

test("FMS 1.5 released baseline supports active 1.6 work and full lifecycle exploration", async ({
  page,
  request,
}) => {
  test.setTimeout(60_000);
  await apiLogin(request);
  const seed = await request.post(`${apiBase}/api/showcase/seed`, {
    timeout: 45_000,
  });
  expect(seed.ok(), await seed.text()).toBeTruthy();
  await login(page);
  await page
    .locator(".program > select:not(.releaseSelector)")
    .selectOption({ label: "Flight Management System Live Program" });
  await expect(page.getByText("FMS Product Development", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Active release")).toHaveValue(/.+/);
  await expect(
    page.getByText("Complete FMS lifecycle inventory"),
  ).toBeVisible();
  await expect(page.getByText("1,100", { exact: true })).toBeVisible();
  await expect(page.getByText("515", { exact: true })).toBeVisible();
  await expect(page.getByText("520", { exact: true })).toBeVisible();
  await expect(
    page.getByText("Total SCRs").locator("..").locator("strong"),
  ).toBeVisible();

  await page.getByRole("link", { name: "HLR & LLR Requirements" }).click();
  await expect(
    page.getByRole("heading", { name: "Requirements Workspace" }),
  ).toBeVisible();
  await page.getByLabel("Search requirements").fill("LLR-00000700");
  await expect(page.getByText(/LLR-00000700/).first()).toBeVisible();
  await page.getByRole("link", { name: /Command Center/ }).first().click();
  await page.getByRole("link", { name: "Traceability & Outputs" }).click();
  await expect(
    page.getByRole("heading", { name: "Traceability & Documents" }),
  ).toBeVisible();
  await expect(page.getByText("1,250 requirements")).toBeVisible();
  await page.getByRole("button", { name: /Controlled Documents/ }).click();
  await expect(page.getByText("SYSRD-00000015.00")).toBeVisible();
  await expect(page.getByText("HLRD-00000015.00")).toBeVisible();
  await expect(page.getByText("LLRD-00000015.00")).toBeVisible();
  await page.getByRole("link", { name: /Command Center/ }).first().click();
  await page.getByRole("link", { name: "Release Campaign" }).click();
  await expect(
    page.getByRole("heading", { name: "FMS 1.6 Release Campaign" }),
  ).toBeVisible();
  await expect(page.getByText("release gates complete")).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Drive every blocker to evidence" }),
  ).toBeVisible();
  expect(await page.locator(".executionPanel.changes article").count()).toBeGreaterThanOrEqual(8);
  await expect(
    page.getByRole("heading", { name: "FMS 1.5 → 1.6" }),
  ).toBeVisible();
  await expect(page.getByText(/\d+ pending/)).toBeVisible();
  await expect(
    page
      .locator(".comparisonStats article")
      .filter({ hasText: "retired" })
      .locator("b"),
  ).toHaveText("0");
});
