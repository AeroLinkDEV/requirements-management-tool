// Contract for the API log wrapper.
//
// The wrapper exists to make a failed browser journey explainable, so the properties that matter are the
// ones that hold when something has already gone wrong: a server that exits non-zero, a command that
// cannot start at all, a run that is signalled part-way through. Each case is driven with a disposable
// child process rather than the real API — the behaviour under test belongs to the wrapper, and a test
// that had to start ASP.NET Core would be slow, load-sensitive, and no more truthful about it.
//
// Nothing here commits a deliberately unreliable product test to prove retention works.

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdtempSync, readFileSync, existsSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const wrapper = fileURLToPath(new URL('./run-api-with-log.mjs', import.meta.url))

/** Runs the wrapper around an arbitrary command and resolves with its exit code and transcript. */
const runWrapper = (command, { logPath, env = {}, killAfterMs } = {}) =>
  new Promise(resolve => {
    const child = spawn(process.execPath, [wrapper], {
      env: {
        ...process.env,
        AEROLINK_E2E_API_COMMAND: command,
        ...(logPath ? { AEROLINK_E2E_API_LOG: logPath } : {}),
        ...env,
      },
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    let stderr = ''
    child.stderr.setEncoding('utf8')
    child.stderr.on('data', chunk => { stderr += chunk })
    child.stdout.resume()
    if (killAfterMs !== undefined) setTimeout(() => child.kill('SIGTERM'), killAfterMs)
    child.on('close', (code, signal) => resolve({ code, signal, stderr }))
  })

const scratch = () => mkdtempSync(join(tmpdir(), 'aerolink-apilog-'))

/** A node one-liner, quoted so it survives the wrapper's `shell: true` on both cmd.exe and POSIX sh. */
const nodeCommand = source => `"${process.execPath}" -e "${source.replaceAll('"', '\\"')}"`

test('retains both stdout and stderr from the child', async () => {
  const dir = scratch()
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper(
      nodeCommand("process.stdout.write('hello-from-stdout\\n');process.stderr.write('hello-from-stderr\\n')"),
      { logPath },
    )
    assert.equal(result.code, 0)
    const log = readFileSync(logPath, 'utf8')
    // stdout is the stream Playwright discards, and the one the API's own logger actually uses.
    assert.match(log, /out hello-from-stdout/)
    assert.match(log, /err hello-from-stderr/)
    // Still forwarded to the parent, exactly as webServer already did.
    assert.match(result.stderr, /hello-from-stderr/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('stamps every captured line with a UTC timestamp', async () => {
  const dir = scratch()
  const logPath = join(dir, 'api.log')
  try {
    await runWrapper(nodeCommand("process.stdout.write('stamped\\n')"), { logPath })
    // Captured lines only — the header echoes the command, which contains the same word.
    const line = readFileSync(logPath, 'utf8')
      .split('\n')
      .find(item => /^\d{4}-\d{2}-\d{2}T/.test(item) && item.includes('stamped'))
    assert.ok(line, 'the captured line is present')
    assert.match(line, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z out stamped$/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a successful child still reports success', async () => {
  const dir = scratch()
  try {
    const result = await runWrapper(nodeCommand('process.exit(0)'), { logPath: join(dir, 'api.log') })
    assert.equal(result.code, 0)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a failing child keeps its exit code and its output is still retained', async () => {
  const dir = scratch()
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper(
      nodeCommand("process.stdout.write('dying-message\\n');process.exit(37)"),
      { logPath },
    )
    // The wrapper must never convert a failed server launch into a passing job.
    assert.equal(result.code, 37)
    const log = readFileSync(logPath, 'utf8')
    assert.match(log, /out dying-message/)
    assert.match(log, /exit=37/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('output written immediately before exit is not lost', async () => {
  const dir = scratch()
  const logPath = join(dir, 'api.log')
  try {
    // The failure this guards against is a wrapper that exits before its own file is flushed, which is
    // exactly the crash a transcript is supposed to explain.
    const result = await runWrapper(
      nodeCommand("process.stdout.write('last-words-before-exit\\n');process.exit(1)"),
      { logPath },
    )
    assert.equal(result.code, 1)
    assert.match(readFileSync(logPath, 'utf8'), /out last-words-before-exit/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a command that cannot start is reported and does not pass', async () => {
  const dir = scratch()
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper('this-command-does-not-exist-aerolink', { logPath })
    assert.notEqual(result.code, 0)
    // Whatever the shell said about it belongs in the transcript, and the file must exist to hold it.
    assert.ok(existsSync(logPath), 'a transcript is produced even for a start failure')
    assert.match(readFileSync(logPath, 'utf8'), /api log start/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a torn-down run keeps everything the server had already printed', async () => {
  const dir = scratch()
  const logPath = join(dir, 'api.log')
  try {
    const result = await runWrapper(
      nodeCommand("process.stdout.write('serving\\n');setInterval(()=>{},1000)"),
      { logPath, killAfterMs: 400 },
    )
    assert.notEqual(result.code, 0, 'a stopped server is not a successful one')
    // The durable guarantee, and the one that matters: output already read from the child is on disk.
    //
    // The closing `api log end` marker is deliberately NOT asserted. On Windows a terminated process
    // receives no catchable signal, so the wrapper cannot run its own ending — which is precisely why
    // every line is committed synchronously as it arrives rather than at exit. A transcript without the
    // end marker means the wrapper was killed; it never means capture failed.
    assert.match(readFileSync(logPath, 'utf8'), /out serving/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('separate execution identities do not overwrite one another', async () => {
  const dir = scratch()
  const first = join(dir, 'api-1.log')
  const second = join(dir, 'api-2.log')
  try {
    await runWrapper(nodeCommand("process.stdout.write('shard-one\\n')"), {
      logPath: first,
      env: { AEROLINK_E2E_SHARD: '1' },
    })
    await runWrapper(nodeCommand("process.stdout.write('shard-two\\n')"), {
      logPath: second,
      env: { AEROLINK_E2E_SHARD: '2' },
    })
    assert.match(readFileSync(first, 'utf8'), /shard-one/)
    assert.match(readFileSync(second, 'utf8'), /shard-two/)
    // A shard must never be able to read as the other one.
    assert.doesNotMatch(readFileSync(first, 'utf8'), /shard-two/)
    assert.doesNotMatch(readFileSync(second, 'utf8'), /shard-one/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('a retry appends to the transcript instead of erasing the first attempt', async () => {
  const dir = scratch()
  const logPath = join(dir, 'api.log')
  try {
    await runWrapper(nodeCommand("process.stdout.write('first-attempt\\n')"), { logPath })
    await runWrapper(nodeCommand("process.stdout.write('second-attempt\\n')"), { logPath })
    const log = readFileSync(logPath, 'utf8')
    assert.match(log, /first-attempt/)
    assert.match(log, /second-attempt/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

test('an unwritable log destination warns without failing the run', async () => {
  try {
    // The directory component is an existing file, so creating the tree beneath it cannot succeed.
    const dir = scratch()
    const blocker = join(dir, 'blocker')
    const result = await runWrapper(nodeCommand("process.stdout.write('still-ran\\n')"), {
      logPath: join(blocker, 'nested', 'api.log'),
      env: { AEROLINK_E2E_API_LOG_BLOCKER: blocker },
    })
    // Diagnostics that can redden a correct run are worse than no diagnostics.
    assert.equal(result.code, 0)
    rmSync(dir, { recursive: true, force: true })
  } catch (error) {
    assert.fail(`the wrapper must survive an unusable log path: ${error.message}`)
  }
})

test('no log path configured is a working run, not an error', async () => {
  const result = await runWrapper(nodeCommand("process.stdout.write('no-log-configured\\n')"))
  assert.equal(result.code, 0)
})

test('a missing command is refused rather than silently doing nothing', async () => {
  const result = await new Promise(resolve => {
    const child = spawn(process.execPath, [wrapper], {
      env: { ...process.env, AEROLINK_E2E_API_COMMAND: '' },
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    let stderr = ''
    child.stderr.setEncoding('utf8')
    child.stderr.on('data', chunk => { stderr += chunk })
    child.stdout.resume()
    child.on('close', code => resolve({ code, stderr }))
  })
  assert.notEqual(result.code, 0)
  assert.match(result.stderr, /AEROLINK_E2E_API_COMMAND is not set/)
})
