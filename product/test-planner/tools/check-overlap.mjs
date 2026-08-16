// Trusted-base advisory checker for pull request #569.
//
// This module is intentionally API-only: it never checks out, imports, or executes pull-request-head
// code. The workflow runs it from the default branch under pull_request_target, where the write-capable
// token is available only to this trusted file. Every failure is represented as Unknown and the CLI exits
// zero because this is an advisory signal, never a merge gate.

import fs from 'node:fs/promises'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import {
  detectOverlaps,
  overlapsFor,
  renderClearComment,
  renderComment,
  renderUnknownComment,
  boundComment,
  normalizeFileList,
  normalizePath,
} from '../lib/overlap.mjs'

export const MARKER = '<!-- AEROLINK_PR_OVERLAP -->'
export const REPORT_VERSION = 1
const PAGE_SIZE = 100
const MAX_API_PAGES = 300
export const TRUSTED_MARKER_LOGINS = Object.freeze(['github-actions[bot]'])
export const OVERLAP_LIMITS = Object.freeze({
  maxOpenPullRequests: 100,
  maxEligiblePullRequests: 30,
  maxFilesPerPullRequest: 1_000,
  maxPathLength: 4_096,
  maxCommentsPerTarget: 1_000,
  maxCommentBodyLength: 100_000,
  maxAnalysisPairs: 435,
  maxAnalysisFiles: 30_000,
  maxReportItems: 100,
  maxReportErrors: 25,
})
const MAX_REPORT_ITEMS = OVERLAP_LIMITS.maxReportItems
const MAX_REPORT_ERRORS = OVERLAP_LIMITS.maxReportErrors
const SHA_PATTERN = /^[0-9a-f]{40,64}$/i
const FILE_STATUSES = new Set(['added', 'modified', 'deleted', 'renamed', 'copied', 'changed'])
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g

const env = (name) => process.env[name] ?? ''
const asNumber = (value) => {
  const number = Number(value)
  return Number.isSafeInteger(number) && number > 0 ? number : null
}

function headerValue(headers, name) {
  if (!headers) return ''
  if (typeof headers.get === 'function') return headers.get(name) || headers.get(name.toLowerCase()) || ''
  return headers[name] || headers[name.toLowerCase()] || headers[name.toUpperCase()] || ''
}

function nextLink(headers) {
  const link = headerValue(headers, 'link')
  const match = /<([^>]+)>\s*;\s*rel="?next"?/i.exec(link)
  return match?.[1] || null
}

function withPage(path, page, baseUrl) {
  const url = new URL(path, baseUrl)
  url.searchParams.set('per_page', String(PAGE_SIZE))
  url.searchParams.set('page', String(page))
  return url.toString()
}

