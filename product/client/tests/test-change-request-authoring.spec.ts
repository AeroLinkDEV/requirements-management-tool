import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

/**
 * A disposable Program with one approved System change request and one Problem Report, so the authoring
 * journey owns its state instead of depending on the shared showcase dataset.
 */
async function seedWorkspace(request: import('@playwright/test').APIRequestContext, suffix: string, released = false, inheritReport = false) {
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `TCR Authoring ${suffix}`,
    programCode: `TCR${suffix}`,
    projectName: 'TCR Authoring Project',
    softwareProduct: 'TCR Authoring Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: released,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const impacts = JSON.stringify({
    trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
    baseline: 'Not Affected', collaboration: 'Not Affected',
  })
  if (released) return { workspace, sourceChangeId: undefined, sourceNumber: '', report: undefined }

  const reportResponse = await request.post(`${apiBase}/api/problem-reports`, {
    data: {
      category: 'CodeFunctional', projectId: workspace.project.id,
      releaseId: workspace.release.id,
      title: `TCR driving report ${suffix}`,
      problem: 'The observed behavior disagrees with the approved plan.',
    },
  })
  expect(reportResponse.ok(), await reportResponse.text()).toBeTruthy()
  const report = await reportResponse.json()

  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    problemReportIds: inheritReport ? [report.id] : [],
    title: `TCR authoring source ${suffix}`,
    problem: 'The new behavior has no test coverage.',
    analysis: 'No procedure exercises it today.',
    solution: 'Write one from this package.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: await firstSectionId(request, workspace.project.id),
      statement: `The ${suffix} product shall expose a TCR-authoring verification target.`,
      rationale: 'Capability qualification.',
      verificationMethod: 'Test',
      impactDispositionJson: impacts,
    }],
  } })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const draft = await draftResponse.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, {
    data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] },
  })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, {
    data: { password: 'AeroLink!2026', meaning: 'Approved for TCR authoring journey verification.' },
  })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const changeRequest = await request.get(`${apiBase}/api/change-requests/${draft.id}`)
  const detail = await changeRequest.json()
  return { workspace, sourceChangeId: draft.id, sourceNumber: detail.displayNumber, report }
}

test('upstream PR context is accepted explicitly and saved atomically with the authored test package', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const seeded = await seedWorkspace(request, `${Date.now()}`.slice(-7), false, true)
  const { workspace, report, sourceChangeId, sourceNumber } = seeded
  const register = await request.get(`${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`)
  expect(register.ok()).toBeTruthy()
  const automatic = (await register.json()).items.find((item: { changeRequestId: string }) => item.changeRequestId === sourceChangeId)
  expect(automatic).toBeTruthy()
  expect(automatic.problemReports).toEqual([])
  expect(automatic.inheritedProblemReportSources).toContainEqual({ id: sourceChangeId, kind: 'ChangeRequest', displayNumber: sourceNumber })
  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  await page.goto(`${root}/system-verification/change-requests/new`)
  const editor = page.locator('[data-tcr-editor]')
  await editor.locator('.tcrSourceChoices').getByRole('checkbox', { name: new RegExp(sourceNumber.replace('.', '\\.')) }).check()
  const inherited = page.getByRole('group', { name: 'Inherited Problem Report context' })
  const candidate = inherited.getByRole('checkbox', { name: new RegExp(report.displayNumber.replace('.', '\\.')) })
  await expect(candidate).not.toBeChecked()
  await expect(inherited.getByText(`Inherited from ${sourceNumber}.`)).toBeVisible()
  // Leaving before save creates neither a package nor a direct link.
  await candidate.check()
  await page.reload()
  const unchanged = await request.get(`${apiBase}/api/problem-reports/linked/TestChangeRequest/${automatic.id}`)
  expect(await unchanged.json()).toEqual([])
  await editor.locator('.tcrSourceChoices').getByRole('checkbox', { name: new RegExp(sourceNumber.replace('.', '\\.')) }).check()
  await expect(candidate).not.toBeChecked()
  await candidate.check()
  await editor.getByLabel('Title', { exact: true }).fill('Explicitly accepted upstream anomaly')
  await editor.getByLabel('Problem', { exact: true }).fill('The upstream anomaly also affects the planned verification.')
  await editor.getByLabel('Analysis', { exact: true }).fill('The engineer explicitly evaluated this source context.')
  await editor.getByLabel('Solution', { exact: true }).fill('Plan the relevant verification under this package.')
  const savedPromise = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/test-change-requests'))
  await editor.getByRole('button', { name: 'Raise SYSTPCR', exact: true }).click()
  const saved = await savedPromise
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const created = await saved.json()
  const direct = await request.get(`${apiBase}/api/problem-reports/linked/TestChangeRequest/${created.id}`)
  expect((await direct.json()).map((item: { id: string }) => item.id)).toEqual([report.id])
  await page.reload()
  const reopened = await request.get(`${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`)
  const packageRow = (await reopened.json()).items.find((item: { id: string }) => item.id === created.id)
  expect(packageRow.problemReports.map((item: { id: string }) => item.id)).toEqual([report.id])
  expect(packageRow.coveredChangeRequests.map((item: { id: string }) => item.id)).toEqual([sourceChangeId])
})

