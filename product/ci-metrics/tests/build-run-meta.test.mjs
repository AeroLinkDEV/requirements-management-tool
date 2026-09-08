import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

const binDir = fileURLToPath(new URL('../bin/', import.meta.url))

const ALL_TRUE = { CLASS_DOCS_ONLY: 'false', CLASS_BACKEND: 'true', CLASS_CLIENT: 'true', CLASS_BROWSER: 'true', CLASS_POSTGRESQL: 'true' }

function runMetaEnv(directory, overrides) {
  return {
    ...process.env,
    GITHUB_RUN_ID: '800',
    GITHUB_RUN_ATTEMPT: '1',
    GITHUB_EVENT_NAME: 'pull_request',
    GITHUB_REF: 'refs/pull/1/merge',
    GITHUB_SHA: 'a'.repeat(40),
    GITHUB_WORKFLOW_REF: 'repo/.github/workflows/ci.yml@refs/pull/1/merge',
    GITHUB_REPOSITORY: 'owner/repo',
    METRICS_TREE_SHA: 'b'.repeat(40),
    METRICS_RUN_META_PATH: join(directory, 'run-meta.json'),
    CLASS_DOCS_ONLY: 'false',
    CLASS_BACKEND: 'false',
    CLASS_CLIENT: 'false',
    CLASS_BROWSER: 'false',
    CLASS_POSTGRESQL: 'false',
    FULL_DIAGNOSTICS: 'true',
    PULL_REQUEST_NUMBER: '',
    PULL_REQUEST_BASE_SHA: '',
    PULL_REQUEST_HEAD_SHA: '',
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
  let meta = null
  if (result.status === 0) meta = JSON.parse(readFileSync(join(directory, 'run-meta.json'), 'utf8'))
  return { directory, result, meta }
}

function instances(meta) {
  return meta.expectedJobs.map((job) => job.instance)
}

function assertSelectedExactly(meta, expected) {
  assert.deepEqual(instances(meta), expected)
  assert.equal(new Set(instances(meta)).size, expected.length)
}

test('full pull-request topology has every product instance, exact gate needs, and only event skips', () => {
  const { directory, result, meta } = build(ALL_TRUE)
  try {
    assert.equal(result.status, 0, result.stderr)
    assertSelectedExactly(meta, [
      'changes', 'metrics-tooling',
      'backend-api-1', 'backend-api-2', 'backend-api-3', 'backend-core-domain', 'backend-core-infrastructure',
      'client', 'script-contracts',
      'browser-pr-1', 'browser-pr-2', 'browser-pr-3', 'browser-pr-4',
      'browser-production', 'postgresql-smoke', 'gate',
    ])
    const gate = meta.expectedJobs.find((job) => job.instance === 'gate')
    assert.deepEqual(gate.needs, [
      'changes', 'metrics-tooling', 'backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'client',
      'script-contracts', 'browser-pr', 'browser-production', 'postgresql-smoke',
    ])
    assert.deepEqual(meta.skippedJobs.map((job) => job.instance), [
      'browser-full-1', 'browser-full-2', 'browser-full-3', 'warm-chromium-cache',
    ])
    assert.equal(meta.provenance.mode, 'shadow')
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('docs-only runs still expect changes, metrics-tooling, and gate, with everything else deliberately skipped', () => {
  const { directory, result, meta } = build({ CLASS_DOCS_ONLY: 'true' })
  try {
    assert.equal(result.status, 0, result.stderr)
    assertSelectedExactly(meta, ['changes', 'metrics-tooling', 'gate'])
    assert.deepEqual(meta.expectedJobs.find((job) => job.instance === 'gate').needs, ['changes', 'metrics-tooling'])
    const skipped = meta.skippedJobs.map((job) => job.instance)
    for (const expected of ['backend-api-1', 'backend-core-domain', 'backend-core-infrastructure', 'client', 'script-contracts', 'browser-pr-1', 'browser-production', 'browser-full-1', 'postgresql-smoke', 'warm-chromium-cache']) {
      assert.ok(skipped.includes(expected), `missing deliberate skip ${expected}`)
    }
    assert.ok(meta.skippedJobs.every((job) => job.reason.length > 0))
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('backend-only pull request prunes client/browser/postgres groups from selection and gate needs', () => {
  const { directory, result, meta } = build({ CLASS_BACKEND: 'true' })
  try {
    assert.equal(result.status, 0, result.stderr)
    assertSelectedExactly(meta, [
      'changes', 'metrics-tooling',
      'backend-api-1', 'backend-api-2', 'backend-api-3', 'backend-core-domain', 'backend-core-infrastructure',
      'script-contracts', 'gate',
    ])
    assert.deepEqual(meta.expectedJobs.find((job) => job.instance === 'gate').needs, [
      'changes', 'metrics-tooling', 'backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'script-contracts',
    ])
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('client-only pull request keeps script-contracts but prunes every other product group', () => {
  const { directory, result, meta } = build({ CLASS_CLIENT: 'true' })
  try {
    assert.equal(result.status, 0, result.stderr)
    assertSelectedExactly(meta, ['changes', 'metrics-tooling', 'client', 'script-contracts', 'gate'])
    assert.deepEqual(meta.expectedJobs.find((job) => job.instance === 'gate').needs, [
      'changes', 'metrics-tooling', 'client', 'script-contracts',
    ])
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('merge-group runs the full pull-request product set', () => {
  const { directory, result, meta } = build({ ...ALL_TRUE, GITHUB_EVENT_NAME: 'merge_group', GITHUB_REF: 'refs/heads/main' })
  try {
    assert.equal(result.status, 0, result.stderr)
    assert.ok(instances(meta).includes('browser-pr-1'))
    assert.ok(instances(meta).includes('browser-production'))
    assert.ok(!instances(meta).includes('warm-chromium-cache'))
    assert.equal(meta.provenance.mode, 'shadow')
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('push on main selects the cache warmer, skips browser families, and is trusted', () => {
  const { directory, result, meta } = build({ ...ALL_TRUE, GITHUB_EVENT_NAME: 'push', GITHUB_REF: 'refs/heads/main' })
  try {
    assert.equal(result.status, 0, result.stderr)
    assert.ok(instances(meta).includes('warm-chromium-cache'))
    for (const absent of ['browser-pr-1', 'browser-production', 'browser-full-1']) assert.ok(!instances(meta).includes(absent))
    assert.deepEqual(meta.expectedJobs.find((job) => job.instance === 'gate').needs, [
      'changes', 'metrics-tooling', 'backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'client', 'script-contracts', 'postgresql-smoke',
    ])
    assert.equal(meta.provenance.mode, 'trusted')
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('schedule and default-branch dispatch select the full browser lanes and skip browser-pr/warm', () => {
  for (const event of ['schedule', 'workflow_dispatch']) {
    const { directory, result, meta } = build({ ...ALL_TRUE, GITHUB_EVENT_NAME: event, GITHUB_REF: 'refs/heads/main' })
    try {
      assert.equal(result.status, 0, result.stderr)
      assert.ok(instances(meta).includes('browser-full-1'))
      assert.ok(instances(meta).includes('browser-full-3'))
      assert.ok(meta.expectedJobs.find((job) => job.instance === 'gate').needs.includes('browser-full'),
        'scheduled critical path must wait for the full browser proof')
      assert.ok(instances(meta).includes('browser-production'))
      assert.ok(!instances(meta).includes('browser-pr-1'))
      assert.ok(!instances(meta).includes('warm-chromium-cache'))
      assert.equal(meta.provenance.mode, 'trusted')
    } finally {
      rmSync(directory, { recursive: true, force: true })
    }
  }
})

test('dispatch topology follows its purpose: PR readiness selects browser-pr, diagnostics select browser-full', () => {
  const prDispatch = build({
    ...ALL_TRUE,
    GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REF: 'refs/heads/feature/942',
    PULL_REQUEST_NUMBER: '942',
    PULL_REQUEST_BASE_SHA: 'a'.repeat(40),
    PULL_REQUEST_HEAD_SHA: 'b'.repeat(40),
  })
  try {
    assert.equal(prDispatch.result.status, 0, prDispatch.result.stderr)
    assert.ok(prDispatch.meta.expectedJobs.some((job) => job.instance === 'browser-pr-1'))
    assert.ok(!prDispatch.meta.expectedJobs.some((job) => job.instance === 'browser-full-1'))
    assert.ok(prDispatch.meta.expectedJobs.find((job) => job.instance === 'gate').needs.includes('browser-pr'))
    assert.equal(prDispatch.meta.expectedRun.pr, 942)
    assert.equal(prDispatch.meta.expectedRun.baseSha, 'a'.repeat(40))
    assert.equal(prDispatch.meta.expectedRun.headSha, 'b'.repeat(40))
  } finally {
    rmSync(prDispatch.directory, { recursive: true, force: true })
  }

  const manual = build({ ...ALL_TRUE, GITHUB_EVENT_NAME: 'workflow_dispatch', GITHUB_REF: 'refs/heads/main', FULL_DIAGNOSTICS: 'false' })
  try {
    assert.equal(manual.result.status, 0, manual.result.stderr)
    assert.ok(!manual.meta.expectedJobs.some((job) => job.instance === 'browser-full-1'))
    assert.ok(manual.meta.skippedJobs.some((job) => job.instance === 'browser-full-1'))
    assert.ok(!manual.meta.expectedJobs.find((job) => job.instance === 'gate').needs.includes('browser-full'))
  } finally {
    rmSync(manual.directory, { recursive: true, force: true })
  }
})

test('non-default-branch and PR events can never self-promote to trusted', () => {
  const cases = [
    { overrides: { ...ALL_TRUE, GITHUB_EVENT_NAME: 'push', GITHUB_REF: 'refs/heads/feature/x' }, expected: 'shadow' },
    { overrides: { ...ALL_TRUE, GITHUB_EVENT_NAME: 'workflow_dispatch', GITHUB_REF: 'refs/heads/feature/x' }, expected: 'shadow' },
    { overrides: { ...ALL_TRUE, GITHUB_EVENT_NAME: 'merge_group', GITHUB_REF: 'refs/heads/main' }, expected: 'shadow' },
    { overrides: { ...ALL_TRUE, GITHUB_EVENT_NAME: 'pull_request', GITHUB_REF: 'refs/pull/1/merge' }, expected: 'shadow' },
    { overrides: { ...ALL_TRUE, GITHUB_EVENT_NAME: 'schedule', GITHUB_REF: 'refs/heads/main' }, expected: 'trusted' },
    { overrides: { ...ALL_TRUE, GITHUB_EVENT_NAME: 'push', GITHUB_REF: 'refs/heads/main' }, expected: 'trusted' },
  ]
  for (const entry of cases) {
    const { directory, result, meta } = build(entry.overrides)
    try {
      assert.equal(result.status, 0, result.stderr)
      assert.equal(meta.provenance.mode, entry.expected, JSON.stringify(entry.overrides))
    } finally {
      rmSync(directory, { recursive: true, force: true })
    }
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

test('run metadata refuses to guess topology when any classifier output is missing', () => {
  const directory = mkdtempSync(join(tmpdir(), 'ci-run-meta-'))
  try {
    const env = runMetaEnv(directory, {})
    delete env.CLASS_BACKEND
    const result = spawnSync(process.execPath, [join(binDir, 'build-run-meta.mjs')], {
      encoding: 'utf8',
      cwd: directory,
      env,
    })
    assert.notEqual(result.status, 0)
    assert.match(result.stderr, /expected topology cannot be derived/)
  } finally {
    rmSync(directory, { recursive: true, force: true })
  }
})

test('expectedRun carries PR, base/head SHA, ref, and workflow identity from the event file', () => {
  const eventDirectory = mkdtempSync(join(tmpdir(), 'ci-run-meta-event-'))
  try {
    const eventPath = join(eventDirectory, 'event.json')
    writeFileSync(eventPath, JSON.stringify({
      pull_request: {
        number: 572,
        base: { sha: 'b'.repeat(40) },
        head: { sha: 'c'.repeat(40) },
      },
    }))
    const { directory, result, meta } = build({
      ...ALL_TRUE,
      GITHUB_EVENT_PATH: eventPath,
      GITHUB_REF: 'refs/pull/572/merge',
      GITHUB_WORKFLOW: 'Product quality gate',
    })
    try {
      assert.equal(result.status, 0, result.stderr)
      assert.equal(meta.expectedRun.pr, 572)
      assert.equal(meta.expectedRun.baseSha, 'b'.repeat(40))
      assert.equal(meta.expectedRun.headSha, 'c'.repeat(40))
      assert.equal(meta.expectedRun.ref, 'refs/pull/572/merge')
      assert.equal(meta.expectedRun.workflow, 'Product quality gate')
      assert.equal(meta.expectedRun.workflowRef, 'repo/.github/workflows/ci.yml@refs/pull/1/merge')
    } finally {
      rmSync(directory, { recursive: true, force: true })
    }
  } finally {
    rmSync(eventDirectory, { recursive: true, force: true })
  }
})
