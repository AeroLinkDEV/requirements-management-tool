import { expect, test } from "@playwright/test";
import { login } from "./auth";

test("an administrator can save and resume a project setup draft", async ({ page }) => {
  await login(page, "admin", { openProject: false });
  await page.goto("/projects/new");
  await expect(page.getByRole("heading", { name: "Create New Project", level: 1 })).toBeVisible();

  const projectName = `Recoverable UI ${Date.now()}`;
  await page.getByLabel("Project name").fill(projectName);
  await page.getByLabel("Software product").fill("Recoverable UI Software");
  await page.getByRole("button", { name: "Save and exit" }).click();
  await expect(page).toHaveURL(/\/projects$/);
  const draft = page.locator("[data-setup-draft-id]").filter({ hasText: projectName });
  await expect(draft).toBeVisible();
  await draft.getByRole("button", { name: "Resume setup" }).click();
  await expect(page).toHaveURL(/\/projects\/setup\/[0-9a-f-]+$/);
  await expect(page.getByLabel("Project name")).toHaveValue(projectName);
  await expect(page.getByText("Saved", { exact: false }).first()).toBeVisible();
});

test("a fresh setup finalizes into one In Work build and returns to its lineage selector", async ({
  page,
}) => {
  await login(page, "admin", { openProject: false });
  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(`Fresh UI ${Date.now()}`);
  await page.getByLabel("Software product").fill("Fresh UI Software");
  await page.getByRole("button", { name: "Continue" }).click();

  await page.getByLabel("Fresh project").check();
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Version").fill("1.02");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  await page.getByLabel(/explicitly accept these concrete review and approval rules/i).check();
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  // Continue saved the accepted definition before arriving at Review; no unsaved edit remains here.
  await expect(page.getByRole("button", { name: "Save review" })).toBeDisabled();
  await expect(page.getByText("Saved on the server", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Create Project" }).click();

  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]+\/builds$/);
  await expect(page.getByRole("heading", { name: "Software Builds", level: 1 })).toBeVisible();
  await expect(page.locator("[data-build-card]")).toHaveCount(1);
  await expect(
    page.locator("[data-build-card]").getByText("In Work", { exact: true }),
  ).toBeVisible();
  await expect(
    page.locator("[data-build-card]").getByText("SW-01.02", { exact: true }),
  ).toBeVisible();
});
