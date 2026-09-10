import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { readSingleJsonFromZip, readNamedJsonFromZip } from './zip.mjs'
import { compareTrustedSurfaces } from './merge-authority-github.mjs'
import { collectMergedPaths } from './provenance.mjs'
import { looksLikeCredential } from './fragment.mjs'
import { resolveJobOrigins } from './api-observations.mjs'
import { REUSE_REPOSITORY, requiredNativeNames, reconcileReuseEvidence, evaluateQueueReuseShadow } from './queue-reuse-shadow.mjs'

const prefix = `/repos/${REUSE_REPOSITORY}`
const positive = n => Number.isSafeInteger(Number(n)) && Number(n) > 0
const sha = s => typeof s === 'string' && /^[a-f0-9]{40}$/.test(s)
const root = fileURLToPath(new URL('../../..', import.meta.url))

// The operator's existing gh credential is used only inside gh for fixed-repository GET requests.
// No token is retrieved or written into the packet; artifact entries remain data, never commands.
export function createReuseReader() {
  function get(path, binary = false) {
    if (!path.startsWith(`${prefix}/`) || /[\r\n#]/.test(path)) throw new Error('Observer request outside fixed repository')
    const output = execFileSync('gh', ['api', '--hostname', 'github.com', '--method', 'GET', path], {
      encoding: binary ? null : 'utf8', maxBuffer: 32 * 1024 * 1024, timeout: 120_000,
      stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
    })
    return binary ? output : JSON.parse(output)
  }
  return { sourceAuthenticity: 'authenticated-github-cli', request: path => get(path), zip: id => {
    if (!positive(id)) throw new Error('Invalid artifact id')
    return get(`${prefix}/actions/artifacts/${id}/zip`, true)
  } }
}

export async function readAllReusePages(request, path, key = null) {
  const rows = []; let total = null
  for (let page = 1; page <= 30; page++) {
    const body = await request(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`)
    const batch = key ? body?.[key] : body
    if (!Array.isArray(batch) || batch.length > 100) throw new Error('Malformed paginated metadata')
    if (key) {
      if (!Number.isSafeInteger(body.total_count) || body.total_count < 0) throw new Error('Missing pagination count')
      if (total !== null && total !== body.total_count) throw new Error('Pagination changed during collection')
      total = body.total_count
    }
    rows.push(...batch)
    if (batch.length < 100) {
      if (key && rows.length !== total) throw new Error('Incomplete pagination')
      const ids = rows.map(r => r.id)
      if (ids.some(id => !positive(id)) || new Set(ids).size !== ids.length) throw new Error('Missing or duplicate metadata identities')
      return rows
    }
  }
  throw new Error('Pagination bound exceeded')
}

export function deriveObserverTopology(run, tree) {
  const directory = mkdtempSync(join(tmpdir(), 'aerolink-reuse-topology-'))
  try {
    const path = join(directory, 'meta.json')
    const safeEnv = Object.fromEntries(Object.entries(process.env).filter(([name]) =>
      !/^(?:GITHUB_|METRICS_|CLASS_|PULL_REQUEST_|FULL_DIAGNOSTICS)/i.test(name)))
    execFileSync(process.execPath, [join(root, 'product/ci-metrics/bin/build-run-meta.mjs')], { windowsHide: true,
      env: { ...safeEnv, METRICS_TREE_SHA: tree, METRICS_RUN_META_PATH: path,
        GITHUB_EVENT_NAME: run.event, GITHUB_REF: `refs/heads/${run.head_branch}`, GITHUB_RUN_ID: String(run.id),
        GITHUB_RUN_ATTEMPT: String(run.run_attempt), GITHUB_SHA: run.head_sha, GITHUB_REPOSITORY: REUSE_REPOSITORY,
        GITHUB_WORKFLOW: 'Product quality gate', GITHUB_WORKFLOW_REF: `${REUSE_REPOSITORY}/.github/workflows/ci.yml@refs/heads/${run.head_branch}`,
        CLASS_DOCS_ONLY: 'false', CLASS_BACKEND: 'true', CLASS_CLIENT: 'true', CLASS_BROWSER: 'true', CLASS_POSTGRESQL: 'true', FULL_DIAGNOSTICS: 'true' },
      stdio: ['ignore', 'pipe', 'pipe'], timeout: 30_000 })
    return JSON.parse(readFileSync(path, 'utf8'))
  } finally { rmSync(directory, { recursive: true, force: true }) }
}

export async function collectReuseJobOrigins(reader, run, jobs) {
  if (!positive(run.run_attempt) || run.run_attempt > 30) throw new Error('Unsupported attempt count')
  const allJobs = await readAllReusePages(reader.request, `${prefix}/actions/runs/${run.id}/jobs?filter=all`, 'jobs')
  const attemptRuns = []
  for (let attempt = 1; attempt <= run.run_attempt; attempt++) {
    const metadata = await reader.request(`${prefix}/actions/runs/${run.id}/attempts/${attempt}`)
    if (metadata.id !== run.id || metadata.run_attempt !== attempt || metadata.head_sha !== run.head_sha ||
      metadata.repository?.full_name !== REUSE_REPOSITORY) throw new Error('Attempt source identity mismatch')
    attemptRuns.push(metadata)
  }
  const ledger = resolveJobOrigins({ latestJobs: jobs, allJobs, runId: run.id, runAttempt: run.run_attempt, attemptRuns })
  if (!ledger.attemptMetadata.complete || ledger.duplicateIds.length) throw new Error('Incomplete originating attempt metadata')
  const unresolved = new Set([...ledger.unresolvedCopies, ...ledger.invalidTimings].map(j => j.jobId))
  return { allJobs, attemptRuns, ledger, jobs: jobs.map(job => {
    const origin = ledger.ledger.find(j => j.effective && j.id === job.id)
    return { ...job, executionOrigin: { jobId: origin?.originJobId ?? null, attempt: origin?.originAttempt ?? null,
      proven: Boolean(origin && !unresolved.has(job.id) && !unresolved.has(origin.originJobId)) } }
  }) }
}

async function collectEvidence(reader, run, tree, jobs, topology) {
  const artifacts = await readAllReusePages(reader.request, `${prefix}/actions/runs/${run.id}/artifacts`, 'artifacts')
  async function named(name, file) {
    const matches = artifacts.filter(a => a.name === name)
    if (matches.length !== 1 || matches[0].expired || !Number.isSafeInteger(matches[0].size_in_bytes) || matches[0].size_in_bytes > 32 * 1024 * 1024) {
      throw new Error(`Missing, expired, duplicated or oversized artifact: ${name}`)
    }
    const archive = await reader.zip(matches[0].id)
    const data = file ? readNamedJsonFromZip(archive, file) : readSingleJsonFromZip(archive)
    function rejectCredential(value) {
      if (typeof value === 'string' && looksLikeCredential(value)) throw new Error('Artifact contains prohibited credential-like data')
      if (value && typeof value === 'object') for (const child of Object.values(value)) rejectCredential(child)
    }
    rejectCredential(data)
    return data
  }
  const record = await named(`ci-metrics-run-${run.id}-${run.run_attempt}`, 'run-metrics.json')
  const manifest = await named(`validated-tree-${run.id}-${run.run_attempt}`, 'validated-tree.json')
  const fragments = []
  for (const job of topology.expectedJobs) {
    const recorded = record.jobs?.filter(j => j.instance === job.instance)
    if (recorded?.length !== 1) throw new Error(`Missing/duplicate recorded job ${job.instance}`)
    const native = jobs.filter(j => j.name === recorded[0].name)
    const origin = native[0]?.executionOrigin
    if (native.length !== 1 || !origin?.proven || !positive(origin.attempt) || origin.attempt > run.run_attempt) throw new Error(`Unproven native fragment origin ${job.instance}`)
    fragments.push(await named(`ci-metrics-fragment-${job.instance}-${origin.attempt}`))
  }
  return { record, manifest, fragments, topology, artifacts: artifacts.map(a => ({ id: a.id, name: a.name, expired: a.expired })) }
}

function proveComposition(candidate, pr, associatedPrs, cwd) {
  const baseSha = candidate.parents?.[0]?.sha
  if (!sha(baseSha) || !sha(pr.head?.sha) || candidate.parents.length !== 1 || associatedPrs.length !== 1) throw new Error('Unsupported queue composition')
  const git = args => execFileSync('git', args, { cwd, encoding: 'utf8', timeout: 120_000, maxBuffer: 5 * 1024 * 1024, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] }).trim()
  for (const value of [baseSha, pr.head.sha, candidate.sha]) {
    try { git(['cat-file', '-e', `${value}^{commit}`]) } catch {
      git(['-c', 'fetch.writeCommitGraph=false', 'fetch', '--no-tags', '--no-write-fetch-head', `https://github.com/${REUSE_REPOSITORY}.git`, value])
    }
  }
  const treeSha = git(['merge-tree', '--write-tree', baseSha, pr.head.sha]).split(/\r?\n/)[0]
  if (!sha(treeSha)) throw new Error('Composition did not produce one tree')
  return { method: 'git-merge-tree', baseSha, prHeadSha: pr.head.sha, treeSha, associatedPrs: associatedPrs.map(p => p.number) }
}

async function collectFallback(reader, fallbackRunId, candidateSha, now) {
  if (!positive(fallbackRunId)) return { passed: false, reason: 'No fallback run was supplied' }
  const run = await reader.request(`${prefix}/actions/runs/${fallbackRunId}`)
  const result = { run, passed: false, reason: 'Fallback qualification incomplete' }
  try {
    const workflow = result.workflow = await reader.request(`${prefix}/actions/workflows/ci.yml`)
    if (run.repository?.full_name !== REUSE_REPOSITORY || run.status !== 'completed' || run.conclusion !== 'success' ||
      run.workflow_id !== workflow.id || workflow.path !== '.github/workflows/ci.yml' ||
      run.name !== 'Product quality gate' || run.path !== '.github/workflows/ci.yml' || run.head_branch !== 'main' ||
      !['schedule', 'workflow_dispatch'].includes(run.event)) throw new Error('Fallback is not a completed successful main diagnostic')
    const commit = result.commit = await reader.request(`${prefix}/git/commits/${run.head_sha}`)
    const changed = await compareTrustedSurfaces({ request: reader.request, repository: REUSE_REPOSITORY, candidateSha, baseSha: run.head_sha })
    result.protectedDefinitionMatches = changed.length === 0
    result.protectedChanges = changed
    if (changed.length) throw new Error('Fallback protected definition differs from candidate')
    const latestJobs = await readAllReusePages(reader.request, `${prefix}/actions/runs/${run.id}/jobs?filter=latest`, 'jobs')
    result.jobOrigins = await collectReuseJobOrigins(reader, run, latestJobs)
    const jobs = result.jobs = result.jobOrigins.jobs
    const names = requiredNativeNames().filter(n => !n.startsWith('Browser journeys (')).concat([1, 2, 3].map(n => `Full browser journeys (${n}/3)`))
    const checks = result.checks = await readAllReusePages(reader.request, `${prefix}/commits/${run.head_sha}/check-runs?filter=all`, 'check_runs')
    for (const name of names) {
      const matches = jobs.filter(j => j.name === name)
      const native = checks.filter(c => c.name === name && c.check_suite?.id === run.check_suite_id &&
        matches[0]?.check_run_url === `https://api.github.com/repos/${REUSE_REPOSITORY}/check-runs/${c.id}`)
      if (matches.length !== 1 || matches[0].status !== 'completed' || matches[0].conclusion !== 'success' || matches[0].run_id !== run.id ||
        native.length !== 1 || native[0].app?.id !== 15368 || native[0].conclusion !== 'success') throw new Error(`Fallback required proof missing: ${name}`)
    }
    const evidence = result.evidence = await collectEvidence(reader, run, commit.tree.sha, jobs, deriveObserverTopology(run, commit.tree.sha))
    const reconciliation = reconcileReuseEvidence({ run, tree: commit.tree.sha, jobs, ...evidence }, now)
    if (reconciliation.errors.length) throw new Error(reconciliation.errors.join('; '))
    const latest = result.latestRun = await reader.request(`${prefix}/actions/runs/${run.id}`)
    result.currentAttempt = latest.run_attempt === run.run_attempt && latest.head_sha === run.head_sha && latest.status === 'completed' && latest.conclusion === 'success'
    result.passed = result.currentAttempt
    result.reason = result.passed ? null : 'Fallback attempt changed'
  } catch (error) { result.reason = String(error.message).slice(0, 500) }
  return result
}

export async function collectQueueReuseShadow({ runId, prNumber, fallbackRunId, cwd = root, reader = createReuseReader(), now = Date.now(),
  compositionProof = proveComposition, topology = deriveObserverTopology } = {}) {
  if (!positive(runId) || !positive(prNumber)) throw new Error('Positive run and PR identifiers are required')
  const started = performance.now()
  const packet = { collectionErrors: [] }
  try {
    packet.run = await reader.request(`${prefix}/actions/runs/${runId}`)
    packet.pr = await reader.request(`${prefix}/pulls/${prNumber}`)
    if (!sha(packet.run.head_sha) || !sha(packet.pr.merge_commit_sha)) throw new Error('Candidate or landed SHA unavailable')
    packet.workflow = await reader.request(`${prefix}/actions/workflows/ci.yml`)
    packet.candidate = await reader.request(`${prefix}/git/commits/${packet.run.head_sha}`)
    packet.landed = await reader.request(`${prefix}/git/commits/${packet.pr.merge_commit_sha}`)
    packet.main = await reader.request(`${prefix}/branches/main`)
    packet.mainRelationship = await reader.request(`${prefix}/compare/${packet.landed.sha}...${packet.main.commit.sha}`)
    const headChecks = await readAllReusePages(reader.request, `${prefix}/commits/${packet.pr.head.sha}/check-runs?filter=latest`, 'check_runs')
    const binding = headChecks.filter(c => c.name === 'Trusted merge-queue binding')
    packet.prReadiness = { binding: binding.length === 1 ? binding[0] : null }
    const match = binding[0]?.details_url?.match(new RegExp(`^https://github.com/${REUSE_REPOSITORY}/actions/runs/([0-9]+)$`))
    if (match) packet.prReadiness.run = await reader.request(`${prefix}/actions/runs/${match[1]}`)
    const associated = await readAllReusePages(reader.request, `${prefix}/commits/${packet.landed.sha}/pulls`)
    packet.composition = compositionProof(packet.candidate, packet.pr, associated, cwd)
    packet.changedPaths = await collectMergedPaths({ prNumber, api: path => reader.request(`${prefix}${path}`) })
    packet.protectedChanges = await compareTrustedSurfaces({ request: reader.request, repository: REUSE_REPOSITORY,
      candidateSha: packet.candidate.sha, baseSha: packet.composition.baseSha })
    packet.jobs = await readAllReusePages(reader.request, `${prefix}/actions/runs/${runId}/jobs?filter=latest`, 'jobs')
    packet.jobOrigins = await collectReuseJobOrigins(reader, packet.run, packet.jobs)
    packet.jobs = packet.jobOrigins.jobs
    packet.checks = await readAllReusePages(reader.request, `${prefix}/commits/${packet.candidate.sha}/check-runs?filter=all`, 'check_runs')
    packet.evidence = await collectEvidence(reader, packet.run, packet.candidate.tree.sha, packet.jobs, topology(packet.run, packet.candidate.tree.sha))
    packet.fallback = await collectFallback(reader, fallbackRunId, packet.candidate.sha, now)
    packet.latestRun = await reader.request(`${prefix}/actions/runs/${runId}`)
    const currentPr = await reader.request(`${prefix}/pulls/${prNumber}`)
    if (currentPr.head?.sha !== packet.pr.head?.sha || currentPr.merge_commit_sha !== packet.pr.merge_commit_sha) throw new Error('PR identity changed during collection')
  } catch (error) { packet.collectionErrors.push(String(error.message).slice(0, 500)) }
  const report = evaluateQueueReuseShadow(packet, { now, source: reader.sourceAuthenticity === 'authenticated-github-cli'
    ? 'authenticated-github-metadata; artifact assertions require individual reconciliation' : 'unverified-fixture' })
  report.observerMs = Math.round(performance.now() - started)
  report.collectedAt = new Date(now).toISOString()
  return { report, packet }
}
