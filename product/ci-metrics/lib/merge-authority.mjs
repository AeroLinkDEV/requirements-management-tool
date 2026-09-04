// Pure decision logic for the trusted merge-group binding (#549, phase B).
//
// The merge queue validates a candidate with a merge_group run of the Product quality gate, but that
// run's workflow definition comes from the composed candidate tree — so its own green tick can never
// be the merge authority by itself. This module decides, from supplied metadata only, whether a
// completed merge-group run may be bound to its queue SHA by the trusted default-branch verifier:
//
//   1. the run must be the real Product gate on this repository's queue refs, and completed;
//   2. every required job must belong to that exact run, have executed, and succeeded;
//   3. the candidate must not alter trusted merge machinery relative to the default branch, or the
//      gates that ran were not the gates main ships and the evidence cannot be bound.
//
// Authority comes exclusively from that enumerated job set. The run's overall conclusion is not
// consulted, because non-authoritative reporting jobs (see NON_AUTHORITATIVE_JOB_IDS) can fail the
// run by design without any product gate failing.
//
// Nothing here performs I/O or network calls: the caller (a workflow_run verifier reading GitHub API
// payloads and a git diff) supplies everything, and any missing input refuses rather than guessing.
// Every refusal carries machine-readable reasons so a stalled queue entry can be dispositioned
// without re-deriving the decision.

/** Only queue-candidate refs created by GitHub's merge queue may bind evidence. */
export const QUEUE_REF_PREFIX = 'gh-readonly-queue/'

export const PRODUCT_WORKFLOW_NAME = 'Product quality gate'
export const PRODUCT_WORKFLOW_PATH = '.github/workflows/ci.yml'

/** The single job whose success the binding attests to. More than one is ambiguity, not richness. */
export const AGGREGATE_JOB_NAME = 'Full Product evidence aggregate'

/** The classifier must have run: broad merge-group classification is what makes the gates required. */
export const CLASSIFIER_JOB_NAME = 'Classify changed product areas'

// Workflow jobs that are deliberately NOT merge authority: the workflow itself excludes them from
// its failure list, so their failure turns the run red without any product gate failing. Binding
// never consults them, and the run's overall conclusion is ignored for the same reason — otherwise
// observability would gain a merge veto the Product gate never gave it. The parity test uses this
// set to account for every gate dependency the verifier does not require.
export const NON_AUTHORITATIVE_JOB_IDS = ['metrics-tooling', 'metrics-report']

// HARD CUTOVER REQUIREMENT (#549): before the workflow_run verifier that consumes this module goes
// live, product/ci-metrics/tests/merge-authority.test.mjs MUST be added to the metrics-tooling CI
// invocation (and to the documented full-suite command) in .github/workflows/ci.yml — together with,
// or before, that verifier. Until then this suite runs only from local/PR-lane commands, which is
// acceptable solely because no live path consumes the module yet.

// Trusted merge machinery. A candidate that changes any of these surfaces relative to the default
// branch is evaluating itself with machinery the candidate chose, so the evidence cannot be bound.
// The verifier's own definition already comes from the default branch (workflow_run); this closes the
// candidate-composed Product run against the same class of substitution.
export const TRUSTED_SURFACE_PREFIXES = ['.github/', 'product/test-planner/', 'product/ci-metrics/']

// Exact-name jobs that must exist in this run and conclude success.
export const REQUIRED_JOBS = [
  'Domain test suite',
  'Infrastructure test suite',
  'Client lint, type-check, and build',
  'Operator and recovery script contracts',
  'Browser journeys on the production build',
  'PostgreSQL migrations and secure bootstrap',
]

// Sharded groups. Shard counts are deliberately tuned over time, so the verifier requires a complete,
// self-consistent i/N set at the configured size rather than fixed names; the test suite pins these
// numbers against ci.yml so drift on either side is caught before it can matter.
export const SHARDED_JOB_GROUPS = [
  { name: 'API test suite', pattern: /^API test suite \((\d+)\/(\d+)\)$/, expectedShards: 3 },
  { name: 'Browser journeys', pattern: /^Browser journeys \((\d+)\/(\d+)\)$/, expectedShards: 4 },
]

