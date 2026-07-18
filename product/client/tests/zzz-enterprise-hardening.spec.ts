import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup } from "./auth";

test("enterprise control proves content, queries, jobs, concurrency, redlines, and qualification", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  const documentLabel = `FMS controlled interface note ${Date.now()}`;
  await apiLogin(request);
  const seed = await request.post(`${apiBase}/api/showcase/seed`, {
    timeout: 60_000,
  });
  expect(seed.ok(), await seed.text()).toBeTruthy();
  await login(page);
  await page
    .locator(".program > select:not(.releaseSelector)")
    .selectOption({ label: "Flight Management System Live Program" });
  await openNavigationGroup(page,"ADMINISTRATION");
  await page.getByRole("link", { name: /Enterprise Control/ }).click();
  await expect(
    page.getByRole("heading", { name: "Enterprise Control" }),
  ).toBeVisible();
  await expect(page.getByText("Repository assurance posture")).toBeVisible();

  await page.getByRole("button", { name: "Content vault" }).click();
  await expect(
    page.getByRole("heading", { name: "Attachment & evidence vault" }),
  ).toBeVisible();
  await expect(
    page.locator(".artifactPicker select option").first(),
  ).toBeAttached({ timeout: 30_000 });
  await page.getByLabel("Document label").fill(documentLabel);
  await page
    .getByLabel("Description")
    .fill(
      "Exact-revision supporting evidence for the enterprise qualification workflow.",
    );
  await page
    .locator(".vaultUpload input[type=file]")
    .setInputFiles({
      name: "fms-interface.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("FMS controlled interface evidence v1"),
    });
  await page.getByRole("button", { name: "Upload controlled version" }).click();
  await expect(page.getByText(documentLabel)).toBeVisible();
  await page
    .getByRole("button", { name: /Verify integrity|Verified/ })
    .first()
    .click();
  await expect(page.getByText(/Integrity verified/)).toBeVisible();

  await page.getByRole("button", { name: "Query builder" }).click();
  await page.getByLabel("Name").fill(`Release readiness gaps ${Date.now()}`);
  await page
    .locator(".queryBuilder select[name=level]")
    .selectOption("HighLevel");
  await page
    .locator(".queryBuilder select[name=verification]")
    .selectOption("Test");
  await page.getByText("Only requirements with open discussions").click();
  await page.getByText("Share with authorized Program members").click();
  await page
    .getByRole("button", { name: "Save permission-aware view" })
    .click();
  await expect(page.getByText(/Reusable views/)).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Copy stable link" }).last(),
  ).toBeVisible();

  await page.getByRole("button", { name: "Job engine" }).click();
  await page
    .getByRole("button", { name: "Generate controlled export" })
    .click();
  await expect(page.getByText("Durable job accepted")).toBeVisible();
  await expect(page.locator(".jobTable em.completed").first()).toBeVisible({
    timeout: 20_000,
  });
  await expect(
    page.getByRole("link", { name: "Download" }).first(),
  ).toBeVisible();

  await page.getByRole("button", { name: "Concurrency" }).click();
  await page.getByRole("button", { name: "Open editing session" }).click();
  await page.getByRole("button", { name: "Simulate parallel session" }).click();
  await expect(
    page.getByRole("button", { name: "Parallel session detected" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Save draft checkpoint" }).click();
  await expect(page.getByText("MERGE REQUIRED")).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Resolve concurrent changes" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Accept local resolution" }).click();
  await expect(page.getByText(/Three-way conflict resolved/)).toBeVisible();

  await page.getByRole("button", { name: "Redlines" }).click();
  await expect(
    page.getByRole("heading", { name: "Visual change intelligence" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Qualification" }).click();
  await page.getByRole("button", { name: "Run integrity checkpoint" }).click();
  await expect(page.getByText(/Integrity checkpoint/)).toBeVisible();
  await expect(page.getByText("10,000 requirements")).toBeVisible();
});
