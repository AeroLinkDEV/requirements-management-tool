import { collectMaintenancePreflight } from './maintenance-preflight-github.mjs'
import { evidenceDigest, MAINTENANCE_REPOSITORY as repository } from './maintenance-preflight.mjs'
import { createMaintenanceReview, evaluateMaintenanceApproval, MAINTENANCE_REVIEW_ENVIRONMENT } from './maintenance-approval.mjs'
import { fetchWorkflowRun } from './merge-authority-github.mjs'
import { routeMaintenanceRead, MAINTENANCE_RULESET_PATH } from './maintenance-evidence-reader.mjs'
import { maintenanceFailure, maintenanceFailureCode, maintenanceReadCode, maintenanceStage } from './maintenance-diagnostics.mjs'

export async function collectMaintenanceReview({ read, graphql, rulesetReader, preparer, prNumber, runId, bindingRunId, bindingRunAttempt, expectedProduct }) {
  const routedRead = rulesetReader ? routeMaintenanceRead({ read, rulesetReader }) : read
  const maintenanceRead = (path, options) => maintenanceStage(
    path === MAINTENANCE_RULESET_PATH ? 'collection-ruleset-read' : maintenanceReadCode(path, `/repos/${repository}`),
    () => routedRead(path, options))
  if (!Number.isSafeInteger(bindingRunId) || bindingRunId < 1 || bindingRunAttempt !== 1) {
    throw maintenanceFailure('binding-attempt-guard', new Error('A first-attempt binding workflow is required for fresh owner approval.'))
  }
  const root = `/repos/${repository}`
  const [packet, bindingRun, environment, branchPolicies] = await Promise.all([
    maintenanceStage('collection', () => collectMaintenancePreflight({ read: maintenanceRead,
      graphql: query => maintenanceStage('collection-graphql-read', () => graphql(query)), preparer, prNumber, runId })),
    maintenanceStage('collection-binding-read', () => read(`${root}/actions/runs/${bindingRunId}`)),
    maintenanceStage('collection-environment-read', () => routedRead(`${root}/environments/${MAINTENANCE_REVIEW_ENVIRONMENT}`)),
    maintenanceStage('collection-policy-read', () => routedRead(`${root}/environments/${MAINTENANCE_REVIEW_ENVIRONMENT}/deployment-branch-policies`)),
  ])
  if (bindingRun.id !== bindingRunId || bindingRun.run_attempt !== bindingRunAttempt || bindingRun.status !== 'in_progress') {
    throw maintenanceFailure('binding-identity-guard', new Error('Binding run advanced or is not active.'))
  }
  if (packet.evidence.run.headSha !== expectedProduct?.headSha || packet.evidence.run.runAttempt !== expectedProduct?.runAttempt) {
    throw maintenanceFailure('product-identity-guard', new Error('Product run no longer matches the triggering candidate and attempt.'))
  }
  const currentBinding = await maintenanceStage('collection-binding-read', () => read(`${root}/actions/runs/${bindingRunId}`))
  if (['id', 'run_attempt', 'status', 'head_sha', 'head_branch', 'path', 'event', 'name'].some(key =>
    currentBinding[key] !== bindingRun[key]) || currentBinding.repository?.full_name !== bindingRun.repository?.full_name) {
    throw maintenanceFailure('binding-freshness-guard', new Error('Binding workflow advanced during evidence collection.'))
  }
  // Status changes while waiting for the owner; immutable execution identity is what the digest binds.
  return maintenanceStage('review-validation', () => createMaintenanceReview({ packet, environment, branchPolicies, binding: {
    id: bindingRun.id, attempt: bindingRun.run_attempt, repository: bindingRun.repository?.full_name,
    event: bindingRun.event, path: bindingRun.path, name: bindingRun.name,
    headSha: bindingRun.head_sha, headBranch: bindingRun.head_branch,
  } }))
}

/** Recollect after reading authenticated approval history. Publish only against that fresh snapshot. */
export async function verifyApprovedMaintenance({ expectedDigest, ...input }) {
  const approvals = await maintenanceStage('approval-read', () => input.read(`/repos/${repository}/actions/runs/${input.bindingRunId}/approvals`))
  const review = await maintenanceStage('collection', () => collectMaintenanceReview(input))
  const currentApprovals = await maintenanceStage('approval-reread', () => input.read(`/repos/${repository}/actions/runs/${input.bindingRunId}/approvals`))
  await maintenanceStage('approval-history-guard', () => {
    if (evidenceDigest(approvals) !== evidenceDigest(currentApprovals)) throw new Error('Approval history changed during revalidation.')
  })
  return { ...await maintenanceStage('approval-evaluation', () => evaluateMaintenanceApproval({ review, expectedDigest, approvals: currentApprovals })), review }
}

/** Publication is injected so tests can prove that late cancellation/rerun never posts a PASS. */
export async function publishApprovedMaintenance({ publish, onFailure, ...input }) {
  try {
    await maintenanceStage('initial-publication', () => publish({ decision: 'PENDING', reasons: [] }))
    const result = await verifyApprovedMaintenance(input)
    const [currentProduct, currentBinding] = await Promise.all([
      maintenanceStage('final-product-read', () => fetchWorkflowRun({ request: input.read, repository, runId: input.runId })),
      maintenanceStage('final-binding-read', () => input.read(`/repos/${repository}/actions/runs/${input.bindingRunId}`)),
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
      throw maintenanceFailure('final-execution-guard', new Error('Product or binding execution advanced immediately before publication.'))
    }
    await maintenanceStage('final-publication', () => publish({ decision: result.decision, reasons: result.reasons }))
    return result
  } catch (error) {
    // Diagnostic sink failure cannot replace the original exception or grant authority.
    try { onFailure?.(maintenanceFailureCode(error)) } catch { }
    throw error
  }
}
