import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { routePath } from "../src/routing";
import { apiBase, login } from "./auth";
import { readFileSync } from "node:fs";

test("actual authorized build identities, states and predecessors drive visual selection", async ({ page }, testInfo) => {
  await login(page, "admin", { openProject: false });
  const response = await page.request.get(`${apiBase}/api/workspaces`);
  expect(response.ok()).toBeTruthy();
  const workspaces = await response.json() as Array<{
    program: { id: string };
    projects: Array<{ project: { id: string }; releases: Array<{
      id: string; version: string; isReleased: boolean; predecessorReleaseId?: string | null;
    }> }>;
  }>;
  const choices = workspaces.flatMap(workspace => workspace.projects.map(project => ({ workspace, project })));
  const fixture = process.env.AEROLINK_E2E_LINEAGE_FIXTURE
    ? JSON.parse(readFileSync(process.env.AEROLINK_E2E_LINEAGE_FIXTURE, "utf8")) as {
      projectId: string; rootReleaseId: string; childReleaseIds: string[];
    } : undefined;
  const choice = choices.find(({ project }) => fixture ? project.project.id === fixture.projectId
    : project.releases.some(release => release.isReleased)
      && project.releases.some(release => !release.isReleased && release.predecessorReleaseId));
  expect(choice, "the isolated seeded installation contains actual historical and working builds").toBeTruthy();
  const { workspace, project } = choice!;
  if (fixture) {
    expect(project.releases.map(release => release.version)).toEqual(["9.0", "10.5", "11.0"]);
    expect(project.releases.filter(release => release.predecessorReleaseId === fixture.rootReleaseId)
      .map(release => release.id).sort()).toEqual([...fixture.childReleaseIds].sort());
    expect(project.releases.filter(release => release.isReleased)).toHaveLength(2);
    expect(project.releases.filter(release => !release.isReleased)).toHaveLength(1);
  }
  await page.goto(`/projects/${project.project.id}/builds`);
  await expect(page.getByRole("heading", { name: "Software Builds", level: 1 })).toBeVisible();
  await expect(page.locator("[data-build-card]")).toHaveCount(project.releases.length);
  for (const release of project.releases) {
    const card = page.locator(`[data-build-id="${release.id}"]`);
    await expect(card).toHaveAttribute("data-build-version", release.version);
    await expect(card.locator("..")).toHaveAttribute("data-predecessor-release-id", release.predecessorReleaseId ?? "");
    await expect(card.getByText(release.isReleased ? "Released" : "In Work", { exact: true })).toBeVisible();
  }
  await page.screenshot({ path: testInfo.outputPath("actual-stored-lineage.png"), fullPage: true });
  const selected = project.releases.find(release => release.isReleased)!;
  await page.locator(`[data-build-id="${selected.id}"]`).getByRole("button", { name: /Open build/ }).click();
  await expect(page).toHaveURL(routePath({ programId: workspace.program.id,
    projectId: project.project.id, releaseId: selected.id }, "dashboard"));
  for (const childId of fixture?.childReleaseIds ?? []) {
    await page.goto(`/projects/${project.project.id}/builds`);
    const card = page.locator(`[data-build-id="${childId}"]`);
    await expect(card).toContainText("SW-09.00");
    await card.getByRole("button", { name: /Open build/ }).click();
    await expect(page).toHaveURL(routePath({ programId: workspace.program.id,
      projectId: project.project.id, releaseId: childId }, "dashboard"));
    await expect(page.getByRole("heading", { name: "Command Center", level: 1 })).toBeVisible();
  }
});

const branchWorkspace = {
  program: { id: "lineage-program", name: "Lineage Program", code: "LIN" },
  projects: [
    {
      project: { id: "lineage-project", name: "Branch Navigation", softwareProduct: "Navigation" },
      releases: [
        { id: "release-100", version: "1.00", isReleased: true, predecessorReleaseId: null },
        { id: "release-150", version: "1.50", isReleased: false, predecessorReleaseId: "release-100" },
        { id: "release-200", version: "2.00", isReleased: true, predecessorReleaseId: "release-100" },
      ],
    },
    {
      // The duplicate display name makes an old mutable slug ambiguous. Stable IDs remain separate links.
      project: { id: "lineage-project-two", name: "Branch Navigation", softwareProduct: "Other Navigation" },
      releases: [{ id: "release-other", version: "1.00", isReleased: false, predecessorReleaseId: null }],
    },
  ],
};

