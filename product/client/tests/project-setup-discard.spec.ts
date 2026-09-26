import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { apiBase, continuePastFeatures, login } from "./auth";

/**
 * #1045 owner follow-up: Discard setup removes an unfinished saved setup from active discovery. It is
 * logical abandonment, never Project deletion, and the confirmation names the setup before anything is
 * written.
 *
 * Journeys 1 and 2 drive the real browser, the real API and the run's disposable database. Journey 3 is a
 * labelled transport fixture for the Finalizing presentation: a setup being finalized cannot be put into
 * that state through the supported API without completing it, so the list response is supplied by the test
 * harness and the assertion is about which control the product offers, not about server behaviour.
 */

type DraftView = { state: string; version: number; project: { name: string } };

async function draftState(page: Page, draftId: string) {
  const response = await page.request.get(`${apiBase}/api/project-setups/${draftId}`);
  expect(response.ok(), await response.text()).toBeTruthy();
  return (await response.json()) as DraftView;
}

async function listedDraftIds(page: Page) {
  const response = await page.request.get(`${apiBase}/api/project-setups`);
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { draftId: string }[]).map((entry) => entry.draftId);
}

/** Create and save a real unfinished setup through the supported walkthrough, then return to Projects. */
async function createSavedSetupThroughWalkthrough(page: Page, projectName: string) {
  await page.goto("/projects/new");
  await expect(page.getByRole("heading", { name: "Create New Project", level: 1 })).toBeVisible();
  await page.getByLabel("Project name").fill(projectName);
  await page.getByLabel("Software product").fill(`${projectName} software`);
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Fresh project").check();
  await page.getByRole("button", { name: "Continue" }).click();
  await continuePastFeatures(page);
  await page.getByLabel("Version").fill("0.01");
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  const draftId = new URL(page.url()).pathname.split("/").pop() ?? "";
  await page.getByRole("button", { name: "Save and exit" }).click();
  await expect(page.getByRole("heading", { name: "Projects", level: 1 })).toBeVisible();
  return draftId;
}

const setupCard = (page: Page, draftId: string) => page.locator(`[data-setup-draft-id="${draftId}"]`);

