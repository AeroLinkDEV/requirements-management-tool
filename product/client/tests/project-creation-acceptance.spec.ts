import { expect, test } from "@playwright/test";
import type { APIRequestContext, Page } from "@playwright/test";
import { apiBase, apiLogin, login } from "./auth";
import { routePath } from "../src/routing";

// Route handlers in this file fetch and parse real responses. Let them settle before Playwright closes the
// context, or the next test inherits "Response has been disposed" from this one (#1001).
test.afterEach(async ({ page }) => {
  await page.unrouteAll({ behavior: "wait" });
});

type WorkspaceProjection = {
  program: { id: string; name: string; code: string };
  projects: Array<{
    project: { id: string; name: string; softwareProduct: string };
    releases: Array<{
      id: string;
      version: string;
      isReleased: boolean;
      predecessorReleaseId?: string | null;
    }>;
  }>;
};

async function createExistingWorkspace(request: APIRequestContext, name: string, code: string) {
  const response = await request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName: `${name} Program`,
      programCode: code,
      projectName: name,
      softwareProduct: `${name} software`,
      initialRelease: "0.01",
      initialReleaseIsReleased: false,
    },
  });
  expect(response.ok(), await response.text()).toBeTruthy();
  return await response.json() as {
    program: { id: string };
    project: { id: string; name: string };
    release: { id: string; version: string };
  };
}

async function completeFreshSetup(
  page: Page,
  projectName: string,
  version: string,
  options: { customerOnly?: boolean } = {},
) {
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
  if (options.customerOnly) {
    const rows = page.locator(".setupLadderRows > li");
    await rows.nth(0).getByRole("combobox").first().selectOption("Customer");
    await rows.nth(2).getByRole("button", { name: "Remove" }).click();
    await rows.nth(1).getByRole("button", { name: "Remove" }).click();
    await expect(rows).toHaveCount(1);
    await expect(rows.nth(0).getByRole("combobox").first()).toHaveValue("Customer");
  }
  await page.getByRole("button", { name: "Continue" }).click();

  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  if (options.customerOnly) {
    // A ladder change leaves the prior accepted definition visible beside the server's current
    // suggestion. Applying that suggestion is an explicit creator action; acceptance is still required.
    const refresh = page.getByRole("button", { name: "Use rules for this ladder" });
    await expect(refresh).toBeVisible();
    await refresh.click();
    await expect(page.getByText(/no applicable review subjects for this ladder/i)).toBeVisible();
  }
  const accepted = page.getByLabel(/explicitly accept these concrete review and approval rules/i);
  await expect(accepted).toBeEnabled();
  await accepted.check();
  await page.getByRole("button", { name: "Continue" }).click();

  await expect(page.getByRole("heading", { name: "Repository setup", level: 2 })).toBeVisible();
  await expect(page.getByText(/Pending\./).first()).toBeVisible();
  await expect(page.getByText(/credentials stay with the server/i)).toBeVisible();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
  await page.getByRole("button", { name: "Create Project" }).click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]+\/builds$/);
  return page.url();
}

async function getWorkspaces(page: Page) {
  const response = await page.request.get(`${apiBase}/api/workspaces`);
  expect(response.ok(), await response.text()).toBeTruthy();
  return await response.json() as WorkspaceProjection[];
}

function projectByName(workspaces: WorkspaceProjection[], name: string) {
  const entries = workspaces.flatMap(workspace => workspace.projects.map(project => ({ workspace, project })));
  const match = entries.find(entry => entry.project.project.name === name);
  expect(match, `workspace project ${name}`).toBeTruthy();
  return match!;
}

