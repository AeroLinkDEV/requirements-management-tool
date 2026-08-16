// Tested-tree provenance logic for #562 (shadow observation plus fail-safe enforcement).
//
// The decision is deliberately conservative: a main push may be considered provenanced only when a
// validated-tree manifest exists for the exact tree, its gate evidence says every selected product job
// passed with zero missing, and the manifest's workflow revision is acceptable. Anything else requires
// the complete fallback gate. Phase A never skips the post-merge run; it only records what a later
// enforcement phase would do.

export const PROVENANCE_SCHEMA_VERSION = 'aerolink-validated-tree/v1'

/**
 * Evidence older than this is not trusted to authorize a skip.
 *
 * The manifest says a tree passed, not that it would pass today. Runner images, SDKs, browsers and
 * transitive dependencies drift underneath an unchanged tree, which is precisely the drift the weekly
 * full run exists to catch. A tree can also legitimately recur long after it was tested — a revert that
 * restores an earlier tree produces a byte-identical tree SHA and would otherwise match evidence of
 * any age. Thirty days matches the artifact retention the manifest is stored under, so this refuses
 * nothing that is still fetchable and does not depend on retention to enforce the policy.
 */
export const MAX_EVIDENCE_AGE_DAYS = 30

/**
 * Tolerance for a manifest dated slightly ahead of the decision clock. Runner and API clocks are not
 * identical; a few minutes of skew is ordinary. Anything beyond it is not a clock difference.
 */
export const MAX_CLOCK_SKEW_MINUTES = 10

/**
 * Paths whose contents define the gate itself, rather than the product the gate tests.
 *
 * A tree match proves the merged tree is the tested tree. It cannot prove the gate that tested it was
 * trustworthy, because the change under test may be the gate. When a merge edits these paths, its own
 * evidence was produced under the definition it is introducing, so it does not get to authorize
 * skipping the first independent run of that definition on main.
 */
export const GATE_DEFINING_PATHS = [
  // The gate itself: what runs, how it shards, and what counts as passing.
  '.github/workflows/ci.yml',
  // The trusted default-branch workflow that consumes the manifest and makes the skip decision. This was
  // written as `main-provenance.yml` in the first round — a file that does not exist — so the guard
  // protected nothing while appearing to protect the most important consumer in the set. Every path here
  // is now asserted to exist on disk by a test, because a guard keyed on a typo fails silently and looks
  // exactly like a guard that works.
  '.github/workflows/ci-main-provenance.yml',
  // The decision logic itself.
  'product/ci-metrics/lib/provenance.mjs',
  'product/ci-metrics/bin/check-main-provenance.mjs',
  // The producer of the manifest, and the artifact reader the consumer resolves it through.
  'product/ci-metrics/bin/write-validated-tree.mjs',
  'product/ci-metrics/lib/zip.mjs',
  // The evidence the manifest asserts — gate results and verified totals — is produced here and read by
  // write-validated-tree as run-metrics.json. A change that alters what those totals mean changes what a
  // passing manifest claims, without touching the decision code at all.
  'product/ci-metrics/lib/aggregate.mjs',
  'product/ci-metrics/bin/aggregate.mjs',
]

/** True when any changed path is one the gate's own trustworthiness depends on. */
export function touchesGateDefinition(changedPaths = []) {
  if (!Array.isArray(changedPaths)) return false
  return changedPaths.some((path) => typeof path === 'string' && GATE_DEFINING_PATHS.includes(path))
}

/** GitHub's files endpoint pages at 100; 30 pages covers the 3,000-file API maximum for a pull request. */
export const MAX_FILE_PAGES = 30
const FILES_PER_PAGE = 100

/**
 * Every path a merge touched, according to GitHub, as a complete list or not at all.
 *
 * `api` is injected rather than imported so this is testable without a network — the previous version
 * lived in the bin script and could only be exercised by running the real workflow, which meant its
 * pagination, its count reconciliation and its rename handling were all assertions in a comment rather
 * than tested behaviour.
 *
 * Every failure path throws. The caller treats a throw as gate-defining, so an unreadable or partial
 * answer produces the same conservative outcome as "yes, this merge changed the gate". That matters
 * more than it sounds: a partial list is indistinguishable from a list containing nothing interesting,
 * so silently returning one would look exactly like a clean result.
 *
 * Renames contribute both names. GitHub reports a rename with the destination in `filename` and the
 * origin in `previous_filename`; taking only the former would let "rename a guarded file away" read as
 * an ordinary change.
 */
