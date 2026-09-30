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
import BrowserStorageReporter from './browser-storage-reporter.mjs'

// An OS handle, not a mocked remover: Windows refuses deletion until the holder releases it.
test('a Windows deletion failure retains ownership and can be retried after the handle closes', {
  skip: process.platform !== 'win32' && 'Windows file sharing contract', timeout: 30_000,
}, async t => {
  const runId = randomUUID(); const state = createBrowserStorage(runId)
  writeFileSync(state.database, 'owned database')
  const started = performance.now()
  const phases = []
  const phase = message => {
    const entry = `${Math.round(performance.now() - started)}ms ${message}`
    phases.push(entry)
    process.stderr.write(`[native handle ${runId}] ${entry}\n`)
  }
  const holder = spawn('powershell.exe', ['-NoProfile', '-Command',
    '$f=[IO.File]::Open($env:LOCKED_FILE,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite); try { [Console]::WriteLine("locked"); Start-Sleep -Seconds 20 } finally { $f.Dispose() }'],
  { windowsHide: true, env: { ...process.env, LOCKED_FILE: state.database }, stdio: ['ignore', 'pipe', 'pipe'] })
  phase(`spawn requested, pid ${holder.pid ?? 'unavailable'}`)
  let stderr = ''; holder.stderr.on('data', chunk => { stderr += chunk })
  holder.on('error', error => phase(`helper error: ${error.message}`))
  holder.on('exit', (code, signal) => phase(`helper exited: ${code ?? signal}${stderr ? `; stderr: ${stderr}` : ''}`))
  const abortHolder = () => {
    phase(`test aborted; phases: ${phases.join(' | ')}${stderr ? `; stderr: ${stderr}` : ''}`)
    holder.kill()
  }
  t.signal.addEventListener('abort', abortHolder, { once: true })
  const closed = once(holder, 'exit')
  // Observe early spawn failures even while waiting for the readiness message.
  void closed.catch(() => {})
  try {
    // Startup shares this test's existing 30-second deadline. A separate ten-second
    // timer measured PowerShell scheduling, not the storage contract under test.
    const [ready] = await Promise.race([
      once(holder.stdout, 'data', { signal: t.signal }),
      closed.then(([code, signal]) => { throw new Error(`Handle holder exited before readiness (${code ?? signal}): ${stderr}`) }),
    ])
    assert.match(ready.toString(), /locked/)
    phase('exclusive-delete handle acquired')
    assert.throws(() => removeBrowserStorage(runId), { code: 'EPERM' })
    phase('native EPERM observed; ownership retained')
    assert.equal(readFileSync(join(state.root, '.owner'), 'utf8'), runId)
    assert.equal(readFileSync(state.database, 'utf8'), 'owned database')
    // The reporter starts while the OS handle is still held. Its await must cover
    // release and complete cleanup, rather than fail a run with passing assertions.
    const release = setTimeout(() => { phase('release timer fired'); holder.kill() }, 1_000)
    try {
      phase('reporter cleanup started')
      assert.equal(await new BrowserStorageReporter({ runId }).onEnd({ status: 'passed' }), undefined)
      assert.equal(existsSync(state.root), false)
      phase('reporter cleanup finished; storage removed')
    } finally {
      clearTimeout(release)
    }
  } finally {
    // Terminating this one owned helper releases its native handle. Do not rely on
    // Console.ReadLine accepting redirected stdin on a headless Windows runner.
    holder.kill()
    await closed
    t.signal.removeEventListener('abort', abortHolder)
    // Restore only this fixture's marker after exercising the pre-fix regression.
    if (existsSync(state.root) && !existsSync(join(state.root, '.owner'))) writeFileSync(join(state.root, '.owner'), runId, { flag: 'wx' })
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

test('failure removing the emptied directory restores its marker and fails the run after bounded retries', async t => {
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
    assert.deepEqual(await new BrowserStorageReporter({ runId }).onEnd({ status: 'passed' }), { status: 'failed' })
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