async function mockLineageShell(page: Page) {
  await page.route("**/api/**", async route => {
    const path = new URL(route.request().url()).pathname;
    const json = path === "/api/auth/me"
      ? { id: "admin", userName: "admin", displayName: "Administrator", isAdministrator: true, mustChangePassword: false, programs: [] }
      : path === "/api/workspaces"
        ? [branchWorkspace]
        : path.endsWith("/configuration")
          ? { effectiveSteps: [{ catalogueEntry: "System", capabilities: 15 }] }
          : path === "/api/dashboard"
            ? { system: { total: 0, draft: 0, inReview: 0, approved: 0, deferred: 0 }, software: { total: 0, draft: 0, inReview: 0, approved: 0, deferred: 0 }, verification: { system: { totalChangeRequests: 0, triagedChangeRequests: 0, openDecisions: 0, resolvedDecisions: 0 }, hlr: { totalChangeRequests: 0, triagedChangeRequests: 0, openDecisions: 0, resolvedDecisions: 0 }, llr: { totalChangeRequests: 0, triagedChangeRequests: 0, openDecisions: 0, resolvedDecisions: 0 } } }
            : [];
    await route.fulfill({ json });
  });
}

test("the visual lineage shows released branches from stored predecessors and explicit build selection", async ({ page }, testInfo) => {
  const dashboardRequests: URL[] = [];
  page.on("request", request => {
    const url = new URL(request.url());
    if (url.pathname === "/api/dashboard") dashboardRequests.push(url);
  });
  await mockLineageShell(page);
  await page.goto("/projects/lineage-project/builds");
  await expect(page.getByRole("heading", { name: "Software Builds", level: 1 })).toBeVisible();
  const cards = page.locator("[data-build-card]");
  await expect(cards).toHaveCount(3);
  expect(await cards.evaluateAll(items => items.map(item => ({
    id: item.getAttribute("data-build-id"),
    version: item.getAttribute("data-build-version"),
    predecessor: item.parentElement?.getAttribute("data-predecessor-release-id"),
  })))).toEqual([
    { id: "release-100", version: "1.00", predecessor: "" },
    { id: "release-150", version: "1.50", predecessor: "release-100" },
    { id: "release-200", version: "2.00", predecessor: "release-100" },
  ]);
  await expect(cards.nth(0)).toContainText("None recorded");
  await expect(cards.nth(1)).toContainText("SW-01.00");
  await expect(cards.nth(2)).toContainText("Released");
  await page.screenshot({ path: testInfo.outputPath("branched-lineage.png"), fullPage: true });
  expect(dashboardRequests, "project overview must not request a summary for an absent or inferred build").toEqual([]);

  await cards.nth(1).getByRole("button", { name: /Open build 1\.50/ }).click();
  await expect(page).toHaveURL(routePath({ programId: "lineage-program", projectId: "lineage-project", releaseId: "release-150" }, "dashboard"));
  await expect(page.getByRole("heading", { name: "Command Center", level: 1 })).toBeVisible();
  await expect.poll(() => dashboardRequests.length).toBeGreaterThan(0);
  expect(dashboardRequests.every(url => url.searchParams.get("projectId") === "lineage-project"
    && url.searchParams.get("releaseId") === "release-150")).toBe(true);
});

test("exact build links refuse missing targets and duplicate legacy slugs never select a project", async ({ page }) => {
  await mockLineageShell(page);
  await page.goto(routePath({ programId: "lineage-program", projectId: "lineage-project", releaseId: "removed" }, "dashboard"));
  await expect(page.getByRole("heading", { name: "Workspace unavailable", level: 1 })).toBeVisible();
  await expect(page.getByText(/No other workspace has been substituted/i)).toBeVisible();

  await page.goto("/projects/branch-navigation/builds");
  await expect(page.getByRole("heading", { name: "Workspace unavailable", level: 1 })).toBeVisible();
  await expect(page.getByText(/No other workspace has been substituted/i)).toBeVisible();
});
