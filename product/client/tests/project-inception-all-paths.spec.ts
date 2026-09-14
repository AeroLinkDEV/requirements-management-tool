import { expect, test, type APIRequestContext, type Page, type TestInfo } from "@playwright/test";
import { apiBase, login } from "./auth";

type Draft = {
  draftId: string;
  version: number;
  start?: { kind?: string; sourceBaselineId?: string | null; sourceImportId?: string | null };
};

type SourceObject = {
  key: string;
  module: string;
  sourceIdentifier: string;
  kind: string;
  attributes: Record<string, string>;
};

type SourceRelation = {
  key?: string;
  sourceKey?: string;
  targetKey?: string;
  sourceType: string;
  type?: string;
};

type SourceModule = { objects?: SourceObject[]; objectKeys?: string[] };
type Source = {
  id: string;
  kind: "AeroLinkBaseline" | "ExternalBaseline";
  format: string;
  fileName?: string;
  sha256: string;
  selectedCategories: string[];
  modules: SourceModule[];
  relations: SourceRelation[];
  findings: { key: string; message: string }[];
  reconciliation?: { ready: boolean; errors: string[] } | null;
  assertion?: { text: string; hash: string } | null;
};

const password = "AeroLink!2026";
const xlsxFixture = Buffer.from(
  "UEsDBBQAAAAIAPq5LV2hvrYTnAAAAOoAAAAPAAAAeGwvd29ya2Jvb2sueG1sjI+xEoIwEER/he46AhYWDKGyofUPIjlMhiQX78LI5zuC9FZb7Ly3s/2beHkQLdUWQxINrpTcKSWTw2ikpoxpi2EmjqZITfxUkhmNFYdYYlCXprmqaHyCw9DxPw6aZz/hjaY1YiqHhDGY4imJ81lg6PcF+WWVTEQNd3ytnvELCVR7M1oNLVTceauBR9uCGnp1wur8N3wAAAD//wMAUEsDBBQAAAAIAPq5LV37PYqEIgEAAHgDAAAYAAAAeGwvd29ya3NoZWV0cy9zaGVldDEueG1sjJM9a8MwEIb/ijZNtRIHOhRZ0DZLIVPdIevVPtei+khPlyb598WmmBSkkk0cPO/pfeD0KdJnGhFZnL0LqZEj8+FBqdSN6CFV8YDh7N0QyQOnKtKHSgdC6GfIO1WvVvfKgw3S6Hm2BQajKZ4ENXItje6mx+NaCm6kDc4GbJmk0TYZzealx8B2sEhasdFqmqrul3oqUTv8RpcBnktAy8DoMXAG2pagV2AbAzj8CymKp6VhvTSsCyn7Xbu/W+falYj2khh9rl6JeBtRTHsE4dfR0txUpBGcE+8oCBlswF7gGTp2lyonofiZeKQOBd3gYrO42Pznos65KBFlFyVicpGwi6EvKQGX4rWXrI9S/NYOA9IcdoMZdXUTajk28wMAAP//AwBQSwMEFAAAAAgA+rktXfZOkEqVAAAA8QAAABoAAAB4bC9fcmVscy93b3JrYm9vay54bWwucmVsc4zPsQ6DMAwE0F/J5g1Dhw4VMHVhrfiBKBiCSOIodlX695U6VFTq0OmkG97p2hsFqysn8WsWs8eQpAOvmi+I4jxFKxVnSnsMM5doVSouC2brNrsQnur6jOVoQN8eTTNMHZRhasCMz0z/2DzPq6Mru3ukpD8m8MFlE0+kYEZbFtIOPpXgO5pqjwGwb/HrYf8CAAD//wMAUEsBAhQAFAAAAAgA+rktXaG+thOcAAAA6gAAAA8AAAAAAAAAAAAAAAAAAAAAAHhsL3dvcmtib29rLnhtbFBLAQIUABQAAAAIAPq5LV37PYqEIgEAAHgDAAAYAAAAAAAAAAAAAAAAAMkAAAB4bC93b3Jrc2hlZXRzL3NoZWV0MS54bWxQSwECFAAUAAAACAD6uS1d9k6QSpUAAADxAAAAGgAAAAAAAAAAAAAAAAAhAgAAeGwvX3JlbHMvd29ya2Jvb2sueG1sLnJlbHNQSwUGAAAAAAMAAwDLAAAA7gIAAAAA",
  "base64",
);

