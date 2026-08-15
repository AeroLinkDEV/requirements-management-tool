import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, mkdtempSync, writeFileSync, mkdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildRouteCoverage, extractRoutes, extractTestReferences, normalisePath, routeKey, summariseCoverage } from '../lib/routes.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = join(here, '..', '..', '..')
const apiDirectory = join(repoRoot, 'product', 'src', 'AeroLink.Api')
const testsDirectory = join(repoRoot, 'product', 'tests', 'AeroLink.Api.Tests')
const manifestPath = join(here, '..', 'route-coverage.json')

const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
const current = buildRouteCoverage(apiDirectory, testsDirectory)

const keyOf = (route) => routeKey(route.method, route.path)
const byKey = (routes) => new Map(routes.map((route) => [keyOf(route), route]))

test('every mutating route in the source is declared in the manifest', () => {
  // A new mutating endpoint is a new piece of public surface. It arrives here before it arrives in a
  // migration, so this is the point at which "does anything test it?" is still a cheap question.
  const declared = byKey(manifest.routes)
  const added = current.filter((route) => !declared.has(keyOf(route)))
  assert.deepEqual(
    added.map(keyOf),
    [],
    `New mutating route(s) are not in route-coverage.json. Add hosted boundary coverage, then regenerate:\n` +
      `  node product/test-contracts/tools/generate-route-manifest.mjs\n` +
      added.map((route) => `  ${route.method} ${route.path}  [${route.file}]`).join('\n'),
  )
})

test('every manifest route still exists in the source', () => {
  // Catches a route renamed or its method changed without the contract being updated: the old entry would
  // otherwise sit in the manifest asserting coverage of something that no longer exists.
  const live = byKey(current)
  const stale = manifest.routes.filter((route) => !live.has(keyOf(route)))
  assert.deepEqual(
    stale.map(keyOf),
    [],
    `route-coverage.json names route(s) the API no longer declares. If this was a deliberate rename or\n` +
      `removal, regenerate the manifest and confirm the replacement still has hosted coverage:\n` +
      stale.map((route) => `  ${route.method} ${route.path}`).join('\n'),
  )
})

test('no route loses its last hosted test', () => {
  // The guard #566 actually needs. Migrating a rule matrix down a tier is safe until it takes the final
  // hosted test with it, and every speed metric improves either way — so the loss has to be made loud here
  // rather than noticed later.
  const live = byKey(current)
  const regressions = manifest.routes
    .filter((route) => route.coveredBy.length > 0)
    .map((route) => ({ route, now: live.get(keyOf(route)) }))
    .filter(({ now }) => now && now.coveredBy.length === 0)
  assert.deepEqual(
    regressions.map(({ route }) => keyOf(route)),
    [],
    `Route(s) had hosted coverage in the manifest and now have none:\n` +
      regressions
        .map(({ route }) => `  ${route.method} ${route.path}  (was covered by ${route.coveredBy.join(', ')})`)
        .join('\n'),
  )
})

test('the recorded uncovered set does not grow', () => {
  // 85 mutating routes had no hosted test reaching them when this manifest was first taken. That is a finding
  // to work down, not a reason to block the build today — but it must not be quietly added to.
  const declaredUncovered = new Set(manifest.routes.filter((route) => route.coveredBy.length === 0).map(keyOf))
  const nowUncovered = current.filter((route) => route.coveredBy.length === 0).map(keyOf)
  const newlyUncovered = nowUncovered.filter((key) => !declaredUncovered.has(key))
  assert.deepEqual(newlyUncovered, [], `Route(s) newly have no hosted coverage:\n  ${newlyUncovered.join('\n  ')}`)
})

test('paths normalise so a route parameter is not mistaken for coverage of a different route', () => {
  assert.equal(normalisePath('/api/Change-Requests/{id:guid}/submit'), '/api/change-requests/{}/submit')
  assert.equal(normalisePath('/api/x/{a}/y/'), '/api/x/{}/y')
  // Two distinct routes that differ only after the parameter must not collapse together.
  assert.notEqual(normalisePath('/api/x/{id}/approve'), normalisePath('/api/x/{id}/reject'))
})

test('a group-relative path is resolved against its group prefix', () => {
  // Paths inside MapGroup begin with "/" and are still relative. Reading them as absolute produced entries
  // like `POST /checkout`, which match no test and look like an uncovered public route.
  const directory = mkdtempSync(join(tmpdir(), 'route-contract-'))
  writeFileSync(
    join(directory, 'Sample.cs'),
    `var group = app.MapGroup("/api/sample");\n group.MapPost("/checkout", Handler);\n app.MapPost("/api/plain", Handler);\n`,
    'utf8',
  )
  const routes = extractRoutes(directory)
  assert.deepEqual(routes.map((route) => route.key).sort(), ['POST /api/plain', 'POST /api/sample/checkout'])
})

test('an interpolated test URL still counts as reaching the route', () => {
  // Tests build URLs as $"{api}/api/...", so the literal does not begin with /api.
  const directory = mkdtempSync(join(tmpdir(), 'route-refs-'))
  mkdirSync(directory, { recursive: true })
  writeFileSync(
    join(directory, 'ThingApiTests.cs'),
    'var r = await client.PostAsync($"{api}/api/things/{id:guid}/submit", null);\n',
    'utf8',
  )
  const references = extractTestReferences(directory)
  assert.deepEqual([...(references.get('/api/things/{}/submit') ?? [])], ['ThingApiTests'])
})

test('the manifest summary is internally consistent', () => {
  const summary = summariseCoverage(manifest.routes)
  assert.equal(summary.total, manifest.routes.length)
  assert.equal(summary.covered + summary.uncovered.length, summary.total)
  assert.ok(summary.total > 200, `expected the full mutating surface, found ${summary.total}`)
})
