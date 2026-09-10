#!/usr/bin/env node
// Runs the browser suite's API server and keeps a copy of everything it printed.
//
// Playwright starts the API through `webServer`, and `webServer` decides on its own what happens to the
// child's output. In the pinned 1.61.1 the rule is asymmetric: stdout is forwarded only when the config
// sets `stdout: 'pipe'`, while stderr is forwarded unless the config says otherwise. Nothing here sets
// either, so stderr reaches the job console and stdout does not.
//
// That asymmetry is the whole problem, because ASP.NET Core's console logger writes *every* level to
// stdout and this repository never raises `LogToStandardErrorThreshold`. The API's request log, its
// warnings and its exceptions therefore all travel on the one stream Playwright drops. When a browser
// journey recorded requests that never completed (#939, candidate `5817a4c2`), the complete retained job
// log contained no API output at all — not because the server was silent, but because the stream carrying
// its account of those requests was discarded before anything could retain it.
//
// Forwarding stdout as well would put the output in the console, but the console is not an artifact and a
// job that dies during startup may never flush a reporter. So this wrapper owns a file instead. It is
// deliberately a wrapper rather than a Playwright reporter for three reasons: a reporter cannot capture
// output from a webServer that fails before the run begins, a reporter's output is bound to the console
// this is trying to stop flooding, and a wrapper can be tested on its own without starting Playwright.
//
// What it must not do is change the outcome. The child's exit code is this process's exit code, the
// child's stderr still reaches the parent exactly as before, and a failure to open or write the log is
// reported and then tolerated — diagnostics that can turn a passing run red are worse than no diagnostics.
//
// ---------------------------------------------------------------------------------------------------
// Process ownership, stated exactly, because getting this wrong orphans a server
//
// The command is spawned WITHOUT a shell, from a structured argument vector. An intermediate shell would
// add a process boundary that this wrapper cannot see past: signalling it would leave the real server
// running with its pipes open, which is precisely the defect this file was first written with.
//
// Removing the shell is not by itself sufficient. `dotnet run` launches the application as a further
// child, so the owned unit is a *tree*, never a single PID.
//
// The supported guarantee is therefore: **the outer owner terminates the tree.** Playwright does exactly
// that — `taskkill /pid <pid> /T /F` on Windows (see `playwright/lib/runner/index.js`), a group signal
// elsewhere — and this wrapper stays attached to that tree rather than detaching from it. The signal
// handling below is an addition for the cases the platform lets a process catch, not a replacement for
// it: a Windows `TerminateProcess` is uncatchable and no JavaScript handler can respond to one.
//
// Anything the wrapper kills, it kills by the PID it started, as a tree, and never by process name.

import { execFileSync, spawn } from 'node:child_process'
import { closeSync, mkdirSync, openSync, writeSync } from 'node:fs'
import { dirname, resolve } from 'node:path'

/** The command to run, as a JSON argument vector: `["<exe>", "<arg>", ...]`.
 *
 * Structured rather than a shell string so that a Windows dotnet path containing spaces needs no quoting
 * and no shell to interpret it. */
const argvJson = process.env.AEROLINK_E2E_API_ARGV

/** Where to write the transcript. Absent means "no file", which stays a working run rather than an error:
 * a developer invoking the config directly should not have to configure logging to run the suite. */
const logPath = process.env.AEROLINK_E2E_API_LOG

if (!argvJson) {
  process.stderr.write('run-api-with-log: AEROLINK_E2E_API_ARGV is not set.\n')
  process.exit(2)
}

let argv
try {
  argv = JSON.parse(argvJson)
} catch (error) {
  process.stderr.write(`run-api-with-log: AEROLINK_E2E_API_ARGV is not valid JSON (${error.message}).\n`)
  process.exit(2)
}
if (!Array.isArray(argv) || argv.length === 0 || argv.some(item => typeof item !== 'string')) {
  process.stderr.write('run-api-with-log: AEROLINK_E2E_API_ARGV must be a non-empty array of strings.\n')
  process.exit(2)
}

/** Identity written into the file's header. Everything is optional: the same wrapper runs on a laptop,
 * where none of these exist, and in CI, where all of them do. A transcript that cannot say which run,
 * shard and attempt produced it is very hard to line up against a failed job months later. */
const identity = {
  startedAtUtc: new Date().toISOString(),
  runId: process.env.GITHUB_RUN_ID ?? '',
  runAttempt: process.env.GITHUB_RUN_ATTEMPT ?? '',
  job: process.env.GITHUB_JOB ?? '',
  shard: process.env.AEROLINK_E2E_SHARD ?? '',
  sourceSha: process.env.GITHUB_SHA ?? '',
  label: process.env.AEROLINK_E2E_API_LOG_LABEL ?? 'api',
}

/** An open file descriptor rather than a write stream, and every record committed with `writeSync`.
 *
 * Playwright stops the server by killing the process tree, and on Windows that is `TerminateProcess`, not
 * a signal a Node process can catch and drain. A buffered stream loses whatever had not yet been handed
 * to the OS at that moment, and the records most likely to still be in that buffer are the last ones
 * before the kill, which are the ones worth having.
 *
 * This makes each record durable against the wrapper being killed. It says nothing about output the API
 * had produced but that had not yet reached this process, and it is not a power-loss claim. */
let logFd = null
/** Set once if the transcript could not be opened or a write failed, so the end record can say the
 * transcript is incomplete instead of implying it is whole. */
let captureDegraded = ''

if (logPath) {
  try {
    const absolute = resolve(logPath)
    mkdirSync(dirname(absolute), { recursive: true })
    // Append rather than truncate. A shard that retries, or a config with more than one API entry, must
    // never silently erase the earlier transcript.
    logFd = openSync(absolute, 'a')
  } catch (error) {
    captureDegraded = `open failed: ${error.message}`
    process.stderr.write(`run-api-with-log: could not open ${logPath} (${error.message}); continuing with diagnostics unavailable.\n`)
    logFd = null
  }
}

