import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, existsSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { classify, explain, localPlan, selectJobs, AREA_PATTERNS, BROAD_EVENTS, normalizePath } from '../lib/classify.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))

const of = (paths, event = 'pull_request') => classify(paths, { event })

test('broad events classify every area without a diff', () => {
  // A merge-group event carries no base to diff against, and it is the last gate before main. The same
  // applies to push, schedule and dispatch, which have no pull-request base either.
  for (const event of BROAD_EVENTS) {
    const result = of(['README.md'], event)
    assert.equal(result.docsOnly, false, event)
    for (const area of ['backend', 'client', 'browser', 'postgresql']) {
      assert.equal(result[area], true, `${event} must select ${area}`)
    }
  }
})

test('documentation-only changes select nothing', () => {
  const result = of([
    'README.md',
    'product/docs/OPERATIONS.md',
    'docs/whatever.txt',
    'design/mockup.png',
    'showcase/demo.gif',
  ])
  assert.equal(result.docsOnly, true)
  assert.equal(result.backend, false)
  assert.equal(result.client, false)
  assert.equal(result.browser, false)
  assert.equal(result.postgresql, false)
  assert.deepEqual(localPlan(result).map((s) => s.label), ['Nothing'])
})

test('a workflow change selects every area, including backend and client', () => {
  // The regression this exists for: ci.yml keyed browser and postgresql but not backend and client, so
  // a change to how the backend tests run did not run the backend tests. It was only caught because an
  // unrelated merge exposed it.
  const result = of(['.github/workflows/ci.yml'])
  assert.equal(result.backend, true, 'a workflow change must run the backend suites')
  assert.equal(result.client, true, 'a workflow change must validate the client')
  assert.equal(result.browser, true)
  assert.equal(result.postgresql, true)
  assert.equal(result.unclassified, false, 'the workflow is recognised, not a fallback')
})

test('planner and shared build changes force every area', () => {
  for (const path of [
    'product/test-planner/lib/classify.mjs',
    'product/test-planner/tools/plan.mjs',
    'product/test-contracts/tests/inventory.test.mjs',
    'product/Directory.Build.props',
    'product/client/package-lock.json',
    '.github/workflows/pr-overlap.yml',
    'product/src/AeroLink.Domain/Contracts/RequirementDto.cs',
    'product/src/AeroLink.Domain/RequirementDto.cs',
    'product/src/AeroLink.Domain/RequirementContract.cs',
  ]) {
    const result = of([path])
    for (const area of ['backend', 'client', 'browser', 'postgresql']) assert.equal(result[area], true, `${path} must select ${area}`)
    assert.equal(result.broad, true, `${path} must be marked broad`)
  }
})

test('Windows separators and case are normalized before matching', () => {
  assert.equal(normalizePath('.\\PRODUCT\\SRC\\AEROLINK.INFRASTRUCTURE\\PERSISTENCE\\Thing.cs'), 'product/src/aerolink.infrastructure/persistence/thing.cs')
  const result = of(['.\\PRODUCT\\SRC\\AEROLINK.INFRASTRUCTURE\\PERSISTENCE\\Thing.cs'])
  assert.equal(result.backend, true)
  assert.equal(result.browser, true)
  assert.equal(result.postgresql, true)
})

test('legal path whitespace is preserved and cannot turn an unknown file into documentation', () => {
  assert.equal(normalizePath(' docs/changed.cs '), ' docs/changed.cs ')
  const result = of([' docs/changed.cs '])
  assert.equal(result.docsOnly, false)
  assert.equal(result.unclassified, true)
  for (const area of ['backend', 'client', 'browser', 'postgresql']) assert.equal(result[area], true, area)
})

test('nested product docs/design/showcase lookalikes remain product paths', () => {
  for (const path of [
    'product/src/docs/DocumentationLoader.cs',
    'product/client/src/showcase/ShowcasePanel.tsx',
    'product/src/AeroLink.Api/design/DesignPreview.cs',
  ]) {
    const result = of([path])
    assert.equal(result.docsOnly, false, path)
    assert.equal(result.unclassified || result.backend || result.client, true, path)
  }
})

test('a rename keeps both old and new sensitive paths in the supplied fixture', () => {
  const result = of([
    'product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_old.cs',
    'product/src/AeroLink.Domain/Rules/0001_new.cs',
  ])
  assert.equal(result.postgresql, true, 'the old migration path must continue selecting PostgreSQL')
  assert.equal(result.browser, true, 'the new domain path must select browser validation')
})

