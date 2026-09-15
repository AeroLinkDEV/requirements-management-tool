import { expect, test } from "@playwright/test";
import type { Locator, Page } from "@playwright/test";
import { apiBase, login } from "./auth";
import {
  compatibleRememberedProfile,
  enabledVerificationProfileInvalid,
  profileSelection,
  savedArtifactsLabel,
} from "../src/projectSetupVerificationProfile";

/**
 * Screenshots are captured into the run's own output directory. Promoting one into
 * product/docs/screenshots is a deliberate, separate step, so a routine test run never overwrites the
 * tracked review evidence the reviewer is looking at.
 */

/**
 * #1045 browser coverage for a project setup whose verification capability is disabled.
 *
 * Evidence categories stay separate on purpose:
 *  - journeys 1, 2a, 2b, 5, 6, 7, 8, 9 and 10 drive the real walkthrough against the real API with a
 *    disposable database;
 *  - journey 3 uses a controlled server refusal delivered by the test harness (transport fixture);
 *  - journey 4 uses a genuine client transport failure, which is a fixture for client behaviour and is
 *    *not* evidence about the transaction;
 *  - journey 4b mocks a failed recovery read, and 4c a server-reported in-flight finalization.
 *
 * The committed-but-response-lost claim against the real server stays in project-creation-acceptance.spec.ts.
 */

type PersistedStep = { catalogueEntry: string; capabilities: number; enabledArtifactKinds?: string[] };

async function persistedDraft(page: Page, draftId: string) {
  const response = await page.request.get(`${apiBase}/api/project-setups/${draftId}`);
  expect(response.ok(), await response.text()).toBeTruthy();
  return (await response.json()) as {
    state: string;
    version: number;
    ladder: { steps: PersistedStep[] };
    validation?: {
      version: number;
      ladderValid: boolean;
      configurationReady: boolean;
      findings: { code: string; level?: string | null; token?: string | null; message: string }[];
      steps: { level: string; stored?: string[] | null; effective: string[]; profileSource: string }[];
      review: { covers: boolean; accepted: boolean };
    } | null;
  };
}

function step(persisted: Awaited<ReturnType<typeof persistedDraft>>, level: string) {
  return persisted.ladder.steps.find((item) => item.catalogueEntry === level);
}

async function startFreshDraftAtLadder(page: Page, projectName: string, version: string) {
  await page.goto("/projects/new");
  await expect(page.getByRole("heading", { name: "Create New Project", level: 1 })).toBeVisible();
  await page.getByLabel("Project name").fill(projectName);
  await page.getByLabel("Software product").fill(`${projectName} software`);
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Fresh project").check();
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Version").fill(version);
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  return new URL(page.url()).pathname.split("/").pop() ?? "";
}

const systemRowOf = (page: Page) => page.locator(".setupLadderRows > li").nth(0);
const highLevelRowOf = (page: Page) => page.locator(".setupLadderRows > li").nth(1);
const verificationCheckbox = (row: ReturnType<typeof systemRowOf>) =>
  row.getByRole("checkbox", { name: "Verification", exact: true });

function levelFinding(page: Page, level: RegExp) {
  return page
    .locator('[role="alert"], [role="status"]')
    .filter({ hasText: level })
    .filter({ hasText: /verification/i });
}

/** Applies the server's current standard for this ladder, reviews it, and accepts it. */
async function applyCurrentStandardAndAccept(page: Page) {
  const refresh = page.getByRole("button", { name: "Use rules for this ladder" });
  if (await refresh.isVisible().catch(() => false)) await refresh.click();
  const accepted = page.getByLabel(/explicitly accept these concrete review and approval rules/i);
  await expect(accepted).toBeEnabled();
  if (!(await accepted.isChecked())) await accepted.check();
}

/**
 * Re-ticks acceptance over whatever definition is on screen without applying the current standard.
 * Whether the UI prevents that (by disabling the control) or permits it, the draft must not be ready;
 * the negative assertion below therefore holds either way and does not require the defect.
 */
async function acceptStaleDefinitionIfPermitted(page: Page) {
  const accepted = page.getByLabel(/explicitly accept these concrete review and approval rules/i);
  if (await accepted.isEnabled().catch(() => false)) await accepted.check().catch(() => undefined);
}

async function continueToReview(page: Page) {
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Repository setup", level: 2 })).toBeVisible();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
}

/**
 * A saved draft body generated by the real server for this exact ladder: the accepted definition and the
 * readiness verdict come from the shared authorities, not from a hand-built fixture. Transport-mocking
 * journeys clone this shape so a mocked response is labelled honestly while still being coherent.
 */
async function serverShapedDraft(page: Page, projectName: string, ladder: unknown) {
  const created = await page.request.post(`${apiBase}/api/project-setups`, { data: { projectName } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const draftId = ((await created.json()) as { draftId: string }).draftId;
  const saved = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      expectedVersion: 1,
      currentStep: "Review",
      project: { name: projectName, softwareProduct: `${projectName} software` },
      start: { kind: "Fresh" },
      build: { version: "0.01" },
      selectedCategories: [],
      ladder,
      reviewRules: {},
      reviewRulesAccepted: true,
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  const read = await page.request.get(`${apiBase}/api/project-setups/${draftId}`);
  expect(read.ok(), await read.text()).toBeTruthy();
  return (await read.json()) as Record<string, unknown> & { validation: Record<string, unknown> };
}

test("disabling verification at System leaves no enabled verification artifact", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Disabled system ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  const systemRow = systemRowOf(page);
  await verificationCheckbox(systemRow).uncheck();
  // A disabled capability must be unambiguous where it is owned, not a cleared checkbox beside an
  // artifact the level still enables.
  await expect(systemRow).toContainText(/verification (is )?(off|disabled)|no verification artifacts/i);

  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();

  const persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "System")?.capabilities, "System keeps its unrelated capabilities").toBe(5);
  expect(step(persisted, "System")?.enabledArtifactKinds, "disabled verification enables no artifact").toEqual([]);
  expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual(["Case", "Procedure"]);
  expect(step(persisted, "LowLevel")?.enabledArtifactKinds).toEqual(["Case", "Procedure"]);
  expect(persisted.validation?.ladderValid, "the ordinary toggle now produces a coherent ladder").toBe(true);
  await page.screenshot({ path: testInfo.outputPath("disabled-system-ladder.png"), fullPage: true });
});

test("re-ticking a stale rule definition cannot make a changed ladder ready", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Stale accepted ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  await verificationCheckbox(systemRowOf(page)).uncheck();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  await acceptStaleDefinitionIfPermitted(page);
  await continueToReview(page);

  await expect(page.getByRole("button", { name: "Create Project" })).toBeDisabled();
  await expect(page.locator(".setupReadyNotice")).toHaveCount(0);
  await expect(
    page.locator('[role="alert"], [role="status"]').filter({ hasText: /rules|subjects/i }).first(),
  ).toBeVisible();

  const persisted = await persistedDraft(page, draftId);
  expect(persisted.state).toBe("Draft");
  expect(persisted.validation?.review.covers).toBe(false);
  expect(step(persisted, "System")?.capabilities).toBe(5);
});

test("a saved draft already carrying stale accepted rules is not ready on load", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Stale saved ${Date.now().toString(36)}`;
  const created = await page.request.post(`${apiBase}/api/project-setups`, { data: { projectName } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const draftId = ((await created.json()) as { draftId: string }).draftId;

  // First save with System verification enabled, so the accepted standard covers SystemTest.
  const enabledLadder = {
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
  const base = {
    project: { name: projectName, softwareProduct: `${projectName} software` },
    start: { kind: "Fresh" },
    build: { version: "0.01" },
    selectedCategories: [],
    reviewRules: {},
    reviewRulesAccepted: true,
    repository: { mode: "ConfigureLater" },
    mapping: {},
  };
  const enabled = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: { ...base, expectedVersion: 1, currentStep: "WorkingRules", ladder: enabledLadder },
  });
  expect(enabled.ok(), await enabled.text()).toBeTruthy();
  const staleDefinition = (await enabled.json()).reviewRules.definition as unknown;

  // Then disable System verification while keeping the definition written for the previous ladder. This
  // is a saved draft whose acceptance is stale, independent of any checkbox behaviour in the walkthrough.
  const disabled = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      ...base,
      expectedVersion: 2,
      currentStep: "Review",
      ladder: {
        ...enabledLadder,
        steps: [{ ...enabledLadder.steps[0], capabilities: 5, enabledArtifactKinds: [] }, ...enabledLadder.steps.slice(1)],
      },
      reviewRules: staleDefinition,
    },
  });
  expect(disabled.ok(), await disabled.text()).toBeTruthy();

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Create New Project", level: 1 })).toBeVisible();
  await page.getByRole("button", { name: /Review and finish/ }).click();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeDisabled();
  await expect(page.getByText(/do not cover this saved ladder exactly/i)).toBeVisible();
  await expect(page.locator(".setupReadyNotice")).toHaveCount(0);
});

test("a refused finalization keeps its actionable reason visible after recovery settles", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Refused finalize ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await applyCurrentStandardAndAccept(page);
  await continueToReview(page);
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();

  // Controlled rejection: the server's own recorded refusal, delivered by the harness. This is a
  // transport fixture for the client's recovery behaviour, not transaction evidence.
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.fulfill({
      status: 400,
      contentType: "application/json",
      body: JSON.stringify({
        code: "cannot_finalize",
        error: "A level without verification capability cannot enable verification artifacts.",
        findings: [
          {
            code: "verification_disabled_with_artifacts",
            level: "System",
            field: "enabledArtifactKinds",
            message: "A level without verification capability cannot enable verification artifacts.",
            token: null,
          },
        ],
      }),
    });
  });
  const recovery = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/project-setups/${draftId}`) && response.request().method() === "GET",
  );
  await page.getByRole("button", { name: "Create Project" }).click();
  // Synchronise on the recovery read completing, then on the settled state it produces.
  await recovery;

  const failure = page.locator(".projectSetupError");
  await expect(failure).toBeVisible();
  await expect(failure).toContainText(/cannot enable verification artifacts/i);
  await expect(failure).toContainText(/repair/i);
  // The screen must not claim the Project exists. Its configuration verdict is a separate, still-true fact.
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  expect(page.url()).toContain(draftId);
  await page.screenshot({ path: testInfo.outputPath("refused-finalization-visible.png"), fullPage: true });

  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
  const persisted = await persistedDraft(page, draftId);
  expect(persisted.state).toBe("Draft");
});

