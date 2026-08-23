import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

const completeImpacts = JSON.stringify({
  trace: 'Not Affected',
  verification: 'Not Affected',
  documents: 'Not Affected',
  baseline: 'Not Affected',
  collaboration: 'Not Affected',
})

/**
 * Creating a test procedure through the test change request that governs it.
 *
 * This is the test-side twin of authoring a requirement change inside a change request, and until now the
 * product had no room for it: a package could be raised, assessed and approved, but never say what procedure
 * work it actually proposed.
 *
 * The journey raises its own change request rather than claiming a seeded one. Claiming takes a package out
 * of a pool the other testing journeys draw from, and that pool has no spare — two of them failed when this
 * took one. Approving a change request is also how a test change request comes to exist in the first place,
 * so building the subject here is the honest setup rather than a workaround.
 */
test('a test engineer proposes a new procedure inside the test change request that governs it', async ({ page, request }) => {
  test.setTimeout(120_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const programName = `Procedure Authoring ${suffix}`
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName,
    programCode: `PA${suffix}`,
    projectName: 'Procedure Authoring Project',
    softwareProduct: 'Procedure Authoring Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()

  // The journey owns its Program and build. Freezing the shared showcase build made this test corrupt the
  // fixture that unrelated browser journeys inspect, and parallel CI exposed the false coupling.
  const usersResponse = await request.get(`${apiBase}/api/admin/users`)
  expect(usersResponse.ok(), await usersResponse.text()).toBeTruthy()
  const testEngineer = (await usersResponse.json())
    .find((user: { userName: string }) => user.userName === 'test.engineer')
  expect(testEngineer).toBeTruthy()
  const grant = await request.post(`${apiBase}/api/admin/users/${testEngineer.id}/memberships`, { data: {
    programId: workspace.program.id,
    role: 'TestEngineer',
  } })
  expect(grant.ok(), await grant.text()).toBeTruthy()

  const title = 'Oceanic sequencing for test change request authoring'
  const created = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title,
    problem: 'Oceanic waypoint sequencing is not represented.',
    analysis: 'The verification discipline must answer for the new behaviour.',
    solution: 'Introduce the requirement and let the test discipline assess it.',
    requirementChanges: [{
      level: 'System',
      kind: 'Introduce',
      targetSectionId: await firstSectionId(request, workspace.project.id, 'System'),
      statement: 'The FMS shall sequence oceanic waypoints in the order the active flight plan holds.',
      rationale: 'New capability.',
      verificationMethod: 'Test',
      impactDispositionJson: completeImpacts,
    }],
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()

  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    approvers: [{ userId: 'admin', name: 'Caller supplied name ignored' }],
    mode: 'Sequential',
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  // Approval is what raises the test assessment, so the package this journey authors into exists only from
  // here on.
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved so the verification discipline can assess it.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()
  const sourceNumber = draft.displayNumber as string

  // A procedure must link to an exact controlled requirement revision. Materialize this journey's approved
  // change into its own disposable candidate baseline so the TCR picker has that exact revision to offer.
  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    projectId: workspace.project.id,
    releaseId: workspace.release.id,
    name: 'Procedure authoring materialized build',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const [path, data] of [
    ['selections', { changeRequestId: draft.id }],
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), await response.text()).toBeTruthy()
  }

  await login(page, 'test.engineer', { openProject: false })
  await selectProgram(page, programName)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()

  const row = page.locator('.downstreamAssessment').filter({ hasText: sourceNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  // Claiming and concluding happen inside the assessment; the row offers one control in every state.
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  // Exact, because "SYSTCR required" is a substring of the button beside it that concludes the opposite.
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })
  await assessment.getByRole('button', { name: 'Decide' }).click()
  const impactDecision = page.getByRole('dialog', { name: /Decide / })
  await impactDecision.getByLabel('Decision').selectOption('NewProcedureRequired')
  await impactDecision.getByLabel('Rationale').fill('A new controlled procedure is required.')
  await impactDecision.getByRole('button', { name: 'Record decision' }).click()
  await expect(impactDecision).toHaveCount(0, { timeout: 30_000 })

  // The package opens in its own workspace, as a change request does from the requirements drawer.
  await assessment.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}$/ }).click()
  const drawer = page.getByRole('dialog', { name: /test procedure decisions/ })
  await expect(drawer).toBeVisible()
  // A package that has concluded test work is required but names none is unfinished, and says so rather than
  // rendering an empty list that reads as "nothing to do".
  await expect(drawer).toContainText('No test procedure decisions are proposed yet')

  await drawer.getByRole('button', { name: 'Write engineering case' }).click()
  const caseDialog = page.getByRole('dialog', { name: /Edit the case of/ })
  await caseDialog.getByLabel('Title').fill('Oceanic procedure change')
  await caseDialog.getByLabel('Problem').fill('The changed behavior has no controlled procedure decision.')
  await caseDialog.getByLabel('Analysis').fill('A new procedure is required to verify the requirement.')
  await caseDialog.getByLabel('Solution').fill('Propose and independently approve the new procedure.')
  await caseDialog.getByRole('button', { name: 'Save case' }).click()
  await expect(caseDialog).toHaveCount(0, { timeout: 30_000 })
  await drawer.getByRole('button', { name: 'Close test change request' }).click()

  // Reopen from persisted state: the queue contract must survive refresh, not depend on the drawer callback.
  await page.reload()
  const refreshedRow = page.locator('.downstreamAssessment').filter({ hasText: sourceNumber }).first()
  await refreshedRow.getByRole('button', { name: 'Open assessment' }).click()
  const refreshedAssessment = page.getByRole('dialog', { name: /test impact/ })
  await expect(refreshedAssessment.getByRole('button', { name: 'Send for approval' })).toHaveCount(0)
  const addDecision = refreshedAssessment.getByRole('button', { name: 'Add a test procedure decision' })
  await expect(addDecision).toBeVisible({ timeout: 30_000 })
  await addDecision.click()
  await expect(drawer).toBeVisible()

  await drawer.getByRole('button', { name: 'Propose a test procedure change' }).click()
  const dialog = page.getByRole('dialog', { name: 'Propose a test procedure change' })

  // The requirements a procedure verifies are chosen here, not left empty — without them the procedure
  // revision cannot be bound to what caused it.
  await expect(dialog.getByRole('group', { name: 'Requirements this test procedure verifies' })).toBeVisible()
  // Introducing allocates the number centrally, so there is deliberately nowhere to type one.
  await expect(dialog.getByRole('combobox', { name: 'Procedure' })).toHaveCount(0)
  await dialog.getByLabel('What is being done').selectOption('Retire')
  // A retirement withdraws a procedure rather than restating it, so no body is asked for — but which
  // procedure is being retired is not optional.
  await expect(dialog.getByRole('combobox', { name: 'Procedure' })).toBeVisible()
  await expect(dialog.getByLabel('Steps')).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Propose decision' })).toBeDisabled()
  await dialog.getByLabel('What is being done').selectOption('Introduce')

  await dialog.getByLabel('Title').fill('Oceanic waypoint sequencing')
  await dialog.getByLabel('Objective').fill('Verify oceanic waypoints sequence in flight-plan order.')
  await dialog.getByLabel('Steps').fill('1. Load the plan. 2. Advance past the first waypoint.')
  await dialog.getByLabel('Expected result').fill('The next eligible oceanic waypoint is sequenced.')
  await dialog.getByLabel('Why this test procedure work is required').fill('No procedure exercises oceanic sequencing after the approved change.')
  await expect(dialog.getByText('Select at least one exact requirement this new test procedure verifies.')).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Propose decision' })).toBeDisabled()
  await dialog.getByRole('group', { name: 'Requirements this test procedure verifies' })
    .getByRole('checkbox').first().check()
  await dialog.getByRole('button', { name: 'Propose decision' }).click()

  await expect(drawer.getByText(/SYSTP-\d{6}\.00 · New test procedure/)).toBeVisible({ timeout: 30_000 })
  await expect(drawer).toContainText('Oceanic waypoint sequencing')
  await expect(drawer).toContainText('1 test procedure decision proposed')

  // It is a controlled record, not drawer state: it survives leaving the page and coming back.
  await page.reload()
  const reopened = page.locator('.downstreamAssessment').filter({ hasText: sourceNumber }).first()
  await expect(reopened).toBeVisible({ timeout: 30_000 })
  // Reachable straight from the queue row, without opening the assessment first.
  await reopened.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2} · / }).click()
  const again = page.getByRole('dialog', { name: /test procedure decisions/ })
  await expect(again.getByText(/SYSTP-\d{6}\.00 · New test procedure/)).toBeVisible({ timeout: 30_000 })
  await expect(again).toContainText('SYSR-')

  await again.getByRole('button', { name: 'Withdraw this decision' }).click()
  await expect(again).toContainText('No test procedure decisions are proposed yet', { timeout: 30_000 })
})

