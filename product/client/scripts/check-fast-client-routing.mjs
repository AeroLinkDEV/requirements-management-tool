import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const client = fileURLToPath(new URL('../', import.meta.url))
const manifest = JSON.parse(readFileSync(join(client, 'fast-client-tests.json'), 'utf8'))
assert.equal(manifest.schemaVersion, 1)
const files = [...manifest.logic, ...manifest.rendered]
assert.equal(files.length, new Set(files).size, 'a file may belong to only one Fast tier')
for (const file of files) assert.match(file, /^[a-z0-9-]+\.spec\.ts$/, 'Fast files must be explicit root spec names')

function discover(config) {
  const run = spawnSync(process.execPath, [join(client, 'node_modules/@playwright/test/cli.js'),
    'test', `--config=${config}`, '--list', '--reporter=json'], { cwd: client, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 })
  assert.equal(run.status, 0, run.stderr || run.stdout)
  const report = JSON.parse(run.stdout)
  assert.deepEqual(report.errors, [], 'discovery must not omit files due to import failures')
  const entries = []
  function visit(suite, parents = []) {
    for (const spec of suite.specs ?? []) {
      for (const _test of spec.tests) entries.push({ file: spec.file, title: [...parents, spec.title].join(' > ') })
    }
    for (const child of suite.suites ?? []) visit(child, [...parents, child.title])
  }
  for (const suite of report.suites) visit(suite)
  assert.ok(entries.length > 0, `empty ${config} discovery is not proof`)
  return entries
}
// Fast tiers intentionally use their own project labels, so the cross-tier identity is file + describe/title.
// A second Full project or any parameter collision produces a duplicate key and fails the uniqueness check.
const key = entry => JSON.stringify([entry.file, entry.title])
const full = discover('playwright.config.ts')
const expected = new Map(full.map(entry => [key(entry), entry]))
assert.equal(expected.size, full.length, 'Full identities must be unique')
const routed = new Map()
const counts = {}
for (const tier of ['logic', 'rendered']) {
  const entries = discover(`playwright.${tier}.config.ts`)
  assert.deepEqual([...new Set(entries.map(entry => entry.file))].sort(), [...manifest[tier]].sort(), `${tier} must discover every named file and nothing else`)
  for (const entry of entries) {
    const identity = key(entry)
    assert.ok(expected.has(identity), `Fast identity absent from Full: ${identity}`)
    assert.ok(!routed.has(identity), `duplicate Fast identity: ${identity}`)
    routed.set(identity, tier)
  }
  counts[tier] = entries.length
}
for (const entry of full) {
  if (files.includes(entry.file)) assert.ok(routed.has(key(entry)), `Fast omitted an identity: ${key(entry)}`)
}
const identities = full.map(entry => ({ ...entry, fastTier: routed.get(key(entry)) ?? 'full-only' }))
const result = {
  schemaVersion: 1,
  full: full.length,
  fast: counts,
  fullOnly: full.length - routed.size,
  fullIdentitySha256: createHash('sha256').update([...expected.keys()].sort().join('\n')).digest('hex'),
  note: 'Fast is additive. Full still executes every identity once; unknown files remain Full-only.',
  identities,
}
const output = join(client, 'test-results/fast/routing.json')
mkdirSync(dirname(output), { recursive: true })
writeFileSync(output, JSON.stringify(result, null, 2) + '\n')
console.log(JSON.stringify({ ...result, identities: undefined }, null, 2))
