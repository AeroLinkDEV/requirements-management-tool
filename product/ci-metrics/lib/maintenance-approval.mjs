// Only the protected default-branch caller supplies these API snapshots. No candidate artifact is input.
import { canonicalJson, evidenceDigest, evaluateMaintenancePreflight,
  MAINTENANCE_REPOSITORY } from './maintenance-preflight.mjs'

export const MAINTENANCE_REVIEW_ENVIRONMENT = 'merge-authority-maintenance'
export const MAINTENANCE_REQUEST_LABEL = 'authority-maintenance-requested'
export const MAINTENANCE_OWNER = Object.freeze({ id: 295123958, login: 'seanmccarthyns', type: 'User' })
const positive = value => Number.isSafeInteger(value) && value > 0
const digestPattern = /^[0-9a-f]{64}$/

export function maintenanceReviewRefusals({ packet, binding, environment, branchPolicies } = {}) {
  const reasons = []
  const { digest, ...payload } = packet ?? {}
  if (!digestPattern.test(digest ?? '') || evidenceDigest(payload) !== digest ||
      canonicalJson(packet?.assessment ?? null) !== canonicalJson(evaluateMaintenancePreflight(packet?.evidence)) ||
      packet?.assessment?.disposition !== 'REVIEW_REQUIRED') reasons.push('maintenance-preflight-not-reviewable')
  const preparerIsMain = packet?.preparer?.commitSha === packet?.evidence?.main?.sha &&
    packet?.preparer?.treeSha === packet?.evidence?.baseTreeSha
  // #1164: main advances past the checkout while a candidate waits for the entries ahead to merge. Accept that
  // lag only when no protected path differs between the checkout and current main, so the judging kernel is
  // byte-identical to main's (DEC-121). Missing drift evidence is never equivalence.
  const drift = packet?.evidence?.preparerProtectedDrift
  const preparerEquivalent = Array.isArray(drift) && drift.length === 0
  if (!preparerIsMain && !preparerEquivalent) reasons.push('preparer-not-current-protected-main')
  if (!packet?.evidence?.pr?.labels?.includes(MAINTENANCE_REQUEST_LABEL)) reasons.push('maintenance-not-requested')
  // Approval history has no attempt identifier. Never carry it across a rerun of the binding workflow.
  if (!positive(binding?.id) || binding?.attempt !== 1 || binding?.repository !== MAINTENANCE_REPOSITORY ||
      binding?.event !== 'workflow_run' || binding?.path !== '.github/workflows/merge-queue-binding.yml' ||
      binding?.name !== 'Trusted merge-queue binding' || binding?.headBranch !== 'main' ||
      binding?.headSha !== packet?.preparer?.commitSha) reasons.push('binding-workflow-identity-unverified')
  const rules = environment?.protection_rules
  const reviewRules = Array.isArray(rules) ? rules.filter(rule => rule.type === 'required_reviewers') : []
  const reviewers = reviewRules[0]?.reviewers
  if (!positive(environment?.id) || environment?.name !== MAINTENANCE_REVIEW_ENVIRONMENT ||
      environment?.can_admins_bypass !== false || rules?.length !== 2 || reviewRules.length !== 1 ||
      rules.filter(rule => rule.type === 'branch_policy').length !== 1 ||
      reviewRules[0].prevent_self_review !== false || reviewers?.length !== 1 ||
      reviewers[0].type !== 'User' || reviewers[0].reviewer?.id !== MAINTENANCE_OWNER.id ||
      reviewers[0].reviewer?.login !== MAINTENANCE_OWNER.login || reviewers[0].reviewer?.type !== 'User' ||
      environment?.deployment_branch_policy?.protected_branches !== false ||
      environment?.deployment_branch_policy?.custom_branch_policies !== true ||
      branchPolicies?.total_count !== 1 || branchPolicies?.branch_policies?.length !== 1 ||
      branchPolicies.branch_policies[0].name !== 'main' || branchPolicies.branch_policies[0].type !== 'branch') {
    reasons.push('owner-review-environment-unverified')
  }
  return reasons
}

export function createMaintenanceReview(input) {
  const reasons = maintenanceReviewRefusals(input)
  if (reasons.length) throw new Error(`Maintenance review refused: ${reasons.join('; ')}`)
  const payload = { schemaVersion: 'aerolink-authority-maintenance-review/v1', ...input }
  return { ...payload, digest: evidenceDigest(payload) }
}

