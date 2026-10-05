import { test } from 'node:test'
import assert from 'node:assert/strict'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const manifestPath = join(repoRoot, 'product/test-planner/fast-ci-manifest.json')
const workflowPath = join(repoRoot, '.github/workflows/fast-pr-feedback.yml')
const fullWorkflowPath = join(repoRoot, '.github/workflows/ci.yml')
const requesterWorkflowPath = join(repoRoot, '.github/workflows/request-full-ci.yml')
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
const workflow = readFileSync(workflowPath, 'utf8')
const fullWorkflow = readFileSync(fullWorkflowPath, 'utf8')
const requesterWorkflow = readFileSync(requesterWorkflowPath, 'utf8')

test('Fast phase 1 is explicitly advisory, versioned and bounded', () => {
  assert.equal(manifest.schemaVersion, 1)
  assert.equal(manifest.id, 'aerolink-fast-ci/v4')
  assert.equal(manifest.authoritative, false)
  assert.equal(manifest.targetMs, 240000)
  assert.equal(manifest.safety.persistentPostgreSql, 'forbidden')
  assert.equal(manifest.safety.persistentEvidenceRoot, 'forbidden')
  assert.match(manifest.safety.mergeAuthority, /existing Product quality gate remains the only merge authority/i)

  assert.match(workflow, /^name: Fast PR feedback \(advisory\)$/m)
  assert.match(workflow, /pull_request:/)
  assert.doesNotMatch(workflow, /^\s*push:/m)
  assert.match(workflow, /group: fast-pr-/)
  assert.match(workflow, /cancel-in-progress: true/)
  assert.match(workflow, /fast-ci-manifest\.json/)
  assert.match(workflow, /Fast feedback is advisory/i)
  assert.doesNotMatch(workflow, /Report what this run validated/)
  assert.match(fullWorkflow, /Full Product evidence aggregate/)
  assert.match(requesterWorkflow, /Report what this run validated/)
})

test('Fast backend manifest names only reviewed source-controlled smoke classes', () => {
  assert.equal(manifest.backend.domainProject, 'product/tests/AeroLink.Domain.Tests/AeroLink.Domain.Tests.csproj')
  assert.deepEqual(manifest.backend.infrastructureClasses, [
    'ArtifactScopingTests',
    'BaselinePersistenceTests',
    'ConcurrencyTests',
    'IdentityPersistenceTests',
    'MigrationRegistrationTests',
  ])
  assert.deepEqual(manifest.backend.apiClasses, ['SharedHostIsolationTests'])

  for (const className of manifest.backend.infrastructureClasses) {
    assert.equal(
      existsSync(join(repoRoot, `product/tests/AeroLink.Infrastructure.Tests/${className}.cs`)),
      true,
      `Fast Infrastructure class is not source-controlled: ${className}`,
    )
  }
  for (const className of manifest.backend.apiClasses) {
    assert.equal(
      existsSync(join(repoRoot, `product/tests/AeroLink.Api.Tests/${className}.cs`)),
      true,
      `Fast API class is not source-controlled: ${className}`,
    )
  }
})

test('Fast client adds explicit isolated behavior checks and Full retains heavyweight evidence', () => {
  assert.deepEqual(manifest.client.commands, [
    'npm ci', 'npm run lint', 'npm run typecheck', 'npm run test:fast:routes',
    'npm run test:fast:logic', 'npx playwright install chromium', 'npm run test:fast:isolation',
    'npm run test:fast:rendered',
  ])
  assert.equal(manifest.client.workingDirectory, 'product/client')
  assert.doesNotMatch(JSON.stringify(manifest.client), /test:smoke|test:production/i)
  for (const command of manifest.client.commands) assert.ok(workflow.includes(`'${command}' { ${command} }`))
  assert.match(workflow, /name: Retain Fast client discovery and failure diagnostics\s+if: always\(\)/)

  const fullOnly = manifest.fullOnlyEvidence.join('\n')
  for (const expected of ['complete API suite', 'complete infrastructure suite', 'PostgreSQL', 'production-browser', 'full browser']) {
    assert.match(fullOnly, new RegExp(expected, 'i'))
  }
})

