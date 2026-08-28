import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("requirements explorer shows truthful access-aware counts", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible();
  await expect(page.getByRole("status", {
    name: /Loading controlled requirements/,
  })).toBeHidden();
  await expect(page.getByText("150 requirements")).toBeVisible();
  await expect(page.locator(".confidence")).toContainText("Live counts · respects your access");
});

test("requirements explorer primary targets are native links in table and document views", async ({
  page,
  request,
}) => {
  test.setTimeout(180_000);
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(page.getByRole("heading", { name: "System Requirements Explorer" })).toBeVisible();
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden();
  await page.getByLabel("Search requirements").fill("SYSR-000150");

  const tableTarget = page.locator('.reqTable article > a.requirementTarget').first();
  await expect(tableTarget).toBeVisible();
  await expect(tableTarget).toHaveAttribute('href', /requirements\/[0-9a-f-]{36}\?discipline=system$/);
  const tableUrl = new URL(await tableTarget.getAttribute('href')!, page.url()).toString();
  const [opened] = await Promise.all([
    page.context().waitForEvent('page', { timeout: 30_000 }),
    tableTarget.click({ button: 'middle' }),
  ]);
  await expect(opened).toHaveURL(tableUrl);
  await opened.close();

  await tableTarget.click();
  await expect(page.getByRole('button', { name: 'Close requirement inspector' })).toBeVisible();
  await page.getByRole('button', { name: 'Close requirement inspector' }).click();
  await page.getByRole('button', { name: 'Document view' }).click();

  const documentTarget = page.locator('.documentMode article > a.requirementTarget').first();
  await expect(documentTarget).toBeVisible();
  await expect(documentTarget).toHaveAttribute('href', /requirements\/[0-9a-f-]{36}\?discipline=system$/);
  await documentTarget.click();
  await expect(page.getByRole('button', { name: 'Close requirement inspector' })).toBeVisible();
});

