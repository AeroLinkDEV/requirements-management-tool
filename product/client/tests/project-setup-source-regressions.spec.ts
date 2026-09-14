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
        { sourceKey: "req-2", include: true, level: "HighLevel", attributes: [{ sourceAttribute: "Statement", destination: "Rationale", reason: "Retain foreign rationale semantics" }, { sourceAttribute: "Level", destination: "SourceOnly", reason: "Retain source level" }] },
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
  // The source panel deliberately saves setup answers before each source mutation. Keep this
  // focused UI fixture independent from the server's real source-package existence check while
  // retaining the optimistic-versioned request shape used by the product.
  await page.route(/\/api\/project-setups\/[^/]+(?:\/save-and-exit)?$/, async (route) => {
    if (!(["PUT", "POST"] as const).includes(route.request().method() as "PUT" | "POST")) {
      await route.continue();
      return;
    }
    const body = JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>;
    const project = body.project as Record<string, unknown> | undefined;
    const start = body.start as Record<string, unknown> | undefined;
    const build = body.build as Record<string, unknown> | undefined;
    const repository = body.repository as Record<string, unknown> | undefined;
    await route.fulfill({
      json: {
        draftId: route.request().url().split("/").at(-1),
        state: "Draft",
        currentStep: typeof body.currentStep === "string" ? body.currentStep : "StartingPoint",
        version: Number(body.expectedVersion ?? 1) + 1,
        project: project ?? { name: "Source proof", softwareProduct: "Source proof software" },
        start: start ?? { kind: "ExternalBaseline", sourceImportId: sourceId },
        build: build ?? { version: "1.02", officialName: "SW-01.02" },
        selectedCategories: Array.isArray(body.selectedCategories) ? body.selectedCategories : ["Requirements"],
        ladder: body.ladder ?? {},
        reviewRules: { accepted: body.reviewRulesAccepted === true, definition: body.reviewRules ?? {} },
        repository: repository ?? { mode: "ConfigureLater", provider: "GitLab", endpoint: null },
        mapping: body.mapping ?? {},
      },
    });
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
  await expect(page.getByText(/decisions that differ between source objects/i)).toBeVisible();
  await expect(page.getByLabel("Mapping for Requirements REQ-1 Statement")).toHaveValue("SourceOnly");
  await expect(page.getByLabel("Mapping for Requirements REQ-2 Statement")).toHaveValue("Rationale");

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

  await page.getByLabel("Mapping for Requirements REQ-2 Statement").selectOption("Statement");
  await expect(page.getByText("Reconciliation ready", { exact: true })).toHaveCount(0);
  await expect(page.getByText(/Source acceptance is not available yet/i)).toBeVisible();
  await expect(page.getByLabel("Password to finalize source acceptance")).toHaveCount(0);

  const firstConfiguration = configurationBody;
  expect(firstConfiguration).toBeDefined();
  if (!firstConfiguration) throw new Error("The source configuration request was not captured.");
  expect((firstConfiguration.mapping as { sourceSha256?: string }).sourceSha256).toBe(source.sha256);
  const mappedObjects = ((firstConfiguration.mapping as { objects?: Array<{ sourceKey: string; attributes: Array<{ sourceAttribute: string; destination: string }> }> }).objects ?? []);
  expect(mappedObjects.find((item) => item.sourceKey === "req-1")?.attributes.find((item) => item.sourceAttribute === "Statement")?.destination).toBe("SourceOnly");
  expect(mappedObjects.find((item) => item.sourceKey === "req-2")?.attributes.find((item) => item.sourceAttribute === "Statement")?.destination).toBe("Rationale");
  await page.getByLabel("Relation direction for AllocatedFrom").selectOption("");
  await page.getByRole("button", { name: "Save choices and reconcile" }).click();
  await expect(page.getByRole("alert")).toContainText(/Choose the direction for AllocatedFrom/i);
  expect(configurationCalls).toBe(1);

  // Restore the relation decision and save again. The edited object must be serialized with its
  // new destination while the untouched sibling retains its original decision.
  await page.getByLabel("Relation direction for AllocatedFrom").selectOption("parent");
  await page.getByRole("button", { name: "Save choices and reconcile" }).click();
  await expect(page.getByText("Reconciliation ready", { exact: true })).toBeVisible();
  expect(configurationCalls).toBe(2);
  if (!configurationBody) throw new Error("The updated source configuration request was not captured.");
  const remappedObjects = ((configurationBody.mapping as { objects?: Array<{ sourceKey: string; attributes: Array<{ sourceAttribute: string; destination: string }> }> }).objects ?? []);
  expect(remappedObjects.find((item) => item.sourceKey === "req-1")?.attributes.find((item) => item.sourceAttribute === "Statement")?.destination).toBe("SourceOnly");
  expect(remappedObjects.find((item) => item.sourceKey === "req-2")?.attributes.find((item) => item.sourceAttribute === "Statement")?.destination).toBe("Statement");
  await page.screenshot({ path: testInfo.outputPath("source-proof-invalidated-and-reconciled.png"), fullPage: true });

  // Global Save and exit must flush a source-owned edit through the versioned source endpoint before
  // persisting the setup draft. A setup PUT alone cannot own these source choices.
  await page.getByLabel("Mapping for Requirements REQ-2 Statement").selectOption("Rationale");
  await expect(page.getByText("Reconciliation ready", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Save and exit" }).click();
  await expect(page.getByRole("heading", { name: "Projects", level: 1 })).toBeVisible();
  expect(configurationCalls).toBe(3);
  if (!configurationBody) throw new Error("The Save and exit source configuration request was not captured.");
  const flushedObjects = ((configurationBody.mapping as { objects?: Array<{ sourceKey: string; attributes: Array<{ sourceAttribute: string; destination: string }> }> }).objects ?? []);
  expect(flushedObjects.find((item) => item.sourceKey === "req-2")?.attributes.find((item) => item.sourceAttribute === "Statement")?.destination).toBe("Rationale");
});

test("unmapped heterogeneous source objects can be configured independently", async ({ page }, testInfo) => {
  const sourceId = "00000000-0000-4000-8000-000000000204";
  let source: Record<string, unknown> = {
    id: sourceId,
    kind: "ExternalBaseline",
    displayName: "Heterogeneous source objects",
    fileName: "heterogeneous.reqif",
    format: "REQIF",
    sha256: "source-sha-204",
    metadata: { sourceSystem: "Foreign ReqIF tool" },
    selectedCategories: [],
    categories: [
      { key: "Requirements", count: 2, requires: [], supported: true },
      { key: "Traces", count: 0, requires: ["Requirements"], supported: true },
    ],
    modules: [{
      key: "requirements",
      name: "Requirements",
      objectCount: 2,
      include: true,
      objects: [
        {
          key: "source-system",
          module: "requirements",
          sourceIdentifier: "SYS-1",
          kind: "Requirement",
          attributes: { Statement: "System statement", Owner: "System owner", Level: "System" },
        },
        {
          key: "source-high",
          module: "requirements",
          sourceIdentifier: "HLR-1",
          kind: "Requirement",
          attributes: { Statement: "High-level statement", Rationale: "High-level rationale", Level: "HighLevel" },
        },
      ],
    }],
    relations: [],
    findings: [],
    findingResolutions: {},
    reconciliation: null,
    assertion: null,
  };
  let sourceReady = false;
  let configurationBody: Record<string, unknown> | undefined;

  await page.route(/\/api\/project-setups\/[^/]+\/source$/, async (route) => {
    await route.fulfill({ json: sourceReady ? source : { draftVersion: 2, source: null } });
  });
  await page.route(/\/api\/project-setups\/[^/]+\/source\/upload\?/, async (route) => {
    sourceReady = true;
    await route.fulfill({ json: { id: sourceId, stage: "Analysed", sha256: "source-sha-204", draftVersion: 2 } });
  });
  await page.route(/\/api\/project-setups\/[^/]+\/source\/configuration$/, async (route) => {
    configurationBody = JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>;
    const mapping = configurationBody.mapping as Record<string, unknown>;
    source = {
      ...source,
      selectedCategories: ["Requirements"],
      mapping,
      reconciliation: {
        ready: true,
        observedObjects: 2,
        includedObjects: 1,
        excludedObjects: 1,
        observedRelations: 0,
        includedRelations: 0,
        excludedRelations: 0,
        errors: [],
        manifestHash: "manifest-204",
      },
      assertion: { text: "Source source-sha-204 was reconciled.", hash: "assertion-204" },
    };
    await route.fulfill({ json: { id: sourceId, stage: "Reconciled", manifestHash: "manifest-204", draftVersion: 3 } });
  });
  await page.route(/\/api\/project-setups\/[^/]+(?:\/save-and-exit)?$/, async (route) => {
    if (!["PUT", "POST"].includes(route.request().method())) {
      await route.continue();
      return;
    }
    const body = JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>;
    const project = body.project as Record<string, unknown> | undefined;
    const start = body.start as Record<string, unknown> | undefined;
    const build = body.build as Record<string, unknown> | undefined;
    await route.fulfill({
      json: {
        draftId: route.request().url().split("/").at(-1),
        state: "Draft",
        currentStep: typeof body.currentStep === "string" ? body.currentStep : "StartingPoint",
        version: Number(body.expectedVersion ?? 1) + 1,
        project: project ?? { name: "Heterogeneous source", softwareProduct: "Heterogeneous software" },
        start: start ?? { kind: "ExternalBaseline", sourceImportId: sourceId },
        build: build ?? { version: "1.02", officialName: "SW-01.02" },
        selectedCategories: Array.isArray(body.selectedCategories) ? body.selectedCategories : [],
        ladder: body.ladder ?? {},
        reviewRules: { accepted: body.reviewRulesAccepted === true, definition: body.reviewRules ?? {} },
        repository: body.repository ?? { mode: "ConfigureLater", provider: "GitLab", endpoint: null },
        mapping: body.mapping ?? {},
      },
    });
  });

  await login(page, "admin", { openProject: false });
  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(`Heterogeneous source ${Date.now()}`);
  await page.getByLabel("Software product").fill("Heterogeneous source software");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("External baseline from another tool").check();
  await page.getByLabel("Baseline file (ReqIF, CSV, or XLSX)").setInputFiles({
    name: "heterogeneous.reqif",
    mimeType: "application/xml",
    buffer: Buffer.from("<REQ-IF/>"),
  });
  await page.getByRole("button", { name: "Upload and analyze source" }).click();
  await expect(page.getByText("Heterogeneous source objects", { exact: true })).toBeVisible();

  const system = page.locator("section.setupSourceObjectMapping").filter({ hasText: "SYS-1" });
  const high = page.locator("section.setupSourceObjectMapping").filter({ hasText: "HLR-1" });
  await expect(system).toHaveCount(1);
  await expect(high).toHaveCount(1);
  await expect(system.getByLabel("Mapping for Requirements SYS-1 Statement")).toBeVisible();
  await expect(high.getByLabel("Mapping for Requirements HLR-1 Rationale")).toBeVisible();
  // Header controls remain useful bulk actions. They must update every exact object before an
  // individual override, otherwise the UI would show one decision while the payload retained old
  // per-object defaults.
  const module = page.locator("article.setupSourceModule").filter({ hasText: "Requirements" }).first();
  const moduleInclude = module.getByLabel("Include Requirements");
  await moduleInclude.uncheck();
  await expect(system.getByLabel("Include this source object")).not.toBeChecked();
  await expect(high.getByLabel("Include this source object")).not.toBeChecked();
  await module.getByLabel(/Exclusion reason for this module/).fill("Excluded by the module bulk decision.");
  await expect(system.getByLabel("Exclusion reason", { exact: true })).toHaveValue("Excluded by the module bulk decision.");
  await expect(high.getByLabel("Exclusion reason", { exact: true })).toHaveValue("Excluded by the module bulk decision.");
  await moduleInclude.check();
  await expect(system.getByLabel("Include this source object")).toBeChecked();
  await expect(high.getByLabel("Include this source object")).toBeChecked();
  await module.getByLabel("Ladder level for Requirements").selectOption("LowLevel");
  await system.getByRole("combobox").first().selectOption("System");
  await high.getByLabel("Include this source object").uncheck();
  await high.getByLabel("Exclusion reason", { exact: true }).fill("Not included in this project start.");
  await system.getByLabel("Mapping for Requirements SYS-1 Owner").selectOption("SourceOnly");
  await page.getByRole("button", { name: "Save choices and reconcile" }).click();
  await expect(page.getByText("Reconciliation ready", { exact: true })).toBeVisible();
  expect(configurationBody).toBeDefined();
  if (!configurationBody) throw new Error("The heterogeneous source configuration request was not captured.");
  const objects = ((configurationBody.mapping as { objects?: Array<{
    sourceKey: string;
    include: boolean;
    level?: string;
    exclusionReason?: string;
    attributes: Array<{ sourceAttribute: string; destination: string }>;
  }> }).objects ?? []);
  expect(objects.map((object) => object.sourceKey).sort()).toEqual(["source-high", "source-system"]);
  const systemMapping = objects.find((object) => object.sourceKey === "source-system");
  const highMapping = objects.find((object) => object.sourceKey === "source-high");
  expect(systemMapping?.include).toBe(true);
  expect(systemMapping?.level).toBe("System");
  expect(systemMapping?.attributes.find((attribute) => attribute.sourceAttribute === "Owner")?.destination).toBe("SourceOnly");
  expect(highMapping?.include).toBe(false);
  expect(highMapping?.level).toBe("LowLevel");
  expect(highMapping?.exclusionReason).toBe("Not included in this project start.");
  await page.screenshot({ path: testInfo.outputPath("heterogeneous-object-mappings.png"), fullPage: true });
});

test("large source modules page object editors without losing exact mapping decisions", async ({ page }, testInfo) => {
  const sourceId = "00000000-0000-4000-8000-000000000205";
  const objects = Array.from({ length: 45 }, (_, index) => ({
    key: `source-${index + 1}`,
    module: "requirements",
    sourceIdentifier: `REQ-${String(index + 1).padStart(2, "0")}`,
    kind: "Requirement",
    attributes: {
      Statement: `Requirement ${index + 1} statement`,
      Level: index < 20 ? "System" : "HighLevel",
    },
  }));
  let sourceReady = false;
  let configurationBody: Record<string, unknown> | undefined;
  let source: Record<string, unknown> = {
    id: sourceId,
    kind: "ExternalBaseline",
    displayName: "Paged source module",
    fileName: "paged.csv",
    format: "CSV",
    sha256: "source-sha-205",
    metadata: { sourceSystem: "Paged source tool" },
    selectedCategories: [],
    categories: [{ key: "Requirements", count: objects.length, requires: [], supported: true }],
    modules: [{
      key: "requirements",
      name: "Requirements",
      objectCount: objects.length,
      include: true,
      objects,
    }],
    relations: [],
    findings: [],
    findingResolutions: {},
    reconciliation: null,
    assertion: null,
  };

  await page.route(/\/api\/project-setups\/[^/]+\/source$/, async (route) => {
    await route.fulfill({ json: sourceReady ? source : { draftVersion: 2, source: null } });
  });
  await page.route(/\/api\/project-setups\/[^/]+\/source\/upload\?/, async (route) => {
    sourceReady = true;
    await route.fulfill({ json: { id: sourceId, stage: "Analysed", sha256: "source-sha-205", draftVersion: 2 } });
  });
  await page.route(/\/api\/project-setups\/[^/]+\/source\/configuration$/, async (route) => {
    configurationBody = JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>;
    source = {
      ...source,
      selectedCategories: ["Requirements"],
      reconciliation: {
        ready: true,
        observedObjects: objects.length,
        includedObjects: objects.length,
        excludedObjects: 0,
        observedRelations: 0,
        includedRelations: 0,
        excludedRelations: 0,
        errors: [],
        manifestHash: "manifest-205",
      },
      assertion: { text: "Paged source source-sha-205 was reconciled.", hash: "assertion-205" },
    };
    await route.fulfill({ json: { id: sourceId, stage: "Reconciled", manifestHash: "manifest-205", draftVersion: 3 } });
  });
  await page.route(/\/api\/project-setups\/[^/]+(?:\/save-and-exit)?$/, async (route) => {
    if (!["PUT", "POST"].includes(route.request().method())) {
      await route.continue();
      return;
    }
    const body = JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>;
    await route.fulfill({
      json: {
        draftId: route.request().url().split("/").at(-1),
        state: "Draft",
        currentStep: typeof body.currentStep === "string" ? body.currentStep : "StartingPoint",
        version: Number(body.expectedVersion ?? 1) + 1,
        project: body.project ?? { name: "Paged source", softwareProduct: "Paged software" },
        start: body.start ?? { kind: "ExternalBaseline", sourceImportId: sourceId },
        build: body.build ?? { version: "1.02", officialName: "SW-01.02" },
        selectedCategories: Array.isArray(body.selectedCategories) ? body.selectedCategories : [],
        ladder: body.ladder ?? {},
        reviewRules: { accepted: body.reviewRulesAccepted === true, definition: body.reviewRules ?? {} },
        repository: body.repository ?? { mode: "ConfigureLater", provider: "GitLab", endpoint: null },
        mapping: body.mapping ?? {},
      },
    });
  });

  await login(page, "admin", { openProject: false });
  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(`Paged source ${Date.now()}`);
  await page.getByLabel("Software product").fill("Paged software");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("External baseline from another tool").check();
  await page.getByLabel("Baseline file (ReqIF, CSV, or XLSX)").setInputFiles({
    name: "paged.csv",
    mimeType: "text/csv",
    buffer: Buffer.from("foreign-id,statement\nREQ-01,Requirement 1 statement\n"),
  });
  await page.getByRole("button", { name: "Upload and analyze source" }).click();
  await expect(page.getByText("Paged source module", { exact: true })).toBeVisible();

  const module = page.locator("article.setupSourceModule").filter({ hasText: "Requirements" }).first();
  await expect(module.getByText("1–20 of 45 source objects", { exact: true })).toBeVisible();
  const first = module.locator("section.setupSourceObjectMapping").filter({ hasText: "REQ-01" });
  await expect(first).toHaveCount(1);
  await first.getByLabel("Mapping for Requirements REQ-01 Statement").selectOption("Rationale");
  const moduleInclude = module.getByLabel("Include Requirements");
  await moduleInclude.uncheck();
  await expect(first.getByLabel("Include this source object")).not.toBeChecked();
  await module.getByLabel(/Exclusion reason for this module/).fill("Bulk exclusion before per-object review.");
  await expect(first.getByLabel("Exclusion reason", { exact: true })).toHaveValue("Bulk exclusion before per-object review.");
  await moduleInclude.check();
  await module.getByLabel("Ladder level for Requirements").selectOption("HighLevel");
  await expect(first.getByLabel("Ladder level")).toHaveValue("HighLevel");

  await module.getByRole("button", { name: "Next objects page" }).click();
  await expect(module.getByText("21–40 of 45 source objects", { exact: true })).toBeVisible();
  const pageTwo = module.locator("section.setupSourceObjectMapping").filter({ hasText: "REQ-21" });
  await expect(pageTwo).toHaveCount(1);
  await expect(pageTwo.getByLabel("Include this source object")).toBeChecked();
  await expect(pageTwo.getByLabel("Ladder level")).toHaveValue("HighLevel");
  await pageTwo.getByLabel("Mapping for Requirements REQ-21 Statement").selectOption("SourceOnly");
  await pageTwo.getByLabel("Ladder level").selectOption("LowLevel");
  await pageTwo.getByLabel("Include this source object").uncheck();
  await pageTwo.getByLabel("Exclusion reason", { exact: true }).fill("Per-object exclusion on page two.");

  await module.getByRole("button", { name: "Previous objects page" }).click();
  await expect(module.getByText("1–20 of 45 source objects", { exact: true })).toBeVisible();
  await expect(first.getByLabel("Mapping for Requirements REQ-01 Statement")).toHaveValue("Rationale");
  await expect(first.getByLabel("Include this source object")).toBeChecked();
  await expect(first.getByLabel("Ladder level")).toHaveValue("HighLevel");
  await expect(first.getByLabel("Exclusion reason", { exact: true })).toHaveValue("Bulk exclusion before per-object review.");
  await module.getByRole("button", { name: "Next objects page" }).click();
  await expect(pageTwo.getByLabel("Mapping for Requirements REQ-21 Statement")).toHaveValue("SourceOnly");
  await expect(pageTwo.getByLabel("Include this source object")).not.toBeChecked();
  await expect(pageTwo.getByLabel("Ladder level")).toHaveValue("LowLevel");
  await expect(pageTwo.getByLabel("Exclusion reason", { exact: true })).toHaveValue("Per-object exclusion on page two.");

  await module.getByRole("button", { name: "Previous objects page" }).click();
  await page.getByRole("checkbox", { name: /^Requirements / }).check();
  await page.getByRole("button", { name: "Save choices and reconcile" }).click();
  await expect(page.getByText("Reconciliation ready", { exact: true })).toBeVisible();
  expect(configurationBody).toBeDefined();
  if (!configurationBody) throw new Error("The paged source configuration request was not captured.");
  const mappedObjects = ((configurationBody.mapping as {
    objects?: Array<{
      sourceKey: string;
      include: boolean;
      level?: string;
      exclusionReason?: string;
      attributes: Array<{ sourceAttribute: string; destination: string }>;
    }>;
  }).objects ?? []);
  expect(mappedObjects).toHaveLength(objects.length);
  const firstMapping = mappedObjects.find((item) => item.sourceKey === "source-1");
  const pageTwoMapping = mappedObjects.find((item) => item.sourceKey === "source-21");
  const untouchedPageTwoMapping = mappedObjects.find((item) => item.sourceKey === "source-22");
  expect(firstMapping?.include).toBe(true);
  expect(firstMapping?.level).toBe("HighLevel");
  expect(firstMapping?.exclusionReason).toBe("Bulk exclusion before per-object review.");
  expect(pageTwoMapping?.include).toBe(false);
  expect(pageTwoMapping?.level).toBe("LowLevel");
  expect(pageTwoMapping?.exclusionReason).toBe("Per-object exclusion on page two.");
  expect(untouchedPageTwoMapping?.include).toBe(true);
  expect(untouchedPageTwoMapping?.level).toBe("HighLevel");
  expect(untouchedPageTwoMapping?.exclusionReason).toBe("Bulk exclusion before per-object review.");
  expect(firstMapping?.attributes
    .find((item) => item.sourceAttribute === "Statement")?.destination).toBe("Rationale");
  expect(pageTwoMapping?.attributes
    .find((item) => item.sourceAttribute === "Statement")?.destination).toBe("SourceOnly");
  await page.screenshot({ path: testInfo.outputPath("paged-source-object-mappings.png"), fullPage: false });
});

test("native source picker follows an authoritative page total past the first 50 baselines", async ({ page }, testInfo) => {
  const rows = (offset: number) => Array.from({ length: offset === 0 ? 50 : 1 }, (_, index) => {
    const number = offset + index + 1;
    return {
      baselineId: `00000000-0000-4000-8000-${number.toString().padStart(12, "0")}`,
      projectId: `00000000-0000-4000-9000-${number.toString().padStart(12, "0")}`,
      projectName: `Authorized project ${number}`,
      name: `Baseline ${number}`,
      displayNumber: `SW-${number.toString().padStart(2, "0")}.01`,
      state: "Frozen",
      requirementsCount: number,
      casesCount: 0,
      proceduresCount: 0,
      evidenceCount: 0,
    };
  });
  await page.route(/\/api\/project-setups\/source-options\?/, async (route) => {
    const offset = Number(new URL(route.request().url()).searchParams.get("offset") ?? "0");
    await route.fulfill({ json: { items: rows(offset), total: 51, offset, limit: 50 } });
  });

  await login(page, "admin", { openProject: false });
  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(`Paging proof ${Date.now()}`);
  await page.getByLabel("Software product").fill("Paging proof software");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Existing authorized AeroLink baseline").check();
  await expect(page.getByText("1–50 of 51", { exact: true })).toBeVisible();
  const next = page.getByRole("button", { name: "Next page" });
  await expect(next).toBeEnabled();
  await next.click();
  await expect(page.getByText("51–51 of 51", { exact: true })).toBeVisible();
  await expect(page.getByText("Baseline 51", { exact: true })).toBeVisible();
  await expect(next).toBeDisabled();
  await expect(page.getByRole("button", { name: "Previous page" })).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath("native-source-paging-last-page.png"), fullPage: true });
});

