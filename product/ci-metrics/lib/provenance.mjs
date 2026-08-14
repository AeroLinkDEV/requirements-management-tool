// Tested-tree provenance logic for #562 (phase A: shadow observation).
//
// The decision is deliberately conservative: a main push may be considered provenanced only when a
// validated-tree manifest exists for the exact tree, its gate evidence says every selected product job
// passed with zero missing, and the manifest's workflow revision is acceptable. Anything else requires
// the complete fallback gate. Phase A never skips the post-merge run; it only records what a later
// enforcement phase would do.

export const PROVENANCE_SCHEMA_VERSION = 'aerolink-validated-tree/v1'

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

export function decideProvenance({ pushTreeSha, mergedPr = null, manifests = [] }) {
  if (typeof pushTreeSha !== 'string' || !/^[0-9a-f]{40}$/.test(pushTreeSha)) {
    return { outcome: 'fallback-needed', canSkip: false, reason: 'The pushed main tree SHA is missing or malformed.' }
  }
  if (!mergedPr) {
    return { outcome: 'fallback-needed', canSkip: false, reason: 'No merged pull request was found for the pushed commit (direct push or unusual merge method).' }
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