const reqIfFixture = `<REQ-IF>
  <REQ-IF-HEADER><SOURCE-TOOL-ID>External ReqIF Tool</SOURCE-TOOL-ID></REQ-IF-HEADER>
  <SPEC-TYPES><SPEC-OBJECT-TYPE IDENTIFIER="REQ">
    <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="id" LONG-NAME="Identifier"/>
    <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="level" LONG-NAME="Level"/>
    <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="statement" LONG-NAME="Statement"/>
    <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="rationale" LONG-NAME="Rationale"/>
  </SPEC-OBJECT-TYPE></SPEC-TYPES>
  <SPEC-OBJECTS>
    <SPEC-OBJECT IDENTIFIER="req-system"><TYPE><SPEC-OBJECT-TYPE-REF>REQ</SPEC-OBJECT-TYPE-REF></TYPE><VALUES>
      <ATTRIBUTE-VALUE-STRING THE-VALUE="REQ-SYS"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>id</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
      <ATTRIBUTE-VALUE-STRING THE-VALUE="System"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>level</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
      <ATTRIBUTE-VALUE-STRING THE-VALUE="The system shall retain the ReqIF source fact."><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>statement</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
      <ATTRIBUTE-VALUE-STRING THE-VALUE="ReqIF source rationale."><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>rationale</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
    </VALUES></SPEC-OBJECT>
    <SPEC-OBJECT IDENTIFIER="req-high"><TYPE><SPEC-OBJECT-TYPE-REF>REQ</SPEC-OBJECT-TYPE-REF></TYPE><VALUES>
      <ATTRIBUTE-VALUE-STRING THE-VALUE="REQ-HIGH"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>id</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
      <ATTRIBUTE-VALUE-STRING THE-VALUE="HighLevel"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>level</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
      <ATTRIBUTE-VALUE-STRING THE-VALUE="The high-level requirement shall retain its upstream relationship."><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>statement</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
      <ATTRIBUTE-VALUE-STRING THE-VALUE="ReqIF high-level rationale."><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>rationale</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
    </VALUES></SPEC-OBJECT>
  </SPEC-OBJECTS>
  <SPEC-RELATIONS><SPEC-RELATION IDENTIFIER="trace-1"><SOURCE><SPEC-OBJECT-REF>req-high</SPEC-OBJECT-REF></SOURCE><TARGET><SPEC-OBJECT-REF>req-system</SPEC-OBJECT-REF></TARGET><TYPE><SPEC-RELATION-TYPE-REF>DerivedFrom</SPEC-RELATION-TYPE-REF></TYPE></SPEC-RELATION></SPEC-RELATIONS>
</REQ-IF>`;

// Keep a root-only ReqIF sample alongside the hierarchical fixture. The former
// proves the supported format end to end while the latter remains the explicit
// regression for preserving exact parent links during materialization.
const reqIfRootFixture = reqIfFixture
  .replace(/\s*<SPEC-OBJECT IDENTIFIER="req-high">[\s\S]*?<\/SPEC-OBJECT>/, "")
  .replace(/\s*<SPEC-RELATIONS>[\s\S]*?<\/SPEC-RELATIONS>/, "");

async function json<T>(response: Awaited<ReturnType<APIRequestContext["get"]>>): Promise<T> {
  const body = await response.text();
  expect(response.ok(), body).toBeTruthy();
  return JSON.parse(body) as T;
}

async function draft(request: APIRequestContext, draftId: string) {
  return json<Draft>(await request.get(`${apiBase}/api/project-setups/${draftId}`));
}

async function source(request: APIRequestContext, draftId: string) {
  return json<Source>(await request.get(`${apiBase}/api/project-setups/${draftId}/source`));
}

function allObjects(view: Source) {
  return view.modules.flatMap((module) => module.objects ?? []);
}

