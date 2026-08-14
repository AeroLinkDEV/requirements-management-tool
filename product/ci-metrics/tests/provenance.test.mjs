import { test } from 'node:test'
import assert from 'node:assert/strict'
import { validateManifest, decideProvenance } from '../lib/provenance.mjs'

function manifest(overrides = {}) {
  return {
    schemaVersion: 'aerolink-validated-tree/v1',
    provenance: 'shadow',
    repository: 'owner/repo',
    workflow: 'Product quality gate',
    workflowRef: 'owner/repo/.github/workflows/ci.yml@refs/pull/1/merge',
    run: { id: 100, attempt: 1, event: 'pull_request' },
    pullRequest: { number: 1, baseSha: 'a'.repeat(40), headSha: 'b'.repeat(40) },
    checkedOut: { commitSha: 'c'.repeat(40), treeSha: 'd'.repeat(40), ref: 'refs/pull/1/merge' },
    event: 'pull_request',
    classifications: {},
    gates: { selected: [], skipped: [], missing: [], gatePassed: true, allSelectedPassed: true },
    verifiedTotals: { expected: 100, executed: 100, passed: 100, failed: 0, skipped: 0, flaky: 0 },
    validatedAt: '2026-08-14T00:00:00Z',
    canAuthorizePostMergeSkip: true,
    ...overrides,
  }
}

test('validateManifest enforces the closed manifest contract', () => {
  assert.deepEqual(validateManifest(manifest()), [])
  assert.ok(validateManifest({ ...manifest(), schemaVersion: 'old' }).some((e) => /Unsupported/.test(e)))
  assert.ok(validateManifest({ ...manifest(), checkedOut: { ...manifest().checkedOut, treeSha: 'short' } }).some((e) => /treeSha/.test(e)))
  assert.ok(validateManifest({ ...manifest(), canAuthorizePostMergeSkip: 'yes' }).some((e) => /boolean/.test(e)))
  assert.ok(validateManifest(null).some((e) => /not an object/.test(e)))
})

test('decideProvenance requires an exact tree match and complete gate evidence', () => {
  const good = manifest()
  const match = decideProvenance({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [good] })
  assert.equal(match.outcome, 'provenanced-match')
  assert.equal(match.canSkip, false)
  assert.equal(match.source.runId, 100)

  const noPr = decideProvenance({ pushTreeSha: 'd'.repeat(40), mergedPr: null, manifests: [good] })
  assert.equal(noPr.outcome, 'fallback-needed')
  assert.match(noPr.reason, /No merged pull request/)

  const treeMismatch = decideProvenance({ pushTreeSha: 'e'.repeat(40), mergedPr: { number: 1 }, manifests: [good] })
  assert.equal(treeMismatch.outcome, 'fallback-needed')
  assert.match(treeMismatch.reason, /does not match the pushed main tree/)

  const notAuthorized = decideProvenance({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [{ ...good, canAuthorizePostMergeSkip: false }] })
  assert.equal(notAuthorized.outcome, 'fallback-needed')
  assert.match(notAuthorized.reason, /does not authorize/)

  const malformed = decideProvenance({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [{ ...good, checkedOut: { ...good.checkedOut, treeSha: 'x' } }] })
  assert.equal(malformed.outcome, 'fallback-needed')
  assert.match(malformed.reason, /validation/)

  assert.equal(decideProvenance({ pushTreeSha: 'bad', mergedPr: { number: 1 }, manifests: [] }).outcome, 'fallback-needed')
})

test('decideProvenance picks the newest acceptable manifest', () => {
  const older = manifest({ run: { id: 100, attempt: 1, event: 'pull_request' } })
  const newer = manifest({ run: { id: 200, attempt: 2, event: 'pull_request' } })
  const match = decideProvenance({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [older, newer] })
  assert.equal(match.source.runId, 200)
  assert.equal(match.source.attempt, 2)
})
