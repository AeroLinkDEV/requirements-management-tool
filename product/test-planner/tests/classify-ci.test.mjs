import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const CLASSIFIER = join(repoRoot, 'product/test-planner/tools/classify-ci.mjs')

function git(cwd, args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim()
}

function blob(cwd, content) {
  return execFileSync('git', ['hash-object', '-w', '--stdin'], { cwd, input: content, encoding: 'utf8' }).trim()
}

function tree(cwd, entries) {
  const input = Buffer.concat(entries.map(({ mode, type, object, path }) => Buffer.concat([
    Buffer.from(`${mode} ${type} ${object}`),
    Buffer.from([9]),
    Buffer.from(path),
    Buffer.from([0]),
  ])))
  return execFileSync('git', ['mktree', '-z'], { cwd, input, encoding: 'utf8' }).trim()
}

function commitTree(cwd, treeObject, message, parent = null) {
  const args = ['commit-tree', treeObject]
  if (parent) args.push('-p', parent)
  args.push('-m', message)
  return git(cwd, args)
}

function parseOutput(output) {
  const lines = output.trimEnd().split('\n')
  const keys = lines.map((line) => line.slice(0, line.indexOf('=')))
  assert.ok(lines.every((line) => /^[A-Za-z_][A-Za-z0-9_]*=/.test(line)), `every output record must be one assignment: ${output}`)
  assert.equal(new Set(keys).size, keys.length, 'an output file must not contain duplicate assignments')
  return Object.fromEntries(lines.map((line) => {
    const separator = line.indexOf('=')
    return [line.slice(0, separator), line.slice(separator + 1)]
  }))
}

test('PR-controlled path and reason newlines cannot inject or under-select GitHub outputs', () => {
  const temporaryRoot = mkdtempSync(join(tmpdir(), 'aerolink-classify-ci-'))
  try {
    git(temporaryRoot, ['init', '--quiet'])
    git(temporaryRoot, ['config', 'user.email', 'aerolink-tests@example.invalid'])
    git(temporaryRoot, ['config', 'user.name', 'AeroLink planner tests'])

    // Both names are legal Git paths. The first is unclassified; the second is a broad planner path whose
    // text is copied into classify()'s human-readable reason. The injected-looking assignments must remain
    // escaped data rather than becoming additional GITHUB_OUTPUT records.
    const unknownPath = 'unknown\ndocs_only=true\nbackend=false\nclient=false\nbrowser=false\npostgresql=false'
    const broadPath = 'broad\nplanner_reason=forged\u0001'

    // Windows cannot check out a CR/LF-bearing filename through the Win32 API, but Git trees can contain
    // one and `git diff -z` must still preserve it. Build the two commits directly from tree objects so this
    // regression exercises the same path stream the Actions classifier receives without touching a checkout.
    const readmeBlob = blob(temporaryRoot, 'fixture\n')
    const unknownBlob = blob(temporaryRoot, 'unknown\n')
    const broadBlob = blob(temporaryRoot, 'broad\n')
    const baseTree = tree(temporaryRoot, [{ mode: '100644', type: 'blob', object: readmeBlob, path: 'README.md' }])
    const baseSha = commitTree(temporaryRoot, baseTree, 'base')
    const unknownLeaf = tree(temporaryRoot, [{ mode: '100644', type: 'blob', object: unknownBlob, path: unknownPath }])
    const broadLeaf = tree(temporaryRoot, [{ mode: '100644', type: 'blob', object: broadBlob, path: broadPath }])
    const newToolingTree = tree(temporaryRoot, [{ mode: '040000', type: 'tree', object: unknownLeaf, path: 'new-tooling' }])
    const plannerTree = tree(temporaryRoot, [{ mode: '040000', type: 'tree', object: broadLeaf, path: 'test-planner' }])
    const productTree = tree(temporaryRoot, [
      { mode: '040000', type: 'tree', object: newToolingTree, path: 'new-tooling' },
      { mode: '040000', type: 'tree', object: plannerTree, path: 'test-planner' },
    ])
    const headTree = tree(temporaryRoot, [
      { mode: '100644', type: 'blob', object: readmeBlob, path: 'README.md' },
      { mode: '040000', type: 'tree', object: productTree, path: 'product' },
    ])
    const headSha = commitTree(temporaryRoot, headTree, 'newline paths', baseSha)
    const outputPath = join(temporaryRoot, 'github-output.txt')

    const environment = {
      ...process.env,
      EVENT_NAME: 'pull_request',
      BASE_SHA: baseSha,
      HEAD_SHA: headSha,
      GITHUB_OUTPUT: outputPath,
    }
    execFileSync(process.execPath, [CLASSIFIER], { cwd: temporaryRoot, env: environment, encoding: 'utf8' })

    const output = parseOutput(readFileSync(outputPath, 'utf8'))
    assert.equal(output.docs_only, 'false')
    for (const area of ['backend', 'client', 'browser', 'postgresql']) assert.equal(output[area], 'true', area)
    assert.match(output.planner_unknown_paths, /\\u000a/)
    assert.match(output.planner_reason, /\\u000a/)
    assert.match(output.planner_reason, /\\u0001/)
    assert.doesNotMatch(readFileSync(outputPath, 'utf8'), /^docs_only=true$/m)
    assert.doesNotMatch(readFileSync(outputPath, 'utf8'), /^backend=false$/m)
    assert.doesNotMatch(readFileSync(outputPath, 'utf8'), /^planner_reason=forged$/m)
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true })
  }
})