test("an unresolved finalization states uncertainty rather than success or definite failure", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Unresolved finalize ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await applyCurrentStandardAndAccept(page);
  await continueToReview(page);
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();

  // A genuine pre-dispatch transport failure. `route.abort` without `route.fetch` means the request never
  // reaches the server, so this proves client behaviour only — never that a transaction rolled back.
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.abort("connectionreset");
  });
  const recovery = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/project-setups/${draftId}`) && response.request().method() === "GET",
  );
  await page.getByRole("button", { name: "Create Project" }).click();
  await recovery;

  const status = page
    .locator('[role="alert"], [role="status"]')
    .filter({ hasText: /cannot yet be confirmed|still being finalized|not yet (been )?confirmed/i });
  await expect(status.first()).toBeVisible();
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  await expect(page.getByText(/start (a )?(new|another) (setup|draft)/i)).toHaveCount(0);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);

  const persisted = await persistedDraft(page, draftId);
  expect(persisted.state).toBe("Draft");
});

test("a failed recovery read preserves the original context without claiming certainty", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Recovery failed ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await applyCurrentStandardAndAccept(page);
  await continueToReview(page);

  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.fulfill({ status: 500, contentType: "application/json", body: JSON.stringify({ code: "server_error" }) });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.abort("connectionreset");
  });
  await page.getByRole("button", { name: "Create Project" }).click();

  const failure = page.locator(".projectSetupError");
  await expect(failure).toBeVisible();
  await expect(failure).toContainText(/cannot yet be confirmed/i);
  await expect(failure).toContainText(/recovery read failed/i);
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
});

test("a setup the server reports as still finalizing is described as in progress", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `In flight ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await applyCurrentStandardAndAccept(page);
  await continueToReview(page);

  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.fulfill({ status: 500, contentType: "application/json", body: JSON.stringify({ code: "server_error" }) });
  });
  const recovery = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/project-setups/${draftId}`) && response.request().method() === "GET",
  );
  await page.getByRole("button", { name: "Create Project" }).click();
  const recovered = await recovery;
  const body = (await recovered.json()) as Record<string, unknown>;
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({ json: { ...body, state: "Finalizing" } });
  });
  // The first recovery already settled; trigger a second attempt so the in-flight state is what the
  // screen describes.
  await page.getByRole("button", { name: "Create Project" }).click();

  await expect(page.locator(".projectSetupError")).toContainText(/still being finalized/i);
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
});

test("an explicit empty software verification profile is not silently replaced on resume", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Explicit empty ${Date.now().toString(36)}`;
  const created = await page.request.post(`${apiBase}/api/project-setups`, { data: { projectName } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const draftId = ((await created.json()) as { draftId: string }).draftId;
  const saved = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      expectedVersion: 1,
      currentStep: "Ladder",
      project: { name: projectName, softwareProduct: `${projectName} software` },
      start: { kind: "Fresh" },
      build: { version: "0.01" },
      selectedCategories: [],
      ladder: {
        steps: [
          { catalogueEntry: "System", position: 1, capabilities: 7, enabledArtifactKinds: ["Procedure"] },
          // Verification is enabled and deliberately selects no artifact yet: incomplete, not a default.
          { catalogueEntry: "HighLevel", position: 2, capabilities: 7, enabledArtifactKinds: [] },
          { catalogueEntry: "LowLevel", position: 3, capabilities: 15, enabledArtifactKinds: ["Case"] },
        ],
        relationships: [
          { parent: "System", child: "HighLevel" },
          { parent: "HighLevel", child: "LowLevel" },
        ],
      },
      reviewRules: {},
      reviewRulesAccepted: true,
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Create New Project", level: 1 })).toBeVisible();
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  const profile = highLevelRowOf(page).getByLabel("Verification profile");
  await expect(profile).toBeVisible();
  // The only faithful rendering of an explicit empty list is a clearly unanswered choice: not Case-only,
  // not Case + Procedure, and not silently the catalogue default.
  await expect(profile).toHaveValue("");
  await expect(levelFinding(page, /High-?Level/i).first()).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("explicit-empty-profile.png"), fullPage: true });

  const persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual([]);
  expect(persisted.validation?.ladderValid).toBe(false);
  expect(
    persisted.validation?.findings.some(
      (finding) => finding.code === "verification_profile_invalid" && finding.level === "HighLevel",
    ),
  ).toBe(true);

  // A supported save must not normalize the explicit empty list into a valid-looking profile, and the
  // resumed draft must still show the unanswered choice.
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const afterSave = await persistedDraft(page, draftId);
  expect(step(afterSave, "HighLevel")?.capabilities, "verification stays enabled").toBe(7);
  expect(step(afterSave, "HighLevel")?.enabledArtifactKinds, "explicit empty survives a save").toEqual([]);
  expect(step(afterSave, "LowLevel")?.enabledArtifactKinds).toEqual(["Case"]);
  await page.goto(`/projects/setup/${draftId}`);
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(highLevelRowOf(page).getByLabel("Verification profile")).toHaveValue("");
});

test("a Case-only software profile survives disabling and re-enabling verification", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Case only ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  const highLevelRow = highLevelRowOf(page);
  await highLevelRow.getByLabel("Verification profile").selectOption("Case");
  await verificationCheckbox(highLevelRow).uncheck();
  // Turning verification off really does clear the artifacts; the off state is not merely a hidden toggle.
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const disabled = await persistedDraft(page, draftId);
  expect(step(disabled, "HighLevel")?.capabilities & 2, "verification is off").toBe(0);
  expect(step(disabled, "HighLevel")?.enabledArtifactKinds).toEqual([]);

  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  const resumedRow = highLevelRowOf(page);
  await verificationCheckbox(resumedRow).check();
  // Switching the capability back on must not silently substitute Case + Procedure for the choice the
  // creator made a moment ago.
  await expect(resumedRow.getByLabel("Verification profile")).toHaveValue("Case");
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const restored = await persistedDraft(page, draftId);
  expect(step(restored, "HighLevel")?.capabilities & 2, "verification is enabled again").toBe(2);
  expect(step(restored, "HighLevel")?.enabledArtifactKinds, "Case-only is preserved").toEqual(["Case"]);

  await page.goto(`/projects/setup/${draftId}`);
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  await expect(highLevelRowOf(page).getByLabel("Verification profile")).toHaveValue("Case");
});

test("an unrecognized verification artifact is not silently presented as a valid profile", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Unknown artifact ${Date.now().toString(36)}`;
  const created = await page.request.post(`${apiBase}/api/project-setups`, { data: { projectName } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const draftId = ((await created.json()) as { draftId: string }).draftId;
  const saved = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      expectedVersion: 1,
      currentStep: "Ladder",
      project: { name: projectName, softwareProduct: `${projectName} software` },
      start: { kind: "Fresh" },
      build: { version: "0.01" },
      selectedCategories: [],
      ladder: {
        steps: [
          { catalogueEntry: "System", position: 1, capabilities: 7, enabledArtifactKinds: ["Procedure"] },
          { catalogueEntry: "HighLevel", position: 2, capabilities: 7, enabledArtifactKinds: ["Case", "Rubbish"] },
          { catalogueEntry: "LowLevel", position: 3, capabilities: 15, enabledArtifactKinds: ["Case"] },
        ],
        relationships: [
          { parent: "System", child: "HighLevel" },
          { parent: "HighLevel", child: "LowLevel" },
        ],
      },
      reviewRules: {},
      reviewRulesAccepted: true,
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Create New Project", level: 1 })).toBeVisible();
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  const finding = page
    .locator('[role="alert"], [role="status"]')
    .filter({ hasText: /High-?Level/i })
    .filter({ hasText: /unrecognized|unsupported|unknown|invalid/i });
  await expect(finding.first()).toBeVisible();

  const persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual(["Case", "Rubbish"]);
  expect(
    persisted.validation?.findings.some((item) => item.token === "Rubbish"),
    "the verdict names the token rather than reducing the profile",
  ).toBe(true);

  // The unrecognized token survives a supported save and a resume instead of being filtered away.
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const afterSave = await persistedDraft(page, draftId);
  expect(step(afterSave, "HighLevel")?.enabledArtifactKinds).toEqual(["Case", "Rubbish"]);
  await page.goto(`/projects/setup/${draftId}`);
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(
    page
      .locator('[role="alert"], [role="status"]')
      .filter({ hasText: /High-?Level/i })
      .filter({ hasText: /unrecognized|unsupported|unknown|invalid/i })
      .first(),
  ).toBeVisible();
});

test("the final review states each level's verification profile, including a disabled one", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Profile summary ${Date.now().toString(36)}`;
  await startFreshDraftAtLadder(page, projectName, "0.01");

  await verificationCheckbox(systemRowOf(page)).uncheck();
  await page.getByRole("button", { name: "Continue" }).click();
  await applyCurrentStandardAndAccept(page);
  await continueToReview(page);

  const review = page.locator(".setupReviewList");
  await expect(review).toContainText(/System[^]{0,240}?[Vv]erification[^]{0,120}?(disabled|off|none)/);
  await expect(review).toContainText(/High[- ]?Level[^]{0,240}?[Vv]erification/);
  await expect(review).toContainText(/Low[- ]?Level[^]{0,240}?[Vv]erification/);
  await page.screenshot({ path: testInfo.outputPath("review-summary.png"), fullPage: true });
});

