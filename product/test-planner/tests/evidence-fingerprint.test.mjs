import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { basename, dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { snapshotEvidence } from '../lib/evidence-fingerprint.mjs'

function disposable(action) {
  const root = mkdtempSync(join(tmpdir(), 'aerolink-fingerprint-contract-'))
  try { action(root) } finally {
    assert.equal(dirname(resolve(root)), resolve(tmpdir()))
    assert.ok(basename(root).startsWith('aerolink-fingerprint-contract-'))
    rmSync(root, { recursive: true, force: true })
  }
}

test('fingerprint distinguishes absent, empty, added, removed and renamed evidence', () => disposable(root => {
  const evidence = join(root, 'evidence')
  const absent = snapshotEvidence(evidence)
  mkdirSync(evidence)
  const empty = snapshotEvidence(evidence)
  assert.notDeepEqual(empty, absent)
  writeFileSync(join(evidence, 'one'), 'ab')
  const added = snapshotEvidence(evidence)
  assert.notDeepEqual(added, empty)
  assert.deepEqual(snapshotEvidence(evidence), added)
  renameSync(join(evidence, 'one'), join(evidence, 'two'))
  assert.notDeepEqual(snapshotEvidence(evidence), added)
  rmSync(join(evidence, 'two'))
  assert.deepEqual(snapshotEvidence(evidence), empty)
}))

test('fingerprint detects same-size content changes with restored mtime and metadata-only changes', () => disposable(root => {
  const file = join(root, 'proof.txt')
  const originalTime = new Date('2000-01-01T00:00:00.000Z')
  writeFileSync(file, 'ab')
  utimesSync(file, originalTime, originalTime)
  const before = snapshotEvidence(root)
  writeFileSync(file, 'ac')
  utimesSync(file, originalTime, originalTime)
  assert.notDeepEqual(snapshotEvidence(root), before)
  writeFileSync(file, 'ab')
  utimesSync(file, originalTime, originalTime)
  assert.deepEqual(snapshotEvidence(root), before)
  utimesSync(file, originalTime, new Date('2000-01-01T00:00:01.000Z'))
  assert.notDeepEqual(snapshotEvidence(root), before)
}))

test('actual CLI fails closed for missing, overwritten, altered and foreign snapshots', () => disposable(root => {
  const cli = fileURLToPath(new URL('../tools/evidence-fingerprint.mjs', import.meta.url))
  const snapshot = join(root, 'before.json')
  const run = (...args) => spawnSync(process.execPath, [cli, ...args], { encoding: 'utf8' })
  assert.notEqual(run('verify', snapshot).status, 0)
  assert.equal(run('capture', snapshot).status, 0)
  assert.equal(run('verify', snapshot).status, 0)
  const original = readFileSync(snapshot, 'utf8')
  assert.notEqual(run('capture', snapshot).status, 0, 'capture must not replace the original baseline')
  assert.equal(readFileSync(snapshot, 'utf8'), original)
  writeFileSync(snapshot, JSON.stringify({ ...JSON.parse(original), entries: ['forged'] }))
  assert.notEqual(run('verify', snapshot).status, 0)
  writeFileSync(snapshot, JSON.stringify({ ...JSON.parse(original), root: 'another-worktree' }))
  assert.notEqual(run('verify', snapshot).status, 0)
  writeFileSync(snapshot, JSON.stringify({ ...JSON.parse(original), schemaVersion: 99 }))
  assert.notEqual(run('verify', snapshot).status, 0)
  const forbidden = join(JSON.parse(original).root, 'fingerprint-must-not-be-written.json')
  const refusal = run('capture', forbidden)
  assert.notEqual(refusal.status, 0)
  assert.match(refusal.stderr, /must not write the evidence store/)
}))