function attrKey(key: string, name: string) {
  const normalized = key.toLocaleLowerCase();
  const target = name.toLocaleLowerCase();
  return normalized === target || normalized.endsWith(`:${target}`);
}

function objectMapping(item: SourceObject) {
  const isRequirement = ["Requirement", "Unmapped", ""].includes(item.kind);
  if (!isRequirement) {
    return {
      sourceKey: item.key,
      include: false,
      exclusionReason: "This source category is outside the selected requirement qualification.",
      attributes: [],
    };
  }
  const level = Object.entries(item.attributes).find(([key]) => attrKey(key, "Level"))?.[1] || "System";
  return {
    sourceKey: item.key,
    include: true,
    level,
    attributes: Object.keys(item.attributes).map((attribute) => {
      const destination = attrKey(attribute, "Statement")
        ? "Statement"
        : attrKey(attribute, "Rationale")
          ? "Rationale"
          : attrKey(attribute, "Identifier") || attrKey(attribute, "ID")
            ? "SourceIdentifier"
            : "SourceOnly";
      return {
        sourceAttribute: attribute,
        destination,
        ...(destination === "SourceOnly" ? { reason: "Retain the exact foreign value as a source fact." } : {}),
      };
    }),
  };
}

function configurationFor(view: Source) {
  const objects = allObjects(view);
  const included = new Set(objects.filter((item) => view.kind === "AeroLinkBaseline" || ["Requirement", "Unmapped", ""].includes(item.kind)).map((item) => item.key));
  const mappings = objects.map((item) => objectMapping(item));
  const relations = view.relations.map((relation) => {
    const key = relation.key ?? relation.sourceKey ?? relation.sourceType;
    const relationType = relation.type ?? relation.sourceType;
    const canTrace = relationType.toLocaleLowerCase() === "derivedfrom"
      && included.has(relation.sourceKey ?? "") && included.has(relation.targetKey ?? "");
    return canTrace
      ? { sourceKey: key, include: true, type: "AllocatedFrom", sourceIsParent: false }
      : { sourceKey: key, include: false, exclusionReason: "This relation is outside the selected inception qualification." };
  });
  return {
    sourceSha256: view.sha256,
    objects: mappings,
    relations,
    findingResolutions: Object.fromEntries(view.findings.map((finding) => [finding.key, "Reviewed and explicitly excluded from this qualification."])),
  };
}

async function configureSimpleExternalSource(page: Page) {
  const requirements = page.getByRole("checkbox", { name: /^Requirements / });
  if (!(await requirements.isChecked())) await requirements.check();

  const objectPanels = page.locator("section.setupSourceObjectMapping");
  const objectCount = await objectPanels.count();
  expect(objectCount, "the parsed source must expose at least one exact object for mapping").toBeGreaterThan(0);
  for (let panelIndex = 0; panelIndex < objectCount; panelIndex += 1) {
    const objectPanel = objectPanels.nth(panelIndex);
    await objectPanel.getByRole("combobox").first().selectOption("System");
    const attributeSelects = objectPanel.locator('select[aria-label^="Mapping for "]');
    for (let attributeIndex = 0; attributeIndex < await attributeSelects.count(); attributeIndex += 1) {
      const select = attributeSelects.nth(attributeIndex);
      const rawLabel = await select.getAttribute("aria-label") ?? "";
      const label = rawLabel.toLocaleLowerCase();
      const destination = label.endsWith("statement")
        ? "Statement"
        : label.endsWith("rationale")
          ? "Rationale"
          : label.endsWith("identifier") || label.endsWith(":id")
            ? "SourceIdentifier"
            : "SourceOnly";
      await select.selectOption(destination);
      if (destination === "SourceOnly") {
        const reasonLabel = rawLabel.replace(/^Mapping for /i, "");
        const reason = objectPanel.locator(`input[aria-label="Reason for ${reasonLabel}"]`);
        await expect(reason).toHaveCount(1);
        await reason.fill("Retain the exact foreign value as a source fact.");
      }
    }
  }
  await page.getByRole("button", { name: "Save choices and reconcile" }).click();
  await expect(page.getByText("Reconciliation ready", { exact: true })).toBeVisible({ timeout: 180_000 });
}