test('backend, client and browser select on their own paths', () => {
  const backend = of(['product/src/AeroLink.Domain/ChangeControl/SystemChangeRequest.cs'])
  assert.equal(backend.backend, true)
  assert.equal(backend.client, false)
  assert.equal(backend.browser, true, 'a domain change can alter what a journey sees')

  const client = of(['product/client/src/App.tsx'])
  assert.equal(client.client, true)
  assert.equal(client.browser, true)
  assert.equal(client.backend, false)

  // Project files anywhere under product/ are backend, not only those under src/ or tests/.
  assert.equal(of(['product/Directory.Build.props']).backend, true)
  assert.equal(of(['product/AeroLink.slnx']).backend, true)
})

test('postgresql keys on persistence as well as migrations, case-insensitively', () => {
  // A change to an EF query needs the real provider even when no schema moves: translation is not
  // portable, and the SQLite path every other gate runs on will accept an expression Npgsql cannot
  // produce.
  assert.equal(of(['product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_init.cs']).postgresql, true)
  assert.equal(of(['product/src/AeroLink.Api/AuthEndpoints.cs']).postgresql, true)
  assert.equal(of(['product/tests/AeroLink.Api.Tests/DatabaseBootstrapTests.cs']).postgresql, true)
  assert.equal(of(['product/src/AeroLink.Infrastructure/PERSISTENCE/Thing.cs']).postgresql, true, 'matching is case-insensitive')
  assert.equal(of(['product/src/AeroLink.Domain/ChangeControl/Rules.cs']).postgresql, false)
})

test('an unrecognised product path runs broad validation rather than nothing', () => {
  // The failure this prevents: a change that was neither documentation nor recognised product code
  // selected nothing, every step skipped on its condition, and the job reported success having executed
  // no test at all. A launcher script, a root config file, or a new top-level directory all landed here.
  const result = of(['START_AEROLINK_PRODUCTION.bat'])
  assert.equal(result.docsOnly, false)
  assert.equal(result.backend, true)
  assert.equal(result.client, true)
  assert.equal(result.browser, true)
  assert.equal(result.postgresql, true)
  assert.equal(result.unclassified, true)
  assert.equal(result.broad, true)
  assert.match(result.reason, /Unclassified/)

  // And the case observed in practice: ci-metrics and test-contracts tooling match no area rule.
  const tooling = of(['product/ci-metrics/lib/rolling.mjs'])
  assert.equal(tooling.unclassified, true)
  assert.equal(tooling.backend, true)
  assert.equal(tooling.client, true)
  assert.equal(tooling.browser, true)
  assert.equal(tooling.postgresql, true)
})

test('a documentation file alongside product code does not make the change docs-only', () => {
  const result = of(['README.md', 'product/client/src/App.tsx'])
  assert.equal(result.docsOnly, false)
  assert.equal(result.client, true)
})

test('scripts, docs, deletions and unknown paths have explicit conservative fixtures', () => {
  const docs = of(['docs/OPERATIONS.md', 'design/mockup.png', 'README.md'])
  assert.equal(docs.docsOnly, true)

  const script = of(['START_AEROLINK_PRODUCTION.bat'])
  assert.equal(script.unclassified, true)
  assert.equal(script.backend, true)
  assert.equal(script.client, true)
  assert.equal(script.browser, true)
  assert.equal(script.postgresql, true)

  // The classifier receives the old path from a deletion/rename diff. It must retain the sensitive area
  // even when the new tree no longer contains the file.
  const deletedMigration = of(['product/src/AeroLink.Infrastructure/Persistence/Migrations/DeletedMigration.cs'])
  assert.equal(deletedMigration.postgresql, true)

  const unknown = of(['product/new-tooling/unknown-format.xyz'])
  assert.equal(unknown.unclassified, true)
  assert.equal(unknown.backend, true)
  assert.equal(unknown.client, true)
  assert.equal(unknown.browser, true)
  assert.equal(unknown.postgresql, true)
  assert.equal(unknown.broad, true)
})

test('explain attributes each path to the areas it selected', () => {
  const rows = explain(['.github/workflows/ci.yml', 'README.md', 'product/client/src/App.tsx'])
  assert.deepEqual(rows[0].areas, ['backend', 'client', 'browser', 'postgresql'])
  assert.equal(rows[1].product, false)
  assert.deepEqual(rows[2].areas, ['client', 'browser'])
})