export async function collectMergedPaths({ prNumber, api, maxPages = MAX_FILE_PAGES }) {
  if (typeof api !== 'function') throw new Error('collectMergedPaths requires an api function.')

  const meta = await api(`/pulls/${prNumber}`)
  if (!meta || typeof meta !== 'object' || Array.isArray(meta)) {
    throw new Error(`Pull request ${prNumber} metadata was not an object, so the changed-file count could not be verified.`)
  }
  if (!Number.isInteger(meta.changed_files) || meta.changed_files < 0) {
    // Without an authoritative count there is nothing to reconcile the enumeration against, so
    // completeness cannot be established and must not be assumed.
    throw new Error(`Pull request ${prNumber} reported no usable changed_files count, so list completeness cannot be verified.`)
  }
  const expected = meta.changed_files

  const files = []
  for (let page = 1; page <= maxPages; page += 1) {
    const batch = await api(`/pulls/${prNumber}/files?per_page=${FILES_PER_PAGE}&page=${page}`)
    if (!Array.isArray(batch)) {
      throw new Error(`Pull request ${prNumber} files page ${page} was not an array; refusing to treat a malformed response as the end of the list.`)
    }
    files.push(...batch)
    if (batch.length < FILES_PER_PAGE) break
    if (page === maxPages) {
      throw new Error(`Pull request ${prNumber} has more files than ${maxPages} pages can enumerate; the changed-path list would be incomplete.`)
    }
  }

  if (files.length !== expected) {
    throw new Error(`Pull request ${prNumber} reports ${expected} changed files but ${files.length} were enumerated; refusing to decide on a list that does not reconcile.`)
  }

  const paths = []
  for (const file of files) {
    if (!file || typeof file !== 'object' || typeof file.filename !== 'string') {
      throw new Error(`Pull request ${prNumber} returned a file entry with no usable filename; the changed-path list cannot be trusted.`)
    }
    paths.push(file.filename)
    if (typeof file.previous_filename === 'string' && file.previous_filename.length > 0) {
      paths.push(file.previous_filename)
    }
  }
  return paths
}

/**
 * Age verdict for one manifest. Returns a reason string when the evidence may not be used, or null.
 * `now` is passed in rather than read from the clock so the boundary is testable and the decision is
 * reproducible when replayed.
 */
export function evidenceAgeRejection(manifest, now) {
  const stamp = manifest?.validatedAt
  if (typeof stamp !== 'string') return 'Manifest has no validatedAt timestamp, so its evidence cannot be aged.'
  const validatedAt = Date.parse(stamp)
  if (!Number.isFinite(validatedAt)) return `Manifest validatedAt "${stamp.slice(0, 40)}" is not a parseable timestamp.`
  const reference = typeof now === 'number' ? now : Date.parse(now)
  if (!Number.isFinite(reference)) return 'The decision reference time is missing or malformed.'
  const ageMs = reference - validatedAt
  if (ageMs < -MAX_CLOCK_SKEW_MINUTES * 60 * 1000) {
    return `Manifest validatedAt ${stamp} is ahead of the decision time by more than ${MAX_CLOCK_SKEW_MINUTES} minutes.`
  }
  const maxAgeMs = MAX_EVIDENCE_AGE_DAYS * 24 * 60 * 60 * 1000
  if (ageMs > maxAgeMs) {
    const ageDays = Math.floor(ageMs / (24 * 60 * 60 * 1000))
    return `Manifest evidence is ${ageDays} days old, beyond the ${MAX_EVIDENCE_AGE_DAYS}-day limit.`
  }
  return null
}

export function validateManifest(manifest) {
  const errors = []
  if (manifest === null || typeof manifest !== 'object' || Array.isArray(manifest)) return ['Manifest is not an object.']
  if (manifest.schemaVersion !== PROVENANCE_SCHEMA_VERSION) errors.push(`Unsupported manifest schema "${manifest.schemaVersion ?? 'missing'}".`)
  const checkedOut = manifest.checkedOut
  if (!checkedOut || typeof checkedOut !== 'object') {
    errors.push('Manifest has no checkedOut identity.')
  } else {
    if (typeof checkedOut.commitSha !== 'string' || !/^[0-9a-f]{40}$/.test(checkedOut.commitSha)) errors.push('checkedOut.commitSha is invalid.')
    if (typeof checkedOut.treeSha !== 'string' || !/^[0-9a-f]{40}$/.test(checkedOut.treeSha)) errors.push('checkedOut.treeSha is invalid.')
  }
  if (!Number.isInteger(manifest.run?.id) || manifest.run.id < 1) errors.push('run.id is invalid.')
  if (!Number.isInteger(manifest.run?.attempt) || manifest.run.attempt < 1) errors.push('run.attempt is invalid.')
  if (typeof manifest.repository !== 'string' || manifest.repository.length > 200) errors.push('repository is invalid.')
  if (typeof manifest.provenance !== 'string' || manifest.provenance.length > 50) errors.push('provenance is invalid.')
  if (manifest.canAuthorizePostMergeSkip !== true && manifest.canAuthorizePostMergeSkip !== false) errors.push('canAuthorizePostMergeSkip must be boolean.')
  // The producer has always written validatedAt; nothing read it, so a manifest without a usable one
  // was accepted and then aged against nothing.
  if (typeof manifest.validatedAt !== 'string' || manifest.validatedAt.length > 40 || !Number.isFinite(Date.parse(manifest.validatedAt))) {
    errors.push('validatedAt must be a parseable ISO-8601 timestamp.')
  }
  const gates = manifest.gates
  if (!gates || typeof gates !== 'object') {
    errors.push('Manifest has no gates evidence.')
  } else {
    if (typeof gates.gatePassed !== 'boolean' || typeof gates.allSelectedPassed !== 'boolean') errors.push('Gate result flags must be boolean.')
  }
  const json = JSON.stringify(manifest)
  if (Buffer.byteLength(json, 'utf8') > 256 * 1024) errors.push('Manifest exceeds the bounded size.')
  return errors
}