test('a procedure modification shows retained coverage and records an explicit reviewed delta', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  const packageRow = page.locator('.downstreamAssessment').filter({ hasText: /SYSTCR-/ }).first()
  await expect(packageRow).toBeVisible({ timeout: 30_000 })

  const retainedId = '10000000-0000-0000-0000-000000000001'
  const removableId = '10000000-0000-0000-0000-000000000002'
  const additionId = '10000000-0000-0000-0000-000000000003'
  let recorded = false
  let submitted: Record<string, unknown> | undefined
  await page.route('**/api/test-change-reviews/*/procedure-changes', async route => {
    if (route.request().method() === 'POST') {
      submitted = route.request().postDataJSON()
      recorded = true
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        id: '20000000-0000-0000-0000-000000000001', displayNumber: 'SYSTP-000900.01',
        baseNumber: 'SYSTP-000900', revision: 1, kind: 'Modify', level: 'System', title: 'Revised procedure',
      }) })
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      id: '30000000-0000-0000-0000-000000000001', displayNumber: 'SYSTCR-000401.00',
      baseNumber: 'SYSTCR-000401', revision: 0, discipline: 'System', state: 'Draft',
      outcome: 'ChangeRequired', procedureLevel: 'System', sourceChangeRequestNumber: 'SRCR-000401.00',
      assignedEngineerId: 'test.engineer', version: recorded ? 2 : 1,
      title: 'Govern procedure coverage', problem: 'Coverage must remain exact.', analysis: 'Use an explicit delta.',
      solution: 'Retain unchanged links and record additions/removals.', problemRich: '', analysisRich: '', solutionRich: '',
      capabilities: { canProposeProcedureChange: true, canWithdrawProcedureChange: true, canRevise: false },
      drivingRequirementChoices: [
        { id: '50000000-0000-0000-0000-000000000001', revisionId: removableId, displayNumber: 'SYSR-000401.00', statement: 'Changed requirement.', level: 'System' },
        { id: '50000000-0000-0000-0000-000000000002', revisionId: additionId, displayNumber: 'SYSR-000403.00', statement: 'New governed requirement.', level: 'System' },
      ],
      procedureTargets: [{ baseNumber: 'SYSTP-000900', title: 'Carried procedure', currentRevision: 0,
        currentCoverage: [
          { id: '50000000-0000-0000-0000-000000000003', revisionId: removableId, displayNumber: 'SYSR-000401.00', statement: 'Changed requirement.', level: 'System', isSuspect: true },
          { id: '50000000-0000-0000-0000-000000000004', revisionId: retainedId, displayNumber: 'SYSR-000402.00', statement: 'Unchanged requirement.', level: 'System', isSuspect: false },
        ] }],
      procedureChanges: recorded ? [{
        id: '20000000-0000-0000-0000-000000000001', displayNumber: 'SYSTP-000900.01',
        baseNumber: 'SYSTP-000900', revision: 1, kind: 'Modify', level: 'System', title: 'Revised procedure',
        objective: 'Verify revised behavior.', preconditions: '', steps: 'Execute.', expectedResult: 'Observed.',
        rationale: 'The approved change alters procedure behavior.', drivingRequirementRevisionIds: [additionId],
        removedRequirementRevisionIds: [removableId], coverageChangeRationale: 'Replace obsolete coverage.',
        coverageChangedBy: 'test.engineer',
      }] : [],
    }) })
  })
  await page.route('**/api/test-change-reviews/*/procedure-targets*', async route => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      page: 1, pageSize: 50, totalCount: 1, totalPages: 1,
      items: [{ procedureId: '60000000-0000-0000-0000-000000000001', baseNumber: 'SYSTP-000900',
        title: 'Carried procedure', currentRevision: 0,
        currentCoverage: [
          { id: '50000000-0000-0000-0000-000000000003', revisionId: removableId, displayNumber: 'SYSR-000401.00', statement: 'Changed requirement.', level: 'System', isSuspect: true },
          { id: '50000000-0000-0000-0000-000000000004', revisionId: retainedId, displayNumber: 'SYSR-000402.00', statement: 'Unchanged requirement.', level: 'System', isSuspect: false },
        ] }],
    }) })
  })
  await page.route('**/api/test-change-reviews/*/requirement-candidates*', async route => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      page: 1, pageSize: 50, totalCount: 2, totalPages: 1,
      items: [
        { id: '50000000-0000-0000-0000-000000000001', revisionId: removableId, displayNumber: 'SYSR-000401.00', statement: 'Changed requirement.', level: 'System' },
        { id: '50000000-0000-0000-0000-000000000002', revisionId: additionId, displayNumber: 'SYSR-000403.00', statement: 'New governed requirement.', level: 'System' },
      ],
    }) })
  })

  await packageRow.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}/ }).click()
  const drawer = page.getByRole('dialog', { name: /test procedure decisions/ })
  await drawer.getByRole('button', { name: 'Propose a test procedure change' }).click()
  const dialog = page.getByRole('dialog', { name: 'Propose a test procedure change' })
  await dialog.getByLabel('What is being done').selectOption('Modify')
  await dialog.getByRole('combobox', { name: /^Procedure/ }).selectOption('SYSTP-000900')
  const current = dialog.getByRole('group', { name: 'Current exact coverage' })
  await expect(current).toContainText('SYSR-000401.00')
  await expect(current).toContainText('Suspect')
  await expect(current).toContainText('retained; outside this package change scope')
  await current.getByLabel(/SYSR-000401\.00/).uncheck()
  await dialog.getByRole('group', { name: 'Requirements this test procedure verifies' })
    .getByLabel(/SYSR-000403\.00/).check()
  await expect(dialog).toContainText('Proposed coverage: 1 retained, 1 added, 1 removed.')
  await dialog.getByLabel('Title').fill('Revised procedure')
  await dialog.getByLabel('Objective').fill('Verify revised behavior.')
  await dialog.getByLabel('Steps').fill('Execute.')
  await dialog.getByLabel('Expected result').fill('Observed.')
  await dialog.getByLabel('Why coverage is being added or removed').fill('Replace obsolete coverage.')
  await dialog.getByLabel('Why this test procedure work is required').fill('The approved change alters procedure behavior.')
  await dialog.getByRole('button', { name: 'Propose decision' }).click()

  expect(submitted?.drivingRequirementRevisionIds).toEqual([additionId])
  expect(submitted?.removedRequirementRevisionIds).toEqual([removableId])
  expect(submitted?.coverageChangeRationale).toBe('Replace obsolete coverage.')
  await expect(drawer).toContainText('Retained coverage: SYSR-000402.00')
  await expect(drawer).toContainText('Added coverage: SYSR-000403.00')
  await expect(drawer).toContainText('Removed coverage: SYSR-000401.00')
  await expect(drawer).toContainText('Approved final coverage: SYSR-000402.00 · Unchanged requirement., SYSR-000403.00 · New governed requirement.')
  await expect(drawer).toContainText('Coverage rationale: Replace obsolete coverage. · test.engineer')
})
test('a stale Modify target reloads controlled state and requires an explicit re-selection', async ({ page }) => {
  test.setTimeout(120_000)
  await login(page, 'test.engineer')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  const packageRow = page.locator('.downstreamAssessment').filter({ hasText: /SYSTCR-/ }).first()
  await expect(packageRow).toBeVisible({ timeout: 30_000 })

  let targetReads = 0
  let proposals = 0
  let staleReturned = false
  await page.route('**/api/test-change-reviews/*/procedure-changes', async route => {
    if (route.request().method() === 'POST') {
      proposals += 1
      staleReturned = true
      return route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({
        code: 'procedure_revision_not_next_for_build',
        error: 'The selected procedure changed after it was loaded. Refresh the procedure list and reselect the current controlled revision.',
      }) })
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      id: '30000000-0000-0000-0000-000000000002', displayNumber: 'SYSTCR-000402.00',
      baseNumber: 'SYSTCR-000402', revision: 0, discipline: 'System', state: 'Draft',
      outcome: 'ChangeRequired', procedureLevel: 'System', sourceChangeRequestNumber: 'SRCR-000402.00',
      assignedEngineerId: 'test.engineer', version: 1,
      title: 'Recover stale procedure target', problem: 'The selected revision may change.', analysis: 'Refresh authoritative effectivity.',
      solution: 'Require re-selection.', problemRich: '', analysisRich: '', solutionRich: '',
      capabilities: { canProposeProcedureChange: true, canWithdrawProcedureChange: true, canRevise: false },
      drivingRequirementChoices: [],
      procedureTargets: [],
      procedureChanges: [],
    }) })
  })
  await page.route('**/api/test-change-reviews/*/procedure-targets*', async route => {
    targetReads += 1
    const revision = staleReturned ? 1 : 0
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      page: 1, pageSize: 50, totalCount: 1, totalPages: 1,
      items: [{
        procedureId: '60000000-0000-0000-0000-000000000002',
        baseNumber: 'SYSTP-000901',
        title: 'Carried recovery procedure',
        currentRevision: revision,
        state: 'Approved',
        currentCoverage: [{
          id: '50000000-0000-0000-0000-000000000005',
          revisionId: '10000000-0000-0000-0000-000000000005',
          displayNumber: 'SYSR-000405.00',
          statement: 'Retained controlled requirement.',
          level: 'System',
          isSuspect: false,
        }],
      }],
    }) })
  })
  await page.route('**/api/test-change-reviews/*/requirement-candidates*', async route => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      page: 1, pageSize: 50, totalCount: 0, totalPages: 0, items: [],
    }) })
  })

  await packageRow.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}/ }).click()
  const drawer = page.getByRole('dialog', { name: /test procedure decisions/ })
  await drawer.getByRole('button', { name: 'Propose a test procedure change' }).click()
  const dialog = page.getByRole('dialog', { name: 'Propose a test procedure change' })
  await dialog.getByLabel('What is being done').selectOption('Modify')
  const procedure = dialog.getByRole('combobox', { name: /^Procedure/ })
  await expect(procedure.getByRole('option', { name: 'SYSTP-000901.00 - Carried recovery procedure · Approved' })).toHaveCount(1)
  await procedure.selectOption('SYSTP-000901')
  await dialog.getByLabel('Title').fill('Preserved procedure edit')
  await dialog.getByLabel('Objective').fill('Preserve authored intent while refreshing the controlled target.')
  await dialog.getByLabel('Steps').fill('Execute the retained controlled procedure.')
  await dialog.getByLabel('Expected result').fill('The selected behavior remains correct.')
  await dialog.getByLabel('Why this test procedure work is required').fill('The build changed after the picker was loaded.')
  const readsBeforeProposal = targetReads

  await dialog.getByRole('button', { name: 'Propose decision' }).click()

  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('alert')).toContainText(/refresh.*reselect/i)
  await expect(procedure).toHaveValue('')
  await expect(dialog.getByLabel('Title')).toHaveValue('Preserved procedure edit')
  await expect.poll(() => targetReads).toBeGreaterThan(readsBeforeProposal)
  await expect(procedure.getByRole('option', { name: 'SYSTP-000901.01 - Carried recovery procedure · Approved' })).toHaveCount(1)
  expect(proposals).toBe(1)
})
