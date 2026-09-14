import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { apiBase, login } from "./auth";

/**
 * #1045 regression coverage for a project setup whose verification capability is disabled.
 *
 * These journeys express the REQUIRED behaviour, so they are expected to fail on the unfixed base: the
 * walkthrough currently lets the capability mask and the enabled artifact profile disagree, reports the
 * contradictory draft as ready, and then swallows the server's refusal. They are deliberately written
 * against the real API and the real walkthrough rather than a normalizer unit, because the defect spans
 * the toggle, normalization, save, resume, review and finalization recovery.
 */

type PersistedStep = { catalogueEntry: string; capabilities: number; enabledArtifactKinds: string[] };

async function persistedLadder(page: Page, draftId: string) {
  const response = await page.request.get(`${apiBase}/api/project-setups/${draftId}`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const draft = (await response.json()) as { state: string; version: number; ladder: { steps: PersistedStep[] } };
  return draft;
}

/** Starts a Fresh draft, fills the identities and lands on the requirement ladder step. */
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

async function acceptVisibleRulesAndAdvanceToReview(page: Page) {
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const accepted = page.getByLabel(/explicitly accept these concrete review and approval rules/i);
  await expect(accepted).toBeEnabled();
  if (!(await accepted.isChecked())) await accepted.check();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Repository setup", level: 2 })).toBeVisible();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
}

/** A finding/alert that names the affected level and its verification problem. */
function levelFinding(page: Page, level: RegExp) {
  return page
    .locator('[role="alert"], [role="status"]')
    .filter({ hasText: level })
    .filter({ hasText: /verification/i });
}

test("disabling verification at System leaves no enabled verification artifact", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Disabled system ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  const rows = page.locator(".setupLadderRows > li");
  const systemRow = rows.nth(0);
  await systemRow.getByRole("checkbox", { name: "Verification", exact: true }).uncheck();

  // A disabled capability must be unambiguous on the step that owns it, not merely a cleared checkbox
  // beside a still-enabled Procedure artifact.
  await expect(systemRow).toContainText(/verification (is )?(off|disabled)|no verification artifacts|verification: none/i);

  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();

  const persisted = await persistedLadder(page, draftId);
  const system = persisted.ladder.steps.find((step) => step.catalogueEntry === "System");
  expect(system?.capabilities, "System keeps its unrelated capabilities").toBe(5);
  expect(system?.enabledArtifactKinds, "disabled verification enables no artifact").toEqual([]);
  // The untouched levels are preserved exactly.
  expect(persisted.ladder.steps.find((step) => step.catalogueEntry === "HighLevel")?.enabledArtifactKinds)
    .toEqual(["Case", "Procedure"]);
  expect(persisted.ladder.steps.find((step) => step.catalogueEntry === "LowLevel")?.enabledArtifactKinds)
    .toEqual(["Case", "Procedure"]);
});

test("a configuration the server will refuse is never presented as ready", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Not ready ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  const rows = page.locator(".setupLadderRows > li");
  await rows.nth(0).getByRole("checkbox", { name: "Verification", exact: true }).uncheck();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();

  // The server's current standard for this ladder no longer covers the subjects of the earlier profile.
  // Re-ticking acceptance over the stale definition is not coverage.
  await expect(page.getByText(/no longer|newer review standard|Required subjects added:|Subjects no longer required:/i).first())
    .toBeVisible();
  await acceptVisibleRulesAndAdvanceToReview(page);

  await expect(levelFinding(page, /System/).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeDisabled();
  await expect(page.locator(".setupReadyNotice")).toHaveCount(0);

  // The saved draft is untouched by the readiness claim: an unusable configuration is not a rewrite.
  const persisted = await persistedLadder(page, draftId);
  expect(persisted.state).toBe("Draft");
  expect(persisted.ladder.steps.find((step) => step.catalogueEntry === "System")?.capabilities).toBe(5);
});

