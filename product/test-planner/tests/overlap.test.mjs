import { test } from 'node:test'
import assert from 'node:assert/strict'
import { detectOverlaps, overlapsFor, renderComment, surfacesFor, SURFACES } from '../lib/overlap.mjs'

const pr = (number, files, extra = {}) => ({ number, title: `PR ${number}`, author: 'agent', branch: `b/${number}`, files, ...extra })

test('two pull requests editing the same file are a high-severity overlap', () => {
  const overlaps = detectOverlaps([
    pr(1, ['product/client/src/App.tsx', 'README.md']),
    pr(2, ['product/client/src/App.tsx']),
  ])
  assert.equal(overlaps.length, 1)
  assert.equal(overlaps[0].severity, 'high')
  assert.deepEqual(overlaps[0].sharedFiles, ['product/client/src/App.tsx'])
})

test('a shared surface with no shared file is still reported', () => {
  // The case git cannot see, and the reason this exists: two migrations, different files, both apply
  // cleanly alone and produce an ambiguous sequence together.
  const overlaps = detectOverlaps([
    pr(1, ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0007_add_positions.cs']),
    pr(2, ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0008_add_stage_kind.cs']),
  ])
  assert.equal(overlaps.length, 1)
  assert.equal(overlaps[0].severity, 'medium', 'no textual conflict, but a real one')
  assert.deepEqual(overlaps[0].sharedFiles, [])
  assert.equal(overlaps[0].sharedSurfaces[0].key, 'migrations')
})

test('unrelated pull requests produce nothing', () => {
  const overlaps = detectOverlaps([
    pr(1, ['product/docs/OPERATIONS.md']),
    pr(2, ['product/client/src/components/Badge.tsx']),
  ])
  assert.deepEqual(overlaps, [])
  assert.equal(renderComment(1, overlaps), null, 'silence when there is nothing to say')
})

test('the real collisions from 2026-08-15 are detected', () => {
  // Both happened today. The first is two agents editing the CI gate; the second is a route-table
  // change landing beside a workflow change, which is how the classifier got flipped under a branch.
  const gateCollision = detectOverlaps([
    pr(592, ['.github/workflows/ci.yml', 'product/ci-metrics/tests/ci-workflow-contract.test.mjs']),
    pr(597, ['.github/workflows/ci.yml', 'product/test-planner/lib/classify.mjs']),
  ])
  assert.equal(gateCollision[0].severity, 'high')
  assert.ok(gateCollision[0].sharedSurfaces.some((s) => s.key === 'ci-gate'))

  const routeAndMetrics = detectOverlaps([
    pr(588, ['product/test-contracts/route-coverage.json', 'product/src/AeroLink.Api/RequirementsEndpoints.cs']),
    pr(590, ['product/ci-metrics/lib/provenance.mjs']),
  ])
  assert.deepEqual(routeAndMetrics, [], 'these two genuinely did not overlap, and must not be flagged')
})

test('severity ordering puts textual conflicts first', () => {
  const overlaps = detectOverlaps([
    pr(1, ['product/src/AeroLink.Domain/A.cs']),
    pr(2, ['product/src/AeroLink.Domain/B.cs']),
    pr(3, ['product/src/AeroLink.Domain/A.cs']),
  ])
  assert.equal(overlaps[0].severity, 'high', 'the shared-file pair sorts first')
  assert.ok(overlaps.every((entry) => entry.sharedFiles.length > 0 || entry.sharedSurfaces.length > 0))
})

test('overlapsFor always presents the requesting pull request as "a"', () => {
  const overlaps = detectOverlaps([pr(1, ['x/App.tsx']), pr(2, ['x/App.tsx'])])
  const forTwo = overlapsFor(2, overlaps)
  assert.equal(forTwo[0].a.number, 2)
  assert.equal(forTwo[0].b.number, 1)
})

test('the comment states the evidence, not just a verdict', () => {
  const overlaps = detectOverlaps([
    pr(1, ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0007.cs']),
    pr(2, ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0008.cs']),
  ])
  const body = renderComment(1, overlaps)
  assert.match(body, /#2/)
  assert.match(body, /migration sequence/)
  assert.match(body, /0008\.cs/, 'the other pull request\'s actual paths must appear')
  assert.match(body, /warning, not a block/)
  assert.match(body, /No file is shared/, 'the no-textual-conflict case must be explained, not implied')
})

test('surfaces are narrow enough to be worth reading', () => {
  // A surface matching most of the repository would flag every pair and be muted within a day.
  const everything = [
    'README.md',
    'product/client/src/components/Badge.tsx',
    'product/client/tests/journey.spec.ts',
    'product/docs/OPERATIONS.md',
    'product/tests/AeroLink.Domain.Tests/RuleTests.cs',
  ]
  const hits = surfacesFor(everything)
  assert.equal(hits.size, 0, `ordinary files matched surfaces: ${[...hits.keys()].join(', ')}`)
})

test('every surface carries a reason a reader can act on', () => {
  for (const surface of SURFACES) {
    assert.ok(surface.label.length > 0, `${surface.key} needs a label`)
    assert.ok(surface.why.length > 40, `${surface.key} needs a why worth reading`)
    assert.ok(surface.patterns.length > 0, `${surface.key} needs patterns`)
  }
})

test('malformed input cannot throw', () => {
  // This runs against whatever the GitHub API returns for every open pull request; it must degrade
  // rather than fail, because it is advisory and must never redden a gate.
  assert.deepEqual(detectOverlaps(null), [])
  assert.deepEqual(detectOverlaps([null, undefined, {}]), [])
  assert.deepEqual(detectOverlaps([pr(1, ['a']), { number: 2 }]), [])
  assert.deepEqual(surfacesFor(null).size, 0)
})
