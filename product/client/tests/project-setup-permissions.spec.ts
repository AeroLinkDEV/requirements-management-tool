import { expect, test } from "@playwright/test";

test("a nonadministrator can resume a draft but cannot accept or create a Project", async ({ page }) => {
  const draftId = "00000000-0000-4000-8000-000000000901";
  const projectName = "Administrator finalization gate";
  const draft = {
    draftId,
    state: "Draft",
    currentStep: "Review",
    version: 4,
    project: { name: projectName, softwareProduct: "Permission gate software" },
    start: { kind: "Fresh" },
    build: { version: "1.02", officialName: "SW-01.02" },
    selectedCategories: [],
    ladder: {
      steps: [{ catalogueEntry: "System", position: 1, capabilities: 7, enabledArtifactKinds: ["Procedure"] }],
      relationships: [],
    },
    reviewRules: {
      accepted: true,
      definition: {
        rules: [{
          subject: "System",
          name: "System review and approval",
          stages: [
            { name: "Engineering review", kind: "Review", requiredRole: "SystemEngineer", authorityKind: "BaseRole" },
            { name: "Engineering approval", kind: "Approval", requiredRole: "SystemEngineer", authorityKind: "BaseRole" },
          ],
        }],
      },
    },
    repository: { mode: "ConfigureLater", status: "Pending", provider: "GitLab", endpoint: null },
    mapping: {},
  };
  let authenticated = false;

  await page.route("**/api/setup/status", async (route) => {
    await route.fulfill({ json: { bootstrapRequired: false, bootstrapEnabled: false } });
  });
  await page.route("**/api/auth/login", async (route) => {
    authenticated = true;
    await route.fulfill({ json: {
      id: "00000000-0000-4000-8000-000000000902",
      userName: "project.creator",
      displayName: "Project Creator",
      email: "creator@example.test",
      isAdministrator: false,
      mustChangePassword: false,
      programs: [],
    } });
  });
  await page.route("**/api/auth/me", async (route) => {
    if (!authenticated) {
      await route.continue();
      return;
    }
    await route.fulfill({ json: {
      id: "00000000-0000-4000-8000-000000000902",
      userName: "project.creator",
      displayName: "Project Creator",
      email: "creator@example.test",
      isAdministrator: false,
      mustChangePassword: false,
      programs: [],
    } });
  });
  await page.route("**/api/workspaces", async (route) => {
    await route.fulfill({ json: [] });
  });
  await page.route("**/api/project-setups", async (route) => {
    if (route.request().method() === "GET") await route.fulfill({ json: [] });
    else await route.continue();
  });
  await page.route(/\/api\/project-setups\/.*$/, async (route) => {
    await route.fulfill({ json: draft });
  });

  await page.goto("/");
  await page.getByLabel("Username").fill("project.creator");
  await page.getByLabel("Password").fill("AeroLink!2026");
  await page.getByRole("button", { name: /Sign in securely/ }).click();
  await page.goto(`/projects/setup/${draftId}`);
  await expect(page.getByRole("heading", { name: "Review and finish", level: 2 })).toBeVisible();
  await expect(page.getByText(/Only an AeroLink administrator can accept source facts or create a Project/i)).toBeVisible();
  const create = page.getByRole("button", { name: "Create Project" });
  await expect(create).toBeDisabled();
});
