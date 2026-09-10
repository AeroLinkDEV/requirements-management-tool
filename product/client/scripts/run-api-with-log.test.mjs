// Contract for the API log wrapper.
//
// The wrapper exists to make a failed browser journey explainable, so the properties that matter are the
// ones that hold when something has already gone wrong: a server that exits non-zero, a command that
// cannot start at all, a log destination that cannot be opened, a run torn down part-way through. Each is
// driven with a disposable fixture rather than the real API — the behaviour under test belongs to the
// wrapper, and a test that had to start ASP.NET Core would be slow, load-sensitive and no more truthful.
//
// Three rules this file follows deliberately, each of them written after getting it wrong:
//
//   * No timing guess is ever used as proof. Where a test needs the child to have reached a state, the
//     fixture says so and the test waits for that under a cancellable deadline.
//   * Every process this file starts is registered and its tree torn down in `finally`, including when an
//     assertion fails or a deadline expires. An earlier version leaked four orphaned node processes that
//     were still running an hour later.
//   * A test that cannot assert its subject on this platform is *skipped with a reason*, never quietly
//     turned into a no-op that reports as passing.
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
const isWindows = process.platform === 'win32'

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
    if (isWindows)
      execFileSync('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, timeout: 5_000, stdio: 'ignore' })
    else
      process.kill(pid, 'SIGKILL')
  } catch { /* already gone */ }
}

/**
 * A deadline that is always cancelled.
 *
 * `Promise.race` against a bare `setTimeout` leaves the timer armed after the race is won, which keeps the
 * event loop alive for the remainder of the deadline and makes a fast suite look slow for no reason.
 */
const withDeadline = (promise, what, timeout = DEADLINE_MS) => {
  let timer
  const deadline = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`timed out after ${timeout}ms waiting for ${what}`)), timeout)
  })
  return Promise.race([promise, deadline]).finally(() => clearTimeout(timer))
}

/** Polls for an observed condition under a deadline. Bounded polling of a real observation, never a guess
 * about how long something takes. */
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
  const state = { proc, stdout: '', stderr: '', spawnError: null }
  proc.stdout.setEncoding('utf8')
  proc.stderr.setEncoding('utf8')
  proc.stdout.on('data', chunk => { state.stdout += chunk })
  proc.stderr.on('data', chunk => { state.stderr += chunk })
  state.closed = new Promise((resolve, reject) => {
    proc.on('close', (code, signal) => resolve({ code, signal }))
    // An explicit failure rather than a wait that can only end at the deadline.
    proc.on('error', error => { state.spawnError = error; reject(error) })
  })
  return state
}

