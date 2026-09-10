import { expect, test } from "@playwright/test";

test("operator console surfaces expired work and keeps live claims unreplayable", async ({ page }) => {
  await page.goto("/tests/fixtures/webhook-delivery-operations.html");

  await expect(page.getByRole("heading", { name: "Integration Command Center" })).toBeVisible();
  await expect(page.getByText("1 expired claim(s)")).toBeVisible();

  const expired = page.locator(".deliveryList > div").filter({ hasText: "Expired requirement event" });
  await expect(expired).toContainText("Expired claim");
  await expect(expired.getByRole("button", { name: "Replay" })).toBeVisible();

  const live = page.locator(".deliveryList > div").filter({ hasText: "Live requirement event" });
  await expect(live).toContainText("Delivering");
  await expect(live.getByRole("button", { name: "Replay" })).toHaveCount(0);

  await page.screenshot({ path: test.info().outputPath("expired-delivery-operations.png"), fullPage: true });
  await expired.getByRole("button", { name: "Replay" }).click();
  await expect(expired).toContainText("Pending");
  await expect(expired.getByRole("button", { name: "Replay" })).toHaveCount(0);
});