test("fresh creation joins an existing project and requires explicit build selection", async ({ page, request }, testInfo) => {
  test.setTimeout(120_000);
  await apiLogin(request);
  const suffix = Date.now().toString(36);
  const existingName = `Existing authorized ${suffix}`;
  await createExistingWorkspace(request, existingName, `EA${suffix}`.slice(0, 12));

  await login(page, "admin", { openProject: false });
  const existingCard = page.getByRole("link", { name: `Open ${existingName}`, exact: true });
  await expect(existingCard).toBeVisible();
  await expect(existingCard).toHaveAttribute("href", /^\/projects\/[0-9a-f-]+\/builds$/);
  await expect(existingCard).toHaveAttribute("data-project-id", /[0-9a-f-]+/);
  const authorizedProjectCountBeforeNewProject = await page.locator("[data-project-card]").count();
  const freshName = `Fresh alongside ${suffix}`;
  await completeFreshSetup(page, freshName, "1.02");

  const freshId = new URL(page.url()).pathname.split("/")[2];
  expect(freshId).toMatch(/^[0-9a-f-]+$/);
  await expect(page.locator("[data-build-card]")).toHaveCount(1);
  await expect(page.locator("[data-build-card]").getByText("In Work", { exact: true })).toBeVisible();
  await expect(page.locator("[data-build-card]").getByText("SW-01.02", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("fresh-alongside-lineage.png"), fullPage: true });

  const fresh = projectByName(await getWorkspaces(page), freshName);
  const freshRelease = fresh.project.releases[0];
  expect(fresh.project.project.id).toBe(freshId);
  expect(fresh.project.releases).toHaveLength(1);
  expect(freshRelease.isReleased).toBe(false);
  // Completion lands at the Project's lineage selector. It must not silently enter this one build.
  await expect(page.getByRole("heading", { name: "Software Builds", level: 1 })).toBeVisible();
  await expect(page.getByRole("button", { name: /Open build 1\.02/ })).toBeVisible();
  await page.getByRole("button", { name: /Open build 1\.02/ }).click();
  await expect(page).toHaveURL(`/programs/${fresh.workspace.program.id}/projects/${fresh.project.project.id}/releases/${freshRelease.id}/command-center`);
  await expect(page.getByRole("heading", { name: "Command Center", level: 1 })).toBeVisible();
  await page.goto(`/programs/${fresh.workspace.program.id}/projects/${fresh.project.project.id}/releases/00000000-0000-4000-8000-000000000404/command-center`);
  await expect(page.getByRole("heading", { name: "Workspace unavailable", level: 1 })).toBeVisible();
  await expect(page.getByText(/No other workspace has been substituted/i)).toBeVisible();

  await page.getByRole("button", { name: "Back to Projects", exact: true }).click();
  await expect(page.getByRole("link", { name: `Open ${existingName}`, exact: true })).toBeVisible();
  const freshCard = page.locator(`[data-project-id="${fresh.project.project.id}"]`);
  await expect(freshCard).toHaveAttribute("href", `/projects/${fresh.project.project.id}/builds`);
  await expect(page.locator("[data-project-card]")).toHaveCount(authorizedProjectCountBeforeNewProject + 1);
});

test("legacy slug selection fails closed when authorized projects share a name", async ({ page, request }) => {
  await apiLogin(request);
  const suffix = Date.now().toString(36);
  const duplicateName = `Duplicate project ${suffix}`;
  await createExistingWorkspace(request, duplicateName, `DP${suffix}`.slice(0, 12));
  await createExistingWorkspace(request, duplicateName, `DQ${suffix}`.slice(0, 12));

  await login(page, "admin", { openProject: false });
  const cards = page.locator("[data-project-card]").filter({ hasText: duplicateName });
  await expect(cards).toHaveCount(2);
  const ids = await cards.evaluateAll(items => items.map(item => item.getAttribute("data-project-id")));
  expect(new Set(ids).size).toBe(2);
  const slug = duplicateName.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
  await page.goto(`/projects/${slug}/builds`);
  await expect(page.getByRole("heading", { name: "Workspace unavailable", level: 1 })).toBeVisible();
  await expect(page.getByText(/No other workspace has been substituted/i)).toBeVisible();
});

