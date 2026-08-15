// Rolling CI metrics collector (phase B).
//
// Runs as a trusted default-branch workflow (workflow_run/schedule/dispatch). It queries the GitHub API
// for recent quality-gate runs, downloads each run's latest `ci-metrics-run-<id>-<attempt>` artifact,
// validates it as untrusted data, cross-checks its identity against GitHub's own run metadata, enriches it
// with Actions queue/cancellation timestamps, and publishes a bounded rolling JSON + Markdown report plus
// sustained-regression candidates. This never executes PR code and never influences the product gate.

import { writeFileSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'
import { readNamedJsonFromZip, ZipParseError } from '../lib/zip.mjs'

/** The merged current-run report inside the `ci-metrics-run-*` artifact directory. */
const RUN_METRICS_FILE = 'run-metrics.json'
import {
  validateRunRecord, queueAndCancellation, rollingStats, flakeTrend, cacheTrend,
  detectRegressions, classifyRun, buildRollingReport, recordFormat, fullGatesPerMerge, MAX_RECORDS,
} from '../lib/rolling.mjs'

const env = (name) => process.env[name] ?? ''

async function api(path, { token, apiUrl = 'https://api.github.com' } = {}) {
  const response = await fetch(`${apiUrl}${path}`, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  })
  if (!response.ok) {
    throw new Error(`GitHub API ${path} returned ${response.status}.`)
  }
  return response.json()
}

