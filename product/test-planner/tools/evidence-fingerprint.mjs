import assert from 'node:assert/strict'
import { appendFileSync, readFileSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { isAbsolute, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { assertDestinationOutsideRoot, snapshotEvidence } from '../lib/evidence-fingerprint.mjs'

const [operation, snapshotPath, expectedDigest, ...extra] = process.argv.slice(2)
assert.ok(['capture', 'verify'].includes(operation) && snapshotPath && (operation === 'capture' ? !expectedDigest : /^[0-9a-f]{64}$/i.test(expectedDigest ?? '')) && extra.length === 0,
  'Usage: node evidence-fingerprint.mjs capture <absolute snapshot path outside product/.local> | verify <path> <capture sha256>')
assert.ok(isAbsolute(snapshotPath), 'Snapshot destination must be absolute')
const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const root = resolve(repoRoot, 'product/.local')
const destination = resolve(snapshotPath)
assertDestinationOutsideRoot(root, destination)
if (operation === 'capture') {
  writeFileSync(destination, JSON.stringify({ schemaVersion: 1, root, entries: snapshotEvidence(root) }) + '\n', { flag: 'wx' })
  const digest = createHash('sha256').update(readFileSync(destination)).digest('hex')
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `sha256=${digest}\n`)
  console.log(`Persistent evidence capture passed. sha256=${digest}`)
} else {
  const raw = readFileSync(destination)
  const actualDigest = createHash('sha256').update(raw).digest('hex')
  assert.equal(actualDigest, expectedDigest.toLowerCase(), 'Snapshot digest does not match the capture output')
  const before = JSON.parse(raw.toString('utf8'))
  assert.deepEqual(Object.keys(before).sort(), ['entries', 'root', 'schemaVersion'])
  assert.equal(before.schemaVersion, 1)
  assert.equal(before.root, root, 'Snapshot belongs to another worktree')
  assert.ok(Array.isArray(before.entries) && before.entries.every(entry => typeof entry === 'string'))
  assert.deepEqual(snapshotEvidence(root), before.entries, 'Operator contracts changed product/.local')
  console.log(`Persistent evidence verify passed. sha256=${actualDigest}`)
}
