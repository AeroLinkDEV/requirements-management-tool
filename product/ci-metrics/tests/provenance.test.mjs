import { test } from 'node:test'
import assert from 'node:assert/strict'
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  validateManifest, decideProvenance, deriveEligibility, bindManifest,
  evidenceAgeRejection, touchesGateDefinition,
  MAX_EVIDENCE_AGE_DAYS, MAX_CLOCK_SKEW_MINUTES, GATE_DEFINING_PATHS,
} from '../lib/provenance.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))

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

/**
 * A reference time six hours after the fixture's validatedAt, and a changed path that is not part of
 * the gate's definition. The cases below predate the evidence-age and self-modification rules and are
 * about the tree/gate contract, so they hold both new variables constant rather than exercising them.
 */
const NOW = Date.parse('2026-08-14T06:00:00Z')
const UNRELATED_PATH = 'product/src/AeroLink.Api/Program.cs'
const decide = (input) => decideProvenance({ now: NOW, changedPaths: [UNRELATED_PATH], ...input })

test('validateManifest enforces the closed manifest contract', () => {
  assert.deepEqual(validateManifest(manifest()), [])
  assert.ok(validateManifest({ ...manifest(), schemaVersion: 'old' }).some((e) => /Unsupported/.test(e)))
  assert.ok(validateManifest({ ...manifest(), checkedOut: { ...manifest().checkedOut, treeSha: 'short' } }).some((e) => /treeSha/.test(e)))
  assert.ok(validateManifest({ ...manifest(), canAuthorizePostMergeSkip: 'yes' }).some((e) => /boolean/.test(e)))
  assert.ok(validateManifest(null).some((e) => /not an object/.test(e)))
})

test('decideProvenance requires an exact tree match and complete gate evidence', () => {
  const good = manifest()
  const match = decide({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [good] })
  assert.equal(match.outcome, 'provenanced-match')
  assert.equal(match.canSkip, false)
  assert.equal(match.source.runId, 100)

  const noPr = decide({ pushTreeSha: 'd'.repeat(40), mergedPr: null, manifests: [good] })
  assert.equal(noPr.outcome, 'fallback-needed')
  assert.match(noPr.reason, /No merged pull request/)

  const treeMismatch = decide({ pushTreeSha: 'e'.repeat(40), mergedPr: { number: 1 }, manifests: [good] })
  assert.equal(treeMismatch.outcome, 'fallback-needed')
  assert.match(treeMismatch.reason, /does not match the pushed main tree/)

  const notAuthorized = decide({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [{ ...good, canAuthorizePostMergeSkip: false }] })
  assert.equal(notAuthorized.outcome, 'fallback-needed')
  assert.match(notAuthorized.reason, /does not authorize/)

  const malformed = decide({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [{ ...good, checkedOut: { ...good.checkedOut, treeSha: 'x' } }] })
  assert.equal(malformed.outcome, 'fallback-needed')
  assert.match(malformed.reason, /validation/)

  assert.equal(decide({ pushTreeSha: 'bad', mergedPr: { number: 1 }, manifests: [] }).outcome, 'fallback-needed')
})

test('decideProvenance picks the newest acceptable manifest', () => {
  const older = manifest({ run: { id: 100, attempt: 1, event: 'pull_request' } })
  const newer = manifest({ run: { id: 200, attempt: 2, event: 'pull_request' } })
  const match = decide({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [older, newer] })
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
    const result = decide({ pushTreeSha: 'd'.repeat(40), mergedPr: { number: 1 }, manifests: [crafted] })
    assert.equal(result.outcome, 'fallback-needed', entry.name)
    assert.match(result.reason, /eligible|incoherent|No selected|Missing gate/, entry.name)
    const eligibility = deriveEligibility(crafted)
    assert.equal(eligibility.eligible, false, entry.name)
  }
})

const DAY_MS = 24 * 60 * 60 * 1000
const validatedAt = Date.parse('2026-08-14T00:00:00Z')

