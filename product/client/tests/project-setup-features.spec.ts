import { expect, test } from "@playwright/test";
import { apiBase, login } from "./auth";

/**
 * #1113: Create New Project has a Features step, so a project can start with only the modules it uses. This
 * drives the real walkthrough against the real API and the run's disposable database.
 */
test("a new project can start as Problem Reports only, and the choice is its first feature history entry", async ({
  page,
}, testInfo) => {
  await login(page, "admin", { openProject: false });
  await page.goto("/projects/new");
  await page.getByLabel("Project name").fill(`Features UI ${Date.now()}`);
  await page.getByLabel("Software product").fill("Features UI Software");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Fresh project").check();
  await page.getByRole("button", { name: "Continue" }).click();

  await expect(page.getByRole("heading", { name: "Choose the project's features", level: 2 })).toBeVisible();
  const feature = (name: string) => page.getByRole("checkbox", { name: new RegExp(`^${name}`) });
  for (const name of ["Team Work", "Requirements", "Verification", "Code", "Documentation Center", "Problem Reports", "Release"])
    await expect(feature(name)).toBeChecked();

  // The dependency rule is explained before anything is sent; the server still decides.
  await feature("Requirements").uncheck();
  await expect(page.getByRole("alert")).toHaveText(/Code needs Requirements/);
  await feature("Code").uncheck();
  // Verification may stand without Requirements (DEC-144), so nothing more is refused.
  await expect(page.getByRole("alert")).toHaveCount(0);
  await feature("Verification").uncheck();
  await feature("Documentation Center").uncheck();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("setup-features-step.png"), fullPage: true });

  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Version").fill("1.05");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and approval rules", level: 2 })).toBeVisible();
  await page.getByLabel(/explicitly accept these concrete review and approval rules/i).check();
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.locator(".setupReviewList > div").filter({ has: page.locator("dt", { hasText: /^Features$/ }) }))
    .toContainText("Team Work, Problem Reports, Release");

  // Going back shows the saved choice, not the default.
  await page.getByRole("button", { name: /3\. Features/ }).click();
  await expect(feature("Requirements")).not.toBeChecked();
  await expect(feature("Problem Reports")).toBeChecked();
  await page.getByRole("button", { name: /8\. Review and finish/ }).click();

  await page.getByRole("button", { name: "Create Project" }).click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]+\/builds$/);
  const projectId = new URL(page.url()).pathname.split("/")[2];

  const response = await page.request.get(`${apiBase}/api/projects/${projectId}/features`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const features = (await response.json()) as {
    persisted: boolean;
    enabled: string[];
    history: { reason: string; previous: string[]; enabled: string[] }[];
  };
  expect(features.persisted).toBe(true);
  expect(features.enabled).toEqual(["TeamWork", "ProblemReports", "Release"]);
  expect(features.history).toHaveLength(1);
  expect(features.history[0].reason).toBe("Chosen during project setup.");
  expect(features.history[0].enabled).toEqual(["TeamWork", "ProblemReports", "Release"]);
});
