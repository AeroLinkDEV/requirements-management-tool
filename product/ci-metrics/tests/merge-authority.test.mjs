// Contract and decision tests for the trusted merge-group binding (product/ci-metrics/lib/merge-authority.mjs).
//
// The verifier is the thing that will eventually publish the merge-authority check for a queue
// candidate, so its refusals are specified exhaustively here: one positive case (a legitimate
// merge-group run binds) and one negative case per refusal class. Everything runs on synthetic
// metadata — no network, no credentials, no GitHub API.
//
// A final test pins the module's expected job topology against .github/workflows/ci.yml, so a shard
// change or a gate rename that outdates the verifier refuses loudly in tests instead of silently
// stalling real queue entries in production.

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import {
  evaluateMergeGroupCandidate,
  QUEUE_REF_PREFIX,
  PRODUCT_WORKFLOW_NAME,
  PRODUCT_WORKFLOW_PATH,
  AGGREGATE_JOB_NAME,
  CLASSIFIER_JOB_NAME,
  TRUSTED_SURFACE_PREFIXES,
  SHARDED_JOB_GROUPS,
} from '../lib/merge-authority.mjs'

const REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
const HEAD_SHA = 'a'.repeat(40)
const RUN_ID = 424242

function allJobsSuccess(runId = RUN_ID) {
  const jobs = [
    { name: CLASSIFIER_JOB_NAME, conclusion: 'success', runId },
    { name: 'API test suite (1/3)', conclusion: 'success', runId },
    { name: 'API test suite (2/3)', conclusion: 'success', runId },
    { name: 'API test suite (3/3)', conclusion: 'success', runId },
    { name: 'Domain test suite', conclusion: 'success', runId },
    { name: 'Infrastructure test suite', conclusion: 'success', runId },
    { name: 'Client lint, type-check, and build', conclusion: 'success', runId },
    { name: 'Operator and recovery script contracts', conclusion: 'success', runId },
    { name: 'Browser journeys (1/4)', conclusion: 'success', runId },
    { name: 'Browser journeys (2/4)', conclusion: 'success', runId },
    { name: 'Browser journeys (3/4)', conclusion: 'success', runId },
    { name: 'Browser journeys (4/4)', conclusion: 'success', runId },
    { name: 'Browser journeys on the production build', conclusion: 'success', runId },
    { name: 'PostgreSQL migrations and secure bootstrap', conclusion: 'success', runId },
    { name: AGGREGATE_JOB_NAME, conclusion: 'success', runId },
  ]
  return jobs
}

function legitimateRun() {
  return {
    repository: REPOSITORY,
    workflowName: PRODUCT_WORKFLOW_NAME,
    workflowPath: PRODUCT_WORKFLOW_PATH,
    event: 'merge_group',
    headSha: HEAD_SHA,
    headBranch: `${QUEUE_REF_PREFIX}main/909/d4f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9`,
    runId: RUN_ID,
    status: 'completed',
    conclusion: 'success',
  }
}

function legitimateCandidate() {
  return {
    run: legitimateRun(),
    jobs: allJobsSuccess(),
    changedPaths: [],
    expected: { repository: REPOSITORY, headSha: HEAD_SHA, runId: RUN_ID },
  }
}

function reasonsFor(override) {
  const candidate = { ...legitimateCandidate(), ...override }
  const { run, jobs, changedPaths, expected } = candidate
  return evaluateMergeGroupCandidate({ run, jobs, changedPaths, expected })
}

test('a legitimate merge-group candidate binds', () => {
  const result = reasonsFor({})
  assert.equal(result.decision, 'PASS')
  assert.deepEqual(result.reasons, [])
})

test('refuses non-merge-group events', () => {
  for (const event of ['pull_request', 'workflow_dispatch', 'push', 'schedule']) {
    const result = reasonsFor({ run: { ...legitimateRun(), event } })
    assert.equal(result.decision, 'REFUSE', `event ${event} must refuse`)
    assert.ok(result.reasons.some((reason) => reason.startsWith('event-not-merge-group')), event)
  }
})

test('refuses a run from another repository', () => {
  const result = reasonsFor({ run: { ...legitimateRun(), repository: 'someone-else/requirements-management-tool' } })
  assert.equal(result.decision, 'REFUSE')
  assert.ok(result.reasons.some((reason) => reason.startsWith('run-repository-mismatch')))
})

