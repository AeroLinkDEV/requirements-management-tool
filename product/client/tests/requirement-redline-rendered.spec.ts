import { expect, test } from "@playwright/test";

test("long redlines explain full-field comparison and retain both changed tails", async ({ page }) => {
  await page.goto("/tests/fixtures/requirement-redline.html?case=long");
  const dialog = page.getByRole("dialog", { name: "Revision comparison" });
  const statement = dialog.getByRole("region", { name: "Statement" });
  await expect(statement).toContainText("Full previous and current text");
  const prefix = Array.from({ length: 400 }, () => "requirement").join(" ");
  await expect(statement.locator(".removed")).toHaveText(prefix + " reject");
  await expect(statement.locator(".added")).toHaveText(prefix + " accept");
  await expect(dialog.getByRole("region", { name: "Rationale" })).not.toContainText("Individual word changes");
  await expect(dialog).toContainText("Verification changed: Test → Analysis");
  await page.screenshot({ path: test.info().outputPath("complete-long-redline.png"), fullPage: true });
  await dialog.locator(".verificationDiff").scrollIntoViewIfNeeded();
  await expect(dialog.locator(".verificationDiff")).toBeInViewport();
  await page.screenshot({ path: test.info().outputPath("complete-long-redline-tail.png"), fullPage: true });
  await dialog.getByRole("button", { name: "Close comparison" }).click();
  await expect(dialog).toHaveCount(0);
});

test("short redlines retain word highlighting without a long-field notice", async ({ page }) => {
  await page.goto("/tests/fixtures/requirement-redline.html?case=short");
  const dialog = page.getByRole("dialog", { name: "Revision comparison" });
  await expect(dialog.locator(".added")).toHaveText("safely");
  await expect(dialog).not.toContainText("Full previous and current text");
});

test("incomplete comparison metadata cannot present an unchanged redline", async ({ page }) => {
  await page.goto("/tests/fixtures/requirement-redline.html?case=incomplete");
  await expect(page.getByRole("alert")).toContainText("A complete comparison is unavailable");
  await expect(page.locator(".redlineText")).toHaveCount(0);
});
