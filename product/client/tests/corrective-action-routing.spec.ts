import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import { apiBase, apiLogin, login, selectProgram } from "./auth";

/**
 * "Record a passing successor execution" navigated to the generic System Verification workspace carrying
 * nothing. A software author therefore landed in the wrong discipline, on a tab about change impact, with no
 * report, procedure or result selected — the primary remediation call to action could not guide anybody to
 * the evidence it was asking for.
 *
 * Both disciplines are driven here because the defect was invisible from the system side: a system report
 * happened to arrive somewhere plausible, and only a software one exposed that the destination was hardcoded.
 */

/**
 * Raises a report from a failure the showcase already contains.
 *
 * Recording a new failing run here would work and would be wrong: the showcase's execution count is a
 * documented figure that another journey asserts, and a fixture that changes what other tests find is not an
 * isolated fixture. This reads what is there instead of adding to it.
 */
async function raiseReport(page: Page, projectId: string, scope: "System" | "Software") {
  const procedures = (await (await page.request.get(
    `${apiBase}/api/test-procedures?projectId=${projectId}&scope=${scope}&pageSize=200`)).json()).items;
  const revisionIds = new Set(procedures.map((x: { revisionId: string }) => x.revisionId));

  const executions = await (await page.request.get(`${apiBase}/api/test-executions?projectId=${projectId}`)).json();
  const execution = executions.find((x: { outcome: string; procedureRevisionId: string }) =>
    x.outcome === "Fail" && revisionIds.has(x.procedureRevisionId));

  let report: { id: string; displayNumber: string; version: number };
  let procedureNumber: string | undefined;

  if (execution) {
    // Raised from a failure: the discipline and the procedure both come from the execution.
    const created = await page.request.post(`${apiBase}/api/problem-reports/from-test-execution/${execution.id}`, {
      data: { title: `${scope} corrective routing ${Date.now()}` },
    });
    expect(created.ok(), `raising the ${scope} report: ${created.status()}`).toBe(true);
    report = await created.json();
    const procedure = procedures.find((x: { revisionId: string }) => x.revisionId === execution.procedureRevisionId);
    procedureNumber = (procedure.displayNumber as string).replace(/\.\d{2}$/, "");
  } else {
    // The showcase holds no failed System execution, so this exercises the other resolution path the fix
    // added: a report raised by hand takes its discipline from the requirement it is linked to.
    const workspace = await (await page.request.get(
      `${apiBase}/api/enterprise-requirements/workspace?projectId=${projectId}&level=${scope}&page=1&pageSize=1`)).json();
    const requirement = workspace.items[0];
    expect(requirement, `the showcase must hold a ${scope} requirement`).toBeTruthy();

    const created = await page.request.post(`${apiBase}/api/problem-reports`, {
      data: { projectId, title: `${scope} corrective routing ${Date.now()}`, problem: "Raised by hand for corrective routing." },
    });
    expect(created.ok(), `raising the ${scope} report: ${created.status()}`).toBe(true);
    report = await created.json();

    const linked = await page.request.post(`${apiBase}/api/problem-reports/${report.id}/links`, {
      data: { artifactType: "Requirement", artifactId: requirement.id, relationship: "AffectedRequirement" },
    });
    expect(linked.ok(), `linking the ${scope} requirement: ${linked.status()}`).toBe(true);
  }

  // Advance to the state whose call to action is the one under test.
  for (const [path, body] of [
    ["investigation", { analysis: "Root cause identified in the corrective routing fixture." }],
    ["resolution", { correctiveAction: "Re-run the procedure once the correction is in place." }],
  ] as const) {
    const response = await page.request.post(`${apiBase}/api/problem-reports/${report.id}/${path}`, {
      data: { expectedVersion: report.version, ...body },
    });
    expect(response.ok(), `${path} on the ${scope} report: ${response.status()}`).toBe(true);
    report = await response.json();
  }
  return { report, procedureNumber };
}

test("a corrective action opens the discipline, report and procedure it belongs to", async ({ page, request }) => {
  test.setTimeout(240_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  await login(page);
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");

  const workspaces = await (await page.request.get(`${apiBase}/api/workspaces`)).json();
  const fms = workspaces.find((x: { program: { name: string } }) => x.program.name === "Flight Management System Live Program");
  const projectId = fms.projects[0].project.id;

  const system = await raiseReport(page, projectId, "System");
  const software = await raiseReport(page, projectId, "Software");

  for (const [scope, raised, expectedPath] of [
    ["System", system, "system-verification"],
    ["Software", software, "software-verification"],
  ] as const) {
    await page.goto(new URL(root + "/problem-reports", page.url()).toString(), { waitUntil: "load" });
    await page.getByRole("button", { name: new RegExp(raised.report.displayNumber) }).first().click();

    await page.getByRole("button", { name: /Record a passing successor execution/ }).click();

    // The address names the discipline and the report, so refresh and back return to this remediation.
    await expect(page).toHaveURL(new RegExp(`/${expectedPath}/${raised.report.id}$`), { timeout: 30_000 });

    // The destination says which record it is correcting and which procedure to record against, rather than
    // being a generic tab with nothing selected.
    const banner = page.getByRole("status", { name: "Corrective verification action" });
    await expect(banner).toBeVisible({ timeout: 30_000 });
    await expect(banner).toContainText(raised.report.displayNumber);
    if (raised.procedureNumber) await expect(banner).toContainText(raised.procedureNumber);

    // Back returns to the report, not to a dashboard.
    await page.goBack({ waitUntil: "load" });
    await expect(page).toHaveURL(new RegExp("/problem-reports$"), { timeout: 30_000 });
    expect(scope).toBeTruthy();
  }
});
