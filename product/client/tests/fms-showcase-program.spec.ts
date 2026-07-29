import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("FMS 1.5 released baseline supports active 1.6 work and full lifecycle exploration", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  await apiLogin(request);
  await login(page);
  await selectProgram(page,"Flight Management System Live Program");
  await expect(page.getByText("FMS Product Development", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Active build 1.6")).toContainText("In work");
  await expect(page.locator(".programInventory")).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "System change control" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Software change control" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Change triage" })).toBeVisible();
  await expect(page.getByText("Release attention")).toHaveCount(0);
  await expect(page.getByText("Change request flow")).toHaveCount(0);

  await openNavigationGroup(page,"SOFTWARE ENGINEERING");
  await page.getByRole("link", { name: "Software Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "Software Requirements Explorer" }),
  ).toBeVisible();
  await page.getByLabel("Search requirements").fill("LLR-000700");
  await expect(page.getByText(/LLR-000700/).first()).toBeVisible();
  await page.getByRole("link", { name: /Command Center/ }).first().click();
  await openNavigationGroup(page,"VERIFICATION");
  await page.getByRole("link", { name: "Traceability & Outputs" }).click();
  await expect(
    page.getByRole("heading", { name: "Digital Thread" }),
  ).toBeVisible();
  await expect(page.getByText("1,250 requirements")).toBeVisible();
  await page.getByRole("button", { name: /Controlled Documents/ }).click();
  // The inherited 1.5 requirements remain visible as labelled evidence, but their documents are not
  // presented as current 1.6 outputs.
  await expect(page.getByText("No outputs for this baseline")).toBeVisible();
  await expect(page.getByText("SYSRD-000015.00")).toHaveCount(0);
  await page.getByRole("link", { name: /Command Center/ }).first().click();
  await openNavigationGroup(page,"RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Lifecycle Decision Room" }).click();
  await page.getByRole("button", { name: "Open exact work →" }).first().click();
  await expect(
    page.getByRole("heading", { name: "FMS 1.6 Release Campaign" }),
  ).toBeVisible();
  await expect(page.getByText("release gates complete")).toBeVisible();
  await page.getByRole("button", { name: /Open release workbench/ }).click();
  await expect(page.getByRole("heading", { name: "Drive every blocker to evidence" })).toBeVisible();
  expect(await page.locator(".executionPanel.changes article").count()).toBeGreaterThanOrEqual(8);
  await expect(page.getByLabel("Active build 1.6")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Drive every blocker to evidence" })).toBeVisible();
});