test('Fast runs the Node contract suites that Full owns, and its aggregate fails when they fail', () => {
  // #1152 B1: the hosted boundary contracts failed ten Full runs in two weeks while Fast stayed green.
  assert.deepEqual(manifest.contracts.suites, ['product/test-contracts/tests', 'product/test-planner/tests'])
  for (const suite of manifest.contracts.suites) {
    assert.ok(workflow.includes(`'${suite}' { }`), `Fast workflow has no reviewed arm for ${suite}`)
    assert.ok(readdirSync(join(repoRoot, suite)).some((name) => name.endsWith('.test.mjs')), `${suite} has no contract tests`)
    // Additive only: Full must keep running the same directory.
    assert.ok(fullWorkflow.includes(`-LiteralPath ${suite} -Filter '*.test.mjs'`), `Full no longer runs ${suite}`)
  }
  assert.ok(workflow.includes('needs: [backend-fast, client-fast, contracts-fast]'), 'the Fast aggregate does not wait for every job')
  assert.ok(workflow.includes("$contracts -ne 'success'"), 'a contracts failure does not fail the Fast aggregate')
  // A matrix job's result is a failure when any leg fails, so one client check covers every shard.
  assert.ok(workflow.includes("$client -ne 'success'"), 'a failed client shard does not fail the Fast aggregate')
  assert.ok(workflow.includes('($backendMs, $clientMs, $contractsMs | Measure-Object -Maximum).Maximum'), 'a job is outside the Fast budget')
  // Every client shard's time is in the budget, and a shard that reported none fails the aggregate (#1456).
  assert.ok(workflow.includes('$clientMs = if ($legs.Count -gt 0) { ($legs.Values | Measure-Object -Maximum).Maximum } else { 0 }'), 'a client shard is outside the Fast budget')
  assert.ok(workflow.includes('if ($missing.Count -gt 0) { throw'), 'a client shard without a time does not fail the Fast aggregate')
})

test('Fast workflow contains no persistent-database or persistent-evidence escape hatch', () => {
  assert.doesNotMatch(workflow, /54329/)
  assert.doesNotMatch(workflow, /ConnectionStrings__AeroLink|Database__Provider|postgres:17/i)
  assert.doesNotMatch(workflow, /docker\s+(run|compose)|Start-Postgres/i)

  const persistentEvidenceMentions = workflow.match(/product[\\/]\.local/gi) ?? []
  assert.equal(persistentEvidenceMentions.length, 1, 'Fast workflow may mention product/.local only in its explicit safety statement.')
  assert.match(workflow, /persistent PostgreSQL and product\/.local are forbidden/i)
  assert.doesNotMatch(
    workflow,
    /(?:Get|Set|Remove|New|Test)-(?:Item|Content|ChildItem|Path)[^\n]*product[\\/]\.local|(?:path|working-directory):[^\n]*product[\\/]\.local/i,
  )
})

