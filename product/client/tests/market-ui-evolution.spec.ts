import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup } from "./auth";

async function expectLegibleAndContained(page: import("@playwright/test").Page, root: string) {
  await expect(page.locator(root)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBeTruthy();
  const tiny = await page.locator(root).evaluate((element) =>
    [...element.querySelectorAll("p,span,small,button,input,select,label,a,b,i,code,dt,dd")]
      .filter((candidate) => {
        const box = candidate.getBoundingClientRect();
        return box.width > 0 && box.height > 0 && (candidate.textContent ?? "").trim() && Number.parseFloat(getComputedStyle(candidate).fontSize) < 10;
      })
      .map((candidate) => ({ text: (candidate.textContent ?? "").trim().slice(0, 40), size: getComputedStyle(candidate).fontSize })),
  );
  expect(tiny).toEqual([]);
}

test("market evolution views stay live, legible, and visually contained", async ({ page, request }, testInfo) => {
  test.setTimeout(90_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  const seed = await request.post(`${apiBase}/api/showcase/seed`, { timeout: 45_000 });
  expect(seed.ok(), await seed.text()).toBeTruthy();
  await login(page);
  await page.locator(".program > select:not(.releaseSelector)").selectOption({ label: "Flight Management System Live Program" });

  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expectLegibleAndContained(page, ".reqWorkspace");
  await expect(page.locator(".requirementInspector")).toBeVisible();
  await testInfo.attach("concept-a-precision-workbench", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });

  await openNavigationGroup(page, "VERIFICATION");
  await page.getByRole("link", { name: "Digital Thread" }).click();
  await expect(page.locator(".digitalThreadStage")).toBeVisible();
  await expectLegibleAndContained(page, ".lifecyclePage");
  await testInfo.attach("concept-b-digital-thread", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });

  await openNavigationGroup(page, "RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Lifecycle Decision Room" }).click();
  await page.getByRole("button", { name: "Open exact work →" }).first().click();
  await expect(page.locator(".campaignGateRail")).toBeVisible();
  await expect(page.locator(".evidencePulse")).toBeVisible();
  await expectLegibleAndContained(page, ".campaignPage");
  await testInfo.attach("concept-c-release-command", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });
});
