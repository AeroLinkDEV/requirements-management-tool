// How often advisory Fast says green on a SHA whose readiness Full run then fails (#1152 B4).
//
// The rate is the gate for #1152's E-a option: Full on the pull-request head may give way to a lighter
// pre-queue check only when, over at least two weeks, at most 20% of the Full failures it would no longer
// see leak past Fast. On 2026-09-11..25 the leak was 27 of the 29 failed readiness runs that had a Fast
// result. This module only counts; it never changes a workflow, a check or a merge decision.

const SHA = /^[0-9a-f]{40}$/

export function normalizeLane(jobName) {
  return String(jobName ?? '').replace(/\s*\(\d+\/\d+\)$/, '').trim()
}

/**
 * Fast's verdict for one head SHA from every completed Fast run on it. Any failure is red, because a red
 * attempt was feedback the author had; otherwise green if any run succeeded; otherwise none.
 */
export function fastVerdict(runsForSha = []) {
  const done = runsForSha.filter((run) => run && run.status === 'completed' && ['success', 'failure'].includes(run.conclusion))
  if (done.some((run) => run.conclusion === 'failure')) return 'red'
  if (done.some((run) => run.conclusion === 'success')) return 'green'
  return 'none'
}

/**
 * @param {object} input
 * @param {Array<object>} input.readinessRuns Product runs dispatched for pull-request readiness
 * @param {Array<object>} input.fastRuns Fast PR feedback runs
 * @param {Record<string, string[]>} input.failedJobsByRun failed job names per readiness run id
 */
export function summarizeFastLeak({ readinessRuns = [], fastRuns = [], failedJobsByRun = {} }) {
  const fastBySha = new Map()
  for (const run of fastRuns) {
    if (!run || !SHA.test(String(run.head_sha))) continue
    if (!fastBySha.has(run.head_sha)) fastBySha.set(run.head_sha, [])
    fastBySha.get(run.head_sha).push(run)
  }

  const summary = { failedReadiness: 0, caught: 0, leaked: 0, noFast: 0, leakRate: null, lanes: {}, leakedRuns: [] }
  for (const run of readinessRuns) {
    if (!run || run.event !== 'workflow_dispatch' || run.status !== 'completed' || run.conclusion !== 'failure') continue
    if (!SHA.test(String(run.head_sha))) continue
    summary.failedReadiness += 1
    const verdict = fastVerdict(fastBySha.get(run.head_sha) ?? [])
    const lanes = [...new Set((failedJobsByRun[run.id] ?? []).map(normalizeLane).filter((lane) => lane && lane !== 'Full Product evidence aggregate'))].sort()
    if (verdict === 'none') {
      summary.noFast += 1
      continue
    }
    const leaked = verdict === 'green'
    if (leaked) {
      summary.leaked += 1
      summary.leakedRuns.push({ id: run.id, headSha: run.head_sha, lanes })
    } else {
      summary.caught += 1
    }
    for (const lane of lanes.length > 0 ? lanes : ['(no failed job recorded)']) {
      summary.lanes[lane] ??= { failed: 0, leaked: 0 }
      summary.lanes[lane].failed += 1
      if (leaked) summary.lanes[lane].leaked += 1
    }
  }
  const judged = summary.caught + summary.leaked
  summary.leakRate = judged === 0 ? null : summary.leaked / judged
  return summary
}

export function renderFastLeak(summary, { since, until } = {}) {
  const pct = (value) => (value === null ? 'n/a' : `${(value * 100).toFixed(1)}%`)
  const lines = [
    '# Fast leak rate for readiness Full failures',
    '',
    `Window: ${since ?? '?'} to ${until ?? 'now'}. Advisory measurement for #1152 B4; it authorizes nothing.`,
    '',
    `- Failed readiness Full runs: **${summary.failedReadiness}**`,
    `- Fast was red on the same SHA (caught earlier): **${summary.caught}**`,
    `- Fast was green on the same SHA (leaked): **${summary.leaked}**`,
    `- No completed Fast run on that SHA: ${summary.noFast}`,
    `- **Leak rate: ${pct(summary.leakRate)}** (leaked / (caught + leaked)). The E-a gate is at most 20% over at least two weeks.`,
    '',
    '| Failing lane | Failed runs | Leaked past Fast |',
    '|---|---:|---:|',
    ...Object.entries(summary.lanes).sort((a, b) => b[1].failed - a[1].failed).map(([lane, v]) => `| ${lane} | ${v.failed} | ${v.leaked} |`),
  ]
  return `${lines.join('\n')}\n`
}
