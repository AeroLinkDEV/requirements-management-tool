// Read-only evaluation. No caller of the production skip/authority path imports this module.
import { evaluateMergeGroupCandidate, REQUIRED_JOBS, SHARDED_JOB_GROUPS, AGGREGATE_JOB_NAME, CLASSIFIER_JOB_NAME } from './merge-authority.mjs'
import { deriveEligibility, validateManifest, evidenceAgeRejection, touchesGateDefinition } from './provenance.mjs'
import { aggregateFragments } from './aggregate.mjs'
import { validateFragment } from './fragment.mjs'
import { validateRunRecord } from './rolling.mjs'

export const REUSE_REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
export const REUSE_SCHEMA = 'aerolink-queue-reuse-shadow/v1'
export const AUTHORITY_APP_ID = 4834539
export const ACTIONS_APP_ID = 15368
const sha = (s) => typeof s === 'string' && /^[a-f0-9]{40}$/.test(s)
const countsKeys = ['expected', 'executed', 'passed', 'failed', 'skipped', 'flaky']
const jobMap = (jobs) => new Map(jobs.map(j => [j.name, j]))
const day = 86_400_000

export function requiredNativeNames() {
  return [CLASSIFIER_JOB_NAME, AGGREGATE_JOB_NAME, ...REQUIRED_JOBS,
    ...SHARDED_JOB_GROUPS.flatMap(g => Array.from({ length: g.expectedShards }, (_, i) => `${g.name} (${i + 1}/${g.expectedShards})`))]
}

// Reconcile authenticated effective jobs with actual originating fragments; never splice attempts.
export function reconcileReuseEvidence({ run, tree, jobs, record, manifest, fragments, topology }, now) {
  const errors = []
  const refuse = (condition, reason) => { if (!condition) errors.push(reason) }
  refuse(validateManifest(manifest).length === 0, 'manifest-schema')
  refuse(validateRunRecord(record).length === 0, 'run-record-schema')
  const expectedRef = `refs/heads/${run.head_branch}`
  for (const identity of [record?.run, ...fragments.map(f => f.run)]) {
    refuse(identity?.id === run.id && identity?.repository === REUSE_REPOSITORY && identity?.sha === run.head_sha &&
      identity?.tree === tree && identity?.event === run.event && identity?.workflow === 'Product quality gate' &&
      identity?.workflowRef === `${REUSE_REPOSITORY}/.github/workflows/ci.yml@${expectedRef}` && identity?.ref === expectedRef,
    'artifact-run-tree-workflow-binding')
  }
  refuse(record?.run?.attempt === run.run_attempt && manifest?.run?.attempt === run.run_attempt &&
    manifest?.run?.id === run.id && manifest?.run?.event === run.event && manifest?.event === run.event &&
    manifest?.repository === REUSE_REPOSITORY && manifest?.workflow === 'Product quality gate' &&
    manifest?.workflowRef === `${REUSE_REPOSITORY}/.github/workflows/ci.yml@${expectedRef}` &&
    manifest?.checkedOut?.commitSha === run.head_sha && manifest?.checkedOut?.treeSha === tree &&
    manifest?.checkedOut?.ref === expectedRef, 'manifest-binding')
  refuse(!evidenceAgeRejection(manifest, now), 'stale-or-invalid-manifest-time')
  const eligible = deriveEligibility(manifest)
  refuse(eligible.eligible && manifest?.canAuthorizePostMergeSkip === eligible.eligible, 'shared-eligibility-refusal')
  const byName = jobMap(jobs)
  refuse(byName.size === jobs.length, 'duplicate-native-job-name')
  refuse(new Set(fragments.map(f => `${f.job?.instance}:${f.run?.attempt}`)).size === fragments.length, 'duplicate-fragment')
  for (const fragment of fragments) {
    try { validateFragment(fragment) } catch { errors.push('invalid-fragment'); continue }
    const native = byName.get(fragment.job.name)
    refuse(native?.run_id === run.id && native?.head_sha === run.head_sha && native?.status === 'completed' &&
      native?.conclusion === fragment.job.result && native?.run_attempt === fragment.run.attempt &&
      native.run_attempt <= run.run_attempt, `fragment-origin:${fragment.job.instance}`)
  }
  if (errors.length) return { errors: [...new Set(errors)], merged: null }
  const merged = aggregateFragments({ fragments, runMeta: topology })
  refuse(merged.missingTotal === 0 && merged.missing.length === 0 && merged.criticalPath.unavailableReason === null, 'incomplete-fragments-or-topology')
  refuse(new Set(record.jobs.map(j => j.instance)).size === record.jobs.length && record.jobs.length === merged.jobs.length, 'record-job-union')
  for (const job of merged.jobs) {
    const original = record.jobs.find(j => j.instance === job.instance)
    refuse(original?.sourceAttempt === job.sourceAttempt && original?.result === job.result &&
      countsKeys.every(k => original?.counts?.[k] === job.counts[k]), `record-fragment-reconciliation:${job.instance}`)
  }
  refuse(countsKeys.every(k => merged.counts[k] === record.counts?.[k] && merged.counts[k] === manifest.verifiedTotals?.[k]), 'count-reconciliation')
  refuse(record.missingTotal === 0 && record.missing?.length === 0, 'record-missing-evidence')
  const selected = manifest.gates?.selected
  refuse(Array.isArray(selected) && selected.length === merged.jobs.length && new Set(selected.map(j => j.instance)).size === selected.length &&
    merged.jobs.every(j => selected.some(s => s.instance === j.instance && s.result === j.result)), 'manifest-selected-union')
  return { errors: [...new Set(errors)], merged }
}

