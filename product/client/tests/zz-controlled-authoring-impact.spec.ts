import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("engineer analyzes impact and creates a rich controlled requirement proposal", async ({
  page,
  request,
}) => {
  test.setTimeout(75_000);
  await apiLogin(request);
  await login(page);
  await selectProgram(page,"Flight Management System Live Program");
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible();
  await page.getByLabel("Search requirements").fill("SYSR-000150");
  await page.getByRole("button", { name: /SYSR-000150\.\d{2}/ }).first().click();
  await page.getByRole("button", { name: "Trace & impact" }).click();
  await expect(page.getByRole("heading", { name: "Verification coverage" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Open complete Digital Thread →" })).toBeVisible();
  await page.getByRole("button", { name: "Overview" }).click();
  await page
    .getByRole("button", { name: "Propose controlled change →" })
    .click();
  await expect(page.getByText("Started from Requirements Explorer")).toBeVisible();
  await expect(page.locator('input[value*="SYSR-000150"]').first()).toBeVisible();
  await page.getByLabel("Problem").fill("The selected controlled behavior requires an attributable update.");
  await page.getByRole("textbox", { name: "Analysis", exact: true }).fill("The requirement, trace, verification, and document impacts will be dispositioned in this change.");
  await page.getByLabel("Solution").fill("Create the proposed successor revision without altering the authoritative requirement directly.");
  await page.getByRole("button", { name: "Save SCR Draft" }).click();
  await expect(
    page.getByRole("heading", { name: "Change case" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Check out & edit" }).click();
  await expect(
    page.getByRole("heading", { name: "Controlled requirement authoring" }),
  ).toBeVisible();
  await page
    .locator(".richEditor")
    .fill(
      "**Controlled update**\n- preserve deterministic sequencing\n- verify the affected route mode\n\n[[SYSR-000150]]",
    );
  const dispositions = page.locator(".editorColumns aside select");
  expect(await dispositions.count()).toBe(5);
  for (let i = 0; i < 5; i++)
    await dispositions.nth(i).selectOption("Affected");
  await page.getByRole("button", { name: "Save & check in" }).click();
  await expect(page.getByText("Requirement impact")).toBeVisible();
  await expect(page.getByText(/Record version/)).toBeVisible();
});
