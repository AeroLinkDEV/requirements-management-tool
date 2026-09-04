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
  REQUIRED_JOBS,
  NON_AUTHORITATIVE_JOB_IDS,
  TRUSTED_SURFACE_PREFIXES,
  SHARDED_JOB_GROUPS,
} from '../lib/merge-authority.mjs'

const REPOSITORY = 'AeroLinkDEV/requirements-management-tool'
const HEAD_SHA = 'a'.repeat(40)
const RUN_ID = 424242
const RUN_ATTEMPT = 2

function allJobsSuccess(runId = RUN_ID, runAttempt = RUN_ATTEMPT) {
  const jobs = [
    { name: CLASSIFIER_JOB_NAME, conclusion: 'success', runId, runAttempt },
    { name: 'API test suite (1/3)', conclusion: 'success', runId, runAttempt },
    { name: 'API test suite (2/3)', conclusion: 'success', runId, runAttempt },
    { name: 'API test suite (3/3)', conclusion: 'success', runId, runAttempt },
    { name: 'Domain test suite', conclusion: 'success', runId, runAttempt },
    { name: 'Infrastructure test suite', conclusion: 'success', runId, runAttempt },
    { name: 'Client lint, type-check, and build', conclusion: 'success', runId, runAttempt },
    { name: 'Operator and recovery script contracts', conclusion: 'success', runId, runAttempt },
    { name: 'Browser journeys (1/4)', conclusion: 'success', runId, runAttempt },
    { name: 'Browser journeys (2/4)', conclusion: 'success', runId, runAttempt },
    { name: 'Browser journeys (3/4)', conclusion: 'success', runId, runAttempt },
    { name: 'Browser journeys (4/4)', conclusion: 'success', runId, runAttempt },
    { name: 'Browser journeys on the production build', conclusion: 'success', runId, runAttempt },
    { name: 'PostgreSQL migrations and secure bootstrap', conclusion: 'success', runId, runAttempt },
    { name: AGGREGATE_JOB_NAME, conclusion: 'success', runId, runAttempt },
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
    runAttempt: RUN_ATTEMPT,
    status: 'completed',
    conclusion: 'success',
  }
}