test("answers typed while a save is in flight are kept and do not restore readiness", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `In flight edit ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await applyCurrentStandardAndAccept(page);
  await continueToReview(page);
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();

  // Hold the save response so an edit lands while the request is still in flight. The second edit is made
  // on the repository step, whose controls stay mounted until the held response is released.
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let held = false;
  let holdNext = false;
  await page.route(/\/api\/project-setups\/[0-9a-f-]+$/i, async (route) => {
    if (route.request().method() !== "PUT") {
      await route.continue();
      return;
    }
    if (!holdNext) {
      await route.continue();
      return;
    }
    held = true;
    await gate;
    await route.continue();
  });
  await page.getByRole("button", { name: /Repository/ }).click();
  await expect(page.getByRole("heading", { name: "Repository setup", level: 2 })).toBeVisible();
  await page.getByLabel("Connect now").check();
  const endpoint = "https://gitlab.example/group/project";
  await page.getByLabel("GitLab project endpoint (HTTPS)").fill(endpoint);
  holdNext = true;
  await page.getByRole("button", { name: "Continue" }).click();
  await expect.poll(() => held).toBe(true);
  // The newer choice is made while the save that committed the older one is still outstanding.
  await page.getByLabel("Configure later").check();
  release();

  // The newer edit survives the response that was already in flight, so it is still outstanding work and
  // the answers the save did commit are not silently declared ready.
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.locator(".projectSetupSavedState")).toContainText("Unsaved changes");
  await expect(page.getByRole("button", { name: "Create Project" })).toBeDisabled();
  const persisted = await persistedDraft(page, draftId);
  expect(persisted.state).toBe("Draft");
  expect(
    (persisted as unknown as { repository?: { mode?: string; endpoint?: string | null } }).repository,
    "the save committed the older choice; the newer one is still on screen and unsaved",
  ).toMatchObject({ mode: "ConnectNow", endpoint });
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+$/i);
});

test("a source-driven version change does not inherit the previous verdict, and rechecking restores it", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Source advance ${Date.now().toString(36)}`;
  const draftId = "00000000-0000-4000-8000-000000001045";

  // A saved source setup that the server reports as ready at version 5, then a source read that advances
  // the draft to version 6. The verdict describes version 5, so it cannot authorise version 6.
  const source = {
    id: "00000000-0000-4000-8000-000000001046",
    kind: "ExternalBaseline",
    displayName: "Staged navigation requirements",
    fileName: "navigation.xlsx",
    format: "XLSX",
    sha256: "source-sha-1045",
    metadata: { sourceSystem: "Planning Tool" },
    categories: [{ key: "Requirements", count: 2, requires: [], supported: true }],
    selectedCategories: ["Requirements"],
    modules: [],
    relations: [],
    findings: [],
    findingResolutions: {},
    reconciliation: {
      ready: true,
      observedObjects: 2,
      includedObjects: 2,
      excludedObjects: 0,
      observedRelations: 0,
      includedRelations: 0,
      excludedRelations: 0,
      errors: [],
      manifestHash: "manifest-1045",
    },
    assertion: { text: "Source source-sha-1045 was reconciled for this exact project start.", hash: "assertion-1045" },
  };
  const ladder = {
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
  // Generated through the real server for this exact ladder, then re-pointed at the mocked source identity.
  const shaped = await serverShapedDraft(page, projectName, ladder);
  const draftAt = (version: number) => ({
    ...shaped,
    draftId,
    state: "Draft",
    currentStep: "Review",
    version,
    start: { kind: "ExternalBaseline", sourceBaselineId: null, sourceImportId: source.id },
    selectedCategories: ["Requirements"],
    validation: { ...shaped.validation, draftId, version },
  });
  // A phase flag rather than a call counter: a development build may mount the walkthrough more than once,
  // and every read during the first phase must describe the same version the verdict belongs to.
  let phase = 1;
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({ json: draftAt(phase === 1 ? 5 : 6) });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}/source$`), async (route) => {
    // The source service advanced the draft without returning the setup view.
    await route.fulfill({ json: { draftVersion: 6, source } });
  });

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  // The creator's own acceptance and password are present before the version claim is judged, so the only
  // thing standing between this draft and readiness is the verdict that describes the older version.
  await page.getByLabel(/I accept this exact source assertion/i).check();
  await page.getByLabel("Password to finalize source acceptance").fill("AeroLink!2026");
  // Version 6 is not validated, so the screen may not claim readiness, but it must offer the safe route
  // back to a verdict instead of losing the staged source or the answers.
  await expect(page.getByRole("button", { name: "Create Project" })).toBeDisabled();
  await expect(page.locator(".setupReadyNotice")).toHaveCount(0);
  const recheck = page.getByRole("button", { name: /Recheck the saved configuration/i });
  await expect(recheck).toBeVisible();
  await expect(page.getByLabel(/I accept this exact source assertion/i)).toBeVisible();

  // The saved configuration is now read at its current version; the staged source is not re-uploaded.
  phase = 2;
  await recheck.click();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
  // Rechecking refreshed the verdict without discarding the accepted source state or the answers.
  await expect(page.getByLabel(/I accept this exact source assertion/i)).toBeChecked();
  await expect(page.getByLabel("Password to finalize source acceptance")).toHaveValue("AeroLink!2026");
  await page.unroute(new RegExp(`/api/project-setups/${draftId}$`));
  await page.unroute(new RegExp(`/api/project-setups/${draftId}/source$`));
});

test("a 403 whose recovery read also fails keeps the authorization problem and the failed read distinct", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const draftId = await readyFreshDraftAtReview(page, `Forbidden ${Date.now().toString(36)}`);
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    // Results.Forbid() carries no body, which is how a real permission refusal reaches the browser.
    await route.fulfill({ status: 403 });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.abort("connectionreset");
  });
  await page.getByRole("button", { name: "Create Project" }).click();

  const failure = page.locator(".projectSetupError");
  await expect(failure).toContainText(/does not have authority for this action/i);
  await expect(failure).toContainText(/follow-up recovery read failed/i);
  await expect(failure).not.toContainText(/still saved as a draft at version/i);
  await expect(failure).not.toContainText(/No success was recorded/i);
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
  await page.unroute(new RegExp(`/api/project-setups/${draftId}$`));
});

test("a 409 whose recovery read fails does not claim the current version was reloaded", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const draftId = await readyFreshDraftAtReview(page, `Conflict read failed ${Date.now().toString(36)}`);
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.fulfill({
      status: 409,
      contentType: "application/json",
      body: JSON.stringify({
        code: "finalization_conflict",
        error: "The setup changed before finalization could be claimed. Refresh and retry.",
      }),
    });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.abort("connectionreset");
  });
  await page.getByRole("button", { name: "Create Project" }).click();

  const failure = page.locator(".projectSetupError");
  await expect(failure).toContainText(/setup changed before finalization could be claimed/i);
  await expect(failure).toContainText(/follow-up recovery read failed/i);
  await expect(failure).not.toContainText(/reloaded at its current version/i);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
  await page.unroute(new RegExp(`/api/project-setups/${draftId}$`));
});

test("a 409 whose recovered draft is still finalizing is described as in progress", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const draftId = await readyFreshDraftAtReview(page, `Conflict in flight ${Date.now().toString(36)}`);
  const current = await persistedDraft(page, draftId);

  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.fulfill({
      status: 409,
      contentType: "application/json",
      body: JSON.stringify({
        code: "finalization_conflict",
        error: "This setup is already being finalized. Retry after it completes.",
      }),
    });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({ json: { ...current, state: "Finalizing" } });
  });
  await page.getByRole("button", { name: "Create Project" }).click();

  const failure = page.locator(".projectSetupError");
  await expect(failure).toContainText(/still being finalized/i);
  await expect(failure).not.toContainText(/reloaded at its current version/i);
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
  await page.unroute(new RegExp(`/api/project-setups/${draftId}$`));
});

test("a 500 without an explanatory message states uncertainty rather than a rollback", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const draftId = await readyFreshDraftAtReview(page, `Server error ${Date.now().toString(36)}`);
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.fulfill({ status: 500 });
  });
  const recovery = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/project-setups/${draftId}`) && response.request().method() === "GET",
  );
  await page.getByRole("button", { name: "Create Project" }).click();
  await recovery;

  const failure = page.locator(".projectSetupError");
  await expect(failure).toContainText(/cannot yet be confirmed/i);
  await expect(failure).toContainText(/HTTP 500/i);
  await expect(failure).toContainText(/without an explanatory message/i);
  await expect(failure).toContainText(/unfinished draft at version/i);
  await expect(failure).not.toContainText(/No success was recorded/i);
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("finalization-uncertain.png"), fullPage: true });
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
});

test("a recheck that adopts a changed saved ladder drops the earlier source acceptance", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Source invalidation ${Date.now().toString(36)}`;
  const draftId = "00000000-0000-4000-8000-000000001048";
  const fullLadder = {
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
  const changedLadder = {
    ...fullLadder,
    steps: [
      { catalogueEntry: "System", position: 1, capabilities: 5, enabledArtifactKinds: [] },
      ...fullLadder.steps.slice(1),
    ],
  };
  const shaped = await serverShapedDraft(page, `${projectName} before`, fullLadder);
  const changed = await serverShapedDraft(page, `${projectName} after`, changedLadder);
  const draftFor = (source: Record<string, unknown>, version: number) => ({
    ...source,
    draftId,
    state: "Draft",
    currentStep: "Review",
    version,
    start: { kind: "ExternalBaseline", sourceBaselineId: null, sourceImportId: stagedSource.id },
    selectedCategories: ["Requirements"],
    validation: { ...(source.validation as Record<string, unknown>), draftId, version },
  });
  let phase = 1;
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    // Phase 1 is the accepted state at version 4; phase 2 is the changed saved ladder at version 5.
    await route.fulfill({ json: draftFor(phase === 1 ? shaped : changed, phase === 1 ? 4 : 5) });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}/source$`), async (route) => {
    await route.fulfill({ json: { draftVersion: 4, source: stagedSource } });
  });
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.fulfill({
      status: 400,
      contentType: "application/json",
      body: JSON.stringify({
        code: "cannot_finalize",
        error: "A level without verification capability cannot enable verification artifacts.",
        findings: [
          {
            code: "verification_disabled_with_artifacts",
            level: "System",
            field: "enabledArtifactKinds",
            message: "A level without verification capability cannot enable verification artifacts.",
            token: null,
          },
        ],
      }),
    });
  });

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await page.getByLabel(/I accept this exact source assertion/i).check();
  await page.getByLabel("Password to finalize source acceptance").fill("AeroLink!2026");
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();

  // The server now reports a changed saved ladder. The recovery read that follows a refusal adopts those
  // changed facts, so the earlier acceptance and password are no longer authority for them — while the
  // staged source itself stays staged, and the refusal context stays visible.
  phase = 2;
  await page.getByRole("button", { name: "Create Project" }).click();
  await expect(page.locator(".projectSetupError")).toContainText(/cannot enable verification artifacts/i);
  await expect(page.locator(".projectSetupNotice")).toContainText(/source acceptance was established against/i);
  // The earlier acceptance is gone rather than carried over: the assertion must be reconciled and accepted
  // again, while the staged source itself stays staged.
  await expect(page.getByLabel(/I accept this exact source assertion/i)).toHaveCount(0);
  await expect(page.getByText(/Source acceptance is not available yet/i)).toBeVisible();
  await expect(page.locator(".setupReviewList")).toContainText(/External baseline/);
  await expect(page.getByRole("button", { name: "Create Project" })).toBeDisabled();
  await page.unroute(new RegExp(`/api/project-setups/${draftId}$`));
  await page.unroute(new RegExp(`/api/project-setups/${draftId}/source$`));
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
});

/** The staged external source these mocked-transport journeys serve back to the walkthrough. */
const stagedSource = {
  id: "00000000-0000-4000-8000-000000001046",
  kind: "ExternalBaseline",
  displayName: "Staged navigation requirements",
  fileName: "navigation.xlsx",
  format: "XLSX",
  sha256: "source-sha-1045",
  metadata: { sourceSystem: "Planning Tool" },
  categories: [{ key: "Requirements", count: 2, requires: [], supported: true }],
  selectedCategories: ["Requirements"],
  modules: [],
  relations: [],
  findings: [],
  findingResolutions: {},
  reconciliation: {
    ready: true,
    observedObjects: 2,
    includedObjects: 2,
    excludedObjects: 0,
    observedRelations: 0,
    includedRelations: 0,
    excludedRelations: 0,
    errors: [],
    manifestHash: "manifest-1045",
  },
  assertion: {
    text: "Source source-sha-1045 was reconciled for this exact project start.",
    hash: "assertion-1045",
  },
};

/** A fresh draft taken to a ready Review step through the real walkthrough and real server. */
async function readyFreshDraftAtReview(page: Page, projectName: string) {
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await applyCurrentStandardAndAccept(page);
  await continueToReview(page);
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
  return draftId;
}

