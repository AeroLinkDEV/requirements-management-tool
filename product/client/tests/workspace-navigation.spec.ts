import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { resolveWorkspaceContext } from "../src/workspaceContext";
import { parseRoute, projectAreaPath, projectSlugOf, routePath } from "../src/routing";

const fms = { program: { id: "fms-program", name: "FMS Program", code: "FMS" }, projects: [{
  project: { id: "fms-project", name: "FMS Product Development", softwareProduct: "FMS" },
  releases: [{ id: "fms-old", version: "1.5", isReleased: true }, { id: "fms-current", version: "1.6", isReleased: false }],
}] };
const other = { program: { id: "other-program", name: "Other Program", code: "OTHER" }, projects: [{
  project: { id: "other-project", name: "DOORS Import Practice", softwareProduct: "Practice" },
  releases: [{ id: "other-build", version: "1.6", isReleased: false }],
}] };
const workspaces = [other, fms];
const fmsPath = projectAreaPath(projectSlugOf("FMS Product Development"), "builds");
const context = { programId: "fms-program", projectId: "fms-project", releaseId: "fms-current" };

async function mockShell(page: Page, load: () => Promise<unknown> = async () => workspaces) {
  await page.route("**/api/**", async route => {
    const path = new URL(route.request().url()).pathname;
    const json = path === "/api/auth/me"
      ? { id: "author", userName: "author", displayName: "Author", isAdministrator: false, mustChangePassword: false, programs: [] }
      : path === "/api/workspaces" ? await load()
        : path.endsWith("/configuration") ? { effectiveSteps: [{ catalogueEntry: "System", capabilities: 15 }] }
          : path === "/api/dashboard" ? { system: {}, software: {}, verification: { system: {}, hlr: {}, llr: {} } }
            : [];
    await route.fulfill({ json });
  });
}

test("an authenticated root destination canonicalizes to the Projects portal", async ({ page }) => {
  await mockShell(page);
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Projects", exact: true })).toBeVisible();
  await expect(page).toHaveURL(/\/projects$/);
});

