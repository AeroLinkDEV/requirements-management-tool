import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'

const client = fileURLToPath(new URL('../', import.meta.url))
const cli = join(client, 'node_modules/@playwright/test/cli.js')

function runProbe(spec) {
  return spawnSync(process.execPath, [
    cli,
    'test',
    '--config=playwright.isolation-probe.config.ts',
    spec,
    '--reporter=line',
  ], {
    cwd: client,
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
  })
}

test('a swallowed API-context violation still fails the child test', () => {
  const result = runProbe('offending.spec.ts')
  const output = `${result.stdout}\n${result.stderr}`

  assert.notEqual(result.status, 0, output)
  assert.match(output, /rendered fixture used a forbidden API request context/)
  assert.match(output, /page\.request/)
  assert.match(output, /context\.request\.fetch/)
  assert.match(output, /1 failed/)
})

test('the ordinary isolation-probe control passes', () => {
  const result = runProbe('control.spec.ts')
  const output = `${result.stdout}\n${result.stderr}`

  assert.equal(result.status, 0, output)
  assert.match(output, /1 passed/)
})
