import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { buildFragment } from '../lib/fragment.mjs'
import { aggregateFragments } from '../lib/aggregate.mjs'
import { REUSE_REPOSITORY as repository, evaluateQueueReuseShadow, requiredNativeNames } from '../lib/queue-reuse-shadow.mjs'
import { deriveObserverTopology, readAllReusePages, collectQueueReuseShadow, createReuseReader } from '../lib/queue-reuse-observer.mjs'

const now = Date.parse('2026-09-10T01:00:00Z')
const iso = offset => new Date(now + offset).toISOString()
const hash = c => c.repeat(40)
const api = `https://api.github.com/repos/${repository}`
function fixture() {
  const run = { id: 71, run_attempt: 1, name: 'Product quality gate', path: '.github/workflows/ci.yml', workflow_id: 72,
    repository: { full_name: repository }, status: 'completed', conclusion: 'success', event: 'merge_group',
    head_sha: hash('a'), head_branch: `gh-readonly-queue/main/pr-9-${hash('d')}`, check_suite_id: 73,
    created_at: iso(-120_000), updated_at: iso(-10_000) }
  const topology = deriveObserverTopology(run, hash('b'))
  const fixedNames = { changes: 'Classify changed product areas', 'metrics-tooling': 'CI metrics tooling tests',
    'backend-core-domain': 'Domain test suite', 'backend-core-infrastructure': 'Infrastructure test suite',
    client: 'Client lint, type-check, and build', 'script-contracts': 'Operator and recovery script contracts',
    'browser-production': 'Browser journeys on the production build', 'postgresql-smoke': 'PostgreSQL migrations and secure bootstrap',
    gate: 'Full Product evidence aggregate' }
  const name = j => fixedNames[j.instance] ?? (j.group === 'backend-api' ? `API test suite (${j.instance.at(-1)}/3)` : `Browser journeys (${j.instance.at(-1)}/4)`)
  const fragments = topology.expectedJobs.map(j => buildFragment({ run: topology.expectedRun,
    job: { ...j, name: name(j), result: 'success', matrix: null },
    timings: { jobStartMs: now - 100_000, setupEndMs: now - 90_000, testEndMs: now - 80_000, jobEndMs: now - 70_000,
      setupMs: 10_000, testMs: 10_000, postTestMs: 10_000, missing: {} },
    counts: { expected: 2, executed: 1, passed: 1, failed: 0, skipped: 1, flaky: null, source: 'trx', missing: null },
    cache: {}, classification: { docsOnly: false, backend: true, client: true, browser: true, postgresql: true, unavailable: false } }))
  const record = aggregateFragments({ fragments, runMeta: topology })
  const jobs = topology.expectedJobs.map((j, i) => ({ id: 100 + i, name: name(j), run_id: run.id, run_attempt: 1,
    head_sha: run.head_sha, status: 'completed', conclusion: 'success', started_at: iso(-100_000), completed_at: iso(-50_000),
    check_run_url: `${api}/check-runs/${200 + i}` }))
  const checks = jobs.map((j, i) => ({ id: 200 + i, name: j.name, app: { id: 15368 }, head_sha: run.head_sha,
    status: 'completed', conclusion: 'success', check_suite: { id: run.check_suite_id } }))
  checks.push({ id: 300, name: 'Trusted merge-queue binding', app: { id: 4834539 }, head_sha: run.head_sha,
    status: 'completed', conclusion: 'success', completed_at: iso(-20_000), details_url: `https://github.com/${repository}/actions/runs/${run.id}` })
  const manifest = { schemaVersion: 'aerolink-validated-tree/v1', provenance: 'shadow', repository,
    workflow: run.name, workflowRef: topology.expectedRun.workflowRef, run: { id: run.id, attempt: 1, event: run.event }, event: run.event,
    checkedOut: { commitSha: run.head_sha, treeSha: hash('b'), ref: topology.expectedRun.ref },
    gates: { gatePassed: true, allSelectedPassed: true, missing: [], missingTotal: 0,
      selected: record.jobs.map(j => ({ instance: j.instance, result: j.result })) },
    verifiedTotals: Object.fromEntries(['expected', 'executed', 'passed', 'failed', 'skipped', 'flaky'].map(k => [k, record.counts[k]])),
    validatedAt: iso(-20_000), canAuthorizePostMergeSkip: true }
  return { run, latestRun: structuredClone(run), workflow: { id: 72, path: run.path },
    main: { name: 'main', protected: true }, mainRelationship: { status: 'identical', merge_base_commit: { sha: run.head_sha } },
    prReadiness: { binding: { app: { id: 4834539 }, head_sha: hash('c'), conclusion: 'success', status: 'completed', completed_at: iso(-130_000) },
      run: { repository: { full_name: repository }, head_sha: hash('c'), path: '.github/workflows/request-full-ci.yml', event: 'pull_request_target', status: 'completed', conclusion: 'success' } },
    pr: { number: 9, merged: true, state: 'closed', merge_commit_sha: run.head_sha, head: { sha: hash('c'), repo: { full_name: repository } },
      base: { ref: 'main', repo: { full_name: repository } } },
    candidate: { sha: run.head_sha, tree: { sha: hash('b') }, parents: [{ sha: hash('d') }] },
    landed: { sha: run.head_sha, tree: { sha: hash('b') } },
    composition: { method: 'git-merge-tree', baseSha: hash('d'), prHeadSha: hash('c'), treeSha: hash('b'), associatedPrs: [9] },
    changedPaths: ['product/client/src/view.tsx'], protectedChanges: [], jobs, checks,
    evidence: { record, manifest, fragments, topology }, collectionErrors: [],
    fallback: { passed: true, protectedDefinitionMatches: true, currentAttempt: true, run: { id: 90, repository: { full_name: repository },
      event: 'workflow_dispatch', head_branch: 'main', updated_at: iso(-50_000) } } }
}