/** Runs the wrapper to completion under a cancellable deadline. */
const runWrapper = async (argv, options = {}) => {
  const registry = options.registry ?? []
  const state = startWrapper(argv, { ...options, registry })
  const outcome = await withDeadline(state.closed, 'the wrapper to exit')
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
    // Unchanged from what webServer already did. The wrapper forwards stderr and not stdout.
    assert.match(result.stderr, /hello-from-stderr/)
    assert.doesNotMatch(result.stdout, /hello-from-stdout/)
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
// Output received is output persisted, including an unterminated fragment
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
    // exact case the first implementation lost.
    const state = startWrapper(
      nodeArgv("process.stdout.write('printed-before-kill');setInterval(()=>{},1000)"),
      { logPath, registry },
    )
    await waitFor(
      () => existsSync(logPath) && /out~ printed-before-kill/.test(readFileSync(logPath, 'utf8')),
      'the fragment to reach the transcript',
    )
    killTree(state.proc.pid)
    const outcome = await withDeadline(state.closed, 'the wrapper to exit after teardown')
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
    // The split is established by acknowledgement, not by a delay.
    //
    // The earlier version wrote three pieces 60ms apart and assumed they arrived as three chunks. That is
    // a guess about the transport, not evidence: a slow reader can coalesce them and the test would then
    // be asserting nothing about split input. Here each piece is released only once the *previous* piece
    // has been observed in the transcript, so the boundaries are a fact the fixture waited for.
    const fixture = join(dir, 'chunked-fixture.mjs')
    writeFileSync(fixture, `
import { readFileSync, existsSync } from 'node:fs'
const log = process.env.AEROLINK_E2E_API_LOG
const seen = async text => {
  const until = Date.now() + 15000
  while (Date.now() < until) {
    if (existsSync(log) && readFileSync(log, 'utf8').includes(text)) return true
    await new Promise(r => setTimeout(r, 20))
  }
  throw new Error('fixture never observed ' + text)
}
process.stdout.write('PART-A|')
await seen('out~ PART-A|')
process.stdout.write('PART-B|')
await seen('out~ PART-B|')
process.stdout.write('PART-C\\n')
`)
    const result = await runWrapper([process.execPath, fixture], { logPath, registry })
    assert.equal(result.code, 0, result.stderr)

    const log = readFileSync(logPath, 'utf8')
    // Pieces while the line is open, then the record that closes it.
    assert.match(log, /^\S+ out~ PART-A\|$/m)
    assert.match(log, /^\S+ out~ PART-B\|$/m)
    assert.match(log, /^\S+ out\^ PART-C$/m)

    // Reassembly must reproduce the original exactly once. Counted over captured records only: the header
    // echoes the argv, and the fixture's own source contains the same markers.
    const pieces = log.split('\n')
      .filter(line => /^\d{4}-\d{2}-\d{2}T\S+ out[~^] /.test(line))
      .map(line => line.replace(/^\S+ out[~^] /, ''))
    assert.equal(pieces.join(''), 'PART-A|PART-B|PART-C')
    assert.equal(pieces.filter(piece => piece === 'PART-B|').length, 1, 'PART-B is captured exactly once')
  } finally {
    registry.forEach(killTree)
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a CRLF line ending is normalised, and that is a text transcript rather than a byte copy', async () => {
  const dir = scratch()
  const registry = []
  const logPath = join(dir, 'api.log')
  try {
    await runWrapper(nodeArgv("process.stdout.write('windows-line\\r\\n')"), { logPath, registry })
    // Recorded honestly: the trailing CR is stripped so a record is one readable line. This transcript is
    // text, and does not claim byte-for-byte preservation of arbitrary output.
    assert.match(readFileSync(logPath, 'utf8'), /^\S+ out windows-line$/m)
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
  const registry = []
  try {
    const refuse = env => {
      const child = spawn(process.execPath, [wrapper], {
        env: { ...process.env, ...env },
        stdio: ['ignore', 'pipe', 'pipe'],
      })
      // Registered like every other process this file starts, so a refusal that unexpectedly hangs is
      // still cleaned up by the caller's `finally`.
      registry.push(child.pid)
      let stderr = ''
      child.stderr.setEncoding('utf8')
      child.stderr.on('data', chunk => { stderr += chunk })
      child.stdout.resume()
      const closed = new Promise((resolve, reject) => {
        child.on('close', code => resolve({ code, stderr }))
        child.on('error', reject)
      })
      return withDeadline(closed, 'the wrapper to refuse and exit')
    }

    const missing = await refuse({ AEROLINK_E2E_API_ARGV: '' })
    assert.notEqual(missing.code, 0)
    assert.match(missing.stderr, /AEROLINK_E2E_API_ARGV is not set/)

    const malformed = await refuse({ AEROLINK_E2E_API_ARGV: 'not json' })
    assert.notEqual(malformed.code, 0)
    assert.match(malformed.stderr, /not valid JSON/)

    const empty = await refuse({ AEROLINK_E2E_API_ARGV: '[]' })
    assert.notEqual(empty.code, 0)
    assert.match(empty.stderr, /non-empty array of strings/)
  } finally {
    registry.forEach(killTree)
  }
})

