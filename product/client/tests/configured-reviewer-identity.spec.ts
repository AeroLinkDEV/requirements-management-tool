import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, showcaseSeed } from './auth'

const completeImpacts = JSON.stringify({
  trace: 'Not Affected',
  verification: 'Not Affected',
  documents: 'Not Affected',
  baseline: 'Not Affected',
  collaboration: 'Not Affected',
})

let ownedWorkflowId: string | undefined

test.afterEach(async ({ request }) => {
  if (!ownedWorkflowId) return
  await apiLogin(request)
  const retired = await request.post(`${apiBase}/api/review-workflows/${ownedWorkflowId}/retire`, { data: {} })
  expect(retired.ok(), await retired.text()).toBeTruthy()
  ownedWorkflowId = undefined
})

test('configured reviewer identity remains canonical from assignment through signature', async ({ page, request, playwright }) => {
  test.setTimeout(120_000)
  const suffix = Date.now().toString().slice(-7)
  const showcase = await showcaseSeed(request)

  // Cancellation journeys can retire the shared showcase policy. This test specifically requires a
  // configured review, so supply that precondition without replacing an existing active policy.
  await apiLogin(request)
  const applicable = await request.get(`${apiBase}/api/review-workflows/applicable?projectId=${showcase.projectId}&type=System`)
  expect(applicable.ok(), await applicable.text()).toBeTruthy()
  if (!(await applicable.json()).required) {
    const created = await request.post(`${apiBase}/api/review-workflows`, { data: {
      projectId: showcase.projectId,
      name: `Canonical reviewer fixture ${suffix}`,
      appliesTo: 'System',
      mode: 'Sequential',
      stages: [
        { name: 'Systems review', kind: 'Review', requiredAuthority: { kind: 'BaseRole', role: 'SystemEngineer' } },
        { name: 'Systems lead review', kind: 'Review', requiredAuthority: { kind: 'LeadershipPosition', position: 'SystemEngineeringLead' } },
        { name: 'Software lead approval', kind: 'Approval', requiredAuthority: { kind: 'LeadershipPosition', position: 'SoftwareEngineeringLead' } },
      ],
    } })
    expect(created.ok(), await created.text()).toBeTruthy()
    const workflow = await created.json()
    const activated = await request.post(`${apiBase}/api/review-workflows/${workflow.id}/activate`, { data: {} })
    expect(activated.ok(), await activated.text()).toBeTruthy()
    ownedWorkflowId = workflow.id
  }

  await apiLogin(request, 'systems.author')
  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    type: 'System',
    title: `Canonical reviewer ${suffix}`,
    problem: 'Approval attribution must identify the configured principal.',
    analysis: 'Author, assigned reviewer, active reviewer, and signer are distinct concepts.',
    solution: 'Carry the canonical account identity and frozen authority through the review cycle.',
    requirementChanges: [{
      level: 'System',
      kind: 'Introduce',
      targetSectionId: await firstSectionId(request, showcase.projectId),
      statement: 'The FMS shall preserve canonical reviewer attribution.',
      rationale: 'Controlled approval evidence cannot name another participant.',
      verificationMethod: 'Inspection',
      impactDispositionJson: completeImpacts,
    }],
  } })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const draft = await draftResponse.json()
  const submitResponse = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    mode: 'Sequential',
    approvers: ['systems.reviewer', 'systems.lead', 'software.lead'].map(userId => ({
      userId, name: 'Caller supplied name is ignored',
    })),
  } })
  expect(submitResponse.ok(), await submitResponse.text()).toBeTruthy()
  const submitted = await submitResponse.json()
  expect(submitted.reviewCycles.at(-1).steps).toHaveLength(3)
  expect(submitted.reviewCycles.at(-1).steps[0]).toMatchObject({
    approverId: 'systems.reviewer',
    approverName: 'Systems Engineer',
    authority: 'SystemEngineer',
    state: 'Active',
  })

  const reviewer = await playwright.request.newContext()
  const reviewerLogin = await reviewer.post(`${apiBase}/api/auth/login`, {
    data: { userName: 'systems.reviewer', password: 'AeroLink!2026' },
  })
  expect(reviewerLogin.ok(), await reviewerLogin.text()).toBeTruthy()
  const queueResponse = await reviewer.get(`${apiBase}/api/enterprise-requirements/work-queue?projectId=${showcase.projectId}`)
  expect(queueResponse.ok(), await queueResponse.text()).toBeTruthy()
  const queue = await queueResponse.json()
  expect(queue.notifications).toContainEqual(expect.objectContaining({
    artifactId: draft.id,
    route: `scr:${draft.id}`,
    type: 'ReviewActivated',
  }))
  await reviewer.dispose()

  await login(page, 'systems.reviewer')
  await page.getByRole('link', { name: 'My Work' }).click()
  const assigned = page.locator('.workQueue article').filter({ hasText: draft.displayNumber })
  await expect(assigned).toContainText('Change request approval')
  await assigned.click()
  await expect(page).toHaveURL(new RegExp(`/systems/change-requests/${draft.id}$`))

  const review = page.getByRole('heading', { name: 'Review cycle 1' }).locator('..').locator('..').locator('..')
  await expect(review).toContainText('Systems Engineer')
  await expect(review).toContainText(submitted.reviewCycles.at(-1).steps[0].stageName)
  await expect(review).toContainText('Systems Engineer is the active reviewer')
  await expect(review).not.toContainText('Maya Patel')

  await page.getByRole('button', { name: 'Review & electronically approve' }).click()
  await page.getByLabel('Re-enter your password').fill('AeroLink!2026')
  await page.getByRole('button', { name: 'Sign & approve' }).click()
  await expect(review).toContainText('Approved')

  const signatureResponse = await request.get(`${apiBase}/api/signatures?artifactId=${draft.id}`)
  expect(signatureResponse.ok(), await signatureResponse.text()).toBeTruthy()
  expect(await signatureResponse.json()).toContainEqual(expect.objectContaining({
    userName: 'systems.reviewer',
    displayName: 'Systems Engineer',
    artifactId: draft.id,
  }))
  await expect(page.locator('.auditRow').filter({ hasText: 'Approval Recorded' }).first()).toContainText('Systems Engineer')
})
