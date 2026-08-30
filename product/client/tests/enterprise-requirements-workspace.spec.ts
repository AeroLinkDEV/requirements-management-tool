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

test("requirements explorer keeps every primary filter usable at desktop review widths", async ({
  page,
  request,
}, testInfo) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(page.getByRole("heading", { name: "System Requirements Explorer" })).toBeVisible();
  await expect(page.getByRole("status", { name: /Loading controlled requirements/ })).toBeHidden();

  const toolbar = page.locator(".reqCommand");
  const search = page.getByLabel("Search requirements");
  const level = page.getByLabel("Level filter");
  const coverage = page.getByLabel("Coverage state filter");
  const tag = page.getByLabel("Tag filter");
  const namedControls = [
    search,
    level,
    page.getByLabel("Verification filter"),
    coverage,
    tag,
    page.getByRole("button", { name: "Advanced" }),
    page.getByRole("button", { name: "Clear" }),
    page.getByLabel("Rows per page"),
    page.getByRole("button", { name: "Table view" }),
    page.getByRole("button", { name: "Document view" }),
  ];

  for (const width of [1440, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(toolbar).toBeVisible();
    for (const control of namedControls) await expect(control).toBeVisible();
    await expect(toolbar.locator("kbd")).toHaveText(/\d[\d,]* found/);

    const geometry = await toolbar.evaluate((element) => {
      const rect = element.getBoundingClientRect();
      const children = Array.from(element.children)
        .filter((child) => getComputedStyle(child).display !== "none")
        .map((child) => {
          const box = child.getBoundingClientRect();
          return { left: box.left, right: box.right, top: box.top, bottom: box.bottom };
        });
      return {
        toolbar: { left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom },
        children,
        viewportWidth: document.documentElement.clientWidth,
        documentWidth: document.documentElement.scrollWidth,
      };
    });
    expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.viewportWidth);
    for (const child of geometry.children) {
      expect(child.left).toBeGreaterThanOrEqual(geometry.toolbar.left - 1);
      expect(child.right).toBeLessThanOrEqual(geometry.toolbar.right + 1);
      expect(child.top).toBeGreaterThanOrEqual(geometry.toolbar.top - 1);
      expect(child.bottom).toBeLessThanOrEqual(geometry.toolbar.bottom + 1);
    }

    const searchBox = await search.boundingBox();
    const levelBox = await level.boundingBox();
    expect(searchBox).not.toBeNull();
    expect(levelBox).not.toBeNull();
    expect(searchBox!.width).toBeGreaterThanOrEqual(240);
    expect(searchBox!.y + searchBox!.height).toBeLessThanOrEqual(levelBox!.y + 1);

    await testInfo.attach(`requirements-toolbar-${width}`, {
      body: await toolbar.screenshot(),
      contentType: "image/png",
    });
  }

  await coverage.selectOption("covered");
  await expect(coverage).toHaveValue("covered");
  await tag.fill("navigation");
  await expect(tag).toHaveValue("navigation");
  await page.getByRole("button", { name: "Clear", exact: true }).click();
  await expect(coverage).toHaveValue("");
  await expect(tag).toHaveValue("");

  // At the 1280px overlay breakpoint the inspector is fixed. A multi-row sticky toolbar must not follow
  // the document over its tabs and intercept every click after the user scrolls the record workspace.
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.locator(".reqTable article > a.requirementTarget").first().click();
  await expect(page.locator(".requirementInspector")).toBeVisible();
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  const discussionTab = page.getByRole("tab", { name: /Discussion/ });
  await discussionTab.click();
  await expect(discussionTab).toHaveAttribute("aria-selected", "true");
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

  const contextRoot = new URL(page.url()).pathname.replace(/\/systems\/requirements$/, "");
  const tableTarget = page.locator('.reqTable article > a.requirementTarget').first();
  await expect(tableTarget).toBeVisible();
  const tableHref = await tableTarget.getAttribute('href');
  expect(tableHref).toBeTruthy();
  const tableUrlObject = new URL(tableHref!, page.url());
  expect(tableUrlObject.pathname).toMatch(new RegExp(`^${contextRoot}/requirements/[0-9a-f-]{36}$`));
  expect(tableUrlObject.search).toBe('?discipline=system');
  const tableUrl = tableUrlObject.toString();
  const [opened] = await Promise.all([
    page.context().waitForEvent('page', { timeout: 30_000 }),
    tableTarget.click({ button: 'middle' }),
  ]);
  await expect(opened).toHaveURL(tableUrl);
  await opened.close();

  await tableTarget.focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(tableUrl);
  await expect(page.getByRole('button', { name: 'Close requirement inspector' })).toBeVisible();
  await page.getByRole('button', { name: 'Close requirement inspector' }).click();

  await tableTarget.click();
  await expect(page.getByRole('button', { name: 'Close requirement inspector' })).toBeVisible();
  await page.getByRole('button', { name: 'Close requirement inspector' }).click();
  await page.getByRole('button', { name: 'Document view' }).click();

  const documentTarget = page.locator('.documentMode article > a.requirementTarget').first();
  await expect(documentTarget).toBeVisible();
  const documentHref = await documentTarget.getAttribute('href');
  expect(documentHref).toBeTruthy();
  const documentUrlObject = new URL(documentHref!, page.url());
  expect(documentUrlObject.pathname).toMatch(new RegExp(`^${contextRoot}/requirements/[0-9a-f-]{36}$`));
  expect(documentUrlObject.search).toBe('?discipline=system');
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
