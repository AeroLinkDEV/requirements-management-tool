import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("Lifecycle Decision Room connects readiness, impact, evidence, people, and compact identifiers", async ({ page, request }) => {
  test.setTimeout(90_000);
  await page.setViewportSize({ width: 1600, height: 1000 });
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");

  await openNavigationGroup(page, "RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Lifecycle Decision Room" }).click();
  await expect(page.getByRole("heading", { name: "Release Readiness" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Release lifecycle" })).toBeVisible();
  // The attention panel must show what the server computed, not organisational facts invented in the
  // browser. It previously rendered an owner, a due date and a priority keyed by position in the list;
  // this assertion used to require that fabrication to be present.
  await expect(page.getByText("To clear this").first()).toBeVisible();
  await expect(page.getByText("Remaining").first()).toBeVisible();
  await expect(page.getByText("Waiting for a materialized baseline", { exact: false })).toHaveCount(4);
  await expect(page.getByText("Not evaluated", { exact: true })).toHaveCount(4);
  await expect(page.getByRole("button", { name: /Open prerequisite/ })).toHaveCount(4);
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