const JOB_CONCLUSION_SUCCESS = 'success'
const RUN_STATUS_COMPLETED = 'completed'
const MERGE_GROUP_EVENT = 'merge_group'

function duplicateNames(jobs, names) {
  const duplicates = []
  for (const name of names) {
    if (jobs.filter((job) => job?.name === name).length > 1) duplicates.push(name)
  }
  return duplicates
}

function collectShardReasons(jobs, reasons) {
  for (const group of SHARDED_JOB_GROUPS) {
    const matched = jobs.filter((job) => typeof job?.name === 'string' && group.pattern.test(job.name))
    if (matched.length === 0) {
      reasons.push(`missing-job: no ${group.name} shards ran`)
      continue
    }
    const parsed = matched.map((job) => {
      const [, shard, total] = group.pattern.exec(job.name)
      return { job, shard: Number(shard), total: Number(total) }
    })
    const totals = new Set(parsed.map((entry) => entry.total))
    if (totals.size !== 1) {
      reasons.push(`shard-set-inconsistent: ${group.name} shards declare different totals (${[...totals].join(', ')})`)
      continue
    }
    const total = parsed[0].total
    const shards = parsed.map((entry) => entry.shard)
    const complete = shards.length === total && new Set(shards).size === shards.length &&
      [...shards].every((shard) => Number.isInteger(shard) && shard >= 1 && shard <= total)
    if (!complete) {
      reasons.push(`shard-set-incomplete: ${group.name} ran ${shards.length} of ${total} shards (${shards.join(', ')})`)
    }
    if (total !== group.expectedShards) {
      reasons.push(`shard-count-drift: ${group.name} ran ${total} shards but the verifier expects ${group.expectedShards}; update SHARDED_JOB_GROUPS with the workflow change`)
    }
    for (const { job, shard } of parsed) {
      if (job.conclusion !== JOB_CONCLUSION_SUCCESS) {
        reasons.push(`job-not-success: ${group.name} shard ${shard} concluded '${job.conclusion ?? 'unknown'}'`)
      }
    }
  }
}

/**
 * Decide whether a completed merge-group run may be bound to its queue SHA.
 *
 * @param {object} input
 * @param {object} input.run GitHub metadata for the triggering run: repository, workflowName,
 *   workflowPath, event, headSha, headBranch, runId?, status (conclusion is deliberately unused —
 *   see NON_AUTHORITATIVE_JOB_IDS).
 * @param {Array<{name: string, conclusion: string, runId?: number|string, runAttempt?: number}>} input.jobs the jobs of
 *   exactly this run (caller resolves via the runs API); runId included when known.
 * @param {Array<string|{from: string, to: string}>|undefined} input.changedPaths paths differing
 *   between the candidate queue SHA and the default branch; a rename supplies both sides, because
 *   the FROM side is the one that can remove trusted machinery. Refuses when absent — an unverified
 *   surface is not a trusted one.
 * @param {object} input.expected trusted configuration from the verifier's own context:
 *   { repository, headSha, baseBranch, runId, runAttempt } — all mandatory; omitting any refuses.
 * @returns {{decision: 'PASS'|'REFUSE', reasons: string[]}}
 */
