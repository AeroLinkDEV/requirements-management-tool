import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { apiBase, login } from "./auth";

/**
 * #1045 owner follow-up: System has one verification meaning — its test procedures — so its toggle reads as
 * one plain sentence with a quiet saved line, and never as the software profile table and restoration
 * explanation that High-Level and Low-Level software legitimately need. These journeys drive the real
 * walkthrough against the real API with the run's disposable database.
 */

type PersistedStep = { catalogueEntry: string; capabilities: number; enabledArtifactKinds?: string[] };

async function persistedDraft(page: Page, draftId: string) {
  const response = await page.request.get(`${apiBase}/api/project-setups/${draftId}`);
  expect(response.ok(), await response.text()).toBeTruthy();
  return (await response.json()) as {
    state: string;
    version: number;
    ladder: { steps: PersistedStep[] };
  };
}

function step(persisted: Awaited<ReturnType<typeof persistedDraft>>, level: string) {
  return persisted.ladder.steps.find((item) => item.catalogueEntry === level);
}

const systemRow = (page: Page) => page.locator(".setupLadderRows > li").nth(0);
const highLevelRow = (page: Page) => page.locator(".setupLadderRows > li").nth(1);
const lowLevelRow = (page: Page) => page.locator(".setupLadderRows > li").nth(2);
const systemToggle = (page: Page) =>
  systemRow(page).getByRole("checkbox", { name: "Verification", exact: true });
const systemSentence = (page: Page) => systemRow(page).locator(".setupSystemVerification");

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

/** A saved draft whose ladder is exactly the recorded contradictory owner shape for one level. */
async function seedContradictoryDraft(page: Page, projectName: string) {
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
          { catalogueEntry: "System", position: 1, capabilities: 5, enabledArtifactKinds: ["Procedure"] },
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
  return draftId;
}

test("normal System toggling reads as one plain sentence and leaves the software profiles alone", async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  const projectName = `System toggle ${Date.now().toString(36)}`;
  const draftId = await startFreshDraftAtLadder(page, projectName, "0.01");

  // Saved state: System is on, and the row says so without an unsaved qualifier.
  await expect(systemToggle(page)).toBeChecked();
  await expect(systemSentence(page)).toContainText("System test procedures: On");
  await expect(systemSentence(page)).not.toContainText(/Unsaved change/);
  // System never renders the software profile table or a profile selector.
  await expect(systemRow(page).locator(".setupVerificationFacts")).toHaveCount(0);
  await expect(systemRow(page).getByLabel("Verification profile")).toHaveCount(0);
  await expect(page.locator(".setupSystemVerification small")).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("system-toggle-on-saved.png"), fullPage: true });

  // A normal deselection is a plain state change: one sentence, one quiet saved line, no warning panel,
  // no restoration instruction and no repair control.
  await systemToggle(page).uncheck();
  await expect(systemSentence(page)).toContainText("System test procedures: Off — Unsaved change");
  await expect(systemSentence(page)).toContainText("Last saved: On.");
  await expect(systemRow(page).locator('[role="alert"]')).toHaveCount(0);
  await expect(systemRow(page)).not.toContainText(/Enabling it again does not silently substitute a default/i);
  await expect(systemRow(page)).not.toContainText(/Effective describes the last saved check/i);
  await expect(systemRow(page)).not.toContainText(/No verification profile is saved for this level/i);
  await expect(systemRow(page).locator(".setupLadderRepairs")).toHaveCount(0);
  await expect(systemRow(page).locator(".setupLadderProfileRepair")).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("system-toggle-off-unsaved.png"), fullPage: true });

  // Turning System off changes nothing about the software levels' own choices.
  await expect(highLevelRow(page).getByLabel("Verification profile")).toHaveValue("Case+Procedure");
  await expect(lowLevelRow(page).getByLabel("Verification profile")).toHaveValue("Case+Procedure");
  await expect(highLevelRow(page).getByRole("checkbox", { name: "Verification", exact: true })).toBeChecked();
  await expect(lowLevelRow(page).getByRole("checkbox", { name: "Verification", exact: true })).toBeChecked();

  // Back on inside the same visit is not an unsaved change any more.
  await systemToggle(page).check();
  await expect(systemSentence(page)).toContainText("System test procedures: On");
  await expect(systemSentence(page)).not.toContainText(/Unsaved change/);

  // Save the off state and reload: the saved answer, the sentence and the software profiles all survive.
  await systemToggle(page).uncheck();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const saved = await persistedDraft(page, draftId);
  expect(step(saved, "System")?.capabilities, "System verification off is saved").toBe(5);
  expect(step(saved, "System")?.enabledArtifactKinds).toEqual([]);
  expect(step(saved, "HighLevel")?.enabledArtifactKinds, "HLR profile preserved").toEqual(["Case", "Procedure"]);
  expect(step(saved, "LowLevel")?.enabledArtifactKinds, "LLR profile preserved").toEqual(["Case", "Procedure"]);

  await page.getByRole("button", { name: /Requirement ladder/ }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  await expect(systemSentence(page)).toContainText("System test procedures: Off");
  await expect(systemSentence(page)).not.toContainText(/Unsaved change/);
  await expect(highLevelRow(page).getByLabel("Verification profile")).toHaveValue("Case+Procedure");

  await page.reload();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  await expect(systemToggle(page)).not.toBeChecked();
  await expect(systemSentence(page)).toContainText("System test procedures: Off");
  await expect(systemRow(page).locator('[role="alert"]')).toHaveCount(0);
  await expect(highLevelRow(page).getByLabel("Verification profile")).toHaveValue("Case+Procedure");
  await page.screenshot({ path: testInfo.outputPath("system-toggle-off-saved-reloaded.png"), fullPage: true });
});