/** Small injected GitHub REST client. `fetchImpl` is injected in tests for deterministic API mocks. */
export function createGithubApi({ baseUrl = 'https://api.github.com', token = '', fetchImpl = globalThis.fetch } = {}) {
  if (typeof fetchImpl !== 'function') throw new Error('A fetch implementation is required.')
  const origin = baseUrl.replace(/\/$/, '')
  let configuredOrigin
  try {
    configuredOrigin = new URL(origin).origin
  } catch {
    throw new Error('A valid GitHub API base URL is required.')
  }
  const trustedUrl = (path) => {
    let url
    try {
      url = new URL(path, origin)
    } catch {
      throw new Error('GitHub API URL is invalid.')
    }
    if (url.origin !== configuredOrigin) throw new Error(`GitHub API URL crossed the configured origin: ${url.origin}`)
    return url.toString()
  }
  const request = async (path, { method = 'GET', body } = {}) => {
    const url = trustedUrl(path)
    const response = await fetchImpl(url, {
      method,
      headers: {
        Authorization: token ? `Bearer ${token}` : undefined,
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
    })
    if (!response.ok && Number(response.status) >= 400) throw new Error(`GitHub API ${url} returned ${response.status}.`)
    const data = Number(response.status) === 204 ? null : await response.json()
    return { data, headers: response.headers, status: response.status }
  }

  const paginate = async (path, { maxItems = OVERLAP_LIMITS.maxAnalysisFiles } = {}) => {
    if (!Number.isSafeInteger(maxItems) || maxItems < 1) throw new Error(`Invalid pagination bound for ${path}.`)
    const items = []
    let page = 1
    let next = withPage(path, page, origin)
    while (next) {
      if (page > MAX_API_PAGES) throw new Error(`GitHub API pagination exceeded ${MAX_API_PAGES} pages for ${path}.`)
      const response = await request(next)
      if (!Array.isArray(response.data)) throw new Error(`GitHub API returned a non-list for ${path}.`)
      if (items.length + response.data.length > maxItems) throw new Error(`GitHub API response exceeded the bounded limit of ${maxItems} items for ${path}.`)
      items.push(...response.data)
      const linked = nextLink(response.headers)
      if (linked) {
        // Link headers are response-controlled. Validate before request() can attach the bearer token.
        next = trustedUrl(linked)
        page += 1
      } else if (response.data.length === PAGE_SIZE) {
        page += 1
        next = withPage(path, page, origin)
      } else {
        next = null
      }
    }
    return items
  }

  return { request, paginate }
}

export const filesFor = (api, repository, number) => api.paginate(`/repos/${repository}/pulls/${number}/files`, { maxItems: OVERLAP_LIMITS.maxFilesPerPullRequest })
export const commentsFor = (api, repository, number) => api.paginate(`/repos/${repository}/issues/${number}/comments`, { maxItems: OVERLAP_LIMITS.maxCommentsPerTarget })

function boundedText(value, max = 500) {
  return String(value ?? '').replace(CONTROL_CHARACTERS, ' ').replace(/\s+/g, ' ').trim().slice(0, max)
}

function requiredText(value, label) {
  if (typeof value !== 'string' || value.trim() === '') throw new Error(`${label} is missing.`)
  return value
}

/** Reject incomplete GitHub identity records rather than allowing them to become a clean result. */
export function validatePullRequest(pr, context = 'pull request') {
  if (!pr || typeof pr !== 'object') throw new Error(`${context} is not an object.`)
  const number = asNumber(pr.number)
  if (!number) throw new Error(`${context} number is invalid.`)
  requiredText(pr.title, `${context} #${number} title`)
  requiredText(pr.user?.login, `${context} #${number} author`)
  requiredText(pr.head?.ref, `${context} #${number} head branch`)
  requiredText(pr.base?.ref, `${context} #${number} base branch`)
  if (typeof pr.head?.sha !== 'string' || !SHA_PATTERN.test(pr.head.sha)) throw new Error(`${context} #${number} head SHA is invalid.`)
  if (typeof pr.base?.sha !== 'string' || !SHA_PATTERN.test(pr.base.sha)) throw new Error(`${context} #${number} base SHA is invalid.`)
  if (typeof pr.draft !== 'boolean') throw new Error(`${context} #${number} draft state is invalid.`)
  return pr
}

/** Validate every file object returned by GitHub, including rename evidence. */
export function validateFileList(files, context = 'pull request files') {
  if (!Array.isArray(files)) throw new Error(`${context} is not a list.`)
  if (files.length > OVERLAP_LIMITS.maxFilesPerPullRequest) throw new Error(`${context} exceeded ${OVERLAP_LIMITS.maxFilesPerPullRequest} files.`)
  return files.map((file, index) => {
    if (!file || typeof file !== 'object') throw new Error(`${context}[${index}] is malformed.`)
    if (typeof file.filename !== 'string' || file.filename.trim() === '') throw new Error(`${context}[${index}] filename is missing.`)
    if (file.filename.length > OVERLAP_LIMITS.maxPathLength) throw new Error(`${context}[${index}] filename exceeds ${OVERLAP_LIMITS.maxPathLength} characters.`)
    if (!normalizePath(file.filename)) throw new Error(`${context}[${index}] filename is not a usable repository path.`)
    if (typeof file.status !== 'string' || !FILE_STATUSES.has(file.status)) throw new Error(`${context}[${index}] status is invalid.`)
    if (file.previous_filename !== undefined && (typeof file.previous_filename !== 'string' || file.previous_filename.trim() === '')) throw new Error(`${context}[${index}] previous filename is invalid.`)
    if (typeof file.previous_filename === 'string' && file.previous_filename.length > OVERLAP_LIMITS.maxPathLength) throw new Error(`${context}[${index}] previous filename exceeds ${OVERLAP_LIMITS.maxPathLength} characters.`)
    if (file.previous_filename !== undefined && !normalizePath(file.previous_filename)) throw new Error(`${context}[${index}] previous filename is not a usable repository path.`)
    if ((file.status === 'renamed' || file.status === 'copied') && (typeof file.previous_filename !== 'string' || file.previous_filename.trim() === '')) throw new Error(`${context}[${index}] rename source is missing.`)
    return file
  })
}

export function validateCommentList(comments, context = 'issue comments') {
  if (!Array.isArray(comments)) throw new Error(`${context} is not a list.`)
  if (comments.length > OVERLAP_LIMITS.maxCommentsPerTarget) throw new Error(`${context} exceeded ${OVERLAP_LIMITS.maxCommentsPerTarget} comments.`)
  return comments.map((comment, index) => {
    const id = Number(comment?.id)
    if (!comment || typeof comment !== 'object' || !Number.isSafeInteger(id) || id < 1) throw new Error(`${context}[${index}] id is invalid.`)
    if (typeof comment.body !== 'string') throw new Error(`${context}[${index}] body is invalid.`)
    if (comment.body.length > OVERLAP_LIMITS.maxCommentBodyLength) throw new Error(`${context}[${index}] body exceeds ${OVERLAP_LIMITS.maxCommentBodyLength} characters.`)
    if (typeof comment.user?.login !== 'string' || comment.user.login.trim() === '' || typeof comment.user?.type !== 'string' || comment.user.type.trim() === '') throw new Error(`${context}[${index}] author is incomplete.`)
    return comment
  })
}

export function findMarkerComments(comments) {
  return (Array.isArray(comments) ? comments : [])
    .filter((comment) => {
      const id = Number(comment?.id)
      return Number.isSafeInteger(id) && id > 0
        && typeof comment?.body === 'string'
        && comment.body.includes(MARKER)
        && TRUSTED_MARKER_LOGINS.includes(comment.user?.login)
        && comment.user?.type === 'Bot'
    })
    .sort((a, b) => Number(a.id) - Number(b.id))
}

function pullRequestRecord(pr, files = []) {
  const number = asNumber(pr?.number)
  if (!number) return null
  return {
    number,
    title: typeof pr.title === 'string' ? pr.title : null,
    author: typeof pr.user?.login === 'string' ? pr.user.login : null,
    branch: typeof pr.head?.ref === 'string' ? pr.head.ref : null,
    headSha: typeof pr.head?.sha === 'string' ? pr.head.sha : null,
    baseSha: typeof pr.base?.sha === 'string' ? pr.base.sha : null,
    draft: Boolean(pr.draft),
    files: normalizeFileList(files),
  }
}

function mergeCurrent(open, current, action) {
  const rows = new Map(open.map((pr) => [asNumber(pr.number), pr]))
  const number = asNumber(current?.number)
  if (!number || action === 'closed') return [...rows.values()]
  const existing = rows.get(number)
  rows.set(number, { ...(existing || {}), ...current, number, draft: Boolean(current.draft) })
  return [...rows.values()]
}

function reportError(error) {
  return boundedText(error?.message || error || 'Unknown API failure')
}

function summarizeOverlap(entry) {
  const boundedPath = (value) => boundedText(value, 240)
  const identity = (value) => ({
    number: value.number,
    title: boundedText(value.title, 240),
    author: boundedText(value.author, 120),
    branch: boundedText(value.branch, 240),
    headSha: value.headSha || 'Unknown',
    baseSha: value.baseSha || 'Unknown',
  })
  return {
    severity: entry.severity,
    a: identity(entry.a),
    b: identity(entry.b),
    sharedFiles: entry.sharedFiles.slice(0, 20).map(boundedPath),
    sharedSurfaces: entry.sharedSurfaces.slice(0, 10).map((surface) => ({ key: surface.key, label: boundedText(surface.label, 240), aPaths: surface.aPaths.slice(0, 4).map(boundedPath), bPaths: surface.bPaths.slice(0, 4).map(boundedPath) })),
  }
}

export function buildReport({ repository = 'Unknown', action = 'unknown', currentPr = null, analysisTimestamp = 'Unknown', openCount = 0, eligibleCount = 0, records = [], overlaps = [], errors = [], updates = [], analysisComplete } = {}) {
  const boundedErrors = errors.slice(0, MAX_REPORT_ERRORS).map(reportError)
  const complete = analysisComplete === undefined ? boundedErrors.length === 0 : Boolean(analysisComplete) && boundedErrors.length === 0
  const status = !complete ? 'Unknown' : overlaps.length > 0 ? 'Overlap' : 'Clear'
  const identity = currentPr ? {
    number: currentPr.number,
    title: boundedText(currentPr.title, 240),
    author: boundedText(currentPr.author, 120),
    branch: boundedText(currentPr.branch, 240),
    headSha: currentPr.headSha || 'Unknown',
    baseSha: currentPr.baseSha || 'Unknown',
  } : null
  return {
    version: REPORT_VERSION,
    status,
    repository: boundedText(repository, 240),
    action: boundedText(action, 80),
    analysisTimestamp: boundedText(analysisTimestamp, 80),
    analysisComplete: complete,
    limits: OVERLAP_LIMITS,
    currentPr: identity,
    openPullRequests: Math.min(openCount, OVERLAP_LIMITS.maxOpenPullRequests),
    eligiblePullRequests: Math.min(eligibleCount, OVERLAP_LIMITS.maxEligiblePullRequests),
    peerHeads: records.map((record) => ({ number: record.number, title: boundedText(record.title, 240), author: boundedText(record.author, 120), branch: boundedText(record.branch, 240), headSha: record.headSha || 'Unknown', baseSha: record.baseSha || 'Unknown' })).slice(0, MAX_REPORT_ITEMS),
    overlaps: overlaps.slice(0, MAX_REPORT_ITEMS).map(summarizeOverlap),
    updates: updates.slice(0, MAX_REPORT_ITEMS).map((update) => ({ number: update.number, action: boundedText(update.action, 40), reason: boundedText(update.reason, 500), duplicates: Number.isSafeInteger(update.duplicates) ? update.duplicates : 0 })),
    errors: boundedErrors,
  }
}

async function reconcileComment(api, repository, target, comments, body, { dryRun = false } = {}) {
  const markers = findMarkerComments(comments)
  const payload = body === null ? null : boundComment(`${MARKER}\n\n${body}`)
  if (dryRun) return { number: target.number, action: payload ? markers.length ? 'would-update' : 'would-post' : 'none', duplicates: Math.max(0, markers.length - 1) }
  if (markers.length > 0) {
    const primary = markers[0]
    if (payload && primary.body !== payload) await api.request(`/repos/${repository}/issues/comments/${primary.id}`, { method: 'PATCH', body: { body: payload } })
    for (const duplicate of markers.slice(1)) await api.request(`/repos/${repository}/issues/comments/${duplicate.id}`, { method: 'DELETE' })
    return { number: target.number, action: payload ? 'updated' : 'deleted', duplicates: Math.max(0, markers.length - 1) }
  }
  if (!payload) return { number: target.number, action: 'none', duplicates: 0 }
  await api.request(`/repos/${repository}/issues/${target.number}/comments`, { method: 'POST', body: { body: payload } })
  return { number: target.number, action: 'posted', duplicates: 0 }
}

/**
 * Fetch all open PRs, all changed-file pages and all issue-comment pages, then refresh every open
 * peer's marker comment. The event PR is also refreshed when it is closed so its old warning is cleared.
 */
export async function runOverlapCheck({ repository, event = {}, api, analysisTimestamp = new Date().toISOString(), dryRun = false } = {}) {
  if (!repository || !api) throw new Error('repository and api are required.')
  const action = typeof event.action === 'string' && event.action ? event.action : 'manual'
  const eventPr = event.pull_request || null
  const lifecyclePr = action === 'converted_to_draft' && eventPr
    ? { ...eventPr, draft: true }
    : action === 'ready_for_review' && eventPr
      ? { ...eventPr, draft: false }
      : eventPr
  const currentNumber = asNumber(eventPr?.number)
  const errors = []
  let validatedEvent = null
  if (eventPr) {
    try {
      validatedEvent = validatePullRequest(lifecyclePr, 'event pull request')
    } catch (error) {
      errors.push(error)
    }
  }

  let openRaw
  let openFetchFailed = false
  try {
    openRaw = await api.paginate(`/repos/${repository}/pulls?state=open&sort=updated&direction=desc`, { maxItems: OVERLAP_LIMITS.maxOpenPullRequests })
    if (!Array.isArray(openRaw)) throw new Error('GitHub open pull-request response is not a list.')
    if (openRaw.length > OVERLAP_LIMITS.maxOpenPullRequests) throw new Error(`Open pull-request response exceeded ${OVERLAP_LIMITS.maxOpenPullRequests} items.`)
  } catch (error) {
    openFetchFailed = true
    errors.push(new Error(`Open pull requests: ${reportError(error)}`))
    openRaw = []
  }

  const validatedOpen = []
  const targetOpenByNumber = new Map()
  if (!openFetchFailed) {
    const seenNumbers = new Set()
    for (const [index, pr] of openRaw.entries()) {
      const boundedNumber = asNumber(pr?.number)
      // Keep only the bounded number for reconciliation when identity metadata is malformed. The
      // malformed metadata never enters analysis or comment text, but an existing trusted marker
      // must still be changed to Unknown rather than left stale.
      if (boundedNumber && !targetOpenByNumber.has(boundedNumber)) targetOpenByNumber.set(boundedNumber, { number: boundedNumber })
      try {
        const validated = validatePullRequest(pr, `open pull request ${index + 1}`)
        const number = asNumber(validated.number)
        if (seenNumbers.has(number)) throw new Error(`open pull request #${number} is duplicated in the API response.`)
        seenNumbers.add(number)
        validatedOpen.push(validated)
        targetOpenByNumber.set(number, validated)
      } catch (error) {
        errors.push(error)
      }
    }
  }
  let open = mergeCurrent(validatedOpen, validatedEvent, action)
  if (action === 'closed' && currentNumber) open = open.filter((pr) => asNumber(pr.number) !== currentNumber)
  const eligibleRaw = open.filter((pr) => !pr.draft)
  if (eligibleRaw.length > OVERLAP_LIMITS.maxEligiblePullRequests) errors.push(new Error(`Eligible pull-request count exceeded ${OVERLAP_LIMITS.maxEligiblePullRequests}; analysis was not truncated.`))
  const records = []
  if (errors.length === 0) {
    for (const pr of eligibleRaw) {
      try {
        const apiFiles = await filesFor(api, repository, pr.number)
        const validatedFiles = validateFileList(apiFiles, `PR #${pr.number} files`)
        records.push(pullRequestRecord(pr, validatedFiles))
      } catch (error) {
        errors.push(new Error(`PR #${pr.number} files: ${reportError(error)}`))
      }
    }
  }
  const validRecords = records.filter(Boolean)
  const analyzedFileCount = validRecords.reduce((count, record) => count + record.files.length, 0)
  if (analyzedFileCount > OVERLAP_LIMITS.maxAnalysisFiles) errors.push(new Error(`Analysis file count exceeded ${OVERLAP_LIMITS.maxAnalysisFiles}; analysis was not truncated.`))
  const pairCount = (validRecords.length * Math.max(0, validRecords.length - 1)) / 2
  if (pairCount > OVERLAP_LIMITS.maxAnalysisPairs) errors.push(new Error(`Analysis pair count exceeded ${OVERLAP_LIMITS.maxAnalysisPairs}; analysis was not truncated.`))
  const analysisComplete = errors.length === 0
  const overlaps = analysisComplete ? detectOverlaps(validRecords) : []
  const peerHeads = validRecords.map((record) => record.headSha).filter(Boolean)
  if (eventPr && currentNumber) {
    const eventTarget = validatedEvent || { number: currentNumber }
    if (action === 'closed' || !targetOpenByNumber.has(currentNumber) || validatedEvent) targetOpenByNumber.set(currentNumber, eventTarget)
  }
  const targetByNumber = targetOpenByNumber
  const updates = []

  // Fetch all comment lists before rendering any clear result. A later comment/API failure must
  // make every result Unknown; otherwise an earlier target could be incorrectly cleared.
  const commentsByNumber = new Map()
  for (const target of targetByNumber.values()) {
    const number = asNumber(target.number)
    if (!number) continue
    try {
      commentsByNumber.set(number, validateCommentList(await commentsFor(api, repository, number), `PR #${number} comments`))
    } catch (error) {
      const reason = reportError(error)
      errors.push(new Error(`PR #${number} comments: ${reason}`))
      commentsByNumber.set(number, null)
    }
  }

  const safeAnalysis = analysisComplete && errors.length === 0
  for (const target of targetByNumber.values()) {
    const number = asNumber(target.number)
    if (!number) continue
    const comments = commentsByNumber.get(number)
    if (!Array.isArray(comments)) {
      updates.push({ number, action: 'unknown', reason: 'Comment list was unavailable; no marker was changed.' })
      continue
    }
    const markerExists = findMarkerComments(comments).length > 0
    let body = null
    if (!safeAnalysis) {
      body = renderUnknownComment({ analysisTimestamp, currentSha: target.head?.sha, reason: errors[0], action })
    } else if (!target.draft && !(action === 'closed' && number === currentNumber)) {
      const mine = overlapsFor(number, overlaps)
      body = mine.length > 0
        ? renderComment(number, overlaps, { analysisTimestamp, currentSha: target.head?.sha })
        : markerExists
          ? renderClearComment({ analysisTimestamp, currentSha: target.head?.sha, action, peerHeads })
          : null
    } else if (markerExists) {
      body = renderClearComment({ analysisTimestamp, currentSha: target.head?.sha, action, reason: target.draft ? 'Draft pull requests are not compared' : 'Pull request is closed', peerHeads })
    }
    try {
      updates.push(await reconcileComment(api, repository, { number }, comments, body, { dryRun }))
    } catch (error) {
      errors.push(new Error(`PR #${number} comment update: ${reportError(error)}`))
      updates.push({ number, action: 'unknown', reason: reportError(error) })
    }
  }
  return buildReport({ repository, action, currentPr: validatedEvent ? pullRequestRecord(validatedEvent) : null, analysisTimestamp, openCount: open.length, eligibleCount: eligibleRaw.length, records: validRecords, overlaps, errors, updates, analysisComplete: safeAnalysis })
}

async function readEvent() {
  const eventPath = env('GITHUB_EVENT_PATH')
  if (!eventPath) return {}
  try { return JSON.parse(await fs.readFile(eventPath, 'utf8')) } catch (error) { throw new Error(`Cannot read GitHub event: ${reportError(error)}`) }
}

async function writeReport(path, report) {
  if (!path) return
  await fs.writeFile(resolve(path), `${JSON.stringify(report, null, 2)}\n`, 'utf8')
}

export async function main() {
  const repository = env('GITHUB_REPOSITORY')
  const outputPath = env('OVERLAP_JSON_OUTPUT')
  const timestamp = env('OVERLAP_ANALYSIS_TIMESTAMP') || new Date().toISOString()
  let report
  try {
    if (!repository || !env('GITHUB_TOKEN')) throw new Error('GITHUB_REPOSITORY and GITHUB_TOKEN are required.')
    const event = await readEvent()
    const api = createGithubApi({ baseUrl: env('GITHUB_API_URL') || 'https://api.github.com', token: env('GITHUB_TOKEN') })
    report = await runOverlapCheck({ repository, event, api, analysisTimestamp: timestamp, dryRun: process.argv.includes('--dry-run') })
    console.log(JSON.stringify(report))
  } catch (error) {
    report = buildReport({ repository: repository || 'Unknown', analysisTimestamp: timestamp, errors: [error] })
    console.error(`Overlap check status: Unknown — ${report.errors[0]}`)
  }
  await writeReport(outputPath, report).catch((error) => console.error(`Could not write overlap JSON: ${reportError(error)}`))
  return report
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().then(() => process.exit(0)).catch((error) => { console.error(`Overlap checker failed safely: ${reportError(error)}`); process.exit(0) })
}
