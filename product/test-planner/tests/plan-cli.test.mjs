import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

// Executes the real CLI rather than a copy of its logic. The defects these cover were both in the
// argument handling and the emitted strings — neither of which a test of the library would have caught,
// which is exactly how they reached review.
const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const CLI = join(repoRoot, 'product/test-planner/tools/plan.mjs')

function run(args) {
  try {
    return { code: 0, out: execFileSync(process.execPath, [CLI, ...args], { encoding: 'utf8', cwd: repoRoot }) }
  } catch (error) {
    return { code: error.status ?? 1, out: `${error.stdout ?? ''}${error.stderr ?? ''}` }
  }
}

test('--files stops at the next option instead of swallowing its value', () => {
  // The defect: `--files README.md --event pull_request` classified the literal path `pull_request`,
  // turning a documentation-only change into an unclassified one and printing a plan for work that was
  // not needed.
  const { out } = run(['--files', 'README.md', '--event', 'pull_request', '--json'])
  const parsed = JSON.parse(out)
  assert.deepEqual(parsed.changedPaths, ['README.md'])
  assert.equal(parsed.classification.docsOnly, true)
  assert.equal(parsed.event, 'pull_request')
})

test('option order does not change the result', () => {
  const a = JSON.parse(run(['--event', 'merge_group', '--files', 'README.md', '--json']).out)
  const b = JSON.parse(run(['--files', 'README.md', '--event', 'merge_group', '--json']).out)
  assert.deepEqual(a.changedPaths, b.changedPaths)
  assert.deepEqual(a.classification, b.classification)
  assert.equal(a.event, 'merge_group')
})

test('a broad event still classifies everything through the CLI', () => {
  const parsed = JSON.parse(run(['--files', 'README.md', '--event', 'merge_group', '--json']).out)
  assert.equal(parsed.classification.backend, true)
  assert.equal(parsed.classification.browser, true)
})

test('multiple files are all collected', () => {
  const parsed = JSON.parse(run(['--files', 'README.md', 'product/client/src/App.tsx', '--json']).out)
  assert.deepEqual(parsed.changedPaths, ['README.md', 'product/client/src/App.tsx'])
  assert.equal(parsed.classification.client, true)
})

test('an option missing its value is refused rather than guessed', () => {
  for (const args of [['--event'], ['--base'], ['--files', 'a.cs', '--event']]) {
    const { code, out } = run(args)
    assert.equal(code, 2, `${args.join(' ')} should exit 2`)
    assert.match(out, /requires a value/)
  }
})

test('an unknown option or a bare argument is refused', () => {
  const unknown = run(['--wat'])
  assert.equal(unknown.code, 2)
  assert.match(unknown.out, /Unknown option/)

  const bare = run(['README.md'])
  assert.equal(bare.code, 2)
  assert.match(bare.out, /must start with --/)
})

test('paths beginning with a dash can be passed after a bare --', () => {
  // `--` is POSIX: everything after it is positional. So other options must come *before* it, which is
  // why `--json` leads here. Writing this test the other way round was my own error, and it is worth
  // keeping the correct ordering visible since the trap is easy to fall into again.
  const parsed = JSON.parse(run(['--json', '--files', '--', '-weird-name.cs']).out)
  assert.ok(parsed.changedPaths.includes('-weird-name.cs'), 'a dash-leading path must survive')
  assert.equal(parsed.changedPaths.length, 1, 'and nothing else is absorbed with it')
})

test('the emitted backend commands run one target each', () => {
  const parsed = JSON.parse(run(['--files', 'product/src/AeroLink.Domain/X.cs', '--json']).out)
  const commands = parsed.local.map((step) => step.command).filter(Boolean).filter((c) => c.startsWith('dotnet test'))
  assert.ok(commands.length >= 2)
  for (const command of commands) {
    const targets = command.replace(/^dotnet test\s+/, '').split(/\s+/).filter((t) => !t.startsWith('--') && t !== 'Release')
    assert.equal(targets.length, 1, `"${command}" passes ${targets.length} targets`)
  }
})

test('the CI forecast in JSON comes from the workflow', () => {
  const parsed = JSON.parse(run(['--files', 'product/client/src/App.tsx', '--json']).out)
  assert.ok(Array.isArray(parsed.ci.selected), 'the forecast is a derived selection, not a static list')
  assert.ok(parsed.ci.selected.some((job) => /Client lint/.test(job.name ?? job.id)))
  assert.ok(parsed.ci.skipped.some((job) => /API test suite/.test(job.name ?? job.id)))
})
