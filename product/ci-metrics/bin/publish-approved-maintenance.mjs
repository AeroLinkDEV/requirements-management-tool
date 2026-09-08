// Runs only after the separate owner-review job, still inside the existing main-only App environment.
import { readFileSync } from 'node:fs'
import { createGitHubRequest, publishMergeAuthorityCheck } from '../lib/merge-authority-github.mjs'
import { publishApprovedMaintenance } from '../lib/maintenance-approval-github.mjs'
import { trustedMaintenanceContext } from '../lib/maintenance-runtime.mjs'
import { createMaintenanceRulesetReader } from '../lib/maintenance-evidence-reader.mjs'
import { MAINTENANCE_REPOSITORY as repository } from '../lib/maintenance-preflight.mjs'

async function main() {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'))
  const context = trustedMaintenanceContext(event)
  const request = createGitHubRequest({ token: process.env.GITHUB_TOKEN })
  const authority = createGitHubRequest({ token: process.env.MERGE_AUTHORITY_TOKEN })
  const rulesetReader = createMaintenanceRulesetReader({
    token: process.env.MAINTENANCE_EVIDENCE_TOKEN,
    expectedAppId: Number(process.env.MAINTENANCE_EVIDENCE_APP_ID),
    expectedInstallationId: Number(process.env.MAINTENANCE_EVIDENCE_INSTALLATION_ID),
    expectedAppSlug: process.env.MAINTENANCE_EVIDENCE_APP_SLUG,
    actionAppSlug: process.env.MAINTENANCE_EVIDENCE_ACTION_APP_SLUG,
    actionInstallationId: Number(process.env.MAINTENANCE_EVIDENCE_ACTION_INSTALLATION_ID),
    apiUrl: process.env.GITHUB_API_URL || 'https://api.github.com',
  })
  const headSha = context.expectedProduct.headSha
  const detailsUrl = `https://github.com/${repository}/actions/runs/${context.bindingRunId}`
  const result = await publishApprovedMaintenance({ ...context, expectedDigest: process.env.MAINTENANCE_REVIEW_DIGEST,
    read: request, rulesetReader, graphql: query => request('/graphql', { method: 'POST', body: { query } }),
    publish: decision => publishMergeAuthorityCheck({ request: authority, repository, headSha, detailsUrl, ...decision }) })
  console.log(`[merge-authority-maintenance] ${result.decision}: ${result.reasons.join('; ') || 'exact owner approval and current native evidence verified'}`)
  if (result.decision !== 'PASS') process.exitCode = 1
}

main().catch(() => {
  // Do not print raw child process output, API payloads, inherited tokens, or candidate-controlled text.
  console.error('[merge-authority-maintenance] Failed closed; no maintenance authority was granted.')
  process.exitCode = 1
})
