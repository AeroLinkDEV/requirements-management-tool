import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { canonicalJson, compareProtectedTrees, evidenceDigest, evaluateMaintenancePreflight,
  MAINTENANCE_REPOSITORY as repository, MAINTENANCE_RULESET_ID } from '../lib/maintenance-preflight.mjs'
import { collectMaintenancePreflight } from '../lib/maintenance-preflight-github.mjs'
import { REQUIRED_JOBS, CLASSIFIER_JOB_NAME, AGGREGATE_JOB_NAME } from '../lib/merge-authority.mjs'

const sha = character => character.repeat(40)
const runId = 42
const prNumber = 946
function fixture() {
  const names = [...REQUIRED_JOBS, CLASSIFIER_JOB_NAME, AGGREGATE_JOB_NAME,
    ...[1, 2, 3].map(n => `API test suite (${n}/3)`), ...[1, 2, 3, 4].map(n => `Browser journeys (${n}/4)`)]
  return {
    repository, main: { name: 'main', sha: sha('a') },
    pr: { number: prNumber, state: 'open', draft: false, base: { ref: 'main' }, head: { sha: sha('b'), repo: { full_name: repository } } },
    queue: { prNumber, prHeadSha: sha('b'), position: 1, state: 'AWAITING_CHECKS', headSha: sha('c'), baseSha: sha('a') },
    run: { repository, workflowName: 'Product quality gate', workflowPath: '.github/workflows/ci.yml',
      event: 'merge_group', headSha: sha('c'), headBranch: `gh-readonly-queue/main/pr-${prNumber}-${sha('a')}`,
      runId, runAttempt: 2, status: 'completed', checkSuiteId: 43 },
    jobs: names.map(name => ({ name, runId, runAttempt: 2, conclusion: 'success' })),
    ruleset: { id: MAINTENANCE_RULESET_ID, enforcement: 'active', target: 'branch',
      conditions: { ref_name: { include: ['~DEFAULT_BRANCH'], exclude: [] } }, bypass_actors: [], rules: [
      { type: 'required_status_checks', parameters: { required_status_checks: [
        { context: 'Trusted merge-queue binding', integration_id: 4834539 },
        { context: AGGREGATE_JOB_NAME, integration_id: 15368 },
      ] } },
      { type: 'merge_queue', parameters: { grouping_strategy: 'ALLGREEN', max_entries_to_merge: 1, merge_method: 'SQUASH' } },
      { type: 'pull_request' }, { type: 'deletion' }, { type: 'non_fast_forward' },
    ] },
    environment: { name: 'merge-authority', deployment_branch_policy: { custom_branch_policies: true, protected_branches: false } },
    branchPolicies: { total_count: 1, branch_policies: [{ name: 'main', type: 'branch' }] },
    checks: [{ name: AGGREGATE_JOB_NAME, app: { id: 15368 }, head_sha: sha('c'), status: 'completed', conclusion: 'success', check_suite: { id: 43 } }],
    latestProductRunId: runId, baseTreeSha: sha('d'), candidateTreeSha: sha('e'),
    changes: [{ path: '.github/workflows/ci.yml', before: { sha: sha('1'), mode: '100644', type: 'blob' }, after: { sha: sha('2'), mode: '100644', type: 'blob' } }],
  }
}

test('a complete maintenance packet still grants no publishing or merging permission', () => {
  const result = evaluateMaintenancePreflight(fixture())
  assert.equal(result.disposition, 'REVIEW_REQUIRED')
  assert.deepEqual(result.reasons, [])
  assert.equal(result.canPublishAuthority, false)
  assert.equal(result.canMerge, false)
  assert.equal(result.ordinaryDecision.decision, 'REFUSE')
  assert.match(result.ordinaryDecision.reasons[0], /^trusted-surface-modified:/)
})

