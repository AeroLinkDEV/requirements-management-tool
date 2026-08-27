import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("engineer analyzes impact and creates a rich controlled requirement proposal", async ({
  page,
  request,
}) => {
  test.setTimeout(75_000);
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible();
  await page.getByLabel("Search requirements").fill("SYSR-000150");
  const requirementResult = page
    .getByRole("button", { name: /SYSR-000150\.\d{2}/ })
    .first();
  const requirementInspector = page.getByRole("heading", {
    name: /SYSR-000150\.\d{2}/,
  });
  if (!(await requirementInspector.isVisible())) {
    await expect(requirementResult).toBeVisible();
    await requirementResult.click();
  }
  await expect(requirementInspector).toBeVisible();
  await page.getByRole("tab", { name: "Trace & impact" }).click();
  await expect(page.getByRole("heading", { name: "Verification coverage" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Open complete Digital Thread →" })).toBeVisible();
  await page.getByRole("tab", { name: "Overview" }).click();
  await page
    .getByRole("button", { name: "Propose controlled change →" })
    .click();
  await page.getByRole("dialog", { name: "Choose a change request" })
    .getByRole("button", { name: /Start a new Draft change request/ }).click();
  await expect(page.getByText("Started from Requirements Explorer")).toBeVisible();
  await expect(page.locator('input[value*="SYSR-000150"]').first()).toBeVisible();
  await page.getByLabel("Problem", { exact: true }).fill("The selected controlled behavior requires an attributable update.");
  await page.getByRole("textbox", { name: "Analysis", exact: true }).fill("The requirement, trace, verification, and document impacts will be dispositioned in this change.");
  await page.getByLabel("Solution").fill("Create the proposed successor revision without altering the authoritative requirement directly.");
  await page.getByRole("button", { name: "Save SRCR Draft" }).click();
  await expect(
    page.getByRole("heading", { name: "Change case" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Check out & edit" }).click();
  await expect(
    page.getByRole("heading", { name: "Controlled requirement authoring" }),
  ).toBeVisible();
  // Supporting content is structure, not markup: a paragraph and a table, authored the way an engineer
  // actually reaches them, and shown back as what the approver will read rather than as authored source.
  const supporting = page.locator(".supportingBody .richEditor");
  // Supporting content opens holding the statement as one paragraph, so there is something to edit rather
  // than an empty box the author has to work out how to start.
  await supporting
    .locator(".richParagraphBody")
    .fill("Preserve deterministic sequencing and verify the affected route mode.");
  await supporting.getByRole("button", { name: "Table", exact: true }).click();
  await supporting.getByLabel("Column 1 heading").fill("Mode");
  await supporting.getByLabel("Column 2 heading").fill("Sequencing");
  await supporting.getByLabel("Row 1, column 1").fill("Oceanic");
  await supporting.getByLabel("Row 1, column 2").fill("Round robin");

  const preview = page.locator(".controlledPreview");
  await expect(preview.getByRole("columnheader", { name: "Mode" })).toBeVisible();
  await expect(preview.getByRole("cell", { name: "Round robin" })).toBeVisible();

  await expect(page.getByText("Known downstream context", { exact: true })).toBeVisible();
  await expect(page.locator(".editorColumns aside select")).toHaveCount(0);
  await page.getByRole("button", { name: "Save & check in" }).click();
  await expect(page.getByText("Requirement impact")).toHaveCount(0);
  await expect(page.getByText(/Record version/)).toBeVisible();
});