test("an unfinished setup is discarded only after a named confirmation, and Cancel changes nothing", async ({
  page,
}, testInfo) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Discard journey ${Date.now().toString(36)}`;
  const draftId = await createSavedSetupThroughWalkthrough(page, projectName);
  const card = setupCard(page, draftId);
  await expect(card).toBeVisible();
  await expect(card).toContainText(projectName);
  await page.screenshot({ path: testInfo.outputPath("discard-card-before.png"), fullPage: true });

  // Cancel writes nothing and leaves the confirmation behind.
  await card.getByRole("button", { name: "Discard setup" }).click();
  const confirmation = card.getByRole("group", { name: `Confirm discarding ${projectName}` });
  await expect(confirmation).toContainText(projectName);
  await expect(confirmation).toContainText(/not Project deletion/i);
  await expect(confirmation).toContainText(/cannot be undone from here/i);
  await page.screenshot({ path: testInfo.outputPath("discard-confirmation.png"), fullPage: true });
  await confirmation.getByRole("button", { name: "Cancel" }).click();
  await expect(confirmation).toHaveCount(0);
  await expect(card).toBeVisible();
  expect((await draftState(page, draftId)).state).toBe("Draft");
  await page.reload();
  await expect(setupCard(page, draftId)).toBeVisible();
  expect(await listedDraftIds(page)).toContain(draftId);

  // The confirmed discard names the setup, reports the outcome, and the setup stays gone after reload.
  await setupCard(page, draftId).getByRole("button", { name: "Discard setup" }).click();
  await setupCard(page, draftId)
    .getByRole("group", { name: `Confirm discarding ${projectName}` })
    .getByRole("button", { name: "Discard setup" })
    .click();
  await expect(page.locator(".setupDraftsNotice")).toContainText(
    new RegExp(`Discarded the unfinished setup .*${projectName}`),
  );
  await expect(setupCard(page, draftId)).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("discard-completed.png"), fullPage: true });

  expect((await draftState(page, draftId)).state).toBe("Abandoned");
  expect(await listedDraftIds(page)).not.toContain(draftId);
  await page.reload();
  await expect(setupCard(page, draftId)).toHaveCount(0);
  expect(await listedDraftIds(page)).not.toContain(draftId);

  // Logical abandonment only: no Project was created for this setup.
  const workspaces = await page.request.get(`${apiBase}/api/workspaces`);
  expect(workspaces.ok(), await workspaces.text()).toBeTruthy();
  const listing = (await workspaces.json()) as { projects: { project: { name: string } }[] }[];
  expect(listing.flatMap((workspace) => workspace.projects).some((entry) => entry.project.name === projectName)).toBe(
    false,
  );
});

test("a stale open page cannot save, finalize or resurrect a discarded setup", async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Discard stale page ${Date.now().toString(36)}`;
  const draftId = await createSavedSetupThroughWalkthrough(page, projectName);
  const before = await draftState(page, draftId);

  // The walkthrough is open on this setup while another operator discards it through the supported route.
  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  const discard = await page.request.post(`${apiBase}/api/project-setups/${draftId}/discard`, {
    data: { expectedVersion: before.version },
  });
  expect(discard.ok(), await discard.text()).toBeTruthy();

  // Saving from the abandoned page is refused and says why; the screen does not advance.
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.locator(".projectSetupError")).toContainText(/discarded/i);
  await expect(page.getByRole("heading", { name: "Review the requirement ladder", level: 2 })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("discard-stale-save-refused.png"), fullPage: true });

  // Finalizing the discarded setup is refused too, and it stays discarded.
  const finalize = await page.request.post(`${apiBase}/api/project-setups/${draftId}/finalize`, {
    data: { expectedVersion: before.version, idempotencyKey: "stale-page-after-discard" },
  });
  expect(finalize.status(), await finalize.text()).toBe(400);

  // Reopening the recorded setup reports what happened instead of showing an editable walkthrough.
  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "This setup was discarded" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Create Project" })).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("discard-stale-page-reopened.png"), fullPage: true });

  expect((await draftState(page, draftId)).state).toBe("Abandoned");
  expect(await listedDraftIds(page)).not.toContain(draftId);
});

test("a setup that is being finalized is not offered as a discard target", async ({ page }) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const suffix = Date.now().toString(36);
  const finalizingId = "00000000-0000-4000-8000-000000001045";
  const draftId = "00000000-0000-4000-8000-000000001046";
  await page.route(`${apiBase}/api/project-setups`, async (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify([
        {
          draftId: finalizingId,
          state: "Finalizing",
          currentStep: "Review",
          version: 9,
          project: { name: `Finalizing ${suffix}`, softwareProduct: "Finalizing product" },
        },
        {
          draftId,
          state: "Draft",
          currentStep: "Ladder",
          version: 4,
          project: { name: `Unfinished ${suffix}`, softwareProduct: "Unfinished product" },
        },
      ]),
    });
  });

  await page.goto("/projects");
  await expect(page.getByRole("heading", { name: "Projects", level: 1 })).toBeVisible();
  const finalizingCard = setupCard(page, finalizingId);
  await expect(finalizingCard).toContainText(/Finalizing — resume to recover the result/i);
  // Resuming a finalization to recover its recorded result stays available; discarding it does not.
  await expect(finalizingCard.getByRole("button", { name: "Resume setup" })).toBeVisible();
  await expect(finalizingCard.getByRole("button", { name: "Discard setup" })).toHaveCount(0);
  // The unfinished draft beside it keeps the action.
  await expect(setupCard(page, draftId).getByRole("button", { name: "Discard setup" })).toBeVisible();
  await page.unroute(`${apiBase}/api/project-setups`);
});