test("the recorded contradictory draft is repaired without re-enabling verification and completes for real", async ({
  page,
}, testInfo) => {
  test.setTimeout(240_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Owner shape ${Date.now().toString(36)}`;
  const created = await page.request.post(`${apiBase}/api/project-setups`, { data: { projectName } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const draftId = ((await created.json()) as { draftId: string }).draftId;

  // The recorded owner shape: System mask 5 (verification off) still enabling Procedure.
  const recordedLadder = {
    steps: [
      { catalogueEntry: "System", position: 1, capabilities: 5, enabledArtifactKinds: ["Procedure"] },
      { catalogueEntry: "HighLevel", position: 2, capabilities: 7, enabledArtifactKinds: ["Case", "Procedure"] },
      { catalogueEntry: "LowLevel", position: 3, capabilities: 15, enabledArtifactKinds: ["Case", "Procedure"] },
    ],
    relationships: [
      { parent: "System", child: "HighLevel" },
      { parent: "HighLevel", child: "LowLevel" },
    ],
  };
  const answers = {
    project: { name: projectName, softwareProduct: `${projectName} software` },
    start: { kind: "Fresh" },
    build: { version: "0.01" },
    selectedCategories: [],
    repository: { mode: "ConfigureLater" },
    mapping: {},
  };
  // The server derives the standard for this exact (disabled-verification) ladder, then the creator keeps a
  // compatible customisation of it: a different rule name, the same subjects, stages and authorities.
  const seeded = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      ...answers,
      expectedVersion: 1,
      currentStep: "Ladder",
      ladder: recordedLadder,
      reviewRules: {},
      reviewRulesAccepted: true,
    },
  });
  expect(seeded.ok(), await seeded.text()).toBeTruthy();
  const definition = (await seeded.json()).reviewRules.definition as { rules: { name: string }[] };
  definition.rules[0].name = "Programme acceptance review";
  const customised = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      ...answers,
      expectedVersion: 2,
      currentStep: "Ladder",
      ladder: recordedLadder,
      reviewRules: definition,
      reviewRulesAccepted: true,
    },
  });
  expect(customised.ok(), await customised.text()).toBeTruthy();

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  // 1. The precise inconsistency is visible where it is owned, and the contradictory level is not described
  //    as artifact-free.
  const systemRow = systemRowOf(page);
  await expect(systemRow).toContainText(/Verification is disabled, but the saved profile still enables Procedure/i);
  await expect(systemRow).not.toContainText(/no verification artifacts are enabled/i);
  await expect(systemRow.locator(".setupVerificationFacts")).toContainText(/Procedure/);
  await page.screenshot({ path: testInfo.outputPath("owner-shape-before-repair.png"), fullPage: true });

  // 2. The explicit disabled-preserving repair.
  await systemRow.getByRole("button", { name: "Keep verification disabled and remove the enabled artifacts" }).click();
  await expect(systemRow).not.toContainText(/still enables Procedure/i);
  await expect(systemRow.locator(".setupVerificationFacts")).toContainText(/none selected/);
  await expect(verificationCheckbox(systemRow)).not.toBeChecked();
  await page.screenshot({ path: testInfo.outputPath("owner-shape-after-repair.png"), fullPage: true });

  // 3. Unrelated answers and compatible customisations survive; the repair requires renewed acceptance.
  await page.getByRole("button", { name: /Review rules/ }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  await expect(page.getByLabel("Rule name 1")).toHaveValue("Programme acceptance review");
  await expect(page.getByRole("button", { name: "Use rules for this ladder" })).toHaveCount(0);
  const accepted = page.getByLabel(/explicitly accept these concrete review and approval rules/i);
  await expect(accepted).toBeEnabled();
  await expect(accepted).not.toBeChecked();

  const repaired = await persistedDraft(page, draftId);
  expect(step(repaired, "System")?.capabilities, "verification stays disabled").toBe(5);
  expect(step(repaired, "System")?.enabledArtifactKinds).toEqual([]);
  expect(step(repaired, "HighLevel")?.enabledArtifactKinds, "unrelated level untouched").toEqual([
    "Case",
    "Procedure",
  ]);
  expect(repaired.validation?.ladderValid).toBe(true);

  await accepted.check();
  await continueToReview(page);

  // 4. Save and resume keep the repaired, re-accepted state. Reaching the Review step already committed it;
  //    nothing is left outstanding.
  await expect(page.locator(".projectSetupSavedState")).toContainText(/Saved/);
  await expect(page.getByRole("button", { name: "Save review" })).toBeDisabled();
  await page.reload();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
  await expect(page.locator(".setupReviewList")).toContainText(/System[^]{0,240}?Verification disabled/);
  await expect(page.locator(".setupReviewList")).toContainText(/Procedure/);
  await page.screenshot({ path: testInfo.outputPath("owner-shape-final-summary.png"), fullPage: true });

  // 5. Real completion with the actual first build, authorized discovery and explicit build selection.
  await page.getByRole("button", { name: "Create Project" }).click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]+\/builds$/);
  const projectId = new URL(page.url()).pathname.split("/")[2];
  await expect(page.getByRole("heading", { name: "Software Builds" })).toBeVisible();
  const workspaces = await page.request.get(`${apiBase}/api/workspaces`);
  expect(workspaces.ok(), await workspaces.text()).toBeTruthy();
  const listing = (await workspaces.json()) as {
    projects: { project: { id: string; name: string }; releases: { version: string; isReleased: boolean }[] }[];
  }[];
  const discovered = listing
    .flatMap((workspace) => workspace.projects)
    .find((entry) => entry.project.name === projectName);
  expect(discovered, "the created project appears in authorized discovery").toBeTruthy();
  expect(discovered!.releases.some((release) => release.version === "0.01" && !release.isReleased)).toBe(true);

  // 6. The created project's own ladder configuration respects the accepted profile.
  const configuration = await page.request.get(`${apiBase}/api/projects/${projectId}/configuration`);
  expect(configuration.ok(), await configuration.text()).toBeTruthy();
  const ladder = (await configuration.json()) as {
    steps: { catalogueEntry: string; capabilities: string; enabledArtifactKinds?: string[] | null }[];
  };
  const createdSystem = ladder.steps.find((item) => item.catalogueEntry === "System");
  // The read model renders the capability mask as its flag names, so the assertion is on the flags.
  expect(createdSystem?.capabilities).toContain("HasChangeControl");
  expect(createdSystem?.capabilities).not.toContain("HasVerification");
  expect(createdSystem?.enabledArtifactKinds ?? []).toEqual([]);
  expect(ladder.steps.find((item) => item.catalogueEntry === "HighLevel")?.enabledArtifactKinds).toEqual([
    "Case",
    "Procedure",
  ]);
  await page.screenshot({ path: testInfo.outputPath("owner-shape-repaired.png"), fullPage: true });
});

test("System, HLR and LLR verification can be disabled independently and all together", async ({ page }) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Level matrix ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  const lowLevelRow = () => page.locator(".setupLadderRows > li").nth(2);

  // HLR off on its own.
  await verificationCheckbox(highLevelRowOf(page)).uncheck();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  let persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "HighLevel")?.capabilities, "HLR verification off").toBe(5);
  expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual([]);
  expect(step(persisted, "System")?.capabilities, "System untouched").toBe(7);
  expect(step(persisted, "LowLevel")?.capabilities, "LLR untouched").toBe(15);

  // HLR back on restores the compatible Case + Procedure choice rather than a different default.
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await verificationCheckbox(highLevelRowOf(page)).check();
  await expect(highLevelRowOf(page).getByLabel("Verification profile")).toHaveValue("Case+Procedure");

  // LLR off from a Case-only choice.
  await lowLevelRow().getByLabel("Verification profile").selectOption("Case");
  await verificationCheckbox(lowLevelRow()).uncheck();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "LowLevel")?.capabilities, "LLR verification off").toBe(13);
  expect(step(persisted, "LowLevel")?.enabledArtifactKinds).toEqual([]);
  expect(step(persisted, "HighLevel")?.enabledArtifactKinds, "HLR remains enabled").toEqual([
    "Case",
    "Procedure",
  ]);

  // Combined: System off as well.
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await verificationCheckbox(systemRowOf(page)).uncheck();
  await verificationCheckbox(highLevelRowOf(page)).uncheck();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "System")?.capabilities).toBe(5);
  expect(step(persisted, "HighLevel")?.capabilities).toBe(5);
  expect(step(persisted, "LowLevel")?.capabilities).toBe(13);
  expect(step(persisted, "System")?.enabledArtifactKinds).toEqual([]);
  expect(persisted.validation?.ladderValid, "a coherently disabled ladder is valid").toBe(true);

  // The final summary states each level's own disabled status instead of only naming the levels.
  await page.getByRole("button", { name: /Review and finish/ }).click();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  const summary = page.locator(".setupReviewList");
  await expect(summary).toContainText(/System[^]{0,240}?Verification disabled/);
  await expect(summary).toContainText(/High-?Level[^]{0,240}?Verification disabled/);
  await expect(summary).toContainText(/Low-?Level[^]{0,240}?Verification disabled/);

  // Re-enabling System verification visibly selects its sole valid profile instead of inventing a choice.
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await verificationCheckbox(systemRowOf(page)).check();
  await expect(systemRowOf(page).locator(".setupVerificationFacts")).toContainText(/Procedure/);

  // LLR restores the compatible Case-only choice it was disabled from, not the Case + Procedure default,
  // and the restored profile is what the server stores.
  const lowLevelRowAgain = page.locator(".setupLadderRows > li").nth(2);
  await verificationCheckbox(lowLevelRowAgain).check();
  await expect(lowLevelRowAgain.getByLabel("Verification profile")).toHaveValue("Case");
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const restored = await persistedDraft(page, draftId);
  expect(step(restored, "LowLevel")?.capabilities).toBe(15);
  expect(step(restored, "LowLevel")?.enabledArtifactKinds).toEqual(["Case"]);
});

test("save and exit does not leave while newer unsaved answers remain", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Save exit race ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let held = false;
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/save-and-exit$/i, async (route) => {
    held = true;
    await gate;
    await route.continue();
  });
  await page.getByRole("button", { name: "Save and exit" }).click();
  await expect.poll(() => held).toBe(true);

  // A newer answer lands while the save-and-exit request is still in flight.
  await verificationCheckbox(systemRowOf(page)).uncheck();
  release();

  // The walkthrough stays open and says plainly that the newer answer was never saved.
  await expect(page).toHaveURL(new RegExp(`/projects/setup/${draftId}$`));
  await expect(page.locator(".projectSetupError")).toContainText(/kept this setup open/i);
  await expect(page.locator(".projectSetupError")).toContainText(/still unsaved/i);
  await expect(verificationCheckbox(systemRowOf(page))).not.toBeChecked();
  await expect(page.locator(".projectSetupSavedState")).toContainText(/Unsaved changes/i);
  await page.screenshot({ path: testInfo.outputPath("save-and-exit-superseded.png"), fullPage: true });
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/save-and-exit$/i);

  // Saving again commits the newer answer; resuming shows exactly that.
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "System")?.capabilities).toBe(5);
  expect(step(persisted, "System")?.enabledArtifactKinds).toEqual([]);
  await page.goto(`/projects/setup/${draftId}`);
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(verificationCheckbox(systemRowOf(page))).not.toBeChecked();
});

test("a delayed recheck cannot regress a version the draft advanced past", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Recheck race ${Date.now().toString(36)}`;
  const draftId = "00000000-0000-4000-8000-000000001047";
  const ladder = {
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
  const shaped = await serverShapedDraft(page, projectName, ladder);
  // Mocked transport fixture, labelled: the draft view is served by this test while the behaviour under test
  // is the client's own staleness guard.
  const stale = {
    ...shaped,
    draftId,
    state: "Draft",
    currentStep: "Review",
    version: 6,
    validation: { ...shaped.validation, draftId, version: 5 },
  };
  const advanced = {
    ...shaped,
    draftId,
    state: "Draft",
    currentStep: "Services",
    version: 7,
    validation: { ...shaped.validation, draftId, version: 7 },
  };

  let holdNextRead = false;
  let releaseHeld: () => void = () => undefined;
  const held = new Promise<void>((resolve) => {
    releaseHeld = resolve;
  });
  let heldReached = false;
  let saveLanded = false;
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    const method = route.request().method();
    if (method === "PUT") {
      saveLanded = true;
      await route.fulfill({ json: advanced });
      return;
    }
    if (method !== "GET") {
      await route.continue();
      return;
    }
    if (holdNextRead) {
      holdNextRead = false;
      heldReached = true;
      await held;
      // The response the held recheck eventually returns still describes the older version.
      await route.fulfill({ json: stale });
      return;
    }
    await route.fulfill({ json: stale });
  });

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  // The saved verdict describes version 5 while the draft is at version 6, so it cannot claim readiness.
  await expect(page.getByRole("button", { name: "Create Project" })).toBeDisabled();

  // Issue the recheck and hold it; a save then advances the draft to version 7.
  holdNextRead = true;
  await page.getByRole("button", { name: /Recheck the saved configuration/i }).click();
  await expect.poll(() => heldReached).toBe(true);
  await page.getByRole("button", { name: /Repository/ }).click();
  await expect.poll(() => saveLanded).toBe(true);
  await expect(page.getByRole("heading", { name: "Repository setup", level: 2 })).toBeVisible();
  releaseHeld();

  // The older response is refused instead of dragging the screen back to version 6 and its old verdict.
  await expect(page.locator(".projectSetupNotice")).toContainText(/older than the one on this screen/i);
  await expect(page.getByRole("heading", { name: "Repository setup", level: 2 })).toBeVisible();
  await page.unroute(new RegExp(`/api/project-setups/${draftId}$`));
});