async function listAll(path, { token, apiUrl } = {}) {
  const items = []
  let page = 1
  while (true) {
    const body = await api(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`, { token, apiUrl })
    const rows = Array.isArray(body) ? body : body.items ?? body.workflow_runs ?? body.jobs ?? body.artifacts ?? []
    items.push(...rows)
    if (rows.length < 100) break
    page += 1
    if (page > 10) break
  }
  return items
}

async function downloadArtifactZip(artifact, { token, apiUrl } = {}) {
  const response = await fetch(`${apiUrl}/repos/${process.env.GITHUB_REPOSITORY}/actions/artifacts/${artifact.id}/zip`, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  })
  if (!response.ok) throw new Error(`Artifact ${artifact.id} download returned ${response.status}.`)
  return Buffer.from(await response.arrayBuffer())
}

async function fetchCommitTree(sha, { token, apiUrl } = {}) {
  const body = await api(`/repos/${process.env.GITHUB_REPOSITORY}/git/commits/${sha}`, { token, apiUrl })
  return body.tree?.sha ?? null
}

function latestRunArtifact(artifacts, runId) {
  const prefix = `ci-metrics-run-${runId}-`
  let best = null
  for (const artifact of artifacts) {
    if (!artifact.name.startsWith(prefix)) continue
    const attempt = Number(artifact.name.slice(prefix.length))
    if (!Number.isInteger(attempt) || attempt < 1) continue
    if (best === null || attempt > best.attempt) best = { artifact, attempt }
  }
  return best
}

function identityMatches(record, apiRun, commitTree) {
  if (record.run?.id !== apiRun.id) return false
  if (record.run?.event !== apiRun.event) return false
  if (record.run?.repository !== apiRun.repository?.full_name) return false
  if (record.run.event === 'pull_request') {
    // The runs API's pull_requests array is empty for many pull_request runs, so the binding comes from
    // the record's own closed PR identity plus the API's head SHA (the reviewed PR head).
    if (Number.isInteger(record.run.pr) && record.run.pr >= 1 && record.run.ref === `refs/pull/${record.run.pr}/merge` && record.run.headSha === apiRun.head_sha) {
      // Full PR binding available.
    } else if (recordFormat(record) !== 'v1-legacy') {
      return false
    }
    // v1-legacy records predate the PR-identity projection; they are bound by run id, event, repository,
    // and the GitHub-verified tested commit tree below, with an explicit note.
  } else if (record.run.sha !== apiRun.head_sha) {
    return false
  }
  // The record's sha is the commit actually checked out (for PR runs, the merge ref commit). GitHub's own
  // commit object must agree that this commit has exactly the tree the runner recorded.
  if (commitTree !== record.run.tree) return false
  return true
}

async function main() {
  const token = env('GITHUB_TOKEN')
  const apiUrl = env('GITHUB_API_URL') || 'https://api.github.com'
  const repository = env('GITHUB_REPOSITORY')
  const outputDir = env('ROLLING_OUTPUT_DIR')
  const window = Math.max(1, Math.min(MAX_RECORDS, Number(env('ROLLING_RUN_WINDOW') || 40)))
  if (!token || !repository || !outputDir) {
    console.error('[ci-metrics] GITHUB_TOKEN, GITHUB_REPOSITORY, and ROLLING_OUTPUT_DIR are required.')
    process.exit(2)
  }

  const workflowRuns = await listAll(`/repos/${repository}/actions/workflows/ci.yml/runs`, { token, apiUrl })
  const mergedPrs = await listAll(`/repos/${repository}/pulls?state=closed&sort=updated&direction=desc`, { token, apiUrl })
  const completed = workflowRuns
    .filter((run) => run.status === 'completed')
    .sort((a, b) => String(a.created_at).localeCompare(String(b.created_at)))
    .slice(-window)

  const records = []
  const missing = []
  for (const apiRun of completed) {
    const artifacts = await listAll(`/repos/${repository}/actions/runs/${apiRun.id}/artifacts`, { token, apiUrl })
    const latest = latestRunArtifact(artifacts, apiRun.id)
    if (!latest) {
      missing.push({ runId: apiRun.id, reason: 'No ci-metrics-run artifact found for this run.' })
      continue
    }
    let parsed
    try {
      const zip = await downloadArtifactZip(latest.artifact, { token, apiUrl })
      // By name, because `ci-metrics-run-*` uploads an output directory rather than a single file. Asking for
      // "the only JSON" made this collector depend on being the sole writer to that directory, which it
      // stopped being the moment tested-tree provenance began writing `validated-tree.json` beside it.
      parsed = readNamedJsonFromZip(zip, RUN_METRICS_FILE)
    } catch (error) {
      if (error instanceof ZipParseError || error.message.startsWith('Artifact ')) {
        missing.push({ runId: apiRun.id, reason: `Artifact could not be read: ${error.message}` })
        continue
      }
      throw error
    }
    const errors = validateRunRecord(parsed)
    if (errors.length > 0) {
      missing.push({ runId: apiRun.id, reason: `Run record failed validation: ${errors.join('; ')}` })
      continue
    }
    const commitTree = await fetchCommitTree(parsed.run.sha, { token, apiUrl })
    if (!identityMatches(parsed, apiRun, commitTree)) {
      missing.push({ runId: apiRun.id, reason: 'Run record identity does not match GitHub run metadata.' })
      continue
    }
    const jobs = await listAll(`/repos/${repository}/actions/runs/${apiRun.id}/jobs?filter=all`, { token, apiUrl })
    const timing = queueAndCancellation(apiRun, jobs)
    const format = recordFormat(parsed)
    records.push({
      ...parsed,
      format,
      conclusion: timing.conclusion,
      apiTiming: timing,
      ...(format === 'v1-legacy' ? { legacyIdentityNote: 'v1-legacy record: bound by run id, event, repository, and GitHub-verified tested commit tree; PR/base/head identity was not retained by v1 records.' } : {}),
    })
  }

  records.sort((a, b) => String(a.run?.id).localeCompare(String(b.run?.id)))
  const byCategory = new Map()
  for (const record of records) {
    const category = classifyRun(record)
    const list = byCategory.get(category) ?? []
    list.push(record)
    byCategory.set(category, list)
  }
  const regressions = []
  for (const [category, list] of byCategory) {
    regressions.push(...detectRegressions(list, { window: 8, minRuns: 3, ratio: 1.15, minDeltaMs: 60_000 }).map((entry) => ({ ...entry, category })))
  }

  const fullGates = fullGatesPerMerge(
    mergedPrs.filter((pr) => {
      if (!pr.merged_at) return false
      const mergedAt = Date.parse(pr.merged_at)
      return Number.isFinite(mergedAt) && Date.now() - mergedAt <= 30 * 24 * 60 * 60 * 1000
    }),
    workflowRuns,
  )
  const report = buildRollingReport({ records, regressions, missing, fullGates })
  mkdirSync(outputDir, { recursive: true })
  writeFileSync(join(outputDir, 'rolling-metrics.json'), `${JSON.stringify(report, null, 2)}\n`, 'utf8')
  writeFileSync(join(outputDir, 'rolling-metrics.md'), `${report.markdown}\n`, 'utf8')
  console.log(`[ci-metrics] Rolling collector processed ${records.length} runs; ${missing.length} runs missing/unreadable; ${regressions.length} sustained regressions.`)
}

main().catch((error) => {
  console.error(`[ci-metrics] Rolling collector failed: ${error.message}`)
  process.exit(1)
})