test("requirements stay read-only while controlled proposals and imports move into Changes", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  const commentText = `Please confirm coverage with @test.engineer before baseline ${Date.now()}.`;
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");
  await openNavigationGroup(page,"SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible();

  const loadingState = page.getByRole("status", {
    name: /Loading controlled requirements/,
  });
  await expect(loadingState).toBeHidden();
  await expect(page.getByText("150 requirements")).toBeVisible();

  await page.getByLabel("Search requirements").fill("SYSR-000150");
  await expect(page.getByText(/SYSR-000150\.\d{2}/)).toBeVisible();
  await page.getByText(/SYSR-000150\.\d{2}/).click();
  await expect(page.getByText("Controlled revision")).toHaveCount(0);
  await page.getByRole("tab", { name: /Discussion/ }).click();
  await page
    .getByPlaceholder(
      "Add an attributable comment. Use @username to mention someone.",
    )
    .fill(commentText);
  await page.getByRole("button", { name: "Add comment" }).click();
  await expect(page.getByText(commentText)).toBeVisible();
  await page.getByRole("button", { name: "Close requirement inspector" }).click();
  await expect(page.getByText("Authoritative view", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Workspace tools", { exact: true })).toHaveCount(0);
  await page.getByLabel("Search requirements").fill("SYSR-000150");
  await page.getByRole("link", { name: /SYSR-000150\.\d{2}/ }).first().click();
  await page.getByRole("tab", { name: "Trace & impact" }).click();
  await expect(page.getByRole("button", { name: "Open complete Digital Thread →" })).toBeVisible();
  await page.getByRole("tab", { name: "Overview" }).click();
  await page.getByRole("button", { name: "Propose controlled change →" }).click();
  await page.getByRole("dialog", { name: "Choose a change request" })
    .getByRole("button", { name: /Start a new Draft change request/ }).click();
  await expect(page.getByRole("heading", { name: "Create System Change Request" })).toBeVisible();
  await expect(page.getByText("Started from Requirements Explorer")).toBeVisible();
  await expect(page.locator('input[value*="SYSR-000150"]').first()).toBeVisible();
  await page.getByRole("button", { name: "Import into Draft SRCR" }).click();
  await page
    .getByLabel("Requirements import file")
    .setInputFiles({
      name: "requirements.csv",
      mimeType: "text/csv",
      buffer: Buffer.from(
        "Identifier,Level,Statement,Rationale,VerificationMethod\nHLR-999999,HighLevel,The FMS software shall retain a governed import preview.,Enterprise onboarding,Test",
      ),
    });
  await page.getByRole("button", { name: "Validate and preview" }).click();
  await expect(
    page.locator(".changeImportIdentity").getByText("1", { exact: true }),
  ).toBeVisible();
  await expect(
    page.locator(".changeImportIdentity").getByText("valid", { exact: true }),
  ).toBeVisible();
  await expect(page.getByText("HLR-999999")).toBeVisible();
});

test("requirements explorer chooser opens an eligible Draft and preserves keyboard escape focus", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  await apiLogin(request);
  const workspaces = await (await request.get(`${apiBase}/api/workspaces`)).json();
  const workspace = workspaces.find((item: { program: { name: string } }) => item.program.name === "Flight Management System Live Program");
  const project = workspace.projects[0];
  const release = project.releases.find((item: { isReleased: boolean }) => !item.isReleased);
  const csrf = await (await request.get(`${apiBase}/api/auth/csrf`)).json();
  const createDraft = async (title: string) => {
    const response = await request.post(`${apiBase}/api/change-request-drafts`, {
      headers: { "X-AeroLink-CSRF": csrf.token },
      data: {
        baseNumber: "",
        projectId: project.project.id,
        targetReleaseId: release.id,
        title,
        problem: "The selected requirement needs a controlled proposal.",
        analysis: "The exact build revision and downstream impact will be assessed.",
        solution: "Add the proposal to this Draft.",
        requirementChanges: [],
        type: "System",
      },
    });
    expect(response.ok(), await response.text()).toBeTruthy();
    return await response.json() as { id: string; title: string };
  };
  const draft = await createDraft(`Explorer chooser Draft ${Date.now()}`);
  const retireDraft = await createDraft(`Explorer retire proposal ${Date.now()}`);
  const requirementWorkspace = await (await request.get(
    `${apiBase}/api/enterprise-requirements/workspace?projectId=${project.project.id}&releaseId=${release.id}&level=System&search=SYSR-000150&page=1&pageSize=25`,
  )).json();
  const requirement = requirementWorkspace.items.find((item: { baseNumber: string }) => item.baseNumber === "SYSR-000150");
  const proposalOptions = await (await request.get(
    `${apiBase}/api/enterprise-requirements/${requirement.id}/propose-options?targetReleaseId=${release.id}`,
  )).json();
  const retireDetail = await (await request.get(`${apiBase}/api/change-requests/${retireDraft.id}`)).json();
  const retireProposal = await request.post(`${apiBase}/api/enterprise-requirements/${requirement.id}/propose`, {
    headers: { "Content-Type": "application/json", "X-AeroLink-CSRF": csrf.token },
    data: {
      targetReleaseId: release.id,
      kind: "Retire",
      existingScrId: retireDraft.id,
      requirementRevisionId: proposalOptions.requirement.revisionId,
      expectedVersion: retireDetail.version,
    },
  });
  expect(retireProposal.ok(), await retireProposal.text()).toBeTruthy();

  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(page.getByRole("heading", { name: "System Requirements Explorer" })).toBeVisible();
  await page.getByLabel("Search requirements").fill("SYSR-000150");
  await page.getByRole("link", { name: /SYSR-000150\.\d{2}/ }).first().click();
  await page.getByRole("tab", { name: "Overview" }).click();

  const trigger = page.getByRole("button", { name: "Propose controlled change →" });
  await trigger.click();
  const chooser = page.getByRole("dialog", { name: "Choose a change request" });
  await expect(chooser).toBeVisible();
  await expect(chooser.getByLabel("Search existing Draft change requests")).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(chooser).toHaveCount(0);
  await expect(trigger).toBeFocused();

  await trigger.click();
  await expect(chooser.getByText(/Exact source:/)).toBeVisible();
  await chooser.getByLabel("Search existing Draft change requests").fill(retireDraft.title);
  const retireRow = chooser.locator("article").filter({ hasText: retireDraft.title });
  await expect(retireRow).toContainText("non-reopenable");
  await expect(retireRow.getByRole("button", { name: /Open existing proposal/ })).toHaveCount(0);
  await expect(retireRow.getByRole("button")).toBeDisabled();
  await chooser.getByLabel("Search existing Draft change requests").fill(draft.title);
  await expect(chooser.getByText(draft.title)).toBeVisible();
  await chooser.getByRole("button", { name: /Add Modify proposal/ }).click();
  await expect(page.getByRole("heading", { name: "Change case" })).toBeVisible({ timeout: 30_000 });
  const draftDetail = await (await request.get(`${apiBase}/api/change-requests/${draft.id}`)).json();
  const proposalId = draftDetail.requirementChanges[0].id as string;
  await expect(page).toHaveURL(new RegExp(`proposalId=${proposalId}`));
  await expect(page.locator(`#requirement-proposal-${proposalId}`)).toBeFocused();
});