export function deriveEligibility(manifest) {
  const reasons = []
  const gates = manifest?.gates
  if (!gates || typeof gates !== 'object') {
    reasons.push('Manifest has no gates evidence.')
  } else {
    if (gates.gatePassed !== true) reasons.push('The required gate did not pass.')
    if (gates.allSelectedPassed !== true) reasons.push('Not every selected gate passed.')
    if (!Array.isArray(gates.selected) || gates.selected.length === 0) reasons.push('No selected gates were recorded.')
    if (Array.isArray(gates.selected) && gates.selected.some((job) => !job || job.result !== 'success')) {
      reasons.push('A selected gate did not succeed.')
    }
    if (Array.isArray(gates.missing) && gates.missing.length > 0) reasons.push('Missing gate evidence is present.')
  }
  const totals = manifest?.verifiedTotals ?? {}
  for (const key of ['expected', 'executed', 'passed', 'failed', 'skipped']) {
    if (!Number.isInteger(totals[key]) || totals[key] < 0) reasons.push(`verifiedTotals.${key} is not a non-negative integer.`)
  }
  if (Number.isInteger(totals.expected) && Number.isInteger(totals.executed) && Number.isInteger(totals.skipped) &&
    totals.expected !== totals.executed + totals.skipped) {
    reasons.push('verifiedTotals are incoherent: expected must equal executed + skipped.')
  }
  if (Number.isInteger(totals.executed) && Number.isInteger(totals.passed) && Number.isInteger(totals.failed) &&
    totals.executed !== totals.passed + totals.failed) {
    reasons.push('verifiedTotals are incoherent: executed must equal passed + failed.')
  }
  return { eligible: reasons.length === 0, reasons }
}

export function bindManifest(manifest, {
  repository, workflow, runId, runAttempt, artifactAttempt, prNumber, expectedHeadSha, expectedBaseSha = null,
  expectedMergeRef, checkoutCommitTree = null,
}) {
  const fail = (reason) => ({ ok: false, reason })
  if (manifest.repository !== repository) return fail(`Manifest repository ${manifest.repository} does not match ${repository}.`)
  if (manifest.workflow !== workflow) return fail(`Manifest workflow ${manifest.workflow} does not match ${workflow}.`)
  const expectedWorkflowRef = `${repository}/.github/workflows/ci.yml@${expectedMergeRef}`
  if (manifest.workflowRef !== expectedWorkflowRef) {
    return fail(`Manifest workflowRef ${manifest.workflowRef} does not exactly match ${expectedWorkflowRef}.`)
  }
  if (manifest.run?.id !== runId) return fail(`Manifest run id ${manifest.run?.id} does not match the candidate run ${runId}.`)
  if (artifactAttempt !== runAttempt) return fail(`Artifact attempt ${artifactAttempt} does not match the authoritative candidate run attempt ${runAttempt}.`)
  if (manifest.run?.attempt !== runAttempt) return fail(`Manifest run attempt ${manifest.run?.attempt} does not match the artifact attempt ${runAttempt}.`)
  if (manifest.pullRequest?.number !== prNumber) return fail(`Manifest PR ${manifest.pullRequest?.number} does not match ${prNumber}.`)
  if (manifest.pullRequest?.headSha !== expectedHeadSha) return fail('Manifest PR head SHA does not match the candidate run head.')
  if (expectedBaseSha !== null && manifest.pullRequest?.baseSha !== expectedBaseSha) {
    return fail('Manifest PR base SHA does not match the merged PR base.')
  }
  if (manifest.checkedOut?.ref !== expectedMergeRef) return fail('Manifest checkout ref does not match the expected PR merge ref.')
  if (checkoutCommitTree !== null && checkoutCommitTree !== manifest.checkedOut?.treeSha) {
    return fail('The manifest checkout commit GitHub-side tree does not match the manifest tree.')
  }
  return { ok: true }
}

