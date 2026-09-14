import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { routePath } from "../src/routing";

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

  await cards.nth(1).getByRole("button", { name: /Open build 1\.50/ }).click();
  await expect(page).toHaveURL(routePath({ programId: "lineage-program", projectId: "lineage-project", releaseId: "release-150" }, "dashboard"));
  await expect(page.getByRole("heading", { name: "Command Center", level: 1 })).toBeVisible();
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