test('evidence stops authorizing a skip once it is older than the retention limit', () => {
  const fresh = manifest()
  // Exactly at the limit is still inside it; the rule rejects evidence *older* than 30 days.
  const atLimit = decide({
    pushTreeSha: 'd'.repeat(40),
    mergedPr: { number: 1 },
    manifests: [fresh],
    now: validatedAt + MAX_EVIDENCE_AGE_DAYS * DAY_MS,
  })
  assert.equal(atLimit.outcome, 'provenanced-match')

  const justPast = decide({
    pushTreeSha: 'd'.repeat(40),
    mergedPr: { number: 1 },
    manifests: [fresh],
    now: validatedAt + MAX_EVIDENCE_AGE_DAYS * DAY_MS + 1,
  })
  assert.equal(justPast.outcome, 'fallback-needed')
  assert.match(justPast.reason, /30 days old|beyond the 30-day limit/)
})

test('a revert that restores an old tree does not inherit that tree\'s old evidence', () => {
  // The tree SHA matches exactly, the gate evidence is perfect, and every other rule passes. Only the
  // age of the evidence stands between this and a skipped post-merge gate — which is the point: the
  // environment underneath an unchanged tree has had three months to drift.
  const old = manifest()
  const result = decide({
    pushTreeSha: 'd'.repeat(40),
    mergedPr: { number: 1 },
    manifests: [old],
    now: validatedAt + 90 * DAY_MS,
  })
  assert.equal(result.outcome, 'fallback-needed')
  assert.equal(result.canSkip, false)
  assert.match(result.reason, /90 days old/)
})

test('clock skew is tolerated but a manifest genuinely from the future is not', () => {
  const ahead = manifest()
  const withinSkew = decide({
    pushTreeSha: 'd'.repeat(40),
    mergedPr: { number: 1 },
    manifests: [ahead],
    now: validatedAt - MAX_CLOCK_SKEW_MINUTES * 60 * 1000,
  })
  assert.equal(withinSkew.outcome, 'provenanced-match')

  const beyondSkew = decide({
    pushTreeSha: 'd'.repeat(40),
    mergedPr: { number: 1 },
    manifests: [ahead],
    now: validatedAt - MAX_CLOCK_SKEW_MINUTES * 60 * 1000 - 1,
  })
  assert.equal(beyondSkew.outcome, 'fallback-needed')
  assert.match(beyondSkew.reason, /ahead of the decision time/)
})

test('unusable or absent timestamps are refused rather than treated as age zero', () => {
  assert.ok(validateManifest({ ...manifest(), validatedAt: undefined }).some((e) => /validatedAt/.test(e)))
  assert.ok(validateManifest({ ...manifest(), validatedAt: 'sometime last week' }).some((e) => /validatedAt/.test(e)))
  assert.ok(validateManifest({ ...manifest(), validatedAt: 'x'.repeat(60) }).some((e) => /validatedAt/.test(e)))

  assert.equal(evidenceAgeRejection({ validatedAt: '2026-08-14T00:00:00Z' }, NaN), 'The decision reference time is missing or malformed.')
  assert.match(evidenceAgeRejection({}, NOW), /no validatedAt timestamp/)

  // No reference time at all must fail closed, not skip the age rule.
  const noNow = decideProvenance({
    pushTreeSha: 'd'.repeat(40),
    mergedPr: { number: 1 },
    manifests: [manifest()],
    changedPaths: [UNRELATED_PATH],
  })
  assert.equal(noNow.outcome, 'fallback-needed')
  assert.match(noNow.reason, /reference time/)
})

