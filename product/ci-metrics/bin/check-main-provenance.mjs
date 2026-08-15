// Shadow provenance checker for #562 (phase A).
//
// Triggered by workflow_run for every completed quality-gate run. For a main push it resolves the merged
// pull request, locates validated-tree manifests from that PR's successful gate runs, and decides whether
// the pushed tree was already validated. Phase A is observation only: the post-merge product gate still
// runs, and canSkip is always false in the output.

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import { readSingleJsonFromZip } from '../lib/zip.mjs'
import { decideProvenance, validateManifest, bindManifest, GATE_DEFINING_PATHS } from '../lib/provenance.mjs'

const env = (name) => process.env[name] ?? ''

async function api(path, { token, apiUrl } = {}) {
  const response = await fetch(`${apiUrl}${path}`, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  })
  if (!response.ok) throw new Error(`GitHub API ${path} returned ${response.status}.`)
  return response.json()
}

async function listAll(path, { token, apiUrl } = {}) {
  const items = []
  let page = 1
  while (true) {
    const body = await api(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`, { token, apiUrl })
    const rows = Array.isArray(body) ? body : body.items ?? body.workflow_runs ?? body.artifacts ?? []
    items.push(...rows)
    if (rows.length < 100) break
    page += 1
    if (page > 5) break
  }
  return items
}

async function fetchTree(sha, { token, apiUrl, repository }) {
  const body = await api(`/repos/${repository}/git/commits/${sha}`, { token, apiUrl })
  return body.tree?.sha ?? null
}

/**
 * The paths this merge introduced, from GitHub's own view of the pull request rather than from
 * anything the branch supplied. Used only to decide whether the merge edits the gate's own definition.
 */
async function fetchMergedPaths(prNumber, { token, apiUrl, repository }) {
  const files = await listAll(`/repos/${repository}/pulls/${prNumber}/files`, { token, apiUrl })
  return files.map((file) => file?.filename).filter((name) => typeof name === 'string')
}

async function latestManifestForRun(runId, { token, apiUrl, repository }) {
  const artifacts = await listAll(`/repos/${repository}/actions/runs/${runId}/artifacts`, { token, apiUrl })
  const prefix = `validated-tree-${runId}-`
  let best = null
  for (const artifact of artifacts) {
    if (!artifact.name.startsWith(prefix)) continue
    const attempt = Number(artifact.name.slice(prefix.length))
    if (!Number.isInteger(attempt) || attempt < 1) continue
    if (best === null || attempt > best.attempt) best = { artifact, attempt }
  }
  if (!best) return null
  const response = await fetch(`${apiUrl}/repos/${repository}/actions/artifacts/${best.artifact.id}/zip`, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
  })
  if (!response.ok) return null
  const zip = Buffer.from(await response.arrayBuffer())
  try {
    return { manifest: readSingleJsonFromZip(zip), attempt: best.attempt }
  } catch {
    return null
  }
}

function escapeMarkdown(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\|/g, '\\|')
    .replace(/\r?\n/g, ' ')
}

async function main() {
  const token = env('GITHUB_TOKEN')
  const apiUrl = env('GITHUB_API_URL') || 'https://api.github.com'
  const repository = env('GITHUB_REPOSITORY')
  const eventPath = env('GITHUB_EVENT_PATH')
  const outputDir = env('PROVENANCE_OUTPUT_DIR')
  if (!token || !repository || !eventPath || !outputDir) {
    console.error('[ci-metrics] GITHUB_TOKEN, GITHUB_REPOSITORY, GITHUB_EVENT_PATH, and PROVENANCE_OUTPUT_DIR are required.')
    process.exit(2)
  }
  const event = JSON.parse(readFileSync(eventPath, 'utf8'))
  const run = event.workflow_run
  const pushSha = run?.head_sha ?? null
  const isMainPush = run?.event === 'push' && run?.head_branch === 'main'

  let result
  if (!isMainPush) {
    result = {
      schemaVersion: 'aerolink-main-provenance/v1',
      mode: 'shadow',
      triggeringRun: { id: run?.id ?? null, event: run?.event ?? null, branch: run?.head_branch ?? null },
      outcome: 'not-applicable',
      reason: 'Only main push quality-gate runs are provenance candidates.',
      canSkip: false,
    }
  } else {
    const pushTree = await fetchTree(pushSha, { token, apiUrl, repository })
    const closedPrs = await listAll(`/repos/${repository}/pulls?state=closed&sort=updated&direction=desc`, { token, apiUrl })
    const mergedPr = closedPrs.find((pr) => pr.merged_at && pr.merge_commit_sha === pushSha && pr.head?.ref) ?? null
    const manifests = []
    const manifestErrors = []
    if (mergedPr) {
      const runs = await listAll(`/repos/${repository}/actions/workflows/ci.yml/runs`, { token, apiUrl })
      const created = Date.parse(mergedPr.created_at)
      const merged = Date.parse(mergedPr.merged_at)
      const cutoff = merged + 24 * 60 * 60 * 1000
      const candidates = runs
        .filter((candidate) => candidate.event === 'pull_request' && candidate.head_branch === mergedPr.head.ref && candidate.conclusion === 'success')
        .filter((candidate) => {
          const at = Date.parse(candidate.created_at)
          return Number.isFinite(at) && at >= created - 60 * 60 * 1000 && at <= cutoff
        })
        .sort((a, b) => String(b.created_at).localeCompare(String(a.created_at)))
      for (const candidate of candidates.slice(0, 10)) {
        const downloaded = await latestManifestForRun(candidate.id, { token, apiUrl, repository })
        if (!downloaded) {
          manifestErrors.push({ runId: candidate.id, reason: 'No validated-tree manifest artifact found.' })
          continue
        }
        const manifest = downloaded.manifest
        const errors = validateManifest(manifest)
        if (errors.length > 0) {
          manifestErrors.push({ runId: candidate.id, reason: `Manifest failed validation: ${errors.join('; ')}` })
          continue
        }
        const checkoutTree = await fetchTree(manifest.checkedOut.commitSha, { token, apiUrl, repository })
        const bound = bindManifest(manifest, {
          repository,
          workflow: 'Product quality gate',
          runId: candidate.id,
          runAttempt: candidate.run_attempt ?? 1,
          artifactAttempt: downloaded.attempt,
          prNumber: mergedPr.number,
          expectedHeadSha: candidate.head_sha,
          expectedBaseSha: mergedPr.base?.sha ?? null,
          expectedMergeRef: `refs/pull/${mergedPr.number}/merge`,
          checkoutCommitTree: checkoutTree,
        })
        if (!bound.ok) {
          manifestErrors.push({ runId: candidate.id, reason: bound.reason })
          continue
        }
        manifests.push(manifest)
      }
    }
    // Fail closed: if GitHub will not tell us what the merge changed, we cannot rule out that it
    // changed the gate itself, so the decision must be the same as if it had.
    let changedPaths = null
    let changedPathsError = null
    try {
      changedPaths = await fetchMergedPaths(mergedPr.number, { token, apiUrl, repository })
    } catch (error) {
      changedPathsError = error.message
      changedPaths = [...GATE_DEFINING_PATHS]
    }
    const decision = decideProvenance({
      pushTreeSha: pushTree,
      mergedPr,
      manifests,
      now: Date.now(),
      changedPaths,
    })
    result = {
      schemaVersion: 'aerolink-main-provenance/v1',
      mode: 'shadow',
      triggeringRun: { id: run?.id ?? null, event: run?.event ?? null, branch: run?.head_branch ?? null },
      push: { commitSha: pushSha, treeSha: pushTree },
      mergedPr: mergedPr ? { number: mergedPr.number, mergedAt: mergedPr.merged_at, headRef: mergedPr.head.ref } : null,
      manifestsFound: manifests.length,
      manifestErrors: manifestErrors.slice(0, 20),
      outcome: decision.outcome,
      canSkip: decision.canSkip,
      reason: decision.reason,
      source: decision.source ?? null,
      rejected: decision.rejected ?? [],
      selfModifying: decision.selfModifying === true,
      changedPathsUnavailable: changedPathsError,
    }
  }

  const lines = []
  lines.push('# Main-push provenance check (shadow)')
  lines.push('')
  lines.push(`- Mode: ${escapeMarkdown(result.mode)} (observation only; the post-merge gate still runs)`)
  lines.push(`- Outcome: ${escapeMarkdown(result.outcome)}`)
  if (result.push) lines.push(`- Pushed tree: \`${escapeMarkdown(result.push.treeSha)}\``)
  if (result.source) lines.push(`- Validated by PR #${result.source.pr}, run ${result.source.runId} attempt ${result.source.attempt}, tree \`${escapeMarkdown(result.source.treeSha)}\``)
  if (result.reason) lines.push(`- Reason: ${escapeMarkdown(result.reason)}`)
  if (result.selfModifying) lines.push('- This merge changed the gate\'s own definition, so main validates it once independently regardless of tree match.')
  if (result.changedPathsUnavailable) {
    lines.push(`- The merge's changed-file list could not be read (${escapeMarkdown(result.changedPathsUnavailable)}); treated as gate-defining and sent to fallback.`)
  }
  if (result.outcome === 'provenanced-match' && result.manifestsFound > 0) {
    lines.push('- Would skip under phase B: backend-api, backend-core, client, script-contracts, postgresql-smoke (lightweight cache warming would remain).')
  }
  result.markdown = lines.join('\n')

  mkdirSync(outputDir, { recursive: true })
  writeFileSync(join(outputDir, 'main-provenance.json'), `${JSON.stringify(result, null, 2)}\n`, 'utf8')
  writeFileSync(join(outputDir, 'main-provenance.md'), `${result.markdown}\n`, 'utf8')
  console.log(`[ci-metrics] Provenance check: ${result.outcome} (mode=${result.mode}).`)
}

main().catch((error) => {
  console.error(`[ci-metrics] Provenance check failed: ${error.message}`)
  process.exit(1)
})
