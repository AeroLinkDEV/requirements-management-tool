import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup } from "./auth";

test("FMS 1.5 released baseline supports active 1.6 work and full lifecycle exploration", async ({
  page,
  request,
}) => {
  test.setTimeout(60_000);
  await apiLogin(request);
  const seed = await request.post(`${apiBase}/api/showcase/seed`, {
    timeout: 45_000,
  });
  expect(seed.ok(), await seed.text()).toBeTruthy();
  await login(page);
  await page
    .locator(".program > select:not(.releaseSelector)")
    .selectOption({ label: "Flight Management System Live Program" });
  await expect(page.getByText("FMS Product Development", { exact: true })).toBeVisible();
  await expect(page.getByLabel("Active release")).toHaveValue(/.+/);
  await expect(
    page.getByRole("heading", { name: /Complete .+ lifecycle inventory/ }),
  ).toBeVisible();
  const inventory = page.locator(".programInventory");
  await expect(inventory.locator("article").filter({ hasText: "System requirements" }).locator("b")).toHaveText("150");
  await expect(inventory.locator("article").filter({ hasText: "HLR" }).locator("b")).toHaveText("400");
  await expect(inventory.locator("article").filter({ hasText: "LLR" }).locator("b")).toHaveText("700");
  const traceLinkCount = Number(
    (await inventory.locator("article").filter({ hasText: "Trace links" }).locator("b").textContent())?.replaceAll(",", ""),
  );
  expect(traceLinkCount).toBeGreaterThanOrEqual(1_100);
  await expect(inventory.locator("article").filter({ hasText: "Test procedures" }).locator("b")).toHaveText("515");
  await expect(inventory.locator("article").filter({ hasText: "Test executions" }).locator("b")).toHaveText("520");
  await expect(
    page.getByRole("button", { name: "Open All controlled changes" }),
  ).toBeVisible();

  await openNavigationGroup(page,"SOFTWARE ENGINEERING");
  await page.getByRole("link", { name: "HLR & LLR Requirements" }).click();
  await expect(
    page.getByRole("heading", { name: "Software Requirements" }),
  ).toBeVisible();
  await page.getByLabel("Search requirements").fill("LLR-00000700");
  await expect(page.getByText(/LLR-00000700/).first()).toBeVisible();
  await page.getByRole("link", { name: /Command Center/ }).first().click();
  await openNavigationGroup(page,"VERIFICATION");
  await page.getByRole("link", { name: "Traceability & Outputs" }).click();
  await expect(
    page.getByRole("heading", { name: "Digital Thread" }),
  ).toBeVisible();
  await expect(page.getByText("1,250 requirements")).toBeVisible();
  await page.getByRole("button", { name: /Controlled Documents/ }).click();
  await expect(page.getByText("SYSRD-00000015.00")).toBeVisible();
  await expect(page.getByText("HLRD-00000015.00")).toBeVisible();
  await expect(page.getByText("LLRD-00000015.00")).toBeVisible();
  await page.getByRole("link", { name: /Command Center/ }).first().click();
  await openNavigationGroup(page,"RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Release Campaign" }).click();
  await expect(
    page.getByRole("heading", { name: "FMS 1.6 Release Campaign" }),
  ).toBeVisible();
  await expect(page.getByText("release gates complete")).toBeVisible();
  await page.getByText("Release execution workbench", { exact: true }).click();
  await expect(page.getByRole("heading", { name: "Drive every blocker to evidence" })).toBeVisible();
  expect(await page.locator(".executionPanel.changes article").count()).toBeGreaterThanOrEqual(8);
  await page.locator("details.comparisonDisclosure > summary").click();
  await expect(
    page.getByRole("heading", { name: "Version 1.5 → 1.6" }),
  ).toBeVisible();
  await expect(page.locator(".campaignCard.impacts > .campaignTitle > b")).toHaveText(/\d+ pending/);
  await expect(
    page
      .locator(".comparisonStats article")
      .filter({ hasText: "retired" })
      .locator("b"),
  ).toHaveText("0");
});
