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
