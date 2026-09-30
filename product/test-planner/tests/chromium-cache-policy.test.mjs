import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')

/** The text of one job: from its `  <id>:` line to the next top-level job. */
function job(id) {
  const start = workflow.indexOf(`\n  ${id}:\n`)
  assert.ok(start >= 0, `ci.yml has no ${id} job`)
  const next = workflow.slice(start + 1).search(/\n {2}[a-z0-9-]+:\n/)
  return next < 0 ? workflow.slice(start) : workflow.slice(start, start + 1 + next)
}

/** The `uses:` of a job's "Cache Chromium" step. */
function chromiumCache(id) {
  const match = job(id).match(/- name: Cache Chromium\n(?:.*\n)*?\s+uses: (\S+)/)
  assert.ok(match, `${id} has no Cache Chromium step`)
  return match[1]
}

// #1304: a pull request's cache is visible to that pull request alone, so the browser jobs, which never run on a main
// push, saved entries nobody could read — and on a key miss spent minutes archiving Chromium after their tests had
// passed, inside the 30-minute job limit (#1301's shard 3: 374 passed, cancelled in "Post Cache Chromium"). They
// restore only; the one job that runs on main saves.
test('the browser jobs restore the Chromium cache without saving it, and only the main-branch warm job saves', () => {
  for (const id of ['browser-pr', 'browser-production']) {
    assert.match(chromiumCache(id), /^actions\/cache\/restore@[0-9a-f]{40}$/, `${id} must restore only`)
    assert.doesNotMatch(job(id), /github\.event_name == 'push'(?! \|\|)/, `${id} is not expected to run on a main push`)
  }
  assert.match(chromiumCache('warm-chromium-cache'), /^actions\/cache@[0-9a-f]{40}$/, 'warm-chromium-cache must save')
  assert.match(job('warm-chromium-cache'), /if: github\.event_name == 'push'/, 'the warm job must run on main pushes')
  // The same key everywhere, so what main saves is what the pull requests restore.
  const keys = new Set([...workflow.matchAll(/path: ~\/AppData\/Local\/ms-playwright\n\s+key: (.+)/g)].map((match) => match[1].trim()))
  assert.deepEqual([...keys], ["playwright-chromium-${{ runner.os }}-${{ hashFiles('product/client/package-lock.json') }}"])
})