async function beginSetup(page: Page, name: string, startLabel: string) {
  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(name);
  await page.getByLabel("Software product").fill(`${name} software`);
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel(startLabel).check();
  await expect(page.getByRole("heading", { name: "Choose a starting point", level: 2 })).toBeVisible();
  const setupUrl = page.url();
  const draftId = new URL(setupUrl).pathname.split("/").pop() ?? "";
  expect(draftId).toMatch(/^[0-9a-f-]{36}$/i);
  return { setupUrl, draftId };
}

async function moveToRepository(page: Page, version: string) {
  await page.getByRole("button", { name: /3\. First build/ }).click();
  const versionInput = page.getByRole("textbox").first();
  await expect(versionInput).toBeVisible();
  await versionInput.fill(version);
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const acceptance = page.getByLabel(/explicitly accept these concrete review and approval rules/i);
  await expect(acceptance).toBeEnabled();
  await acceptance.check();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Repository setup", level: 2 })).toBeVisible();
}

async function reconcile(
  page: Page,
  draftId: string,
  categories: string[],
) {
  const current = await draft(page.request, draftId);
  const view = await source(page.request, draftId);
  const response = await page.request.put(`${apiBase}/api/project-setups/${draftId}/source/configuration`, {
    timeout: 180_000,
    data: {
      expectedVersion: current.version,
      selectedCategories: categories,
      mapping: configurationFor(view),
      metadata: {},
    },
  });
  await json(response);
  const reconciled = await source(page.request, draftId);
  expect(reconciled.reconciliation?.ready, JSON.stringify(reconciled.reconciliation)).toBeTruthy();
  expect(reconciled.reconciliation?.errors ?? []).toEqual([]);
  return reconciled;
}

async function finalizeSource(
  page: Page,
  setupUrl: string,
  testInfo: TestInfo,
  evidenceName: string,
) {
  await page.goto(setupUrl);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.getByText("Accept the source facts", { exact: true })).toBeVisible();
  const sourceAcceptance = page.getByLabel(/I accept this exact source assertion/i);
  await sourceAcceptance.check();
  await expect(sourceAcceptance).toBeChecked();
  await page.getByLabel("Password to finalize source acceptance").fill(password);
  await expect(sourceAcceptance).toBeChecked();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath(`${evidenceName}-ready.png`), fullPage: true });
  const finalizeResponse = page.waitForResponse((response) => response.url().includes("/finalize"));
  await page.getByRole("button", { name: "Create Project" }).click();
  const finalizeResult = await finalizeResponse;
  if (!finalizeResult.ok()) throw new Error(`Project finalization returned ${finalizeResult.status()}: ${await finalizeResult.text()}`);
  await page.waitForURL(/\/projects\/[0-9a-f-]+\/builds$/i, { timeout: 60_000 }).catch(async () => {
    const alerts = await page.getByRole("alert").allTextContents();
    throw new Error(`Project finalization did not complete: ${alerts.join(" | ")}`);
  });
  await expect(page.getByRole("heading", { name: "Software Builds", level: 1 })).toBeVisible();
  await expect(page.locator("[data-build-card]")).toHaveCount(1);
  await expect(page.locator("[data-build-card]").getByText("In Work", { exact: true })).toBeVisible();
  const projectId = new URL(page.url()).pathname.split("/")[2];
  expect(projectId).toMatch(/^[0-9a-f-]{36}$/i);
  await page.goto(`/projects/${projectId}/configuration`);
  await expect(page.getByRole("heading", { name: "Project configuration", level: 1 })).toBeVisible();
  await page.getByRole("button", { name: /Source provenance/ }).click();
  await expect(page.getByRole("heading", { name: "Source provenance", level: 2 })).toBeVisible();
  await expect(page.getByRole("heading", { name: /Inherited source records/ })).toBeVisible();
  await expect(page.getByText("Source acceptance fact", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath(`${evidenceName}-provenance.png`), fullPage: true });
  return projectId;
}