test('refuses a run of a different workflow', () => {
  for (const [key, value] of [['workflowName', 'Fast PR feedback (advisory)'], ['workflowPath', '.github/workflows/fake.yml']]) {
    const result = reasonsFor({ run: { ...legitimateRun(), [key]: value } })
    assert.equal(result.decision, 'REFUSE', `${key} must refuse`)
  }
})

test('refuses evidence bound to a different SHA than the queue candidate', () => {
  const result = reasonsFor({ run: { ...legitimateRun(), headSha: 'b'.repeat(40) } })
  assert.equal(result.decision, 'REFUSE')
  assert.ok(result.reasons.some((reason) => reason.startsWith('head-sha-mismatch')))
})

test('refuses refs that are not merge-queue candidates', () => {
  for (const headBranch of ['main', 'refs/heads/main', 'feature/881-launcher', 'gh-readonly-something']) {
    const result = reasonsFor({ run: { ...legitimateRun(), headBranch } })
    assert.equal(result.decision, 'REFUSE', `ref '${headBranch}' must refuse`)
    assert.ok(result.reasons.some((reason) => reason.startsWith('ref-not-queue')), headBranch)
  }
})

test('refuses runs that have not completed successfully', () => {
  for (const [key, value] of [['status', 'in_progress'], ['conclusion', 'failure'], ['conclusion', 'cancelled']]) {
    const result = reasonsFor({ run: { ...legitimateRun(), [key]: value } })
    assert.equal(result.decision, 'REFUSE', `${key}=${value} must refuse`)
  }
})

test('refuses missing, skipped, failed, or cancelled expected jobs', () => {
  const missing = reasonsFor({ jobs: allJobsSuccess().filter((job) => job.name !== 'Domain test suite') })
  assert.equal(missing.decision, 'REFUSE')
  assert.ok(missing.reasons.some((reason) => reason.startsWith('missing-job: Domain')))

  for (const conclusion of ['skipped', 'failure', 'cancelled']) {
    const jobs = allJobsSuccess().map((job) => (job.name === 'Client lint, type-check, and build' ? { ...job, conclusion } : job))
    const result = reasonsFor({ jobs })
    assert.equal(result.decision, 'REFUSE', `client ${conclusion} must refuse`)
    assert.ok(result.reasons.some((reason) => reason.includes('Client lint, type-check, and build')), conclusion)
  }
})

test('refuses a failed or skipped shard while other shards succeeded', () => {
  const jobs = allJobsSuccess().map((job) => (job.name === 'Browser journeys (3/4)' ? { ...job, conclusion: 'skipped' } : job))
  const result = reasonsFor({ jobs })
  assert.equal(result.decision, 'REFUSE')
  assert.ok(result.reasons.some((reason) => reason.includes('Browser journeys shard 3')))
})

test('refuses an ambiguous aggregate', () => {
  const jobs = [...allJobsSuccess(), { name: AGGREGATE_JOB_NAME, conclusion: 'success', runId: RUN_ID }]
  const result = reasonsFor({ jobs })
  assert.equal(result.decision, 'REFUSE')
  assert.ok(result.reasons.some((reason) => reason.startsWith('ambiguous-aggregate')))
})

test('refuses when the classifier did not run', () => {
  const jobs = allJobsSuccess().filter((job) => job.name !== CLASSIFIER_JOB_NAME)
  const result = reasonsFor({ jobs })
  assert.equal(result.decision, 'REFUSE')
  assert.ok(result.reasons.some((reason) => reason.startsWith('missing-job: Classify changed product areas')))
})

test('refuses jobs that belong to a different run', () => {
  const jobs = allJobsSuccess().map((job) => (job.name === 'Domain test suite' ? { ...job, runId: 999999 } : job))
  const result = reasonsFor({ jobs })
  assert.equal(result.decision, 'REFUSE')
  assert.ok(result.reasons.some((reason) => reason.startsWith('job-from-foreign-run')))
})

