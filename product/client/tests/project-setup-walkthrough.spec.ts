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
}, testInfo) => {
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
  await page.screenshot({ path: testInfo.outputPath("fresh-build-lineage.png"), fullPage: true });
});

test("refreshes review rules after a saved ladder adds Interface and changes software verification", async ({
  page,
}, testInfo) => {
  await login(page, "admin", { openProject: false });
  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill("Rules refresh UI " + Date.now());
  await page.getByLabel("Software product").fill("Rules refresh UI Software");
  await page.getByRole("button", { name: "Continue" }).click();

  await page.getByLabel("Fresh project").check();
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Version").fill("1.03");
  await page.getByRole("button", { name: "Continue" }).click();

  // The first visit persists the default ladder and receives its server standard. The second
  // visit changes the persisted ladder, which must expose the newer suggested definition.
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  const ladderRows = page.locator(".setupLadderRows > li");
  await expect(ladderRows).toHaveCount(3);
  await page.getByRole("button", { name: "Add supported level" }).click();
  await page.getByRole("button", { name: "Add supported level" }).click();
  await expect(ladderRows).toHaveCount(5);
  await ladderRows.nth(1).getByLabel("Verification profile").selectOption("Case");
  await ladderRows.nth(2).getByLabel("Verification profile").selectOption("Case");
  await ladderRows.nth(4).getByRole("combobox").first().selectOption("Interface");
  await page.getByRole("button", { name: "Continue" }).click();

  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Use rules for this ladder" })).toBeVisible();
  await expect(page.getByText("Required subjects added:", { exact: false })).toBeVisible();
  await expect(page.getByText("replaces this draft's existing rule definition", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Use rules for this ladder" }).click();
  const accepted = page.getByLabel(/explicitly accept these concrete review and approval rules/i);
  await expect(accepted).not.toBeChecked();
  await accepted.check();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Repository setup", level: 2 })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("fr05-rules-accepted.png"), fullPage: true });
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await page.getByRole("button", { name: "Create Project" }).click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]+\/builds$/);
  await expect(page.locator("[data-build-card]")).toHaveCount(1);
  await expect(page.locator("[data-build-card]").getByText("SW-01.03", { exact: true })).toBeVisible();
  await expect(page.locator("[data-build-card]").getByText("In Work", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("fr05-adjusted-ladder-created.png"), fullPage: true });
});

test("stages an external XLSX source through the server reconciliation envelope", async ({ page }, testInfo) => {
  await login(page, "admin", { openProject: false });
  const sourceId = "00000000-0000-4000-8000-000000000103";
  let source = {
    id: sourceId,
    kind: "ExternalBaseline",
    displayName: "Imported navigation requirements",
    fileName: "navigation.xlsx",
    format: "XLSX",
    sha256: "source-sha-103",
    metadata: { sourceSystem: "Planning Tool" },
    categories: [
      { key: "Requirements", count: 2, requires: [], supported: true },
      { key: "Cases", count: 1, requires: ["Requirements"], supported: true },
      { key: "Evidence", count: 1, requires: ["Cases"], supported: true },
    ],
    selectedCategories: [],
    modules: [{
      key: "requirements",
      name: "Requirements",
      objectCount: 2,
      attributes: [{ key: "priority", name: "Priority" }],
      mappings: [{ sourceAttribute: "priority", destination: "SourceOnly", valueMappings: [{ sourceValue: "high", destinationValue: "" }] }],
      include: true,
    }],
    relations: [{ sourceType: "satisfies", count: 1, include: true, sourceIsParent: true }],
    findings: [],
    findingResolutions: {},
    reconciliation: null,
    assertion: null,
  };
  let uploadedBytes = 0;
  let configurationBody: Record<string, unknown> | undefined;
  await page.route(/\/api\/project-setups\/[^/]+\/source$/, async (route) => {
    await route.fulfill({ json: { draftVersion: 2, source: null } });
  });
  await page.route(/\/api\/project-setups\/[^/]+\/source\/upload\?/, async (route) => {
    uploadedBytes = route.request().postDataBuffer()?.length ?? 0;
    source = { ...source, selectedCategories: [] };
    // This browser test mocks the source service only; the disposable setup service remains the
    // authority for the draft version. Keeping the envelope at that current version models a
    // source response whose server-side version has already been observed by the caller.
    await route.fulfill({ json: { draftVersion: 2, source } });
  });
  await page.route(/\/api\/project-setups\/[^/]+\/source\/configuration$/, async (route) => {
    configurationBody = JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>;
    source = {
      ...source,
      selectedCategories: ["Requirements", "Cases"],
      reconciliation: { ready: true, observedObjects: 3, includedObjects: 3, excludedObjects: 0, observedRelations: 1, includedRelations: 1, excludedRelations: 0, errors: [], manifestHash: "manifest-103" },
      assertion: { text: "Source source-sha-103 was reconciled for this exact project start.", hash: "assertion-103" },
    };
    await route.fulfill({ json: { draftVersion: 2, source } });
  });

  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(`External source UI ${Date.now()}`);
  await page.getByLabel("Software product").fill("External source software");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("External baseline from another tool").check();
  await page.getByLabel("Baseline file (ReqIF, CSV, or XLSX)").setInputFiles({
    name: "navigation.xlsx",
    mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    buffer: Buffer.from("foreign-id,statement\nREQ-1,Navigation shall work\n"),
  });
  await page.getByRole("button", { name: "Upload and analyze source" }).click();
  await expect(page.getByText("Imported navigation requirements", { exact: true })).toBeVisible();
  await page.getByRole("checkbox", { name: /^Requirements 2 observed/ }).check();
  await page.getByRole("checkbox", { name: /^Test cases 1 observed/ }).check();
  await page.getByRole("button", { name: "Save choices and reconcile" }).click();
  await expect(page.getByText("Reconciliation ready", { exact: true })).toBeVisible();
  await expect(page.getByText("Source source-sha-103 was reconciled", { exact: false })).toBeVisible();
  await expect(page.getByLabel(/I accept this exact source assertion/i)).toBeVisible();
  await expect(page.getByLabel("Password to finalize source acceptance")).toHaveValue("");
  expect(uploadedBytes).toBeGreaterThan(0);
  expect(configurationBody?.selectedCategories).toEqual(["Requirements", "Cases"]);
  expect(JSON.stringify(configurationBody)).not.toContain("objectCount");
  await page.screenshot({ path: testInfo.outputPath("external-xlsx-source-reconciled.png"), fullPage: true });
});
