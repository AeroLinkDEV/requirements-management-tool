import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup } from "./auth";

test("engineer analyzes impact and creates a rich controlled requirement proposal", async ({
  page,
  request,
}) => {
  test.setTimeout(75_000);
  const assignmentTitle = `Disposition requirement 150 verification impact ${Date.now()}`;
  await apiLogin(request);
  const seed = await request.post(`${apiBase}/api/showcase/seed`, {
    timeout: 45_000,
  });
  expect(seed.ok(), await seed.text()).toBeTruthy();
  await login(page);
  await page
    .locator(".program > select:not(.releaseSelector)")
    .selectOption({ label: "Flight Management System Live Program" });
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements" }).click();
  await expect(
    page.getByRole("heading", { name: "Requirements Workspace" }),
  ).toBeVisible();
  await page.getByText("Workspace tools", { exact: true }).click();
  await page.getByRole("button", { name: /My work/ }).click();
  await expect(
    page.getByRole("heading", { name: "Engineering Work Center" }),
  ).toBeVisible();
  await expect(page.getByText("Repository scale")).toBeVisible();
  await expect(page.getByText("Performance gate")).toBeVisible();
  await page.getByLabel("Close controlled authoring center").click();
  await page.getByLabel("Search requirements").fill("SYSR-00000150");
  await page.getByText(/SYSR-00000150\.\d{2}/).click();
  await page
    .getByRole("button", { name: "Analyze impact & propose change →" })
    .click();
  await expect(page.getByText("Change-impact readiness")).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Verification procedures" }),
  ).toBeVisible();
  await expect(page.getByRole("heading", { name: "Baselines" })).toBeVisible();
  await page.getByRole("button", { name: "Collaboration" }).click();
  await page.getByLabel("Assignee username").fill("admin");
  await page
    .getByLabel("Title", { exact: true })
    .fill(assignmentTitle);
  await page
    .getByLabel("Description")
    .fill(
      "Confirm the existing procedure remains sufficient for the proposed revision.",
    );
  await page.getByLabel("Due date").fill("2026-08-01");
  await page.getByRole("button", { name: "Create assignment" }).click();
  await expect(
    page.getByText(assignmentTitle, {
      exact: true,
    }),
  ).toBeVisible();
  await page.getByRole("button", { name: "My work & operations" }).click();
  await expect(
    page
      .getByText(assignmentTitle, {
        exact: true,
      })
      .first(),
  ).toBeVisible();
  await page.getByRole("button", { name: "Propose controlled change" }).click();
  await expect(
    page.getByText("SCR/SWCR remains the approval authority"),
  ).toBeVisible();
  await page.getByRole("button", { name: "Create Draft SCR/SWCR →" }).click();
  await expect(
    page.getByRole("heading", { name: "Change case" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Check out & edit" }).click();
  await expect(
    page.getByRole("heading", { name: "Controlled requirement authoring" }),
  ).toBeVisible();
  await page
    .locator(".richEditor")
    .fill(
      "**Controlled update**\n- preserve deterministic sequencing\n- verify the affected route mode\n\n[[SYSR-00000150]]",
    );
  const dispositions = page.locator(".editorColumns aside select");
  expect(await dispositions.count()).toBe(5);
  for (let i = 0; i < 5; i++)
    await dispositions.nth(i).selectOption("Affected");
  await page.getByRole("button", { name: "Save & check in" }).click();
  await expect(page.getByText("Requirement impact")).toBeVisible();
  await expect(page.getByText(/Record version/)).toBeVisible();
});