test('complete explicitly synthetic evidence can only be shadow positive and retains explicit test skips', () => {
  const report = evaluateQueueReuseShadow(fixture(), { now })
  assert.equal(report.outcome, 'would_reuse', JSON.stringify(report.conditions.filter(c => !c.passed)))
  assert.equal(report.sourceAuthenticity, 'unverified-fixture')
  assert.equal(report.canSkip, false)
  assert.equal(report.adoptionEligible, false)
  assert.equal(report.executionSelectorChanged, false)
  assert.equal(report.potentiallyAvoidable.deliveredRunnerMinutes, 0)
})

const negatives = {
  repository: p => { p.run.repository.full_name = 'other/repo' },
  target: p => { p.pr.base.ref = 'develop' },
  'unprotected-main': p => { p.main.protected = false },
  'non-main-landing': p => { p.mainRelationship.status = 'diverged' },
  'readiness-head': p => { p.prReadiness.binding.head_sha = hash('e') },
  'landed-tree': p => { p.landed.tree.sha = hash('e') },
  'composition-head': p => { p.composition.prHeadSha = hash('e') },
  'multi-pr': p => { p.composition.associatedPrs.push(10) },
  'readiness-substitution': p => { p.run.event = 'workflow_dispatch' },
  'workflow-id': p => { p.workflow.id++ },
  'workflow-path': p => { p.run.path = '.github/workflows/fake.yml' },
  'protected-content': p => { p.protectedChanges = ['product/test-planner/'] },
  'gate-definition': p => { p.changedPaths = ['.github/workflows/ci.yml'] },
  'app-publisher': p => { p.checks.at(-1).app.id = 15368 },
  'binding-run': p => { p.checks.at(-1).details_url += '9' },
  'binding-stale': p => { p.checks.at(-1).completed_at = iso(-500_000) },
  'binding-missing': p => { p.checks.pop() },
  'newer-invalidation': p => { p.checks.push({ ...p.checks.at(-1), id: 400, status: 'in_progress', conclusion: null }) },
  'native-publisher': p => { p.checks[0].app.id = 999 },
  'failed-job': p => { p.jobs[2].conclusion = 'failure' },
  'cancelled-job': p => { p.jobs[3].conclusion = 'cancelled' },
  'active-job': p => { p.jobs[2].status = 'in_progress' },
  'missing-job': p => { p.jobs.splice(2, 1) },
  'newer-attempt': p => { p.latestRun.run_attempt++ },
  'active-attempt': p => { p.latestRun.status = 'in_progress' },
  'conclusion-drift': p => { p.latestRun.conclusion = 'cancelled' },
  'stale-run': p => { p.run.updated_at = iso(-31 * 86400000) },
  'stale-origin': p => { p.jobs[2].completed_at = iso(-31 * 86400000) },
  'future-run': p => { p.run.updated_at = iso(86400000) },
  'stale-artifact': p => { p.evidence.manifest.validatedAt = iso(-31 * 86400000) },
  'missing-fragment': p => { p.evidence.fragments.pop() },
  'duplicate-fragment': p => { p.evidence.fragments.push(p.evidence.fragments[0]) },
  'wrong-fragment-origin': p => { p.evidence.fragments[0].run.attempt = 2 },
  'wrong-fragment-tree': p => { p.evidence.fragments[0].run.tree = hash('e') },
  'malformed-count': p => { p.evidence.fragments[0].counts.expected++ },
  'missing-total': p => { delete p.evidence.manifest.gates.missingTotal },
  'count-mismatch': p => { p.evidence.record.counts.expected++ },
  'selected-union': p => { p.evidence.manifest.gates.selected.pop() },
  'manifest-authority-claim': p => { p.evidence.manifest.canAuthorizePostMergeSkip = false },
  'pagination-gap': p => { p.collectionErrors.push('Incomplete pagination') },
  'fallback-unavailable': p => { delete p.fallback },
  'fallback-failed': p => { p.fallback.passed = false },
  'fallback-definition': p => { p.fallback.protectedDefinitionMatches = false },
  'fallback-stale': p => { p.fallback.run.updated_at = iso(-31 * 86400000) },
}
for (const [name, mutate] of Object.entries(negatives)) test(`shadow refuses ${name} without creating authority`, () => {
  const packet = fixture(); mutate(packet)
  const report = evaluateQueueReuseShadow(packet, { now })
  assert.notEqual(report.outcome, 'would_reuse')
  assert.equal(report.canSkip, false)
})

