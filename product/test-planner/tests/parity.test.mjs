import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const planner = join(repoRoot, 'product/test-planner/tools/plan.mjs')
const ciClassifier = join(repoRoot, 'product/test-planner/tools/classify-ci.mjs')

const EVENTS = ['pull_request', 'merge_group', 'push', 'schedule', 'workflow_dispatch']

// These rows deliberately mix real repository surfaces with synthetic product paths. Each row is built
// into a disposable Git repository, then both entry points consume the same base/head diff. Keeping this
// fixture in the tree makes the local/Actions parity claim reproducible instead of relying on a review
// session's uncommitted differential script.
const CASES = [
  { name: 'documentation', base: ['README.md'], head: ['README.md', 'docs/OPERATIONS.md'] },
  { name: 'backend', base: ['README.md'], head: ['product/src/AeroLink.Domain/Rules/Rule.cs'] },
  { name: 'client', base: ['README.md'], head: ['product/client/src/App.tsx'] },
  { name: 'browser', base: ['README.md'], head: ['product/client/tests/requirement.spec.ts'] },
  { name: 'postgresql', base: ['README.md'], head: ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_init.cs'] },
  { name: 'mixed', base: ['README.md'], head: ['product/client/src/App.tsx', 'product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_init.cs', 'product/scripts/operator.ps1'] },
  { name: 'workflow', base: ['README.md'], head: ['.github/workflows/ci.yml'] },
  { name: 'unknown-product', base: ['README.md'], head: ['product/new-tooling/unknown-format.xyz'] },
  { name: 'root-script', base: ['README.md'], head: ['START_AEROLINK_PRODUCTION.bat'] },
  { name: 'rename-sensitive-path', base: ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_old.cs'], head: ['product/src/AeroLink.Domain/Rules/0001_new.cs'], rename: true },
  { name: 'deletion-sensitive-path', base: ['README.md', 'product/src/AeroLink.Infrastructure/Persistence/Migrations/Deleted.cs'], head: ['README.md'], deletion: true },
  { name: 'nested-lookalike', base: ['README.md'], head: ['product/src/docs/DocumentationLoader.cs'] },
]

function git(cwd, args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim()
}

function writeFixtureFile(root, path, content) {
  const fullPath = join(root, ...path.split('/'))
  mkdirSync(dirname(fullPath), { recursive: true })
  writeFileSync(fullPath, content)
}

function createFixture(row) {
  const root = mkdtempSync(join(tmpdir(), 'aerolink-planner-parity-'))
  git(root, ['init', '--quiet'])
  git(root, ['config', 'user.email', 'aerolink-planner@example.invalid'])
  git(root, ['config', 'user.name', 'AeroLink planner parity'])

  for (const path of row.base) writeFixtureFile(root, path, row.rename ? 'same rename content\n' : `base:${path}\n`)
  git(root, ['add', '--all'])
  git(root, ['commit', '--quiet', '-m', 'base'])
  const base = git(root, ['rev-parse', 'HEAD'])

  for (const path of row.base) {
    if (!row.head.includes(path)) rmSync(join(root, ...path.split('/')), { force: true })
  }
  for (const path of row.head) writeFixtureFile(root, path, row.rename ? 'same rename content\n' : `head:${path}\n`)
  git(root, ['add', '--all'])
  git(root, ['commit', '--quiet', '-m', 'head'])
  const head = git(root, ['rev-parse', 'HEAD'])
  return { root, base, head }
}

function parseOutput(path) {
  const result = {}
  for (const line of readFileSync(path, 'utf8').split(/\r?\n/).filter(Boolean)) {
    const separator = line.indexOf('=')
    assert.ok(separator > 0, `classifier emitted malformed output line: ${line}`)
    result[line.slice(0, separator)] = line.slice(separator + 1)
  }
  return result
}

function normalizedJobs(jobs, selected) {
  return jobs.map((job) => ({
    id: job.id,
    name: job.name ?? job.id,
    reason: job.always ? 'always-running reporting job' : `condition ${selected ? 'matched' : 'not matched'}: ${job.condition ?? 'none'}`,
  }))
}

function invokePlan(fixture, event) {
  const output = execFileSync(process.execPath, [planner, '--base', fixture.base, '--head', fixture.head, '--event', event, '--json', '--dry-run'], {
    cwd: fixture.root,
    encoding: 'utf8',
  })
  return JSON.parse(output)
}

function invokeCiClassifier(fixture, event) {
  const outputPath = join(fixture.root, `github-output-${event}.txt`)
  execFileSync(process.execPath, [ciClassifier], {
    cwd: fixture.root,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, EVENT_NAME: event, BASE_SHA: fixture.base, HEAD_SHA: fixture.head, GITHUB_OUTPUT: outputPath },
  })
  return parseOutput(outputPath)
}

test('local planner and CI classifier agree on the same path/event matrix', () => {
  for (const row of CASES) {
    const fixture = createFixture(row)
    try {
      for (const event of EVENTS) {
        const local = invokePlan(fixture, event)
        const ci = invokeCiClassifier(fixture, event)
        const expectedAreas = ['docs_only', 'backend', 'client', 'browser', 'postgresql']
        for (const area of expectedAreas) assert.equal(String(local.classification[area === 'docs_only' ? 'docsOnly' : area]), ci[area], `${row.name}/${event}/${area}`)
        assert.equal(local.classification.reason ?? '', ci.planner_reason, `${row.name}/${event}/reason`)
        assert.deepEqual(local.compact.unknownPaths, ci.planner_unknown_paths ? ci.planner_unknown_paths.split(', ').filter(Boolean) : [], `${row.name}/${event}/unknown paths`)
        assert.equal(local.compact.planner.version, ci.planner_version, `${row.name}/${event}/planner version`)
        assert.equal(local.compact.planner.hash, ci.planner_hash, `${row.name}/${event}/planner hash`)
        assert.deepEqual(normalizedJobs(local.ci.selected, true), JSON.parse(ci.planner_decisions).selected, `${row.name}/${event}/selected decisions`)
        assert.deepEqual(normalizedJobs(local.ci.skipped, false), JSON.parse(ci.planner_decisions).skipped, `${row.name}/${event}/skipped decisions`)
      }
    } finally {
      rmSync(fixture.root, { recursive: true, force: true })
    }
  }
})

test('parity fixture is tracked and includes real and synthetic surfaces', () => {
  assert.ok(existsSync(join(repoRoot, 'product/test-planner/tests/parity.test.mjs')))
  assert.ok(CASES.some((row) => row.name === 'unknown-product'))
  assert.ok(CASES.some((row) => row.name === 'rename-sensitive-path'))
  assert.ok(CASES.some((row) => row.name === 'postgresql'))
})