test("inherits an authorized native baseline with exact mapped traces and discoverable provenance", async ({ page }, testInfo) => {
  test.setTimeout(20 * 60 * 1000);
  await login(page, "admin", { openProject: false });
  const { setupUrl, draftId } = await beginSetup(page, `Native inception ${Date.now()}`, "Existing authorized AeroLink baseline");
  await expect(page.getByRole("button", { name: "Select exact baseline" }).first()).toBeVisible({ timeout: 120_000 });
  const options = await json<{ items: Array<{ baselineId: string; projectName: string; name: string; displayNumber: string; state: string; requirementsCount: number }> }>(
    await page.request.get(`${apiBase}/api/project-setups/source-options?offset=0&limit=50`),
  );
  const option = options.items.find((item) => item.projectName === "FMS Product Development" && ["Frozen", "Released"].includes(item.state));
  expect(option, "an authorized materialized native baseline").toBeTruthy();
  const nativeRow = page.locator("tr").filter({ hasText: option!.name }).filter({ hasText: option!.displayNumber });
  await nativeRow.getByRole("button", { name: "Select exact baseline" }).click();
  await expect(page.getByText("Exact source", { exact: true })).toBeVisible({ timeout: 180_000 });
  const captured = await source(page.request, draftId);
  expect(captured.kind).toBe("AeroLinkBaseline");
  expect(captured.selectedCategories).toEqual([]);
  await moveToRepository(page, "1.06");
  const reconciled = await reconcile(page, draftId, ["Requirements", "Traces"]);
  expect(reconciled.modules.some((module) => (module.objects?.length ?? 0) > 1)).toBeTruthy();
  expect(reconciled.relations.length).toBeGreaterThan(0);
  expect(reconciled.assertion).toBeTruthy();
  const projectId = await finalizeSource(page, setupUrl, testInfo, "native-inception");
  const projection = await json<{ projectId: string; package?: { kind: string; format: string; sourceBaselineId?: string; sha256: string }; acceptance?: { authority: string }; records: { sourceIdentifier: string; sourceRevision: string; sourceState: string }[] }>(
    await page.request.get(`${apiBase}/api/projects/${projectId}/inception-source`),
  );
  expect(projection.projectId).toBe(projectId);
  expect(projection.package?.kind).toBe("AeroLinkBaseline");
  expect(projection.package?.sourceBaselineId).toBe(option!.baselineId);
  expect(projection.package?.sourceState).toBeTruthy();
  expect(projection.records.length).toBeGreaterThan(0);
  expect(projection.records.some((record) => record.sourceRevision.length > 0)).toBeTruthy();
  expect(projection.acceptance?.authority).toBeTruthy();
});

