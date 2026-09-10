import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { execFileSync, spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { basename, dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { assertDestinationOutsideRoot, snapshotEvidence } from '../lib/evidence-fingerprint.mjs'

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
  const removed = snapshotEvidence(evidence)
  assert.notDeepEqual(removed, empty, 'directory metadata records the create/remove mutation')
  assert.equal(removed.length, 1)
}))

test('fingerprint records root and nested directory metadata and modification times', () => disposable(root => {
  const evidence = join(root, 'evidence')
  const nested = join(evidence, 'nested')
  mkdirSync(nested, { recursive: true })
  const before = snapshotEvidence(evidence)
  assert.match(before[0], /^<root>\|D\|/)
  const directoryTime = new Date('2000-01-01T00:00:00.000Z')
  utimesSync(nested, directoryTime, directoryTime)
  assert.notDeepEqual(snapshotEvidence(evidence), before)
  const rootBefore = snapshotEvidence(evidence)
  utimesSync(evidence, directoryTime, new Date('2000-01-01T00:00:01.000Z'))
  assert.notDeepEqual(snapshotEvidence(evidence), rootBefore)
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

test('real-path containment follows a Windows junction and rejects missing leaves inside it', { skip: process.platform !== 'win32' }, () => disposable(root => {
  const protectedRoot = join(root, 'protected')
  const outsideRoot = join(root, 'outside')
  const junction = join(outsideRoot, 'protected-link')
  mkdirSync(protectedRoot)
  mkdirSync(outsideRoot)
  execFileSync(process.env.ComSpec ?? 'cmd.exe', ['/d', '/c', 'mklink', '/J', junction, protectedRoot], { stdio: 'ignore' })
  try {
    assert.throws(() => assertDestinationOutsideRoot(protectedRoot, join(protectedRoot, 'new.json')), /must not write/)
    assert.throws(() => assertDestinationOutsideRoot(protectedRoot, join(junction, 'through-link.json')), /must not write/)
  } finally {
    execFileSync(process.env.ComSpec ?? 'cmd.exe', ['/d', '/c', 'rmdir', junction], { stdio: 'ignore' })
  }
}))

test('actual CLI fails closed for missing, overwritten, altered and foreign snapshots', () => disposable(root => {
  const cli = fileURLToPath(new URL('../tools/evidence-fingerprint.mjs', import.meta.url))
  const snapshot = join(root, 'before.json')
  const captureOutput = join(root, 'capture-output.txt')
  const run = (args, extraEnv = {}) => spawnSync(process.execPath, [cli, ...args], {
    encoding: 'utf8', env: { ...process.env, ...extraEnv },
  })
  assert.notEqual(run(['verify', snapshot]).status, 0, 'verify must require the capture digest')
  const capture = run(['capture', snapshot], { GITHUB_OUTPUT: captureOutput })
  assert.equal(capture.status, 0)
  assert.match(capture.stdout, /sha256=[0-9a-f]{64}/i)
  const outputDigest = /^sha256=([0-9a-f]{64})$/im.exec(readFileSync(captureOutput, 'utf8'))?.[1]
  assert.ok(outputDigest, 'capture must publish the snapshot digest')
  assert.equal(run(['verify', snapshot, outputDigest]).status, 0)
  const original = readFileSync(snapshot, 'utf8')
  const originalJson = JSON.parse(original)
  const digestOfSnapshot = () => createHash('sha256').update(readFileSync(snapshot)).digest('hex')
  assert.notEqual(run(['capture', snapshot]).status, 0, 'capture must not replace the original baseline')
  assert.equal(readFileSync(snapshot, 'utf8'), original)

  writeFileSync(snapshot, JSON.stringify({ ...originalJson, entries: ['forged'] }) + '\n')
  assert.notEqual(run(['verify', snapshot, digestOfSnapshot()]).status, 0, 'a self-consistent forged fingerprint must still fail')
  writeFileSync(snapshot, JSON.stringify({ ...originalJson, extra: 'forged' }) + '\n')
  assert.notEqual(run(['verify', snapshot, digestOfSnapshot()]).status, 0, 'extra snapshot fields must fail closed')
  writeFileSync(snapshot, JSON.stringify(originalJson, null, 2) + '\n')
  assert.notEqual(run(['verify', snapshot, outputDigest]).status, 0, 'raw snapshot formatting is digest-bound')
  writeFileSync(snapshot, '{malformed\n')
  assert.notEqual(run(['verify', snapshot, outputDigest]).status, 0, 'malformed snapshots must fail before comparison')
  writeFileSync(snapshot, JSON.stringify({ ...originalJson, root: 'another-worktree' }) + '\n')
  assert.notEqual(run(['verify', snapshot, outputDigest]).status, 0)
  writeFileSync(snapshot, JSON.stringify({ ...originalJson, schemaVersion: 99 }) + '\n')
  assert.notEqual(run(['verify', snapshot, outputDigest]).status, 0)
  const forbidden = join(originalJson.root, 'fingerprint-must-not-be-written.json')
  const refusal = run(['capture', forbidden])
  assert.notEqual(refusal.status, 0)
  assert.match(refusal.stderr, /must not write the evidence store/)
}))