test('multiple exact CR sources share inherited reports without losing independent direct selections on check-in', async ({ page, request }, testInfo) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const seeded = await seedWorkspace(request, `${Date.now()}`.slice(-7), false, true)
  const { workspace, report, sourceChangeId, sourceNumber } = seeded
  const reports = []
  for (const title of ['Subset not accepted', 'Independent manual selection']) {
    const response = await request.post(`${apiBase}/api/problem-reports`, { data: {
      category: 'CodeFunctional', projectId: workspace.project.id, releaseId: workspace.release.id, title, problem: 'Observed anomaly.'
    } })
    expect(response.ok(), await response.text()).toBeTruthy()
    reports.push(await response.json())
  }
  const secondResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id, targetReleaseId: workspace.release.id, type: 'System', title: 'Second upstream source',
    problem: 'P', analysis: 'A', solution: 'S', problemReportIds: [report.id, reports[0].id],
    requirementChanges: [{ level: 'System', kind: 'Introduce', targetSectionId: await firstSectionId(request, workspace.project.id),
      statement: 'The system shall retain explicit downstream context.', rationale: 'Controlled trace.', verificationMethod: 'Test',
      impactDispositionJson: JSON.stringify({ trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected', baseline: 'Not Affected', collaboration: 'Not Affected' }) }]
  } })
  expect(secondResponse.ok(), await secondResponse.text()).toBeTruthy()
  const second = await secondResponse.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${second.id}/submit`, { data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${second.id}/approve`, { data: { password: 'AeroLink!2026', meaning: 'Approved exact source for inherited context qualification.' } })
  expect(approved.ok(), await approved.text()).toBeTruthy()
  const secondDetail = await (await request.get(`${apiBase}/api/change-requests/${second.id}`)).json()
  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  await page.goto(`${root}/software/change-requests/new?level=HLR`)
  const picker = page.getByLabel('Upstream change requests', { exact: true })
  for (const number of [sourceNumber, secondDetail.displayNumber]) {
    await picker.getByLabel('Find a direct parent').fill(number.split('.')[0])
    await picker.getByRole('button', { name: new RegExp(number.replace('.', '\\.')) }).click()
    await picker.getByLabel(`Rationale for ${number}`, { exact: true }).fill('This exact approved decision drives the downstream scope.')
  }
  const inherited = page.getByRole('group', { name: 'Inherited Problem Report context' })
  const shared = inherited.getByRole('checkbox', { name: new RegExp(report.displayNumber.replace('.', '\\.')) })
  await expect(shared).toHaveCount(1)
  await shared.check()
  await expect(inherited.getByRole('checkbox', { name: new RegExp(reports[0].displayNumber.replace('.', '\\.')) })).not.toBeChecked()
  const manual = page.locator('.problemReportPicker').filter({ has: page.getByRole('searchbox', { name: 'Find controlled PR' }) })
  await manual.getByRole('searchbox').fill(reports[1].title)
  await manual.getByRole('checkbox', { name: new RegExp(reports[1].displayNumber.replace('.', '\\.')) }).check()
  await page.getByLabel('Title', { exact: true }).fill('Independent downstream PR acceptance')
  await page.screenshot({ path: testInfo.outputPath('upstream-pr-subset-and-manual-selection.png'), fullPage: true })
  const savedPromise = page.waitForResponse(response => response.url().endsWith('/api/change-request-drafts') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Save HLRCR Draft', exact: true }).click()
  const saved = await savedPromise
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const created = await saved.json()
  const acceptedIds = [report.id, reports[1].id].sort()
  const directIds = async () => (await (await request.get(`${apiBase}/api/problem-reports/linked/ChangeRequest/${created.id}`)).json()).map((item: { id: string }) => item.id).sort()
  expect(await directIds()).toEqual(acceptedIds)
  await page.getByRole('button', { name: 'Check out & edit', exact: true }).click()
  await picker.locator('.upstreamDraftRow').filter({ hasText: sourceNumber }).getByRole('button', { name: 'Remove', exact: true }).click()
  await inherited.getByRole('button', { name: 'Refresh inherited context' }).click()
  await expect(shared).toBeChecked()
  await page.getByRole('button', { name: 'Save & check in', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Check out & edit', exact: true })).toBeVisible()
  await page.reload()
  expect(await directIds()).toEqual(acceptedIds)
  const reopened = await (await request.get(`${apiBase}/api/change-requests/${created.id}`)).json()
  expect(reopened.upstream.map((item: { upstreamChangeRequestId: string }) => item.upstreamChangeRequestId)).toEqual([second.id])
  expect(reopened.upstream.some((item: { upstreamChangeRequestId: string }) => item.upstreamChangeRequestId === sourceChangeId)).toBeFalsy()
})

test('an engineer raises a System test change request with its case from the Change Requests page', async ({ page, request }) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const seeded = await seedWorkspace(request, suffix)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, `TCR Authoring ${suffix}`)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Change Requests' })).toBeVisible({ timeout: 30_000 })

  // A page, not a pop-up: raising a package is the same act as raising a change request, and its counterpart
  // has always been a page.
  await page.getByRole('button', { name: '+ New System Test Procedure Change Request' }).click()
  const editor = page.locator('[data-tcr-editor]')
  await expect(page.getByRole('heading', { name: 'Create System Test Procedure Change Request', level: 1 })).toBeVisible({ timeout: 30_000 })
  await expect(page).toHaveURL(/\/system-verification\/change-requests\/new$/)
  // The same two numbered stages the requirements editor shows. Addressed as headings, because each stage
  // name appears twice on the page — once in the progress rail and once on the card it points at.
  await expect(editor.getByRole('heading', { name: 'Change case', level: 2 })).toBeVisible()
  await expect(editor.getByRole('heading', { name: 'Procedure changes', level: 2 })).toBeVisible()

  await editor.getByLabel('Title').fill('Verify the TCR authoring behavior as one package')
  await editor.getByLabel('Problem').fill('The approved change introduces behavior with no procedure.')
  await editor.getByLabel('Analysis').fill('The behavior spans one procedure boundary and belongs together.')
  await editor.getByLabel('Solution').fill('Raise one SYSTPCR and write the procedure it needs.')

  const sourceCheckbox = editor.getByRole('checkbox', { name: new RegExp(seeded.sourceNumber.replace('.', '\\.')) })
  await expect(sourceCheckbox).toBeVisible()
  await sourceCheckbox.check()

  const reportSearch = editor.getByRole('searchbox', { name: 'Find controlled PR' })
  await reportSearch.fill(seeded.report.title.slice(-12))
  const reportChoice = editor.getByRole('checkbox', { name: new RegExp(seeded.report.displayNumber.replace('.', '\\.')) })
  await expect(reportChoice).toBeVisible()
  await reportChoice.check()

  // Stage two: a procedure decision authored on the page and saved with the package, exactly as a change
  // request is created together with the requirement changes it proposes.
  // The act is chosen before the card exists, as it is on the requirements side.
  await editor.getByRole('button', { name: '+ Introduce System test procedure' }).click()
  const proposal = editor.locator('[data-procedure-proposal="0"]')
  await expect(proposal).toBeVisible()
  await proposal.getByLabel('Procedure number').fill('SYSTP-009901')
  await proposal.getByLabel('Title 1').fill('Verify oceanic sequencing under the new behaviour')
  await proposal.getByLabel('Objective 1').fill('Show the sequencing holds across the transition.')
  await proposal.getByLabel('Steps 1').fill('Exercise the changed behaviour on the rig.')
  await proposal.getByLabel('Expected result 1').fill('The sequencing is observed to hold.')
  await proposal.getByLabel('Rationale 1').fill('The approved change introduces behaviour with no procedure.')

  // An incomplete decision holds the package back rather than being silently dropped on save.
  await expect(editor.getByRole('button', { name: 'Raise SYSTPCR' })).toBeEnabled()

  await editor.getByRole('button', { name: 'Raise SYSTPCR' }).click()

  // The package opens onto its workspace so the engineer can start its procedure decisions.
  const workspace = page.getByRole('dialog', { name: /procedure decisions/ })
  await expect(workspace).toBeVisible({ timeout: 30_000 })
  await expect(page).toHaveURL(/authoring=[^&]+/)
  await page.reload()
  await expect(workspace).toBeVisible({ timeout: 30_000 })
  await expect(workspace.getByText('Engineering case')).toBeVisible()
  await expect(workspace.getByText('Verify the TCR authoring behavior as one package', { exact: true })).toBeVisible()
  await expect(workspace.getByText('Raise one SYSTPCR and write the procedure it needs.', { exact: true })).toBeVisible()

  // The case stays correctable while the package is open.
  await workspace.getByRole('button', { name: 'Edit case' }).click()
  const caseDialog = page.getByRole('dialog', { name: /Edit the case of/ })
  await expect(caseDialog).toBeVisible()
  await caseDialog.getByLabel('Title').fill('Verify the TCR authoring behavior as one package (corrected)')
  await caseDialog.getByRole('button', { name: 'Save case' }).click()
  await expect(workspace.getByText('Verify the TCR authoring behavior as one package (corrected)', { exact: true })).toBeVisible({ timeout: 30_000 })

  await workspace.getByRole('button', { name: 'Close test change request' }).click()
  await expect(page).not.toHaveURL(/authoring=/)
  await page.reload()
  await expect(workspace).toHaveCount(0)
  await expect(page.locator('.downstreamAssessment').filter({ hasText: /SYSTPCR-/ }).first())
    .toContainText(/SYSTPCR-\d{6}\.\d{2}/, { timeout: 30_000 })
})

test('released builds offer no new test change request action', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const seeded = await seedWorkspace(request, suffix, true)
  await login(page, 'admin', { openProject: false })
  await page.goto(
    `/programs/${seeded.workspace.program.id}/projects/${seeded.workspace.project.id}/releases/${seeded.workspace.release.id}/system-verification/coverage`,
    { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Downstream Assessments' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: /New System Test (Case|Procedure) Change Request/ })).toHaveCount(0)
})

test('HLR and LLR Change Requests pages offer their own creation actions', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('button', { name: 'Software' }).last().click()

  await page.getByRole('link', { name: 'Software Test Change Requests' }).click()
  await expect(page.getByRole('heading', { name: 'Software Test Change Requests' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: '+ New HLR Test Case Change Request' })).toBeVisible()

  await page.getByRole('tab', { name: 'LLR' }).click()
  await expect(page.getByRole('button', { name: '+ New LLR Test Case Change Request' })).toBeVisible()

  await page.getByRole('button', { name: 'System', exact: true }).last().click()
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  await expect(page.getByRole('button', { name: '+ New System Test Procedure Change Request' })).toBeVisible()
})
