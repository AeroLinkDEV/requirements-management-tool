// Rolling CI metrics aggregation for phase B.
//
// Consumes validated `aerolink-ci-run/v2` records (untrusted artifact data, strict bounds) plus GitHub
// Actions run/job metadata (trusted API data) and produces like-for-like rolling statistics: queue delay,
// cancellation consumption, critical-path and job-group durations, flake titles, cache hits, and
// sustained-regression candidates. Nothing here influences the product gate.

import { looksLikeCredential } from './fragment.mjs'

export const ROLLING_SCHEMA_VERSION = 'aerolink-ci-rolling/v1'
export const MAX_RECORDS = 200
export const MAX_TITLES = 100
export const MAX_REGRESSIONS = 20
export const MAX_REPORT_BYTES = 512 * 1024

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value))
}

export function median(values) {
  const sorted = [...values].filter((value) => Number.isFinite(value)).sort((a, b) => a - b)
  if (sorted.length === 0) return null
  const middle = Math.floor(sorted.length / 2)
  return sorted.length % 2 === 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2
}

export function percentile(values, p) {
  const sorted = [...values].filter((value) => Number.isFinite(value)).sort((a, b) => a - b)
  if (sorted.length === 0) return null
  const rank = clamp(Math.ceil((p / 100) * sorted.length), 1, sorted.length)
  return sorted[rank - 1]
}

export function classifyRun(record) {
  const event = record.run?.event ?? 'unknown'
  if (event === 'push') return 'push-main'
  if (event === 'schedule') return 'scheduled'
  if (event === 'workflow_dispatch') return 'manual'

  const classification = record.classifications ?? {}
  if (classification.docsOnly > 0) return 'docs-only'

  const selected = []
  if (classification.backend > 0) selected.push('backend')
  if (classification.client > 0) selected.push('client')
  if (classification.browser > 0) selected.push('browser')
  if (classification.postgresql > 0) selected.push('postgresql')
  if (selected.length === 0) return 'unclassified'
  if (selected.length === 1) return `${selected[0]}-only`
  return 'mixed'
}

export function runDurationMs(record) {
  const path = record.criticalPath
  if (!path || path.durationMs === null || path.unavailableReason) return null
  return path.durationMs
}

export function jobGroupDurations(record) {
  const byGroup = new Map()
  for (const job of Array.isArray(record.jobs) ? record.jobs : []) {
    if (!job || typeof job !== 'object' || !job.timings || typeof job.timings !== 'object') continue
    const start = job.timings.jobStartMs
    const end = job.timings.jobEndMs
    if (start === null || end === null || end < start) continue
    const list = byGroup.get(job.group) ?? []
    list.push(end - start)
    byGroup.set(job.group, list)
  }
  return byGroup
}

function isoMs(value) {
  if (typeof value !== 'string') return null
  const time = Date.parse(value)
  return Number.isFinite(time) ? time : null
}

export function queueAndCancellation(apiRun, apiJobs = []) {
  const created = isoMs(apiRun?.created_at)
  const started = isoMs(apiRun?.run_started_at ?? apiRun?.created_at)
  const completed = isoMs(apiRun?.updated_at ?? apiRun?.completed_at)
  const queueDelayMs = created !== null && started !== null && started >= created ? started - created : null
  const jobs = Array.isArray(apiJobs) ? apiJobs : []
  const cancelledConsumedMs = jobs
    .filter((job) => job.conclusion === 'cancelled')
    .map((job) => {
      const jobStart = isoMs(job.started_at)
      const jobEnd = isoMs(job.completed_at)
      return jobStart !== null && jobEnd !== null && jobEnd >= jobStart ? jobEnd - jobStart : 0
    })
    .reduce((sum, value) => sum + value, 0)
  const cancelledJobs = jobs.filter((job) => job.conclusion === 'cancelled').length
  return {
    queueDelayMs,
    cancelledConsumedMs,
    cancelledJobs,
    runConsumedMs: created !== null && completed !== null && completed >= created ? completed - created : null,
    conclusion: apiRun?.conclusion ?? 'unknown',
  }
}

export function flakeTrend(records) {
  const titleCounts = new Map()
  const byRun = []
  for (const record of records) {
    const titles = Array.isArray(record.flakyTests) ? record.flakyTests : []
    if (titles.length === 0) continue
    byRun.push({ run: record.run?.id ?? null, attempt: record.run?.attempt ?? null, count: titles.length, titles })
    for (const title of titles) titleCounts.set(title, (titleCounts.get(title) ?? 0) + 1)
  }
  const titles = [...titleCounts.entries()]
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .slice(0, MAX_TITLES)
    .map(([title, runs]) => ({ title, runs }))
  return { byRun, titles, totalFlakyRuns: byRun.length }
}

