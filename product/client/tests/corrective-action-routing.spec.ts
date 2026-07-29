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

/** Records a failing run against a procedure of the given scope and raises the report it belongs to. */
async function raiseReport(page: Page, projectId: string, scope: "System" | "Software") {
  const procedures = await (await page.request.get(
    `${apiBase}/api/test-procedures?projectId=${projectId}&scope=${scope}`)).json();
  const procedure = procedures.find((x: { state: string }) => x.state === "Approved") ?? procedures[0];
  expect(procedure, `the showcase must hold a ${scope} procedure`).toBeTruthy();

  const failure = await page.request.post(`${apiBase}/api/test-executions`, {
    data: {
      projectId,
      procedureRevisionId: procedure.revisionId,
      outcome: "Fail",
      configuration: "Corrective routing fixture",
      determination: "Observed output did not satisfy the expected result.",
      evidenceReference: `evidence/corrective/${scope}-${Date.now()}.json`,
      executedAt: new Date().toISOString(),
    },
  });
  expect(failure.ok(), `recording the ${scope} failure: ${failure.status()}`).toBe(true);
  const execution = await failure.json();

  const created = await page.request.post(`${apiBase}/api/problem-reports/from-test-execution/${execution.id}`, {
    data: { title: `${scope} corrective routing ${Date.now()}` },
  });
  expect(created.ok(), `raising the ${scope} report: ${created.status()}`).toBe(true);
  let report = await created.json();

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
  // The banner names the procedure by its controlled base number; the list returns it revision-suffixed.
  return { report, procedureNumber: (procedure.displayNumber as string).replace(/\.\d{2}$/, "") };
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
    await expect(banner).toContainText(raised.procedureNumber);

    // Back returns to the report, not to a dashboard.
    await page.goBack({ waitUntil: "load" });
    await expect(page).toHaveURL(new RegExp("/problem-reports$"), { timeout: 30_000 });
    expect(scope).toBeTruthy();
  }
});
