import { expect, test, type Page } from "@playwright/test";
import { login, selectProgram } from "./auth";

/**
 * The identity card styles its own plain elements and leaves components alone.
 *
 * `CategoryTile` brings its own flex layout. The card used to reach it with `.prIdentity span`, which
 * outranks the tile's own `.catTile` rule, so the tile stopped being a flex container and its
 * two-digit code stretched across the whole card as a grey bar that read like a broken progress bar
 * (#1082).
 *
 * This is the third time a component's rule has lost to a page stylesheet's blanket descendant
 * selector here — `.richFileInput` lost its width, `.richEditor` lost its font — so the assertion is
 * on the mechanism, not on the pixels: the tile must still be laying itself out, and its code must
 * still be sized by its content rather than by the container.
 */

const identity = (page: Page) => page.locator(".prIdentity");

test("the category code is a badge, not a bar across the card", async ({ page }) => {
  test.setTimeout(240_000);
  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: "load" });

  // Any classified report will do; the queue's first row is one in the seeded showcase data.
  const firstRow = page.locator(".prList > button").first();
  await expect(firstRow).toBeVisible({ timeout: 30_000 });
  await firstRow.click();

  const tile = identity(page).locator(".catTile").first();
  await expect(tile).toBeVisible({ timeout: 30_000 });

  const measured = await tile.evaluate((element) => {
    const code = element.querySelector(":scope > b");
    return {
      tileDisplay: getComputedStyle(element).display,
      tileWidth: Math.round(element.getBoundingClientRect().width),
      codeWidth: code ? Math.round(code.getBoundingClientRect().width) : -1,
    };
  });

  // The tile lays itself out. If a container rule blockifies it again, this is what changes first.
  expect(measured.tileDisplay).toBe("flex");
  // Sized by its content: a two-digit code, its padding and a 30px floor — nowhere near the card.
  expect(measured.codeWidth).toBeGreaterThan(0);
  expect(measured.codeWidth).toBeLessThan(80);
  expect(measured.codeWidth).toBeLessThan(measured.tileWidth / 2);
});