/** Writing must never be the thing that kills the run, so a failed write disables logging, records why,
 * and says so once rather than throwing into a handler nobody is watching. */
const writeLog = text => {
  if (logFd === null) return
  try {
    writeSync(logFd, text)
  } catch (error) {
    captureDegraded = `write failed: ${error.message}`
    process.stderr.write(`run-api-with-log: log write failed (${error.message}); continuing with diagnostics unavailable.\n`)
    try { closeSync(logFd) } catch { /* already unusable */ }
    logFd = null
  }
}

writeLog(`==== api log start ${JSON.stringify(identity)} ====\n`)
writeLog(`==== argv: ${JSON.stringify(argv)}\n`)

/**
 * Persists output the moment it arrives, without waiting for a line to be finished.
 *
 * The obvious implementation holds a partial line until its newline turns up. That loses the fragment
 * outright when the server is terminated mid-line — which is the normal way this server ends — so the
 * last thing it said before dying is exactly what would go missing.
 *
 * Every byte received is therefore written once, immediately, and nothing is buffered waiting for a
 * terminator. The tag says how to reassemble:
 *
 *   `out`  / `err`   a whole line
 *   `out~` / `err~`  a piece of a line; more of that line follows
 *   `out^` / `err^`  the piece that finishes a line begun by `~`
 *
 * A reader joins consecutive `~` records for one stream and closes the line at the `^`. No record is ever
 * written twice, so a transcript cannot double-count what it received.
 *
 * The streams are read with `setEncoding('utf8')`, so Node's decoder holds back an incomplete multi-byte
 * sequence and a character is never split across two records.
 */
const makeStamper = stream => {
  let midLine = false
  return {
    push(chunk) {
      let rest = chunk
      let newline = rest.indexOf('\n')
      while (newline >= 0) {
        const piece = rest.slice(0, newline).replace(/\r$/, '')
        writeLog(`${new Date().toISOString()} ${stream}${midLine ? '^' : ''} ${piece}\n`)
        midLine = false
        rest = rest.slice(newline + 1)
        newline = rest.indexOf('\n')
      }
      if (rest.length > 0) {
        writeLog(`${new Date().toISOString()} ${stream}~ ${rest}\n`)
        midLine = true
      }
    },
  }
}

const out = makeStamper('out')
const err = makeStamper('err')

const child = spawn(argv[0], argv.slice(1), { shell: false, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })

child.stdout.setEncoding('utf8')
child.stderr.setEncoding('utf8')
child.stdout.on('data', chunk => out.push(chunk))
child.stderr.on('data', chunk => {
  err.push(chunk)
  // Unchanged from what Playwright already did with stderr. Removing this would quietly take away console
  // visibility that people currently rely on when a server refuses to start.
  process.stderr.write(chunk)
})

/**
 * Terminates the owned tree by the PID this wrapper started.
 *
 * The same mechanism the repository already uses for an owned server it must stop
 * (`product/ci-metrics/lib/api-benchmark.mjs`): `taskkill /T /F` on Windows so descendants go with the
 * parent, a signal elsewhere. Never a sweep by process name, and never a PID this wrapper did not start.
 */
const terminateOwnedTree = () => {
  const running = child.pid !== undefined && child.exitCode === null && child.signalCode === null
  if (!running) return
  try {
    if (process.platform === 'win32') {
      // `/T` is what reaches `dotnet run`'s own child. Without it the application keeps running.
      execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, timeout: 5_000, stdio: 'ignore' })
    } else {
      // Not detached, so this process has no group of its own to signal — deliberately, because
      // detaching would take the server out of the tree the outer owner tears down. Signalling the child
      // directly is what is available here; descendant cleanup on this platform is the outer owner's.
      child.kill('SIGTERM')
    }
  } catch {
    // Already gone, or refused. Either way there is nothing further this wrapper can or should do, and
    // the outer owner's own tree teardown still applies.
  }
}

/** Only for the shutdowns a platform actually lets a process catch. A Windows `TerminateProcess` is not
 * one of them; that case is covered by the outer owner killing the tree, as documented at the top. */
let signalled = false
for (const signal of ['SIGTERM', 'SIGINT', 'SIGHUP']) {
  process.on(signal, () => {
    if (signalled) return
    signalled = true
    terminateOwnedTree()
  })
}

const finish = (code, signal) => {
  const state = captureDegraded === '' ? 'complete' : `degraded (${captureDegraded})`
  // Best-effort. On Windows a torn-down server is terminated rather than signalled, so this record is
  // frequently absent. Its absence means normal completion was not recorded — forced termination is one
  // explanation and a capture failure is another, which is why the state is named here rather than
  // inferred by a reader.
  writeLog(`==== api log end ${new Date().toISOString()} exit=${code ?? 'null'} signal=${signal ?? 'null'} capture=${state} ====\n`)
  if (logFd !== null) {
    try { closeSync(logFd) } catch { /* the transcript is already on disk */ }
    logFd = null
  }
  // A signalled child has no exit code of its own. 143/130 are the conventional shell encodings, and
  // using them keeps "stopped on purpose" distinguishable from "exited 0".
  process.exit(code ?? (signal === 'SIGINT' ? 130 : signal ? 143 : 0))
}

child.on('error', error => {
  process.stderr.write(`run-api-with-log: failed to start the API command (${error.message}).\n`)
  err.push(`wrapper: spawn failed: ${error.message}\n`)
  finish(1, null)
})

child.on('close', (code, signal) => finish(code, signal))
