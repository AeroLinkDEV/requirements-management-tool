import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = join(here, '..', '..', '..')
const journeys = join(repoRoot, 'product', 'client', 'tests')

const specs = (directory) => readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
  const path = join(directory, entry.name)
  if (entry.isDirectory()) return specs(path)
  return entry.name.endsWith('.spec.ts') ? [path] : []
})

// A page.route handler that fetches the real response keeps working after its test's last assertion. If
// Playwright closes the context first, the next test inherits "Response has been disposed" or "Test ended"
// from this one. #1001 fixed two files; the same flake then came back from procedure-explorer-parity (5 retries
// in a week of runs, 2026-09-19 to 09-26). Every spec whose handlers fetch must wait for them in teardown.
test('every journey whose route handlers fetch real responses settles them before teardown', () => {
  const fetching = specs(journeys).filter((path) => readFileSync(path, 'utf8').includes('route.fetch('))
  assert.ok(fetching.length >= 16, `expected the known fetching journeys, found ${fetching.length}`)
  // The wait must sit inside a top-level test.afterEach body, which ends at the first line that closes it.
  const settles = (text) => [...text.matchAll(/^test\.afterEach\(/gm)].some((hook) => {
    const end = text.indexOf('\n})', hook.index)
    return /await page\.unrouteAll\(\{ behavior: ['"]wait['"] \}\)/.test(text.slice(hook.index, end < 0 ? undefined : end))
  })
  const unguarded = fetching
    .filter((path) => !settles(readFileSync(path, 'utf8').replaceAll('\r\n', '\n')))
    .map((path) => relative(repoRoot, path).replaceAll('\\', '/'))
  assert.deepEqual(unguarded, [], 'Add test.afterEach(async ({ page }) => { await page.unrouteAll({ behavior: \'wait\' }) }) to these specs')
})
