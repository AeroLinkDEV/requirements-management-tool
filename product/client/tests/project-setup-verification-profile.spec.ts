import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { join } from "node:path";
import { apiBase, login } from "./auth";

/**
 * Review evidence this journey commits under product/docs/screenshots so the reviewer can see the
 * screen rather than only read an assertion about it.
 */
const reviewEvidence = (name: string) => join("..", "docs", "screenshots", name);

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
  await page.screenshot({ path: reviewEvidence("project-setup-1045-disabled-system.png"), fullPage: true });
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
  await expect(failure).toContainText(/could not be confirmed/i);
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

test("an explicit empty software verification profile is not silently replaced on resume", async ({ page }) => {
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

  const persisted = await persistedDraft(page, draftId);
  expect(step(persisted, "HighLevel")?.enabledArtifactKinds).toEqual([]);
  expect(persisted.validation?.ladderValid).toBe(false);
  expect(
    persisted.validation?.findings.some(
      (finding) => finding.code === "verification_profile_invalid" && finding.level === "HighLevel",
    ),
  ).toBe(true);
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
  await page.screenshot({ path: reviewEvidence("project-setup-1045-review-summary.png"), fullPage: true });
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
  const draftAt = (version: number) => ({
    draftId,
    state: "Draft",
    currentStep: "Review",
    version,
    project: { name: projectName, softwareProduct: `${projectName} software` },
    start: { kind: "ExternalBaseline", sourceBaselineId: null, sourceImportId: source.id },
    build: { version: "0.01", officialName: "SW-00.01" },
    selectedCategories: ["Requirements"],
    ladder,
    reviewRules: {
      accepted: true,
      acceptanceHash: "hash-1045",
      definition: { rules: [] },
      suggestedDefinition: { rules: [] },
    },
    validation: {
      draftId,
      version,
      evaluatedConfigurationHash: "configuration-1045",
      ladderValid: true,
      steps: ladder.steps.map((item) => ({
        level: item.catalogueEntry,
        capabilities: item.capabilities,
        stored: item.enabledArtifactKinds,
        effective: item.enabledArtifactKinds,
        profileSource: "explicit",
      })),
      findings: [],
      review: {
        applicableSubjects: [],
        acceptedSubjects: [],
        missingSubjects: [],
        unexpectedSubjects: [],
        duplicateSubjects: [],
        accepted: true,
        definitionConcrete: true,
        acceptanceMatchesConfiguration: true,
        covers: true,
      },
      configurationReady: true,
    },
    repository: { mode: "ConfigureLater", status: "Pending", provider: "GitLab", endpoint: "" },
    mapping: {},
    finalization: null,
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