/**
 * C2R-02 helper assertions. These run the real source module the walkthrough uses, so the compatibility rule
 * is asserted directly as well as through the screens below.
 */
test("the profile helpers judge the raw saved answer without filtering or reordering", () => {
  const step = (catalogueEntry: string, capabilities: number, enabledArtifactKinds?: unknown) => ({
    catalogueEntry,
    capabilities,
    enabledArtifactKinds,
  });

  // Only the exact supported shapes are compatible; nothing is shortened, reordered or de-duplicated.
  expect(compatibleRememberedProfile(step("HighLevel", 7, ["Case", 7]))).toBeUndefined();
  expect(compatibleRememberedProfile(step("HighLevel", 7, ["Procedure", "Case"]))).toBeUndefined();
  expect(compatibleRememberedProfile(step("HighLevel", 7, ["Case", "Case"]))).toBeUndefined();
  expect(compatibleRememberedProfile(step("HighLevel", 7, [7, 8]))).toBeUndefined();
  expect(compatibleRememberedProfile(step("HighLevel", 7, ["Case", "Procedure", "Case"]))).toBeUndefined();
  expect(compatibleRememberedProfile(step("System", 7, ["Case"]))).toBeUndefined();
  expect(compatibleRememberedProfile(step("HighLevel", 7, ["Case"]))).toEqual(["Case"]);
  expect(compatibleRememberedProfile(step("HighLevel", 7, ["Case", "Procedure"]))).toEqual([
    "Case",
    "Procedure",
  ]);
  expect(compatibleRememberedProfile(step("System", 7, ["Procedure"]))).toEqual(["Procedure"]);

  // The control shows an unanswered choice unless the raw answer is exactly one supported profile.
  expect(profileSelection(step("HighLevel", 7, ["Procedure", "Case"]))).toBe("");
  expect(profileSelection(step("HighLevel", 7, ["Case", 7]))).toBe("");
  expect(profileSelection(step("HighLevel", 7, []))).toBe("");
  expect(profileSelection(step("HighLevel", 7, ["Case"]))).toBe("Case");
  expect(profileSelection(step("HighLevel", 7, ["Case", "Procedure"]))).toBe("Case+Procedure");

  // Saved content stays distinguishable from genuinely absent or empty content.
  expect(savedArtifactsLabel(step("HighLevel", 7))).toBe("none recorded");
  expect(savedArtifactsLabel(step("HighLevel", 7, []))).toBe("none selected");
  expect(savedArtifactsLabel(step("HighLevel", 7, ["Case", 7]))).toContain("not artifact kinds");
  expect(savedArtifactsLabel(step("HighLevel", 7, ["Procedure", "Case"]))).toBe("Procedure, Case");
  expect(savedArtifactsLabel(step("HighLevel", 7, "Case"))).toContain("not a list of artifact kinds");

  expect(enabledVerificationProfileInvalid(step("HighLevel", 7, ["Case", 7]))).toBe(true);
  expect(enabledVerificationProfileInvalid(step("HighLevel", 7, ["Procedure", "Case"]))).toBe(true);
  expect(enabledVerificationProfileInvalid(step("System", 7, ["Case"]))).toBe(true);
  expect(enabledVerificationProfileInvalid(step("HighLevel", 7, ["Case"]))).toBe(false);
  expect(enabledVerificationProfileInvalid(step("HighLevel", 7, ["Case", "Procedure"]))).toBe(false);
  expect(enabledVerificationProfileInvalid(step("HighLevel", 7))).toBe(false);
});

/**
 * Saves a draft whose HighLevel profile is exactly the supplied raw value. The accepted standard is derived
 * first for the profile that value effectively means (`effectiveKinds`), so the draft's only problem is the
 * raw shape under test rather than a subject mismatch.
 */
async function seedSoftwareProfileDraft(
  page: Page,
  projectName: string,
  highLevelKinds: unknown,
  effectiveKinds: unknown[] = ["Case"],
) {
  const created = await page.request.post(`${apiBase}/api/project-setups`, { data: { projectName } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const draftId = ((await created.json()) as { draftId: string }).draftId;
  const ladderWith = (kinds: unknown) => ({
    steps: [
      { catalogueEntry: "System", position: 1, capabilities: 7, enabledArtifactKinds: ["Procedure"] },
      { catalogueEntry: "HighLevel", position: 2, capabilities: 7, enabledArtifactKinds: kinds },
      { catalogueEntry: "LowLevel", position: 3, capabilities: 15, enabledArtifactKinds: ["Case"] },
    ],
    relationships: [
      { parent: "System", child: "HighLevel" },
      { parent: "HighLevel", child: "LowLevel" },
    ],
  });
  const first = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      expectedVersion: 1,
      currentStep: "WorkingRules",
      project: { name: projectName, softwareProduct: `${projectName} software` },
      start: { kind: "Fresh" },
      build: { version: "0.01" },
      selectedCategories: [],
      ladder: ladderWith(effectiveKinds),
      reviewRules: {},
      reviewRulesAccepted: true,
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(first.ok(), await first.text()).toBeTruthy();
  const definition = (await first.json()).reviewRules.definition as unknown;
  const saved = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      expectedVersion: 2,
      currentStep: "Ladder",
      project: { name: projectName, softwareProduct: `${projectName} software` },
      start: { kind: "Fresh" },
      build: { version: "0.01" },
      selectedCategories: [],
      ladder: ladderWith(highLevelKinds),
      reviewRules: definition,
      reviewRulesAccepted: true,
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  return draftId;
}

test("an invalid raw profile is never shortened, reordered or remembered as a valid choice", async ({
  page,
}, testInfo) => {
  test.setTimeout(240_000);
  await login(page, "admin", { openProject: false });
  const suffix = Date.now().toString(36);
  const invalidCases: {
    label: string;
    kinds: unknown[];
    effective: unknown[];
    saved: RegExp;
    reason: RegExp;
  }[] = [
    { label: "a non-text entry", kinds: ["Case", 7], effective: ["Case"], saved: /not artifact kinds/i, reason: /not artifact kinds/i },
    { label: "only non-text entries", kinds: [7, 8], effective: [], saved: /not artifact kinds/i, reason: /not artifact kinds/i },
    {
      label: "reversed order",
      kinds: ["Procedure", "Case"],
      effective: ["Procedure", "Case"],
      saved: /Procedure, Case/,
      reason: /not one of this level's supported profiles/i,
    },
    {
      label: "a duplicate kind",
      kinds: ["Case", "Case"],
      effective: ["Case"],
      saved: /Case, Case/,
      reason: /not one of this level's supported profiles/i,
    },
  ];

  for (const invalid of invalidCases) {
    const projectName = `Invalid profile ${invalid.label} ${suffix}`;
    const draftId = await seedSoftwareProfileDraft(page, projectName, invalid.kinds, invalid.effective);
    await page.goto(`/projects/setup/${draftId}`);
    await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

    const row = highLevelRowOf(page);
    // The saved list is described as what it is, never as a shorter valid profile, and the control that
    // records a decision stays unanswered.
    await expect(row.locator(".setupVerificationFacts")).toContainText(invalid.saved);
    // The qualifier lives in the wide diagnostics region, not in the narrow readings column.
    await expect(row).toContainText(/not one of this level's supported choices/i);
    await expect(row.getByLabel("Verification profile")).toHaveValue("");
    await expect(row.locator('[role="alert"]').first()).toContainText(invalid.reason);
    await page.screenshot({
      path: testInfo.outputPath(`invalid-profile-${invalid.label.replaceAll(" ", "-")}.png`),
      fullPage: true,
    });

    // A supported save preserves the raw answer exactly.
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
    let persisted = await persistedDraft(page, draftId);
    expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual(invalid.kinds);

    // Turning verification off and on again does not restore a filtered approximation of it.
    await page.getByRole("button", { name: /Requirement ladder/ }).click();
    const reopened = highLevelRowOf(page);
    await verificationCheckbox(reopened).uncheck();
    await expect(reopened.locator(".setupVerificationFacts")).toContainText(/none selected/);
    await verificationCheckbox(reopened).check();
    await expect(reopened.getByLabel("Verification profile")).toHaveValue("");
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
    persisted = await persistedDraft(page, draftId);
    expect(step(persisted, "HighLevel")?.capabilities).toBe(7);
    expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual([]);
  }

  // Valid profiles keep being remembered and restored exactly.
  const validCases: { kinds: string[]; selection: string }[] = [
    { kinds: ["Case"], selection: "Case" },
    { kinds: ["Case", "Procedure"], selection: "Case+Procedure" },
  ];
  for (const valid of validCases) {
    const draftId = await seedSoftwareProfileDraft(page, `Valid profile ${valid.selection} ${suffix}`, valid.kinds);
    await page.goto(`/projects/setup/${draftId}`);
    await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
    const row = highLevelRowOf(page);
    await expect(row.getByLabel("Verification profile")).toHaveValue(valid.selection);

    await verificationCheckbox(row).uncheck();
    await expect(row.locator(".setupVerificationFacts")).toContainText(/none selected/);
    await verificationCheckbox(row).check();
    await expect(row.getByLabel("Verification profile")).toHaveValue(valid.selection);

    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
    const persisted = await persistedDraft(page, draftId);
    expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual(valid.kinds);
  }
});

/** A source draft taken to the Review step through mocked transport, with everything else coherent. */
async function sourceDraftAtReview(page: Page, projectName: string) {
  const draftId = "00000000-0000-4000-8000-000000001050";
  const ladder = {
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
  const shaped = await serverShapedDraft(page, projectName, ladder);
  const current = {
    ...shaped,
    draftId,
    state: "Draft",
    currentStep: "Review",
    version: 6,
    project: { name: projectName, softwareProduct: `${projectName} software` },
    start: { kind: "ExternalBaseline", sourceBaselineId: null, sourceImportId: stagedSource.id },
    selectedCategories: ["Requirements"],
    validation: { ...shaped.validation, draftId, version: 6 },
  };
  return { draftId, current };
}

test("a rejected stale recovery leaves the current answers and source acceptance unchanged", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Stale recovery ${Date.now().toString(36)}`;
  const { draftId, current } = await sourceDraftAtReview(page, projectName);
  let recovery: "current" | "stale" = "current";
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({
      json:
        recovery === "current"
          ? current
          : {
              ...current,
              version: 5,
              project: { name: "Stale answers", softwareProduct: "Stale product" },
              start: {
                kind: "ExternalBaseline",
                sourceBaselineId: null,
                sourceImportId: "00000000-0000-4000-8000-000000001099",
              },
              validation: { ...current.validation, version: 5 },
            },
    });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}/source$`), async (route) => {
    await route.fulfill({ json: { draftVersion: 6, source: stagedSource } });
  });
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    recovery = "stale";
    await route.fulfill({ status: 500 });
  });

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await page.getByLabel(/I accept this exact source assertion/i).check();
  await page.getByLabel("Password to finalize source acceptance").fill("AeroLink!2026");
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();

  await page.getByRole("button", { name: "Create Project" }).click();

  // The older read is refused as a whole: no answers, no step, no source acceptance, no claims.
  await expect(page.locator(".projectSetupError")).toContainText(/not this draft's current state/i);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.locator(".setupReviewList")).toContainText(projectName);
  await expect(page.locator(".setupReviewList")).not.toContainText("Stale answers");
  await expect(page.getByLabel(/I accept this exact source assertion/i)).toBeChecked();
  await expect(page.getByLabel("Password to finalize source acceptance")).toHaveValue("AeroLink!2026");
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  await page.unroute(new RegExp(`/api/project-setups/${draftId}$`));
  await page.unroute(new RegExp(`/api/project-setups/${draftId}/source$`));
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
});

