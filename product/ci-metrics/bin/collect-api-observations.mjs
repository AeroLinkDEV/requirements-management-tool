// Collect authenticated, read-only API shard observations from GitHub Actions.
//
// Usage: node collect-api-observations.mjs --output <owned-output-dir> [--window 8]
// GITHUB_TOKEN and GITHUB_REPOSITORY are required environment inputs. The collector never executes
// downloaded artifact content; it parses only named api-observation.json, shard.trx, and v2 fragment JSON entries through bounded readers.

import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { isAbsolute, relative, resolve, join } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import {
  createReadOnlyGitHubRequest, downloadArtifactZip, fetchCommitTree, fetchRunArtifacts, fetchRunJobs,
  fetchWorkflow, fetchWorkflowDefinition, fetchWorkflowRun, fetchWorkflowRunAttempt, fetchWorkflowRuns, readObservationArtifact,
} from '../lib/github-readonly.mjs'
import {
  API_OBSERVATION_REPORT_SCHEMA, API_REPOSITORY, API_WORKFLOW_NAME, API_SHARD_COUNT,
  MAX_OBSERVATION_RUNS, buildApiObservationRun, renderApiObservationMarkdown, resolveJobOrigins,
} from '../lib/api-observations.mjs'
import { readNamedJsonFromZip } from '../lib/zip.mjs'

const env = (name) => process.env[name] ?? ''

function parseArgs(argv) {
  const args = { output: null, window: 8 }
  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index]
    if (flag === '--output') args.output = argv[++index] ?? null
    else if (flag === '--window') args.window = Number(argv[++index] ?? NaN)
    else throw new Error(`Unknown option '${flag}'.`)
  }
  if (!args.output || !isAbsolute(args.output)) throw new Error('--output must be an absolute owned output directory.')
  if (!Number.isSafeInteger(args.window) || args.window < 1 || args.window > MAX_OBSERVATION_RUNS) throw new Error(`--window must be 1 through ${MAX_OBSERVATION_RUNS}.`)
  const output = resolve(args.output)
  const temp = resolve(tmpdir())
  if (relative(temp, output).startsWith('..') || relative(temp, output).includes(':')) throw new Error('--output must be under the Windows temp directory.')
  if (existsSync(output)) throw new Error('--output must name a new owned temp directory; refusing to overwrite existing collector output.')
  return { ...args, output }
}

function parseIsoMs(value) {
  if (typeof value !== 'string') return null
  const parsed = Date.parse(value)
  return Number.isFinite(parsed) ? parsed : null
}

function artifactCandidates(artifacts, runId) {
  const candidates = []
  const seenShards = new Set()
  for (const artifact of artifacts) {
    const match = /^api-observations-([1-3])-([1-9][0-9]*)$/.exec(String(artifact.name ?? ''))
    if (!match) continue
    const shard = Number(match[1])
    const artifactAttempt = Number(match[2])
    if (artifact.workflow_run?.id !== undefined && artifact.workflow_run.id !== runId) continue
    if (artifact.expired === true) {
      candidates.push({ shard, artifactAttempt, error: 'Artifact is expired.' })
      continue
    }
    const identity = `${shard}:${artifactAttempt}`
    if (seenShards.has(identity)) {
      candidates.push({ shard, artifactAttempt, artifact, error: `Duplicate observation artifacts for shard ${shard}.` })
      continue
    }
    seenShards.add(identity)
    candidates.push({ shard, artifactAttempt, artifact })
  }
  return candidates.sort((a, b) => a.shard - b.shard)
}

function fragmentCandidates(artifacts, runId) {
  const candidates = []
  const seenShards = new Set()
  for (const artifact of artifacts) {
    const match = /^ci-metrics-fragment-backend-api-([1-3])-([1-9][0-9]*)$/.exec(String(artifact.name ?? ''))
    if (!match) continue
    const shard = Number(match[1])
    const artifactAttempt = Number(match[2])
    if (artifact.workflow_run?.id !== undefined && artifact.workflow_run.id !== runId) continue
    if (artifact.expired === true) {
      candidates.push({ shard, artifactAttempt, error: 'Metrics fragment artifact is expired.' })
      continue
    }
    const identity = `${shard}:${artifactAttempt}`
    if (seenShards.has(identity)) {
      candidates.push({ shard, artifactAttempt, artifact, error: `Duplicate metrics fragments for shard ${shard}.` })
      continue
    }
    seenShards.add(identity)
    candidates.push({ shard, artifactAttempt, artifact })
  }
  return candidates.sort((a, b) => a.shard - b.shard)
}

async function readFragmentCandidate(candidate, { token, apiUrl, repository, shard, fetchImpl }) {
  if (candidate.error) return candidate
  try {
    const zip = await downloadArtifactZip({ fetchImpl, token, apiUrl, repository, artifactId: candidate.artifact.id })
    const fragment = readNamedJsonFromZip(zip, `fragment-backend-api-${shard}.json`)
    return { ...candidate, fragment }
  } catch (error) {
    return { ...candidate, error: String(error.message).slice(0, 300) }
  }
}

