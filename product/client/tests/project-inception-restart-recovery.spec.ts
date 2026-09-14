import { existsSync, readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { test, expect, type APIRequestContext, type Page } from "@playwright/test";
import { apiBase, login } from "./auth";

/**
 * Cross-process recovery qualification for #1037. The two phases are deliberately separate test
 * invocations: the Playwright web server owns and tears down the API process between them. Set
 * AEROLINK_RESTART_RECOVERY_STATE_FILE to an owned temporary path and run against the disposable
 * PostgreSQL config; the default suite skips this operator-driven qualification.
 */
const phase = process.env.AEROLINK_RESTART_RECOVERY_PHASE;
const stateFile = process.env.AEROLINK_RESTART_RECOVERY_STATE_FILE
  ?? join(tmpdir(), "aerolink-1037-restart-recovery.json");
const sourceFixture = `<REQ-IF>
  <REQ-IF-HEADER><SOURCE-TOOL-ID>Restart recovery ReqIF tool</SOURCE-TOOL-ID></REQ-IF-HEADER>
  <SPEC-TYPES><SPEC-OBJECT-TYPE IDENTIFIER="REQ">
    <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="id" LONG-NAME="Identifier"/>
    <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="level" LONG-NAME="Level"/>
    <ATTRIBUTE-DEFINITION-STRING IDENTIFIER="statement" LONG-NAME="Statement"/>
  </SPEC-OBJECT-TYPE></SPEC-TYPES>
  <SPEC-OBJECTS><SPEC-OBJECT IDENTIFIER="restart-req"><TYPE><SPEC-OBJECT-TYPE-REF>REQ</SPEC-OBJECT-TYPE-REF></TYPE><VALUES>
    <ATTRIBUTE-VALUE-STRING THE-VALUE="RESTART-REQ"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>id</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
    <ATTRIBUTE-VALUE-STRING THE-VALUE="System"><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>level</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
    <ATTRIBUTE-VALUE-STRING THE-VALUE="The restart recovery source fact shall remain attributable."><DEFINITION><ATTRIBUTE-DEFINITION-STRING-REF>statement</ATTRIBUTE-DEFINITION-STRING-REF></DEFINITION></ATTRIBUTE-VALUE-STRING>
  </VALUES></SPEC-OBJECT></SPEC-OBJECTS>
</REQ-IF>`;

type Draft = {
  draftId: string;
  version: number;
  project: { name: string; softwareProduct: string };
  start: { kind?: string; sourceBaselineId?: string | null; sourceImportId?: string | null };
  build: { version: string };
  ladder: unknown;
  reviewRules: { definition?: unknown; suggestedDefinition?: unknown; accepted?: boolean };
  repository: unknown;
};

type RecoveryRecord = {
  projectName: string;
  draftId: string;
  kind: "Fresh" | "AeroLinkBaseline" | "ExternalBaseline";
  sourceId?: string;
  sourceBaselineId?: string;
  sourceSha256?: string;
  assertionHash?: string;
  expectedSourceKey?: string;
  expectedSourceAttribute?: string;
  expectedMappingDestination?: string;
};

function responseJson<T>(response: Awaited<ReturnType<APIRequestContext["get"]>>): Promise<T> {
  return response.text().then((body) => {
    expect(response.ok(), body).toBeTruthy();
    return JSON.parse(body) as T;
  });
}

async function getDraft(request: APIRequestContext, draftId: string) {
  return responseJson<Draft>(await request.get(`${apiBase}/api/project-setups/${draftId}`));
}

async function getSource(request: APIRequestContext, draftId: string) {
  const response = await request.get(`${apiBase}/api/project-setups/${draftId}/source`);
  return responseJson<Record<string, unknown>>(response);
}

function defaultLadder() {
  return {
    steps: [
      { catalogueEntry: "System", position: 1, capabilities: 7, enabledArtifactKinds: ["Procedure"] },
      { catalogueEntry: "HighLevel", position: 2, capabilities: 7, enabledArtifactKinds: ["Case", "Procedure"] },
      { catalogueEntry: "LowLevel", position: 3, capabilities: 15, enabledArtifactKinds: ["Case", "Procedure"] },
    ],
    relationships: [
      { parent: "System", child: "HighLevel" },
      { parent: "HighLevel", child: "LowLevel" },
    ],
  };
}

function attributeNameMatches(key: string, name: string) {
  const normalized = key.toLocaleLowerCase();
  const expected = name.toLocaleLowerCase();
  return normalized === expected || normalized.endsWith(`:${expected}`);
}

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function sourceMapping(view: Record<string, unknown>) {
  const modules = Array.isArray(view.modules) ? view.modules : [];
  const objects = modules.flatMap((module) => {
    const row = module as Record<string, unknown>;
    return Array.isArray(row.objects) ? row.objects as Record<string, unknown>[] : [];
  });
  const included = new Set(objects
    .filter((object) => ["Requirement", "Unmapped", ""].includes(String(object.kind ?? "")))
    .map((object) => String(object.key)));
  return {
    sourceSha256: String(view.sha256 ?? ""),
    objects: objects.map((object) => {
      const attributes = object.attributes && typeof object.attributes === "object"
        ? object.attributes as Record<string, unknown>
        : {};
      const isRequirement = ["Requirement", "Unmapped", ""].includes(String(object.kind ?? ""));
      if (!isRequirement) {
        return {
          sourceKey: String(object.key),
          include: false,
          exclusionReason: "Excluded from this restart recovery qualification.",
          attributes: [],
        };
      }
      const level = Object.entries(attributes).find(([key]) => attributeNameMatches(key, "Level"))?.[1];
      return {
        sourceKey: String(object.key),
        include: true,
        level: typeof level === "string" && level ? level : "System",
        attributes: Object.entries(attributes).map(([key]) => {
          const destination = attributeNameMatches(key, "Statement")
            ? "Statement"
            : attributeNameMatches(key, "Rationale")
              ? "Rationale"
              : attributeNameMatches(key, "Identifier") || attributeNameMatches(key, "ID")
                ? "SourceIdentifier"
                : "SourceOnly";
          return {
            sourceAttribute: key,
            destination,
            ...(destination === "SourceOnly"
              ? { reason: "Retain the exact foreign value as a source fact." }
              : {}),
          };
        }),
      };
    }),
    relations: (Array.isArray(view.relations) ? view.relations : []).map((relation) => {
      const row = relation as Record<string, unknown>;
      const sourceKey = String(row.sourceKey ?? "");
      const targetKey = String(row.targetKey ?? "");
      const relationType = String(row.type ?? row.sourceType ?? "");
      const trace = relationType.toLocaleLowerCase() === "derivedfrom"
        && included.has(sourceKey) && included.has(targetKey);
      return trace
        ? { sourceKey: String(row.key ?? sourceKey), include: true, type: "AllocatedFrom", sourceIsParent: false }
        : { sourceKey: String(row.key ?? sourceKey), include: false, exclusionReason: "Excluded from this restart recovery qualification." };
    }),
    findingResolutions: Object.fromEntries(
      (Array.isArray(view.findings) ? view.findings : []).map((finding) => {
        const row = typeof finding === "string" ? { key: finding } : finding as Record<string, unknown>;
        const key = String(row.key ?? "");
        return [key, "Reviewed and retained as a source finding for this qualification."];
      }),
    ),
  };
}

async function createDraft(page: Page, name: string) {
  const created = await responseJson<Draft>(await page.request.post(`${apiBase}/api/project-setups`, {
    data: { projectName: name },
  }));
  const ladder = defaultLadder();
  const saved = await responseJson<Draft>(await page.request.put(`${apiBase}/api/project-setups/${created.draftId}`, {
    data: {
      expectedVersion: created.version,
      currentStep: "StartingPoint",
      project: { name, softwareProduct: `${name} software` },
      build: { version: "1.02" },
      selectedCategories: [],
      ladder,
      reviewRules: {},
      reviewRulesAccepted: false,
      repository: { mode: "ConfigureLater", provider: "GitLab" },
      mapping: {},
    },
  }));
  return saved;
}

async function captureExternal(page: Page, draft: Draft, format: "ReqIF" | "CSV" | "XLSX") {
  const files = {
    ReqIF: { fileName: "restart.reqif", mimeType: "application/xml", buffer: Buffer.from(sourceFixture, "utf8") },
    CSV: { fileName: "restart.csv", mimeType: "text/csv", buffer: Buffer.from("Identifier,Level,Statement\nRESTART-CSV,System,CSV restart source fact\n", "utf8") },
    XLSX: {
      fileName: "restart.xlsx",
      mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      buffer: Buffer.from(readFileSync(resolve("tests/project-inception-all-paths.spec.ts"), "utf8")
        .match(/const xlsxFixture = Buffer\.from\(\s*"([^"]+)"/)?.[1] ?? "", "base64"),
    },
  }[format];
  const uploaded = await responseJson<Record<string, unknown>>(await page.request.post(
    `${apiBase}/api/project-setups/${draft.draftId}/source/upload?expectedVersion=${draft.version}&fileName=${encodeURIComponent(files.fileName)}`,
    { headers: { "Content-Type": "application/octet-stream" }, data: files.buffer },
  ));
  expect(String(uploaded.id)).toMatch(/^[0-9a-f-]{36}$/i);
  const view = await getSource(page.request, draft.draftId);
  const source = (view.source ?? view) as Record<string, unknown>;
  const sourceDraft = await getDraft(page.request, draft.draftId);
  const categories = format === "ReqIF" ? ["Requirements"] : ["Requirements"];
  const configured = await responseJson<Record<string, unknown>>(await page.request.put(
    `${apiBase}/api/project-setups/${draft.draftId}/source/configuration`,
    {
      data: {
        expectedVersion: sourceDraft.version,
        selectedCategories: categories,
        mapping: sourceMapping(source),
        metadata: {},
      },
    },
  ));
  expect(String(configured.id)).toBe(String(uploaded.id));
  const reconciled = await getSource(page.request, draft.draftId);
  const reconciledSource = (reconciled.source ?? reconciled) as Record<string, unknown>;
  const reconciledState = (reconciledSource.reconciliation ?? {}) as Record<string, unknown>;
  expect(reconciledState.ready).toBe(true);
  const assertion = (reconciledSource.assertion ?? {}) as Record<string, unknown>;
  const mapping = sourceMapping(reconciledSource) as { objects: { sourceKey: string; attributes: { sourceAttribute: string; destination: string }[] }[] };
  const first = mapping.objects[0];
  const mappedStatement = first?.attributes.find((attribute) => attribute.destination === "Statement");
  return {
    sourceId: String(reconciledSource.id),
    sourceSha256: String(reconciledSource.sha256),
    assertionHash: String(assertion.hash ?? ""),
    expectedSourceKey: first?.sourceKey,
    expectedSourceAttribute: mappedStatement?.sourceAttribute,
    expectedMappingDestination: mappedStatement?.destination,
  };
}

async function persistReview(
  page: Page,
  draftId: string,
  kind: RecoveryRecord["kind"],
  sourceIdentity?: string,
) {
  const current = await getDraft(page.request, draftId);
  const start = kind === "Fresh"
    ? { kind }
    : kind === "AeroLinkBaseline"
      ? { kind, sourceBaselineId: sourceIdentity }
      : { kind, sourceImportId: sourceIdentity };
  const savedEnvelope = await responseJson<{ saved: boolean; draft: Draft }>(await page.request.post(`${apiBase}/api/project-setups/${draftId}/save-and-exit`, {
    data: {
      expectedVersion: current.version,
      currentStep: "Review",
      project: current.project,
      start,
      build: { version: "1.02" },
      ladder: defaultLadder(),
      reviewRules: {},
      reviewRulesAccepted: true,
      repository: { mode: "ConfigureLater", provider: "GitLab" },
    },
  }));
  const saved = savedEnvelope.draft;
  expect(savedEnvelope.saved).toBe(true);
  expect(saved.draftId).toBe(draftId);
  expect(saved.currentStep).toBe("Review");
  expect(saved.reviewRules.accepted).toBe(true);
  expect(saved.start.kind).toBe(kind);
  return saved;
}

test("prepare five disposable drafts for a separate API-process recovery invocation", async ({ page }) => {
  test.skip(phase !== "prepare", "Set AEROLINK_RESTART_RECOVERY_PHASE=prepare to run the first recovery invocation.");
  test.setTimeout(20 * 60 * 1000);
  await login(page, "admin", { openProject: false });
  const records: RecoveryRecord[] = [];
  const freshName = `Restart recovery Fresh ${Date.now()}`;
  const fresh = await createDraft(page, freshName);
  await persistReview(page, fresh.draftId, "Fresh");
  records.push({ projectName: freshName, draftId: fresh.draftId, kind: "Fresh" });

  const nativeName = `Restart recovery Native ${Date.now()}`;
  const native = await createDraft(page, nativeName);
  const optionsResponse = await page.request.get(`${apiBase}/api/project-setups/source-options?offset=0&limit=50`);
  const optionsValue = await responseJson<unknown>(optionsResponse);
  const options = (Array.isArray(optionsValue) ? optionsValue : (optionsValue as { items?: unknown[] }).items ?? []) as Record<string, unknown>[];
  const option = options.find((item) => ["Frozen", "Released"].includes(String(item.state)) && String(item.projectName) === "FMS Product Development");
  expect(option, "an authorized materialized baseline for restart recovery").toBeTruthy();
  const nativeCapture = await responseJson<Record<string, unknown>>(await page.request.post(`${apiBase}/api/project-setups/${native.draftId}/source/native`, {
    data: { expectedVersion: native.version, baselineId: option!.baselineId },
  }));
  const nativeViewEnvelope = await getSource(page.request, native.draftId);
  const nativeView = (nativeViewEnvelope.source ?? nativeViewEnvelope) as Record<string, unknown>;
  const nativeDraft = await getDraft(page.request, native.draftId);
  const nativeConfigured = await responseJson<Record<string, unknown>>(await page.request.put(`${apiBase}/api/project-setups/${native.draftId}/source/configuration`, {
    data: { expectedVersion: nativeDraft.version, selectedCategories: ["Requirements", "Traces"], mapping: sourceMapping(nativeView), metadata: {} },
  }));
  expect(String(nativeConfigured.id)).toBe(String(nativeCapture.id));
  const nativeSourceEnvelope = await getSource(page.request, native.draftId);
  const nativeSource = (nativeSourceEnvelope.source ?? nativeSourceEnvelope) as Record<string, unknown>;
  expect((nativeSource.reconciliation as Record<string, unknown>)?.ready).toBe(true);
  const nativeSourceBaselineId = String(nativeSource.sourceBaselineId ?? option!.baselineId);
  await persistReview(page, native.draftId, "AeroLinkBaseline", nativeSourceBaselineId);
  records.push({
    projectName: nativeName,
    draftId: native.draftId,
    kind: "AeroLinkBaseline",
    sourceId: String(nativeSource.id),
    sourceBaselineId: nativeSourceBaselineId,
    sourceSha256: String(nativeSource.sha256),
    assertionHash: String((nativeSource.assertion as Record<string, unknown> | undefined)?.hash ?? ""),
    expectedSourceKey: (sourceMapping(nativeSource) as { objects: { sourceKey: string }[] }).objects[0]?.sourceKey,
    expectedSourceAttribute: ((sourceMapping(nativeSource) as {
      objects: { sourceKey: string; attributes: { sourceAttribute: string; destination: string }[] }[];
    }).objects[0]?.attributes.find((attribute) => attribute.destination === "Statement"))?.sourceAttribute,
    expectedMappingDestination: "Statement",
  });

  for (const format of ["ReqIF", "CSV", "XLSX"] as const) {
    const name = `Restart recovery ${format} ${Date.now()}`;
    const draft = await createDraft(page, name);
    const sourceRecord = await captureExternal(page, draft, format);
    await persistReview(page, draft.draftId, "ExternalBaseline", sourceRecord.sourceId);
    records.push({ projectName: name, draftId: draft.draftId, kind: "ExternalBaseline", ...sourceRecord });
  }
  mkdirSync(resolve(stateFile, ".."), { recursive: true });
  writeFileSync(stateFile, JSON.stringify({ records }, null, 2), "utf8");
  console.log(`Restart recovery prepare wrote ${records.length} drafts to ${stateFile}`);
});

test("resume all five drafts after the API process has been restarted", async ({ page }, testInfo) => {
  test.skip(phase !== "verify", "Set AEROLINK_RESTART_RECOVERY_PHASE=verify to run the second recovery invocation.");
  test.setTimeout(20 * 60 * 1000);
  expect(existsSync(stateFile), `Recovery state file is required: ${stateFile}`).toBeTruthy();
  const state = JSON.parse(readFileSync(stateFile, "utf8")) as { records: RecoveryRecord[] };
  expect(state.records).toHaveLength(5);
  await login(page, "admin", { openProject: false });
  for (const record of state.records) {
    await page.goto("/projects");
    const draft = page.getByRole("article").filter({ hasText: record.projectName });
    await expect(draft).toHaveCount(1);
    const serverBeforeResume = await getDraft(page.request, record.draftId);
    expect(serverBeforeResume.currentStep, `saved step for ${record.projectName}`).toBe("Review");
    await draft.getByRole("button", { name: "Resume setup" }).click();
    await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
    const loaded = await getDraft(page.request, record.draftId);
    expect(loaded.project.name).toBe(record.projectName);
    expect(loaded.reviewRules.accepted).toBe(true);
    expect(loaded.build.version).toBe("1.02");
    if (record.kind === "Fresh") {
      const sourceResponse = await page.request.get(`${apiBase}/api/project-setups/${record.draftId}/source`);
      expect(sourceResponse.status()).toBe(404);
      await page.getByRole("button", { name: /2\. Starting point/ }).click();
      await expect(page.getByRole("heading", { name: "Choose a starting point", level: 2 })).toBeVisible();
      await expect(page.getByText("Exact source", { exact: true })).toHaveCount(0);
      continue;
    }
    const sourceEnvelope = await getSource(page.request, record.draftId);
    const source = (sourceEnvelope.source ?? sourceEnvelope) as Record<string, unknown>;
    expect(String(source.id)).toBe(record.sourceId);
    expect(String(source.sha256)).toBe(record.sourceSha256);
    expect((source.reconciliation as Record<string, unknown>)?.ready).toBe(true);
    const mapping = source.mapping as { objects?: { sourceKey: string; attributes?: { destination: string }[] }[] } | undefined;
    if (record.expectedSourceKey) expect(mapping?.objects?.some((object) => object.sourceKey === record.expectedSourceKey)).toBe(true);
    if (record.expectedMappingDestination) expect(JSON.stringify(mapping)).toContain(record.expectedMappingDestination);
    await page.getByRole("button", { name: /2\. Starting point/ }).click();
    // Native source snapshots may contain hundreds of exact object controls; allow the browser
    // to finish painting that authoritative panel before asserting its retained choices.
    await expect(page.getByRole("heading", { name: "Choose a starting point", level: 2 })).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText("Exact source", { exact: true })).toBeVisible();
    await expect(page.getByText("Reconciliation ready", { exact: true })).toBeVisible();
    await expect(page.getByRole("checkbox", { name: /^Requirements/ })).toBeChecked();
    if (record.expectedSourceKey) {
      const sourceObject = page.locator("section.setupSourceObjectMapping").filter({ hasText: record.expectedSourceKey });
      await expect(sourceObject).toHaveCount(1);
      if (record.expectedSourceAttribute) {
        const statementMapping = sourceObject
          .getByLabel(new RegExp(` ${escapeRegExp(record.expectedSourceAttribute)}$`))
          .first();
        await expect(statementMapping).toHaveValue(record.expectedMappingDestination ?? "Statement");
      } else {
        throw new Error(`The source mapping has no explicit statement attribute for ${record.expectedSourceKey}.`);
      }
    }
    // Native baselines can contain hundreds of exact source objects. Capture the visible
    // selection/reconciliation state without asking Chromium to rasterize the entire long panel.
    await page.screenshot({ path: testInfo.outputPath(`restart-recovery-${record.kind}-${record.draftId}.png`) });
    const afterSource = await getDraft(page.request, record.draftId);
    const finalization = await responseJson<{ state: string; projectId: string; releaseId: string }>(
      await page.request.post(`${apiBase}/api/project-setups/${record.draftId}/finalize`, {
        headers: { "Idempotency-Key": `restart-recovery-${record.draftId}` },
        data: {
          expectedVersion: afterSource.version,
          idempotencyKey: `restart-recovery-${record.draftId}`,
          ...(record.kind === "Fresh" ? {} : {
            sourceAssertionHash: record.assertionHash,
            sourceAssertionAccepted: true,
            password: "AeroLink!2026",
          }),
        },
      }),
    );
    expect(finalization.state).toBe("Completed");
    expect(finalization.projectId).toMatch(/^[0-9a-f-]{36}$/i);
    expect(finalization.releaseId).toMatch(/^[0-9a-f-]{36}$/i);
    await page.getByRole("button", { name: "Sign out" }).click();
    await login(page, "admin", { openProject: false });
  }
});