test("a completed result for another draft cannot announce success", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Foreign completion ${Date.now().toString(36)}`;
  const { draftId, current } = await sourceDraftAtReview(page, projectName);
  let recovery: "current" | "foreign" = "current";
  await page.route(new RegExp(`/api/project-setups/${draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    await route.fulfill({
      json:
        recovery === "current"
          ? current
          : {
              ...current,
              draftId: "00000000-0000-4000-8000-000000001051",
              state: "Completed",
              version: 7,
              finalization: {
                programId: "00000000-0000-4000-8000-000000001060",
                projectId: "00000000-0000-4000-8000-000000001061",
                releaseId: "00000000-0000-4000-8000-000000001062",
              },
            },
    });
  });
  await page.route(new RegExp(`/api/project-setups/${draftId}/source$`), async (route) => {
    await route.fulfill({ json: { draftVersion: 6, source: stagedSource } });
  });
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    // The recovery read that follows now answers for a different draft's completion.
    recovery = "foreign";
    await route.fulfill({ status: 500 });
  });

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await page.getByLabel(/I accept this exact source assertion/i).check();
  await page.getByLabel("Password to finalize source acceptance").fill("AeroLink!2026");
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
  await page.getByRole("button", { name: "Create Project" }).click();

  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  // No success notice of any kind: the foreign completion was never adopted.
  await expect(page.locator(".projectSetupNotice")).toHaveCount(0);
  await expect(page.locator(".projectSetupError")).toContainText(/not this draft's current state/i);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await page.unroute(new RegExp(`/api/project-setups/${draftId}$`));
  await page.unroute(new RegExp(`/api/project-setups/${draftId}/source$`));
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
});

test("a late load describing another draft does not replace the draft that was just created", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const createdId = "00000000-0000-4000-8000-000000001052";
  await page.route(/\/api\/project-setups$/, async (route) => {
    if (route.request().method() !== "POST") {
      await route.continue();
      return;
    }
    await route.fulfill({
      status: 201,
      json: { draftId: createdId, state: "Draft", currentStep: "Details", version: 1 },
    });
  });
  await page.route(new RegExp(`/api/project-setups/${createdId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    // The late read belongs to another draft: it must not enter this draft's scope.
    await route.fulfill({
      json: {
        draftId: "00000000-0000-4000-8000-000000001053",
        state: "Draft",
        currentStep: "Review",
        version: 9,
        project: { name: "Foreign draft", softwareProduct: "Foreign product" },
        start: { kind: "Fresh" },
        build: { version: "1.02" },
        selectedCategories: [],
        ladder: { steps: [], relationships: [] },
        reviewRules: { accepted: true, definition: { rules: [] } },
        repository: { mode: "ConfigureLater" },
        mapping: {},
      },
    });
  });

  await page.goto("/projects/new");
  await expect(page.getByRole("heading", { name: "Project details", level: 2 })).toBeVisible();
  await expect(page.getByLabel("Project name")).toHaveValue("");
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toHaveCount(0);
  await expect(page.locator(".projectSetupError")).toContainText(/different saved setup than the one requested/i);
  await page.unroute(/\/api\/project-setups$/);
  await page.unroute(new RegExp(`/api/project-setups/${createdId}$`));
});

/** A deterministic barrier: the test decides when this mocked response completes. */
function deferred() {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

/** A real saved draft the journey can open and resume, with its real server view. */
async function seedLifecycleDraft(page: Page, projectName: string, currentStep: string) {
  const created = await page.request.post(`${apiBase}/api/project-setups`, { data: { projectName } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const draftId = ((await created.json()) as { draftId: string }).draftId;
  const saved = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      expectedVersion: 1,
      currentStep,
      project: { name: projectName, softwareProduct: `${projectName} software` },
      start: { kind: "Fresh" },
      build: { version: "0.01" },
      selectedCategories: [],
      ladder: {
        steps: [
          { catalogueEntry: "System", position: 1, capabilities: 7, enabledArtifactKinds: ["Procedure"] },
          { catalogueEntry: "HighLevel", position: 2, capabilities: 7, enabledArtifactKinds: ["Case", "Procedure"] },
          { catalogueEntry: "LowLevel", position: 3, capabilities: 15, enabledArtifactKinds: ["Case", "Procedure"] },
        ],
        relationships: [
          { parent: "System", child: "HighLevel" },
          { parent: "HighLevel", child: "LowLevel" },
        ],
      },
      reviewRules: {},
      reviewRulesAccepted: true,
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  const read = await page.request.get(`${apiBase}/api/project-setups/${draftId}`);
  expect(read.ok(), await read.text()).toBeTruthy();
  return { draftId, body: (await read.json()) as Record<string, unknown> };
}

/** The saved view with a verdict that describes an older version, so the recheck control is offered. */
function withStaleVerdict(body: Record<string, unknown>) {
  const validation = body.validation as Record<string, unknown>;
  return {
    ...body,
    validation: { ...validation, version: (body.version as number) - 1 },
  };
}

/** An otherwise-acceptable view for the same draft at a newer version. */
function atNewerVersion(body: Record<string, unknown>, offset: number) {
  const validation = body.validation as Record<string, unknown>;
  const version = (body.version as number) + offset;
  return {
    ...body,
    version,
    validation: { ...validation, version },
  };
}

test("a pending recheck for one draft cannot enter the screen of another draft", async ({ page }) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  const suffix = Date.now().toString(36);
  const draftA = await seedLifecycleDraft(page, `Lifecycle A ${suffix}`, "Review");
  const draftB = await seedLifecycleDraft(page, `Lifecycle B ${suffix}`, "Details");
  const aBody = withStaleVerdict(draftA.body);

  const recheckGate = deferred();
  const bGate = deferred();
  // User actions issue exactly one read; mounts may issue more than one in a development build. The recheck
  // barrier is therefore one-shot, while the other draft's mount reads are held until their gate opens.
  let holdRecheck = false;
  let recheckReached = false;
  let bReached = false;
  await page.route(new RegExp(`/api/project-setups/${draftA.draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    if (!holdRecheck) {
      await route.fulfill({ json: aBody });
      return;
    }
    holdRecheck = false;
    recheckReached = true;
    await recheckGate.promise;
    await route.fulfill({
      json: { ...aBody, project: { name: "Stale A answers", softwareProduct: "Stale A product" } },
    });
  });
  await page.route(new RegExp(`/api/project-setups/${draftB.draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    bReached = true;
    await bGate.promise;
    await route.fulfill({ json: draftB.body });
  });

  await page.goto(`/projects/setup/${draftA.draftId}`);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  holdRecheck = true;
  await page.getByRole("button", { name: /Recheck the saved configuration/i }).click();
  await expect.poll(() => recheckReached).toBe(true);

  // Leave A through the application's own navigation and resume B in the same screen instance. B's own read
  // is still pending when A's recheck completes.
  await page.getByRole("button", { name: "Projects" }).click();
  await expect(page.getByRole("heading", { name: "Projects", level: 1 })).toBeVisible();
  const bCard = page.locator(`[data-setup-draft-id="${draftB.draftId}"]`);
  await expect(bCard).toHaveCount(1);
  await bCard.getByRole("button").click();
  await expect.poll(() => bReached).toBe(true);
  await expect(page.getByText(/Opening the saved project setup/i)).toBeVisible();

  const aRecheckLanded = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/project-setups/${draftA.draftId}`) &&
      response.request().method() === "GET",
  );
  recheckGate.release();
  await aRecheckLanded;

  // Nothing from A may enter B's screen: no answers, no notice, no findings, and B is still opening.
  await expect(page.getByText(/Opening the saved project setup/i)).toBeVisible();
  await expect(page.getByText("Stale A answers")).toHaveCount(0);
  await expect(page.locator(".projectSetupNotice")).toHaveCount(0);
  await expect(page.locator(".projectSetupError")).toHaveCount(0);

  bGate.release();
  await expect(page.getByRole("heading", { name: "Project details", level: 2 })).toBeVisible();
  await expect(page.getByLabel("Project name")).toHaveValue(`Lifecycle B ${suffix}`);
  await expect(page.locator(".projectSetupNotice")).toHaveCount(0);
  await page.unroute(new RegExp(`/api/project-setups/${draftA.draftId}$`));
  await page.unroute(new RegExp(`/api/project-setups/${draftB.draftId}$`));
});

test("an older visit's response cannot become current when the same draft is opened again", async ({ page }) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  const suffix = Date.now().toString(36);
  const projectNameA = `Lifecycle A ${suffix}`;
  const draftA = await seedLifecycleDraft(page, projectNameA, "Review");
  const draftB = await seedLifecycleDraft(page, `Lifecycle B ${suffix}`, "Details");
  const aBody = withStaleVerdict(draftA.body);
  // The old request is otherwise perfectly acceptable: same draft, newer version — only its visit is over.
  const staleA = atNewerVersion(draftA.body, 5);
  staleA.project = { name: "Stale A answers", softwareProduct: "Stale A product" };

  const recheckGate = deferred();
  const reopenGate = deferred();
  const bGate = deferred();
  let holdRecheck = false;
  let holdReopen = false;
  let recheckReached = false;
  let reopenReached = false;
  let bReached = false;
  await page.route(new RegExp(`/api/project-setups/${draftA.draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    if (holdRecheck) {
      holdRecheck = false;
      recheckReached = true;
      await recheckGate.promise;
      await route.fulfill({ json: staleA });
      return;
    }
    if (holdReopen) {
      reopenReached = true;
      await reopenGate.promise;
      await route.fulfill({ json: aBody });
      return;
    }
    await route.fulfill({ json: aBody });
  });
  await page.route(new RegExp(`/api/project-setups/${draftB.draftId}$`), async (route) => {
    if (route.request().method() !== "GET") {
      await route.continue();
      return;
    }
    bReached = true;
    await bGate.promise;
    await route.fulfill({ json: draftB.body });
  });

  await page.goto(`/projects/setup/${draftA.draftId}`);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  holdRecheck = true;
  await page.getByRole("button", { name: /Recheck the saved configuration/i }).click();
  await expect.poll(() => recheckReached).toBe(true);

  // A -> B, so the old request's visit is over.
  await page.getByRole("button", { name: "Projects" }).click();
  const bCard = page.locator(`[data-setup-draft-id="${draftB.draftId}"]`);
  await expect(bCard).toHaveCount(1);
  await bCard.getByRole("button").click();
  await expect.poll(() => bReached).toBe(true);
  bGate.release();
  await expect(page.getByRole("heading", { name: "Project details", level: 2 })).toBeVisible();

  // ... and back to A, whose own read is still pending.
  await page.getByRole("button", { name: "Projects" }).click();
  const aCard = page.locator(`[data-setup-draft-id="${draftA.draftId}"]`);
  await expect(aCard).toHaveCount(1);
  holdReopen = true;
  await aCard.getByRole("button").click();
  await expect.poll(() => reopenReached).toBe(true);
  await expect(page.getByText(/Opening the saved project setup/i)).toBeVisible();

  // The older visit's response is newer than the saved draft and names the same draft, but it is still not this
  // visit: it must not paint the screen or publish a recheck notice.
  const oldResponseLanded = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/project-setups/${draftA.draftId}`) &&
      response.request().method() === "GET",
  );
  recheckGate.release();
  await oldResponseLanded;
  await expect(page.getByText(/Opening the saved project setup/i)).toBeVisible();
  await expect(page.getByText("Stale A answers")).toHaveCount(0);
  await expect(page.locator(".projectSetupNotice")).toHaveCount(0);

  // The current visit's own read still describes A truthfully.
  reopenGate.release();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.locator(".setupReviewList")).toContainText(projectNameA);
  await expect(page.getByText("Stale A answers")).toHaveCount(0);
  await expect(page.locator(".projectSetupNotice")).toHaveCount(0);
  await page.unroute(new RegExp(`/api/project-setups/${draftA.draftId}$`));
  await page.unroute(new RegExp(`/api/project-setups/${draftB.draftId}$`));
});

