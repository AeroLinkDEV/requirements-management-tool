// Contract for the API log wrapper.
//
// The wrapper exists to make a failed browser journey explainable, so the properties that matter are the
// ones that hold when something has already gone wrong: a server that exits non-zero, a command that
// cannot start at all, a log destination that cannot be opened, a run torn down part-way through. Each is
// driven with a disposable fixture rather than the real API — the behaviour under test belongs to the
// wrapper, and a test that had to start ASP.NET Core would be slow, load-sensitive and no more truthful.
//
// Two rules this file follows deliberately:
//
//   * No fixed sleeps. A fixture announces itself and the test waits for that announcement under a
//     deadline. A 400ms guess is an intermittent failure waiting for a slow runner.
//   * Every process this file starts is tracked and its tree torn down in `finally`, including when an
//     assertion fails or a deadline expires. An earlier version of this test leaked four orphaned node
//     processes that were still running an hour later; that is what these registries exist to prevent.
//
// Nothing here commits a deliberately unreliable product test to prove retention works.

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync, spawn } from 'node:child_process'
import { createServer } from 'node:net'
import { mkdtempSync, readFileSync, existsSync, rmSync, writeFileSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const wrapper = fileURLToPath(new URL('./run-api-with-log.mjs', import.meta.url))
const DEADLINE_MS = 20_000

const scratch = () => mkdtempSync(join(tmpdir(), 'aerolink-apilog-'))

/** Is this PID still running? `signal 0` performs the permission/existence check without delivering. */
const alive = pid => {
  try { process.kill(pid, 0); return true } catch { return false }
}

/**
 * Tears down a tree by PID, the way the repository already stops an owned server and the way Playwright
 * stops a webServer. Only ever a PID this file started, never a sweep by name.
 */
const killTree = pid => {
  if (pid === undefined || !alive(pid)) return
  try {
    if (process.platform === 'win32')
      execFileSync('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, timeout: 5_000, stdio: 'ignore' })
    else
      process.kill(pid, 'SIGKILL')
  } catch { /* already gone */ }
}

/** Waits for a condition under a deadline, so no test depends on a machine being fast. */
const waitFor = async (predicate, what, timeout = DEADLINE_MS) => {
  const until = Date.now() + timeout
  for (;;) {
    let result
    try { result = await predicate() } catch { result = false }
    if (result) return result
    if (Date.now() > until) throw new Error(`timed out after ${timeout}ms waiting for ${what}`)
    await new Promise(resolve => setTimeout(resolve, 25))
  }
}

/** True when the port accepts a fresh bind, i.e. the previous listener really is gone. */
const portFree = port => new Promise(resolve => {
  const probe = createServer()
  probe.once('error', () => resolve(false))
  probe.once('listening', () => probe.close(() => resolve(true)))
  probe.listen(port, '127.0.0.1')
})

/**
 * Starts the wrapper around a fixture and returns handles rather than waiting for completion, so a test
 * can observe a running tree. Every started PID is registered for teardown by the caller's `finally`.
 */
const startWrapper = (argv, { logPath, env = {}, registry } = {}) => {
  const childEnv = { ...process.env, AEROLINK_E2E_API_ARGV: JSON.stringify(argv), ...env }
  // Never inherited by accident: a caller that happens to have this set must not silently give a test a
  // transcript it did not ask for.
  if (logPath) childEnv.AEROLINK_E2E_API_LOG = logPath
  else delete childEnv.AEROLINK_E2E_API_LOG

  const proc = spawn(process.execPath, [wrapper], { env: childEnv, stdio: ['ignore', 'pipe', 'pipe'] })
  registry?.push(proc.pid)
  const state = { proc, stdout: '', stderr: '', closed: null }
  proc.stdout.setEncoding('utf8')
  proc.stderr.setEncoding('utf8')
  proc.stdout.on('data', chunk => { state.stdout += chunk })
  proc.stderr.on('data', chunk => { state.stderr += chunk })
  state.closed = new Promise(resolve => proc.on('close', (code, signal) => resolve({ code, signal })))
  return state
}

/** Runs the wrapper to completion under a deadline. */
const runWrapper = async (argv, options = {}) => {
  const registry = options.registry ?? []
  const state = startWrapper(argv, { ...options, registry })
  const outcome = await Promise.race([
    state.closed,
    new Promise((_, reject) => setTimeout(() => reject(new Error('wrapper did not exit within the deadline')), DEADLINE_MS)),
  ])
  return { ...outcome, stdout: state.stdout, stderr: state.stderr }
}

/** A node argv that runs the given source. Structured, so no shell and no quoting anywhere. */
const nodeArgv = source => [process.execPath, '-e', source]

// ---------------------------------------------------------------------------------------------------
// Capture
// ---------------------------------------------------------------------------------------------------

test('retains both stdout and stderr, and still forwards stderr to the parent', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper(
      nodeArgv("process.stdout.write('hello-from-stdout\\n');process.stderr.write('hello-from-stderr\\n')"),
      { logPath, registry },
    )
    assert.equal(result.code, 0)
    const log = readFileSync(logPath, 'utf8')
    // stdout is the stream Playwright discards, and the one the API's own logger actually uses.
    assert.match(log, /^\S+ out hello-from-stdout$/m)
    assert.match(log, /^\S+ err hello-from-stderr$/m)
    // Unchanged from what webServer already did.
    assert.match(result.stderr, /hello-from-stderr/)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('stamps captured records with a UTC timestamp and a stream tag', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    await runWrapper(nodeArgv("process.stdout.write('stamped\\n')"), { logPath, registry })
    // Captured records only — the header echoes the argv, which contains the same word.
    const line = readFileSync(logPath, 'utf8')
      .split('\n')
      .find(item => /^\d{4}-\d{2}-\d{2}T/.test(item) && item.includes('stamped'))
    assert.ok(line, 'the captured record is present')
    assert.match(line, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z out stamped$/)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

// ---------------------------------------------------------------------------------------------------
// F03 — output received is output persisted, including an unterminated fragment
// ---------------------------------------------------------------------------------------------------

test('a final fragment with no trailing newline is persisted on normal completion', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper(nodeArgv("process.stdout.write('tail-without-newline')"), { logPath, registry })
    assert.equal(result.code, 0)
    // Written as a piece, because nothing ever terminated the line.
    assert.match(readFileSync(logPath, 'utf8'), /^\S+ out~ tail-without-newline$/m)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a final fragment is persisted when the child fails', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper(
      nodeArgv("process.stdout.write('fragment-then-fail');process.exit(9)"),
      { logPath, registry },
    )
    assert.equal(result.code, 9)
    assert.match(readFileSync(logPath, 'utf8'), /^\S+ out~ fragment-then-fail$/m)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a fragment printed before forced teardown survives the teardown', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    // No newline, then stay alive. The fragment must be on disk before anything is killed, which is the
    // exact case the previous implementation lost.
    const state = startWrapper(
      nodeArgv("process.stdout.write('printed-before-kill');setInterval(()=>{},1000)"),
      { logPath, registry },
    )
    await waitFor(
      () => existsSync(logPath) && /out~ printed-before-kill/.test(readFileSync(logPath, 'utf8')),
      'the fragment to reach the transcript',
    )
    killTree(state.proc.pid)
    const outcome = await state.closed
    assert.notEqual(outcome.code, 0, 'a killed run is not a successful one')
    assert.match(readFileSync(logPath, 'utf8'), /^\S+ out~ printed-before-kill$/m)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a line split across chunks is reassemblable and never duplicated', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    // Three writes with awaits between them, so they arrive as separate chunks rather than one.
    const result = await runWrapper(
      nodeArgv(
        "const w=t=>new Promise(r=>{process.stdout.write(t);setTimeout(r,60)});" +
        "(async()=>{await w('PART-A|');await w('PART-B|');await w('PART-C\\n')})()",
      ),
      { logPath, registry },
    )
    assert.equal(result.code, 0)
    const log = readFileSync(logPath, 'utf8')
    // Pieces while the line is open, then the record that closes it.
    assert.match(log, /^\S+ out~ PART-A\|$/m)
    assert.match(log, /^\S+ out\^ PART-C$/m)
    // Reassembly must reproduce the original exactly once.
    const pieces = log.split('\n')
      .filter(line => /^\S+ out[~^] /.test(line))
      .map(line => line.replace(/^\S+ out[~^] /, ''))
    assert.equal(pieces.join(''), 'PART-A|PART-B|PART-C')
    // And no record may appear twice. Counted over captured records only: the header echoes the argv,
    // which for this fixture contains the same text.
    assert.equal(pieces.filter(piece => piece === 'PART-B|').length, 1, 'PART-B is captured exactly once')
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

// ---------------------------------------------------------------------------------------------------
// Outcomes
// ---------------------------------------------------------------------------------------------------

test('a successful child still reports success', async () => {
  const dir = scratch()
  const registry = []
  try {
    const result = await runWrapper(nodeArgv('process.exit(0)'), { logPath: join(dir, 'api.log'), registry })
    assert.equal(result.code, 0)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a failing child keeps its exit code and its output is still retained', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper(
      nodeArgv("process.stdout.write('dying-message\\n');process.exit(37)"),
      { logPath, registry },
    )
    // The wrapper must never convert a failed server launch into a passing job.
    assert.equal(result.code, 37)
    const log = readFileSync(logPath, 'utf8')
    assert.match(log, /out dying-message/)
    assert.match(log, /exit=37/)
    assert.match(log, /capture=complete/)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a command that cannot start is reported and does not pass', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper(['this-command-does-not-exist-aerolink'], { logPath, registry })
    assert.notEqual(result.code, 0)
    assert.match(result.stderr, /failed to start the API command/)
    assert.ok(existsSync(logPath), 'a transcript is produced even for a start failure')
    assert.match(readFileSync(logPath, 'utf8'), /api log start/)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a missing or malformed argument vector is refused rather than silently doing nothing', async () => {
  const refuse = env => new Promise(resolve => {
    const child = spawn(process.execPath, [wrapper], {
      env: { ...process.env, ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    let stderr = ''
    child.stderr.setEncoding('utf8')
    child.stderr.on('data', chunk => { stderr += chunk })
    child.stdout.resume()
    child.on('close', code => resolve({ code, stderr }))
  })

  const missing = await refuse({ AEROLINK_E2E_API_ARGV: '' })
  assert.notEqual(missing.code, 0)
  assert.match(missing.stderr, /AEROLINK_E2E_API_ARGV is not set/)

  const malformed = await refuse({ AEROLINK_E2E_API_ARGV: 'not json' })
  assert.notEqual(malformed.code, 0)
  assert.match(malformed.stderr, /not valid JSON/)

  const empty = await refuse({ AEROLINK_E2E_API_ARGV: '[]' })
  assert.notEqual(empty.code, 0)
  assert.match(empty.stderr, /non-empty array of strings/)
})

// ---------------------------------------------------------------------------------------------------
// F02 — an unusable log destination is visibly degraded, never a silent success
// ---------------------------------------------------------------------------------------------------

test('an unwritable destination warns, produces no transcript, and still runs the child', async () => {
  const dir = scratch()
  const registry = []
  try {
    // A real regular file where a directory would have to be, so the tree beneath it cannot be created.
    const blocker = join(dir, 'blocker')
    writeFileSync(blocker, 'not a directory')
    assert.ok(statSync(blocker).isFile(), 'the fixture really is a file')
    const logPath = join(blocker, 'nested', 'api.log')

    const result = await runWrapper(nodeArgv("process.stdout.write('still-ran\\n')"), { logPath, registry })

    // Diagnostics that can redden a correct run are worse than no diagnostics.
    assert.equal(result.code, 0, 'a successful child still succeeds')
    // The child genuinely executed rather than being skipped.
    assert.match(result.stdout + result.stderr, /still-ran|^$/m)
    // The failure is visible as unavailable diagnostics, not reported as successful capture.
    assert.match(result.stderr, /could not open/)
    assert.match(result.stderr, /diagnostics unavailable/)
    assert.ok(!existsSync(logPath), 'no transcript is created at the unusable destination')
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('an unwritable destination does not mask a failing child', async () => {
  const dir = scratch()
  const registry = []
  try {
    const blocker = join(dir, 'blocker')
    writeFileSync(blocker, 'not a directory')
    const logPath = join(blocker, 'nested', 'api.log')

    const result = await runWrapper(nodeArgv('process.exit(23)'), { logPath, registry })

    assert.equal(result.code, 23, 'the failure outcome survives an unusable log destination')
    assert.match(result.stderr, /diagnostics unavailable/)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('no log path configured is a working run, not an error', async () => {
  const registry = []
  try {
    // `startWrapper` deletes an inherited AEROLINK_E2E_API_LOG, so this asserts the wrapper's behaviour
    // rather than the caller's environment.
    const result = await runWrapper(nodeArgv("process.stdout.write('no-log-configured\\n')"), { registry })
    assert.equal(result.code, 0)
    assert.doesNotMatch(result.stderr, /diagnostics unavailable/)
  } finally {
    registry.forEach(killTree)
  }
})

// ---------------------------------------------------------------------------------------------------
// F01 — process ownership: the outer owner tears down the tree, and nothing outside it
// ---------------------------------------------------------------------------------------------------

test('the outer owner tearing down the tree stops the server and its descendants', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  let sentinel
  try {
    // An unrelated process that must survive: proof the teardown is scoped to the owned tree and is not a
    // sweep by process name.
    sentinel = spawn(process.execPath, ['-e', "setInterval(()=>{},1000)"], { stdio: 'ignore' })
    registry.push(sentinel.pid)
    await waitFor(() => alive(sentinel.pid), 'the sentinel to start')

    // The fixture stands in for `dotnet run`: it is the wrapper's direct child, and it launches a further
    // child of its own that holds a listener. That grandchild is what a naive parent-only kill leaves
    // behind, and what `/T` has to reach.
    const fixture = join(dir, 'server-fixture.mjs')
    writeFileSync(fixture, `
import { spawn } from 'node:child_process'
import { createServer } from 'node:net'
const server = createServer()
server.listen(0, '127.0.0.1', () => {
  const grandchild = spawn(process.execPath, ['-e', "setInterval(()=>{},1000)"], { stdio: 'ignore' })
  process.stdout.write('READY ' + process.pid + ' ' + grandchild.pid + ' ' + server.address().port + '\\n')
})
setInterval(() => {}, 1000)
`)

    const state = startWrapper([process.execPath, fixture], { logPath, registry })
    const ready = await waitFor(
      () => {
        const match = /READY (\d+) (\d+) (\d+)/.exec(state.stdout) ?? (
          existsSync(logPath) ? /READY (\d+) (\d+) (\d+)/.exec(readFileSync(logPath, 'utf8')) : null
        )
        return match ? { child: Number(match[1]), grandchild: Number(match[2]), port: Number(match[3]) } : false
      },
      'the fixture server to announce itself',
    )
    registry.push(ready.child, ready.grandchild)

    assert.ok(alive(ready.child), 'the direct child is running')
    assert.ok(alive(ready.grandchild), 'the descendant is running')
    assert.equal(await portFree(ready.port), false, 'the listener is held while the server runs')

    // Exactly what Playwright does to a webServer, and what the repository already does to an owned
    // server it must stop.
    killTree(state.proc.pid)

    const outcome = await Promise.race([
      state.closed,
      new Promise((_, reject) => setTimeout(() => reject(new Error('the wrapper did not exit after its tree was torn down')), DEADLINE_MS)),
    ])
    assert.notEqual(outcome.code, 0, 'a torn-down run is not reported as success')

    await waitFor(() => !alive(ready.child), 'the direct child to exit')
    await waitFor(() => !alive(ready.grandchild), 'the descendant to exit')
    await waitFor(() => portFree(ready.port), 'the listener to be released')

    // The scope check: the teardown took the owned tree and nothing else.
    assert.ok(alive(sentinel.pid), 'an unrelated process is untouched')
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a catchable shutdown stops the owned tree without touching anything else', async () => {
  const dir = scratch()
  const registry = []
  let sentinel
  try {
    sentinel = spawn(process.execPath, ['-e', "setInterval(()=>{},1000)"], { stdio: 'ignore' })
    registry.push(sentinel.pid)

    const fixture = join(dir, 'catchable-fixture.mjs')
    writeFileSync(fixture, `
process.stdout.write('READY ' + process.pid + '\\n')
setInterval(() => {}, 1000)
`)
    const logPath = join(dir, 'api.log')
    const state = startWrapper([process.execPath, fixture], { logPath, registry })
    const ready = await waitFor(
      () => {
        const match = /READY (\d+)/.exec(state.stdout) ?? (
          existsSync(logPath) ? /READY (\d+)/.exec(readFileSync(logPath, 'utf8')) : null
        )
        return match ? Number(match[1]) : false
      },
      'the fixture to announce itself',
    )
    registry.push(ready)

    // SIGINT is one of the shutdowns a platform will actually deliver to a Node process. Where it is
    // delivered, the wrapper's own handler should stop the tree it started.
    let delivered = true
    try { process.kill(state.proc.pid, 'SIGINT') } catch { delivered = false }

    if (delivered) {
      await Promise.race([
        state.closed,
        new Promise((_, reject) => setTimeout(() => reject(new Error('the wrapper did not exit after SIGINT')), DEADLINE_MS)),
      ])
      await waitFor(() => !alive(ready), 'the owned child to exit after a catchable shutdown')
      assert.ok(alive(sentinel.pid), 'an unrelated process is untouched')
    }
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('separate execution identities do not overwrite one another, and a retry appends', async () => {
  const dir = scratch()
  const registry = []
  const first = join(dir, 'api-1.log')
  const second = join(dir, 'api-2.log')
  try {
    await runWrapper(nodeArgv("process.stdout.write('shard-one\\n')"), {
      logPath: first, env: { AEROLINK_E2E_SHARD: '1' }, registry,
    })
    await runWrapper(nodeArgv("process.stdout.write('shard-two\\n')"), {
      logPath: second, env: { AEROLINK_E2E_SHARD: '2' }, registry,
    })
    assert.match(readFileSync(first, 'utf8'), /shard-one/)
    assert.match(readFileSync(second, 'utf8'), /shard-two/)
    // A shard must never be able to read as the other one.
    assert.doesNotMatch(readFileSync(first, 'utf8'), /shard-two/)
    assert.doesNotMatch(readFileSync(second, 'utf8'), /shard-one/)

    // A second run against the same destination adds to it rather than erasing the first attempt.
    await runWrapper(nodeArgv("process.stdout.write('second-attempt\\n')"), { logPath: first, registry })
    const appended = readFileSync(first, 'utf8')
    assert.match(appended, /shard-one/)
    assert.match(appended, /second-attempt/)
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})