test('binds the trusted expected run id, refusing any other run', () => {
  const otherRun = reasonsFor({ run: { ...legitimateRun(), runId: 999999 } })
  assert.equal(otherRun.decision, 'REFUSE')
  assert.ok(otherRun.reasons.some((reason) => reason.startsWith('run-id-mismatch')))

  const noRunId = reasonsFor({ run: { ...legitimateRun(), runId: undefined } })
  assert.equal(noRunId.decision, 'REFUSE')
  assert.ok(noRunId.reasons.some((reason) => reason.startsWith('run-id-missing')))

  const foreignJob = reasonsFor({
    jobs: allJobsSuccess().map((job) => (job.name === 'Domain test suite' ? { ...job, runId: 999999 } : job)),
  })
  assert.equal(foreignJob.decision, 'REFUSE')
  assert.ok(foreignJob.reasons.some((reason) => reason.startsWith('job-from-foreign-run')))
})

test('refuses an incomplete, over-counted, or inconsistent shard set', () => {
  const incomplete = reasonsFor({ jobs: allJobsSuccess().filter((job) => job.name !== 'API test suite (3/3)') })
  assert.equal(incomplete.decision, 'REFUSE')
  assert.ok(incomplete.reasons.some((reason) => reason.startsWith('shard-set-incomplete: API test suite')))

  const drifted = reasonsFor({
    jobs: allJobsSuccess()
      .filter((job) => !/^API test suite \(/.test(job.name))
      .concat([1, 2, 3, 4].map((shard) => ({ name: `API test suite (${shard}/4)`, conclusion: 'success', runId: RUN_ID }))),
  })
  assert.equal(drifted.decision, 'REFUSE')
  assert.ok(drifted.reasons.some((reason) => reason.startsWith('shard-count-drift: API test suite')))

  const inconsistent = reasonsFor({
    jobs: allJobsSuccess().map((job) => (job.name === 'API test suite (3/3)' ? { ...job, name: 'API test suite (3/4)' } : job)),
  })
  assert.equal(inconsistent.decision, 'REFUSE')
  assert.ok(inconsistent.reasons.some((reason) => reason.startsWith('shard-set-inconsistent: API test suite')))
})

test('refuses when the surface comparison is missing entirely', () => {
  const result = reasonsFor({ changedPaths: undefined })
  assert.equal(result.decision, 'REFUSE')
  assert.ok(result.reasons.some((reason) => reason.startsWith('surface-comparison-missing')))
})

test('refuses when the candidate altered trusted merge machinery', () => {
  for (const path of [
    '.github/workflows/ci.yml',
    '.github/workflows/request-full-ci.yml',
    '.github/CODEOWNERS',
    'product/test-planner/lib/classify.mjs',
    'product/ci-metrics/lib/merge-authority.mjs',
  ]) {
    const result = reasonsFor({ changedPaths: [path] })
    assert.equal(result.decision, 'REFUSE', `altered ${path} must refuse`)
    assert.ok(result.reasons.some((reason) => reason.startsWith('trusted-surface-modified')), path)
  }
})

test('product changes under trusted directories are still expected to differ — the verifier only judges supplied paths', () => {
  const result = reasonsFor({ changedPaths: ['product/src/AeroLink.Api/Program.cs', 'README.md', 'product/client/src/App.tsx'] })
  assert.equal(result.decision, 'PASS')
  assert.deepEqual(result.reasons, [])
})

test('refuses malformed surface comparisons', () => {
  for (const changedPaths of [[null], [42], [''], ['.github/workflows/ci.yml', undefined], [{ from: '.github/workflows/ci.yml' }], [{ to: 'docs/x.yml' }]]) {
    const result = reasonsFor({ changedPaths })
    assert.equal(result.decision, 'REFUSE', `changedPaths ${JSON.stringify(changedPaths)} must refuse`)
    assert.ok(result.reasons.some((reason) => reason.startsWith('surface-comparison-malformed')), JSON.stringify(changedPaths))
  }
})

test('inspects both sides of a rename for trusted-surface removal', () => {
  // Moving trusted machinery out of a trusted prefix must refuse on the FROM side: the destination
  // path matches no prefix rule, so only the rename's source reveals the removal.
  const renamedOut = reasonsFor({ changedPaths: [{ from: '.github/workflows/request-full-ci.yml', to: 'docs/request-full-ci.yml' }] })
  assert.equal(renamedOut.decision, 'REFUSE')
  assert.ok(renamedOut.reasons.some((reason) => reason.startsWith('trusted-surface-modified') && reason.includes('.github/workflows/request-full-ci.yml')))

  const renamedWithin = reasonsFor({ changedPaths: [{ from: 'product/client/src/App.tsx', to: 'product/client/src/App2.tsx' }] })
  assert.equal(renamedWithin.decision, 'PASS')
  assert.deepEqual(renamedWithin.reasons, [])
})

test('refuses malformed input fail-closed', () => {
  assert.equal(evaluateMergeGroupCandidate(undefined).decision, 'REFUSE')
  assert.equal(evaluateMergeGroupCandidate({ run: null }).decision, 'REFUSE')
  assert.equal(evaluateMergeGroupCandidate({ run: legitimateRun() }).decision, 'REFUSE', 'missing expected configuration must refuse')
  assert.equal(reasonsFor({ jobs: [] }).decision, 'REFUSE')
  const noJobs = reasonsFor({ jobs: undefined })
  assert.equal(noJobs.decision, 'REFUSE')
  assert.ok(noJobs.reasons.some((reason) => reason.startsWith('jobs-missing')))
})

test('every refusal reports machine-readable reasons, and PASS reports none', () => {
  const refusal = reasonsFor({ run: { ...legitimateRun(), event: 'push' } })
  assert.equal(refusal.decision, 'REFUSE')
  assert.ok(refusal.reasons.length > 0 && refusal.reasons.every((reason) => /^[a-z-]+: /.test(reason)))

  const pass = reasonsFor({})
  assert.equal(pass.decision, 'PASS')
  assert.deepEqual(pass.reasons, [])
})

test('the expected job topology matches the current workflow', () => {
  const workflow = readFileSync(new URL('../../../.github/workflows/ci.yml', import.meta.url), 'utf8')
  // The display name must match exactly: renaming the workflow keeps every job intact while every
  // real run would be refused by workflow-name-mismatch.
  assert.match(workflow, new RegExp(`^name: ${PRODUCT_WORKFLOW_NAME.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'm'))
  for (const name of [
    'Domain test suite',
    'Infrastructure test suite',
    'Client lint, type-check, and build',
    'Operator and recovery script contracts',
    'Browser journeys on the production build',
    'PostgreSQL migrations and secure bootstrap',
    AGGREGATE_JOB_NAME,
    CLASSIFIER_JOB_NAME,
  ]) {
    // Exact YAML name values, anchored to the whole line: a renamed survivor such as
    // 'Domain test suite v2' must not satisfy the check for 'Domain test suite'.
    assert.match(
      workflow,
      new RegExp(`^    name: ${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'm'),
      `ci.yml must still define the gate job '${name}' exactly`,
    )
  }
  for (const group of SHARDED_JOB_GROUPS) {
    const escaped = group.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
    const jobPattern = new RegExp(`^    name: ${escaped} \\(\\$\\{\\{ matrix\\.shard \\}\\}/\\$\\{\\{ strategy\\.job-total \\}\\}\\)$`, 'm')
    assert.match(workflow, jobPattern, `ci.yml must still define the sharded group '${group.name}' exactly`)
  }
  for (const group of SHARDED_JOB_GROUPS) {
    const escaped = group.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
    // Parse THIS group's own matrix, not any shard array of a matching size elsewhere: browser-full
    // also runs three shards, so a global size list would mask an API or browser-pr change.
    const lines = workflow.split(/\r?\n/)
    // Exact full line, so a renamed survivor like 'API test suite (…template…) v2' cannot satisfy
    // the lookup while the verifier's anchored runtime pattern rejects the renamed jobs.
    const shardedNameLine = `    name: ${group.name} (\${{ matrix.shard }}/\${{ strategy.job-total }})`
    const nameIndex = lines.findIndex((line) => line === shardedNameLine)
    assert.ok(nameIndex >= 0, `ci.yml must define the sharded job '${group.name}'`)
    const matrixLine = lines.slice(nameIndex, nameIndex + 60).find((line) => /^\s+shard: \[[0-9, ]+\]$/.test(line))
    assert.ok(matrixLine, `the '${group.name}' job must declare its shard matrix`)
    // The values matter, not just the count: a 0-based matrix of the same length would satisfy a
    // size check while the verifier refuses every 0/N job as out of range.
    const entries = matrixLine.match(/\[([0-9, ]+)\]/)[1].split(',').map((entry) => Number(entry.trim()))
    assert.deepEqual(
      entries,
      Array.from({ length: group.expectedShards }, (_, index) => index + 1),
      `the '${group.name}' job's shard matrix must be exactly 1..${group.expectedShards}; update SHARDED_JOB_GROUPS with the workflow change`,
    )
  }
})