test("a genuinely invalid System profile keeps its finding and its supported repair", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `System invalid ${Date.now().toString(36)}`;
  const draftId = await seedContradictoryDraft(page, projectName);

  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();

  // The contradiction is still a real finding with its explicit repairs — only the software-profile
  // explanation is gone from System.
  const row = systemRow(page);
  await expect(row).toContainText(/Verification is disabled, but the saved profile still enables Procedure/i);
  await expect(row.locator(".setupLadderRepairs")).toHaveCount(1);
  await expect(systemSentence(page)).toContainText("System test procedures: Off");
  await expect(row).not.toContainText(/Enabling it again does not silently substitute a default/i);
  await page.screenshot({ path: testInfo.outputPath("system-contradiction-before-repair.png"), fullPage: true });

  await row.getByRole("button", { name: "Keep verification disabled and remove the enabled artifacts" }).click();
  await expect(row).not.toContainText(/still enables Procedure/i);
  await expect(row.locator(".setupLadderRepairs")).toHaveCount(0);
  // The repair is unsaved, so the server's verdict for the saved configuration stays on screen and is
  // labelled as the last saved check instead of being silently cleared by a local edit.
  await expect(row).toContainText(/From the last saved check/i);
  await expect(row.locator('[role="alert"]')).toHaveText(
    /A level without verification capability cannot enable verification artifacts/i,
  );
  await expect(systemSentence(page)).toContainText("System test procedures: Off — Unsaved change");
  await page.screenshot({ path: testInfo.outputPath("system-contradiction-repaired.png"), fullPage: true });

  // The repair is a deliberate unsaved edit; saving it is what makes the server agree, and the enabled
  // software profiles are untouched by it.
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  const repaired = await persistedDraft(page, draftId);
  expect(step(repaired, "System")?.capabilities).toBe(5);
  expect(step(repaired, "System")?.enabledArtifactKinds).toEqual([]);
  expect(step(repaired, "HighLevel")?.enabledArtifactKinds).toEqual(["Case", "Procedure"]);
});
