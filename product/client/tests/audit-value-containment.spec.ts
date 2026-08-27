import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

/**
 * One long unbroken audit value took the change-request workspace to 2537px wide at a 1265px viewport and
 * pushed the review rail off screen. The audit column was a flex item at the default min-width: auto, so it
 * could not shrink below the intrinsic width of the longest string inside it.
 *
 * The value is written in rather than relying on a seeded record, because the defect is a property of the
 * layout and not of any particular row: a GUID, a hash, a URL, an imported identifier or a legacy JSON blob
 * all reproduce it, and a seeded record that happens to be short today would make this pass for the wrong
 * reason tomorrow.
 */
test("a long unbroken audit value wraps instead of taking the page sideways", async ({ page, request }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1265, height: 900 });
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "Change Requests" }).click();

  await page.locator(".historyRow").first().click();
  await page.getByRole('link', { name: 'Open change request →' }).click();
  const auditRow = page.locator(".auditRow").first();
  await expect(auditRow).toBeVisible();

  const hostile = `evidence://legacy/${"a1b2c3d4e5f6".repeat(12)}#${"0123456789abcdef".repeat(8)}`;
  await page.evaluate(value => {
    const target = document.querySelector<HTMLElement>(".auditRow p");
    if (target) target.textContent = value;
  }, hostile);

  const measured = await page.evaluate(() => ({
    document: document.documentElement.scrollWidth,
    viewport: window.innerWidth,
    row: document.querySelector(".auditRow")?.getBoundingClientRect().width ?? 0,
  }));

  expect(measured.document, `document ${measured.document}px against a ${measured.viewport}px viewport`)
    .toBeLessThanOrEqual(measured.viewport + 1);
  expect(measured.row).toBeLessThanOrEqual(measured.viewport);

  // The complete value stays readable rather than being clipped away, so nothing needs a reveal control.
  await expect(page.locator(".auditRow p").first()).toHaveText(hostile);
});