test('the normal local Fast infrastructure profile leaves only synthetic showcase maintenance to Full CI', () => {
  const classification = of(['product/src/AeroLink.Domain/Requirements/Requirement.cs'])
  assert.equal(classification.fastFullInfrastructure, false)
  const plan = localPlan(classification)
  const infrastructure = plan.find((step) => step.label === 'Infrastructure suite')
  assert.ok(infrastructure)
  assert.match(infrastructure.command, /--filter=/)
  assert.match(infrastructure.command, /FmsShowcaseSeederTests/)
  assert.match(infrastructure.command, /ShowcaseUpgradeTests/)
  assert.match(infrastructure.why, /authoritative GitHub backend-core/)
})

test('showcase-sensitive and broad changes restore the complete Infrastructure suite in Fast', () => {
  const sensitivePaths = [
    'product/src/AeroLink.Infrastructure/Persistence/FmsShowcaseSeeder.cs',
    'product/tests/AeroLink.Infrastructure.Tests/FmsShowcaseSeederTests.cs',
    'product/tests/AeroLink.Infrastructure.Tests/ShowcaseUpgradeTests.cs',
    'product/tests/AeroLink.Infrastructure.Tests/ShowcaseDatabaseFixture.cs',
  ]
  for (const path of sensitivePaths) {
    const classification = of([path])
    assert.equal(classification.fastFullInfrastructure, true, `${path} must restore complete local Infrastructure coverage`)
    const infrastructure = localPlan(classification).find((step) => step.label === 'Infrastructure suite')
    assert.ok(infrastructure)
    assert.doesNotMatch(infrastructure.command, /--filter=/)
    assert.match(infrastructure.why, /complete Infrastructure suite/)
  }

  const windows = of(['PRODUCT\\SRC\\AeroLink.Infrastructure\\Persistence\\FmsShowcaseSeeder.cs'])
  assert.equal(windows.fastFullInfrastructure, true, 'Windows path normalization must retain the showcase-sensitive escape hatch')

  const broad = of(['product/test-planner/lib/classify.mjs'])
  assert.equal(broad.fastFullInfrastructure, true, 'planner changes must use complete local Infrastructure coverage')

  const unknown = of(['product/new-tooling/unknown-format.xyz'])
  assert.equal(unknown.fastFullInfrastructure, true, 'unknown broad fallback must use complete local Infrastructure coverage')
})

test('the CI forecast is read from the workflow, not restated', () => {
  // The first version carried a hand-written list of jobs per area, which is the drift #568 exists to
  // remove: a restatement of the workflow is wrong the first time either changes and nothing notices.
  const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')

  const client = selectJobs(workflow, of(['product/client/src/App.tsx']), { event: 'pull_request' })
  const names = client.selected.map((job) => job.name ?? job.id)
  assert.ok(names.some((name) => /Client lint/.test(name)), 'a client change must select the client job')
  assert.ok(!names.some((name) => /API test suite/.test(name)), 'and must not select the API suites')
  assert.ok(client.skipped.some((job) => /API test suite/.test(job.name ?? job.id)))

  const docs = selectJobs(workflow, of(['README.md']), { event: 'pull_request' })
  const docNames = docs.selected.map((job) => job.name ?? job.id)
  assert.ok(!docNames.some((name) => /Client lint|API test suite|Domain and infrastructure/.test(name)))
  assert.ok(docNames.some((name) => /Full Product evidence aggregate/.test(name)), 'the Product Full gate always reports its internal aggregate')
})
test('the local plan never claims a PostgreSQL-sensitive change was proven locally', () => {
  const plan = localPlan(of(['product/src/AeroLink.Infrastructure/Persistence/Thing.cs']))
  const postgres = plan.find((step) => /PostgreSQL/.test(step.label))
  assert.ok(postgres, 'a persistence change must mention PostgreSQL')
  assert.equal(postgres.command, null, 'there is no local command that constitutes evidence here')
  assert.match(postgres.why, /not evidence/)
})

