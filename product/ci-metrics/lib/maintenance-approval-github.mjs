import { collectMaintenancePreflight } from './maintenance-preflight-github.mjs'
import { MAINTENANCE_REPOSITORY as repository } from './maintenance-preflight.mjs'
import { createMaintenanceReview, evaluateMaintenanceApproval, MAINTENANCE_REVIEW_ENVIRONMENT } from './maintenance-approval.mjs'

export async function collectMaintenanceReview({ read, graphql, preparer, prNumber, runId, bindingRunId, bindingRunAttempt, expectedProduct }) {
  if (!Number.isSafeInteger(bindingRunId) || bindingRunId < 1 || bindingRunAttempt !== 1) {
    throw new Error('A first-attempt binding workflow is required for fresh owner approval.')
  }
  const root = `/repos/${repository}`
  const [packet, bindingRun, environment, branchPolicies] = await Promise.all([
    collectMaintenancePreflight({ read, graphql, preparer, prNumber, runId }),
    read(`${root}/actions/runs/${bindingRunId}`),
    read(`${root}/environments/${MAINTENANCE_REVIEW_ENVIRONMENT}`),
    read(`${root}/environments/${MAINTENANCE_REVIEW_ENVIRONMENT}/deployment-branch-policies`),
  ])
  if (bindingRun.id !== bindingRunId || bindingRun.run_attempt !== bindingRunAttempt || bindingRun.status !== 'in_progress') {
    throw new Error('Binding run advanced or is not active.')
  }
  if (packet.evidence.run.headSha !== expectedProduct?.headSha || packet.evidence.run.runAttempt !== expectedProduct?.runAttempt) {
    throw new Error('Product run no longer matches the triggering candidate and attempt.')
  }
  // Status changes while waiting for the owner; immutable execution identity is what the digest binds.
  return createMaintenanceReview({ packet, environment, branchPolicies, binding: {
    id: bindingRun.id, attempt: bindingRun.run_attempt, repository: bindingRun.repository?.full_name,
    event: bindingRun.event, path: bindingRun.path, name: bindingRun.name,
    headSha: bindingRun.head_sha, headBranch: bindingRun.head_branch,
  } })
}

/** Recollect after reading authenticated approval history. Publish only against that fresh snapshot. */
export async function verifyApprovedMaintenance({ expectedDigest, ...input }) {
  const approvals = await input.read(`/repos/${repository}/actions/runs/${input.bindingRunId}/approvals`)
  const review = await collectMaintenanceReview(input)
  return { ...evaluateMaintenanceApproval({ review, expectedDigest, approvals }), review }
}
