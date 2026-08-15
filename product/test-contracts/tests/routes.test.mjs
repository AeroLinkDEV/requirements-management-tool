import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, mkdtempSync, writeFileSync, mkdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  buildRouteCoverage,
  extractRoutes,
  extractTestReferences,
  normalisePath,
  routeKey,
  summariseCoverage,
  uncoveredOutsideBaseline,
} from '../lib/routes.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = join(here, '..', '..', '..')
const apiDirectory = join(repoRoot, 'product', 'src', 'AeroLink.Api')
const testsDirectory = join(repoRoot, 'product', 'tests', 'AeroLink.Api.Tests')

const manifest = JSON.parse(readFileSync(join(here, '..', 'route-coverage.json'), 'utf8'))
const grandfathered = new Set(JSON.parse(readFileSync(join(here, '..', 'grandfathered-uncovered.json'), 'utf8')).uncovered)
const current = buildRouteCoverage(apiDirectory, testsDirectory)

const keyOf = (route) => routeKey(route.method, route.path)

// ---------------------------------------------------------------------------------------------------------
// The safety property. Asked against the frozen baseline, never against the regenerable manifest.
// ---------------------------------------------------------------------------------------------------------

test('no mutating route lacks hosted boundary evidence unless it was grandfathered', () => {
  const offenders = uncoveredOutsideBaseline(current, grandfathered)
  assert.deepEqual(
    offenders,
    [],
    `Route(s) have no hosted test exercising them with their own HTTP method, and are not in\n` +
      `grandfathered-uncovered.json. Add hosted boundary coverage — regenerating the manifest will not\n` +
      `silence this, by design:\n  ${offenders.join('\n  ')}`,
  )
})

test('a route that loses its last hosted test still fails after the manifest is regenerated', () => {
  // The round-1 defect. The old guard compared the tree against the generated manifest, and the documented
  // fix for a failure was to regenerate — which made the manifest agree with the loss. This proves the
  // replacement does not care what the manifest says.
  const victim = current.find((route) => route.coveredBy.length > 0)
  assert.ok(victim, 'expected at least one covered route')

  const afterLoss = current.map((route) => (route === victim ? { ...route, coveredBy: [] } : route))
  // Regeneration is simulated by deriving the baseline from the *new* state, exactly as a generator that
  // rewrote both files would have done.
  const regeneratedManifestSaysFine = afterLoss.filter((route) => route.coveredBy.length === 0).length > 0

  assert.ok(regeneratedManifestSaysFine, 'sanity: the loss is present in the regenerated observation')
  assert.deepEqual(uncoveredOutsideBaseline(afterLoss, grandfathered), [keyOf(victim)])
})

test('a new route with no hosted evidence still fails after the manifest is regenerated', () => {
  const invented = { method: 'POST', path: '/api/probe/newly-added', file: 'Probe.cs', coveredBy: [] }
  const afterAddition = [...current, invented]
  assert.deepEqual(uncoveredOutsideBaseline(afterAddition, grandfathered), ['POST /api/probe/newly-added'])
})

test('an exercised method does not cover a different method on the same path', () => {
  // `/api/enterprise-requirements/views/{}` carries both PUT and DELETE. A PUT test must not make the DELETE
  // route appear covered, and a mutating method added later to an already-mentioned path must not inherit
  // coverage it never had.
  const api = mkdtempSync(join(tmpdir(), 'route-method-api-'))
  writeFileSync(join(api, 'Views.cs'), 'app.MapPut("/api/views/{id:guid}", H);\napp.MapDelete("/api/views/{id:guid}", H);\n', 'utf8')
  const tests = mkdtempSync(join(tmpdir(), 'route-method-tests-'))
  writeFileSync(join(tests, 'ViewsApiTests.cs'), 'await client.PutAsJsonAsync($"{api}/api/views/{id}", body);\n', 'utf8')

  const coverage = buildRouteCoverage(api, tests)
  const put = coverage.find((route) => route.method === 'PUT')
  const del = coverage.find((route) => route.method === 'DELETE')
  assert.deepEqual(put.coveredBy, ['ViewsApiTests'], 'the exercised method is covered')
  assert.deepEqual(del.coveredBy, [], 'the unexercised method is not')
  assert.deepEqual(uncoveredOutsideBaseline(coverage, new Set()), ['DELETE /api/views/{}'])
})

// ---------------------------------------------------------------------------------------------------------
// Keeping the observation honest. These self-heal on regeneration, which is why they are not the safeguard.
// ---------------------------------------------------------------------------------------------------------

test('the committed manifest matches the current source', () => {
  const live = new Set(current.map(keyOf))
  const declared = new Set(manifest.routes.map(keyOf))
  const added = [...live].filter((key) => !declared.has(key)).sort()
  const stale = [...declared].filter((key) => !live.has(key)).sort()
  assert.deepEqual(
    { added, stale },
    { added: [], stale: [] },
    `route-coverage.json is out of date. Regenerate it:\n  node product/test-contracts/tools/generate-route-manifest.mjs`,
  )
})

// ---------------------------------------------------------------------------------------------------------
// Extraction behaviour.
// ---------------------------------------------------------------------------------------------------------

test('paths normalise so a route parameter is not mistaken for coverage of a different route', () => {
  assert.equal(normalisePath('/api/Change-Requests/{id:guid}/submit'), '/api/change-requests/{}/submit')
  assert.equal(normalisePath('/api/x/{a}/y/'), '/api/x/{}/y')
  assert.notEqual(normalisePath('/api/x/{id}/approve'), normalisePath('/api/x/{id}/reject'))
})

test('a group-relative path is resolved against its group prefix', () => {
  const directory = mkdtempSync(join(tmpdir(), 'route-contract-'))
  writeFileSync(
    join(directory, 'Sample.cs'),
    'var group = app.MapGroup("/api/sample");\n group.MapPost("/checkout", Handler);\n app.MapPost("/api/plain", Handler);\n',
    'utf8',
  )
  assert.deepEqual(extractRoutes(directory).map((route) => route.key).sort(), ['POST /api/plain', 'POST /api/sample/checkout'])
})

test('an interpolated test URL counts, and a URL with no identifiable verb does not', () => {
  const directory = mkdtempSync(join(tmpdir(), 'route-refs-'))
  mkdirSync(directory, { recursive: true })
  writeFileSync(
    join(directory, 'ThingApiTests.cs'),
    'await client.PostAsync($"{api}/api/things/{id:guid}/submit", null);\n' +
      'var url = $"{api}/api/things/{id:guid}/orphan";\n',
    'utf8',
  )
  const references = extractTestReferences(directory)
  assert.deepEqual([...(references.get('POST /api/things/{}/submit') ?? [])], ['ThingApiTests'])
  // A bare URL with no call attached proves nothing and is not counted as evidence.
  assert.equal([...references.keys()].some((key) => key.endsWith('/orphan')), false)
})

test('the inventory covers the whole mutating surface and its arithmetic holds', () => {
  const summary = summariseCoverage(current)
  assert.equal(summary.covered + summary.uncovered.length, summary.total)
  assert.ok(summary.total > 200, `expected the full mutating surface, found ${summary.total}`)
  // Every grandfathered key must name a route that exists, or the baseline is quietly rotting.
  const live = new Set(current.map(keyOf))
  const phantom = [...grandfathered].filter((key) => !live.has(key)).sort()
  assert.deepEqual(phantom, [], `grandfathered-uncovered.json names route(s) that no longer exist:\n  ${phantom.join('\n  ')}`)
})