test("a refused finalization keeps its actionable reason visible after recovery settles", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Refused finalize ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await acceptVisibleRulesAndAdvanceToReview(page);
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();

  // The recorded owner refusal, replayed as the server's own contract response. The browser already
  // succeeded in saving a valid draft; only the finalization is refused.
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.fulfill({
      status: 400,
      contentType: "application/json",
      body: JSON.stringify({
        code: "cannot_finalize",
        error: "A level without verification capability cannot enable verification artifacts.",
      }),
    });
  });
  const recovery = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/project-setups/${draftId}`) && response.request().method() === "GET",
  );
  await page.getByRole("button", { name: "Create Project" }).click();
  await recovery;
  await page.waitForTimeout(1500);

  const failure = page.locator(".projectSetupError");
  await expect(failure).toBeVisible();
  await expect(failure).toContainText(/cannot enable verification artifacts/i);
  await expect(page.locator(".setupReadyNotice")).toHaveCount(0);
  // The creator keeps their answers and the draft identity, so the repair happens in place.
  expect(page.url()).toContain(draftId);
  await page.screenshot({ path: testInfo.outputPath("refused-finalization-visible.png"), fullPage: true });

  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);
  const persisted = await persistedLadder(page, draftId);
  expect(persisted.state).toBe("Draft");
});

test("an unresolved finalization does not assert either success or definite non-creation", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Unresolved finalize ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await acceptVisibleRulesAndAdvanceToReview(page);
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();

  // A transport failure proves nothing about the transaction: the request may have committed.
  await page.route(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i, async (route) => {
    await route.abort("connectionreset");
  });
  const recovery = page.waitForResponse(
    (response) =>
      response.url().endsWith(`/api/project-setups/${draftId}`) && response.request().method() === "GET",
  );
  await page.getByRole("button", { name: "Create Project" }).click();
  await recovery;
  await page.waitForTimeout(1500);

  const status = page.locator('[role="alert"], [role="status"]')
    .filter({ hasText: /cannot yet be confirmed|still being finalized|not yet (been )?confirmed|unresolved|in progress/i });
  await expect(status.first()).toBeVisible();
  await expect(page.locator(".setupReadyNotice")).toHaveCount(0);
  await expect(page.getByText(/start (a )?(new|another) (setup|draft)/i)).toHaveCount(0);
  await page.unroute(/\/api\/project-setups\/[0-9a-f-]+\/finalize$/i);

  const persisted = await persistedLadder(page, draftId);
  expect(persisted.state).toBe("Draft");
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
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Create New Project", level: 1 })).toBeVisible();
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  const highLevelRow = page.locator(".setupLadderRows > li").nth(1);
  const profile = highLevelRow.getByLabel("Verification profile");
  if (await profile.count()) {
    // An empty explicit profile is an unanswered choice, never the catalogue default.
    expect(
      await profile.inputValue(),
      "the resumed profile must reflect the stored empty artifact list",
    ).not.toBe("Case+Procedure");
  }
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();

  // Saving the resumed screen must not rewrite the stored empty array into catalogue defaults.
  const persisted = await persistedLadder(page, draftId);
  expect(persisted.ladder.steps.find((step) => step.catalogueEntry === "HighLevel")?.enabledArtifactKinds).toEqual([]);
});

test("a Case-only software profile survives disabling and re-enabling verification", async ({ page }) => {

  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Case only ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  const highLevelRow = page.locator(".setupLadderRows > li").nth(1);
  await highLevelRow.getByLabel("Verification profile").selectOption("Case");
  await highLevelRow.getByRole("checkbox", { name: "Verification", exact: true }).uncheck();
  await highLevelRow.getByRole("checkbox", { name: "Verification", exact: true }).check();
  // The creator chose Case-only. Nothing may silently upgrade that choice to Case + Procedure.
  if (await highLevelRow.getByLabel("Verification profile").count()) {
    expect(await highLevelRow.getByLabel("Verification profile").inputValue()).not.toBe("Case+Procedure");
  }

  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const persisted = await persistedLadder(page, draftId);
  const highLevel = persisted.ladder.steps.find((step) => step.catalogueEntry === "HighLevel");
  expect(highLevel?.capabilities & 2, "verification is enabled again").toBe(2);
  expect(highLevel?.enabledArtifactKinds, "Case-only is preserved rather than upgraded").toEqual(["Case"]);

  await page.goto(`/projects/setup/${draftId}`);
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  const resumed = page.locator(".setupLadderRows > li").nth(1);
  if (await resumed.getByLabel("Verification profile").count()) {
    expect(await resumed.getByLabel("Verification profile").inputValue()).not.toBe("Case+Procedure");
  }
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
      repository: { mode: "ConfigureLater" },
      mapping: {},
    },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Create New Project", level: 1 })).toBeVisible();
  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  // The server stores the unrecognized token verbatim and refuses finalization with a payload-level
  // message. The walkthrough must name the affected level itself instead of dropping the token and
  // presenting a shorter, apparently valid profile.
  const finding = page.locator('[role="alert"], [role="status"]')
    .filter({ hasText: /High[- ]?Level/i })
    .filter({ hasText: /unrecognized|unsupported|unknown|invalid/i });
  await expect(finding.first()).toBeVisible();

  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const persisted = await persistedLadder(page, draftId);
  expect(persisted.ladder.steps.find((step) => step.catalogueEntry === "HighLevel")?.enabledArtifactKinds)
    .toEqual(["Case", "Rubbish"]);
});

test("the final review states each level's verification profile, including a disabled one", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Profile summary ${Date.now().toString(36)}`;
  await startFreshDraftAtLadder(page, projectName, "0.01");

  const rows = page.locator(".setupLadderRows > li");
  await rows.nth(0).getByRole("checkbox", { name: "Verification", exact: true }).uncheck();
  await page.getByRole("button", { name: "Continue" }).click();
  await acceptVisibleRulesAndAdvanceToReview(page);

  // The summary must name the affected level and its verification state, not only the ladder's levels.
  const review = page.locator(".setupReviewList");
  await expect(review).toContainText(/System[\s\S]{0,240}?[Vv]erification[\s\S]{0,120}?(disabled|off|none)/);
  await expect(review).toContainText(/High[- ]?Level[\s\S]{0,240}?[Vv]erification/);
  await expect(review).toContainText(/Low[- ]?Level[\s\S]{0,240}?[Vv]erification/);
});
