export const CADENCE_SCHEMA_VERSION = 'aerolink-ci-cadence/v1'
export const CADENCE_WINDOW_DAYS = 30
export const CADENCE_SWITCH_MERGED_AT = '2026-08-17T12:09:45Z'
export const MAX_CADENCE_MERGES = 200

const HOUR_MS = 60 * 60 * 1000
const DAY_MS = 24 * HOUR_MS

function time(value) {
  if (typeof value !== 'string') return null
  const parsed = Date.parse(value)
  return Number.isFinite(parsed) ? parsed : null
}

function validSha(value) {
  return typeof value === 'string' && /^[0-9a-f]{40}$/i.test(value)
}

function attemptCount(runs) {
  return runs.reduce((sum, run) => sum + (Number.isInteger(run?.run_attempt) && run.run_attempt > 0 ? run.run_attempt : 1), 0)
}

function inPrWindow(run, prCreatedMs, mergedMs) {
  const at = time(run?.created_at)
  return at !== null && at >= prCreatedMs - HOUR_MS && at <= mergedMs + DAY_MS
}

function branchRun(run, pr, prCreatedMs, mergedMs) {
  return run?.head_branch === pr.head.ref && inPrWindow(run, prCreatedMs, mergedMs)
}

export function cadenceEntry(pr, productRuns = [], fastRuns = [], cancelledConsumedByRun = new Map()) {
  if (!pr || !pr.merged_at || !pr.created_at || !pr.head || typeof pr.head.ref !== 'string' || !validSha(pr.head.sha)) return null
  if (!validSha(pr.merge_commit_sha)) return null
  const createdMs = time(pr.created_at)
  const mergedMs = time(pr.merged_at)
  if (createdMs === null || mergedMs === null || mergedMs < createdMs) return null

  const pullRequestFullRuns = []
  const readinessFullRuns = []
  const postMergeFullRuns = []
  const headObservations = []

  for (const run of Array.isArray(productRuns) ? productRuns : []) {
    if (!run || typeof run !== 'object') continue
    if (run.event === 'pull_request' && branchRun(run, pr, createdMs, mergedMs)) {
      pullRequestFullRuns.push(run)
      if (validSha(run.head_sha)) headObservations.push(run)
    } else if (run.event === 'workflow_dispatch' && branchRun(run, pr, createdMs, mergedMs)) {
      readinessFullRuns.push(run)
    } else if (run.event === 'push' && run.head_sha === pr.merge_commit_sha) {
      postMergeFullRuns.push(run)
    }
  }

  for (const run of Array.isArray(fastRuns) ? fastRuns : []) {
    if (run?.event === 'pull_request' && branchRun(run, pr, createdMs, mergedMs) && validSha(run.head_sha)) {
      headObservations.push(run)
    }
  }

  const observedBySha = new Map()
  for (const run of headObservations) {
    const at = time(run.created_at)
    if (at === null) continue
    const existing = observedBySha.get(run.head_sha)
    if (existing === undefined || at < existing) observedBySha.set(run.head_sha, at)
  }
  const finalHeadObservedMs = observedBySha.get(pr.head.sha) ?? null
  const finalPushToMergeMs = finalHeadObservedMs !== null && finalHeadObservedMs <= mergedMs ? mergedMs - finalHeadObservedMs : null

  const preMergeFullRuns = [...pullRequestFullRuns, ...readinessFullRuns]
  const allFullRuns = [...preMergeFullRuns, ...postMergeFullRuns]
  const cancelledRunnerMs = allFullRuns.reduce((sum, run) => {
    const value = cancelledConsumedByRun instanceof Map ? cancelledConsumedByRun.get(run.id) : null
    return sum + (Number.isFinite(value) && value >= 0 ? value : 0)
  }, 0)

  return {
    pr: pr.number,
    branch: pr.head.ref,
    finalHeadSha: pr.head.sha,
    mergeCommitSha: pr.merge_commit_sha,
    mergedAt: pr.merged_at,
    cadence: mergedMs < time(CADENCE_SWITCH_MERGED_AT) ? 'before' : 'after',
    pushes: observedBySha.size,
    pushMeasurement: 'distinct PR head SHAs observed by Fast or pull-request Product workflow triggers',
    finalPushObservedAt: finalHeadObservedMs === null ? null : new Date(finalHeadObservedMs).toISOString(),
    finalPushToMergeMs,
    finalPushMeasurement: 'first Fast or pull-request Product workflow trigger observed for the merged head SHA',
    preMergeFullRuns: preMergeFullRuns.length,
    preMergeFullAttempts: attemptCount(preMergeFullRuns),
    pullRequestFullRuns: pullRequestFullRuns.length,
    readinessFullRuns: readinessFullRuns.length,
    postMergeFullRuns: postMergeFullRuns.length,
    totalFullRuns: allFullRuns.length,
    totalFullAttempts: attemptCount(allFullRuns),
    cancelledRunnerMs,
    fullRunIds: allFullRuns.map((run) => run.id).filter((id) => Number.isInteger(id)),
  }
}

