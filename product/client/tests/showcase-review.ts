import { expect } from '@playwright/test'
import type { APIRequestContext } from '@playwright/test'
import { apiBase, apiLogin } from './auth'

/** Prepare a signed fixture without changing the shared project's review policy.
 * The fresh FMS has three configured stages. Workflow journeys can retire that
 * policy, so later fixtures must also support the product's unconfigured fallback.
 * An unexpected active policy fails explicitly; it is never disabled to pass.
 */
export async function approveShowcaseSystemFixture(
  author: APIRequestContext,
  signer: APIRequestContext,
  draft: { id: string; projectId: string; version: number },
  meaning: string,
) {
  const applicable = await author.get(
    `${apiBase}/api/review-workflows/applicable?projectId=${draft.projectId}&type=System`,
  )
  expect(applicable.ok(), await applicable.text()).toBeTruthy()
  const workflow = await applicable.json()
  if (workflow.required) {
    expect(workflow.mode).toBe('Sequential')
    expect(workflow.stages.map((stage: { requiredRole: string; kind: string }) => ({
      role: stage.requiredRole, kind: stage.kind,
    })), 'the active FMS fixture policy must be understood').toEqual([
      { role: 'SystemEngineer', kind: 'Review' },
      { role: 'SystemEngineeringLead', kind: 'Review' },
      { role: 'SoftwareEngineeringLead', kind: 'Approval' },
    ])
  }
  const reviewers = workflow.required
    ? ['systems.reviewer', 'systems.lead', 'software.lead']
    : ['admin']
  const submitted = await author.post(`${apiBase}/api/change-requests/${draft.id}/submit`, {
    data: {
      expectedVersion: draft.version,
      mode: 'Sequential',
      approvers: reviewers.map(userId => ({ userId, name: 'Caller supplied name is ignored' })),
    },
  })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  let detail = await submitted.json()
  const cycle = detail.reviewCycles.at(-1)
  expect(detail.state).toBe('InReview')
  expect(cycle.workflowId).toBe(workflow.required ? workflow.id : null)
  expect(cycle.steps.map((step: { approverId: string }) => step.approverId)).toEqual(reviewers)
  for (const userName of reviewers) {
    await apiLogin(signer, userName)
    const approved = await signer.post(`${apiBase}/api/change-requests/${draft.id}/approve`, {
      data: { expectedVersion: detail.version, password: 'AeroLink!2026', meaning },
    })
    expect(approved.ok(), `${userName}: ${await approved.text()}`).toBeTruthy()
    detail = await approved.json()
  }
  expect(detail.state).toBe('Approved')
  expect(detail.reviewCycles.at(-1).steps.every((step: { state: string }) => step.state === 'Approved')).toBe(true)

  // A scoped empty-signature assertion is only meaningful if real signatures exist.
  const signaturesResponse = await author.get(`${apiBase}/api/signatures?artifactId=${draft.id}`)
  expect(signaturesResponse.ok(), await signaturesResponse.text()).toBeTruthy()
  const signatures = await signaturesResponse.json()
  expect(signatures).toHaveLength(reviewers.length)
  for (const [index, userName] of reviewers.entries()) {
    expect(signatures).toContainEqual(expect.objectContaining({
      artifactId: draft.id, userName, reviewStepId: cycle.steps[index].id,
      workflowId: cycle.workflowId,
    }))
  }
}
