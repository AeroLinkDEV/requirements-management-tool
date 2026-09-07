// Read-only preparation for an owner-reviewed authority maintenance window.
// REVIEW_REQUIRED is an evidence bundle, never permission to publish a check or merge.
import { createHash } from 'node:crypto'
import { evaluateMergeGroupCandidate, TRUSTED_SURFACE_PREFIXES } from './merge-authority.mjs'

export const MAINTENANCE_REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
export const MAINTENANCE_RULESET_ID = 22306102
export const MAINTENANCE_KERNEL_PATHS = [
  '.github/workflows/merge-queue-binding.yml',
  '.github/workflows/request-full-ci.yml',
  '.github/workflows/reset-full-ci-readiness.yml',
  'product/ci-metrics/lib/merge-authority.mjs',
  'product/ci-metrics/lib/merge-authority-github.mjs',
  'product/ci-metrics/bin/verify-merge-authority.mjs',
]
const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value)
const positive = value => Number.isSafeInteger(value) && value > 0
const protectedPath = path => TRUSTED_SURFACE_PREFIXES.some(prefix => path.startsWith(prefix))
const kernelPath = path => MAINTENANCE_KERNEL_PATHS.includes(path) ||
  /^product\/ci-metrics\/(?:lib|bin)\/.*(?:maintenance|merge-authority)/.test(path) ||
  /^\.github\/workflows\/.*maintenance/.test(path)

/** Stable encoding binds the complete snapshot, including negative evidence and job identities. */
export function canonicalJson(value) {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`
  if (value && typeof value === 'object') {
    return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`
  }
  if (value === null || ['string', 'boolean'].includes(typeof value) ||
      (typeof value === 'number' && Number.isFinite(value))) return JSON.stringify(value)
  throw new Error('Evidence contains a value that cannot be canonically encoded.')
}

export function evidenceDigest(value) {
  return createHash('sha256').update(canonicalJson(value)).digest('hex')
}

/** Full root trees are required. A rename appears as a removal and an addition, preserving both sides. */
export function compareProtectedTrees(base, candidate) {
  function index(tree) {
    if (!tree || tree.truncated !== false || !sha(tree.sha) || !Array.isArray(tree.tree)) {
      throw new Error('A complete, untruncated Git tree is required.')
    }
    const entries = new Map()
    for (const entry of tree.tree) {
      if (typeof entry?.path !== 'string' || !entry.path || entry.path.startsWith('/') ||
          entry.path.includes('\\') || entry.path.split('/').some(part => !part || part === '.' || part === '..') ||
          !sha(entry.sha) || !['blob', 'tree', 'commit'].includes(entry.type) ||
          !/^[0-7]{6}$/.test(entry.mode) || entries.has(entry.path)) {
        throw new Error('Git tree contains an invalid or duplicate entry.')
      }
      entries.set(entry.path, { sha: entry.sha, mode: entry.mode, type: entry.type })
    }
    // These are existing trusted directories. A missing root must not become an empty comparison.
    for (const prefix of TRUSTED_SURFACE_PREFIXES) {
      if (entries.get(prefix.slice(0, -1))?.type !== 'tree') throw new Error(`Trusted directory missing: ${prefix}`)
    }
    return entries
  }
  const before = index(base)
  const after = index(candidate)
  return [...new Set([...before.keys(), ...after.keys()])].sort()
    .filter(path => protectedPath(path) && canonicalJson(before.get(path) ?? null) !== canonicalJson(after.get(path) ?? null))
    // Directory identities are retained too: mode changes and replacement with blobs cannot disappear.
    .map(path => ({ path, before: before.get(path) ?? null, after: after.get(path) ?? null }))
}