export function evaluateMaintenanceApproval({ review, expectedDigest, approvals }) {
  const reasons = maintenanceReviewRefusals(review)
  const { digest, ...payload } = review ?? {}
  if (!digestPattern.test(expectedDigest ?? '') || expectedDigest !== digest || evidenceDigest(payload) !== digest) {
    reasons.push('reviewed-evidence-changed')
  }
  // Reject ambiguous/mixed history; a bypass, a label, or an ordinary PR comment is never an approval.
  const approval = Array.isArray(approvals) && approvals.length === 1 ? approvals[0] : null
  if (approval?.state !== 'approved' || approval?.comment !== `APPROVE MAINTENANCE ${expectedDigest}` ||
      approval?.user?.id !== MAINTENANCE_OWNER.id || approval?.user?.login !== MAINTENANCE_OWNER.login ||
      approval?.user?.type !== MAINTENANCE_OWNER.type || approval?.environments?.length !== 1 ||
      approval.environments[0].id !== review?.environment?.id ||
      approval.environments[0].name !== MAINTENANCE_REVIEW_ENVIRONMENT) reasons.push('exact-owner-environment-approval-missing')
  return { decision: reasons.length ? 'REFUSE' : 'PASS', reasons: [...new Set(reasons)] }
}

export function maintenanceReviewSummary(review) {
  const { evidence, preparer } = review.packet
  // Fixed identifiers/URLs and JSON-escaped paths keep candidate filenames out of Markdown instructions.
  const changes = JSON.stringify(evidence.changes, null, 2).replace(/[`<>&]/g, character =>
    `\\u${character.charCodeAt(0).toString(16).padStart(4, '0')}`)
  return [
    '## Owner review required for one authority-maintenance candidate', '',
    `PR #${evidence.pr.number}: ${evidence.pr.head.sha}`,
    `Composed candidate: ${evidence.run.headSha}; tree: ${evidence.candidateTreeSha}`,
    `Protected main/preparer: ${preparer.commitSha}; tree: ${preparer.treeSha}`,
    `Product run: ${evidence.run.runId}, attempt ${evidence.run.runAttempt}`,
    `Binding workflow: ${review.binding.id}, attempt ${review.binding.attempt}`, '',
    `[Complete candidate diff](https://github.com/${MAINTENANCE_REPOSITORY}/compare/${evidence.main.sha}...${evidence.run.headSha})`,
    `[Native Product evidence](https://github.com/${MAINTENANCE_REPOSITORY}/actions/runs/${evidence.run.runId})`, '',
    ...(review.packet.assessment?.kernelChanges?.length
      ? ['**This candidate changes the approval machinery (DEC-142).** The owner is notified after it merges.', '']
      : []),
    'Review the code and native proof before approving the maintenance environment. The exact approval comment is:', '',
    `\`APPROVE MAINTENANCE ${review.digest}\``, '',
    'Approval is revalidated against live evidence. A changed candidate, attempt, main, setting or removed request refuses publication.', '',
    'Protected tree changes (both sides, including modes and removals):', '```json', changes, '```', '',
  ].join('\n')
}

const shaPattern = /^[0-9a-f]{40}$/

// The run summary is readable only on github.com. The same identities and approval line also go to the job log,
// which the REST API serves, so an agent without web access can verify the packet and approve (#1166). Only
// SHAs, integers and the digest are logged: never changed paths or ruleset data, and every line carries a prefix
// so no value can start a runner workflow command.
function maintenanceReviewLogLines(review) {
  const { evidence, preparer } = review?.packet ?? {}
  const shas = [evidence?.pr?.head?.sha, evidence?.run?.headSha, evidence?.candidateTreeSha,
    preparer?.commitSha, preparer?.treeSha]
  const numbers = [evidence?.pr?.number, evidence?.run?.runId, evidence?.run?.runAttempt,
    review?.binding?.id, review?.binding?.attempt]
  if (!shas.every(value => shaPattern.test(value ?? '')) || !numbers.every(positive) ||
      !digestPattern.test(review?.digest ?? '')) throw new Error('Maintenance review identities are not loggable.')
  return [
    'PENDING: exact owner environment approval is required',
    `Review packet: PR #${evidence.pr.number} head ${evidence.pr.head.sha}`,
    `Review packet: composed candidate ${evidence.run.headSha} tree ${evidence.candidateTreeSha}`,
    `Review packet: protected main/preparer ${preparer.commitSha} tree ${preparer.treeSha}`,
    `Review packet: Product run ${evidence.run.runId} attempt ${evidence.run.runAttempt}`,
    `Review packet: binding workflow ${review.binding.id} attempt ${review.binding.attempt}`,
    ...(review.packet.assessment?.kernelChanges?.length ? ['Review packet: changes the approval machinery (DEC-142)'] : []),
    `Approval comment: APPROVE MAINTENANCE ${review.digest}`,
  ].map(line => `[merge-authority] ${line}`)
}

// One call writes the summary, the digest output the publisher verifies against, and the log, so the three
// cannot disagree. Log lines are validated before anything is written.
export function announceMaintenanceReview(review, { appendSummary, appendOutput, log }) {
  const lines = maintenanceReviewLogLines(review)
  appendSummary(maintenanceReviewSummary(review))
  appendOutput(`maintenance-digest=${review.digest}\n`)
  for (const line of lines) log(line)
}