test("delayed hydration retains a real project-card selection and exact build scope", async ({ page }) => {
  let deliver: (value: unknown) => void = () => { throw new Error("Workspace request has not started"); };
  const pending = new Promise(resolve => { deliver = resolve; });
  await mockShell(page, () => pending);
  await page.goto("/projects");
  await page.getByRole("link", { name: "Open FMS Product Development", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Opening workspace" })).toBeVisible();
  deliver(workspaces);
  const build = page.getByRole("button", { name: /Open Build 1.6/i });
  await expect(build).toBeEnabled();
  await build.click();
  await expect(page).toHaveURL(new RegExp("/programs/fms-program/projects/fms-project/releases/fms-current/"));
  await expect(page.getByRole("complementary")).toContainText("FMS Product Development");
  await page.goBack();
  await expect(page).toHaveURL(new RegExp(fmsPath + "$"));
  await expect(build).toBeEnabled();
  await page.goForward();
  await expect(page.getByRole("heading", { name: "FMS 1.6", exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole("complementary")).toContainText("FMS Product Development");
});

test("missing exact build never substitutes the current build", async ({ page }) => {
  await mockShell(page);
  await page.goto(routePath({ ...context, releaseId: "removed" }, "dashboard"));
  await expect(page.getByRole("heading", { name: "Workspace unavailable", exact: true })).toBeVisible();
  await expect(page.locator(".contextBar")).toHaveCount(0);
});

test("project switches and browser history resolve the named project's own requests", async ({ page }) => {
  await mockShell(page);
  await page.goto(fmsPath);
  await page.getByRole("button", { name: "Projects", exact: true }).click();
  const imported = page.waitForRequest(request => request.url().includes("/api/baseline-imports?projectId=other-project"));
  await page.getByRole("link", { name: "Open DOORS Import Practice", exact: true }).click();
  await imported;
  await page.getByRole("button", { name: "← Software Builds", exact: true }).click();
  await expect(page.getByRole("heading", { name: "DOORS Import Practice", exact: true })).toBeVisible();
  await page.goBack();
  await page.goBack();
  await page.goBack();
  await expect(page.getByRole("heading", { name: "FMS Product Development", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /Open build 1.6/i })).toBeEnabled();
});

test("legacy project-wide document links discard obsolete build context", async ({ page }) => {
  await mockShell(page);
  await page.goto("/programs/fms-program/projects/fms-project/releases/removed/documentation-center");
  await expect(page).toHaveURL(/\/programs\/fms-program\/projects\/fms-project\/documentation-center$/);
  await expect(page.getByRole("heading", { name: "Workspace unavailable", exact: true })).toHaveCount(0);
});

test("workspace failures have a truthful retry state", async ({ page }) => {
  await mockShell(page);
  await page.route("**/api/workspaces", route => route.fulfill({ status: 403, json: { error: "Denied" } }));
  await page.goto(fmsPath);
  await expect(page.getByRole("heading", { name: "Workspace access unavailable" })).toBeVisible();
  await page.unroute("**/api/workspaces");
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await expect(page.getByRole("button", { name: /Open Build 1.6/i })).toBeEnabled();
});

test("malformed build data cannot display or request a fabricated workspace and retry retains intent", async ({ page }) => {
  await mockShell(page);
  const dashboardRequests: string[] = [];
  page.on("request", request => { if (request.url().includes("/api/dashboard?")) dashboardRequests.push(request.url()); });
  await page.route("**/api/workspaces", route => route.fulfill({ json: [{ ...fms, projects: [{
    project: fms.projects[0].project, releases: [{ version: "1.6", isReleased: "false" }],
  }] }] }));
  const target = routePath(context, "dashboard");
  await page.goto(target);
  await expect(page.getByRole("heading", { name: "Workspace access unavailable" })).toBeVisible();
  await expect(page.locator(".contextBar")).toHaveCount(0);
  expect(dashboardRequests).toEqual([]);
  await expect(page).toHaveURL(new RegExp("fms-current/command-center$"));
  await page.unroute("**/api/workspaces");
  await page.getByRole("button", { name: "Retry", exact: true }).click();
  await expect(page.getByRole("heading", { name: "FMS 1.6", exact: true })).toBeVisible();
  expect(dashboardRequests.every(url => url.includes("projectId=fms-project") && url.includes("releaseId=fms-current"))).toBe(true);
});

test("late dashboard completion cannot overwrite a newer build", async ({ page }) => {
  await mockShell(page);
  let deliver = () => {};
  const pending = new Promise<void>(resolve => { deliver = resolve; });
  let started = () => {};
  const requested = new Promise<void>(resolve => { started = resolve; });
  await page.route("**/api/dashboard?**", async route => {
    const old = new URL(route.request().url()).searchParams.get("releaseId") === "fms-old";
    if (old) { started(); await pending; }
    const summary = { total: old ? 99 : 17, draft: 0, inReview: 0, approved: 0, deferred: 0 };
    await route.fulfill({ json: { system: summary, software: summary, verification: {
      system: { totalChangeRequests: 0 }, hlr: { totalChangeRequests: 0 }, llr: { totalChangeRequests: 0 },
    } } });
  });
  await page.goto(fmsPath);
  await page.getByRole("button", { name: /Open build 1.5/i }).click();
  await requested;
  await page.getByRole("button", { name: "← Back to Software Builds" }).click();
  await page.getByRole("button", { name: /Open build 1.6/i }).click();
  const total = page.locator(".dashboardAreaCard.system .dashboardTotal strong");
  // The request's completion, rather than a sleep, establishes that the stale response was delivered.
  const completed = page.waitForResponse(response => response.url().includes("/api/dashboard?") && response.url().includes("fms-old"));
  deliver();
  await completed;
  await expect(page.getByRole("heading", { name: "FMS 1.6", exact: true })).toBeVisible();
  await expect(total).toHaveText("17");
});

test("a failed build read does not display another build's summary", async ({ page }) => {
  await mockShell(page);
  await page.route("**/api/dashboard?**", route => route.fulfill({ status: 503, json: { error: "Unavailable" } }));
  await page.goto(routePath(context, "dashboard"));
  await expect(page.getByRole("alert")).toContainText("Build work summary could not be loaded.");
  await expect(page.locator(".dashboardTotal")).toHaveCount(0);
});

test("route resolution preserves exact history and rejects absent program, project and build", () => {
  const route = parseRoute(routePath({ ...context, releaseId: "fms-old" }, "requirements", "system", "requirement") + "&requirementRevisionId=revision");
  const resolved = resolveWorkspaceContext(workspaces, route);
  expect(resolved.release?.id).toBe("fms-old");
  expect(route.requirementRevisionId).toBe("revision");
  for (const missing of [{ programId: "removed" }, { projectId: "other-project" }, { releaseId: "other-build" }]) {
    expect(resolveWorkspaceContext(workspaces, { ...route, ...missing })).toEqual({ active: undefined, project: undefined, release: undefined, unavailable: true });
  }
  expect(resolveWorkspaceContext(workspaces, parseRoute(fmsPath)).project?.project.id).toBe("fms-project");
  expect(resolveWorkspaceContext(workspaces, { ...route, view: "managedDocuments", releaseId: "removed" }).unavailable).toBe(false);
});

test("new package authoring selection survives route parsing for each verification scope", () => {
  for (const kind of ["Procedure", "HighLevel", "LowLevelProcedure"]) {
    const discipline = kind === "Procedure" ? "systemTest" : "softwareTest";
    const route = parseRoute(routePath(context, "testChangeRequests", discipline, "saved-package", kind));
    expect(route).toMatchObject({ ...context, view: "testChangeRequests", discipline, artifactId: "saved-package" });
  }
});
