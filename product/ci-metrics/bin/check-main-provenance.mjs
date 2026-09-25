// Trusted provenance checker: shadow audit by workflow_run and fail-safe enforcement on main push.
//
// For a main push it resolves the merged pull request and asks GitHub whether the merge queue already
// proved that exact commit: a successful merge_group Product run on the pushed SHA, and the Merge
// Authority App's binding check on the same SHA (#1147, #1152 A1). The earlier lookup (#562) searched
// for `pull_request` Product runs, which have not existed since #561, so it could never match.

import { readFileSync, writeFileSync, appendFileSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'
import {
  decideQueueProvenance, collectMergedPaths, GATE_DEFINING_PATHS, normalizeProvenanceTrigger, applyProvenanceMode,
  QUEUE_BINDING_CHECK_NAME,
} from '../lib/provenance.mjs'

const env = (name) => process.env[name] ?? ''

async function api(path, { token, apiUrl } = {}) {
  const response = await fetch(`${apiUrl}${path}`, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  })
  if (!response.ok) throw new Error(`GitHub API ${path} returned ${response.status}.`)
  return response.json()
}

async function listAll(path, { token, apiUrl } = {}) {
  const items = []
  let page = 1
  while (true) {
    const body = await api(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`, { token, apiUrl })
    const rows = Array.isArray(body) ? body : body.items ?? body.workflow_runs ?? body.artifacts ?? []
    items.push(...rows)
    if (rows.length < 100) break
    page += 1
    if (page > 5) break
  }
  return items
}

async function fetchTree(sha, { token, apiUrl, repository }) {
  const body = await api(`/repos/${repository}/git/commits/${sha}`, { token, apiUrl })
  return body.tree?.sha ?? null
}

/**
 * The paths this merge introduced, from GitHub's own view of the pull request rather than from
 * anything the branch supplied. Used only to decide whether the merge edits the gate's own definition.
 *
 * The logic lives in `collectMergedPaths` so it can be tested against a fake API; this only binds it to
 * the real one. It deliberately does not use `listAll`, which stops after five pages and returns what
 * it has with no way for a caller to tell a complete list from a truncated one.
 */
async function fetchMergedPaths(prNumber, { token, apiUrl, repository }) {
  return collectMergedPaths({
    prNumber,
    api: (path) => api(`/repos/${repository}${path}`, { token, apiUrl }),
  })
}

function escapeMarkdown(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\|/g, '\\|')
    .replace(/\r?\n/g, ' ')
}

async function main() {
  const token = env('GITHUB_TOKEN')
  const apiUrl = env('GITHUB_API_URL') || 'https://api.github.com'
  const repository = env('GITHUB_REPOSITORY')
  const eventPath = env('GITHUB_EVENT_PATH')
  const outputDir = env('PROVENANCE_OUTPUT_DIR')
  if (!token || !repository || !eventPath || !outputDir) {
    console.error('[ci-metrics] GITHUB_TOKEN, GITHUB_REPOSITORY, GITHUB_EVENT_PATH, and PROVENANCE_OUTPUT_DIR are required.')
    process.exit(2)
  }
  const event = JSON.parse(readFileSync(eventPath, 'utf8'))
  const mode = env('PROVENANCE_MODE') === 'enforce' ? 'enforce' : 'shadow'
  const run = normalizeProvenanceTrigger({ event, eventName: env('GITHUB_EVENT_NAME'), runId: env('GITHUB_RUN_ID'), sha: env('GITHUB_SHA') })
  const pushSha = run?.head_sha ?? null
  const isMainPush = run?.event === 'push' && run?.head_branch === 'main'

  let result
  if (!isMainPush) {
    result = {
      schemaVersion: 'aerolink-main-provenance/v2',
      mode,
      triggeringRun: { id: run?.id ?? null, event: run?.event ?? null, branch: run?.head_branch ?? null },
      outcome: 'not-applicable',
      reason: 'Only main push quality-gate runs are provenance candidates.',
      canSkip: false,
    }
  } else {
    const pushTree = await fetchTree(pushSha, { token, apiUrl, repository })
    const closedPrs = await listAll(`/repos/${repository}/pulls?state=closed&sort=updated&direction=desc`, { token, apiUrl })
    const mergedPr = closedPrs.find((pr) => pr.merged_at && pr.merge_commit_sha === pushSha && pr.head?.ref) ?? null
    // GitHub's own records, read with the workflow token. Nothing here comes from an artifact the tested
    // run wrote about itself.
    let queueRuns = []
    let aggregateJobs = []
    let bindingChecks = []
    if (mergedPr) {
      const runsBody = await api(`/repos/${repository}/actions/workflows/ci.yml/runs?event=merge_group&head_sha=${pushSha}&per_page=100`, { token, apiUrl })
      queueRuns = Array.isArray(runsBody?.workflow_runs) ? runsBody.workflow_runs : []
      const newest = queueRuns
        .filter((candidate) => candidate.head_sha === pushSha && candidate.conclusion === 'success')
        .sort((a, b) => b.id - a.id)[0]
      if (newest) {
        const jobsBody = await api(`/repos/${repository}/actions/runs/${newest.id}/jobs?filter=latest&per_page=100`, { token, apiUrl })
        aggregateJobs = Array.isArray(jobsBody?.jobs) ? jobsBody.jobs : []
      }
      const checksBody = await api(`/repos/${repository}/commits/${pushSha}/check-runs?check_name=${encodeURIComponent(QUEUE_BINDING_CHECK_NAME)}&filter=all&per_page=100`, { token, apiUrl })
      bindingChecks = Array.isArray(checksBody?.check_runs) ? checksBody.check_runs : []
    }
    // Fail closed: if GitHub will not tell us what the merge changed, we cannot rule out that it
    // changed the gate itself, so the decision must be the same as if it had.
    let changedPaths = null
    let changedPathsError = null
    try {
      changedPaths = await fetchMergedPaths(mergedPr.number, { token, apiUrl, repository })
    } catch (error) {
      changedPathsError = error.message
      changedPaths = [...GATE_DEFINING_PATHS]
    }
    const decision = applyProvenanceMode(decideQueueProvenance({
      pushSha,
      mergedPr,
      changedPaths,
      queueRuns,
      aggregateJobs,
      bindingChecks,
      now: Date.now(),
    }), mode)
    result = {
      schemaVersion: 'aerolink-main-provenance/v2',
      mode,
      triggeringRun: { id: run?.id ?? null, event: run?.event ?? null, branch: run?.head_branch ?? null },
      push: { commitSha: pushSha, treeSha: pushTree },
      mergedPr: mergedPr ? { number: mergedPr.number, mergedAt: mergedPr.merged_at, headRef: mergedPr.head.ref } : null,
      queueRunsFound: queueRuns.length,
      bindingChecksFound: bindingChecks.length,
      outcome: decision.outcome,
      canSkip: decision.canSkip,
      reason: decision.reason,
      source: decision.source ?? null,
      selfModifying: decision.selfModifying === true,
      changedPathsUnavailable: changedPathsError,
    }
  }

  const lines = []
  lines.push(`# Main-push provenance check (${escapeMarkdown(result.mode)})`)
  lines.push('')
  lines.push(result.mode === 'enforce'
    ? '- Mode: enforce (a commit the merge queue already proved may skip the redundant post-merge product retest)'
    : '- Mode: shadow (observation only; the post-merge gate still runs)')
  lines.push(`- Outcome: ${escapeMarkdown(result.outcome)}`)
  if (result.push) lines.push(`- Pushed commit: \`${escapeMarkdown(result.push.commitSha)}\` (tree \`${escapeMarkdown(result.push.treeSha)}\`)`)
  if (result.source) lines.push(`- Proved by the merge-queue candidate for PR #${result.source.pr}: run ${result.source.runId} attempt ${result.source.attempt}, binding check ${result.source.bindingCheckId}`)
  if (result.reason) lines.push(`- Reason: ${escapeMarkdown(result.reason)}`)
  if (result.selfModifying) lines.push('- This merge changed the gate\'s own definition, so main validates it once independently regardless of tree match.')
  if (result.changedPathsUnavailable) {
    lines.push(`- The merge's changed-file list could not be read (${escapeMarkdown(result.changedPathsUnavailable)}); treated as gate-defining and sent to fallback.`)
  }
  if (result.outcome === 'provenanced-match') {
    lines.push(result.canSkip
      ? '- Exact queue-proved commit: backend-api, backend-core-domain, backend-core-infrastructure, client, script-contracts, and postgresql-smoke may skip; lightweight cache warming remains.'
      : '- Would skip under enforcement: backend-api, backend-core-domain, backend-core-infrastructure, client, script-contracts, postgresql-smoke (lightweight cache warming would remain).')
  }
  result.markdown = lines.join('\n')

  mkdirSync(outputDir, { recursive: true })
  writeFileSync(join(outputDir, 'main-provenance.json'), `${JSON.stringify(result, null, 2)}\n`, 'utf8')
  writeFileSync(join(outputDir, 'main-provenance.md'), `${result.markdown}\n`, 'utf8')
  const githubOutput = env('GITHUB_OUTPUT')
  if (githubOutput) {
    appendFileSync(githubOutput, `can_skip=${result.canSkip ? 'true' : 'false'}\noutcome=${result.outcome}\n`, 'utf8')
  }
  console.log(`[ci-metrics] Provenance check: ${result.outcome} (mode=${result.mode}, canSkip=${result.canSkip}).`)
}

main().catch((error) => {
  console.error(`[ci-metrics] Provenance check failed: ${error.message}`)
  process.exit(1)
})
