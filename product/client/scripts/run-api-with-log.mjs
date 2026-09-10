#!/usr/bin/env node
// Runs the browser suite's API server and keeps a copy of everything it printed.
//
// Playwright starts the API through `webServer`, and `webServer` decides on its own what happens to the
// child's output. In the pinned 1.61.1 the rule is asymmetric: stdout is forwarded only when the config
// sets `stdout: 'pipe'`, while stderr is forwarded unless the config says otherwise. Nothing here sets
// either, so stderr reaches the job console and stdout does not.
//
// That asymmetry is the whole problem, because ASP.NET Core's console logger writes *every* level to
// stdout by default and this repository never raises `LogToStandardErrorThreshold`. The API's request
// log, its warnings and its exceptions therefore all travel on the one stream Playwright drops. When a
// browser journey recorded requests that never completed (#939, candidate `5817a4c2`), the complete
// retained job log contained no API output at all — not because the server was silent, but because the
// stream carrying its account of those requests was discarded before anything could retain it.
//
// Forwarding stdout as well would put the output in the console, but the console is not an artifact and
// a job that dies during startup may never flush a reporter. So this wrapper owns a file instead. It is
// deliberately a wrapper rather than a Playwright reporter for three reasons: a reporter cannot capture
// output from a webServer that fails before the run begins, a reporter's output is bound to the console
// this is trying to stop flooding, and a wrapper can be tested on its own without starting Playwright.
//
// What it must not do is change the outcome. The child's exit code is this process's exit code, the
// child's stderr still reaches the parent exactly as before, and a failure to open the log file is
// reported and then ignored — diagnostics that can turn a passing run red are worse than no diagnostics.

import { spawn } from 'node:child_process'
import { closeSync, mkdirSync, openSync, writeSync } from 'node:fs'
import { dirname, resolve } from 'node:path'

/** The command to run, as a shell string. Passed by environment rather than argv so a Windows path with
 * spaces is not re-parsed by `cmd /c` on its way through Playwright's own shell invocation. */
const command = process.env.AEROLINK_E2E_API_COMMAND

/** Where to write the transcript. Absent means "no file", which stays a working run rather than an error:
 * a developer invoking the config directly should not have to configure logging to run the suite. */
const logPath = process.env.AEROLINK_E2E_API_LOG

if (!command) {
  process.stderr.write('run-api-with-log: AEROLINK_E2E_API_COMMAND is not set.\n')
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

/** An open file descriptor rather than a write stream, and every line committed with `writeSync`.
 *
 * Playwright stops the server by killing the process tree, and on Windows — which is where this suite
 * runs — that is `TerminateProcess`, not a signal a Node process can catch and drain. A buffered stream
 * loses whatever had not yet been handed to the OS at that moment, and the lines most likely to still be
 * in that buffer are the last ones before the kill, which are exactly the lines worth having. Paying a
 * syscall per line buys the guarantee that anything the API printed is on disk as soon as it is read. */
let logFd = null
if (logPath) {
  try {
    const absolute = resolve(logPath)
    mkdirSync(dirname(absolute), { recursive: true })
    // Append rather than truncate. A shard that retries, or a config with more than one API entry, must
    // never silently erase the earlier transcript.
    logFd = openSync(absolute, 'a')
  } catch (error) {
    // Explicitly a warning, not a failure. See the note above about diagnostics that can redden a run.
    process.stderr.write(`run-api-with-log: could not open ${logPath} (${error.message}); continuing without it.\n`)
    logFd = null
  }
}

/** Writing must never be the thing that kills the run, so a failed write disables logging and says so
 * once rather than throwing into a stream handler nobody is watching. */
const writeLog = text => {
  if (logFd === null) return
  try {
    writeSync(logFd, text)
  } catch (error) {
    process.stderr.write(`run-api-with-log: log write failed (${error.message}); continuing without it.\n`)
    try { closeSync(logFd) } catch { /* already unusable */ }
    logFd = null
  }
}

writeLog(`==== api log start ${JSON.stringify(identity)} ====\n`)
writeLog(`==== command: ${command}\n`)

/** Each line is stamped as it arrives so a stalled request can be located in time against the Playwright
 * trace, which is the join this investigation actually needs. Chunks are not lines, so a partial trailing
 * line is held until the rest of it turns up. */
const makeStamper = stream => {
  let pending = ''
  return {
    push(chunk) {
      pending += chunk
      const lines = pending.split(/\r?\n/)
      pending = lines.pop() ?? ''
      for (const line of lines) writeLog(`${new Date().toISOString()} ${stream} ${line}\n`)
    },
    flush() {
      if (pending.length > 0) writeLog(`${new Date().toISOString()} ${stream} ${pending}\n`)
      pending = ''
    },
  }
}

const out = makeStamper('out')
const err = makeStamper('err')

const child = spawn(command, { shell: true, stdio: ['ignore', 'pipe', 'pipe'] })

child.stdout.setEncoding('utf8')
child.stderr.setEncoding('utf8')
child.stdout.on('data', chunk => out.push(chunk))
child.stderr.on('data', chunk => {
  err.push(chunk)
  // Unchanged from what Playwright already did with stderr. Removing this would quietly take away
  // console visibility that people currently rely on when a server refuses to start.
  process.stderr.write(chunk)
})

/** Playwright stops the server by signalling this process; the child is what actually has to go. Only
 * processes this wrapper started are touched. */
let signalled = false
for (const signal of ['SIGTERM', 'SIGINT', 'SIGHUP']) {
  process.on(signal, () => {
    if (signalled) return
    signalled = true
    if (child.exitCode === null && child.signalCode === null) child.kill(signal)
  })
}

const finish = (code, signal) => {
  out.flush()
  err.flush()
  // Best-effort. On Windows a torn-down server is terminated rather than signalled, so this closing line
  // is frequently absent — that is a known and accepted limitation, and its absence means "the wrapper
  // was killed", never "capture failed". The transcript's content is already durable regardless, because
  // every line above was committed synchronously as it arrived.
  writeLog(`==== api log end ${new Date().toISOString()} exit=${code ?? 'null'} signal=${signal ?? 'null'} ====\n`)
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
  err.push(`wrapper: spawn failed: ${error.message}`)
  finish(1, null)
})

child.on('close', (code, signal) => finish(code, signal))