test('the workflow delegates to this module rather than carrying its own copy', () => {
  // The point of #568 is that one definition exists. A contract test is the only thing standing between
  // that and someone reintroducing an inline copy that drifts — which is the state this replaced.
  const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
  // `tools/`, not `bin/`: .gitignore carries `**/bin/`, which silently leaves any script placed there
  // untracked. The workflow would then call a file that does not exist in the repository, and the first
  // sign of it would be a red CI run on a change that looked complete locally.
  assert.match(workflow, /test-planner[/\\]tools[/\\]classify-ci\.mjs/, 'the changes job must call the shared classifier')

  const classifyJob = workflow.slice(workflow.indexOf('  changes:'), workflow.indexOf('  backend-api:'))
  assert.doesNotMatch(classifyJob, /grep -Eq '\^product/, 'the inline path patterns must not come back')
  assert.doesNotMatch(classifyJob, /backend=true/, 'the inline classification must not come back')
})

test('backend-core runs every hosted contract test file', () => {
  // A single named route test let a later inventory contract be green locally but invisible in CI.
  // Keep the workflow contract directory-driven so adding another `*.test.mjs` is automatically gated.
  const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
  const backendStart = workflow.indexOf('\n  backend-core:')
  const clientStart = workflow.indexOf('\n  client:', backendStart)
  const backendCore = workflow.slice(backendStart, clientStart)
  assert.match(backendCore, /Get-ChildItem\s+-LiteralPath\s+product\/test-contracts\/tests\s+-Filter\s+'\*\.test\.mjs'/)
  assert.match(backendCore, /node\s+--test\s+\$tests/)
  assert.match(backendCore, /No hosted test-contracts test files were found/)
})

test('CI runs every planner test file directory-driven', () => {
  const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
  const plannerStart = workflow.indexOf('product/test-planner/tests')
  assert.notEqual(plannerStart, -1)
  const plannerJob = workflow.slice(Math.max(0, plannerStart - 500), plannerStart + 1000)
  assert.match(plannerJob, /Get-ChildItem\s+-LiteralPath\s+product\/test-planner\/tests\s+-Filter\s+'\*\.test\.mjs'/)
  assert.match(plannerJob, /node\s+--test\s+\$tests/)
  assert.match(plannerJob, /No planner tests were found/)
})

test('every area pattern is anchored so a lookalike path cannot match', () => {
  // `docs/.github/workflows/ci.yml` is not the workflow, and `vendor/product/src/x.cs` is not ours.
  for (const [area, pattern] of Object.entries(AREA_PATTERNS)) {
    assert.equal(pattern.test('docs/.github/workflows/ci.yml'), false, `${area} matched a nested lookalike`)
    assert.equal(pattern.test('vendor/product/src/Thing.cs'), false, `${area} matched a vendored lookalike`)
  }
})

test('the planner scripts are tracked by git, not swallowed by .gitignore', () => {
  // `.gitignore` line 15 is `**/bin/`. A script placed in a `bin/` directory anywhere under the repo is
  // silently untracked, so the workflow calls a path that does not exist once checked out. This has
  // already happened once, to the route manifest generator, and again here. The test is cheap; the
  // failure mode is a red CI run on a change that was green locally.
  for (const script of ['tools/classify-ci.mjs', 'tools/plan.mjs']) {
    const full = join(repoRoot, 'product/test-planner', script)
    assert.ok(existsSync(full), `${script} must exist`)
    const tracked = execFileSync('git', ['ls-files', '--error-unmatch', `product/test-planner/${script}`], {
      cwd: repoRoot, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
    })
    assert.match(tracked, new RegExp(script.replace('.', '\.')), `${script} must be tracked by git`)
  }
})

test('every emitted command is a single valid dotnet test target', () => {
  // `dotnet test` accepts one project, solution or directory. The first version passed two directories
  // in one invocation and produced `MSBUILD : error MSB1008: Only one project can be specified` before
  // either suite ran — a plan that recommended a command that could not work.
  const plan = localPlan(of(['product/src/AeroLink.Domain/X.cs', 'product/client/src/App.tsx']))
  const dotnet = plan.filter((step) => step.command?.startsWith('dotnet test'))
  assert.ok(dotnet.length >= 2, 'both backend suites must be offered')
  for (const step of dotnet) {
    const targets = step.command
      .replace(/^dotnet test\s+/, '')
      .split(/\s+/)
      .filter((token) => !token.startsWith('--') && token !== 'Release')
    assert.equal(targets.length, 1, `"${step.command}" passes ${targets.length} targets; dotnet test accepts one`)
    assert.ok(existsSync(join(repoRoot, targets[0])), `${targets[0]} must exist`)
  }
})

test('both backend suites are actually named, not merged into one target', () => {
  const commands = localPlan(of(['product/src/AeroLink.Domain/X.cs'])).map((s) => s.command).filter(Boolean).join(' ')
  assert.match(commands, /AeroLink\.Domain\.Tests/)
  assert.match(commands, /AeroLink\.Infrastructure\.Tests/)
})