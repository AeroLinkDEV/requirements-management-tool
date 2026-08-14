import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

const binDir = fileURLToPath(new URL('../bin/', import.meta.url))
const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))

function runMetaEnv(directory, overrides) {
  return {
    ...process.env,
    GITHUB_RUN_ID: '800',
    GITHUB_RUN_ATTEMPT: '1',
    GITHUB_EVENT_NAME: 'pull_request',
    GITHUB_SHA: 'a'.repeat(40),
    GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/heads/main',
    GITHUB_REPOSITORY: 'owner/repo',
    METRICS_TREE_SHA: 'b'.repeat(40),
    METRICS_RUN_META_PATH: join(directory, 'run-meta.json'),
    ...overrides,
  }
}

function build(overrides) {
  const directory = mkdtempSync(join(tmpdir(), 'ci-run-meta-'))
  const result = spawnSync(process.execPath, [join(binDir, 'build-run-meta.mjs')], {
    encoding: 'utf8',
    cwd: directory,
    env: runMetaEnv(directory, overrides),
  })
  return { directory, result }
}

test('full backend+browser+postgres pull-request topology is produced with unique instances', () => {
  const { directory, result } = build({ CLASS_DOCS_ONLY: 'false', CLASS_BACKEND: 'true', CLASS_CLIENT: 'true', CLASS_BROWSER: 'true', CLASS_POSTGRESQL: 'true' })
  try {
    assert.equal(result.status, 0, result.stderr)
    const meta = JSON.parse(readFileSync(join(directory, 'run-meta.json'), 'utf8'))
    assert.equal(meta.expectedRun.tree, 'b'.repeat(40))
    const instances = meta.expectedJobs.map((job) => job.instance)
    for (const expected of ['changes', 'backend-api-1', 'backend-api-2', 'backend-api-3', 'backend-core', 'client',
      'browser-pr-1', 'browser-pr-2', 'browser-pr-3', 'browser-pr-4', 'browser-production',
      'postgresql-smoke', 'script-contracts', 'gate', 'metrics-tooling']) {
      assert.ok(instances.includes(expected), `missing ${expected}`)
    }
    assert.equal(new Set(instances).size, instances.length)
    const gate = meta.expectedJobs.find((job) => job.instance === 'gate')
    assert.ok(gate.needs.includes('browser-pr'))
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('docs-only runs expect only changes and metrics-tooling; push adds the cache warmer; dispatch adds full browser', () => {
  const docs = build({ CLASS_DOCS_ONLY: 'true' })
  try {
    const meta = JSON.parse(readFileSync(join(docs.directory, 'run-meta.json'), 'utf8'))
    assert.deepEqual(meta.expectedJobs.map((job) => job.instance), ['metrics-tooling'])
  } finally {
    rmSync(docs.directory, { recursive: true, force: true })
  }

  const push = build({ CLASS_DOCS_ONLY: 'false', GITHUB_EVENT_NAME: 'push' })
  try {
    const meta = JSON.parse(readFileSync(join(push.directory, 'run-meta.json'), 'utf8'))
    assert.ok(meta.expectedJobs.some((job) => job.instance === 'warm-chromium-cache'))
  } finally {
    rmSync(push.directory, { recursive: true, force: true })
  }

  const dispatch = build({ CLASS_DOCS_ONLY: 'false', GITHUB_EVENT_NAME: 'workflow_dispatch' })
  try {
    const meta = JSON.parse(readFileSync(join(dispatch.directory, 'run-meta.json'), 'utf8'))
    assert.ok(meta.expectedJobs.some((job) => job.instance === 'browser-full-3'))
  } finally {
    rmSync(dispatch.directory, { recursive: true, force: true })
  }
})

test('run metadata requires a valid exact tree SHA', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-run-meta-'))
  try {
    const result = spawnSync(process.execPath, [join(binDir, 'build-run-meta.mjs')], {
      encoding: 'utf8',
      cwd: directory,
      env: runMetaEnv(directory, { METRICS_TREE_SHA: 'not-a-sha' }),
    })
    assert.notEqual(result.status, 0)
    assert.match(result.stderr, /missing or malformed/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})