export function normalizeProvenanceTrigger({ event = {}, eventName = '', runId = '', sha = '' } = {}) {
  if (event?.workflow_run && typeof event.workflow_run === 'object') return event.workflow_run
  if (eventName !== 'push') return null
  const ref = typeof event?.ref === 'string' ? event.ref : ''
  const headBranch = ref.startsWith('refs/heads/') ? ref.slice('refs/heads/'.length) : null
  const numericRunId = Number(runId)
  return {
    id: Number.isInteger(numericRunId) && numericRunId > 0 ? numericRunId : null,
    event: 'push',
    head_branch: headBranch,
    head_sha: (typeof event?.after === 'string' && event.after.length > 0) ? event.after : (sha || null),
  }
}

export function applyProvenanceMode(decision, mode = 'shadow') {
  const normalized = mode === 'enforce' ? 'enforce' : 'shadow'
  return {
    ...decision,
    canSkip: normalized === 'enforce' && decision?.outcome === 'provenanced-match',
  }
}

export function decideProvenance({ pushTreeSha, mergedPr = null, manifests = [], now = null, changedPaths = [] }) {
  if (typeof pushTreeSha !== 'string' || !/^[0-9a-f]{40}$/.test(pushTreeSha)) {
    return { outcome: 'fallback-needed', canSkip: false, reason: 'The pushed main tree SHA is missing or malformed.' }
  }
  if (!mergedPr) {
    return { outcome: 'fallback-needed', canSkip: false, reason: 'No merged pull request was found for the pushed commit (direct push or unusual merge method).' }
  }
  // Checked before any manifest is examined: no manifest, however well-formed, can vouch for a gate
  // definition that this very merge is changing.
  if (touchesGateDefinition(changedPaths)) {
    const edited = changedPaths.filter((path) => GATE_DEFINING_PATHS.includes(path))
    return {
      outcome: 'fallback-needed',
      canSkip: false,
      reason: `This merge changes the gate's own definition (${edited.join(', ')}); its evidence was produced under the definition it introduces, so main runs the full gate once independently.`,
      selfModifying: true,
    }
  }
  // A decision with no reference time cannot age evidence, and unaged evidence is what this rule
  // exists to refuse. Fail closed rather than silently skipping the age check.
  if (now === null) {
    return { outcome: 'fallback-needed', canSkip: false, reason: 'No decision reference time was supplied, so manifest evidence could not be aged.' }
  }
  const valid = []
  const rejected = []
  for (const manifest of manifests) {
    const errors = validateManifest(manifest)
    if (errors.length > 0) {
      rejected.push({ run: manifest?.run?.id ?? '?', reason: `Manifest failed validation: ${errors.join('; ')}` })
      continue
    }
    if (manifest.checkedOut.treeSha !== pushTreeSha) {
      rejected.push({ run: manifest.run.id, reason: `Manifest tree ${manifest.checkedOut.treeSha} does not match the pushed main tree ${pushTreeSha}.` })
      continue
    }
    if (manifest.canAuthorizePostMergeSkip !== true) {
      rejected.push({ run: manifest.run.id, reason: 'Manifest does not authorize a post-merge skip (gate or totals incomplete).' })
      continue
    }
    const stale = evidenceAgeRejection(manifest, now)
    if (stale !== null) {
      rejected.push({ run: manifest.run.id, reason: stale })
      continue
    }
    const eligibility = deriveEligibility(manifest)
    if (!eligibility.eligible) {
      rejected.push({ run: manifest.run.id, reason: `Raw gate/count evidence is not eligible: ${eligibility.reasons.join('; ')}` })
      continue
    }
    valid.push(manifest)
  }
  if (valid.length === 0) {
    return {
      outcome: 'fallback-needed',
      canSkip: false,
      reason: rejected.length > 0 ? `No acceptable manifest: ${rejected[0].reason}` : 'No validated-tree manifest was found for the pushed tree.',
      rejected,
    }
  }
  const best = valid.sort((a, b) => b.run.id - a.run.id || b.run.attempt - a.run.attempt)[0]
  return {
    outcome: 'provenanced-match',
    canSkip: false, // Shadow phase: observation only; enforcement is phase B.
    reason: null,
    source: { pr: mergedPr.number, runId: best.run.id, attempt: best.run.attempt, treeSha: best.checkedOut.treeSha },
    rejected,
  }
}
