import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("enterprise control proves content, queries, jobs, concurrency, redlines, and qualification", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  const documentLabel = `FMS controlled interface note ${Date.now()}`;
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");
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

  // The project this workspace is scoped to, so the assertions below can read authoritative state rather than
  // trusting the banners. Taken from the address because that is where the workspace's identity lives.
  const projectId = new URL(page.url()).pathname.match(/\/projects\/([^/]+)/)?.[1];
  expect(projectId, "the workspace address should carry its project").toBeTruthy();
  const overview = async () => {
    const response = await page.request.get(
      `${apiBase}/api/enterprise-hardening/overview?projectId=${projectId}`,
    );
    expect(response.ok(), await response.text()).toBeTruthy();
    return (await response.json()) as {
      sessions: { id: string; version: number; state: string }[];
      conflicts: { id: string }[];
    };
  };

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

  // The conflict is real before it is resolved, recorded on the server rather than only announced on screen.
  const contested = await overview();
  expect(
    contested.conflicts.length,
    "a merge that requires resolution should be an unresolved conflict record",
  ).toBeGreaterThan(0);
  const contestedSession = contested.sessions.find(x => x.state === "Active");
  expect(contestedSession, "the contested edit session should be active").toBeTruthy();

  await page.getByRole("button", { name: "Accept local resolution" }).click();
  await expect(page.getByText(/Three-way conflict resolved/)).toBeVisible();

  // "Three-way conflict resolved" is a sentence on a screen. What matters is that the conflict record is no
  // longer outstanding and the session advanced — this test previously asserted the sentence and nothing else,
  // so a resolution that changed no stored state would have passed it.
  await expect
    .poll(async () => (await overview()).conflicts.length, { timeout: 30_000 })
    .toBe(0);
  // And it survives a reload, which is the difference between a resolution and a dismissed banner. Resolution
  // records the outcome, resolver and time on the conflict rather than advancing the session, so the session is
  // asserted to still be there with its draft — a resolve that discarded somebody's editing session would be a
  // worse outcome than the conflict it settled.
  await page.reload({ waitUntil: "load" });
  await page.getByRole("button", { name: "Concurrency" }).click();
  await expect(page.getByText("MERGE REQUIRED")).toHaveCount(0);
  const afterReload = await overview();
  expect(afterReload.conflicts.length, "the resolution must outlive the page").toBe(0);
  expect(
    afterReload.sessions.some(x => x.id === contestedSession!.id),
    "resolving a conflict must not discard the editing session it belonged to",
  ).toBe(true);

  await page.getByRole("button", { name: "Redlines" }).click();
  await expect(
    page.getByRole("heading", { name: "Visual change intelligence" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Qualification" }).click();
  await page.getByRole("button", { name: "Run integrity checkpoint" }).click();
  await expect(page.getByText(/Integrity checkpoint/)).toBeVisible();
  await expect(page.getByText("10,000 requirements")).toBeVisible();
});
