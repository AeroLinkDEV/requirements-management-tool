import { collectMaintenancePreflight } from './maintenance-preflight-github.mjs'
import { evidenceDigest, MAINTENANCE_REPOSITORY as repository } from './maintenance-preflight.mjs'
import { createMaintenanceReview, evaluateMaintenanceApproval, MAINTENANCE_REVIEW_ENVIRONMENT } from './maintenance-approval.mjs'
import { fetchWorkflowRun } from './merge-authority-github.mjs'

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
  const currentBinding = await read(`${root}/actions/runs/${bindingRunId}`)
  if (['id', 'run_attempt', 'status', 'head_sha', 'head_branch', 'path', 'event', 'name'].some(key =>
    currentBinding[key] !== bindingRun[key]) || currentBinding.repository?.full_name !== bindingRun.repository?.full_name) {
    throw new Error('Binding workflow advanced during evidence collection.')
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
  const currentApprovals = await input.read(`/repos/${repository}/actions/runs/${input.bindingRunId}/approvals`)
  if (evidenceDigest(approvals) !== evidenceDigest(currentApprovals)) throw new Error('Approval history changed during revalidation.')
  return { ...evaluateMaintenanceApproval({ review, expectedDigest, approvals: currentApprovals }), review }
}

/** Publication is injected so tests can prove that late cancellation/rerun never posts a PASS. */
export async function publishApprovedMaintenance({ publish, ...input }) {
  await publish({ decision: 'PENDING', reasons: [] })
  const result = await verifyApprovedMaintenance(input)
  const [currentProduct, currentBinding] = await Promise.all([
    fetchWorkflowRun({ request: input.read, repository, runId: input.runId }),
    input.read(`/repos/${repository}/actions/runs/${input.bindingRunId}`),
  ])
  const binding = result.review.binding
  if (currentProduct.status !== 'completed' || currentProduct.runAttempt !== input.expectedProduct.runAttempt ||
      currentProduct.headSha !== input.expectedProduct.headSha || currentProduct.repository !== repository ||
      currentProduct.runId !== input.runId || currentProduct.event !== 'merge_group' ||
      currentProduct.workflowPath !== '.github/workflows/ci.yml' || currentProduct.workflowName !== 'Product quality gate' ||
      currentProduct.headBranch !== result.review.packet.evidence.run.headBranch ||
      currentBinding.id !== binding.id || currentBinding.run_attempt !== 1 || currentBinding.status !== 'in_progress' ||
      currentBinding.repository?.full_name !== binding.repository || currentBinding.event !== binding.event ||
      currentBinding.path !== binding.path || currentBinding.name !== binding.name ||
      currentBinding.head_sha !== binding.headSha || currentBinding.head_branch !== binding.headBranch) {
    throw new Error('Product or binding execution advanced immediately before publication.')
  }
  await publish({ decision: result.decision, reasons: result.reasons })
  return result
}