for (const [name, mutate, reason] of [
  ['foreign repository', e => { e.repository = 'attacker/repo' }, 'wrong-repository'],
  ['draft PR', e => { e.pr.draft = true }, 'pull-request-not-ready'],
  ['fork PR', e => { e.pr.head.repo.full_name = 'attacker/repo' }, 'pull-request-not-ready'],
  ['changed PR head', e => { e.pr.head.sha = sha('9') }, 'not-current-single-pr-composition'],
  ['different composed candidate', e => { e.queue.headSha = sha('9') }, 'not-current-single-pr-composition'],
  ['another PR ahead', e => { e.queue.position = 2 }, 'not-current-single-pr-composition'],
  ['advanced main', e => { e.main.sha = sha('9') }, 'not-current-single-pr-composition'],
  ['queue removal', e => { e.queue = null }, 'not-current-single-pr-composition'],
  ['newer Product run', e => { e.latestProductRunId = 44 }, 'newer-or-unverified-product-run'],
  ['no protected comparison', e => { e.changes = [] }, 'protected-diff-missing-or-malformed'],
  ['missing job', e => { e.jobs = e.jobs.filter(j => j.name !== 'Infrastructure test suite') }, 'missing-job:'],
  ['failed job', e => { e.jobs[0].conclusion = 'failure' }, 'job-not-success:'],
  ['duplicate aggregate', e => { e.jobs.push(e.jobs.find(j => j.name === AGGREGATE_JOB_NAME)) }, 'ambiguous-aggregate:'],
  ['incomplete shards', e => { e.jobs = e.jobs.filter(j => j.name !== 'Browser journeys (4/4)') }, 'shard-set-incomplete:'],
  ['active newer attempt', e => { e.run.status = 'in_progress' }, 'run-not-completed:'],
  ['future job attempt', e => { e.jobs[0].runAttempt = 3 }, 'job-attempt-mismatch:'],
  ['diagnostic evidence', e => { e.run.event = 'workflow_dispatch' }, 'event-not-merge-group:'],
  ['wrong publisher', e => { e.checks[0].app.id = 1 }, 'native-gate-publisher-or-suite-unverified'],
  ['check from other suite', e => { e.checks[0].check_suite.id = 99 }, 'native-gate-publisher-or-suite-unverified'],
  ['native gate pending', e => { e.checks[0].status = 'in_progress' }, 'native-gate-publisher-or-suite-unverified'],
  ['ruleset bypass', e => { e.ruleset.bypass_actors.push({ actor_id: 5 }) }, 'required-publishers-or-protection-changed'],
  ['ruleset excludes main', e => { e.ruleset.conditions.ref_name.exclude.push('refs/heads/main') }, 'required-publishers-or-protection-changed'],
  ['PR requirement removed', e => { e.ruleset.rules = e.ruleset.rules.filter(rule => rule.type !== 'pull_request') }, 'required-publishers-or-protection-changed'],
  ['wrong pinned App', e => { e.ruleset.rules[0].parameters.required_status_checks[0].integration_id = 1 }, 'required-publishers-or-protection-changed'],
  ['weak queue policy', e => { e.ruleset.rules[1].parameters.grouping_strategy = 'HEADGREEN' }, 'queue-policy-changed'],
  ['additional credential branch', e => { e.branchPolicies.branch_policies.push({ name: '*', type: 'branch' }) }, 'app-secret-environment-not-main-only'],
  ['tag with main name', e => { e.branchPolicies.branch_policies[0].type = 'tag' }, 'app-secret-environment-not-main-only'],
  ['kernel removal', e => { e.changes[0].path = '.github/workflows/request-full-ci.yml'; e.changes[0].after = null }, 'separate-trust-root-bootstrap-required'],
  ['kernel helper substitution', e => { e.changes[0].path = 'product/ci-metrics/lib/maintenance-preflight.mjs' }, 'separate-trust-root-bootstrap-required'],
]) {
  test(`refuses ${name} without discarding ordinary verifier refusals`, () => {
    const evidence = fixture()
    mutate(evidence)
    const result = evaluateMaintenancePreflight(evidence)
    assert.equal(result.disposition, 'REFUSE')
    assert.ok(result.reasons.some(r => r.startsWith(reason)), JSON.stringify(result))
    assert.equal(result.canPublishAuthority, false)
    assert.equal(result.canMerge, false)
  })
}