test('a merge that edits the gate\'s own definition cannot authorize skipping it', () => {
  for (const path of GATE_DEFINING_PATHS) {
    // Perfect, fresh, exactly-matching evidence — and it still falls back, because that evidence was
    // produced by the very gate definition this merge introduces.
    const result = decide({
      pushTreeSha: 'd'.repeat(40),
      mergedPr: { number: 1 },
      manifests: [manifest()],
      changedPaths: [UNRELATED_PATH, path],
    })
    assert.equal(result.outcome, 'fallback-needed', path)
    assert.equal(result.canSkip, false, path)
    assert.equal(result.selfModifying, true, path)
    assert.match(result.reason, new RegExp(path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')), path)
  }
})

test('every gate-defining path names a file that exists', () => {
  // Round 1 listed `.github/workflows/main-provenance.yml`, which does not exist — the real consumer is
  // `ci-main-provenance.yml`. The guard therefore protected nothing for the most important file in the
  // set, and every behavioural test still passed, because they assert that a listed path triggers the
  // fallback and never that the path refers to anything. A guard keyed on a typo is indistinguishable
  // from a working one until the day it is needed.
  const missing = GATE_DEFINING_PATHS.filter((path) => !existsSync(join(repoRoot, path)))
  assert.deepEqual(missing, [], `GATE_DEFINING_PATHS names files that do not exist: ${missing.join(', ')}`)
})

test('the gate-defining set covers the whole producer and consumer chain', () => {
  // Named individually so removing one is a deliberate act with a failing test attached, rather than a
  // quiet omission. Each earns its place by being able to change what a passing manifest means:
  //   ci.yml                     — which gates run and what passing is
  //   ci-main-provenance.yml     — the trusted workflow that acts on the decision
  //   provenance.mjs             — the decision itself
  //   check-main-provenance.mjs  — the consumer that applies it
  //   write-validated-tree.mjs   — the producer of the manifest
  //   zip.mjs                    — how the consumer reads the artifact
  //   aggregate (lib and bin)    — where the asserted gate results and totals come from
  for (const path of [
    '.github/workflows/ci.yml',
    '.github/workflows/ci-main-provenance.yml',
    'product/ci-metrics/lib/provenance.mjs',
    'product/ci-metrics/bin/check-main-provenance.mjs',
    'product/ci-metrics/bin/write-validated-tree.mjs',
    'product/ci-metrics/lib/zip.mjs',
    'product/ci-metrics/lib/aggregate.mjs',
    'product/ci-metrics/bin/aggregate.mjs',
  ]) {
    assert.ok(GATE_DEFINING_PATHS.includes(path), `${path} must be gate-defining`)
    assert.equal(touchesGateDefinition([path]), true, path)
  }
})

test('a rename away from a guarded path still trips the rule', () => {
  // GitHub reports a rename with the new name in `filename` and the old one in `previous_filename`.
  // Collecting only the former would let "rename provenance.mjs to something else" read as an ordinary
  // change. The collector contributes both names; this asserts the predicate honours the old one.
  const result = decide({
    pushTreeSha: 'd'.repeat(40),
    mergedPr: { number: 1 },
    manifests: [manifest()],
    changedPaths: ['product/ci-metrics/lib/provenance-renamed.mjs', 'product/ci-metrics/lib/provenance.mjs'],
  })
  assert.equal(result.outcome, 'fallback-needed')
  assert.equal(result.selfModifying, true)
})

test('the self-modification rule does not fire on ordinary product changes', () => {
  const ordinary = decide({
    pushTreeSha: 'd'.repeat(40),
    mergedPr: { number: 1 },
    manifests: [manifest()],
    changedPaths: ['product/src/AeroLink.Domain/ChangeControl/SystemChangeRequest.cs', 'product/client/src/App.tsx'],
  })
  assert.equal(ordinary.outcome, 'provenanced-match')
  assert.notEqual(ordinary.selfModifying, true)

  // A path that merely resembles a gate-defining one is not one of them.
  assert.equal(touchesGateDefinition(['docs/.github/workflows/ci.yml']), false)
  assert.equal(touchesGateDefinition(['product/ci-metrics/lib/provenance.test.mjs']), false)
  assert.equal(touchesGateDefinition([]), false)
  assert.equal(touchesGateDefinition(null), false)
  assert.equal(touchesGateDefinition(['.github/workflows/ci.yml']), true)
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