// ---------------------------------------------------------------------------------------------------
// An unusable log destination is visibly degraded, never a silent success
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

    // The observable has to come from the child itself, and it has to be something the wrapper actually
    // surfaces. The wrapper does not forward child stdout, so a stdout marker would be unobservable here
    // and an assertion on it would pass for the wrong reason. A file the child writes is unambiguous.
    const sentinel = join(dir, 'child-ran.txt')
    const result = await runWrapper(
      nodeArgv(`require('node:fs').writeFileSync(${JSON.stringify(sentinel)}, 'child-executed')`),
      { logPath, registry },
    )

    // Diagnostics that can redden a correct run are worse than no diagnostics.
    assert.equal(result.code, 0, 'a successful child still succeeds')
    // Proof the child ran, with no empty-output escape hatch.
    assert.ok(existsSync(sentinel), 'the child executed')
    assert.equal(readFileSync(sentinel, 'utf8'), 'child-executed')
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
// Process ownership: the outer owner tears down the tree, and nothing outside it
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

    // The fixture stands in for `dotnet run`: it is the wrapper's direct child, it holds the listener, and
    // it launches a further child of its own. That grandchild is what a parent-only kill leaves behind and
    // what `/T` has to reach.
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
        const source = state.stdout + (existsSync(logPath) ? readFileSync(logPath, 'utf8') : '')
        const match = /READY (\d+) (\d+) (\d+)/.exec(source)
        return match ? { child: Number(match[1]), grandchild: Number(match[2]), port: Number(match[3]) } : false
      },
      'the fixture server to announce itself',
    )
    registry.push(ready.child, ready.grandchild)

    assert.ok(alive(ready.child), 'the direct child is running')
    assert.ok(alive(ready.grandchild), 'the descendant is running')
    assert.equal(await portFree(ready.port), false, 'the listener is held while the server runs')

    // Exactly what Playwright does to a webServer, and what the repository already does to an owned server
    // it must stop.
    killTree(state.proc.pid)

    const outcome = await withDeadline(state.closed, 'the wrapper to exit after its tree was torn down')
    assert.notEqual(outcome.code, 0, 'a torn-down run is not reported as success')

    // Asserted before the `finally` safety net runs, so these prove the teardown under test did the work
    // rather than the cleanup that follows every test.
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

// Programmatic SIGINT to *another* process is not a usable mechanism on Windows, so there is nothing here
// this platform can honestly assert.
//
// Measured on this machine rather than assumed: with Node v24.18.0 / libuv 1.52.1 on win32,
// `process.kill(<other pid>, 'SIGINT')` throws `ESRCH`, no handler runs, and the target survives. An
// earlier version of this test swallowed that throw and skipped its assertions, so it reported as passing
// while proving nothing — which is exactly the failure mode this comment exists to prevent recurring.
//
// The claim is therefore withdrawn on Windows rather than propped up: a console-control harness able to
// deliver a genuine Ctrl+C would be a far larger piece of machinery than the behaviour it verifies. The
// supported Windows path is the outer-owner tree teardown above, which is mandatory and unconditional.
test('a catchable shutdown stops the owned tree without touching anything else', {
  skip: isWindows
    ? 'programmatic SIGINT to another process is ESRCH on win32 (Node v24.18.0, libuv 1.52.1): no handler runs, so there is nothing to assert. Windows teardown is covered by the outer-owner tree test.'
    : false,
}, async () => {
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
        const source = state.stdout + (existsSync(logPath) ? readFileSync(logPath, 'utf8') : '')
        const match = /READY (\d+)/.exec(source)
        return match ? Number(match[1]) : false
      },
      'the fixture to announce itself',
    )
    registry.push(ready)

    // On a platform that delivers this, it must be delivered — a throw here is a failure, not a reason to
    // skip the assertions.
    process.kill(state.proc.pid, 'SIGINT')

    await withDeadline(state.closed, 'the wrapper to exit after SIGINT')
    await waitFor(() => !alive(ready), 'the owned child to exit after a catchable shutdown')
    assert.ok(alive(sentinel.pid), 'an unrelated process is untouched')
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