export function evaluateMergeGroupCandidate(input) {
  const reasons = []
  const { run, jobs, changedPaths, expected } = input ?? {}

  if (!run || typeof run !== 'object') {
    return { decision: 'REFUSE', reasons: ['run-metadata-missing: no triggering-run metadata was supplied'] }
  }
  if (!expected || typeof expected !== 'object' || typeof expected.repository !== 'string' || typeof expected.headSha !== 'string' || typeof expected.baseBranch !== 'string' || (typeof expected.runId !== 'string' && typeof expected.runId !== 'number') || typeof expected.runAttempt !== 'number' || expected.runAttempt < 1) {
    return { decision: 'REFUSE', reasons: ['expected-missing: the verifier must supply its own trusted repository, head SHA, protected base branch, run id, and run attempt'] }
  }

  if (run.repository !== expected.repository) {
    reasons.push(`run-repository-mismatch: run belongs to '${run.repository ?? 'unknown'}', expected '${expected.repository}'`)
  }
  if (run.workflowName !== PRODUCT_WORKFLOW_NAME) {
    reasons.push(`workflow-name-mismatch: run is '${run.workflowName ?? 'unknown'}', expected '${PRODUCT_WORKFLOW_NAME}'`)
  }
  if (run.workflowPath !== PRODUCT_WORKFLOW_PATH) {
    reasons.push(`workflow-path-mismatch: run is '${run.workflowPath ?? 'unknown'}', expected '${PRODUCT_WORKFLOW_PATH}'`)
  }
  if (run.event !== MERGE_GROUP_EVENT) {
    reasons.push(`event-not-merge-group: run event is '${run.event ?? 'unknown'}'; only merge_group runs validate a queue candidate`)
  }
  if (run.headSha !== expected.headSha) {
    reasons.push(`head-sha-mismatch: run head '${run.headSha ?? 'unknown'}' does not match the queue candidate '${expected.headSha}'`)
  }
  // The trusted configuration's run id binds everything: the run itself, and every job's membership
  // in it. It is mandatory, not optional — omitting it must fail closed, or a missing binding input
  // would silently turn into authorization. Metadata resolved from a different Product run at the
  // same SHA must not authorize.
  if (typeof run.runId === 'undefined') {
    reasons.push('run-id-missing: the run metadata does not carry its run id, so membership in the expected run is unverifiable')
  } else if (String(run.runId) !== String(expected.runId)) {
    reasons.push(`run-id-mismatch: run ${run.runId} is not the expected run ${expected.runId}`)
  }
  // Rerun attempts share a run id, and the run conclusion is deliberately not consulted — so without
  // attempt binding, job records from a successful first attempt could authorize a failed rerun.
  // This mirrors the attempt binding provenance.mjs already applies to validated manifests.
  if (typeof run.runAttempt !== 'number') {
    reasons.push('run-attempt-missing: the run metadata does not carry its attempt number')
  } else if (run.runAttempt !== expected.runAttempt) {
    reasons.push(`run-attempt-mismatch: run attempt ${run.runAttempt} is not the expected attempt ${expected.runAttempt}`)
  }
  // Queue refs name their base branch and end with the candidate's head SHA, and the workflow
  // subscribes to merge_group for any base — so a slash-bearing sibling branch's queue ref (base
  // 'main/foo' begins 'gh-readonly-queue/main/foo/', satisfying base 'main's prefix) must be
  // excluded by the SHA it carries: a different base composes a different candidate tree and
  // therefore a different head SHA, which cannot end this ref.
  const expectedQueuePrefix = `${QUEUE_REF_PREFIX}${expected.baseBranch}/`
  if (
    typeof run.headBranch !== 'string' ||
    !run.headBranch.startsWith(expectedQueuePrefix) ||
    !run.headBranch.endsWith(`-${expected.headSha}`)
  ) {
    reasons.push(`ref-not-queue: run ref '${run.headBranch ?? 'unknown'}' is not a '${expectedQueuePrefix}<candidate>-${expected.headSha}' ref`)
  }
  // The run must be completed, but its overall conclusion is deliberately NOT consulted: by design
  // the workflow treats metrics/reporting jobs as non-authoritative, so their failure turns the run
  // conclusion red without any product gate failing. Deriving authority from the conclusion would
  // hand those jobs a merge veto they were explicitly built not to have. The authoritative evidence
  // is the enumerated job set below.
  if (run.status !== RUN_STATUS_COMPLETED) {
    reasons.push(`run-not-completed: run status is '${run.status ?? 'unknown'}'`)
  }

  if (!Array.isArray(jobs) || jobs.length === 0) {
    reasons.push('jobs-missing: no job list was supplied for this run')
    return { decision: 'REFUSE', reasons }
  }
  for (const job of jobs) {
    if (typeof job?.runId === 'undefined') {
      // Missing membership evidence refuses: an unmapped job record is unverified, not exempt.
      reasons.push(`job-run-id-missing: job '${job?.name ?? 'unknown'}' does not carry its run id, so its membership in run ${expected.runId} is unverified`)
    } else if (String(job.runId) !== String(expected.runId)) {
      reasons.push(`job-from-foreign-run: job '${job?.name ?? 'unknown'}' belongs to run ${job.runId}, not the expected run ${expected.runId}`)
    } else if (typeof job?.runAttempt !== 'number') {
      reasons.push(`job-attempt-missing: job '${job?.name ?? 'unknown'}' does not carry its run attempt, so it may belong to a different attempt of run ${expected.runId}`)
    } else if (job.runAttempt > expected.runAttempt) {
      // A partial rerun ("Re-run failed jobs") retains successful jobs at their earlier attempt — a
      // continuation of the same run, per the established partial-rerun model in ci-metrics — so
      // earlier attempts are accepted as retained evidence. Only records from a LATER attempt than
      // the one being bound are refused: they cannot belong to the candidate's validation.
      reasons.push(`job-attempt-mismatch: job '${job?.name ?? 'unknown'}' belongs to attempt ${job.runAttempt}, later than the expected attempt ${expected.runAttempt}`)
    }
  }

  const uniqueJobs = [...REQUIRED_JOBS, CLASSIFIER_JOB_NAME, AGGREGATE_JOB_NAME]
  for (const duplicate of duplicateNames(jobs, uniqueJobs)) {
    reasons.push(`ambiguous-job: '${duplicate}' appears more than once; the expected gate set must be unambiguous`)
  }

  const classifier = jobs.find((job) => job?.name === CLASSIFIER_JOB_NAME)
  if (!classifier) {
    reasons.push(`missing-job: ${CLASSIFIER_JOB_NAME} did not run`)
  } else if (classifier.conclusion !== JOB_CONCLUSION_SUCCESS) {
    reasons.push(`job-not-success: ${CLASSIFIER_JOB_NAME} concluded '${classifier.conclusion ?? 'unknown'}'`)
  }

  const aggregates = jobs.filter((job) => job?.name === AGGREGATE_JOB_NAME)
  if (aggregates.length === 0) {
    reasons.push(`missing-job: ${AGGREGATE_JOB_NAME} did not run`)
  } else if (aggregates.length > 1) {
    reasons.push(`ambiguous-aggregate: ${aggregates.length} ${AGGREGATE_JOB_NAME} jobs found; the binding cannot choose`)
  } else if (aggregates[0].conclusion !== JOB_CONCLUSION_SUCCESS) {
    reasons.push(`job-not-success: ${AGGREGATE_JOB_NAME} concluded '${aggregates[0].conclusion ?? 'unknown'}'`)
  }

  for (const name of REQUIRED_JOBS) {
    const job = jobs.find((candidate) => candidate?.name === name)
    if (!job) {
      reasons.push(`missing-job: ${name} did not run`)
    } else if (job.conclusion !== JOB_CONCLUSION_SUCCESS) {
      reasons.push(`job-not-success: ${name} concluded '${job.conclusion ?? 'unknown'}'`)
    }
  }

  collectShardReasons(jobs, reasons)

  if (!Array.isArray(changedPaths)) {
    reasons.push('surface-comparison-missing: no candidate-vs-default-branch path list was supplied; an unverified surface is not a trusted one')
  } else {
    const isPath = (value) => typeof value === 'string' && value.length > 0
    const isRename = (value) => value !== null && typeof value === 'object' && isPath(value.from) && isPath(value.to)
    if (changedPaths.some((entry) => !isPath(entry) && !isRename(entry))) {
      // A parsing or mapping failure upstream could silently erase an altered trusted path from the
      // list; malformed input refuses instead of being skipped over.
      reasons.push('surface-comparison-malformed: the changed-path list contains entries that are neither paths nor {from, to} rename pairs')
    } else {
      // A rename must present both sides: the FROM side is the one that can remove trusted merge
      // machinery under a destination no prefix rule would ever inspect (the same lesson the CI
      // classifier learned — include both sides of renames).
      const inspected = changedPaths.flatMap((entry) => (typeof entry === 'string' ? [entry] : [entry.from, entry.to]))
      for (const path of inspected) {
        if (TRUSTED_SURFACE_PREFIXES.some((prefix) => path.startsWith(prefix))) {
          reasons.push(`trusted-surface-modified: '${path}' differs from the default branch, so the candidate did not run main's merge machinery`)
        }
      }
    }
  }

  return { decision: reasons.length === 0 ? 'PASS' : 'REFUSE', reasons }
}
