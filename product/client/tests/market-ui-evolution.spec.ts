import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

async function expectLegibleAndContained(page: import("@playwright/test").Page, root: string) {
  await expect(page.locator(root)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBeTruthy();
  const tiny = await page.locator(root).evaluate((element) =>
    [...element.querySelectorAll("p,span,small,button,input,select,label,a,b,i,code,dt,dd")]
      .filter((candidate) => {
        const box = candidate.getBoundingClientRect();
        // The Digital Thread canvas is a scaled scene (#880 §10.1, DEC-117): its computed font-size is
        // authored in scene units, not the size the reader sees, so a CSS-pixel floor measures the wrong
        // number there. Legibility of the canvas is asserted at default zoom by the Digital Thread specs.
        if (candidate.closest(".dtCanvasScene")) return false;
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
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");

  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expectLegibleAndContained(page, ".reqWorkspace");
  await expect(page.locator(".requirementInspector")).toBeVisible();
  await testInfo.attach("concept-a-precision-workbench", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });

  // #880 §4.3 moved the Digital Thread into RELEASE, and §4.2 made the canvas the page.
  await openNavigationGroup(page, "RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Digital Thread" }).click();
  await expect(page.locator(".dtPage .dtnRoot")).toBeVisible();
  await expectLegibleAndContained(page, ".dtPage");
  await testInfo.attach("concept-b-digital-thread", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });

  await openNavigationGroup(page, "RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Lifecycle Decision Room" }).click();
  await page.getByRole("button", { name: "Open exact work →" }).first().click();
  await expect(page.locator(".campaignGateRail")).toBeVisible();
  await expect(page.locator(".evidencePulse")).toBeVisible();
  await expectLegibleAndContained(page, ".campaignPage");
  await testInfo.attach("concept-c-release-command", { body: await page.screenshot({ fullPage: true }), contentType: "image/png" });
});