export function evaluateMaintenancePreflight(evidence) {
  const reasons = []
  const { repository, main, pr, queue, run, jobs, ruleset, environment, branchPolicies, checks, changes,
    candidateTreeSha, baseTreeSha, latestProductRunId } = evidence ?? {}
  if (repository !== MAINTENANCE_REPOSITORY) reasons.push('wrong-repository')
  if (main?.name !== 'main' || !sha(main?.sha) || !sha(candidateTreeSha) || !sha(baseTreeSha)) reasons.push('invalid-main-or-tree-identity')
  if (!positive(pr?.number) || pr?.state !== 'open' || pr?.draft !== false ||
      pr?.base?.ref !== 'main' || pr?.head?.repo?.full_name !== MAINTENANCE_REPOSITORY ||
      !sha(pr?.head?.sha)) reasons.push('pull-request-not-ready')
  if (!queue || queue.position !== 1 || queue.headSha !== run?.headSha || queue.baseSha !== main?.sha ||
      queue.prHeadSha !== pr?.head?.sha || queue.prNumber !== pr?.number ||
      !['AWAITING_CHECKS', 'MERGEABLE'].includes(queue.state)) reasons.push('not-current-single-pr-composition')
  if (String(latestProductRunId) !== String(run?.runId)) reasons.push('newer-or-unverified-product-run')
  if (!Array.isArray(changes) || changes.length === 0 || changes.some(change =>
    typeof change?.path !== 'string' || !protectedPath(change.path))) reasons.push('protected-diff-missing-or-malformed')
  const kernelChanges = Array.isArray(changes) ? changes.filter(change => typeof change?.path === 'string' && kernelPath(change.path)).map(change => change.path) : []
  if (kernelChanges.length) reasons.push('separate-trust-root-bootstrap-required')

  // Keep the existing verifier's refusal reasons. Only its explicit protected-surface refusal is
  // potentially reviewable; missing jobs, failed jobs and all other refusals still stop preparation.
  const ordinaryDecision = evaluateMergeGroupCandidate({
    run, jobs, changedPaths: Array.isArray(changes) ? changes.map(change => change.path) : undefined,
    expected: { repository: MAINTENANCE_REPOSITORY, headSha: run?.headSha, baseBranch: 'main', runId: run?.runId, runAttempt: run?.runAttempt },
  })
  for (const reason of ordinaryDecision.reasons) {
    if (!reason.startsWith('trusted-surface-modified:')) reasons.push(reason)
  }
  if (!ordinaryDecision.reasons.some(reason => reason.startsWith('trusted-surface-modified:'))) reasons.push('not-an-authority-maintenance-candidate')

  const statusRules = ruleset?.rules?.filter(rule => rule.type === 'required_status_checks') ?? []
  const expectedChecks = [
    { context: 'Full Product evidence aggregate', integration_id: 15368 },
    { context: 'Trusted merge-queue binding', integration_id: 4834539 },
  ]
  const configuredChecks = statusRules[0]?.parameters?.required_status_checks
  const structuralRulesPresent = ['pull_request', 'deletion', 'non_fast_forward'].every(type =>
    ruleset?.rules?.filter(rule => rule.type === type).length === 1)
  if (ruleset?.id !== MAINTENANCE_RULESET_ID || ruleset?.enforcement !== 'active' ||
      ruleset?.target !== 'branch' ||
      canonicalJson(ruleset?.conditions?.ref_name ?? null) !== canonicalJson({ include: ['~DEFAULT_BRANCH'], exclude: [] }) ||
      !Array.isArray(ruleset?.bypass_actors) || ruleset.bypass_actors.length !== 0 ||
      !structuralRulesPresent || statusRules.length !== 1 || !Array.isArray(configuredChecks) ||
      canonicalJson([...configuredChecks].sort((a, b) => a.context.localeCompare(b.context))) !== canonicalJson(expectedChecks)) reasons.push('required-publishers-or-protection-changed')
  const queueRules = ruleset?.rules?.filter(rule => rule.type === 'merge_queue') ?? []
  if (queueRules.length !== 1 || queueRules[0].parameters?.grouping_strategy !== 'ALLGREEN' ||
      queueRules[0].parameters?.max_entries_to_merge !== 1 || queueRules[0].parameters?.merge_method !== 'SQUASH') reasons.push('queue-policy-changed')
  if (environment?.name !== 'merge-authority' || environment?.deployment_branch_policy?.custom_branch_policies !== true ||
      environment?.deployment_branch_policy?.protected_branches !== false || branchPolicies?.total_count !== 1 ||
      branchPolicies?.branch_policies?.length !== 1 || branchPolicies.branch_policies[0].name !== 'main' ||
      branchPolicies.branch_policies[0].type !== 'branch') reasons.push('app-secret-environment-not-main-only')

  const nativeGates = Array.isArray(checks) ? checks.filter(check => check.name === 'Full Product evidence aggregate') : []
  if (nativeGates.length !== 1 || nativeGates[0].app?.id !== 15368 ||
      nativeGates[0].head_sha !== run?.headSha || nativeGates[0].status !== 'completed' ||
      nativeGates[0].conclusion !== 'success' || nativeGates[0].check_suite?.id !== run?.checkSuiteId) reasons.push('native-gate-publisher-or-suite-unverified')

  return {
    disposition: reasons.length ? 'REFUSE' : 'REVIEW_REQUIRED',
    canPublishAuthority: false,
    canMerge: false,
    reasons: [...new Set(reasons)],
    kernelChanges,
    ordinaryDecision,
  }
}