export function evaluateQueueReuseShadow(packet, { now = Date.now(), source = 'unverified-fixture' } = {}) {
  const conditions = []
  const check = (name, ok, reason = name, refusal = 'insufficient_evidence') => {
    conditions.push({ name, passed: ok === true, reason: ok === true ? null : reason, refusal })
  }
  const { run = {}, landed = {}, pr = {}, candidate = {}, composition = {}, workflow = {}, checks = [], jobs = [],
    changedPaths, protectedChanges, evidence, fallback, latestRun, main, mainRelationship, prReadiness, collectionErrors = [] } = packet ?? {}
  check('collection', collectionErrors.length === 0, collectionErrors.join('; '))
  check('repository-target', run.repository?.full_name === REUSE_REPOSITORY && pr.base?.repo?.full_name === REUSE_REPOSITORY &&
    pr.head?.repo?.full_name === REUSE_REPOSITORY && pr.base?.ref === 'main', 'wrong repository or protected target', 'must_retest')
  check('landed-pr', pr.merged === true && pr.state === 'closed' && pr.merge_commit_sha === landed.sha && sha(landed.sha) &&
    sha(landed.tree?.sha), 'unproven landed PR', 'must_retest')
  check('protected-main-ancestry', main?.name === 'main' && main?.protected === true &&
    ['identical', 'ahead'].includes(mainRelationship?.status) && mainRelationship?.merge_base_commit?.sha === landed.sha,
  'landed commit is not proven on protected main', 'must_retest')
  check('pr-head-readiness', prReadiness?.binding?.app?.id === AUTHORITY_APP_ID &&
    prReadiness?.binding?.head_sha === pr.head?.sha && prReadiness?.binding?.conclusion === 'success' &&
    prReadiness?.binding?.status === 'completed' && Date.parse(prReadiness.binding.completed_at) <= Date.parse(run.created_at) &&
    prReadiness?.run?.repository?.full_name === REUSE_REPOSITORY && prReadiness?.run?.head_sha === pr.head?.sha &&
    prReadiness?.run?.path === '.github/workflows/request-full-ci.yml' && prReadiness?.run?.event === 'pull_request_target' &&
    prReadiness?.run?.status === 'completed' && prReadiness?.run?.conclusion === 'success', 'exact PR-head readiness binding is unavailable')
  check('native-queue', run.event === 'merge_group' && run.status === 'completed' && run.head_sha === candidate.sha &&
    run.head_branch?.startsWith('gh-readonly-queue/main/'), 'not completed native queue evidence', 'must_retest')
  check('composition', candidate.sha === landed.sha && candidate.tree?.sha === landed.tree?.sha &&
    candidate.parents?.length === 1 && candidate.parents[0].sha === composition.baseSha &&
    composition.prHeadSha === pr.head?.sha && composition.treeSha === candidate.tree?.sha &&
    composition.method === 'git-merge-tree' && composition.associatedPrs?.length === 1 && composition.associatedPrs[0] === pr.number,
  'only proven single-PR candidate-equals-landed composition is supported')
  check('protected-definition', Array.isArray(changedPaths) && Array.isArray(protectedChanges) &&
    !touchesGateDefinition(changedPaths) && protectedChanges.length === 0, 'gate or protected definition changed or unavailable', 'must_retest')
  check('workflow-identity', workflow.id === run.workflow_id && workflow.path === '.github/workflows/ci.yml' &&
    run.path === workflow.path && run.name === 'Product quality gate', 'wrong native workflow identity', 'must_retest')
  check('current-attempt', latestRun?.id === run.id && latestRun?.head_sha === run.head_sha &&
    latestRun?.run_attempt === run.run_attempt && latestRun?.status === 'completed' && latestRun?.conclusion === run.conclusion,
  'newer, active or changed attempt supersedes evidence')
  const native = evaluateMergeGroupCandidate({ run: { repository: run.repository?.full_name, workflowName: run.name,
    workflowPath: run.path, event: run.event, headSha: run.head_sha, headBranch: run.head_branch, runId: run.id,
    runAttempt: run.run_attempt, status: run.status }, jobs: jobs.map(j => ({ name: j.name, conclusion: j.conclusion,
    runId: j.run_id, runAttempt: j.run_attempt })), changedPaths: protectedChanges,
  expected: { repository: REUSE_REPOSITORY, baseBranch: 'main', headSha: candidate.sha, runId: run.id, runAttempt: run.run_attempt } })
  check('required-native-jobs', native.decision === 'PASS', native.reasons.join('; '), 'must_retest')
  check('native-execution-age', jobs.filter(j => requiredNativeNames().includes(j.name)).every(j =>
    Number.isFinite(Date.parse(j.completed_at)) && now - Date.parse(j.completed_at) <= 30 * day &&
    now >= Date.parse(j.completed_at) - 600_000), 'required originating execution is stale or has invalid time')
  // Older in-progress invalidations are retained as separate checks. Only the newest check can bind.
  const bound = checks.filter(c => c.name === 'Trusted merge-queue binding').sort((a, b) => b.id - a.id).slice(0, 1)
  check('pinned-binding', bound.length === 1 && bound[0].app?.id === AUTHORITY_APP_ID && bound[0].head_sha === candidate.sha &&
    bound[0].status === 'completed' && bound[0].conclusion === 'success' &&
    bound[0].details_url === `https://github.com/${REUSE_REPOSITORY}/actions/runs/${run.id}` &&
    Date.parse(bound[0].completed_at) >= Math.max(...jobs.filter(j => requiredNativeNames().includes(j.name)).map(j => Date.parse(j.completed_at))),
  'missing, wrong-publisher, stale or unrelated App binding')
  for (const name of requiredNativeNames()) {
    const nativeJob = jobs.find(j => j.name === name)
    const rows = checks.filter(c => c.name === name && c.check_suite?.id === run.check_suite_id &&
      nativeJob?.check_run_url === `https://api.github.com/repos/${REUSE_REPOSITORY}/check-runs/${c.id}`)
    check(`native-publisher:${name}`, rows.length === 1 && rows[0].app?.id === ACTIONS_APP_ID && rows[0].head_sha === candidate.sha &&
      rows[0].status === 'completed' && rows[0].conclusion === 'success', 'required native publisher/check missing', 'must_retest')
  }
  let reconciled = null
  try {
    reconciled = reconcileReuseEvidence({ run, tree: candidate.tree?.sha, jobs, ...evidence }, now)
    check('reconciled-evidence', reconciled.errors.length === 0, reconciled.errors.join('; '))
  } catch { check('reconciled-evidence', false, 'malformed or unavailable artifact evidence') }
  check('fresh-native-time', Number.isFinite(Date.parse(run.updated_at)) && now - Date.parse(run.updated_at) <= 30 * day &&
    now >= Date.parse(run.updated_at) - 600_000 && now >= Date.parse(run.created_at) - 600_000, 'native execution is stale or has invalid dates')
  check('fallback-proof', fallback?.passed === true && fallback?.protectedDefinitionMatches === true &&
    fallback?.currentAttempt === true && fallback?.run?.repository?.full_name === REUSE_REPOSITORY &&
    ['schedule', 'workflow_dispatch'].includes(fallback?.run?.event) && fallback?.run?.head_branch === 'main' &&
    now - Date.parse(fallback?.run?.updated_at) <= 30 * day && now >= Date.parse(fallback?.run?.updated_at),
  fallback?.reason ?? 'fresh complete main scheduled/manual fallback is unavailable')
  const failed = conditions.filter(c => !c.passed)
  const outcome = failed.some(c => c.refusal === 'must_retest') ? 'must_retest' : failed.length ? 'insufficient_evidence' : 'would_reuse'
  const avoidableGroups = ['backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'client', 'script-contracts', 'postgresql-smoke']
  const avoidableNames = reconciled?.merged?.jobs?.filter(j => avoidableGroups.includes(j.group)).map(j => j.name) ?? []
  const estimatedMs = jobs.filter(j => avoidableNames.includes(j.name)).reduce((sum, j) => sum + Math.max(0, Date.parse(j.completed_at) - Date.parse(j.started_at)), 0)
  return { schemaVersion: REUSE_SCHEMA, mode: 'shadow-only', sourceAuthenticity: source, outcome, canSkip: false,
    executionSelectorChanged: false, adoptionEligible: false, conditions, run: { id: run.id, attempt: run.run_attempt, sha: run.head_sha,
      status: run.status, conclusion: run.conclusion },
    nonAuthoritativeOutcomes: jobs.filter(j => ['CI metrics tooling tests', 'Aggregate CI metrics'].includes(j.name))
      .map(j => ({ jobId: j.id, name: j.name, status: j.status, conclusion: j.conclusion })),
    composition, landed: { sha: landed.sha, tree: landed.tree?.sha }, fallback: fallback ? { runId: fallback.run?.id, passed: fallback.passed, reason: fallback.reason } : null,
    reconciledCounts: reconciled?.errors?.length === 0 ? reconciled.merged.counts : null,
    reviewBoundary: 'The observer binds the exact PR head through protected readiness; it does not certify independence or truth of review comments.',
    originatingExecutions: jobs.filter(j => requiredNativeNames().includes(j.name)).map(j => ({ jobId: j.id, name: j.name, runId: j.run_id, attempt: j.run_attempt })),
    mandatoryWork: 'All existing main-push jobs remain mandatory under their unchanged production conditions. This observer grants no skip authority.',
    potentiallyAvoidable: { estimated: true, deliveredRunnerMinutes: 0, queueJobProxyRunnerMinutes: Number.isFinite(estimatedMs) ? estimatedMs / 60_000 : null,
      limitation: 'Queue job time is only a proxy for potentially avoidable main work; it is not delivered savings or a complete historical compute ledger.' },
    laterEnforcementRequirements: ['separate owner decision and independent integration review', 'representative reliability and performance evidence',
      'supported durable composition and fallback proofs', 'preserve independent gate-definition validation and all existing refusals'] }
}

export function renderQueueReuseShadow(report) {
  return ['# Queue-to-main reuse shadow', '', `Outcome: **${report.outcome}**. Source: ${report.sourceAuthenticity}. Actual skip authority: **false**.`,
    `Run ${report.run.id}, attempt ${report.run.attempt}; status ${report.run.status}; overall conclusion **${report.run.conclusion}**; candidate ${report.run.sha}; landed ${report.landed.sha}.`, '',
    `Composition: PR head ${report.composition.prHeadSha ?? 'unavailable'} plus base ${report.composition.baseSha ?? 'unavailable'}; tree ${report.landed.tree ?? 'unavailable'}.`,
    `Fallback run: ${report.fallback?.runId ?? 'unavailable'}; qualified: ${report.fallback?.passed === true}.`,
    `Reconciled test counts: ${report.reconciledCounts ? countsKeys.map(k => `${k}=${report.reconciledCounts[k]}`).join(', ') : 'unavailable'}.`, '',
    'Non-authoritative reporting outcomes (these do not replace required Product proof):',
    ...report.nonAuthoritativeOutcomes.slice(0, 10).map(j => `- ${j.name}: ${j.status}, ${j.conclusion ?? 'unavailable'} (job ${j.jobId}).`), '',
    '| Condition | Result | Reason |', '|---|---|---|', ...report.conditions.map(c => `| ${c.name} | ${c.passed ? 'pass' : 'refuse'} | ${(c.reason ?? '').replace(/[|\r\n]/g, ' ')} |`),
    '', report.mandatoryWork, '', `Estimated queue-job proxy: ${report.potentiallyAvoidable.queueJobProxyRunnerMinutes ?? 'unavailable'} runner-minutes. Delivered savings: 0.`,
    report.potentiallyAvoidable.limitation, '', `Observer overhead: ${report.observerMs ?? 'unavailable'} ms.`, ''].join('\n')
}
