// Runs only after the separate owner-review job, still inside the existing main-only App environment.
import { readFileSync } from 'node:fs'
import { createGitHubRequest, fetchWorkflowRun, publishMergeAuthorityCheck } from '../lib/merge-authority-github.mjs'
import { verifyApprovedMaintenance } from '../lib/maintenance-approval-github.mjs'
import { trustedMaintenanceContext } from '../lib/maintenance-runtime.mjs'
import { MAINTENANCE_REPOSITORY as repository } from '../lib/maintenance-preflight.mjs'

async function main() {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'))
  const context = trustedMaintenanceContext(event)
  const request = createGitHubRequest({ token: process.env.GITHUB_TOKEN })
  const authority = createGitHubRequest({ token: process.env.MERGE_AUTHORITY_TOKEN })
  const headSha = context.expectedProduct.headSha
  const detailsUrl = `https://github.com/${repository}/actions/runs/${context.bindingRunId}`
  await publishMergeAuthorityCheck({ request: authority, repository, headSha, decision: 'PENDING', reasons: [], detailsUrl })
  const result = await verifyApprovedMaintenance({ ...context, expectedDigest: process.env.MAINTENANCE_REVIEW_DIGEST,
    read: request, graphql: query => request('/graphql', { method: 'POST', body: { query } }) })
  const current = await fetchWorkflowRun({ request, repository, runId: context.runId })
  if (current.status !== 'completed' || current.runAttempt !== context.expectedProduct.runAttempt || current.headSha !== headSha) {
    throw new Error('Product attempt advanced immediately before publication.')
  }
  await publishMergeAuthorityCheck({ request: authority, repository, headSha,
    decision: result.decision, reasons: result.reasons, detailsUrl })
  console.log(`[merge-authority-maintenance] ${result.decision}: ${result.reasons.join('; ') || 'exact owner approval and current native evidence verified'}`)
  if (result.decision !== 'PASS') process.exitCode = 1
}

main().catch(() => {
  // Do not print raw child process output, API payloads, inherited tokens, or candidate-controlled text.
  console.error('[merge-authority-maintenance] Failed closed; no maintenance authority was granted.')
  process.exitCode = 1
})
