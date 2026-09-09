import assert from 'node:assert/strict'
import { readFileSync, writeFileSync } from 'node:fs'
import { isAbsolute, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { snapshotEvidence } from '../lib/evidence-fingerprint.mjs'

const [operation, snapshotPath, ...extra] = process.argv.slice(2)
assert.ok(['capture', 'verify'].includes(operation) && snapshotPath && extra.length === 0,
  'Usage: node evidence-fingerprint.mjs capture|verify <absolute snapshot path outside product/.local>')
assert.ok(isAbsolute(snapshotPath), 'Snapshot destination must be absolute')
const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const root = resolve(repoRoot, 'product/.local')
const destination = resolve(snapshotPath)
const inside = relative(root, destination)
assert.ok(inside.startsWith(`..${sep}`) || inside === '..' || isAbsolute(inside),
  'Snapshot destination must not write the evidence store being protected')
if (operation === 'capture') {
  writeFileSync(destination, JSON.stringify({ schemaVersion: 1, root, entries: snapshotEvidence(root) }) + '\n', { flag: 'wx' })
} else {
  const before = JSON.parse(readFileSync(destination, 'utf8'))
  assert.equal(before.schemaVersion, 1)
  assert.equal(before.root, root, 'Snapshot belongs to another worktree')
  assert.deepEqual(snapshotEvidence(root), before.entries, 'Operator contracts changed product/.local')
}
console.log(`Persistent evidence ${operation} passed.`)
