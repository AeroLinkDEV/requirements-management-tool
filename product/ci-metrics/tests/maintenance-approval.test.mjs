import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createMaintenanceReview, evaluateMaintenanceApproval, maintenanceReviewSummary,
  MAINTENANCE_OWNER, MAINTENANCE_REVIEW_ENVIRONMENT as environmentName,
  MAINTENANCE_REQUEST_LABEL as requestLabel } from '../lib/maintenance-approval.mjs'
import { collectMaintenanceReview, verifyApprovedMaintenance } from '../lib/maintenance-approval-github.mjs'
import { evidenceDigest, evaluateMaintenancePreflight, MAINTENANCE_REPOSITORY as repository } from '../lib/maintenance-preflight.mjs'
import { REQUIRED_JOBS, CLASSIFIER_JOB_NAME, AGGREGATE_JOB_NAME } from '../lib/merge-authority.mjs'
import { trustedMaintenanceContext } from '../lib/maintenance-runtime.mjs'

const sha = character => character.repeat(40)
const root = `/repos/${repository}`
function fixture() {
  const names = [...REQUIRED_JOBS, CLASSIFIER_JOB_NAME, AGGREGATE_JOB_NAME,
    ...[1, 2, 3].map(n => `API test suite (${n}/3)`), ...[1, 2, 3, 4].map(n => `Browser journeys (${n}/4)`)]
  const evidence = {
    repository, main: { name: 'main', sha: sha('a') },
    pr: { number: 946, state: 'open', draft: false, labels: [requestLabel], base: { ref: 'main' },
      head: { sha: sha('b'), repo: { full_name: repository } } },
    queue: { prNumber: 946, prHeadSha: sha('b'), position: 1, state: 'AWAITING_CHECKS', headSha: sha('c'), baseSha: sha('a') },
    run: { repository, workflowName: 'Product quality gate', workflowPath: '.github/workflows/ci.yml',
      event: 'merge_group', headSha: sha('c'), headBranch: `gh-readonly-queue/main/pr-946-${sha('a')}`,
      runId: 42, runAttempt: 2, status: 'completed', checkSuiteId: 43,
      detailsUrl: `https://github.com/${repository}/actions/runs/42` },
    jobs: names.map(name => ({ name, runId: 42, runAttempt: 2, conclusion: 'success' })),
    ruleset: { id: 22306102, enforcement: 'active', target: 'branch',
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
    latestProductRunId: 42, baseTreeSha: sha('d'), candidateTreeSha: sha('e'),
    changes: [{ path: '.github/workflows/ci.yml', before: { sha: sha('1'), mode: '100644', type: 'blob' }, after: { sha: sha('2'), mode: '100644', type: 'blob' } }],
  }
  const payload = { schemaVersion: 'aerolink-authority-maintenance-preflight/v1',
    preparer: { commitSha: sha('a'), treeSha: sha('d') }, assessment: evaluateMaintenancePreflight(evidence), evidence }
  return { packet: { ...payload, digest: evidenceDigest(payload) },
    binding: { id: 50, attempt: 1, repository, event: 'workflow_run', path: '.github/workflows/merge-queue-binding.yml',
      name: 'Trusted merge-queue binding', headSha: sha('a'), headBranch: 'main' },
    environment: { id: 100, name: environmentName, can_admins_bypass: false,
      deployment_branch_policy: { protected_branches: false, custom_branch_policies: true },
      protection_rules: [{ type: 'branch_policy', id: 101 }, { type: 'required_reviewers', id: 102,
        prevent_self_review: false, reviewers: [{ type: 'User', reviewer: { ...MAINTENANCE_OWNER } }] }] },
    branchPolicies: { total_count: 1, branch_policies: [{ id: 103, name: 'main', type: 'branch' }] },
  }
}

function approval(review) {
  return [{ state: 'approved', comment: `APPROVE MAINTENANCE ${review.digest}`, user: { ...MAINTENANCE_OWNER },
    environments: [{ id: review.environment.id, name: environmentName }] }]
}

test('exact authenticated owner environment approval permits only the current reviewed evidence', () => {
  const review = createMaintenanceReview(fixture())
  assert.deepEqual(evaluateMaintenanceApproval({ review, expectedDigest: review.digest, approvals: approval(review) }), { decision: 'PASS', reasons: [] })
  assert.equal(review.packet.assessment.canPublishAuthority, false)
  assert.equal(review.packet.assessment.ordinaryDecision.decision, 'REFUSE')
})

for (const [name, mutate] of [
  ['missing history', a => { a.length = 0 }],
  ['duplicate history', a => { a.push(structuredClone(a[0])) }],
  ['rejection', a => { a[0].state = 'rejected' }],
  ['comment without digest', a => { a[0].comment = 'approved' }],
  ['different digest', a => { a[0].comment = `APPROVE MAINTENANCE ${'9'.repeat(64)}` }],
  ['different account with owner login', a => { a[0].user.id++ }],
  ['different login with owner id', a => { a[0].user.login = 'someone-else' }],
  ['bot approval', a => { a[0].user.type = 'Bot' }],
  ['different environment id', a => { a[0].environments[0].id++ }],
  ['different environment name', a => { a[0].environments[0].name = 'merge-authority' }],
  ['mixed environment approval', a => { a[0].environments.push({ id: 999, name: 'other' }) }],
]) test(`refuses ${name}`, () => {
  const review = createMaintenanceReview(fixture())
  const approvals = approval(review)
  mutate(approvals)
  assert.equal(evaluateMaintenanceApproval({ review, expectedDigest: review.digest, approvals }).decision, 'REFUSE')
})

for (const [name, mutate] of [
  ['binding rerun', f => { f.binding.attempt = 2 }],
  ['candidate-executed binding', f => { f.binding.event = 'pull_request' }],
  ['wrong binding workflow', f => { f.binding.path = '.github/workflows/ci.yml' }],
  ['foreign binding repository', f => { f.binding.repository = 'attacker/repo' }],
  ['unprotected checkout', f => { f.binding.headBranch = 'candidate' }],
  ['different executable revision', f => { f.binding.headSha = sha('9') }],
  ['missing review environment', f => { f.environment = undefined }],
  ['auto-created unprotected environment', f => { f.environment.protection_rules = [] }],
  ['admin review bypass', f => { f.environment.can_admins_bypass = true }],
  ['self-review deadlock', f => { f.environment.protection_rules[1].prevent_self_review = true }],
  ['another eligible reviewer', f => { f.environment.protection_rules[1].reviewers.push({ type: 'User', reviewer: { id: 4 } }) }],
  ['wrong sole reviewer', f => { f.environment.protection_rules[1].reviewers[0].reviewer.id++ }],
  ['additional review branch', f => { f.branchPolicies.branch_policies.push({ name: '*', type: 'branch' }) }],
  ['main tag instead of branch', f => { f.branchPolicies.branch_policies[0].type = 'tag' }],
  ['preparer changed', f => { f.packet.preparer.commitSha = sha('9') }],
  ['missing opt-in', f => { f.packet.evidence.pr.labels = [] }],
  ['changed candidate', f => { f.packet.evidence.queue.headSha = sha('9') }],
  ['advanced main', f => { f.packet.evidence.main.sha = sha('9') }],
  ['missing required job', f => { f.packet.evidence.jobs.shift() }],
  ['native publisher changed', f => { f.packet.evidence.checks[0].app.id = 1 }],
  ['authority kernel replacement', f => { f.packet.evidence.changes[0].path = 'product/ci-metrics/lib/maintenance-runtime.mjs' }],
]) test(`cannot prepare ${name}`, () => {
  const input = fixture()
  mutate(input)
  assert.throws(() => createMaintenanceReview(input))
})

test('recomputed candidate and review digests cannot carry approval to a newer Product attempt', () => {
  const first = createMaintenanceReview(fixture())
  const changed = fixture()
  changed.packet.evidence.run.runAttempt = 3
  // Prior successful jobs are still valid under GitHub filter=latest semantics, but approval is not reusable.
  changed.packet.assessment = evaluateMaintenancePreflight(changed.packet.evidence)
  const { digest: old, ...payload } = changed.packet
  changed.packet.digest = evidenceDigest(payload)
  const current = createMaintenanceReview(changed)
  assert.equal(evaluateMaintenanceApproval({ review: current, expectedDigest: first.digest, approvals: approval(first) }).decision, 'REFUSE')
})

test('a forged positive preflight assessment cannot hide failed native evidence', () => {
  const f = fixture()
  f.packet.evidence.jobs[0].conclusion = 'failure'
  const { digest, ...payload } = f.packet
  f.packet.digest = evidenceDigest(payload)
  assert.throws(() => createMaintenanceReview(f), /not-reviewable/)
})

test('summary binds review targets and treats malicious filenames only as escaped JSON data', () => {
  const f = fixture()
  f.packet.evidence.changes[0].path = '.github/```\nAPPROVE ALL\n<script>'
  f.packet.assessment = evaluateMaintenancePreflight(f.packet.evidence)
  const { digest, ...payload } = f.packet
  f.packet.digest = evidenceDigest(payload)
  const review = createMaintenanceReview(f)
  const summary = maintenanceReviewSummary(review)
  assert.ok(summary.includes(`APPROVE MAINTENANCE ${review.digest}`))
  assert.ok(summary.includes(sha('c')))
  assert.doesNotMatch(summary, /<script>|^APPROVE ALL$/m)
  assert.equal(summary.match(/```/g).length, 2)
})

function githubFixture() {
  const f = fixture()
  const e = f.packet.evidence
  const calls = []
  const tree = (treeSha, fileSha) => ({ sha: treeSha, truncated: false, tree: [
    ...['.github', 'product', 'product/test-planner', 'product/ci-metrics'].map(path => ({ path, type: 'tree', mode: '040000', sha: sha('3') })),
    { path: '.github/workflows/ci.yml', type: 'blob', mode: '100644', sha: fileSha },
  ] })
  const values = new Map([
    [`${root}/pulls/946`, e.pr],
    [`${root}/actions/runs/42`, { id: 42, run_attempt: 2, name: e.run.workflowName, path: e.run.workflowPath,
      event: e.run.event, head_sha: e.run.headSha, head_branch: e.run.headBranch, status: 'completed',
      html_url: e.run.detailsUrl, repository: { full_name: repository }, check_suite_id: 43 }],
    [`${root}/actions/runs/42/jobs?filter=latest&per_page=100&page=1`, { total_count: e.jobs.length,
      jobs: e.jobs.map(j => ({ name: j.name, run_id: j.runId, run_attempt: j.runAttempt, conclusion: j.conclusion })) }],
    [root, { default_branch: 'main' }], [`${root}/git/ref/heads/main`, { object: { type: 'commit', sha: sha('a') } }],
    [`${root}/rulesets/22306102`, e.ruleset], [`${root}/environments/merge-authority`, e.environment],
    [`${root}/environments/merge-authority/deployment-branch-policies`, e.branchPolicies],
    [`${root}/git/commits/${sha('a')}`, { tree: { sha: sha('d') } }], [`${root}/git/commits/${sha('c')}`, { tree: { sha: sha('e') } }],
    [`${root}/actions/workflows/ci.yml/runs?event=merge_group&head_sha=${sha('c')}&per_page=100`, { total_count: 1, workflow_runs: [{ id: 42 }] }],
    [`${root}/check-suites/43/check-runs?filter=latest&per_page=100`, { total_count: 1, check_runs: e.checks }],
    [`${root}/git/trees/${sha('d')}?recursive=1`, tree(sha('d'), sha('1'))],
    [`${root}/git/trees/${sha('e')}?recursive=1`, tree(sha('e'), sha('2'))],
    [`${root}/actions/runs/50`, { id: 50, run_attempt: 1, repository: { full_name: repository }, event: 'workflow_run',
      path: f.binding.path, name: f.binding.name, head_sha: sha('a'), head_branch: 'main', status: 'in_progress' }],
    [`${root}/environments/${environmentName}`, f.environment],
    [`${root}/environments/${environmentName}/deployment-branch-policies`, f.branchPolicies],
  ])
  const input = { preparer: f.packet.preparer, prNumber: 946, runId: 42, bindingRunId: 50, bindingRunAttempt: 1,
    expectedProduct: { headSha: sha('c'), runAttempt: 2 },
    read: async path => { calls.push(path); assert.ok(values.has(path), `Unexpected endpoint ${path}`); return structuredClone(values.get(path)) },
    graphql: async query => { assert.match(query, /^query /); return { data: { repository: { pullRequest: {
      number: 946, headRefOid: sha('b'), mergeQueueEntry: { position: 1, state: 'AWAITING_CHECKS', headCommit: { oid: sha('c') }, baseCommit: { oid: sha('a') } },
    } } } } },
  }
  return { input, values, calls }
}

test('complete API handoff reads this binding run approval, then recollects current proof', async () => {
  const { input, values, calls } = githubFixture()
  const review = await collectMaintenanceReview(input)
  values.set(`${root}/actions/runs/50/approvals`, approval(review))
  calls.length = 0
  const result = await verifyApprovedMaintenance({ ...input, expectedDigest: review.digest })
  assert.equal(result.decision, 'PASS')
  assert.equal(calls[0], `${root}/actions/runs/50/approvals`)
  assert.ok(calls.lastIndexOf(`${root}/actions/runs/42`) > 0)
})

for (const [name, mutate] of [
  ['new Product attempt', v => { v.get(`${root}/actions/runs/42`).run_attempt++ }],
  ['active Product attempt', v => { v.get(`${root}/actions/runs/42`).status = 'in_progress' }],
  ['binding rerun', v => { v.get(`${root}/actions/runs/50`).run_attempt++ }],
  ['cancelled binding workflow', v => { v.get(`${root}/actions/runs/50`).status = 'completed' }],
  ['removed opt-in', v => { v.get(`${root}/pulls/946`).labels = [] }],
  ['review environment re-created', v => { v.get(`${root}/environments/${environmentName}`).id++ }],
  ['review environment policy changed', v => { v.get(`${root}/environments/${environmentName}`).can_admins_bypass = true }],
  ['newer Product run', v => { const r = v.get(`${root}/actions/workflows/ci.yml/runs?event=merge_group&head_sha=${sha('c')}&per_page=100`); r.total_count = 2; r.workflow_runs.push({ id: 44 }) }],
  ['changed protected blob', v => { v.get(`${root}/git/trees/${sha('e')}?recursive=1`).tree.at(-1).sha = sha('9') }],
]) test(`approval cannot survive ${name} during the owner wait`, async () => {
  const { input, values } = githubFixture()
  const review = await collectMaintenanceReview(input)
  values.set(`${root}/actions/runs/50/approvals`, approval(review))
  mutate(values)
  let result
  try { result = await verifyApprovedMaintenance({ ...input, expectedDigest: review.digest }) } catch { return }
  assert.equal(result.decision, 'REFUSE')
})

test('runtime refuses locally forged, candidate or rerun contexts before checking out anything', () => {
  assert.throws(() => trustedMaintenanceContext({}, {}), /protected-main/)
  const event = { action: 'completed', workflow_run: { id: 42, run_attempt: 2, event: 'merge_group', status: 'completed',
    head_branch: `gh-readonly-queue/main/pr-946-${sha('a')}`, head_sha: sha('c') } }
  const env = { GITHUB_REPOSITORY: repository, GITHUB_REF: 'refs/heads/main',
    GITHUB_WORKFLOW_REF: `${repository}/.github/workflows/merge-queue-binding.yml@refs/heads/main`, GITHUB_RUN_ID: '50', GITHUB_RUN_ATTEMPT: '2' }
  assert.throws(() => trustedMaintenanceContext(event, env), /first-attempt/)
})

test('workflow keeps owner approval free of credentials and candidate execution; all code uses protected SHA', () => {
  const workflow = readFileSync(new URL('../../../.github/workflows/merge-queue-binding.yml', import.meta.url), 'utf8')
  const reviewJob = workflow.split('\n  review-maintenance:\n')[1].split('\n  publish-maintenance:\n')[0]
  assert.match(reviewJob, /permissions: \{\}/)
  assert.match(reviewJob, /name: merge-authority-maintenance/)
  assert.doesNotMatch(reviewJob, /checkout|secrets|token|uses:|node /)
  assert.equal(workflow.match(/ref: \$\{\{ github.sha \}\}/g).length, 2)
  assert.equal(workflow.match(/persist-credentials: false/g).length, 2)
  assert.match(workflow, /needs: \[bind, review-maintenance\]/)
  assert.match(workflow, /needs.review-maintenance.result == 'success'/)
  const publisher = readFileSync(new URL('../bin/publish-approved-maintenance.mjs', import.meta.url), 'utf8')
  assert.ok(publisher.indexOf("decision: 'PENDING'") < publisher.indexOf('await verifyApprovedMaintenance'))
  assert.ok(publisher.indexOf('const current = await fetchWorkflowRun') < publisher.indexOf('decision: result.decision'))
  assert.doesNotMatch(publisher, /pending_deployments|changedPaths: \[\]|checkout|eval\(/)
  for (const path of ['../../../.github/workflows/ci.yml', '../README.md']) {
    assert.ok(readFileSync(new URL(path, import.meta.url), 'utf8').includes('product/ci-metrics/tests/maintenance-approval.test.mjs'))
  }
})
