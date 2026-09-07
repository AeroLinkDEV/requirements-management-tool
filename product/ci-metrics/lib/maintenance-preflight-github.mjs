import { MAINTENANCE_REPOSITORY as repository, MAINTENANCE_RULESET_ID,
  compareProtectedTrees, evaluateMaintenancePreflight, evidenceDigest } from './maintenance-preflight.mjs'
import { fetchDefaultBranch, fetchLatestRunJobs, fetchWorkflowRun } from './merge-authority-github.mjs'

const requireSha = (value, label) => {
  if (typeof value !== 'string' || !/^[0-9a-f]{40}$/.test(value)) throw new Error(`Invalid ${label}.`)
  return value
}

/** Only read functions are injected. No artifact contents, candidate code or credentials enter the bundle. */
export async function collectMaintenancePreflight({ read, graphql, prNumber, runId }) {
  if (!Number.isSafeInteger(prNumber) || prNumber < 1 || !Number.isSafeInteger(runId) || runId < 1) throw new Error('Positive integer PR and run IDs are required.')
  const root = `/repos/${repository}`
  const queueQuery = `query { repository(owner:"AeroLinkDEV",name:"requirements-management-tool") { pullRequest(number:${prNumber}) { number headRefOid mergeQueueEntry { position state headCommit { oid } baseCommit { oid } } } } }`
  const [pr, run, rawRun, jobs, main, ruleset, environment, branchPolicies, queueBody] = await Promise.all([
    read(`${root}/pulls/${prNumber}`),
    fetchWorkflowRun({ request: read, repository, runId }),
    read(`${root}/actions/runs/${runId}`),
    fetchLatestRunJobs({ request: read, repository, runId }),
    fetchDefaultBranch({ request: read, repository }),
    read(`${root}/rulesets/${MAINTENANCE_RULESET_ID}`),
    read(`${root}/environments/merge-authority`),
    read(`${root}/environments/merge-authority/deployment-branch-policies`),
    graphql(queueQuery),
  ])
  requireSha(run.headSha, 'Product candidate SHA')
  if (rawRun.id !== run.runId || rawRun.run_attempt !== run.runAttempt || rawRun.head_sha !== run.headSha ||
      !Number.isSafeInteger(rawRun.check_suite_id) || rawRun.check_suite_id < 1) throw new Error('Run changed while its check-suite identity was read.')
  const [baseCommit, candidateCommit, runsBody, checksBody] = await Promise.all([
    read(`${root}/git/commits/${requireSha(main.sha, 'main SHA')}`),
    read(`${root}/git/commits/${run.headSha}`),
    read(`${root}/actions/workflows/ci.yml/runs?event=merge_group&head_sha=${run.headSha}&per_page=100`),
    read(`${root}/check-suites/${rawRun.check_suite_id}/check-runs?filter=latest&per_page=100`),
  ])
  if (!Array.isArray(runsBody.workflow_runs) || runsBody.total_count !== runsBody.workflow_runs.length ||
      !Array.isArray(checksBody.check_runs) || checksBody.total_count !== checksBody.check_runs.length) throw new Error('Run or check pagination is incomplete.')
  const [baseTree, candidateTree] = await Promise.all([
    read(`${root}/git/trees/${requireSha(baseCommit.tree?.sha, 'base tree SHA')}?recursive=1`),
    read(`${root}/git/trees/${requireSha(candidateCommit.tree?.sha, 'candidate tree SHA')}?recursive=1`),
  ])
  if (baseTree.sha !== baseCommit.tree.sha || candidateTree.sha !== candidateCommit.tree.sha) throw new Error('Git tree response identity mismatch.')
  const changes = compareProtectedTrees(baseTree, candidateTree)
  const qpr = queueBody?.data?.repository?.pullRequest
  if (queueBody?.errors || !qpr || qpr.number !== prNumber) throw new Error('Live queue response is missing or ambiguous.')
  const entry = qpr.mergeQueueEntry
  const evidence = {
    repository, main,
    pr: { number: pr.number, state: pr.state, draft: pr.draft, base: { ref: pr.base?.ref },
      head: { sha: pr.head?.sha, repo: { full_name: pr.head?.repo?.full_name } } },
    queue: entry ? { prNumber: qpr.number, prHeadSha: qpr.headRefOid, position: entry.position, state: entry.state,
      headSha: entry.headCommit?.oid, baseSha: entry.baseCommit?.oid } : null,
    run: { ...run, checkSuiteId: rawRun.check_suite_id }, jobs, ruleset, environment, branchPolicies,
    checks: checksBody.check_runs.map(check => ({ name: check.name, app: { id: check.app?.id }, head_sha: check.head_sha,
      status: check.status, conclusion: check.conclusion, check_suite: { id: check.check_suite?.id } })),
    latestProductRunId: runsBody.workflow_runs.map(candidate => candidate.id).sort((a, b) => b - a)[0] ?? null,
    baseTreeSha: baseTree.sha, candidateTreeSha: candidateTree.sha, changes,
  }
  // Refuse a time-of-check mixture instead of presenting it as one immutable review packet.
  const [currentRun, currentMain, currentPr, currentQueue] = await Promise.all([
    fetchWorkflowRun({ request: read, repository, runId }), fetchDefaultBranch({ request: read, repository }),
    read(`${root}/pulls/${prNumber}`), graphql(queueQuery),
  ])
  if (currentRun.runAttempt !== run.runAttempt || currentRun.status !== run.status || currentRun.headSha !== run.headSha ||
      currentMain.sha !== main.sha || currentPr.head?.sha !== pr.head?.sha || currentPr.state !== pr.state ||
      evidenceDigest(currentQueue) !== evidenceDigest(queueBody)) throw new Error('Evidence advanced during collection; collect a fresh packet.')
  return { schemaVersion: 'aerolink-authority-maintenance-preflight/v1',
    digest: evidenceDigest(evidence), assessment: evaluateMaintenancePreflight(evidence), evidence }
}
