import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

/**
 * Browser affordances must match the server's assignment/supervisory policy (#366): an unrelated Test
 * Engineer sees no decision/submit controls, the holder does, and Test Lead/Administrator retain
 * supervisory capability. Reassignment updates the affordance after refresh.
 */
test('TCR assignment authority drives the controls the browser shows', async ({ page, browser, request }) => {
  test.setTimeout(360_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const programName = `Assignment Authority ${suffix}`

  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName,
    programCode: `AA${suffix}`,
    projectName: 'Assignment Authority Project',
    softwareProduct: 'Assignment Authority Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()

  const impacts = JSON.stringify({
    trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
    baseline: 'Not Affected', collaboration: 'Not Affected',
  })
  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: `Assignment authority source ${suffix}`,
    problem: 'The new behavior has no test coverage.',
    analysis: 'A decision is required.',
    solution: 'Record the decision.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: await firstSectionId(request, workspace.project.id),
      statement: `The ${suffix} product shall expose an assignment-authority verification target.`,
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
    data: { password: 'AeroLink!2026', meaning: 'Approved for assignment authority verification.' },
  })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const usersResponse = await request.get(`${apiBase}/api/admin/users`)
  expect(usersResponse.ok(), await usersResponse.text()).toBeTruthy()
  const users = await usersResponse.json()
  const holder = users.find((user: { userName: string }) => user.userName === 'test.engineer')
  const unrelated = users.find((user: { userName: string }) => user.userName === 'test.author')
  expect(holder).toBeTruthy()
  expect(unrelated).toBeTruthy()
  for (const engineer of [holder, unrelated]) {
    const grant = await request.post(`${apiBase}/api/admin/users/${engineer.id}/memberships`,
      { data: { programId: workspace.program.id, role: 'TestEngineer' } })
    expect(grant.ok(), await grant.text()).toBeTruthy()
  }

  const reviews = await (await request.get(`${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`)).json()
  const review = reviews.items.find((item: { displayNumber: string }) => item.displayNumber.startsWith('SRCR-'))
  expect(review).toBeTruthy()
  const reviewId = review.id as string
  const assign = await request.post(`${apiBase}/api/test-change-reviews/${reviewId}/assign`,
    { data: { engineerId: 'test.engineer' } })
  expect(assign.ok(), await assign.text()).toBeTruthy()

  const coverageUrl = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/system-verification/coverage`

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, programName)
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Downstream Assessments' }).click()
  await page.locator('.downstreamAssessment').filter({ hasText: 'SRCR-' }).getByRole('button', { name: 'Open assessment' }).click()
  const adminDrawer = page.getByRole('dialog', { name: /test impact/ })
  await expect(adminDrawer.getByRole('button', { name: 'Decide' }).first()).toBeVisible({ timeout: 30_000 })

  const openAs = async (userName: string) => {
    const context = await browser.newContext()
    const p = await context.newPage()
    await login(p, userName, { openProject: false })
    await p.goto(new URL(coverageUrl, page.url()).toString(), { waitUntil: 'load' })
    const row = p.locator('.downstreamAssessment').filter({ hasText: 'SRCR-' })
    await expect(row).toBeVisible({ timeout: 30_000 })
    await row.getByRole('button', { name: 'Open assessment' }).click()
    const drawer = p.getByRole('dialog', { name: /test impact/ })
    await expect(drawer).toBeVisible()
    return { context, p, drawer }
  }

  const unrelatedView = await openAs('test.author')
  await expect(unrelatedView.drawer.getByRole('button', { name: 'Decide' })).toHaveCount(0)
  await expect(unrelatedView.drawer.getByRole('button', { name: 'Send for approval' })).toHaveCount(0)
  await unrelatedView.context.close()

  const holderView = await openAs('test.engineer')
  await expect(holderView.drawer.getByRole('button', { name: 'Decide' }).first()).toBeVisible({ timeout: 30_000 })
  await holderView.context.close()

  // Reassign to the other engineer; the affordance follows the record after refresh.
  const reassign = await request.post(`${apiBase}/api/test-change-reviews/${reviewId}/assign`,
    { data: { engineerId: 'test.author' } })
  expect(reassign.ok(), await reassign.text()).toBeTruthy()
  const nowHolder = await openAs('test.author')
  await expect(nowHolder.drawer.getByRole('button', { name: 'Decide' }).first()).toBeVisible({ timeout: 30_000 })
  await nowHolder.context.close()
  const nowUnrelated = await openAs('test.engineer')
  await expect(nowUnrelated.drawer.getByRole('button', { name: 'Decide' })).toHaveCount(0)
  await nowUnrelated.context.close()
})