export function cacheTrend(records) {
  const totals = { nuget: { hit: 0, miss: 0 }, npm: { hit: 0, miss: 0 }, chromium: { hit: 0, miss: 0 } }
  for (const record of records) {
    const cache = record.cache ?? {}
    for (const kind of ['nuget', 'npm', 'chromium']) {
      if (cache[kind]?.hit) totals[kind].hit += cache[kind].hit
      if (cache[kind]?.miss) totals[kind].miss += cache[kind].miss
    }
  }
  return totals
}

export function fullGatesPerMerge(mergedPrs, runs) {
  const result = []
  for (const pr of Array.isArray(mergedPrs) ? mergedPrs : []) {
    if (!pr.merged_at || typeof pr.merge_commit_sha !== 'string' || pr.merge_commit_sha.length !== 40) continue
    if (!pr.head || typeof pr.head.ref !== 'string' || typeof pr.created_at !== 'string') continue
    const created = Date.parse(pr.created_at)
    const merged = Date.parse(pr.merged_at)
    if (!Number.isFinite(created) || !Number.isFinite(merged)) continue
    // Pre-merge gates are every pull_request quality-gate run on the PR's branch created between shortly
    // before the PR and one day after the merge (reruns keep their original created_at, so they remain in
    // the window). Post-merge gates are push runs on the exact merge commit.
    const cutoff = merged + 24 * 60 * 60 * 1000
    const prRuns = []
    const postMergeRuns = []
    for (const run of Array.isArray(runs) ? runs : []) {
      if (typeof run.created_at !== 'string') continue
      const at = Date.parse(run.created_at)
      if (!Number.isFinite(at)) continue
      if (run.event === 'pull_request' && run.head_branch === pr.head.ref && at >= created - 60 * 60 * 1000 && at <= cutoff) {
        prRuns.push(run)
      } else if (run.event === 'push' && run.head_sha === pr.merge_commit_sha) {
        postMergeRuns.push(run)
      }
    }
    const attemptCount = (runs) => runs.reduce((sum, run) => sum + (Number.isInteger(run.run_attempt) && run.run_attempt > 0 ? run.run_attempt : 1), 0)
    result.push({
      pr: pr.number,
      mergedAt: pr.merged_at,
      runs: prRuns.length + postMergeRuns.length,
      attempts: attemptCount(prRuns) + attemptCount(postMergeRuns),
      prRuns: prRuns.length,
      postMergeRuns: postMergeRuns.length,
    })
  }
  return result.sort((a, b) => String(b.mergedAt).localeCompare(String(a.mergedAt))).slice(0, MAX_RECORDS)
}

export function rollingStats(records) {
  const groups = new Map()
  for (const record of records) {
    const category = classifyRun(record)
    const entry = groups.get(category) ?? { category, criticalPath: [], jobGroups: new Map(), counts: { runs: 0, expected: 0, executed: 0, failed: 0, skipped: 0, flaky: 0 } }
    entry.counts.runs += 1
    const duration = runDurationMs(record)
    if (duration !== null) entry.criticalPath.push(duration)
    for (const [group, values] of jobGroupDurations(record)) {
      const list = entry.jobGroups.get(group) ?? []
      list.push(...values)
      entry.jobGroups.set(group, list)
    }
    const counts = record.counts ?? {}
    for (const key of ['expected', 'executed', 'failed', 'skipped', 'flaky']) {
      if (Number.isInteger(counts[key])) entry.counts[key] += counts[key]
    }
    groups.set(category, entry)
  }
  const result = []
  for (const entry of groups.values()) {
    const jobGroups = {}
    for (const [group, values] of entry.jobGroups) {
      jobGroups[group] = { median: median(values), p95: percentile(values, 95), samples: values.length }
    }
    result.push({
      category: entry.category,
      runs: entry.counts.runs,
      criticalPath: {
        median: median(entry.criticalPath),
        p95: percentile(entry.criticalPath, 95),
        samples: entry.criticalPath.length,
      },
      jobGroups,
      counts: entry.counts,
    })
  }
  return result.sort((a, b) => a.category.localeCompare(b.category))
}

function windowMedian(records, count) {
  return median(records.slice(-count).map(runDurationMs))
}

