import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("Lifecycle Decision Room connects readiness, impact, evidence, people, and compact identifiers", async ({ page, request }) => {
  test.setTimeout(90_000);
  await page.setViewportSize({ width: 1600, height: 1000 });
  await apiLogin(request);
  await login(page);
  await selectProgram(page,"Flight Management System Live Program");

  await openNavigationGroup(page, "RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Lifecycle Decision Room" }).click();
  await expect(page.getByRole("heading", { name: "Release Readiness" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Release lifecycle" })).toBeVisible();
  await expect(page.getByRole("img", { name: /Maya Patel, Systems Lead/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /Explore changes vs 1\.5/ })).toBeVisible();

  await page.getByRole("button", { name: "Explore baseline changes →" }).click();
  await expect(page.getByRole("heading", { name: "Change Impact Review" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Lifecycle impact" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Requirement revision preview" })).toBeVisible();
  await expect(page.getByText(/SYSR-\d{6}\.\d{2}/).first()).toBeVisible();
  await expect(page.getByRole("img", { name: /Maya Patel, Systems Lead/ })).toBeVisible();

  await page.getByRole("button", { name: "← Back to readiness" }).click();
  await page.getByRole("button", { name: "Review release evidence" }).click();
  await expect(page.getByRole("heading", { name: "Release Evidence & Decision" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Evidence checklist" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Approval path" })).toBeVisible();
  await expect(page.getByRole("img", { name: /Ethan Brooks, Verification Lead/ })).toBeVisible();
  await expect(page.getByRole("img", { name: /Olivia Chen, Program Manager/ })).toBeVisible();
  await expect(page.getByRole("img", { name: /Daniel Reyes, Release Manager/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /SYSRD-\d{6}\.\d{2}/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /TRACE-\d{6}\.\d{2}/ })).toBeVisible();
});