async function readCandidate(candidate, { token, apiUrl, repository, run, job, originJob, fetchImpl }) {
  if (candidate.error) return candidate
  if (job.run_id !== run.id) return { ...candidate, job, error: 'API job originated from another workflow run.' }
  try {
    const zip = await downloadArtifactZip({ fetchImpl, token, apiUrl, repository, artifactId: candidate.artifact.id })
    const parsed = readObservationArtifact(zip)
    return { ...candidate, job, originJob: originJob ?? job, artifact: parsed.artifact, trxText: parsed.trxText }
  } catch (error) {
    return { ...candidate, job, error: String(error.message).slice(0, 300) }
  }
}

function topLevelExclusion(run, reason) {
  return { kind: 'run', runId: run?.id ?? null, reason: String(reason).slice(0, 300) }
}

export async function collectApiObservations({ token, repository = API_REPOSITORY, apiUrl = 'https://api.github.com', window = 8, request, fetchImpl = globalThis.fetch }) {
  if (repository !== API_REPOSITORY) throw new Error(`GITHUB_REPOSITORY must be ${API_REPOSITORY}.`)
  if (!token) throw new Error('A GitHub token is required.')
  if (!Number.isSafeInteger(window) || window < 1 || window > MAX_OBSERVATION_RUNS) throw new Error(`window must be 1 through ${MAX_OBSERVATION_RUNS}.`)
  // A caller-supplied reader is a fixture seam, even when it receives a token-shaped argument. Only the
  // default fixed-origin REST client path may attribute GitHub authentication to the source metadata.
  const sourceAuthenticated = request === undefined && fetchImpl === globalThis.fetch
  const read = request ?? createReadOnlyGitHubRequest({ token, repository, apiUrl, fetchImpl })
  const workflow = await fetchWorkflow({ request: read, repository })
  const allRuns = await fetchWorkflowRuns({ request: read, repository })
  const runs = allRuns
    .filter((run) => run?.name === API_WORKFLOW_NAME || run?.workflow_id === workflow.id)
    .sort((a, b) => String(b.created_at ?? '').localeCompare(String(a.created_at ?? '')))
    .slice(0, window)
  const observations = []
  const exclusions = []
  for (const listedRun of runs) {
    try {
      const run = await fetchWorkflowRun({ request: read, repository, runId: listedRun.id })
      if (run.repository?.full_name !== API_REPOSITORY || run.name !== API_WORKFLOW_NAME || (run.workflow_id !== undefined && run.workflow_id !== workflow.id)) {
        exclusions.push(topLevelExclusion(run, 'Run metadata does not match the fixed Product quality gate workflow.'))
        continue
      }
      if (run.status !== 'completed') {
        exclusions.push(topLevelExclusion(run, `Run is ${run.status ?? 'not completed'}.`))
        continue
      }
      const treeSha = await fetchCommitTree({ request: read, repository, commitSha: run.head_sha })
      const workflowDefinition = await fetchWorkflowDefinition({ request: read, repository, commitSha: run.head_sha })
      const runAttempt = Number(run.run_attempt ?? 1)
      if (!Number.isSafeInteger(runAttempt) || runAttempt < 1 || runAttempt > 40) throw new Error('Run attempt count is outside the bounded collector range.')
      const attemptRuns = await Promise.all(Array.from({ length: runAttempt }, (_, index) => fetchWorkflowRunAttempt({ request: read, repository, runId: run.id, attempt: index + 1 })))
      const [latestJobs, allJobs, artifacts] = await Promise.all([
        fetchRunJobs({ request: read, repository, runId: run.id, filter: 'latest' }),
        fetchRunJobs({ request: read, repository, runId: run.id, filter: 'all' }),
        fetchRunArtifacts({ request: read, repository, runId: run.id }),
      ])
      const attempts = resolveJobOrigins({ latestJobs, allJobs, runId: run.id, runAttempt, attemptRuns })
      const allArtifacts = artifactCandidates(artifacts, run.id)
      const allFragments = fragmentCandidates(artifacts, run.id)
      const readCandidates = []
      const readFragments = []
      for (let shard = 1; shard <= API_SHARD_COUNT; shard += 1) {
        const effectiveJobs = latestJobs.filter((job) => job.name === `API test suite (${shard}/3)`)
        const effectiveJob = effectiveJobs.length === 1 ? effectiveJobs[0] : null
        const ledger = effectiveJob ? attempts.ledger.find((entry) => entry.id === effectiveJob.id) : null
        const originJob = ledger ? allJobs.find((job) => job.id === ledger.originJobId) : null
        const originAttempt = ledger?.originAttempt ?? null
        const observationMatches = allArtifacts.filter((candidate) => candidate.shard === shard && candidate.artifactAttempt === originAttempt)
        const observationCandidate = observationMatches.length === 1
          ? observationMatches[0]
          : { shard, error: observationMatches.length > 1 ? `Duplicate observation artifacts for originating attempt ${originAttempt}.` : `Observation artifact for shard ${shard} and originating attempt ${originAttempt ?? 'unknown'} is missing.` }
        readCandidates.push(await readCandidate(observationCandidate, { token, apiUrl, repository, run, job: effectiveJob ?? {}, originJob: originJob ?? effectiveJob, fetchImpl }))
        const fragmentMatches = allFragments.filter((candidate) => candidate.shard === shard && candidate.artifactAttempt === originAttempt)
        const fragmentCandidate = fragmentMatches.length === 1
          ? fragmentMatches[0]
          : { shard, error: fragmentMatches.length > 1 ? `Duplicate metrics fragments for originating attempt ${originAttempt}.` : `Metrics fragment for shard ${shard} and originating attempt ${originAttempt ?? 'unknown'} is missing.` }
        readFragments.push(await readFragmentCandidate(fragmentCandidate, { token, apiUrl, repository, shard, fetchImpl }))
      }
      const observation = buildApiObservationRun({
        apiRun: run,
        workflow,
        workflowDefinition,
        treeSha,
        artifactResults: readCandidates.sort((a, b) => a.shard - b.shard),
        latestJobs,
        allJobs,
        attemptRuns,
        fragmentResults: readFragments.sort((a, b) => a.shard - b.shard),
        sourceAuthenticated,
      })
      for (const entry of observation.shards) {
        const start = parseIsoMs(entry.job.startedAt)
        const end = parseIsoMs(entry.job.completedAt)
        entry.timing = { ...entry.timing, wallMs: start !== null && end !== null && end >= start ? end - start : null, apiTimingMissing: start === null || end === null ? 'GitHub job timestamps unavailable.' : null }
      }
      observations.push(observation)
    } catch (error) {
      exclusions.push(topLevelExclusion(listedRun, `Run could not be collected: ${error.message}`))
    }
  }
  observations.sort((a, b) => (a.sourceMetadata.run.id - b.sourceMetadata.run.id))
  const eligible = observations.filter((run) => run.comparability.eligible)
  const report = {
    schemaVersion: API_OBSERVATION_REPORT_SCHEMA,
    repository: API_REPOSITORY,
    collector: {
      mode: sourceAuthenticated ? 'authenticated-read-only' : 'injected-read-only-fixture',
      sourceAuthenticated,
      workflow: { id: workflow.id, name: workflow.name, path: workflow.path },
      source: 'GitHub Actions REST API plus attempt-scoped API observation artifacts',
      defaultShardCount: API_SHARD_COUNT,
      adoptionAuthority: 'absent',
    },
    runs: observations,
    exclusions: exclusions.slice(0, 200),
    comparability: {
      eligibleRunCount: eligible.length,
      minimumRepresentativeRunCount: 8,
      sufficientForPerformanceConclusion: false,
      reason: eligible.length >= 8 ? 'Collector output is evidence input; baseline/treatment cohorts still require pre-declared matching configurations and independent timing.' : `Only ${eligible.length} complete comparable run(s) were collected; eight per configuration are required for a representative conclusion.`,
    },
  }
  return report
}