// #1313: one serial client job needed 10.3–15 minutes and hit its 10-minute limit on every PR. It now runs in parts,
// and the test parts in shards (#1456, #1232), and nothing may fall out of the split: every manifest command runs in
// some part, every shard of every part is exactly one matrix leg, only the test runners are sharded, and the two
// rendered parts divide the rendered tier's specs between them.
test('Fast client parts together run every client command, and split the rendered tier without losing a spec', () => {
  const parts = manifest.client.parts
  assert.deepEqual(Object.keys(parts), ['static', 'logic', 'rendered-standard', 'rendered-cdu', 'rendered-3d'])
  const run = new Set(Object.values(parts).flatMap((part) => part.commands))
  assert.deepEqual([...run].sort(), [...manifest.client.commands].sort(), 'the parts must run exactly the manifest commands')
  const legs = [...workflow.matchAll(/^\s+- \{ part: ([a-z0-9-]+), shard: (\d+), shards: (\d+) \}$/gm)].map((m) => `${m[1]} ${m[2]}/${m[3]}`)
  const expectedLegs = Object.entries(parts).flatMap(([name, part]) => Array.from({ length: part.shards }, (_, i) => `${name} ${i + 1}/${part.shards}`))
  assert.deepEqual(legs, expectedLegs, 'the client matrix must run every shard of every manifest part exactly once')
  assert.ok(workflow.includes('$part = $manifest.client.parts.$($env:FAST_PART)'), 'the client job does not read its part from the manifest')
  assert.ok(workflow.includes('if ([int]$part.shards -ne [int]$env:FAST_SHARDS) { throw'), 'the client job does not check its shard count against the manifest')
  for (const [name, part] of Object.entries(parts)) {
    assert.ok(Number.isInteger(part.shards) && part.shards >= 1, `${name} must name its shard count`)
    for (const command of part.commands) assert.ok(manifest.client.commands.includes(command), `${name} runs an unreviewed command: ${command}`)
    assert.equal(part.commands[0], 'npm ci', `${name} must install first`)
    // A sharded part runs one test runner, so nothing but its tests is split or repeated across legs.
    if (part.shards > 1) {
      const runners = part.commands.filter((command) => command === 'npm run test:fast:logic' || command === 'npm run test:fast:rendered')
      assert.equal(runners.length, 1, `${name} is sharded, so it must run exactly one test runner`)
      assert.deepEqual(part.commands.filter((command) => !runners.includes(command) && command !== 'npm ci' && command !== 'npx playwright install chromium'), [],
        `${name} is sharded, so it may only install and run its tests`)
    }
  }
  // Rendered parts take Playwright's count-based shard; logic packs whole files by recorded duration (#1456).
  const renderedSource = readFileSync(join(repoRoot, 'product/client/playwright.rendered.config.ts'), 'utf8')
  assert.match(renderedSource, /shard: fastShard\(\),/, 'the rendered config does not take the Fast shard')
  const logicSource = readFileSync(join(repoRoot, 'product/client/playwright.logic.config.ts'), 'utf8')
  assert.match(logicSource, /testMatch: shard \? packedFiles\(tiers\.logic, durations, shard\) : tiers\.logic,/, 'the logic config does not pack its Fast shard')
  assert.doesNotMatch(logicSource, /shard: fastShard\(\)/, 'the logic config must not also take a count-based shard')
  for (const source of [renderedSource, logicSource]) assert.match(source, /fullyParallel: true,/, "a Fast config must run fully parallel, or one long file sets a shard's time")
  // Each rendered part runs the rendered tier with its own share of it; lint, types, routes, logic and isolation run once.
  assert.deepEqual(Object.values(parts).filter((part) => part.commands.includes('npm run test:fast:rendered')).map((part) => part.renderedPart).sort(), ['3d', 'cdu', 'standard'])
  for (const once of ['npm run lint', 'npm run typecheck', 'npm run test:fast:routes', 'npm run test:fast:logic', 'npm run test:fast:isolation']) {
    assert.equal(Object.values(parts).filter((part) => part.commands.includes(once)).length, 1, `${once} must run in exactly one part`)
  }
  const tiers = JSON.parse(readFileSync(join(repoRoot, 'product/client/fast-client-tests.json'), 'utf8'))
  const config = readFileSync(join(repoRoot, 'product/client/playwright.rendered.config.ts'), 'utf8')
  const list = (name) => JSON.parse(config.match(new RegExp(`export const ${name} = (\\[[^\\]]*\\])`))[1].replace(/'/g, '"'))
  const heavy = list('RENDERED_3D')
  const cdu = list('RENDERED_CDU')
  for (const [part, specs] of [['3d', heavy], ['cdu', cdu]]) {
    assert.ok(specs.length > 0, `the ${part} part must have specs`)
    for (const spec of specs) assert.ok(tiers.rendered.includes(spec), `the ${part} part names a spec outside the rendered tier: ${spec}`)
  }
  assert.ok(!heavy.some((spec) => cdu.includes(spec)), 'a spec must not run in two rendered parts')
  assert.ok(tiers.rendered.some((spec) => !heavy.includes(spec) && !cdu.includes(spec)), 'the standard part must have specs')
  assert.match(config, /part === '3d' \? tiers\.rendered\.filter\(\(file\) => RENDERED_3D\.includes\(file\)\)/)
  assert.match(config, /part === 'cdu' \? tiers\.rendered\.filter\(\(file\) => RENDERED_CDU\.includes\(file\)\)/)
  assert.match(config, /part === 'standard' \? tiers\.rendered\.filter\(\(file\) => !RENDERED_3D\.includes\(file\) && !RENDERED_CDU\.includes\(file\)\)/)
})

// #1456: the logic shards are whole files packed by recorded duration. Every shard computes the same assignment, so
// for any shard count each logic file runs in exactly one shard, recorded or not, and the recorded times come out even.
test('Fast logic shards run every logic file exactly once, and the recorded durations pack them evenly', async () => {
  const { packedFiles } = await import(new URL('../../client/fast-shard.ts', import.meta.url))
  const tiers = JSON.parse(readFileSync(join(repoRoot, 'product/client/fast-client-tests.json'), 'utf8'))
  const durations = JSON.parse(readFileSync(join(repoRoot, 'product/client/fast-logic-durations.json'), 'utf8'))
  const oneUnknown = { ...durations }
  delete oneUnknown[tiers.logic[0]]
  for (const [label, weights] of [['recorded', durations], ['none recorded', {}], ['one unknown', oneUnknown]]) {
    for (const total of [1, 2, manifest.client.parts.logic.shards, 7]) {
      const shards = Array.from({ length: total }, (_, i) => packedFiles(tiers.logic, weights, { current: i + 1, total }))
      assert.deepEqual(shards.flat().sort(), [...tiers.logic].sort(), `${label}, ${total} shards: every logic file runs exactly once`)
    }
  }
  for (const file of Object.keys(durations)) assert.ok(tiers.logic.includes(file), `fast-logic-durations.json names a file outside the logic tier: ${file}`)
  // Balance is an optimisation, but a stale or hand-edited file that packs badly should fail here, not in hosted time.
  const total = manifest.client.parts.logic.shards
  const loads = Array.from({ length: total }, (_, i) => packedFiles(tiers.logic, durations, { current: i + 1, total })
    .reduce((sum, file) => sum + (durations[file] ?? 0), 0))
  const all = Object.values(durations).reduce((sum, value) => sum + value, 0)
  assert.ok(Math.max(...loads) <= Math.max(all / total * 1.1, ...Object.values(durations)), `the logic shards are uneven: ${loads.map(Math.round).join(', ')}`)
})
