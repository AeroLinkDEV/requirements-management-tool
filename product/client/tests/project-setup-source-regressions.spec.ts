import { expect, test } from "@playwright/test";
import { login } from "./auth";

test("source metadata is read-only and source proof expires after mapping or relation edits", async ({ page }, testInfo) => {
  await login(page, "admin", { openProject: false });
  const sourceId = "00000000-0000-4000-8000-000000000203";
  let source = {
    id: sourceId,
    kind: "ExternalBaseline",
    displayName: "Parser-derived source metadata",
    fileName: "source.csv",
    format: "CSV",
    sha256: "source-sha-203",
    metadata: { sourceSystem: "Foreign Tool" },
    selectedCategories: [],
    modules: [{
      key: "requirements",
      name: "Requirements",
      objectCount: 2,
      objectKeys: ["req-1", "req-2"],
      objects: [
        { key: "req-1", module: "requirements", sourceIdentifier: "REQ-1", kind: "Requirement", attributes: { Statement: "Navigation shall work", Level: "System" } },
        { key: "req-2", module: "requirements", sourceIdentifier: "REQ-2", kind: "Requirement", attributes: { Statement: "Navigation shall report status", Level: "HighLevel" } },
      ],
    }],
    relations: [{ key: "trace-1", sourceKey: "req-2", targetKey: "req-1", sourceType: "AllocatedFrom", type: "AllocatedFrom", count: 1, include: true, sourceIsParent: true }],
    findings: [],
    findingResolutions: {},
    reconciliation: null,
    assertion: null,
    ladderSuggestion: { levels: ["System", "HighLevel"], relationships: [{ key: "trace-1", type: "AllocatedFrom", sourceLevel: "HighLevel", targetLevel: "System" }], findings: [] },
    mapping: {
      sourceSha256: "source-sha-203",
      objects: [
        { sourceKey: "req-1", include: true, level: "System", attributes: [{ sourceAttribute: "Statement", destination: "SourceOnly", reason: "Retain source wording" }, { sourceAttribute: "Level", destination: "SourceOnly", reason: "Retain source level" }] },
        { sourceKey: "req-2", include: true, level: "HighLevel", attributes: [{ sourceAttribute: "Statement", destination: "SourceOnly", reason: "Retain source wording" }, { sourceAttribute: "Level", destination: "SourceOnly", reason: "Retain source level" }] },
      ],
      relations: [{ sourceKey: "trace-1", include: true, type: "AllocatedFrom", sourceIsParent: true }],
      findingResolutions: {},
    },
  };
  let configurationCalls = 0;
  let sourceReady = false;
  let configurationBody: Record<string, unknown> | undefined;

  await page.route(/\/api\/project-setups\/[^/]+\/source$/, async (route) => {
    await route.fulfill({ json: sourceReady ? { draftVersion: 2, ...source } : { draftVersion: 2, source: null } });
  });
  await page.route(/\/api\/project-setups\/[^/]+\/source\/upload\?/, async (route) => {
    sourceReady = true;
    await route.fulfill({ json: { id: sourceId, stage: "Analysed", sha256: source.sha256, draftVersion: 2 } });
  });
  await page.route(/\/api\/project-setups\/[^/]+\/source\/configuration$/, async (route) => {
    configurationCalls += 1;
    configurationBody = JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>;
    source = {
      ...source,
      selectedCategories: ["Requirements"],
      reconciliation: {
        ready: true,
        observedObjects: 2,
        includedObjects: 2,
        excludedObjects: 0,
        observedRelations: 1,
        includedRelations: 1,
        excludedRelations: 0,
        errors: [],
        manifestHash: "manifest-203",
      },
      assertion: {
        text: "Source source-sha-203 was reconciled for this exact project start.",
        hash: "assertion-203",
      },
    };
    sourceReady = true;
    await route.fulfill({ json: { id: sourceId, stage: "Reconciled", manifestHash: "manifest-203", draftVersion: 2 } });
  });

  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(`Source proof UI ${Date.now()}`);
  await page.getByLabel("Software product").fill("Source proof software");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("External baseline from another tool").check();
  await page.getByLabel("Baseline file (ReqIF, CSV, or XLSX)").setInputFiles({
    name: "source.csv",
    mimeType: "text/csv",
    buffer: Buffer.from("foreign-id,statement\nREQ-1,Navigation shall work\n"),
  });
  await page.getByRole("button", { name: "Upload and analyze source" }).click();
  await expect(page.getByText("Parser-derived source metadata", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Source-informed ladder suggestion" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Review and use compatible levels" })).toBeVisible();

  await expect(page.locator(".setupSourceMetadata input")).toHaveCount(0);
  await expect(page.getByLabel("Source system", { exact: true })).toHaveText("Foreign Tool");
  await expect(page.getByLabel("Source system version", { exact: true })).toHaveText("Unknown (not reported by source)");
  await expect(page.getByLabel("Source baseline date", { exact: true })).toHaveText("Unknown (not reported by source)");
  await expect(page.getByLabel("Extracted by", { exact: true })).toHaveText("Unknown (not reported by source)");

  await page.getByRole("checkbox", { name: /^Requirements 2 observed/ }).check();
  await page.getByRole("button", { name: "Save choices and reconcile" }).click();
  await expect(page.getByText("Reconciliation ready", { exact: true })).toBeVisible();
  await page.getByLabel(/I accept this exact source assertion/i).check();
  await page.getByLabel("Password to finalize source acceptance").fill("memory-only-password");

  await page.getByLabel("Mapping for Requirements Statement").selectOption("Statement");
  await expect(page.getByText("Reconciliation ready", { exact: true })).toHaveCount(0);
  await expect(page.getByText(/Source acceptance is not available yet/i)).toBeVisible();
  await expect(page.getByLabel("Password to finalize source acceptance")).toHaveCount(0);

  await expect(configurationBody?.mapping).toBeDefined();
  expect((configurationBody?.mapping as { sourceSha256?: string }).sourceSha256).toBe(source.sha256);
  await page.getByLabel("Relation direction for AllocatedFrom").selectOption("");
  await page.getByRole("button", { name: "Save choices and reconcile" }).click();
  await expect(page.getByRole("alert")).toContainText(/Choose the direction for AllocatedFrom/i);
  expect(configurationCalls).toBe(1);
  await page.screenshot({ path: testInfo.outputPath("source-proof-invalidated.png"), fullPage: true });
});
