// Reports which open pull requests are touching the same ground as this one (#569).
//
// Runs early, before the expensive gates, so a collision can be settled while it is still cheap. It
// never blocks: it posts or updates a single comment and exits zero regardless of what it finds. A
// check that can fail a pull request for something another branch did would be its own hazard.
//
// Usage in CI:  GITHUB_TOKEN=... GITHUB_REPOSITORY=owner/repo PR_NUMBER=123 node check-overlap.mjs
// Locally:      node product/test-planner/tools/check-overlap.mjs --dry-run

import { detectOverlaps, renderComment } from '../lib/overlap.mjs'

const MARKER = '<!-- AEROLINK_PR_OVERLAP -->'
const MAX_PRS = 30
const MAX_FILE_PAGES = 10

const env = (name) => process.env[name] ?? ''
const dryRun = process.argv.includes('--dry-run')

async function api(path, { method = 'GET', body } = {}) {
  const response = await fetch(`${env('GITHUB_API_URL') || 'https://api.github.com'}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${env('GITHUB_TOKEN')}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    },
    body: body ? JSON.stringify(body) : undefined,
  })
  if (!response.ok) throw new Error(`GitHub API ${path} returned ${response.status}.`)
  return response.status === 204 ? null : response.json()
}

async function filesFor(repository, number) {
  const files = []
  for (let page = 1; page <= MAX_FILE_PAGES; page += 1) {
    const batch = await api(`/repos/${repository}/pulls/${number}/files?per_page=100&page=${page}`)
    if (!Array.isArray(batch) || batch.length === 0) break
    files.push(...batch.map((file) => file?.filename).filter((name) => typeof name === 'string'))
    if (batch.length < 100) break
  }
  return files
}

async function main() {
  const repository = env('GITHUB_REPOSITORY')
  const prNumber = Number(env('PR_NUMBER'))
  if (!repository || !env('GITHUB_TOKEN')) {
    console.error('GITHUB_REPOSITORY and GITHUB_TOKEN are required.')
    process.exit(2)
  }

  const open = await api(`/repos/${repository}/pulls?state=open&per_page=${MAX_PRS}`)
  const candidates = (Array.isArray(open) ? open : []).filter((pr) => pr && !pr.draft)
  if (candidates.length < 2) {
    console.log(`Only ${candidates.length} open non-draft pull request(s); nothing can overlap.`)
    return
  }

  const pullRequests = []
  for (const pr of candidates) {
    pullRequests.push({
      number: pr.number,
      title: pr.title,
      author: pr.user?.login ?? null,
      branch: pr.head?.ref ?? null,
      files: await filesFor(repository, pr.number),
    })
  }

  const overlaps = detectOverlaps(pullRequests)
  console.log(`Compared ${pullRequests.length} open pull requests; found ${overlaps.length} overlapping pair(s).`)
  for (const entry of overlaps) {
    console.log(`  ${entry.severity.padEnd(6)} #${entry.a.number} <-> #${entry.b.number}  files=${entry.sharedFiles.length} surfaces=${entry.sharedSurfaces.map((s) => s.key).join(',') || '-'}`)
  }

  if (!Number.isInteger(prNumber) || prNumber < 1) {
    console.log('No PR_NUMBER supplied, so no comment was posted.')
    return
  }

  const body = renderComment(prNumber, overlaps)
  const existing = (await api(`/repos/${repository}/issues/${prNumber}/comments?per_page=100`) ?? [])
    .find((comment) => typeof comment.body === 'string' && comment.body.includes(MARKER))

  if (body === null) {
    // Nothing to report. If a previous run posted a warning that no longer applies, correct it rather
    // than leaving a stale claim standing — the same failure the regression tracker had.
    if (existing) {
      const cleared = `${MARKER}\n\n## No open pull request currently overlaps this one\n\nAn earlier revision of this pull request overlapped with others; it no longer does. Regenerated on every push.`
      if (dryRun) console.log('[dry-run] would clear the existing overlap comment')
      else await api(`/repos/${repository}/issues/comments/${existing.id}`, { method: 'PATCH', body: { body: cleared } })
      console.log('Cleared a stale overlap comment.')
    } else {
      console.log('No overlaps involving this pull request; nothing posted.')
    }
    return
  }

  const payload = `${MARKER}\n\n${body}`
  if (dryRun) {
    console.log('[dry-run] would post:\n')
    console.log(payload)
    return
  }
  if (existing) {
    await api(`/repos/${repository}/issues/comments/${existing.id}`, { method: 'PATCH', body: { body: payload } })
    console.log(`Updated the overlap comment on #${prNumber}.`)
  } else {
    await api(`/repos/${repository}/issues/${prNumber}/comments`, { method: 'POST', body: { body: payload } })
    console.log(`Posted an overlap comment on #${prNumber}.`)
  }
}

main().catch((error) => {
  // Never fail the pull request for this. An advisory check that can redden a gate is a liability.
  console.error(`Overlap check could not complete: ${error.message}`)
  process.exit(0)
})