export function detectRegressions(records, { window = 10, minRuns = 3, ratio = 1.15, minDeltaMs = 60_000 } = {}) {
  if (records.length < minRuns * 2) return []
  const recent = records.slice(-window)
  const previous = records.slice(-window * 2, -window)
  if (recent.length < minRuns || previous.length < minRuns) return []
  const recentMedian = median(recent.map(runDurationMs))
  const previousMedian = median(previous.map(runDurationMs))
  const recentP95 = percentile(recent.map(runDurationMs), 95)
  const previousP95 = percentile(previous.map(runDurationMs), 95)
  if (recentMedian === null || previousMedian === null || recentP95 === null || previousP95 === null) return []
  const regressions = []
  if (recentMedian > previousMedian * ratio && recentMedian - previousMedian >= minDeltaMs) {
    regressions.push({ metric: 'criticalPathMedian', current: recentMedian, previous: previousMedian, threshold: previousMedian * ratio, runs: recent.length })
  }
  if (recentP95 > previousP95 * ratio && recentP95 - previousP95 >= minDeltaMs) {
    regressions.push({ metric: 'criticalPathP95', current: recentP95, previous: previousP95, threshold: previousP95 * ratio, runs: recent.length })
  }
  return regressions.slice(0, MAX_REGRESSIONS)
}

export function validateRunRecord(record) {
  const errors = []
  if (record === null || typeof record !== 'object' || Array.isArray(record)) return ['Run record is not an object.']
  const legacy = record.schemaVersion === 'aerolink-ci-run/v1'
  if (record.schemaVersion !== 'aerolink-ci-run/v2' && !legacy) errors.push(`Unsupported run record schema "${record.schemaVersion ?? 'missing'}".`)
  const run = record.run
  if (!run || typeof run !== 'object') {
    errors.push('Run record has no run identity.')
  } else {
    if (!Number.isInteger(run.id) || run.id < 1) errors.push('run.id is invalid.')
    if (!Number.isInteger(run.attempt) || run.attempt < 1) errors.push('run.attempt is invalid.')
    if (typeof run.sha !== 'string' || !/^[0-9a-f]{40}$/.test(run.sha)) errors.push('run.sha is invalid.')
    if (typeof run.tree !== 'string' || !/^[0-9a-f]{40}$/.test(run.tree)) errors.push('run.tree is invalid.')
    if (typeof run.repository !== 'string' || run.repository.length > 200) errors.push('run.repository is invalid.')
    if (typeof run.event !== 'string' || run.event.length > 50) errors.push('run.event is invalid.')
  }
  if (!Array.isArray(record.jobs) || record.jobs.length > 200) errors.push('record.jobs must be an array bounded at 200.')
  if (!legacy) {
    for (const job of Array.isArray(record.jobs) ? record.jobs : []) {
      if (typeof job?.instance !== 'string' || job.instance.length > 120) errors.push('A job has an invalid instance.')
      if (!Number.isInteger(job?.sourceAttempt) || job.sourceAttempt < 1) errors.push('A job has an invalid sourceAttempt.')
    }
  } else {
    for (const job of Array.isArray(record.jobs) ? record.jobs : []) {
      if (!job || typeof job !== 'object') {
        errors.push('A legacy job is not an object.')
        continue
      }
      if (typeof job.group !== 'string' || job.group.length === 0 || job.group.length > 100) errors.push('A legacy job has an invalid group.')
      if (typeof job.instance !== 'string' || job.instance.length > 120) errors.push('A legacy job has an invalid instance.')
      if (!job.timings || typeof job.timings !== 'object') {
        errors.push(`Legacy job "${job.instance ?? '?'}" has no timings object.`)
        continue
      }
      const start = job.timings.jobStartMs
      const end = job.timings.jobEndMs
      if (!Number.isInteger(start) || start < 0 || !Number.isInteger(end) || end < 0) {
        errors.push(`Legacy job "${job.instance ?? '?'}" has non-integer or negative timing endpoints.`)
      } else if (end < start) {
        errors.push(`Legacy job "${job.instance ?? '?'}" has reversed timing endpoints.`)
      }
    }
  }
  const json = JSON.stringify(record)
  if (Buffer.byteLength(json, 'utf8') > 512 * 1024) errors.push('Run record exceeds the bounded size.')
  for (const [key, value] of Object.entries(flatten(record))) {
    if (looksLikeCredential(value)) errors.push(`Field "${key}" matches a credential-value pattern.`)
  }
  return errors
}

export function recordFormat(record) {
  if (record?.schemaVersion === 'aerolink-ci-run/v2') return 'v2'
  if (record?.schemaVersion === 'aerolink-ci-run/v1') return 'v1-legacy'
  return 'unknown'
}

