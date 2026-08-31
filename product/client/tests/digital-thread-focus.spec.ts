import { expect, test } from "@playwright/test";
import { readFileSync } from "node:fs";
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from "./auth";

const pngSize = (path: string) => {
  const bytes = readFileSync(path);
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
};

/**
 * "Open complete Digital Thread" navigated to the thread and then focused whichever requirement happened to
 * be first in the loaded page — HLR-000001.00 — so an engineer arriving from SYSR-000011 was reading a
 * different record than the one they left. The action promised context and dropped it at the boundary.
 *
 * The focused artifact is carried in the route as a stable artifact identity, so the check is that the URL
 * says which record it is and the page agrees, and that both survive a reload.
 */
test("opening the Digital Thread from a requirement focuses that requirement and survives reload", async ({
  page,
  request,
}) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("status", { name: /Loading controlled requirements/ }),
  ).toBeHidden();

  await page.getByLabel("Search requirements").fill("SYSR-000011");
  const row = page.getByText(/SYSR-000011\.\d{2}/).first();
  await expect(row).toBeVisible();
  await row.click();

  await page.getByRole("tab", { name: /Trace/ }).click();
  await page.getByRole("button", { name: /Open complete Digital Thread/ }).click();

  const focused = page.locator(".digitalThreadStage header b").first();
  await expect(focused).toHaveText(/^SYSR-000011\./);

  // The identity is in the route, not only in component state, so a shared link is worth sharing.
  const url = page.url();
  expect(url).toContain("/traceability");
  await page.reload({ waitUntil: "load" });
  await expect(page.locator(".digitalThreadStage header b").first()).toHaveText(/^SYSR-000011\./);
  const exactRequirement = page.locator(".completeThreadPath").getByRole("link", { name: /^SYSR-000011\.\d{2}$/ });
  await expect(exactRequirement).toHaveAttribute("href", /\/requirements\/[^?]+\?discipline=system&requirementRevisionId=/);
});