test("a delayed stale source load cannot clear a new acceptance", async ({ page }) => {
  const draftId = "00000000-0000-4000-8000-000000000999";
  const sourceId = "00000000-0000-4000-8000-000000000998";
  const source = {
    id: sourceId,
    kind: "ExternalBaseline",
    fileName: "delayed.reqif",
    format: "REQIF",
    sha256: "delayed-source-sha",
    selectedCategories: ["Requirements"],
    modules: [{ key: "requirements", name: "Requirements", objectCount: 1, objectKeys: ["req-1"], objects: [{ key: "req-1", module: "requirements", sourceIdentifier: "REQ-1", kind: "Requirement", attributes: { Statement: "A delayed source fact", Level: "System" } }] }],
    relations: [],
    findings: [],
    findingResolutions: {},
    reconciliation: { ready: true, observedObjects: 1, includedObjects: 1, excludedObjects: 0, observedRelations: 0, includedRelations: 0, excludedRelations: 0, errors: [] },
    assertion: { text: "The delayed source assertion is exact.", hash: "delayed-assertion-hash" },
    ladderSuggestion: { levels: ["System"], relationships: [], findings: [] },
  };
  const draft = {
    draftId,
    state: "Draft",
    currentStep: "Review",
    version: 4,
    project: { name: "Delayed source proof", softwareProduct: "Delayed source software" },
    start: { kind: "ExternalBaseline", sourceImportId: sourceId },
    build: { version: "1.02", officialName: "SW-01.02" },
    selectedCategories: ["Requirements"],
    ladder: { steps: [{ catalogueEntry: "System", position: 1, capabilities: 7, enabledArtifactKinds: ["Procedure"] }], relationships: [] },
    reviewRules: {
      accepted: true,
      definition: {
        rules: [{ subject: "System", name: "System review", stages: [
          { name: "Review", kind: "Review", requiredRole: "SystemEngineer", authorityKind: "BaseRole" },
          { name: "Approval", kind: "Approval", requiredRole: "SystemEngineer", authorityKind: "BaseRole" },
        ] }],
      },
    },
    repository: { mode: "ConfigureLater", provider: "GitLab", endpoint: null },
    mapping: {},
  };
  let sourceGets = 0;
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    await route.fulfill({ json: draft });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}/source$`), async (route) => {
    sourceGets += 1;
    if (sourceGets === 1) await new Promise((resolve) => setTimeout(resolve, 1_500));
    await route.fulfill({ json: { draftVersion: 4, source } });
  });

  await login(page, "admin", { openProject: false });
  const initialNavigation = page.goto(`/projects/setup/${draftId}`);
  await expect.poll(() => sourceGets, { timeout: 5_000 }).toBe(1);
  await page.goto(`/projects/setup/${draftId}`);
  await initialNavigation.catch(() => undefined);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  const acceptance = page.getByLabel(/I accept this exact source assertion/i);
  await expect(acceptance).toBeVisible();
  await acceptance.check();
  await page.getByLabel("Password to finalize source acceptance").fill("memory-only-password");
  await expect(acceptance).toBeChecked();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
  await new Promise((resolve) => setTimeout(resolve, 1_800));
  expect(sourceGets).toBeGreaterThan(1);
  await expect(acceptance).toBeChecked();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
});
