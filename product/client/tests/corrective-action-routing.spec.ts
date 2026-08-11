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
      data: { expectedVersion: report.version, artifactType: "Requirement", artifactId: requirement.id, relationship: "AffectedRequirement" },
    });
    expect(linked.ok(), `linking the ${scope} requirement: ${linked.status()}`).toBe(true);
    report.version = (await linked.json()).version;
  }

  // Advance to the state whose call to action is the one under test.
  for (const [path, body] of [
    ["ready-for-sccb", {}],
    ["sccb/open", {}],
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
  const historicalReleaseId = fms.projects[0].releases.find((x: { isReleased: boolean }) => x.isReleased).id;

  const system = await raiseReport(page, projectId, releaseId, "System");
  const software = await raiseReport(page, projectId, releaseId, "Software");

  for (const [scope, raised] of [["System", system], ["Software", software]] as const) {
    const corrective = await page.request.get(`${apiBase}/api/problem-reports/${raised.report.id}/corrective-action`)
    expect(corrective.ok(), await corrective.text()).toBeTruthy()
    const target = await corrective.json()
    expect(target.problemReportId).toBe(raised.report.id)
    if (raised.procedureNumber) expect(target).toEqual(expect.objectContaining({ available: true, discipline: scope.toLowerCase() }))
    else expect(target).toEqual(expect.objectContaining({ available: false, verificationCode: 'pr_verification_scope_unknown' }))
  }
  await page.goto(new URL(root + "/problem-reports", page.url()).toString(), { waitUntil: "load" })
  await expect(page.getByRole("heading", { name: "Problem Reports" })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole("button", { name: new RegExp(system.report.displayNumber.replace('.', '\\.')) })).toBeVisible()
  await expect(page.getByRole("button", { name: new RegExp(software.report.displayNumber.replace('.', '\\.')) })).toBeVisible()
  await page.reload({ waitUntil: "load" })
  await expect(page.getByRole("heading", { name: "Problem Reports" })).toBeVisible()
  const historicalRoot = root.replace(/\/releases\/[^/]+$/, `/releases/${historicalReleaseId}`)
  await page.goto(new URL(historicalRoot + "/problem-reports", page.url()).toString(), { waitUntil: "load" })
  await expect(page.getByText("Released build · read-only")).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole("button", { name: "+ Record problem" })).toHaveCount(0)
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

  const corrective = await (await page.request.get(`${apiBase}/api/problem-reports/${raised.report.id}/corrective-action`)).json()
  const setDiscipline = corrective.procedureNumber.startsWith('HLRTP-') ? 'HighLevelSoftware'
    : corrective.procedureNumber.startsWith('LLRTP-') ? 'LowLevelSoftware' : 'System'
  const included = await page.request.post(`${apiBase}/api/releases/${releaseId}/test-sets/${setDiscipline}/procedures`, {
    data: { procedureRevisionIds: [corrective.procedureRevisionId], reason: 'CorrectiveAction', note: `Closure retest for ${raised.report.displayNumber}` },
  })
  expect(included.ok(), await included.text()).toBeTruthy()
  await page.reload({ waitUntil: 'load' })
  await page.getByRole('button', { name: /Record successor execution/ }).click()
  const record = page.getByRole('dialog', { name: /Record a result for/ })
  await expect(record).toBeVisible()
  await record.getByLabel('Configuration under test').fill('FMS corrective rig')
  await record.getByLabel('Determination', { exact: true }).fill('The corrected behavior satisfies the effective controlled procedure.')
  await record.getByLabel('Evidence reference').fill('controlled://browser/pr-corrective-successor')
  await record.getByRole('button', { name: 'Record determination' }).click()
  await expect(page.getByText(/selected as PR closure evidence/)).toBeVisible({ timeout: 30_000 })

  const detail = await (await page.request.get(`${apiBase}/api/problem-reports/${raised.report.id}`)).json()
  expect(detail.state).toBe('AwaitingSqaClosure')
  expect(detail.testEvidence).toHaveLength(1)
  expect(detail.testEvidence[0].artifactId).toBe(detail.resolutionVerificationExecutionId)

  // A controlled correction cannot inherit the Pass selected above. The page returns to Verifying, names
  // why, removes the SQA action, and retains the first selection only as history.
  const reportAddress = new URL(`${root}/problem-reports/${raised.report.id}`, page.url()).toString()
  await page.goto(reportAddress, { waitUntil: 'load' })
  await expect(page.locator('.prState')).toHaveText('Awaiting SQA Closure', { timeout: 30_000 })
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await editor.getByLabel('Corrective-action narrative').fill('The corrected scheduler and guard are both required before closure.')
  await editor.getByLabel('Root cause', { exact: true }).fill('The scheduler and missing guard combined to produce the failure.')
  await editor.getByRole('button', { name: 'Check in' }).click()
  await expect(editor).toHaveCount(0, { timeout: 30_000 })
  await expect(page.locator('.prState')).toHaveText('Verifying')
  await expect(page.getByRole('status')).toContainText('Closure verification invalidated')
  await expect(page.getByRole('status')).toContainText('Record a new passing successor result')
  await expect(page.getByRole('button', { name: /Approve independent SQA closure/ })).toHaveCount(0)
  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.locator('.prTimeline').getByText('Closure Verification Invalidated By Change')).toBeVisible()

  // Transfer the in-work record to the seeded test engineer while it already requires verification. The
  // new owner records the second exact candidate; an independent seeded SQA account then closes that one.
  let changed = await (await page.request.get(`${apiBase}/api/problem-reports/${raised.report.id}`)).json()
  const reassigned = await page.request.post(`${apiBase}/api/problem-reports/${raised.report.id}/owner`, {
    data: { expectedVersion: changed.version, responsibleEngineerId: 'test.engineer' },
  })
  expect(reassigned.ok(), await reassigned.text()).toBeTruthy()

  await page.context().clearCookies()
  await login(page, 'test.engineer', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(reportAddress, { waitUntil: 'load' })
  await page.getByRole('button', { name: 'Raise release blocker' }).click()
  await expect(page.getByText('Requires a separate independent release-waiver decision.')).toBeVisible()
  await expect(page.getByText('Approve independent release waiver')).toHaveCount(0)
  await page.getByRole('button', { name: /Select closure-supporting test result/ }).click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: /Record successor execution/ }).click()
  const secondRecord = page.getByRole('dialog', { name: /Record a result for/ })
  await secondRecord.getByLabel('Configuration under test').fill('FMS corrected scheduler rig')
  await secondRecord.getByLabel('Determination', { exact: true }).fill('The revised closure candidate satisfies the effective procedure.')
  await secondRecord.getByLabel('Evidence reference').fill('controlled://browser/pr-reverified-successor')
  await secondRecord.getByRole('button', { name: 'Record determination' }).click()
  await expect(page.getByText(/selected as PR closure evidence/)).toBeVisible({ timeout: 30_000 })

  for (const userName of ['systems.reviewer', 'cm.fms', 'program.manager']) {
    await page.context().clearCookies()
    await login(page, userName, { openProject: false })
    await selectProgram(page, 'Flight Management System Live Program')
    await page.goto(reportAddress, { waitUntil: 'load' })
    await expect(page.locator('.prState')).toHaveText('Awaiting SQA Closure', { timeout: 30_000 })
    await expect(page.getByRole('button', { name: /Approve independent SQA closure/ })).toHaveCount(0)
    await expect(page.getByText('Approve independent release waiver')).toHaveCount(userName === 'systems.reviewer' ? 0 : 1)
  }

  await page.context().clearCookies()
  await login(page, 'quality.analyst', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(reportAddress, { waitUntil: 'load' })
  await expect(page.locator('.prState')).toHaveText('Awaiting SQA Closure', { timeout: 30_000 })
  await page.getByRole('button', { name: /Approve independent SQA closure/ }).click()
  await expect(page.locator('.prState')).toHaveText('Closed')
  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.locator('.prTimeline').getByText('Closure Verification Invalidated By Change')).toBeVisible()
  await expect(page.locator('.prTimeline').getByText('Closure Approved')).toBeVisible()
  const packageCard = page.locator('.prClosurePackages article').filter({ hasText: 'Closure revision 0' })
  await expect(packageCard).toContainText('Marcus Hale')
  await expect(packageCard).toContainText('Software Quality Analyst')
  const packageLink = packageCard.getByRole('link', { name: 'Open frozen closure package' })
  const packageHref = await packageLink.getAttribute('href')
  expect(packageHref).toContain(`candidateId=`)
  const frozenResponse = await page.request.get(packageHref!)
  expect(frozenResponse.ok(), await frozenResponse.text()).toBeTruthy()
  const frozen = await frozenResponse.json()
  expect(frozen.snapshot.packageProvenance).toBe('FrozenAtApproval')
  expect(frozen.snapshot.closurePackageHash).toMatch(/^[0-9a-f]{64}$/)
  expect(frozen.snapshot.approvalAuthority).toBe('SoftwareQualityAnalyst')
  expect(frozen.package.closure.approvedBy).toBe('quality.analyst')
  expect(frozen.package.closure.authority).toBe('SoftwareQualityAnalyst')

  // Reopening begins a new controlled revision without erasing or relabeling the prior package.
  await page.context().clearCookies()
  await login(page, 'test.engineer', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const closedDetail = await (await page.request.get(`${apiBase}/api/problem-reports/${raised.report.id}`)).json()
  const reopened = await page.request.post(`${apiBase}/api/problem-reports/${raised.report.id}/reopen`, {
    data: { expectedVersion: closedDetail.version, rationale: 'A follow-on field report requires a new closure cycle.' },
  })
  expect(reopened.ok(), await reopened.text()).toBeTruthy()
  await page.goto(reportAddress, { waitUntil: 'load' })
  await expect(page.locator('.prState')).toHaveText('Open', { timeout: 30_000 })
  await page.getByRole('button', { name: /History/ }).click()
  await expect(page.locator('.prClosurePackages')).toContainText('Closure revision 0 · prior closure cycle')
  await expect(page.locator('.prClosurePackages')).toContainText(frozen.snapshot.closurePackageHash.slice(0, 12))
});
