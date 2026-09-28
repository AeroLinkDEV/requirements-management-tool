import test from 'node:test'
import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { spawn, spawnSync } from 'node:child_process'
import { once } from 'node:events'
import { createRequire, syncBuiltinESMExports } from 'node:module'
import { fileURLToPath } from 'node:url'
import fs, { existsSync, readFileSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { browserStoragePath, createBrowserStorage, removeBrowserStorage } from './browser-storage.mjs'

// An OS handle, not a mocked remover: Windows refuses deletion until the holder releases it.
test('a Windows deletion failure retains ownership and can be retried after the handle closes', {
  skip: process.platform !== 'win32' && 'Windows file sharing contract', timeout: 30_000,
}, async () => {
  const runId = randomUUID(); const state = createBrowserStorage(runId)
  writeFileSync(state.database, 'owned database')
  const holder = spawn('powershell.exe', ['-NoProfile', '-Command',
    '$f=[IO.File]::Open($env:LOCKED_FILE,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite); try { [Console]::WriteLine("locked"); [Console]::ReadLine() | Out-Null } finally { $f.Dispose() }'],
  { windowsHide: true, env: { ...process.env, LOCKED_FILE: state.database }, stdio: ['pipe', 'pipe', 'pipe'] })
  const closed = once(holder, 'exit')
  try {
    const [ready] = await once(holder.stdout, 'data', { signal: AbortSignal.timeout(10_000) })
    assert.match(ready.toString(), /locked/)
    assert.throws(() => removeBrowserStorage(runId), { code: 'EPERM' })
    assert.equal(readFileSync(join(state.root, '.owner'), 'utf8'), runId)
    assert.equal(readFileSync(state.database, 'utf8'), 'owned database')
  } finally {
    holder.stdin.end('\n')
    await closed
    // Restore only this fixture's marker after exercising the pre-fix regression.
    if (!existsSync(join(state.root, '.owner'))) writeFileSync(join(state.root, '.owner'), runId, { flag: 'wx' })
    removeBrowserStorage(runId)
  }
  assert.equal(existsSync(state.root), false)
})

test('the Playwright CLI fails for incomplete cleanup and succeeds after ownership is restored', { timeout: 30_000 }, () => {
  const require = createRequire(import.meta.url)
  const runId = randomUUID(); const state = createBrowserStorage(runId)
  const fixtureId = randomUUID(); const fixture = createBrowserStorage(fixtureId)
  const config = join(fixture.root, 'playwright.config.cjs')
  const reporter = fileURLToPath(new URL('./browser-storage-reporter.mjs', import.meta.url))
  writeFileSync(join(fixture.root, 'passing.spec.cjs'), `const { test, expect } = require(${JSON.stringify(require.resolve('@playwright/test'))}); test('passing assertion', () => expect(2 + 2).toBe(4));`)
  writeFileSync(join(fixture.root, 'teardown.cjs'), `module.exports = () => require('node:fs').writeFileSync(${JSON.stringify(join(state.root, 'teardown-finished'))}, 'teardown still owns storage');`)
  writeFileSync(config, `module.exports = { testDir: '.', testMatch: '*.spec.cjs', globalTeardown: './teardown.cjs', workers: 1, reporter: [['list'], [${JSON.stringify(reporter)}, { runId: ${JSON.stringify(runId)} }]], outputDir: 'results' };`)
  const run = () => spawnSync(process.execPath, [require.resolve('@playwright/test/cli'), 'test', '--config', config], {
    encoding: 'utf8', timeout: 12_000, env: { ...process.env, CI: '', FORCE_COLOR: '0' }, windowsHide: true,
  })
  try {
    writeFileSync(join(state.root, '.owner'), 'someone else')
    const failed = run()
    assert.equal(failed.error, undefined)
    assert.match(failed.stdout, /1 passed/)
    assert.equal(failed.status, 1, failed.stdout + failed.stderr)
    assert.match(failed.stderr, /Browser storage cleanup failed/)
    assert.equal(readFileSync(join(state.root, '.owner'), 'utf8'), 'someone else')
    writeFileSync(join(state.root, '.owner'), runId)
    const passed = run()
    assert.equal(passed.status, 0, passed.stdout + passed.stderr)
    assert.equal(existsSync(state.root), false)
  } finally {
    if (existsSync(state.root)) writeFileSync(join(state.root, '.owner'), runId)
    removeBrowserStorage(runId); removeBrowserStorage(fixtureId)
  }
})

test('failure removing the emptied directory restores its marker for retry', t => {
  const runId = randomUUID(); const state = createBrowserStorage(runId)
  const original = fs.rmdirSync
  // Inject the otherwise timing-dependent final directory failure at the OS boundary.
  t.mock.method(fs, 'rmdirSync', path => {
    if (path === state.root) throw Object.assign(new Error('directory is busy'), { code: 'EPERM' })
    return original(path)
  })
  syncBuiltinESMExports()
  try {
    assert.throws(() => removeBrowserStorage(runId), { code: 'EPERM' })
    assert.equal(readFileSync(join(state.root, '.owner'), 'utf8'), runId)
  } finally {
    t.mock.restoreAll(); syncBuiltinESMExports()
    removeBrowserStorage(runId)
  }
  assert.equal(existsSync(state.root), false)
})

test('missing ownership and linked payloads refuse cleanup without touching another run', () => {
  const runId = randomUUID(); const state = createBrowserStorage(runId)
  const otherId = randomUUID(); const other = createBrowserStorage(otherId)
  const marker = join(state.root, '.owner'); const link = join(state.root, 'linked-evidence')
  try {
    writeFileSync(other.database, 'other database')
    unlinkSync(marker)
    assert.throws(() => removeBrowserStorage(runId), /owner marker/)
    writeFileSync(marker, runId, { flag: 'wx' })
    symlinkSync(other.root, link, process.platform === 'win32' ? 'junction' : 'dir')
    assert.throws(() => removeBrowserStorage(runId), /refuses a link/)
    assert.equal(readFileSync(marker, 'utf8'), runId)
    assert.equal(readFileSync(other.database, 'utf8'), 'other database')
  } finally {
    if (existsSync(link)) unlinkSync(link)
    removeBrowserStorage(runId); removeBrowserStorage(otherId)
  }
})

test('evidence is owned across restarts, independent of ambient storage, and removed with its database', () => {
  const runId = `storage-${randomUUID()}`
  const otherId = `storage-${randomUUID()}`
  const previous = process.env.Evidence__Root
  const other = createBrowserStorage(otherId)
  process.env.Evidence__Root = other.evidence
  try {
    const state = createBrowserStorage(runId)
    assert.notEqual(state.evidence, other.evidence)
    writeFileSync(join(state.evidence, 'generated.docx'), 'test document')
    writeFileSync(state.database, 'test database')
    assert.equal(createBrowserStorage(runId).evidence, state.evidence)
    assert.equal(readFileSync(join(state.evidence, 'generated.docx'), 'utf8'), 'test document')
    removeBrowserStorage(runId)
    assert.equal(existsSync(state.root), false)
    assert.equal(existsSync(other.root), true)
    removeBrowserStorage(runId)
    assert.ok(!browserStoragePath('../../persistent').includes('..'))
  } finally {
    removeBrowserStorage(runId); removeBrowserStorage(otherId)
    if (previous === undefined) delete process.env.Evidence__Root
    else process.env.Evidence__Root = previous
  }
})

test('cleanup refuses an altered owner marker', () => {
  const runId = randomUUID(); const state = createBrowserStorage(runId)
  try {
    writeFileSync(join(state.root, '.owner'), 'someone else')
    assert.throws(() => removeBrowserStorage(runId), /owner marker/)
    assert.equal(existsSync(state.root), true)
  } finally {
    writeFileSync(join(state.root, '.owner'), runId); removeBrowserStorage(runId)
  }
})
