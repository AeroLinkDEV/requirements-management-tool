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
    `(?:^|[/\\\\])${spec.replaceAll('.', '\\.')}$`,
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
  assert.match(output, /3 passed/)
})

test('a reset reused upstream connection is retried once and named (#1494)', () => {
  const result = runProbe('transport-recovery.spec.ts')
  const output = `${result.stdout}\n${result.stderr}`

  assert.equal(result.status, 0, output)
  assert.match(output, /1 passed/)
})

test('an unrecoverable upstream failure fails the child test and names its cause (#1494)', () => {
  const result = runProbe('transport-failure.spec.ts')
  const output = `${result.stdout}\n${result.stderr}`

  assert.notEqual(result.status, 0, output)
  assert.match(output, /rendered fixture network transport failed/)
  assert.match(output, /GET \/reset-always: ECONNRESET .* before any response on a new socket/)
  assert.match(output, /GET \/truncated: ECONNRESET .* during the response body/)
  // Never replayed: a POST, or a GET whose response had begun, on a reset reused connection.
  assert.match(output, /POST \/reset-reused: \w+ \(.*\) before any response on a reused socket after \d+ ms\."/)
  assert.match(output, /GET \/partial-reused: ECONNRESET \(.*\) before any response on a reused socket after \d+ ms\."/)
  assert.match(output, /1 failed/)
})

test('swallowed browser violations are prevented and still fail even when mocked', () => {
  const result = runProbe('network-offending.spec.ts')
  const output = `${result.stdout}\n${result.stderr}`

  assert.notEqual(result.status, 0, output)
  assert.match(output, /receiving-server proof: API=0 external=0; native image and font loaded/)
  assert.match(output, /rendered fixture attempted API or external network access/)
  assert.match(output, /api\/mocked/)
  assert.match(output, /2 failed/)
})