test("setup recovery survives context closure and a fresh sign-in", async ({ page, browser }, testInfo) => {
  await login(page, "admin", { openProject: false });
  const projectName = `Context recovery ${Date.now().toString(36)}`;
  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(projectName);
  await page.getByLabel("Software product").fill(`${projectName} software`);
  await page.getByRole("button", { name: "Save and exit" }).click();
  await expect(page).toHaveURL(/\/projects$/);
  const draft = page.locator("[data-setup-draft-id]").filter({ hasText: projectName });
  await expect(draft).toBeVisible();
  const draftId = await draft.getAttribute("data-setup-draft-id");
  expect(draftId).toMatch(/^[0-9a-f-]+$/);

  const oldContext = page.context();
  const nextContext = await browser.newContext();
  await oldContext.close();
  const resumed = await nextContext.newPage();
  await login(resumed, "admin", { openProject: false });
  await expect(resumed.locator(`[data-setup-draft-id="${draftId}"]`)).toBeVisible();
  await resumed.locator(`[data-setup-draft-id="${draftId}"]`).getByRole("button", { name: "Resume setup" }).click();
  await expect(resumed).toHaveURL(`/projects/setup/${draftId}`);
  await expect(resumed.getByLabel("Project name")).toHaveValue(projectName);
  await expect(resumed.getByText(/Saved on the server/i)).toHaveCount(0);
  await resumed.getByRole("button", { name: "Sign out" }).click();
  await expect(resumed.getByRole("button", { name: /Sign in securely/ })).toBeVisible();
  await login(resumed, "admin", { openProject: false });
  await resumed.locator(`[data-setup-draft-id="${draftId}"]`).getByRole("button", { name: "Resume setup" }).click();
  await expect(resumed.getByLabel("Project name")).toHaveValue(projectName);
  await resumed.screenshot({ path: testInfo.outputPath("recovered-after-context-close.png"), fullPage: true });
  await nextContext.close();
});

test("a committed fresh finalization survives a lost client response and retry keeps its IDs", async ({ page }, testInfo) => {
  await login(page, "admin", { openProject: false });
  const projectName = `Lost response ${Date.now().toString(36)}`;
  const createdResponse = page.waitForResponse(response =>
    response.url().endsWith("/api/project-setups") && response.request().method() === "POST",
  );
  await page.goto("/projects/new");
  const createdDraft = await (await createdResponse).json() as { draftId?: string };
  const draftId = createdDraft.draftId;
  expect(draftId).toMatch(/^[0-9a-f-]+$/);
  await page.getByLabel("Project name").fill(projectName);
  await page.getByLabel("Software product").fill(`${projectName} software`);
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Fresh project").check();
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Version").fill("1.04");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel(/explicitly accept these concrete review and approval rules/i).check();
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("button", { name: "Create Project" })).toBeEnabled();
  let intercepted = false;
  let finalizeUrl = "";
  let finalizeBody: Record<string, unknown> | undefined;
  let idempotencyKey = "";
  await page.route(/\/api\/project-setups\/[^/]+\/finalize$/, async route => {
    if (intercepted) {
      await route.continue();
      return;
    }
    intercepted = true;
    finalizeUrl = route.request().url();
    finalizeBody = JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>;
    idempotencyKey = route.request().headers()["idempotency-key"] ?? "";
    expect(idempotencyKey).toBeTruthy();
    // route.fetch reaches the real server, then the client transport is deliberately reset before it sees
    // the committed response. This distinguishes a committed/client-lost outcome from a server rejection.
    await route.fetch();
    await route.abort("connectionreset");
  });
  await page.getByRole("button", { name: "Create Project" }).click();
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toBeVisible();
  expect(intercepted).toBe(true);
  await page.unroute(/\/api\/project-setups\/[^/]+\/finalize$/);

  const first = projectByName(await getWorkspaces(page), projectName);
  const firstRelease = first.project.releases[0];
  expect(first.project.releases).toHaveLength(1);
  expect(firstRelease.version).toBe("1.04");
  expect(firstRelease.isReleased).toBe(false);
  const retry = await page.request.post(finalizeUrl, {
    data: finalizeBody,
    headers: { "Content-Type": "application/json", "Idempotency-Key": idempotencyKey },
  });
  expect(retry.ok(), await retry.text()).toBeTruthy();
  const retryBody = await retry.json() as { alreadyCompleted?: boolean; projectId?: string; releaseId?: string };
  expect(retryBody.alreadyCompleted).toBe(true);
  expect(retryBody.projectId).toBe(first.project.project.id);
  expect(retryBody.releaseId).toBe(firstRelease.id);

  await page.reload();
  await expect(page.getByRole("heading", { name: "Project created", level: 2 })).toBeVisible();
  const afterReload = projectByName(await getWorkspaces(page), projectName);
  expect(afterReload.project.project.id).toBe(first.project.project.id);
  expect(afterReload.project.releases).toHaveLength(1);
  expect(afterReload.project.releases[0].id).toBe(firstRelease.id);
  await page.screenshot({ path: testInfo.outputPath("lost-response-recovered.png"), fullPage: true });
});