export function cadenceEntries(mergedPrs, productRuns = [], fastRuns = [], cancelledConsumedByRun = new Map()) {
  return (Array.isArray(mergedPrs) ? mergedPrs : [])
    .map((pr) => cadenceEntry(pr, productRuns, fastRuns, cancelledConsumedByRun))
    .filter(Boolean)
    .sort((a, b) => String(b.mergedAt).localeCompare(String(a.mergedAt)))
    .slice(0, MAX_CADENCE_MERGES)
}

function median(values) {
  const sorted = values.filter(Number.isFinite).sort((a, b) => a - b)
  if (sorted.length === 0) return null
  const middle = Math.floor(sorted.length / 2)
  return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2
}

function percentile(values, p) {
  const sorted = values.filter(Number.isFinite).sort((a, b) => a - b)
  if (sorted.length === 0) return null
  const rank = Math.max(1, Math.min(sorted.length, Math.ceil((p / 100) * sorted.length)))
  return sorted[rank - 1]
}

function summarizeGroup(entries) {
  const preMergeRuns = entries.map((entry) => entry.preMergeFullRuns)
  const finalLatency = entries.map((entry) => entry.finalPushToMergeMs).filter(Number.isFinite)
  return {
    merges: entries.length,
    pushes: entries.reduce((sum, entry) => sum + entry.pushes, 0),
    preMergeFullRuns: entries.reduce((sum, entry) => sum + entry.preMergeFullRuns, 0),
    preMergeFullAttempts: entries.reduce((sum, entry) => sum + entry.preMergeFullAttempts, 0),
    cancelledRunnerMs: entries.reduce((sum, entry) => sum + entry.cancelledRunnerMs, 0),
    fullRunsPerMerge: {
      median: median(preMergeRuns),
      p95: percentile(preMergeRuns, 95),
      max: preMergeRuns.length ? Math.max(...preMergeRuns) : null,
    },
    finalPushToMergeMs: {
      median: median(finalLatency),
      p95: percentile(finalLatency, 95),
      samples: finalLatency.length,
    },
  }
}

export function buildCadenceReport(entries, { generatedAt = new Date().toISOString(), windowDays = CADENCE_WINDOW_DAYS } = {}) {
  const rows = Array.isArray(entries) ? entries.slice(0, MAX_CADENCE_MERGES) : []
  const before = rows.filter((entry) => entry.cadence === 'before')
  const after = rows.filter((entry) => entry.cadence === 'after')
  const summary = { before: summarizeGroup(before), after: summarizeGroup(after) }
  const fmtMs = (value) => Number.isFinite(value) ? `${Math.round(value / 1000)}s` : 'unavailable'
  const lines = [
    '# CI cadence metrics',
    '',
    `- Generated: ${generatedAt}`,
    `- Merge window: last ${windowDays} days, newest ${MAX_CADENCE_MERGES} kept`,
    `- Cadence switch: ${CADENCE_SWITCH_MERGED_AT} (#655)`,
    '- Pushes are counted as distinct PR head SHAs observed by Fast or pull-request Product workflow triggers; this is an operational push/update measurement, not a commit-author timestamp.',
    '- Final-push-to-merge starts at the first such workflow trigger observed for the SHA that ultimately merged.',
    '',
    '| Period | Merges | Pushes | Pre-merge Full runs | Full attempts | Cancelled runner time | Full runs/merge median | p95 | Final-push-to-merge median | p95 |',
    '|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|',
  ]
  for (const key of ['before', 'after']) {
    const row = summary[key]
    lines.push(`| ${key} | ${row.merges} | ${row.pushes} | ${row.preMergeFullRuns} | ${row.preMergeFullAttempts} | ${fmtMs(row.cancelledRunnerMs)} | ${row.fullRunsPerMerge.median ?? '—'} | ${row.fullRunsPerMerge.p95 ?? '—'} | ${fmtMs(row.finalPushToMergeMs.median)} | ${fmtMs(row.finalPushToMergeMs.p95)} |`)
  }
  lines.push('', '## Per merged PR', '')
  for (const entry of rows.slice(0, 50)) {
    lines.push(`- PR #${entry.pr} (${entry.cadence}, merged ${entry.mergedAt.slice(0, 10)}): ${entry.pushes} push/head update(s); ${entry.preMergeFullRuns} pre-merge Full run(s) / ${entry.preMergeFullAttempts} attempt(s); ${Math.round(entry.cancelledRunnerMs / 1000)}s cancelled runner time; final-push-to-merge ${fmtMs(entry.finalPushToMergeMs)}; ${entry.postMergeFullRuns} post-merge Full run(s).`)
  }
  return {
    schemaVersion: CADENCE_SCHEMA_VERSION,
    generatedAt,
    switchMergedAt: CADENCE_SWITCH_MERGED_AT,
    windowDays,
    measurement: {
      pushes: 'distinct PR head SHAs observed by Fast or pull-request Product workflow triggers',
      finalPushToMerge: 'first Fast or pull-request Product workflow trigger for the merged head SHA to merged_at',
      cancelledRunner: 'sum of elapsed time of cancelled jobs in Product quality-gate runs attributed to the PR',
      fullRunsPerMerge: 'pre-merge Product quality-gate runs (pull_request before the switch; readiness workflow_dispatch after it)',
    },
    summary,
    entries: rows,
    markdown: lines.join('\n'),
  }
}
