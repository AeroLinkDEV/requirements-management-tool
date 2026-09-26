import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

/**
 * #1188 / DEC-144: a project that uses Verification without Requirements. Its System test change request is
 * raised on its own case, and the procedures it proposes are Standalone. Drives the real editor against the
 * real API and the run's disposable database.
 */
test('a Verification-only project raises test work on its own case with Standalone procedures', async ({ page, request }, testInfo) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const suffix = `${Date.now()}`.slice(-7)
  const created = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Standalone ${suffix}`,
    programCode: `SV${suffix}`,
    projectName: 'Standalone Verification Project',
    softwareProduct: 'Standalone Verification Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json() as { program: { id: string }; project: { id: string }; release: { id: string } }
  const features = await request.put(`${apiBase}/api/projects/${workspace.project.id}/features`, { data: {
    expectedVersion: 0, reason: 'Verification-only bench project', enabled: ['TeamWork', 'Verification', 'Release'],
  } })
  expect(features.ok(), await features.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  await page.goto(`${root}/system-verification/change-requests/new`)
  const editor = page.locator('[data-tcr-editor]')
  // No change request can exist, so none is asked for.
  await expect(editor.getByRole('note')).toContainText('This project does not use Requirements')
  await expect(editor.locator('.tcrSourceChoices')).toHaveCount(0)
  await expect(editor.locator('.tcrDriverHint')).toHaveCount(0)
  await editor.getByRole('note').scrollIntoViewIfNeeded()
  await page.screenshot({ path: testInfo.outputPath('standalone-tcr-note.png') })

  await editor.getByLabel('Title', { exact: true }).fill('Bench frame capture')
  await editor.getByLabel('Problem', { exact: true }).fill('The bench rig loses frames under load.')
  await editor.getByLabel('Analysis', { exact: true }).fill('Nothing exercises frame capture at load today.')
  await editor.getByLabel('Solution', { exact: true }).fill('Introduce a procedure that counts frames at load.')
  await editor.getByRole('button', { name: /^\+ Introduce System/ }).click()
  const number = `SYSTP-${suffix.padStart(6, '0').slice(-6)}`
  await editor.getByLabel(/ number 1$/).fill(number)
  await editor.getByLabel('Title 1', { exact: true }).fill('Frame capture under load')
  await editor.getByLabel('Objective 1', { exact: true }).fill('Show the rig keeps every frame at load.')
  await editor.getByLabel('Steps 1', { exact: true }).fill('1. Apply load. 2. Count frames.')
  await editor.getByLabel('Expected result 1', { exact: true }).fill('No frame is lost.')
  await editor.getByLabel('Rationale 1', { exact: true }).fill('The rig loses frames.')
  await page.screenshot({ path: testInfo.outputPath('standalone-tcr-editor.png'), fullPage: true })

  const savedPromise = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/test-change-requests'))
  await editor.getByRole('button', { name: 'Raise SYSTPCR', exact: true }).click()
  const saved = await savedPromise
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const raised = await saved.json() as { id: string }

  const detail = await request.get(`${apiBase}/api/test-change-reviews/${raised.id}/procedure-changes`)
  expect(detail.ok(), await detail.text()).toBeTruthy()
  const body = await detail.json() as { originDisplayLabel: string; changes?: { parentKind: string }[]; artifactChanges?: { parentKind: string }[]; procedureChanges?: { parentKind: string }[] }
  expect(body.originDisplayLabel).toBe('Own case')
  const changes = body.artifactChanges ?? body.procedureChanges ?? body.changes ?? []
  expect(changes.map(change => change.parentKind)).toEqual(['Standalone'])
})

/**
 * #1188 / DEC-144 answer 3: without Requirements there is no requirement coverage, so each case shows its
 * execution status, and Release Readiness says which gates do not apply.
 */
test('a Verification-only build reports each case by execution status and marks requirement gates not applicable', async ({ page, request, playwright }, testInfo) => {
  test.setTimeout(240_000)
  await apiLogin(request)
  const suffix = `${Date.now()}`.slice(-7)
  const created = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Standalone status ${suffix}`, programCode: `SS${suffix}`, projectName: 'Standalone Status Project',
    softwareProduct: 'Standalone Status Product', initialRelease: '1.0', initialReleaseIsReleased: false,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json() as { program: { id: string }; project: { id: string }; release: { id: string } }
  const features = await request.put(`${apiBase}/api/projects/${workspace.project.id}/features`, { data: {
    expectedVersion: 0, reason: 'Verification-only bench project', enabled: ['TeamWork', 'Verification', 'Release'],
  } })
  expect(features.ok(), await features.text()).toBeTruthy()

  // Test work raised on its own case, reviewed and approved.
  const number = `SYSTP-${suffix.padStart(6, '0').slice(-6)}`
  const raised = await request.post(`${apiBase}/api/releases/${workspace.release.id}/test-change-requests`, { data: {
    discipline: 'System', changeRequestIds: [], title: 'Bench frame capture', problem: 'Frames are lost under load.',
    analysis: 'Nothing exercises it.', solution: 'Introduce a procedure.',
    artifactChanges: [{ baseNumber: number, revision: 0, level: 'System', kind: 'Introduce', title: 'Frame capture under load',
      objective: 'Show the rig keeps every frame at load.', preconditions: 'Rig powered.', steps: '1. Apply load. 2. Count frames.',
      expectedResult: 'No frame is lost.', rationale: 'The rig loses frames.', parentKind: 'Standalone' }],
  } })
  expect(raised.ok(), await raised.text()).toBeTruthy()
  const review = await raised.json() as { id: string }
  // Approval authority comes from a Project Leadership position, as in the issue-726 journey.
  const users = await request.get(`${apiBase}/api/admin/users`)
  expect(users.ok(), await users.text()).toBeTruthy()
  const reviewerAccount = (await users.json() as { id: string; userName: string }[])
    .find(user => user.userName === 'systems.reviewer')
  expect(reviewerAccount, 'the seeded reviewer account must exist').toBeTruthy()
  const grant = await request.post(`${apiBase}/api/admin/users/${reviewerAccount!.id}/memberships`, {
    data: { programId: workspace.program.id, role: 'SystemEngineer' },
  })
  expect(grant.ok() || grant.status() === 409, await grant.text()).toBeTruthy()
  const elevation = await request.post(`${apiBase}/api/projects/${workspace.project.id}/leadership/SystemEngineeringLead/primary`, {
    data: { holderUserId: reviewerAccount!.id },
  })
  expect(elevation.ok(), await elevation.text()).toBeTruthy()
  const submitted = await request.post(`${apiBase}/api/test-change-reviews/${review.id}/submit`, { data: { approverId: 'systems.reviewer' } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const reviewer = await playwright.request.newContext()
  const reviewerLogin = await reviewer.post(`${apiBase}/api/auth/login`, { data: { userName: 'systems.reviewer', password: 'AeroLink!2026' } })
  expect(reviewerLogin.ok(), await reviewerLogin.text()).toBeTruthy()
  const approved = await reviewer.post(`${apiBase}/api/test-change-reviews/${review.id}/approve`, { data: {
    rationale: 'The standalone procedure is complete.', password: 'AeroLink!2026',
    meaning: 'I approve this exact System test change request package.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()
  await reviewer.dispose()

  // A baseline with no change requests: it freezes empty, then carries the approved work.
  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: `SW-98.${suffix.slice(-2)}`, revision: 0, projectId: workspace.project.id, releaseId: workspace.release.id,
    name: 'Standalone bench baseline',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json() as { id: string }
  for (const [path, data] of [
    ['freeze', {}], ['materialize-requirements', {}], ['test-change-requests', { testChangeRequestId: review.id }],
    ['materialize-test-procedures', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy()
  }

  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  await page.goto(`${root}/system-verification/procedures?coverage=report`)
  const report = page.getByLabel(/coverage$/i).filter({ has: page.getByRole('heading', { name: 'Execution status' }) })
  await expect(report.getByRole('heading', { name: 'Execution status', level: 2 })).toBeVisible()
  await expect(report).toContainText('This project does not use Requirements, so there is no requirement coverage.')
  await expect(report.locator('.coverageRow').filter({ hasText: number })).toContainText('Not run')
  await expect(report.getByRole('heading', { name: 'Requirement coverage' })).toHaveCount(0)
  await report.scrollIntoViewIfNeeded()
  await page.screenshot({ path: testInfo.outputPath('standalone-execution-status.png') })

  // Release Readiness: the requirement gates say why they do not apply; coverage is each case's status.
  const campaignResponse = await request.post(`${apiBase}/api/release-campaigns`, { data: {
    projectId: workspace.project.id, releaseId: workspace.release.id, baselineId: baseline.id, name: 'Bench 1.0 release',
  } })
  expect(campaignResponse.ok(), await campaignResponse.text()).toBeTruthy()
  const campaign = await campaignResponse.json() as { id: string }
  const detail = await request.get(`${apiBase}/api/release-campaigns/${campaign.id}`)
  expect(detail.ok(), await detail.text()).toBeTruthy()
  const gates = (await detail.json() as { readiness: { gates: { code: string; name: string; evaluationState: string; detail: string }[] } })
    .readiness.gates
  const gate = (code: string) => gates.find(item => item.code === code)!
  for (const code of ['change_control', 'impact_disposition', 'traceability', 'verification_impact', 'code_traceability'])
    expect(gate(code).evaluationState, code).toBe('NotApplicable')
  expect(gate('coverage').name).toBe('Every case has passed')
  expect(gate('coverage').detail).toContain('0 passed, 0 failed, 0 blocked, 1 not run')
  // Only verification documents are owed: the requirement specifications belong to Requirements.
  expect(gate('documents').detail).toContain('no requirement specification is owed')

  await page.goto(`${root}/release-readiness`)
  const rail = page.getByRole('region', { name: 'Release lifecycle' })
  for (const stage of ['Requirements', 'Changes', 'Trace'])
    await expect(rail.locator('article').filter({ hasText: stage })).toContainText('Not applicable')
  // Coverage is each case's status: the one not-run case is what needs attention.
  await expect(page.locator('.attentionCard article').filter({ hasText: 'Every case has passed' }))
    .toContainText('0 passed, 0 failed, 0 blocked, 1 not run')
  const traceHealth = page.locator('.healthStrip article').filter({ hasText: 'Trace coverage' })
  await expect(traceHealth).toContainText('N/A')
  await expect(traceHealth).not.toContainText('Target achieved')
  await page.screenshot({ path: testInfo.outputPath('standalone-readiness-gates.png'), fullPage: true })
})