test("a finalization response that arrives after leaving the walkthrough cannot take over navigation", async ({
  page,
}) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Left finalize ${Date.now().toString(36)}`;
  const draftId = await readyFreshDraftAtReview(page, projectName);

  // Hold the REAL server response: the request reaches the server (and commits), and only its delivery is
  // delayed — the distinction the request-lifecycle correction turns on.
  const holdResponse = deferred();
  let responseHeld = false;
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    const response = await route.fetch();
    responseHeld = true;
    await holdResponse.promise;
    await route.fulfill({ response });
  });
  await page.getByRole("button", { name: "Create Project" }).click();
  await expect.poll(() => responseHeld).toBe(true);

  // Leave through the application's own navigation while the response is in flight.
  await page.getByRole("button", { name: "Projects" }).click();
  await expect(page.getByRole("heading", { name: "Projects", level: 1 })).toBeVisible();

  // The abandoned instance's continuation would go through the parent callback, which both pushes the build
  // selector route and reloads workspaces. Observe for that bounded window rather than assuming it is idle.
  let abandonedNavigation = false;
  const observe = (request: { method: () => string; url: () => string }) => {
    const path = new URL(request.url()).pathname;
    if (request.method() === "GET" && (path.endsWith("/builds") || path === "/api/workspaces"))
      abandonedNavigation = true;
  };
  page.on("request", observe);
  const finalizeLanded = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname.endsWith("/finalize") && response.request().method() === "POST",
  );
  holdResponse.release();
  await finalizeLanded;
  await page.waitForTimeout(750);
  page.off("request", observe);
  expect(abandonedNavigation, "an abandoned finalization must not navigate or reload through the parent").toBe(false);
  await expect(page.getByRole("heading", { name: "Projects", level: 1 })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toHaveCount(0);
  await expect(page.locator(".projectSetupError")).toHaveCount(0);
  await expect(page.locator(".projectSetupNotice")).toHaveCount(0);

  // The committed result is still recoverable by opening the draft again, with its real identities and no
  // duplicate project.
  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toBeVisible();
  await expect(page.locator(".setupReviewList")).toContainText("SW-00.01");
  await page.getByRole("button", { name: "Open build lineage" }).click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]+\/builds$/);

  const workspaces = await page.request.get(`${apiBase}/api/workspaces`);
  expect(workspaces.ok(), await workspaces.text()).toBeTruthy();
  const listing = (await workspaces.json()) as {
    projects: { project: { name: string } }[];
  }[];
  const matches = listing.flatMap((workspace) => workspace.projects)
    .filter((entry) => entry.project.name === projectName);
  expect(matches, "exactly one project for the completed setup").toHaveLength(1);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
});

/**
 * C2R-01-F1: a save response that does not describe the requested draft is not a save. It must not publish a
 * success notice, must not advance the walkthrough, and must not hand its version to a later source operation.
 * The transport is mocked (the real API does not spontaneously return foreign ids); the distinction under test
 * is the client's own response-rejection boundary.
 */
test("a foreign save response on Continue does not report success or advance", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Foreign save ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  // A real edit that is not yet saved, exactly as the reviewer's reproduction describes.
  await highLevelRowOf(page).getByLabel("Verification profile").selectOption("Case");

  await page.route(/\/api\/project-setups\/[0-9a-f-]+$/i, async (route) => {
    if (route.request().method() !== "PUT") {
      await route.continue();
      return;
    }
    // The body describes another draft and is not forwarded: nothing was committed for this one.
    const body = (route.request().postDataJSON() ?? {}) as Record<string, unknown>;
    await route.fulfill({
      status: 200,
      json: {
        draftId: "00000000-0000-4000-8000-000000001070",
        state: "Draft",
        currentStep: "WorkingRules",
        version: 42,
        project: { name: "Foreign draft", softwareProduct: "Foreign product" },
        start: { kind: "Fresh" },
        build: { version: "1.02" },
        selectedCategories: [],
        ladder: body.ladder ?? {},
        reviewRules: { accepted: true, definition: { rules: [] } },
        repository: { mode: "ConfigureLater" },
        mapping: {},
      },
    });
  });

  await page.getByRole("button", { name: "Continue" }).click();

  const failure = page.locator(".projectSetupError");
  await expect(failure).toContainText(/did not describe this setup/i);
  await expect(page.locator(".projectSetupNotice")).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  // The unsaved edit is still exactly what the creator chose.
  await expect(highLevelRowOf(page).getByLabel("Verification profile")).toHaveValue("Case");
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+$/i);

  // With the real endpoint restored, the same action commits and advances.
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual(["Case"]);
});

test("a foreign save response on Save and exit keeps the walkthrough open without a saved notice", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Foreign exit ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/save-and-exit$/i, async (route) => {
    await route.fulfill({
      status: 200,
      json: {
        saved: true,
        draft: {
          draftId: "00000000-0000-4000-8000-000000001071",
          state: "Draft",
          currentStep: "Review",
          version: 42,
          project: { name: "Foreign draft", softwareProduct: "Foreign product" },
          start: { kind: "Fresh" },
          build: { version: "1.02" },
          selectedCategories: [],
          ladder: { steps: [], relationships: [] },
          reviewRules: { accepted: true, definition: { rules: [] } },
          repository: { mode: "ConfigureLater" },
          mapping: {},
        },
      },
    });
  });
  await page.getByRole("button", { name: "Save and exit" }).click();

  await expect(page).toHaveURL(new RegExp(`/projects/setup/${draftId}$`));
  await expect(page.locator(".projectSetupError")).toContainText(/did not describe this setup/i);
  await expect(page.locator(".projectSetupNotice")).toHaveCount(0);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/save-and-exit$/i);
});

/**
 * PRE-1045-02: the facts block must separate the last saved answer from the creator's current selection, and
 * a verdict for the saved configuration must not read as the meaning of an unsaved edit.
 */
/**
 * PRE-1045-02-F1: an exactly-supported explicit profile is not the only valid saved answer. A disabled level
 * that enables nothing, and an enabled level that records no profile (maintained catalogue default), are both
 * valid server configurations and must not be described as unsupported.
 */
test("valid disabled-empty and maintained-default profiles are not described as unsupported", async ({ page }) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  const suffix = Date.now().toString(36);

  const disabledDraft = await seedLadderDraft(page, `Valid disabled ${suffix}`, {
    steps: [
      { catalogueEntry: "System", position: 1, capabilities: 5, enabledArtifactKinds: [] },
      { catalogueEntry: "HighLevel", position: 2, capabilities: 5, enabledArtifactKinds: [] },
      { catalogueEntry: "LowLevel", position: 3, capabilities: 13, enabledArtifactKinds: [] },
    ],
    relationships: [
      { parent: "System", child: "HighLevel" },
      { parent: "HighLevel", child: "LowLevel" },
    ],
  });
  await page.goto(`/projects/setup/${disabledDraft}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  const disabled = await persistedDraft(page, disabledDraft);
  expect(disabled.validation?.ladderValid, "the server accepts a coherently disabled ladder").toBe(true);
  await expect(page.getByText(/not one of this level's supported choices/i)).toHaveCount(0);
  await expect(page.locator(".setupVerificationFacts").first()).toContainText(/none selected/);
  await expect(page.locator(".setupLadderRepairs")).toHaveCount(0);

  // Verification enabled with no recorded profile: the maintained default, not an unsupported answer.
  const defaultDraft = await seedLadderDraft(page, `Valid default ${suffix}`, {
    steps: [
      { catalogueEntry: "System", position: 1, capabilities: 7 },
      { catalogueEntry: "HighLevel", position: 2, capabilities: 7 },
      { catalogueEntry: "LowLevel", position: 3, capabilities: 15 },
    ],
    relationships: [
      { parent: "System", child: "HighLevel" },
      { parent: "HighLevel", child: "LowLevel" },
    ],
  });
  await page.goto(`/projects/setup/${defaultDraft}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  const maintained = await persistedDraft(page, defaultDraft);
  expect(maintained.validation?.ladderValid, "the server accepts the maintained defaults").toBe(true);
  await expect(page.getByText(/not one of this level's supported choices/i)).toHaveCount(0);
  await expect(page.locator(".setupLadderRepairs")).toHaveCount(0);
  const defaultFacts = highLevelRowOf(page).locator(".setupVerificationFacts");
  await expect(defaultFacts).toContainText(/none recorded/);
  await expect(defaultFacts).toContainText(/Case/);

  // The invalid cases keep being identified: an enabled level with an explicitly empty profile.
  const emptyDraft = await seedSoftwareProfileDraft(page, `Valid default contrast ${suffix}`, [], []);
  await page.goto(`/projects/setup/${emptyDraft}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  await expect(page.getByText(/not one of this level's supported choices/i)).toHaveCount(1);
});

test("an unsaved profile change is not presented as the saved interpretation", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Unsaved profile ${Date.now().toString(36)}`;
  const draftId = await seedSoftwareProfileDraft(page, projectName, ["Case", "Procedure"], [
    "Case",
    "Procedure",
  ]);
  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  const row = highLevelRowOf(page);
  const facts = row.locator(".setupVerificationFacts");
  await expect(row.getByLabel("Verification profile")).toHaveValue("Case+Procedure");
  await expect(facts).toContainText(/Last saved artifacts/);
  await expect(facts).toContainText(/Case, Procedure/);
  await expect(facts).not.toContainText(/not saved yet/);
  await expect(facts).not.toContainText(/from the last saved check/);
  await page.screenshot({ path: testInfo.outputPath("profile-saved-case-and-procedure.png"), fullPage: true });

  // The creator selects Case-only without saving anything.
  await row.getByLabel("Verification profile").selectOption("Case");

  await expect(facts).toContainText(/Case, Procedure/);
  await expect(facts).toContainText(/Current selection/);
  await expect(facts).toContainText(/Case — not saved yet/);
  // The effective reading describes the saved check and says so, rather than the unsaved choice.
  await expect(row).toContainText(/Effective describes the last saved check/i);
  await expect(facts).not.toContainText(/^Case$/);
  await expect(verificationCheckbox(row)).toBeChecked();
  await page.screenshot({ path: testInfo.outputPath("profile-unsaved-case-only.png"), fullPage: true });
});

/**
 * PRE-1045-01: the ladder row keeps its facts, diagnostics and actions in deliberate regions at desktop and
 * narrower widths; these captures are the visual evidence for that layout.
 */
/** Fails when two rendered regions intersect. Capturing images alone cannot catch a collapsed column. */
async function expectRegionsDoNotOverlap(first: Locator, second: Locator, label: string) {
  const a = await first.boundingBox();
  const b = await second.boundingBox();
  expect(a && b, `${label}: both regions must be rendered`).toBeTruthy();
  const intersects =
    a!.x < b!.x + b!.width &&
    b!.x < a!.x + a!.width &&
    a!.y < b!.y + b!.height &&
    b!.y < a!.y + a!.height;
  expect(intersects, `${label}: ${JSON.stringify(a)} must not overlap ${JSON.stringify(b)}`).toBe(false);
}

/**
 * Asserts the ladder row regions do not overlap and that the capability group keeps a usable width at the
 * given viewport. This is the regression check for the collapsed-column defect: an auto-placed fieldset in the
 * row-number column still renders, so only geometry catches it.
 */
/** The widths the row layout is qualified at: both sides of every responsive transition plus the reported ones. */
const ladderLayoutWidths = [1280, 1101, 1099, 1024, 981, 980, 900, 621, 619];

async function expectLadderRowLayout(page: Page, width: number) {
  await page.setViewportSize({ width, height: 1400 });
  // A row that overflows the panel expands the document instead of failing an intersection check, which is how
  // the 981-1024px controls escaped the earlier geometry assertions.
  const documentWidth = await page.evaluate(() => document.documentElement.scrollWidth);
  // At and above the widths the reviewer reported, the page itself must fit the viewport. (Below ~960px the
  // banner keeps a larger min-content width, a pre-existing page-level condition outside this row's scope;
  // the row and its regions are still asserted against the panel and the viewport at every width below.)
  if (width >= 981) {
    expect(documentWidth, `the page must not scroll horizontally at ${width}px`).toBeLessThanOrEqual(width + 1);
  }
  const panel = page.locator(".setupStepPanel").first();
  const panelBox = await panel.boundingBox();
  expect(panelBox, `the ladder panel must be rendered at ${width}px`).toBeTruthy();
  const ladder = page.locator(".setupLadderRows").first();
  const ladderBox = await ladder.boundingBox();
  expect(ladderBox, `the ladder list must be rendered at ${width}px`).toBeTruthy();
  expect(
    ladderBox!.x + ladderBox!.width,
    `the ladder must end inside the panel at ${width}px`,
  ).toBeLessThanOrEqual(panelBox!.x + panelBox!.width + 1);
  expect(
    ladderBox!.x + ladderBox!.width,
    `the ladder must end inside the viewport at ${width}px`,
  ).toBeLessThanOrEqual(width + 1);
  const rows = page.locator(".setupLadderRows > li");
  const count = await rows.count();
  expect(count, `the ladder must render rows at ${width}px`).toBeGreaterThan(0);
  for (let index = 0; index < count; index += 1) {
    const row = rows.nth(index);
    const capabilities = row.locator("fieldset").first();
    const capabilitiesBox = await capabilities.boundingBox();
    expect(
      capabilitiesBox?.width ?? 0,
      `row ${index} capabilities keep a readable width at ${width}px`,
    ).toBeGreaterThan(200);
    const actions = row.locator(".setupRowActions");
    const readings = row.locator(".setupVerificationState");
    const diagnostics = row.locator(".setupLadderDiagnostics");
    const regions: [string, Locator][] = [
      ["capabilities", capabilities],
      ["actions", actions],
      ["readings", readings],
      ["diagnostics", diagnostics],
    ];
    for (const [name, region] of regions) {
      if ((await region.count()) === 0) continue;
      const box = await region.boundingBox();
      expect(box, `row ${index} ${name} must be rendered at ${width}px`).toBeTruthy();
      expect(box!.x, `row ${index} ${name} must start inside the panel at ${width}px`).toBeGreaterThanOrEqual(
        panelBox!.x - 1,
      );
      expect(
        box!.x + box!.width,
        `row ${index} ${name} must end inside the panel at ${width}px`,
      ).toBeLessThanOrEqual(panelBox!.x + panelBox!.width + 1);
      expect(
        box!.x + box!.width,
        `row ${index} ${name} must end inside the viewport at ${width}px`,
      ).toBeLessThanOrEqual(width + 1);
    }
    await expectRegionsDoNotOverlap(capabilities, actions, `row ${index} capabilities vs actions at ${width}px`);
    if ((await readings.count()) > 0) {
      await expectRegionsDoNotOverlap(
        capabilities,
        readings,
        `row ${index} capabilities vs readings at ${width}px`,
      );
      await expectRegionsDoNotOverlap(readings, actions, `row ${index} readings vs actions at ${width}px`);
      // Facts values keep enough width that a short profile name cannot break across lines.
      for (const value of await row.locator(".setupVerificationFacts dd").all()) {
        const box = await value.boundingBox();
        expect(box?.width ?? 0, `row ${index} reading values stay legible at ${width}px`).toBeGreaterThan(140);
      }
    }
    if ((await diagnostics.count()) > 0) {
      await expectRegionsDoNotOverlap(
        diagnostics,
        actions,
        `row ${index} diagnostics vs actions at ${width}px`,
      );
      if ((await readings.count()) > 0)
        await expectRegionsDoNotOverlap(
          diagnostics,
          readings,
          `row ${index} diagnostics vs readings at ${width}px`,
        );
    }
  }
}

/** A draft saved with exactly the supplied ladder; the maintained standard is derived from it. */
async function seedLadderDraft(page: Page, projectName: string, ladder: unknown) {
  const created = await page.request.post(`${apiBase}/api/project-setups`, { data: { projectName } });
  expect(created.ok(), await created.text()).toBeTruthy();
  const draftId = ((await created.json()) as { draftId: string }).draftId;
  const saved = await page.request.put(`${apiBase}/api/project-setups/${draftId}`, {
    data: {
      expectedVersion: 1,
      currentStep: "Ladder",
      project: { name: projectName, softwareProduct: `${projectName} software` },
      start: { kind: "Fresh" },
      build: { version: "0.01" },
      selectedCategories: [],
      ladder,
      reviewRules: {},
      reviewRulesAccepted: true,
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  return draftId;
}

/** The recorded contradictory owner shape: System mask 5 with an enabled Procedure. */
function recordedContradictoryLadder() {
  return {
    steps: [
      { catalogueEntry: "System", position: 1, capabilities: 5, enabledArtifactKinds: ["Procedure"] },
      { catalogueEntry: "HighLevel", position: 2, capabilities: 7, enabledArtifactKinds: ["Case", "Procedure"] },
      { catalogueEntry: "LowLevel", position: 3, capabilities: 15, enabledArtifactKinds: ["Case", "Procedure"] },
    ],
    relationships: [
      { parent: "System", child: "HighLevel" },
      { parent: "HighLevel", child: "LowLevel" },
    ],
  };
}

/**
 * PRE-1045-01 (containment): the row must fit the step panel and the window at the widths the reviewer
 * reported, with the actions visible rather than pushed off the right edge. This is deliberately narrow so a
 * failure names containment rather than any other layout property.
 */
test("the ladder stays inside its panel and the reported viewport widths", async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  await startFreshDraftAtLadder(page, `Viewport containment ${Date.now().toString(36)}`, "0.01");
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  for (const width of [1024, 981]) {
    await page.setViewportSize({ width, height: 1200 });
    const documentWidth = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(documentWidth, `the page must fit ${width}px without horizontal scrolling`).toBeLessThanOrEqual(
      width + 1,
    );
    const panel = await page.locator(".setupStepPanel").first().boundingBox();
    expect(panel, `the ladder panel must be rendered at ${width}px`).toBeTruthy();
    const ladder = await page.locator(".setupLadderRows").first().boundingBox();
    expect(ladder, `the ladder list must be rendered at ${width}px`).toBeTruthy();
    expect(
      ladder!.x + ladder!.width,
      `the ladder must stay inside the panel at ${width}px`,
    ).toBeLessThanOrEqual(panel!.x + panel!.width + 1);
    const actions = systemRowOf(page).locator(".setupRowActions");
    await expect(actions, `row actions must be visible at ${width}px`).toBeVisible();
    const actionsBox = await actions.boundingBox();
    expect(
      actionsBox!.x + actionsBox!.width,
      `row actions must stay inside the viewport at ${width}px`,
    ).toBeLessThanOrEqual(width + 1);
    const readings = systemRowOf(page).locator(".setupVerificationState");
    const readingsBox = await readings.boundingBox();
    expect(
      readingsBox!.x + readingsBox!.width,
      `the readings must stay inside the panel at ${width}px`,
    ).toBeLessThanOrEqual(panel!.x + panel!.width + 1);
    // The window's own capture, not a full-page one: this is what the reviewer's evidence showed.
    await page.screenshot({ path: testInfo.outputPath(`ladder-viewport-${width}.png`) });
  }
});

test("the ladder row keeps facts, findings and repairs readable at desktop and narrow widths", async ({
  page,
}, testInfo) => {
  test.setTimeout(300_000);
  await login(page, "admin", { openProject: false });
  const suffix = Date.now().toString(36);

  // Ordinary software rows with a profile selector.
  await startFreshDraftAtLadder(page, `Layout ordinary ${suffix}`, "0.01");
  for (const width of ladderLayoutWidths) {
    await expectLadderRowLayout(page, width);
  }
  await page.setViewportSize({ width: 1280, height: 1100 });
  await page.screenshot({ path: testInfo.outputPath("ladder-ordinary-1280.png"), fullPage: true });
  await page.setViewportSize({ width: 620, height: 1100 });
  await page.screenshot({ path: testInfo.outputPath("ladder-ordinary-620.png"), fullPage: true });
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  // Contradictory owner shape, then the disabled-preserving repair (a repaired-but-unsaved state).
  const contradictoryDraftId = await seedLadderDraft(
    page,
    `Layout contradictory ${suffix}`,
    recordedContradictoryLadder(),
  );
  await page.goto(`/projects/setup/${contradictoryDraftId}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  for (const width of ladderLayoutWidths) {
    await expectLadderRowLayout(page, width);
  }
  await page.setViewportSize({ width: 900, height: 1400 });
  await page.screenshot({ path: testInfo.outputPath("ladder-contradictory-900.png"), fullPage: true });
  await systemRowOf(page)
    .getByRole("button", { name: "Keep verification disabled and remove the enabled artifacts" })
    .click();
  for (const width of ladderLayoutWidths) {
    await expectLadderRowLayout(page, width);
  }
  await page.setViewportSize({ width: 900, height: 1400 });
  await page.screenshot({ path: testInfo.outputPath("ladder-repaired-unsaved-900.png"), fullPage: true });

  // Explicit-empty software profile: the finding must not overlap the selector.
  const emptyDraftId = await seedSoftwareProfileDraft(page, `Layout empty ${suffix}`, [], []);
  await page.goto(`/projects/setup/${emptyDraftId}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  for (const width of ladderLayoutWidths) {
    await expectLadderRowLayout(page, width);
  }
  const emptyRow = highLevelRowOf(page);
  await page.setViewportSize({ width: 1280, height: 1100 });
  await page.screenshot({ path: testInfo.outputPath("ladder-explicit-empty-1280.png"), fullPage: true });
  await page.setViewportSize({ width: 900, height: 1100 });
  await page.screenshot({ path: testInfo.outputPath("ladder-explicit-empty-900.png"), fullPage: true });
  await expect(emptyRow.getByLabel("Verification profile")).toHaveValue("");

  // Invalid raw profile (reversed) with its statement and unanswered control.
  const invalidDraftId = await seedSoftwareProfileDraft(page, `Layout invalid ${suffix}`, ["Procedure", "Case"], [
    "Procedure",
    "Case",
  ]);
  await page.goto(`/projects/setup/${invalidDraftId}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  for (const width of ladderLayoutWidths) {
    await expectLadderRowLayout(page, width);
  }
  await page.setViewportSize({ width: 1280, height: 1100 });
  await page.screenshot({ path: testInfo.outputPath("ladder-invalid-1280.png"), fullPage: true });
  await page.setViewportSize({ width: 900, height: 1100 });
  await page.screenshot({ path: testInfo.outputPath("ladder-invalid-900.png"), fullPage: true });
});