function flatten(value, prefix = '', out = {}) {
  if (value === null || typeof value !== 'object') {
    if (value !== undefined) out[prefix || '(root)'] = value
    return out
  }
  for (const [key, child] of Object.entries(value)) flatten(child, prefix ? `${prefix}.${key}` : key, out)
  return out
}

function escapeMarkdown(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\|/g, '\\|')
    .replace(/\r?\n/g, ' ')
}

export function buildRollingReport({ records, regressions = [], missing = [], fullGates = [], generatedAt = new Date().toISOString() }) {
  const stats = rollingStats(records)
  const flakes = flakeTrend(records)
  const cache = cacheTrend(records)
  const queueDelays = records.map((record) => record.apiTiming?.queueDelayMs).filter((value) => Number.isFinite(value))
  const cancelledConsumedTotal = records.reduce((sum, record) => sum + (record.apiTiming?.cancelledConsumedMs ?? 0), 0)
  const cancelledJobsTotal = records.reduce((sum, record) => sum + (record.apiTiming?.cancelledJobs ?? 0), 0)
  const cancelledRuns = records.filter((record) => record.conclusion === 'cancelled').length
  const runConsumedTotal = records.reduce((sum, record) => sum + (record.apiTiming?.runConsumedMs ?? 0), 0)
  const lines = []
  lines.push('# CI rolling metrics')
  lines.push('')
  lines.push(`- Generated: ${escapeMarkdown(generatedAt)}`)
  lines.push(`- Runs included: ${records.length}`)
  lines.push(`- Runs missing/unreadable: ${missing.length}`)
  lines.push(`- Flaky runs: ${flakes.totalFlakyRuns}`)
  lines.push(`- Queue delay median: ${median(queueDelays) === null ? 'unavailable' : `${Math.round(median(queueDelays) / 1000)}s`} (${queueDelays.length} measured)`)
  lines.push(`- Cancelled: ${cancelledRuns} run(s), ${cancelledJobsTotal} job(s), ${Math.round(cancelledConsumedTotal / 1000)}s consumed`)
  lines.push(`- Run wall time consumed (window): ${Math.round(runConsumedTotal / 1000)}s`)
  if (fullGates.length > 0) {
    const totalRuns = fullGates.reduce((sum, entry) => sum + (Number.isInteger(entry.runs) ? entry.runs : 0), 0)
    const totalAttempts = fullGates.reduce((sum, entry) => sum + (Number.isInteger(entry.attempts) ? entry.attempts : 0), 0)
    lines.push(`- Full gates per merged PR (window): ${totalRuns} full gate runs / ${totalAttempts} attempts across ${fullGates.length} merges`)
  }
  lines.push('')
  lines.push('## Comparable groups')
  lines.push('')
  lines.push('| Category | Runs | Critical path median | Critical path p95 | Expected | Executed | Failed | Skipped | Flaky |')
  lines.push('|---|---|---|---:|---:|---:|---:|---:|---:|')
  for (const group of stats) {
    lines.push(`| ${escapeMarkdown(group.category)} | ${group.runs} | ${group.criticalPath.median === null ? '—' : `${Math.round(group.criticalPath.median / 1000)}s`} | ${group.criticalPath.p95 === null ? '—' : `${Math.round(group.criticalPath.p95 / 1000)}s`} | ${group.counts.expected} | ${group.counts.executed} | ${group.counts.failed} | ${group.counts.skipped} | ${group.counts.flaky} |`)
  }
  lines.push('')
  if (regressions.length > 0) {
    lines.push('## Sustained regressions')
    lines.push('')
    for (const entry of regressions) {
      const label = entry.category ? `${entry.category}: ${entry.metric}` : entry.metric
      lines.push(`- ${escapeMarkdown(label)}: current ${Math.round(entry.current / 1000)}s vs previous ${Math.round(entry.previous / 1000)}s (threshold ${Math.round(entry.threshold / 1000)}s, ${entry.runs} runs)`)
    }
    lines.push('')
  }
  if (flakes.titles.length > 0) {
    lines.push('## Most frequent flaky titles')
    lines.push('')
    for (const entry of flakes.titles) lines.push(`- ${escapeMarkdown(entry.title)} (${entry.runs} run${entry.runs === 1 ? '' : 's'})`)
    lines.push('')
  }
  if (missing.length > 0) {
    lines.push('## Runs missing or unreadable')
    lines.push('')
    for (const entry of missing.slice(0, 20)) {
      lines.push(`- Run ${escapeMarkdown(String(entry.runId))}: ${escapeMarkdown(entry.reason)}`)
    }
    if (missing.length > 20) lines.push(`- ...and ${missing.length - 20} more.`)
    lines.push('')
  }
  if (fullGates.length > 0) {
    lines.push('## Full gates per merged PR')
    lines.push('')
    for (const entry of fullGates.slice(0, 20)) {
      lines.push(`- PR #${escapeMarkdown(String(entry.pr))} (merged ${escapeMarkdown(String(entry.mergedAt).slice(0, 10))}): ${entry.runs} full gate run(s) / ${entry.attempts} attempt(s) (${entry.prRuns} pre-merge, ${entry.postMergeRuns} post-merge)`)
    }
    lines.push('')
  }
  lines.push('## Cache totals')
  lines.push('')
  lines.push(`- NuGet: ${cache.nuget.hit} hit / ${cache.nuget.miss} miss`)
  lines.push(`- npm: ${cache.npm.hit} hit / ${cache.npm.miss} miss`)
  lines.push(`- Chromium: ${cache.chromium.hit} hit / ${cache.chromium.miss} miss`)
  lines.push('')
  lines.push('## Queue and cancellation')
  lines.push('')
  lines.push(`- Queue delay median: ${median(queueDelays) === null ? 'unavailable' : `${Math.round(median(queueDelays) / 1000)}s`} across ${queueDelays.length} measured run(s)`)
  lines.push(`- Cancelled runs: ${cancelledRuns}; cancelled jobs: ${cancelledJobsTotal}; cancelled consumed: ${Math.round(cancelledConsumedTotal / 1000)}s`)
  lines.push(`- Run wall time consumed: ${Math.round(runConsumedTotal / 1000)}s`)
  lines.push('')
  const markdown = lines.join('\n')
  if (Buffer.byteLength(markdown, 'utf8') > MAX_REPORT_BYTES) throw new Error('Rolling Markdown exceeds the bounded size.')
  return {
    schemaVersion: ROLLING_SCHEMA_VERSION,
    generatedAt,
    records: records.slice(-MAX_RECORDS).map((record) => ({
      id: record.run?.id ?? null,
      attempt: record.run?.attempt ?? null,
      event: record.run?.event ?? null,
      sha: record.run?.sha ?? null,
      format: record.format ?? null,
      legacyIdentityNote: record.legacyIdentityNote ? String(record.legacyIdentityNote).slice(0, 300) : null,
      category: classifyRun(record),
      criticalPathMs: runDurationMs(record),
      conclusion: record.conclusion ?? null,
      apiTiming: record.apiTiming ?? null,
    })),
    stats,
    regressions: regressions.slice(0, MAX_REGRESSIONS),
    missing: missing.slice(0, 50),
    fullGatesPerMerge: fullGates.slice(0, MAX_RECORDS),
    queueAndCancellation: {
      queueDelayMedianMs: median(queueDelays),
      queueDelaySamples: queueDelays.length,
      cancelledRuns,
      cancelledJobs: cancelledJobsTotal,
      cancelledConsumedMs: cancelledConsumedTotal,
      runConsumedMs: runConsumedTotal,
    },
    flakeTrend: flakes,
    cache,
    markdown,
  }
}

export function trackerBody(report) {
  const regressions = Array.isArray(report?.regressions) ? report.regressions : []
  const lines = []
  lines.push('# CI rolling regression tracker')
  lines.push('')
  lines.push('This single issue is the durable tracking item for sustained CI regressions. It is updated')
  lines.push('only when the trusted rolling collector detects a threshold crossing; ordinary runner noise')
  lines.push('never opens or updates issues.')
  lines.push('')
  if (regressions.length === 0) {
    lines.push('No sustained regressions in the current window.')
    lines.push('')
    lines.push(`Last checked: ${report?.generatedAt ?? 'unknown'}`)
  } else {
    lines.push(`Detected ${regressions.length} sustained regression(s):`)
    lines.push('')
    for (const entry of regressions) {
      const label = entry.category ? `${entry.category}: ${entry.metric}` : entry.metric
      lines.push(`- ${escapeMarkdown(label)}: current ${Math.round(entry.current / 1000)}s vs previous ${Math.round(entry.previous / 1000)}s (threshold ${Math.round(entry.threshold / 1000)}s, ${entry.runs} runs)`)
    }
    lines.push('')
    lines.push(`Last updated: ${report?.generatedAt ?? 'unknown'}`)
  }
  return lines.join('\n')
}