test("source upload and mapping survive save-exit and a new signed-in browser context", async ({ page, browser }, testInfo) => {
  test.setTimeout(10 * 60 * 1000);
  const projectName = `Source recovery ${Date.now()}`;
  await login(page, "admin", { openProject: false });
  const { setupUrl, draftId } = await beginSetup(page, projectName, "External baseline from another tool");
  await page.getByLabel("Baseline file (ReqIF, CSV, or XLSX)").setInputFiles({
    name: "recovery.reqif",
    mimeType: "application/xml",
    buffer: Buffer.from(reqIfRootFixture, "utf8"),
  });
  await page.getByRole("button", { name: "Upload and analyze source" }).click();
  await expect(page.getByText("Exact source", { exact: true })).toBeVisible({ timeout: 120_000 });
  const reconciled = await reconcile(page, draftId, ["Requirements"]);
  expect(reconciled.reconciliation?.ready).toBeTruthy();

  // The focused helper reconciles through the versioned source endpoint directly. Refresh once to
  // model the panel's normal optimistic draft-version update before Save and exit. Reconciliation
  // moves the durable draft to Review, so verify the server source state before visiting the source
  // step again; the Review page intentionally summarizes the source rather than repeating its panel.
  await page.goto(setupUrl);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible({ timeout: 120_000 });
  const reloadedSource = await source(page.request, draftId);
  expect(reloadedSource.reconciliation?.ready, JSON.stringify(reloadedSource.reconciliation)).toBeTruthy();
  expect(reloadedSource.id).toBe(reconciled.id);
  expect(reloadedSource.sha256).toBe(reconciled.sha256);
  expect(reloadedSource.assertion?.hash).toBe(reconciled.assertion?.hash);
  await page.getByRole("button", { name: "Save and exit" }).click();
  await expect(page.getByRole("heading", { name: "Projects", level: 1 })).toBeVisible();
  await expect(page.getByText(projectName, { exact: true })).toBeVisible();
  await page.context().close();

  const resumedContext = await browser.newContext();
  const resumedPage = await resumedContext.newPage();
  try {
    await login(resumedPage, "admin", { openProject: false });
    await expect(resumedPage.getByText(projectName, { exact: true })).toBeVisible({ timeout: 30_000 });
    const resumedDraft = resumedPage.getByRole("article").filter({ hasText: projectName });
    await expect(resumedDraft).toHaveCount(1);
    await resumedDraft.getByRole("button", { name: "Resume setup" }).click();
    await expect(resumedPage.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
    const resumedSource = await source(resumedPage.request, draftId);
    expect(resumedSource.reconciliation?.ready, JSON.stringify(resumedSource.reconciliation)).toBeTruthy();
    expect(resumedSource.id).toBe(reconciled.id);
    expect(resumedSource.sha256).toBe(reconciled.sha256);
    expect(resumedSource.assertion?.hash).toBe(reconciled.assertion?.hash);
    await resumedPage.getByRole("button", { name: /2\. Starting point/ }).click();
    await expect(resumedPage.getByRole("heading", { name: "Choose a starting point", level: 2 })).toBeVisible();
    await expect(resumedPage.getByText("Exact source", { exact: true })).toBeVisible({ timeout: 120_000 });
    await expect(resumedPage.getByText("Reconciliation ready", { exact: true })).toBeVisible();
    await expect(resumedPage.getByLabel(/Mapping for Source objects REQ-SYS attribute:statement$/)).toHaveValue("Statement");
    await resumedPage.screenshot({ path: testInfo.outputPath("source-recovery-resumed.png"), fullPage: true });
  } finally {
    await resumedContext.close();
  }
});

const externalFixtures = [
  {
    name: "ReqIF hierarchical",
    format: "REQIF",
    fileName: "inception.reqif",
    mimeType: "application/xml",
    buffer: Buffer.from(reqIfFixture, "utf8"),
    categories: ["Requirements", "Traces"],
  },
  {
    name: "ReqIF root-only",
    format: "REQIF",
    fileName: "inception-root.reqif",
    mimeType: "application/xml",
    buffer: Buffer.from(reqIfRootFixture, "utf8"),
    categories: ["Requirements"],
  },
  {
    name: "CSV",
    format: "CSV",
    fileName: "inception.csv",
    mimeType: "text/csv",
    buffer: Buffer.from("Identifier,Level,Statement,Rationale\r\nCSV-1,System,CSV source statement,CSV source rationale\r\n", "utf8"),
    categories: ["Requirements"],
  },
  { name: "XLSX", format: "XLSX", fileName: "inception.xlsx", mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", buffer: xlsxFixture, categories: ["Requirements"] },
] as const;

for (const fixture of externalFixtures) {
  test(`imports ${fixture.name} through the server parser and retains provenance`, async ({ page }, testInfo) => {
    test.setTimeout(10 * 60 * 1000);
    await login(page, "admin", { openProject: false });
    const { setupUrl, draftId } = await beginSetup(page, `${fixture.name} inception ${Date.now()}`, "External baseline from another tool");
    await page.getByLabel("Baseline file (ReqIF, CSV, or XLSX)").setInputFiles({
      name: fixture.fileName,
      mimeType: fixture.mimeType,
      buffer: fixture.buffer,
    });
    await page.getByRole("button", { name: "Upload and analyze source" }).click();
    await expect(page.getByText("Exact source", { exact: true })).toBeVisible({ timeout: 120_000 });
    const staged = await source(page.request, draftId);
    expect(staged.kind).toBe("ExternalBaseline");
    expect(staged.format).toBe(fixture.format);
    expect(staged.fileName).toBe(fixture.fileName);
    await expect(page.getByRole("region", { name: "Source-informed ladder suggestion" })).toBeVisible();
    if (fixture.name === "ReqIF hierarchical") {
      // This fixture is the explicit regression for heterogeneous hierarchical source objects.
      // Drive every mapping decision through the source panel so the browser path proves that
      // exact per-object choices, relation direction, and dependency selection are usable.
      const requirements = page.getByRole("checkbox", { name: /^Requirements / });
      if (!(await requirements.isChecked())) await requirements.check();
      const traces = page.getByRole("checkbox", { name: /^Trace relationships / });
      if (!(await traces.isChecked())) await traces.check();
      const system = page.locator("section.setupSourceObjectMapping").filter({ hasText: "REQ-SYS" });
      const high = page.locator("section.setupSourceObjectMapping").filter({ hasText: "REQ-HIGH" });
      await expect(system).toHaveCount(1);
      await expect(high).toHaveCount(1);
      await system.getByRole("combobox").first().selectOption("System");
      await high.getByRole("combobox").first().selectOption("HighLevel");
      await system.getByLabel(/Mapping for .*statement$/i).selectOption("Statement");
      await high.getByLabel(/Mapping for .*statement$/i).selectOption("Statement");
      await system.getByLabel(/Mapping for .*rationale$/i).selectOption("Rationale");
      await high.getByLabel(/Mapping for .*rationale$/i).selectOption("Rationale");
      for (const objectPanel of [system, high]) {
        const reasons = objectPanel.locator('input[aria-label^="Reason for "]');
        for (let index = 0; index < await reasons.count(); index += 1) {
          await reasons.nth(index).fill("Retain the exact foreign value as a source fact.");
        }
      }
      await page.getByLabel(/Trace type for/i).selectOption("AllocatedFrom");
      await page.getByLabel(/Relation direction for/i).selectOption("child");
      await page.getByRole("button", { name: "Save choices and reconcile" }).click();
      await expect(page.getByText("Reconciliation ready", { exact: true })).toBeVisible({ timeout: 180_000 });
    } else {
      await configureSimpleExternalSource(page);
    }
    await moveToRepository(page, fixture.format === "REQIF" ? (fixture.name.includes("root") ? "1.10" : "1.07") : fixture.format === "CSV" ? "1.08" : "1.09");
    // Source configuration does not implicitly change the setup step. Persist the repository
    // choice and enter the review step explicitly before the finalization helper reloads it.
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
    const reconciled = await source(page.request, draftId);
    expect(reconciled.reconciliation?.ready).toBeTruthy();
    expect(reconciled.assertion?.hash).toMatch(/^[0-9a-f]{64}$/i);
    const projectId = await finalizeSource(page, setupUrl, testInfo, `external-${fixture.name.toLocaleLowerCase().replaceAll(" ", "-")}`);
    const projection = await json<{ projectId: string; package?: { kind: string; format: string; fileName: string; sha256: string }; records: { sourceIdentifier: string; sourceSnapshot: unknown }[] }>(
      await page.request.get(`${apiBase}/api/projects/${projectId}/inception-source`),
    );
    expect(projection.projectId).toBe(projectId);
    expect(projection.package?.kind).toBe("ExternalBaseline");
    expect(projection.package?.format).toBe(fixture.format);
    expect(projection.package?.fileName).toBe(fixture.fileName);
    expect(projection.records.length).toBeGreaterThan(0);
    expect(projection.records.some((record) => record.sourceIdentifier.length > 0)).toBeTruthy();
    expect(JSON.stringify(projection)).not.toContain("StorageKey");
    if (fixture.name === "ReqIF root-only") {
      const facts = page.locator("details").first();
      await facts.locator("summary").click();
      await expect(facts).toContainText("The system shall retain the ReqIF source fact.");
    }
    if (fixture.name === "CSV") {
      const facts = page.locator("details").first();
      await facts.locator("summary").click();
      await expect(facts).toContainText("CSV source statement");
    }
  });
}