test("fresh standard services show empty content and pending prerequisites across the real workspace", async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await login(page, "admin", { openProject: false });
  const projectName = `Empty services ${Date.now().toString(36)}`;
  await completeFreshSetup(page, projectName, "0.01");
  const fresh = projectByName(await getWorkspaces(page), projectName);
  const context = { programId: fresh.workspace.program.id, projectId: fresh.project.project.id,
    releaseId: fresh.project.releases[0].id };
  await page.getByRole("button", { name: /Open build 0\.01/ }).click();
  await expect(page.getByRole("heading", { name: "Command Center", level: 1 })).toBeVisible();

  await page.goto(routePath(context, "requirements"));
  await expect(page.getByRole("heading", { name: "No system requirements yet" })).toBeVisible();
  await page.goto(routePath(context, "requirements", "software"));
  await expect(page.getByRole("heading", { name: "No software requirements yet" })).toBeVisible();
  await page.goto(routePath(context, "procedureExplorer", "systemTest"));
  await expect(page.locator(".procedureEmpty")).toContainText("This build has no controlled");
  await page.goto(routePath(context, "testResults", "systemTest"));
  await expect(page.getByText("Nothing has been chosen for this build yet", { exact: true })).toBeVisible();
  await expect(page.locator(".testSetSummary article b")).toHaveText(["0", "0", "0", "0"]);
  await page.goto(routePath(context, "teamwork"));
  await expect(page.getByText("No controlled work is recorded in this project yet.", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("fresh-empty-team-work.png"), fullPage: true });
  await page.goto(routePath(context, "problemReports"));
  await expect(page.getByText("No Problem Reports are recorded for this Project.", { exact: true })).toBeVisible();
  await page.goto(routePath(context, "managedDocuments"));
  await expect(page.getByText("No controlled documents match these filters.", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("fresh-empty-managed-documents.png"), fullPage: true });
  await page.goto(routePath(context, "code"));
  await expect(page.getByText("Repository Pending", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Not evaluated yet", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "+ Record code mapping", exact: true })).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("fresh-pending-code.png"), fullPage: true });
  await page.goto(routePath(context, "baselines"));
  await expect(page.getByText("No candidate baseline exists for this release.", { exact: true })).toBeVisible();
  await page.goto(routePath(context, "release"));
  await expect(page.getByRole("heading", { name: "Release readiness is not configured", exact: true })).toBeVisible();
  await expect(page.getByText("No lifecycle decision package is available for this build.", { exact: true })).toBeVisible();
  await page.goto(routePath(context, "releaseOperations"));
  await expect(page.getByRole("heading", { name: "No release campaign for this version", exact: true })).toBeVisible();
  await expect(page.getByText(/Create a campaign from an eligible candidate baseline/)).toBeVisible();
});

test("a Customer-only fresh project displays a concrete empty standard and truthful empty work state", async ({ page }, testInfo) => {
  await login(page, "admin", { openProject: false });
  const projectName = `Customer empty ${Date.now().toString(36)}`;
  await completeFreshSetup(page, projectName, "1.05", { customerOnly: true });
  await expect(page.locator("[data-build-card]")).toHaveCount(1);
  await expect(page.locator("[data-build-card]").getByText("In Work", { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("customer-empty-lineage.png"), fullPage: true });

  const project = projectByName(await getWorkspaces(page), projectName);
  await expect(page.getByRole("button", { name: /Open build 1\.05/ })).toBeVisible();
  await page.getByRole("button", { name: /Open build 1\.05/ }).click();
  await expect(page.getByRole("heading", { name: "Command Center", level: 1 })).toBeVisible();
  await expect(page.locator(".dashboardAreaCard")).toHaveCount(4);
  await expect(page.locator(".dashboardTotal strong")).toHaveText(["0", "0"]);
  // An empty project has no Problem Reports either (#1113), and says so rather than inventing work.
  await expect(page.locator(".prCardHeadline strong").first()).toHaveText("0");
  await expect(page.getByText("No active Problem Reports in this scope.")).toBeVisible();
  await expect(page.locator(".verificationTriageRows article")).toHaveCount(0);
  await page.getByRole("button", { name: "← Back to Software Builds" }).click();
  await page.getByRole("button", { name: "Project configuration" }).click();
  await expect(page.getByRole("heading", { name: "Project configuration", level: 1 })).toBeVisible();
  await expect(page.locator(".ladderRow")).toHaveCount(1);
  await expect(page.locator(".ladderRow select").first()).toHaveValue("Customer");
  await expect(page).toHaveURL(`/projects/${project.project.project.id}/configuration`);
});
