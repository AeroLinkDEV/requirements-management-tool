import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

// The shard planner hands Playwright a list of spec files. Playwright reads each one as a regular expression
// against the spec path rather than as a file name, so a name that ends inside a longer name matches both.
// That is not a hypothetical: review-comments.spec.ts matched document-review-comments.spec.ts, ran a shard's
// test on a shard that was not given it, and failed the count check the workflow makes afterwards.
const planner = fileURLToPath(new URL('../../client/scripts/plan-journey-shard.mjs', import.meta.url))

function plan(listing, shard, total) {
  const directory = mkdtempSync(join(tmpdir(), 'aerolink-journey-shard-'))
  try {
    const path = join(directory, 'listed.txt')
    writeFileSync(path, listing)
    const output = execFileSync(process.execPath, [planner, path, String(shard), String(total)], { encoding: 'utf8' })
    const lines = output.trim().split('\n')
    return { expected: Number(lines[0]), patterns: lines.slice(1) }
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
}

function listingOf(specs) {
  return specs.flatMap(([file, count]) =>
    Array.from({ length: count }, (_, index) => `  [chromium] › ${file}:${index + 1}:1 › case ${index + 1}`)).join('\n')
}

test('a spec whose name ends inside another name is claimed by exactly one shard', () => {
  const specs = [
    ['review-comments.spec.ts', 3],
    ['document-review-comments.spec.ts', 1],
    ['design-system.spec.ts', 4],
    ['zzz-slow.spec.ts', 2],
  ]
  const total = 4
  const claims = new Map(specs.map(([file]) => [file, 0]))
  let expectedAcross = 0

  for (let shard = 1; shard <= total; shard++) {
    const { expected, patterns } = plan(listingOf(specs), shard, total)
    expectedAcross += expected
    for (const pattern of patterns) {
      const matcher = new RegExp(pattern)
      for (const [file] of specs) {
        // Both separators, because the browser shards run on Windows and the planner runs everywhere.
        if (matcher.test(`tests/${file}`) || matcher.test(`tests\${file}`)) claims.set(file, claims.get(file) + 1)
      }
    }
  }

  assert.deepEqual([...claims].filter(([, count]) => count !== 1), [],
    'every spec file must be run by exactly one shard')
  assert.equal(expectedAcross, specs.reduce((sum, [, count]) => sum + count, 0),
    'the shards must together plan every discovered test exactly once')
})

test('the plan still names every discovered file and counts its tests', () => {
  const specs = [['alpha.spec.ts', 2], ['beta.spec.ts', 5]]
  const { expected, patterns } = plan(listingOf(specs), 1, 1)
  assert.equal(expected, 7)
  assert.equal(patterns.length, 2)
  for (const [file] of specs) {
    assert.ok(patterns.some((pattern) => new RegExp(pattern).test(`tests/${file}`)), file)
  }
})