test("a change request opens its stable-ID Digital Thread with exact chain, provenance, and proposal truth", async ({
  page,
  request,
  }, testInfo) => {
  test.setTimeout(180_000);
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  await login(page, "admin", { openProject: false });
  const response = await request.get(`${apiBase}/api/change-requests?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&page=1&pageSize=200`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const listed = await response.json() as { items: { id: string; requirementCount: number }[] };
  const candidate = listed.items.find(item => item.requirementCount > 0);
  expect(candidate, "The showcase must contain a requirement-bearing change request").toBeTruthy();
  const detailResponse = await request.get(`${apiBase}/api/change-requests/${candidate!.id}`);
  expect(detailResponse.ok(), await detailResponse.text()).toBeTruthy();
  const detail = await detailResponse.json() as { displayNumber: string; requirementChanges: { displayNumber: string }[] };
  const contextResponse = await request.get(`${apiBase}/api/build-context?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`);
  expect(contextResponse.ok(), await contextResponse.text()).toBeTruthy();
  const buildContext = await contextResponse.json() as { effectiveBaselineId?: string };
  const traceResponse = await request.get(`${apiBase}/api/change-requests/${candidate!.id}/trace`);
  expect(traceResponse.ok(), await traceResponse.text()).toBeTruthy();
  const trace = await traceResponse.json() as {
    projectId: string; rootArtifactId: string; rootArtifactKind: string;
    nodes: object[]; edges: object[]; state?: object | null;
  };
  const exactRevisionId = "11111111-1111-4111-8111-111111111111";
  const exactArtifactId = "22222222-2222-4222-8222-222222222222";
  const upstreamId = "33333333-3333-4333-8333-333333333333";
  const downstreamId = "44444444-4444-4444-8444-444444444444";
  const historicalId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
  trace.nodes.push(
    { id: exactRevisionId, kind: "RequirementRevision", displayNumber: "HLR-999901.01", title: "Materialized exact requirement", state: null, projectId: showcase.projectId, buildId: null, buildVersion: null, revision: 1, level: "HighLevel", artifactId: exactArtifactId, baselineMembershipIds: [buildContext.effectiveBaselineId] },
    { id: upstreamId, kind: "ChangeRequest", displayNumber: "SRCR-999900.00", title: "Upstream chain node", state: "Approved", projectId: showcase.projectId, buildId: showcase.activeReleaseId, buildVersion: "1.6", revision: 0, level: "System" },
    { id: downstreamId, kind: "ChangeRequest", displayNumber: "HLRCR-999902.00", title: "Downstream chain node", state: "Draft", projectId: showcase.projectId, buildId: showcase.activeReleaseId, buildVersion: "1.6", revision: 0, level: "HighLevel" },
    { id: historicalId, kind: "ChangeRequest", displayNumber: "SRCR-999903.00", title: "Historical multi-hop node", state: "Approved", projectId: showcase.projectId, buildId: showcase.activeReleaseId, buildVersion: "1.6", revision: 0, level: "System" },
  );
  trace.edges.push(
    { fromId: upstreamId, fromKind: "ChangeRequest", toId: candidate!.id, toKind: "ChangeRequest", relation: "Upstream", provenance: [{ kind: "AuthorStated", sourceId: upstreamId }] },
    { fromId: candidate!.id, fromKind: "ChangeRequest", toId: downstreamId, toKind: "ChangeRequest", relation: "Upstream", provenance: [{ kind: "AssessmentDerived", sourceId: downstreamId }] },
    { fromId: upstreamId, fromKind: "ChangeRequest", toId: historicalId, toKind: "ChangeRequest", relation: "Upstream", provenance: [{ kind: "AuthorStated", sourceId: upstreamId }, { kind: "AssessmentDerived", sourceId: historicalId }] },
    { fromId: candidate!.id, fromKind: "ChangeRequest", toId: exactRevisionId, toKind: "RequirementRevision", relation: "OwnsRequirementRevision", provenance: [{ kind: "RequirementRevisionSource", sourceId: candidate!.id }] },
  );
  await page.route(`**/api/change-requests/${candidate!.id}/trace`, route => route.fulfill({ json: trace }));
  await page.route("**/api/traceability/path?*", route => route.fulfill({ json: {
    baselineId: buildContext.effectiveBaselineId,
    focusRevisionId: exactRevisionId,
    baseline: { displayNumber: "SW-999900.00", name: "Materialized browser fixture" },
    nodes: [{ id: exactArtifactId, revisionId: exactRevisionId, displayNumber: "HLR-999901.01", level: "HighLevel", statement: "Materialized exact requirement" }],
    artifact: { id: "55555555-5555-4555-8555-555555555555", revisionId: "66666666-6666-4666-8666-666666666666", displayNumber: "HLRTP-999901.00", title: "Exact verification artifact", artifactKind: "Case", level: "HighLevel", state: "Approved" },
    execution: { id: "77777777-7777-4777-8777-777777777777", outcome: "Pass", executedBy: "test.engineer", executedAt: "2026-08-27T12:00:00Z", determination: "Authoritative result", evidenceReference: "evidence-999901", evidence: [{ id: "88888888-8888-4888-8888-888888888888", originalFileName: "evidence-999901.bin", sha256: "9999999999999999999999999999999999999999999999999999999999999999", size: 12, uploadedAt: "2026-08-27T12:00:00Z" }] },
    build: { id: showcase.activeReleaseId, buildNumber: "1.6", state: "In work", recordedAt: "2026-08-27T12:00:00Z" },
  }}));
  // Model a later effective revision so a missing revisionId would still produce a successful but wrong
  // artifact page. The exact trace link must select the revision displayed by the baseline path.
  const exactArtifactRevisionId = "66666666-6666-4666-8666-666666666666";
  await page.route("**/api/artifacts/test-case/*", async route => {
    const requestedRevisionId = new URL(route.request().url()).searchParams.get("revisionId");
    const exact = requestedRevisionId === exactArtifactRevisionId;
    await route.fulfill({ json: {
      kind: "test-case",
      id: "55555555-5555-4555-8555-555555555555",
      identifier: exact ? "HLRTP-999901.00" : "HLRTP-999901.01",
      title: exact ? "Exact verification artifact" : "Later effective verification artifact",
      state: "Approved",
      subtitle: "HighLevel verification case",
      details: { revisionId: exact ? exactArtifactRevisionId : "99999999-9999-4999-8999-999999999999" },
      related: [],
    }});
  });
  await page.goto(new URL(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/systems/change-requests/${candidate!.id}`, page.url()).toString(), { waitUntil: "load" });
  const digitalThreadLink = page.getByRole("link", { name: "Open Digital Thread →" });
  await expect(digitalThreadLink).toHaveAttribute("href", new RegExp(`/traceability/change-requests/${candidate!.id}$`));
  await digitalThreadLink.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Digital Thread · Change Request" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Connected controlled story" })).toBeVisible();
  await expect(page.getByText(detail.displayNumber, { exact: true }).first()).toBeVisible();
  await expect(page.getByText("Author-stated relationship", { exact: true }).first()).toBeVisible();
  await expect(page.getByText("From downstream assessment", { exact: true }).first()).toBeVisible();
  const graphMap = page.locator(".crGraphBoard");
  await expect(graphMap).toHaveAttribute("data-representable-edge-count", "5");
  await expect(graphMap).toHaveAttribute("data-unrepresentable-edge-count", "0");
  await expect(graphMap).toHaveAttribute("data-rendered-connector-count", "5");
  await expect(graphMap.locator(".crGraphConnector")).toHaveCount(5);
  // Same-layer CR branches must use visible vertical anchors (or an outside rail when a branch skips a card),
  // rather than the old right-to-left path that disappeared behind opaque cards.
  await expect(graphMap.locator('.crGraphConnector[data-route="same-layer-vertical"]')).not.toHaveCount(0);
  await expect(graphMap.locator('.crGraphConnector[data-route="same-layer-rail"]')).not.toHaveCount(0);
  await expect(graphMap.locator('.crGraphConnector[data-route="cross-layer-offset"]')).not.toHaveCount(0);
  const historicalEdge = page.locator(".crGraphEdge").filter({ hasText: "SRCR-999900.00" }).filter({ hasText: "SRCR-999903.00" });
  await expect(historicalEdge).toContainText("Author-stated relationship");
  await expect(historicalEdge).toContainText("From downstream assessment");
  const upstreamFocus = page.getByRole("button", { name: "Focus SRCR-999900.00" });
  await upstreamFocus.focus();
  await page.keyboard.press("Space");
  await expect(upstreamFocus).toHaveAttribute("aria-pressed", "true");
  const upstreamLink = page.locator(".crGraphNode").filter({ hasText: "SRCR-999900.00" }).getByRole("link", { name: /SRCR-999900\.00/ });
  await upstreamLink.focus();
  await expect(upstreamLink).toBeFocused();
  await upstreamLink.press("Enter");
  await expect(page).toHaveURL(new RegExp(`/systems/change-requests/${upstreamId}$`));
  await page.goBack();
  await expect(page.getByRole("heading", { name: "Connected controlled story" })).toBeVisible();
  await expect(page.getByText(exactRevisionId, { exact: true }).first()).toBeVisible();
  await expect(page.getByRole("heading", { name: "Existing baseline-exact requirement path" })).toBeVisible();
  const baselinePath = page.locator(".crBaselinePath");
  await expect(baselinePath.getByRole("link", { name: "HLR-999901.01" })).toHaveAttribute("href", new RegExp(`/requirements/${exactArtifactId}\\?discipline=software&requirementRevisionId=${exactRevisionId}$`));
  await expect(baselinePath.getByRole("link", { name: "HLRTP-999901.00" })).toHaveAttribute("href", new RegExp(`/artifacts/test-case/55555555-5555-4555-8555-555555555555\\?revisionId=${exactArtifactRevisionId}$`));
  await expect(baselinePath.getByRole("link", { name: "Pass", exact: true })).toHaveAttribute("href", /\/artifacts\/test-execution\/77777777-7777-4777-8777-777777777777$/);
  await expect(baselinePath.getByRole("link", { name: "evidence-999901.bin", exact: true })).toHaveAttribute("href", /\/artifacts\/evidence\/88888888-8888-4888-8888-888888888888$/);
  const buildStep = baselinePath.locator(".completeThreadStep").filter({ hasText: "BUILD" });
  await expect(buildStep.locator("[data-exact-artifact-link=unresolved]")).toHaveText("1.6");
  await expect(buildStep.getByRole("link")).toHaveCount(0);
  await expect(page.getByText("Authoritative result", { exact: true })).toBeVisible();
  await expect(page.getByText("evidence-999901.bin", { exact: true })).toBeVisible();
  await expect(page.getByText("1.6", { exact: true }).last()).toBeVisible();
  await expect(page.getByRole("heading", { name: "Proposed requirement changes" })).toBeVisible();
  await expect(page.getByText(detail.requirementChanges[0].displayNumber, { exact: true })).toBeVisible();
  await expect(page.getByText(/not materialized requirement revisions/i)).toBeVisible();
  const captureViewport = async (width: number, height: number, name: string) => {
    await page.setViewportSize({ width, height });
    expect(page.viewportSize()).toMatchObject({ width, height });
    const path = testInfo.outputPath(name);
    await page.screenshot({ path, fullPage: false });
    expect(pngSize(path)).toEqual({ width, height });
    if (width === 900) {
      console.log(await page.evaluate(() => ({
        document: { scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth },
        elements: Array.from(document.querySelectorAll<HTMLElement>("body *")).map(element => ({
          tag: element.tagName, className: element.className, id: element.id,
          left: Math.round(element.getBoundingClientRect().left), right: Math.round(element.getBoundingClientRect().right),
          width: Math.round(element.getBoundingClientRect().width), scrollWidth: element.scrollWidth, clientWidth: element.clientWidth,
        })).filter(item => item.right > document.documentElement.clientWidth || item.left < 0).slice(-20),
      })));
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
    }
  };
  await captureViewport(1440, 900, "cr-thread-1440x900.png");
  await page.screenshot({ path: testInfo.outputPath("cr-thread-full-page.png"), fullPage: true });
  await captureViewport(1280, 900, "cr-thread-1280x900.png");
  await captureViewport(1920, 1080, "cr-thread-1920x1080.png");
  await captureViewport(900, 900, "cr-thread-narrow.png");
  await expect(graphMap.getByText("Scroll horizontally to inspect every connected card and arrow.")).toBeVisible();
  await expect.poll(() => graphMap.evaluate(board => board.scrollWidth > board.clientWidth)).toBe(true);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goBack();
  await expect(page.getByRole("link", { name: "Open Digital Thread →" })).toBeVisible();
  await page.goForward();
  await expect(page.getByRole("heading", { name: "Connected controlled story" })).toBeVisible();
  await page.reload({ waitUntil: "load" });
  await expect(page.getByRole("heading", { name: "Connected controlled story" })).toBeVisible();
  await page.locator(".crBaselinePath").getByRole("link", { name: "HLRTP-999901.00" }).click();
  await expect(page).toHaveURL(new RegExp(`/artifacts/test-case/55555555-5555-4555-8555-555555555555\\?revisionId=${exactArtifactRevisionId}$`));
  await expect(page.getByRole("heading", { name: "HLRTP-999901.00", exact: true })).toBeVisible();
  await expect(page.getByText("Exact verification artifact", { exact: true })).toBeVisible();
});

test("a mismatched CR detail fails closed without showing unrelated Digital Thread content", async ({ page, request }) => {
  test.setTimeout(180_000);
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  await login(page, "admin", { openProject: false });
  const response = await request.get(`${apiBase}/api/change-requests?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&page=1&pageSize=5`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const listed = await response.json() as { items: { id: string }[] };
  const id = listed.items[0].id;
  await page.route(`**/api/change-requests/${id}`, async route => {
    const detail = await (await request.get(`${apiBase}/api/change-requests/${id}`)).json();
    await route.fulfill({ json: { ...detail, projectId: "99999999-9999-4999-8999-999999999999" } });
  });
  await page.goto(new URL(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/traceability/change-requests/${id}`, page.url()).toString(), { waitUntil: "load" });
  await expect(page.getByRole("alert")).toContainText(/unavailable in the selected Project or build/i);
  await expect(page.getByRole("heading", { name: "Connected controlled story" })).toHaveCount(0);
});

test("an invalid baseline path response stays unavailable and exposes retry", async ({ page, request }) => {
  test.setTimeout(180_000);
  const showcase = await showcaseSeed(request);
  await apiLogin(request);
  await login(page, "admin", { openProject: false });
  const listResponse = await request.get(`${apiBase}/api/change-requests?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&page=1&pageSize=200`);
  expect(listResponse.ok(), await listResponse.text()).toBeTruthy();
  const listed = await listResponse.json() as { items: { id: string; requirementCount: number }[] };
  const candidate = listed.items.find(item => item.requirementCount > 0);
  expect(candidate, "The showcase must contain a requirement-bearing change request").toBeTruthy();
  const contextResponse = await request.get(`${apiBase}/api/build-context?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`);
  expect(contextResponse.ok(), await contextResponse.text()).toBeTruthy();
  const context = await contextResponse.json() as { effectiveBaselineId: string };
  const traceResponse = await request.get(`${apiBase}/api/change-requests/${candidate!.id}/trace`);
  expect(traceResponse.ok(), await traceResponse.text()).toBeTruthy();
  const trace = await traceResponse.json() as { nodes: object[]; edges: object[]; projectId: string; rootArtifactId: string; rootArtifactKind: string };
  const revisionId = "11111111-1111-4111-8111-111111111111";
  const artifactId = "22222222-2222-4222-8222-222222222222";
  trace.nodes.push({ id: revisionId, kind: "RequirementRevision", displayNumber: "HLR-999901.01", title: "Materialized exact requirement", state: null, projectId: showcase.projectId, buildId: null, buildVersion: null, revision: 1, level: "HighLevel", artifactId, baselineMembershipIds: [context.effectiveBaselineId] });
  trace.edges.push({ fromId: candidate!.id, fromKind: "ChangeRequest", toId: revisionId, toKind: "RequirementRevision", relation: "OwnsRequirementRevision", provenance: [{ kind: "RequirementRevisionSource", sourceId: candidate!.id }] });
  await page.route(`**/api/change-requests/${candidate!.id}/trace`, route => route.fulfill({ json: trace }));
  await page.route("**/api/traceability/path?*", route => route.fulfill({ status: 200, json: {
    baselineId: "99999999-9999-4999-8999-999999999999", focusRevisionId: revisionId,
    baseline: { displayNumber: "SW-INVALID.00", name: "Mismatched path fixture" }, nodes: [],
  }}));
  await page.goto(new URL(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/traceability/change-requests/${candidate!.id}`, page.url()).toString(), { waitUntil: "load" });
  await expect(page.getByRole("heading", { name: "Connected controlled story" })).toBeVisible();
  await expect(page.getByRole("alert")).toContainText(/did not match the selected revision/i);
  await expect(page.getByRole("alert").getByRole("button", { name: "Retry" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Existing baseline-exact requirement path" })).toHaveCount(0);
});