test('earlier successful fragment is accepted only when the effective native job proves that origin', () => {
  const packet = fixture()
  packet.run.run_attempt = packet.latestRun.run_attempt = packet.evidence.record.run.attempt = packet.evidence.manifest.run.attempt = 2
  packet.evidence.topology.expectedRun.attempt = 2
  const report = evaluateQueueReuseShadow(packet, { now })
  assert.equal(report.outcome, 'would_reuse', JSON.stringify(report.conditions.filter(c => !c.passed)))
  assert.ok(report.originatingExecutions.every(j => j.attempt === 1))
  assert.equal(report.potentiallyAvoidable.deliveredRunnerMinutes, 0)
})

test('complete Product evidence retains the existing non-authoritative reporting failure policy visibly', () => {
  for (const conclusion of ['failure', 'cancelled']) {
    const packet = fixture()
    packet.run.conclusion = packet.latestRun.conclusion = conclusion
    packet.jobs.push({ id: 500, name: 'Aggregate CI metrics', status: 'completed', conclusion,
      run_id: packet.run.id, run_attempt: 1, head_sha: packet.run.head_sha })
    const report = evaluateQueueReuseShadow(packet, { now })
    assert.equal(report.outcome, 'would_reuse', JSON.stringify(report.conditions.filter(c => !c.passed)))
    assert.equal(report.run.conclusion, conclusion)
    assert.equal(report.nonAuthoritativeOutcomes.at(-1).conclusion, conclusion)
    assert.equal(report.canSkip, false)
  }
})

test('pagination refuses truncated counts, duplicate records, malformed batches and changing totals', async () => {
  await assert.rejects(readAllReusePages(async () => ({ total_count: 2, jobs: [{ id: 1 }] }), '/x', 'jobs'), /Incomplete/)
  await assert.rejects(readAllReusePages(async () => ({ total_count: 2, jobs: [{ id: 1 }, { id: 1 }] }), '/x', 'jobs'), /duplicate/)
  await assert.rejects(readAllReusePages(async () => ({}), '/x', 'jobs'), /Malformed/)
  let calls = 0
  await assert.rejects(readAllReusePages(async () => ({ total_count: ++calls === 1 ? 101 : 102,
    jobs: calls === 1 ? Array.from({ length: 100 }, (_, i) => ({ id: i + 1 })) : [{ id: 101 }] }), '/x', 'jobs'), /changed/)
})

test('observer unavailability emits a refusal packet and no authority', async () => {
  const { report } = await collectQueueReuseShadow({ runId: 1, prNumber: 1, now,
    reader: { request: async () => { throw new Error('unavailable') } } })
  assert.notEqual(report.outcome, 'would_reuse')
  assert.equal(report.canSkip, false)
})

test('production consumers remain independent of every observer outcome and observation can be disabled', () => {
  for (const path of ['../../../.github/workflows/ci.yml', '../../../.github/workflows/ci-main-provenance.yml',
    '../../../.github/workflows/merge-queue-binding.yml', '../../test-planner/lib/workflow-jobs.mjs', '../lib/provenance.mjs']) {
    const source = readFileSync(new URL(path, import.meta.url), 'utf8')
    assert.doesNotMatch(source, /queue-reuse-(?:shadow|observer)\.mjs|report-queue-reuse-shadow/)
  }
  assert.ok(requiredNativeNames().includes('Infrastructure test suite'))
  assert.throws(() => createReuseReader().request('/repos/other/repo/actions/runs/1'), /outside fixed repository/)
})