async function main() {
  let args
  try { args = parseArgs(process.argv.slice(2)) } catch (error) {
    console.error(`[ci-metrics] ${error.message}`)
    console.error('usage: node collect-api-observations.mjs --output <owned-temp-dir> [--window 8]')
    process.exit(2)
  }
  const token = env('GITHUB_TOKEN')
  const repository = env('GITHUB_REPOSITORY')
  if (!token || !repository) {
    console.error('[ci-metrics] GITHUB_TOKEN and GITHUB_REPOSITORY are required; no local or production fallback is used.')
    process.exit(2)
  }
  try {
    const report = await collectApiObservations({ token, repository, apiUrl: env('GITHUB_API_URL') || 'https://api.github.com', window: args.window })
    mkdirSync(args.output, { recursive: true })
    writeFileSync(join(args.output, 'api-observations.json'), `${JSON.stringify(report, null, 2)}\n`, 'utf8')
    writeFileSync(join(args.output, 'api-observations.md'), `${renderApiObservationMarkdown(report)}\n`, 'utf8')
    const retainedNonComparableRuns = report.runs.filter((run) => run.comparability?.eligible !== true).length
    console.log(`[ci-metrics] API observations: ${report.runs.length} runs; ${report.comparability.eligibleRunCount} comparable; collection exclusions=${report.exclusions.length}; retained non-comparable runs=${retainedNonComparableRuns}.`)
  } catch (error) {
    console.error(`[ci-metrics] API observation collection failed: ${error.message}`)
    process.exit(1)
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) main()