function legitimateCandidate() {
  return {
    run: legitimateRun(),
    jobs: allJobsSuccess(),
    changedPaths: [],
    expected: { repository: REPOSITORY, headSha: HEAD_SHA, baseBranch: 'main', runId: RUN_ID, runAttempt: RUN_ATTEMPT },
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

test('refuses queue candidates for a base branch other than the protected one', () => {
  // Another branch's merge queue produces the same ref shape; only main's candidates bind here.
  const result = reasonsFor({
    run: { ...legitimateRun(), headBranch: `${QUEUE_REF_PREFIX}release/7/abc123` },
  })
  assert.equal(result.decision, 'REFUSE')
  assert.ok(result.reasons.some((reason) => reason.startsWith('ref-not-queue') && reason.includes('release')))

  const missingConfig = evaluateMergeGroupCandidate({
    run: legitimateRun(),
    jobs: allJobsSuccess(),
    changedPaths: [],
    expected: { repository: REPOSITORY, headSha: HEAD_SHA },
  })
  assert.equal(missingConfig.decision, 'REFUSE')
  assert.ok(missingConfig.reasons.some((reason) => reason.startsWith('expected-missing')))
})

test('refuses runs that have not completed', () => {
  for (const status of ['in_progress', 'queued', 'requested']) {
    const result = reasonsFor({ run: { ...legitimateRun(), status } })
    assert.equal(result.decision, 'REFUSE', `status=${status} must refuse`)
    assert.ok(result.reasons.some((reason) => reason.startsWith('run-not-completed')), status)
  }
})

test('non-authoritative metrics and reporting failures never veto a valid candidate', () => {
  // The workflow deliberately excludes these jobs from its failure list; the binding must not hand
  // them a merge veto through the run's overall conclusion. Both the run conclusion and every other
  // authoritative input stay intact below except for the named non-authoritative job.
  const withMetricsToolingFailed = {
    jobs: [...allJobsSuccess(), { name: 'CI metrics tooling tests', conclusion: 'failure', runId: RUN_ID, runAttempt: RUN_ATTEMPT }],
  }
  const tooling = reasonsFor(withMetricsToolingFailed)
  assert.equal(tooling.decision, 'PASS', 'a failed metrics-tooling job must not refuse valid product evidence')
  assert.deepEqual(tooling.reasons, [])

  const withMetricsReportFailed = {
    jobs: [...allJobsSuccess(), { name: 'Aggregate CI metrics', conclusion: 'failure', runId: RUN_ID, runAttempt: RUN_ATTEMPT }],
  }
  const report = reasonsFor(withMetricsReportFailed)
  assert.equal(report.decision, 'PASS', 'a failed metrics-report job must not refuse valid product evidence')
  assert.deepEqual(report.reasons, [])

  // Even a red run conclusion (cancelled while reporting finished) must not veto when the full
  // authoritative set succeeded.
  const redConclusion = reasonsFor({
    run: { ...legitimateRun(), conclusion: 'failure' },
    jobs: [...allJobsSuccess(), { name: 'CI metrics tooling tests', conclusion: 'failure', runId: RUN_ID, runAttempt: RUN_ATTEMPT }],
  })
  assert.equal(redConclusion.decision, 'PASS')
  assert.deepEqual(redConclusion.reasons, [])
})

test('authoritative product evidence remains binding authority', () => {
  // The removal of run-conclusion authority must not weaken product authority: a failed gate, a
  // failed shard, and a failed aggregate each still refuse.
  const failedGate = reasonsFor({
    jobs: allJobsSuccess().map((job) => (job.name === 'Domain test suite' ? { ...job, conclusion: 'failure' } : job)),
  })
  assert.equal(failedGate.decision, 'REFUSE')

  const failedShard = reasonsFor({
    jobs: allJobsSuccess().map((job) => (job.name === 'API test suite (2/3)' ? { ...job, conclusion: 'failure' } : job)),
  })
  assert.equal(failedShard.decision, 'REFUSE')

  const failedAggregate = reasonsFor({
    jobs: allJobsSuccess().map((job) => (job.name === AGGREGATE_JOB_NAME ? { ...job, conclusion: 'failure' } : job)),
  })
  assert.equal(failedAggregate.decision, 'REFUSE')
  assert.ok(failedAggregate.reasons.some((reason) => reason.includes(AGGREGATE_JOB_NAME)))
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

  const noExpectedRunId = evaluateMergeGroupCandidate({
    run: legitimateRun(),
    jobs: allJobsSuccess(),
    changedPaths: [],
    expected: { repository: REPOSITORY, headSha: HEAD_SHA, baseBranch: 'main' },
  })
  assert.equal(noExpectedRunId.decision, 'REFUSE', 'a missing trusted run id must fail closed, not skip membership checking')
  assert.ok(noExpectedRunId.reasons.some((reason) => reason.startsWith('expected-missing')))

  const foreignJob = reasonsFor({
    jobs: allJobsSuccess().map((job) => (job.name === 'Domain test suite' ? { ...job, runId: 999999 } : job)),
  })
  assert.equal(foreignJob.decision, 'REFUSE')
  assert.ok(foreignJob.reasons.some((reason) => reason.startsWith('job-from-foreign-run')))

  const unmappedJob = reasonsFor({
    jobs: allJobsSuccess().map((job) => (job.name === 'Domain test suite' ? { name: job.name, conclusion: job.conclusion } : job)),
  })
  assert.equal(unmappedJob.decision, 'REFUSE')
  assert.ok(unmappedJob.reasons.some((reason) => reason.startsWith('job-run-id-missing') && reason.includes('Domain test suite')))
})

test('binds the trusted run attempt; retained earlier attempts bind, later attempts refuse', () => {
  // Rerun attempts share a run id, and the run conclusion is not consulted — so attempt identity is
  // what stops a rerun from being authorized by records that predate it.
  const otherAttempt = reasonsFor({ run: { ...legitimateRun(), runAttempt: 3 } })
  assert.equal(otherAttempt.decision, 'REFUSE')
  assert.ok(otherAttempt.reasons.some((reason) => reason.startsWith('run-attempt-mismatch')))

  const noAttempt = reasonsFor({ run: { ...legitimateRun(), runAttempt: undefined } })
  assert.equal(noAttempt.decision, 'REFUSE')
  assert.ok(noAttempt.reasons.some((reason) => reason.startsWith('run-attempt-missing')))

  // A partial rerun ("Re-run failed jobs") retains successful jobs at their earlier attempt — the
  // legitimate recovery path must not stall, so retained records of the SAME run bind.
  const retainedJobs = reasonsFor({
    expected: { repository: REPOSITORY, headSha: HEAD_SHA, baseBranch: 'main', runId: RUN_ID, runAttempt: 3 },
    run: { ...legitimateRun(), runAttempt: 3 },
    jobs: allJobsSuccess(RUN_ID, 1),
  })
  assert.equal(retainedJobs.decision, 'PASS', 'retained jobs from an earlier attempt are valid evidence of the same run')
  assert.deepEqual(retainedJobs.reasons, [])

  // A record from a LATER attempt than the one being bound cannot belong to this validation.
  const futureJob = reasonsFor({
    jobs: allJobsSuccess().map((job) => (job.name === 'Browser journeys (2/4)' ? { ...job, runAttempt: RUN_ATTEMPT + 1 } : job)),
  })
  assert.equal(futureJob.decision, 'REFUSE')
  assert.ok(futureJob.reasons.some((reason) => reason.startsWith('job-attempt-mismatch') && reason.includes('Browser journeys (2/4)')))

  const attemptlessJob = reasonsFor({
    jobs: allJobsSuccess().map((job) => {
      const { runAttempt, ...rest } = job
      return job.name === 'Browser journeys (2/4)' ? rest : job
    }),
  })
  assert.equal(attemptlessJob.decision, 'REFUSE')
  assert.ok(attemptlessJob.reasons.some((reason) => reason.startsWith('job-attempt-missing') && reason.includes('Browser journeys (2/4)')))

  const noExpectedAttempt = evaluateMergeGroupCandidate({
    run: legitimateRun(),
    jobs: allJobsSuccess(),
    changedPaths: [],
    expected: { repository: REPOSITORY, headSha: HEAD_SHA, baseBranch: 'main', runId: RUN_ID },
  })
  assert.equal(noExpectedAttempt.decision, 'REFUSE')
  assert.ok(noExpectedAttempt.reasons.some((reason) => reason.startsWith('expected-missing')))
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
  const lines = workflow.split(/\r?\n/)
  // The display name must match exactly: renaming the workflow keeps every job intact while every
  // real run would be refused by workflow-name-mismatch.
  assert.match(workflow, new RegExp(`^name: ${PRODUCT_WORKFLOW_NAME.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'm'))

  // REQUIRED_JOBS is the single source of truth for the verifier's fixed-name gates; this test holds
  // no independent copy. Exact YAML name values, anchored to the whole line: a renamed survivor such
  // as 'Domain test suite v2' must not satisfy the check for 'Domain test suite'.
  for (const name of [...REQUIRED_JOBS, CLASSIFIER_JOB_NAME, AGGREGATE_JOB_NAME]) {
    assert.match(
      workflow,
      new RegExp(`^    name: ${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'm'),
      `ci.yml must still define the gate job '${name}' exactly`,
    )
  }

  const jobDisplayName = (id) => {
    const jobStart = lines.findIndex((line) => line === `  ${id}:`)
    assert.ok(jobStart >= 0, `ci.yml must define the workflow job '${id}'`)
    const nameLine = lines.slice(jobStart, jobStart + 40).find((line) => /^    name: /.test(line))
    assert.ok(nameLine, `workflow job '${id}' must declare a display name`)
    return nameLine.slice('    name: '.length)
  }

  // Every dependency of the gate job must be accounted for by the verifier model — a REQUIRED_JOBS
  // entry, the classifier, the aggregate, a sharded group's template, or the declared
  // non-authoritative set. Without this direction, deleting an entry from REQUIRED_JOBS would
  // silently weaken merge authority while every remaining name still existed in ci.yml.
  const shardedTemplates = SHARDED_JOB_GROUPS.map((group) => `${group.name} (\${{ matrix.shard }}/\${{ strategy.job-total }})`)
  const covered = new Set([CLASSIFIER_JOB_NAME, AGGREGATE_JOB_NAME, ...REQUIRED_JOBS, ...shardedTemplates])
  const gateIndex = lines.findIndex((line) => line === '  gate:')
  assert.ok(gateIndex >= 0, "ci.yml must define the 'gate' job")
  const needsLine = lines.slice(gateIndex, gateIndex + 40).find((line) => /^    needs: \[/.test(line))
  assert.ok(needsLine, "the 'gate' job must declare its needs")
  const gateNeeds = needsLine.match(/\[([^\]]+)\]/)[1].split(',').map((entry) => entry.trim())
  for (const id of gateNeeds) {
    if (NON_AUTHORITATIVE_JOB_IDS.includes(id)) continue
    const display = jobDisplayName(id)
    assert.ok(
      covered.has(display),
      `gate dependency '${id}' ('${display}') is not accounted for by the verifier model — add it to REQUIRED_JOBS/SHARDED_JOB_GROUPS or, if it is deliberately non-authoritative, to NON_AUTHORITATIVE_JOB_IDS`,
    )
  }
  // Reverse direction: each REQUIRED_JOBS entry must still be a gate dependency of the workflow.
  const gateDependencyNames = gateNeeds.filter((id) => !NON_AUTHORITATIVE_JOB_IDS.includes(id)).map((id) => jobDisplayName(id))
  for (const name of REQUIRED_JOBS) {
    assert.ok(
      gateDependencyNames.includes(name),
      `REQUIRED_JOBS entry '${name}' no longer corresponds to any gate dependency in ci.yml; if the workflow genuinely stopped requiring it, remove it from the verifier consciously`,
    )
  }
  assert.equal(jobDisplayName('gate'), AGGREGATE_JOB_NAME, "the 'gate' job's display name must remain the aggregate the verifier binds")

  for (const group of SHARDED_JOB_GROUPS) {
    const escaped = group.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
    const jobPattern = new RegExp(`^    name: ${escaped} \\(\\$\\{\\{ matrix\\.shard \\}\\}/\\$\\{\\{ strategy\\.job-total \\}\\}\\)$`, 'm')
    assert.match(workflow, jobPattern, `ci.yml must still define the sharded group '${group.name}' exactly`)
  }
  for (const group of SHARDED_JOB_GROUPS) {
    // Parse THIS group's own matrix, not any shard array of a matching size elsewhere: browser-full
    // also runs three shards, so a global size list would mask an API or browser-pr change.
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
