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
async function raiseReport(page: Page, projectId: string, releaseId: string, scope: "System" | "Software") {
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
      data: { releaseId, title: `${scope} corrective routing ${Date.now()}` },
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
      data: { projectId, releaseId, title: `${scope} corrective routing ${Date.now()}`, problem: "Raised by hand for corrective routing." },
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
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");

  const workspaces = await (await page.request.get(`${apiBase}/api/workspaces`)).json();
  const fms = workspaces.find((x: { program: { name: string } }) => x.program.name === "Flight Management System Live Program");
  const projectId = fms.projects[0].project.id;
  const releaseId = fms.projects[0].releases.find((x: { isReleased: boolean }) => !x.isReleased).id;

  const system = await raiseReport(page, projectId, releaseId, "System");
  const software = await raiseReport(page, projectId, releaseId, "Software");

  for (const [scope, raised] of [["System", system], ["Software", software]] as const) {
    const corrective = await page.request.get(`${apiBase}/api/problem-reports/${raised.report.id}/corrective-action`)
    expect(corrective.ok(), await corrective.text()).toBeTruthy()
    expect(await corrective.json()).toEqual(expect.objectContaining({
      problemReportId: raised.report.id,
      discipline: scope.toLowerCase(),
    }))
  }
  await page.goto(new URL(root + "/problem-reports", page.url()).toString(), { waitUntil: "load" })
  await expect(page.getByRole("heading", { name: "Page not found" })).toBeVisible()
  await expect(page.getByText(system.report.displayNumber)).toHaveCount(0)
  await expect(page.getByText(software.report.displayNumber)).toHaveCount(0)
});

/**
 * Where a corrective action actually lands.
 *
 * "Record a passing successor execution" is an instruction to run something again and say what happened, so
 * it opens Test Results — with the report named, and the retest offered against the procedure that failed
 * rather than against whatever the build ran last. The report is part of the address, so refreshing or going
 * back returns to the same remediation instead of to a page with nothing selected.
 */
test("a corrective action opens Test Results, names the report, and survives a reload", async ({ page, request }) => {
  test.setTimeout(240_000);
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");

  const workspaces = await (await page.request.get(`${apiBase}/api/workspaces`)).json();
  const fms = workspaces.find((x: { program: { name: string } }) => x.program.name === "Flight Management System Live Program");
  const projectId = fms.projects[0].project.id;
  const releaseId = fms.projects[0].releases.find((x: { isReleased: boolean }) => !x.isReleased).id;

  const raised = await raiseReport(page, projectId, releaseId, "Software");
  const address = new URL(`${root}/software-verification/hlr/results/${raised.report.id}`, page.url()).toString();
  await page.goto(address, { waitUntil: "load" });

  const banner = page.getByRole("status", { name: "Corrective verification action" });
  await expect(banner).toBeVisible({ timeout: 30_000 });
  await expect(banner).toContainText(`CORRECTING ${raised.report.displayNumber}`);
  await expect(banner).toContainText("successor execution");

  await page.reload({ waitUntil: "load" });
  await expect(page.getByRole("heading", { name: "Test Results" })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("status", { name: "Corrective verification action" })).toContainText(raised.report.displayNumber);
  expect(page.url()).toContain(raised.report.id);
});
