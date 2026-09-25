import { expect, test } from "@playwright/test";
import { login } from "./auth";

test("successful login opens the authorized Projects selector with actual project identities", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await login(page, "admin", { openProject: false });

  await expect(page).toHaveURL(/\/projects$/);
  await expect(page.getByRole("heading", { name: "Projects", level: 1 })).toBeVisible();
  await expect(page.getByText("Select a project to continue.")).toBeVisible();

  const cards = page.locator("[data-project-card]");
  await expect(cards.first()).toBeVisible();
  expect(await cards.count()).toBeGreaterThan(0);
  await expect(page.locator("details.sampleProjectsSection")).toHaveCount(0);
  // #1047: the mock catalogue sits beside the real projects in the same grid, as before #1038. Each is badged
  // Mock and is neither a link nor an authorized project card, so it can never open a build.
  const mocks = page.locator("[data-project-list] [data-mock-project]");
  await expect(mocks).toHaveCount(9);
  await expect(mocks.getByText("Mock", { exact: true })).toHaveCount(9);
  await expect(page.locator("[data-mock-project][data-project-card], a[data-mock-project]")).toHaveCount(0);
  await expect(page.getByRole("link", { name: /GPS Receiver Modernization/ })).toHaveCount(0);
  const active = page.getByRole("link", { name: "Open FMS Product Development" });
  await expect(active).toBeVisible();
  await expect(active).toHaveAttribute("href", /^\/projects\/[^/]+\/builds$/);
  await expect(active).toHaveAttribute("data-project-id", /.+/);

  // Only an administrator can start a draft. The button is a real action, while the project cards are
  // sourced from the authorized workspace projection and carry stable IDs.
  await expect(page.getByRole("button", { name: "Create New Project", exact: true })).toBeVisible();
  await active.focus();
  await expect(active).toBeFocused();
  await active.press("Enter");
  await expect(page).toHaveURL(/\/projects\/[^/]+\/builds$/);
  await expect(page.getByRole("heading", { name: "Software Builds" })).toBeVisible();
});

test("the authorized project grid collapses cleanly without horizontal scrolling", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page, "admin", { openProject: false });

  const cards = page.locator("[data-project-card]");
  await expect(cards.first()).toBeVisible();
  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: document.documentElement.clientWidth,
    columns: new Set(
      [...document.querySelectorAll("[data-project-card]")]
        .filter((card) => card.getClientRects().length > 0)
        .map((card) => Math.round(card.getBoundingClientRect().left)),
    ).size,
  }));
  expect(dimensions.documentWidth).toBe(dimensions.viewportWidth);
  expect(dimensions.columns).toBe(1);
  await expect(page.locator("details.sampleProjectsSection")).toHaveCount(0);
  if (process.env.AEROLINK_PROJECTS_MOBILE_SCREENSHOT)
    await page.screenshot({
      path: process.env.AEROLINK_PROJECTS_MOBILE_SCREENSHOT,
      fullPage: true,
    });
});

test("the project and build selectors survive refresh before entering a build-specific deep route", async ({
  page,
}) => {
  await login(page, "admin", { openProject: false });
  await page.reload();

  await expect(page).toHaveURL(/\/projects$/);
  await expect(page.getByRole("heading", { name: "Projects" })).toBeVisible();
  await page.getByRole("link", { name: "Open FMS Product Development" }).click();
  await expect(page).toHaveURL(/\/projects\/[^/]+\/builds$/);
  await page.reload();
  await expect(page.getByRole("heading", { name: "Software Builds" })).toBeVisible();

  await page.getByRole("button", { name: "Open build 1.6" }).click();
  const workspaceUrl = page.url();
  await page.reload();
  await expect(page).toHaveURL(workspaceUrl);
  await expect(page.getByRole("heading", { name: "Command Center" })).toBeVisible();
});