function tree(rootSha, fileSha) {
  return { sha: rootSha, truncated: false, tree: [
    ...['.github', 'product', 'product/test-planner', 'product/ci-metrics'].map(path => ({ path, sha: sha('3'), mode: '040000', type: 'tree' })),
    { path: '.github/workflows/ci.yml', sha: fileSha, mode: '100644', type: 'blob' },
  ] }
}

test('full Git tree comparison preserves removals, additions and executable-bit changes', () => {
  const base = tree(sha('d'), sha('1'))
  const candidate = tree(sha('e'), sha('1'))
  candidate.tree.at(-1).mode = '100755'
  assert.equal(compareProtectedTrees(base, candidate)[0].after.mode, '100755')
  candidate.tree.at(-1).path = 'product/not-protected.yml'
  const removed = compareProtectedTrees(base, candidate)
  assert.equal(removed[0].path, '.github/workflows/ci.yml')
  assert.equal(removed[0].after, null)
})

test('truncated, duplicate, malformed and missing trusted directories fail closed', () => {
  const base = tree(sha('d'), sha('1'))
  for (const mutate of [t => { t.truncated = true }, t => { delete t.truncated },
    t => { t.tree.push(t.tree[0]) }, t => { t.tree[0].path = '../outside' },
    t => { t.tree = t.tree.filter(entry => entry.path !== '.github') }]) {
    const candidate = tree(sha('e'), sha('2'))
    mutate(candidate)
    assert.throws(() => compareProtectedTrees(base, candidate))
  }
})

test('digest is key-order stable but binds revisions, jobs, settings and every diff side', () => {
  assert.equal(canonicalJson({ b: 1, a: 2 }), canonicalJson({ a: 2, b: 1 }))
  const original = fixture()
  for (const mutate of [e => { e.pr.head.sha = sha('9') }, e => { e.queue.headSha = sha('9') },
    e => { e.run.runAttempt++ }, e => { e.jobs[0].conclusion = 'failure' },
    e => { e.changes[0].before.sha = sha('9') }, e => { e.ruleset.enforcement = 'disabled' }]) {
    const altered = structuredClone(original)
    mutate(altered)
    assert.notEqual(evidenceDigest(altered), evidenceDigest(original))
  }
  assert.throws(() => canonicalJson({ missing: undefined }))
})

test('an authority test update requires review but does not execute in the approval kernel', () => {
  const evidence = fixture()
  evidence.changes[0].path = 'product/ci-metrics/tests/merge-authority.test.mjs'
  const result = evaluateMaintenancePreflight(evidence)
  assert.equal(result.disposition, 'REVIEW_REQUIRED')
  assert.equal(result.canPublishAuthority, false)
  assert.equal(result.ordinaryDecision.decision, 'REFUSE')
})

