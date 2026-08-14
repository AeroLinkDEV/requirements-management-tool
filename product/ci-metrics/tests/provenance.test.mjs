import { test } from 'node:test'
import assert from 'node:assert/strict'
import { validateManifest, decideProvenance, deriveEligibility, bindManifest } from '../lib/provenance.mjs'

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
    gates: { selected: [{ instance: 'gate', result: 'success' }], skipped: [], missing: [], gatePassed: true, allSelectedPassed: true },
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

test('canAuthorizePostMergeSkip cannot override contradictory raw gate evidence', () => {
  const base = manifest()
  const cases = [
    { name: 'gate-flags-false', mutate: (m) => { m.gates.gatePassed = false; m.gates.allSelectedPassed = false } },
    { name: 'failed-selected', mutate: (m) => { m.gates.selected = [{ instance: 'gate', result: 'failure' }] } },
    { name: 'missing-evidence', mutate: (m) => { m.gates.missing = [{ job: 'backend-api-1', reason: 'absent' }] } },
    { name: 'incoherent-totals', mutate: (m) => { m.verifiedTotals = { expected: 100, executed: 99, passed: 99, failed: 0, skipped: 0, flaky: 0 } } },
    { name: 'no-selected', mutate: (m) => { m.gates.selected = [] } },
  ]
  for (const entry of cases) {
    const crafted = base
    entry.mutate(crafted)
    const result = decideProvenance({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [crafted] })
    assert.equal(result.outcome, 'fallback-needed', entry.name)
    assert.match(result.reason, /eligible|incoherent|No selected|Missing gate/, entry.name)
    const eligibility = deriveEligibility(crafted)
    assert.equal(eligibility.eligible, false, entry.name)
  }
})

test('bindManifest rejects any identity mismatch against trusted API metadata', () => {
  const m = manifest()
  const context = {
    repository: 'owner/repo',
    workflow: 'Product quality gate',
    runId: 100,
    runAttempt: 1,
    artifactAttempt: 1,
    prNumber: 1,
    expectedHeadSha: 'b'.repeat(40),
    expectedBaseSha: 'a'.repeat(40),
    expectedMergeRef: 'refs/pull/1/merge',
    checkoutCommitTree: 'd'.repeat(40),
  }
  assert.equal(bindManifest(m, context).ok, true)
  assert.equal(bindManifest({ ...m, repository: 'other/repo' }, context).ok, false)
  assert.equal(bindManifest({ ...m, workflow: 'Other workflow' }, context).ok, false)
  assert.equal(bindManifest({ ...m, workflowRef: 'owner/repo/.github/workflows/other.yml@refs/pull/1/merge' }, context).ok, false)
  assert.equal(bindManifest({ ...m, workflowRef: 'owner/repo/.github/workflows/ci.yml@refs/pull/1/merge-untrusted-suffix' }, context).ok, false)
  assert.equal(bindManifest(m, { ...context, artifactAttempt: 77 }).ok, false)
  assert.equal(bindManifest({ ...m, run: { ...m.run, id: 999 } }, context).ok, false)
  assert.equal(bindManifest({ ...m, run: { ...m.run, attempt: 77 } }, context).ok, false)
  assert.equal(bindManifest({ ...m, pullRequest: { ...m.pullRequest, number: 999 } }, context).ok, false)
  assert.equal(bindManifest({ ...m, pullRequest: { ...m.pullRequest, headSha: 'x'.repeat(40) } }, context).ok, false)
  assert.equal(bindManifest({ ...m, pullRequest: { ...m.pullRequest, baseSha: 'y'.repeat(40) } }, context).ok, false)
  assert.equal(bindManifest({ ...m, checkedOut: { ...m.checkedOut, ref: 'refs/pull/2/merge' } }, context).ok, false)
  assert.equal(bindManifest({ ...m, checkedOut: { ...m.checkedOut, treeSha: 'e'.repeat(40) } }, context).ok, false)
})