function githubFixture({ advanceQueue = false, truncate = false, incomplete = false } = {}) {
  const e = fixture()
  const calls = []
  let queueReads = 0
  const root = `/repos/${repository}`
  const rawRun = { id: runId, run_attempt: 2, name: e.run.workflowName, path: e.run.workflowPath,
    event: e.run.event, head_sha: e.run.headSha, head_branch: e.run.headBranch, status: 'completed',
    html_url: `https://github.com/${repository}/actions/runs/${runId}`, repository: { full_name: repository }, check_suite_id: 43 }
  const table = new Map([
    [`${root}/pulls/${prNumber}`, e.pr], [`${root}/actions/runs/${runId}`, rawRun],
    [`${root}/actions/runs/${runId}/jobs?filter=latest&per_page=100&page=1`,
      { total_count: e.jobs.length, jobs: e.jobs.map(job => ({ name: job.name, conclusion: job.conclusion, run_id: job.runId, run_attempt: job.runAttempt })) }],
    [root, { default_branch: 'main' }], [`${root}/git/ref/heads/main`, { object: { type: 'commit', sha: sha('a') } }],
    [`${root}/rulesets/${MAINTENANCE_RULESET_ID}`, e.ruleset], [`${root}/environments/merge-authority`, e.environment],
    [`${root}/environments/merge-authority/deployment-branch-policies`, e.branchPolicies],
    [`${root}/git/commits/${sha('a')}`, { tree: { sha: sha('d') } }], [`${root}/git/commits/${sha('c')}`, { tree: { sha: sha('e') } }],
    [`${root}/actions/workflows/ci.yml/runs?event=merge_group&head_sha=${sha('c')}&per_page=100`, { total_count: incomplete ? 2 : 1, workflow_runs: [{ id: runId }] }],
    [`${root}/check-suites/43/check-runs?filter=latest&per_page=100`, { total_count: 1, check_runs: e.checks }],
    [`${root}/git/trees/${sha('d')}?recursive=1`, tree(sha('d'), sha('1'))],
    [`${root}/git/trees/${sha('e')}?recursive=1`, { ...tree(sha('e'), sha('2')), truncated: truncate }],
  ])
  return { calls, prNumber, runId, preparer: { commitSha: sha('4'), treeSha: sha('5') },
    read: async path => { calls.push(path); assert.ok(table.has(path), `Unexpected read: ${path}`); return structuredClone(table.get(path)) },
    graphql: async query => {
      assert.match(query, /^query /)
      queueReads++
      return { data: { repository: { pullRequest: { number: prNumber, headRefOid: sha('b'),
        mergeQueueEntry: advanceQueue && queueReads > 1 ? null : { position: 1, state: 'AWAITING_CHECKS', headCommit: { oid: sha('c') }, baseCommit: { oid: sha('a') } } } } } }
    },
  }
}

test('collector binds real endpoint shapes, complete trees, native publisher and current queue twice', async () => {
  const inputs = githubFixture()
  const packet = await collectMaintenancePreflight(inputs)
  assert.equal(packet.assessment.disposition, 'REVIEW_REQUIRED')
  const { digest, ...payload } = packet
  assert.equal(digest, evidenceDigest(payload))
  assert.equal(inputs.calls.filter(path => path.includes('/jobs?filter=latest')).length, 1)
  assert.ok(inputs.calls.includes(`/repos/${repository}/check-suites/43/check-runs?filter=latest&per_page=100`))
})

test('review digest binds the preparer revision and its decision, not just the raw GitHub evidence', async () => {
  const inputs = githubFixture()
  const packet = await collectMaintenancePreflight(inputs)
  const changed = await collectMaintenancePreflight({ ...githubFixture(), preparer: { commitSha: sha('6'), treeSha: sha('7') } })
  assert.notEqual(packet.digest, changed.digest)
  const { digest, ...payload } = packet
  payload.assessment.reasons.push('additional-refusal')
  assert.notEqual(digest, evidenceDigest(payload))
  await assert.rejects(collectMaintenancePreflight({ ...githubFixture(), preparer: undefined }), /preparer commit/)
})

for (const [option, error] of [['advanceQueue', /advanced/], ['truncate', /untruncated/], ['incomplete', /pagination/]]) {
  test(`collector refuses ${option} instead of producing a review packet`, async () => {
    await assert.rejects(collectMaintenancePreflight(githubFixture({ [option]: true })), error)
  })
}

test('operator command has no check publisher, approval endpoint, candidate execution or settings write', () => {
  const cli = readFileSync(new URL('../bin/prepare-authority-maintenance.mjs', import.meta.url), 'utf8')
  assert.match(cli, /'--method', 'GET'/)
  assert.match(cli, /flag: 'wx'/)
  assert.match(cli, /windowsHide: true/)
  assert.doesNotMatch(cli, /publishMergeAuthorityCheck|MERGE_AUTHORITY_TOKEN|pending_deployments|method.*(?:PATCH|PUT|DELETE)|git checkout/)
})

test('hosted metrics tooling executes the maintenance refusal suite', () => {
  const workflow = readFileSync(new URL('../../../.github/workflows/ci.yml', import.meta.url), 'utf8')
  const invocation = workflow.split('\n').find(line => line.includes('run: node --test') && line.includes('merge-authority-github.test.mjs'))
  assert.ok(invocation?.includes('product/ci-metrics/tests/maintenance-preflight.test.mjs'))
})
