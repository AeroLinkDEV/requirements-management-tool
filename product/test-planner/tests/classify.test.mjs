import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, existsSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { classify, explain, localPlan, selectJobs, AREA_PATTERNS, BROAD_EVENTS, normalizePath, isDocumentationOnlyChange, TEST_READ_DOCUMENTATION } from '../lib/classify.mjs'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))

const of = (paths, event = 'pull_request') => classify(paths, { event })
const workflow = () => readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')

test('broad events classify every area without a diff', () => {
  // Push, schedule and dispatch have no pull-request base, and a merge group is the last gate before main,
  // so each classifies every area. A merge group is broad for any change that is not documentation only,
  // and when it has no paths at all.
  for (const event of BROAD_EVENTS) {
    for (const paths of [['product/client/src/App.tsx'], ['README.md', 'product/src/AeroLink.Domain/Rule.cs'], []]) {
      const result = of(paths, event)
      assert.equal(result.docsOnly, false, `${event} ${paths.join(',')}`)
      for (const area of ['backend', 'client', 'browser', 'postgresql']) {
        assert.equal(result[area], true, `${event} must select ${area} for ${paths.join(',') || 'no paths'}`)
      }
    }
    if (event !== 'merge_group') assert.equal(of(['README.md'], event).docsOnly, false, `${event} stays broad for documentation`)
  }
})

test('a merge-group candidate whose own change is documentation takes the documentation topology', () => {
  // #1152 A3. The caller supplies the candidate's diff against its queue base, both sides of renames.
  const docs = of(['README.md', 'product/docs/MERGING.md', '.agents/skills/x/SKILL.md', 'docs/showcase/a.md'], 'merge_group')
  assert.equal(docs.docsOnly, true)
  for (const area of ['backend', 'client', 'browser', 'postgresql']) assert.equal(docs[area], false, area)
  assert.equal(docs.broad, false)
  // A product file renamed into a documentation folder contributes its old path, so it is not documentation.
  assert.equal(of(['product/src/AeroLink.Api/Program.cs', 'docs/Program.cs'], 'merge_group').docsOnly, false)
  // A nested documentation-looking path inside product code is product.
  assert.equal(of(['product/src/docs/DocumentationLoader.cs'], 'merge_group').docsOnly, false)
  assert.equal(isDocumentationOnlyChange([]), false)
  assert.equal(isDocumentationOnlyChange(['README.md', '']), false)
})

test('documentation a product suite reads is backend input, never documentation-only', () => {
  // #1152 A3 review: ProjectLadderConfigurationTests reads the policy matrix, so a queue candidate editing only
  // the matrix must still run the Domain suite that can turn red on it.
  for (const path of ['product/docs/REQUIREMENT_HIERARCHY_POLICY_MATRIX.md', 'docs\\AeroLink Technical Overview.docx']) {
    assert.equal(isDocumentationOnlyChange([path]), false, path)
    for (const event of ['pull_request', 'merge_group']) {
      const result = of([path, 'README.md'], event)
      assert.equal(result.docsOnly, false, `${event} ${path}`)
      assert.equal(result.backend, true, `${event} ${path}`)
    }
  }
  assert.deepEqual(explain(['product/docs/REQUIREMENT_HIERARCHY_POLICY_MATRIX.md'])[0].areas, ['backend'])
  for (const path of TEST_READ_DOCUMENTATION) assert.equal(path, normalizePath(path), `${path} must be stored normalized`)
})

// Every test source that names a documentation file, or builds a path from a documentation root, and what it
// does with it. A read makes the named file test input, so it must be in TEST_READ_DOCUMENTATION; anything
// else is a fixture string, comment or output path. The guard below derives this map from the tree and
// compares it exactly, so a new reference fails until someone decides which kind it is (#1152 A3).
const FIXTURE = 'a fixture path or file name, not a repository read'
const COMMENT = 'a comment or message naming the document'
const DOCUMENTATION_REFERENCES = {
  // Reads by suites the documentation topology skips.
  'product/tests/AeroLink.Domain.Tests/ProjectLadderConfigurationTests.cs': { reads: true, refs: ['directory', 'product/docs/REQUIREMENT_HIERARCHY_POLICY_MATRIX.md'] },
  'product/tests/AeroLink.Infrastructure.Tests/AeroLinkOoxmlProfileTests.cs': { reads: true, refs: ['directory', 'docs/AeroLink Technical Overview.docx'] },
  // Reads every maintained document, but runs in the always-running classifier job (asserted below), so a
  // documentation-only candidate still runs it.
  'product/scripts/Test-RepositoryLayout.Tests.ps1': { reads: false, why: 'always runs', refs: ['directory', 'CURRENT_PRODUCT_HANDOFF_2026-07-29.md', 'DECISIONS_AND_OPEN_QUESTIONS.md', 'FEATURE_CATALOG.md', 'PROJECT_STATE.md', 'README.md', 'docs/REMOTE_DEMO_OPERATOR.md'] },
  // Mentions.
  'product/ci-metrics/tests/maintenance-approval.test.mjs': { reads: false, why: 'reads product/ci-metrics/README.md, which is product', refs: ['README.md'] },
  'product/ci-metrics/tests/merge-authority-github.test.mjs': { reads: false, why: `${FIXTURE}; reads product/ci-metrics/README.md`, refs: ['directory', 'README.md', 'product/docs/MERGING.md'] },
  'product/ci-metrics/tests/merge-authority.test.mjs': { reads: false, why: FIXTURE, refs: ['directory', 'README.md'] },
  'product/ci-metrics/tests/provenance.test.mjs': { reads: false, why: FIXTURE, refs: ['directory', 'README.md'] },
  'product/client/tests/capture-overview.spec.ts': { reads: false, why: 'writes captures into docs/overview-video/shots; reads nothing there', refs: ['directory', 'docs/overview-video/slides.js'] },
  'product/client/tests/code-workspace-rendered.spec.ts': { reads: false, why: FIXTURE, refs: ['README.md'] },
  'product/client/tests/design-system.spec.ts': { reads: false, why: COMMENT, refs: ['DECISIONS_AND_OPEN_QUESTIONS.md'] },
  'product/client/tests/product-claims.spec.ts': { reads: false, why: COMMENT, refs: ['docs/product-definition/SCOPE_AND_BOUNDARIES.md'] },
  'product/client/tests/project-features.spec.ts': { reads: false, why: 'a screenshot output name that ends in command-center.png', refs: ['docs/overview-video/shots/command-center.png'] },
  'product/scripts/AeroLinkBootstrap.Tests.ps1': { reads: false, why: 'writes files into a disposable fixture repository', refs: ['directory', 'README.md'] },
  'product/scripts/AeroLinkRemoteDemo.Tests.ps1': { reads: false, why: COMMENT, refs: ['docs/REMOTE_DEMO_OPERATOR.md'] },
  'product/scripts/AeroLinkTransitionAuthority.Tests.ps1': { reads: false, why: 'writes a stand-in file into a disposable source root', refs: ['README.md'] },
  'product/scripts/Get-AeroLinkTestPlan.Tests.ps1': { reads: false, why: FIXTURE, refs: ['README.md'] },
  'product/test-planner/tests/classify-ci.test.mjs': { reads: false, why: FIXTURE, refs: ['README.md'] },
  'product/test-planner/tests/execution-contract.test.mjs': { reads: false, why: FIXTURE, refs: ['README.md'] },
  'product/test-planner/tests/overlap.test.mjs': { reads: false, why: FIXTURE, refs: ['directory', 'README.md', 'product/docs/OPERATIONS.md'] },
  'product/test-planner/tests/parity.test.mjs': { reads: false, why: FIXTURE, refs: ['directory', 'README.md', 'product/docs/OPERATIONS.md'] },
  'product/test-planner/tests/plan-cli.test.mjs': { reads: false, why: FIXTURE, refs: ['README.md'] },
  'product/tests/AeroLink.Api.Tests/GitLabMetadataApiTests.cs': { reads: false, why: FIXTURE, refs: ['README.md'] },
  'product/tests/AeroLink.Infrastructure.Tests/DocumentReviewEmailTests.cs': { reads: false, why: 'DocumentReviewEmailTemplate.Html(...) matches template.html case-insensitively', refs: ['docs/overview-video/template.html'] },
  'product/tests/AeroLink.Infrastructure.Tests/FmsUpstreamRestoredCopyQualificationTests.cs': { reads: false, why: COMMENT, refs: ['product/docs/OPERATIONS.md'] },
  'product/tests/AeroLink.Infrastructure.Tests/NotificationOutboxTests.cs': { reads: false, why: 'DocumentReviewEmailTemplate.Html(...) matches template.html case-insensitively', refs: ['docs/overview-video/template.html'] },
  'product/tests/AeroLink.Infrastructure.Tests/ProductLinePublicationTests.cs': { reads: false, why: 'a comment naming a `showcase` variable', refs: ['directory'] },
  'product/tests/AeroLink.Infrastructure.Tests/ReleasedSyntheticSourceSupplementServiceTests.cs': { reads: false, why: FIXTURE, refs: ['README.md'] },
}

const TEST_SOURCE = /^(?:product\/tests\/|product\/client\/tests\/|product\/(?:test-planner|ci-metrics|test-contracts)\/tests\/|product\/scripts\/[^/]+\.Tests\.ps1$|product\/client\/src\/.*\.(?:test|spec)\.[cm]?[jt]sx?$)/
// A string that starts at a documentation root, relative or joined segment by segment. Case-sensitive: the
// roots are lower case on disk, and "Design" in a fixture is not a path.
const DOCUMENTATION_DIRECTORY = /["'`](?:\.\.[/\\])*(?:docs|design|showcase|product[/\\]docs|\.agents|\.claude|\.codex)(?:["'`]|[/\\])/

test('every documentation file a test source references is accounted for', () => {
  const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
    .split('\0').filter(Boolean)
  const documentation = tracked.filter((path) => isDocumentationOnlyChange([path]) || TEST_READ_DOCUMENTATION.includes(normalizePath(path)))
  const baseName = (path) => path.slice(path.lastIndexOf('/') + 1).toLowerCase()
  const nameCount = new Map()
  for (const path of tracked) nameCount.set(baseName(path), (nameCount.get(baseName(path)) ?? 0) + 1)

  const actual = {}
  // This file names every reference by construction and reads none, so it is the one source not scanned.
  const self = 'product/test-planner/tests/classify.test.mjs'
  for (const source of tracked.filter((path) => path !== self && TEST_SOURCE.test(path) && /\.(?:cs|[cm]?[jt]sx?|ps1|psm1|json)$/i.test(path))) {
    const raw = readFileSync(join(repoRoot, source), 'utf8')
    // Compare in one spelling: forward slashes (C# and PowerShell escape backslashes) and lower case.
    const text = raw.replace(/\\+/g, '/').toLowerCase()
    const refs = documentation.filter((path) => {
      const normalized = path.toLowerCase()
      // A full path always counts. A bare file name counts only when no other tracked file shares it, since
      // `Path.Combine("docs", name)` names the file without its path.
      return text.includes(normalized) || (nameCount.get(baseName(path)) === 1 && text.includes(baseName(path)))
    })
    if (DOCUMENTATION_DIRECTORY.test(raw)) refs.unshift('directory')
    if (refs.length > 0) actual[source] = refs
  }

  const expected = Object.fromEntries(Object.entries(DOCUMENTATION_REFERENCES).map(([source, entry]) => [source, entry.refs]))
  assert.deepEqual(actual, expected,
    'A test source references documentation. Add it to DOCUMENTATION_REFERENCES: a read also belongs in TEST_READ_DOCUMENTATION.')

  const read = Object.values(DOCUMENTATION_REFERENCES).filter((entry) => entry.reads)
    .flatMap((entry) => entry.refs.filter((ref) => ref !== 'directory')).map(normalizePath)
  assert.deepEqual([...new Set(read)].sort(), [...TEST_READ_DOCUMENTATION].sort())
  for (const [source, entry] of Object.entries(DOCUMENTATION_REFERENCES)) {
    assert.ok(entry.reads === true || (entry.reads === false && entry.why), `${source} must say why it is not a read`)
  }

  // The layout contract reads every maintained document, so it must stay in the job a documentation-only
  // candidate still runs: the classifier job, before the next job begins.
  const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8').replace(/\r\n/g, '\n')
  const start = workflow.indexOf('\n  changes:\n')
  assert.notEqual(start, -1, 'the classifier job exists')
  const rest = workflow.slice(start + 1)
  const next = rest.slice(1).search(/\n  [a-z][a-z-]*:\n/)
  const classifierJob = next === -1 ? rest : rest.slice(0, next + 1)
  assert.doesNotMatch(classifierJob, /^    if:/m, 'the classifier job runs unconditionally')
  const stepStart = classifierJob.indexOf('- name: Validate repository layout and documentation links')
  assert.notEqual(stepStart, -1)
  const step = classifierJob.slice(stepStart, classifierJob.indexOf('\n      - name:', stepStart))
  assert.doesNotMatch(step, /^\s+if:/m, 'the layout step runs unconditionally')
  assert.match(step, /& \.\/product\/scripts\/Test-RepositoryLayout\.ps1/)
  assert.match(step, /& \.\/product\/scripts\/Test-RepositoryLayout\.Tests\.ps1/)
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
  const result = of(['product/new-tooling/unknown-format.xyz'])
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

test('agent-instruction folders are documentation, not unknown product code', () => {
  // #1141: these paths used to fall through to the unclassified fallback, so a change to agent instructions
  // alone paid for the full backend, client, browser and PostgreSQL sweep that cannot observe them.
  const instructions = of([
    'AGENTS.md',
    '.agents/skills/test-audit/SKILL.md',
    '.claude/skills/test-audit/SKILL.md',
    '.codex/agents/coder.toml',
    '.codex/config.toml',
  ])
  assert.equal(instructions.docsOnly, true)
  assert.equal(instructions.unclassified, false)
  for (const area of ['backend', 'client', 'browser', 'postgresql']) {
    assert.equal(instructions[area], false, `an instruction-only change must not select ${area}`)
  }

  // Only the root folders qualify; a nested look-alike under product/ is still product code.
  assert.equal(of(['product/client/.codex/config.toml']).docsOnly, false)

  const mixed = of(['.codex/agents/coder.toml', 'product/client/src/App.tsx'])
  assert.equal(mixed.docsOnly, false)
  assert.equal(mixed.client, true)
})

test('a documentation file alongside product code does not make the change docs-only', () => {
  const result = of(['README.md', 'product/client/src/App.tsx'])
  assert.equal(result.docsOnly, false)
  assert.equal(result.client, true)
})

test('scripts, docs, deletions and unknown paths have explicit conservative fixtures', () => {
  const docs = of(['docs/OPERATIONS.md', 'design/mockup.png', 'README.md'])
  assert.equal(docs.docsOnly, true)

  // A launcher is no longer unknown: the launcher contract reads these files, so the product suites are not
  // what vouches for them. Everything else that is neither documentation nor recognised product code still
  // runs the full sweep.
  const launcher = of(['START_AEROLINK_PRODUCTION.bat'])
  assert.equal(launcher.unclassified, false)
  assert.equal(launcher.launchersOnly, true)
  assert.equal(launcher.backend, false)
  assert.equal(launcher.client, false)
  assert.equal(launcher.browser, false)
  assert.equal(launcher.postgresql, false)

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
  assert.match(infrastructure.command, /FmsShowcaseScenarioTests/)
  assert.match(infrastructure.command, /ShowcaseUpgradeTests/)
  assert.match(infrastructure.why, /authoritative GitHub backend-core-infrastructure/)
})

test('showcase-sensitive and broad changes restore the complete Infrastructure suite in Fast', () => {
  const sensitivePaths = [
    'product/src/AeroLink.Infrastructure/Persistence/FmsShowcaseSeeder.cs',
    'product/tests/AeroLink.Infrastructure.Tests/FmsShowcaseSeederTests.cs',
    'product/tests/AeroLink.Infrastructure.Tests/FmsShowcaseScenarioTests.cs',
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

test('backend-core-domain runs every hosted contract test file', () => {
  // A single named route test let a later inventory contract be green locally but invisible in CI.
  // Keep the workflow contract directory-driven so adding another `*.test.mjs` is automatically gated.
  const workflow = readFileSync(join(repoRoot, '.github/workflows/ci.yml'), 'utf8')
  const backendStart = workflow.indexOf('\n  backend-core-domain:')
  const clientStart = workflow.indexOf('\n  backend-core-infrastructure:', backendStart)
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

test('a launcher-only change is validated by the launcher contract, not by the product suites', () => {
  // Before this rule a .bat-only pull request classified as unknown and ran the full backend, client,
  // browser and PostgreSQL validation: roughly ninety minutes of compute for files none of it can observe.
  for (const paths of [['START_AEROLINK_PRODUCTION.bat'], ['STOP_AEROLINK.bat', 'BACKUP_AEROLINK.bat'], ['product/scripts/launch.cmd']]) {
    const result = of(paths)
    assert.equal(result.launchersOnly, true, paths.join(', '))
    assert.equal(result.unclassified, false, paths.join(', '))
    assert.equal(result.broad, false, paths.join(', '))
    for (const area of ['backend', 'client', 'browser', 'postgresql']) {
      assert.equal(result[area], false, `${area} for ${paths.join(', ')}`)
    }
  }

  // Documentation alongside a launcher does not change the answer, because documentation selects nothing.
  assert.equal(of(['START_AEROLINK.bat', 'README.md']).launchersOnly, true)

  // One launcher touched alongside product code classifies on the product code. The rule is about changes
  // confined to launchers, not about ignoring a launcher that happens to be in a larger change.
  const mixed = of(['START_AEROLINK.bat', 'product/src/AeroLink.Domain/Rules/Rule.cs'])
  assert.equal(mixed.launchersOnly, false)
  assert.equal(mixed.backend, true)

  // The fallback is unchanged for everything it was actually protecting.
  for (const unknown of [['product/ci-metrics/lib/rolling.mjs'], ['newthing/config.xyz'], ['product/new-tooling/x.xyz']]) {
    const result = of(unknown)
    assert.equal(result.unclassified, true, unknown.join(', '))
    assert.equal(result.backend, true, unknown.join(', '))
  }

  // A push still classifies every area regardless: it has no base to diff against.
  assert.equal(of(['START_AEROLINK.bat'], 'push').launchersOnly, false)
})

test('the operator contracts run for every change they can observe, and only skip what no script reads (#1152 C3)', () => {
  const contracts = (paths, event = 'pull_request') => {
    const result = of(paths, event)
    const jobs = selectJobs(workflow(), result, { event })
    return { operator: result.operator, selected: jobs.selected.some((job) => job.id === 'script-contracts') }
  }
  // Client source, client tests, public assets and backend test projects: no script, module or suite reads them.
  for (const paths of [
    ['product/client/src/App.tsx'], ['product/client/tests/login.spec.ts', 'README.md'], ['product/client/public/people/a.png'],
    ['product/tests/AeroLink.Domain.Tests/RuleTests.cs'], ['product\\Client\\Src\\App.tsx'],
  ]) assert.deepEqual(contracts(paths), { operator: false, selected: false }, paths.join(', '))
  // Everything else runs them: backend source (the scripts read Program.cs, settings, the API project and the
  // migrations), the client's package and build files, scripts, launchers, planner and workflow, unknown paths,
  // and any mix with one of those.
  for (const paths of [
    ['product/src/AeroLink.Api/Program.cs'], ['product/src/AeroLink.Infrastructure/Persistence/Migrations/0001_x.cs'],
    ['product/client/package-lock.json'], ['product/client/vite.config.ts'], ['product/client/playwright.config.ts'],
    ['product/scripts/AeroLinkUpgrade.psm1'], ['START_AEROLINK.bat'], ['product/test-planner/lib/classify.mjs'],
    ['.github/workflows/ci.yml'], ['newthing/config.xyz'], ['product/client/src/App.tsx', 'product/scripts/Backup-AeroLink.ps1'],
    ['product/tests/AeroLink.Api.Tests/X.cs', 'product/src/AeroLink.Api/Program.cs'],
  ]) assert.deepEqual(contracts(paths), { operator: true, selected: true }, paths.join(', '))
  // Broad events always run them; documentation never does.
  for (const event of ['push', 'merge_group', 'schedule', 'workflow_dispatch']) {
    assert.deepEqual(contracts(['product/client/src/App.tsx'], event), { operator: true, selected: true }, event)
  }
  assert.deepEqual(contracts(['README.md']), { operator: false, selected: false })
  assert.deepEqual(contracts(['README.md'], 'merge_group'), { operator: false, selected: false })
  // The forecast keeps the job when a classification predates the field: only an explicit false skips it.
  const legacy = { ...of(['product/client/src/App.tsx']) }
  delete legacy.operator
  assert.ok(selectJobs(workflow(), legacy, { event: 'pull_request' }).selected.some((job) => job.id === 'script-contracts'))
})

// Scripts, modules and launchers that name a path the operator contracts are classified as unable to observe,
// and why that name is not an observation by a CI script contract. The guard below derives this from the tree
// and compares it exactly, so a script that starts reading client source or a backend test project fails until
// the classifier's OPERATOR_INVISIBLE_PATHS is narrowed or the reference is explained (#1152 C3). It sees string
// paths; a path assembled one segment at a time is caught by the broad merge queue, not here.
const OPERATOR_INVISIBLE_REFERENCES = {
  'TEST_AEROLINK_CHANGED.bat': 'a usage example in a comment',
  'product/scripts/Get-AeroLinkTestPlan.ps1': 'local Full-mode commands; its CI contract runs it only with -DryRun',
  'product/scripts/Test-ProjectSetupPostgres.ps1': 'its CI contract replaces dotnet with a function and runs no test project',
  'product/scripts/Test-ProjectSetupWord.ps1': 'no CI script contract runs it',
}

test('no operator script reads a path the classifier says the operator contracts cannot observe', () => {
  const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
    .split('\0').filter(Boolean)
  const operatorSource = /^(?:product\/scripts\/.+\.(?:ps1|psm1|psd1|cmd|json|mjs)|[^/]+\.(?:bat|cmd))$/i
  // Client source, tests or public assets, or the backend test projects, spelled from the repository root, the
  // product root (a quoted relative segment) or product/scripts (`..`).
  const invisibleReference = /(?:product\/|\.\.\/|['"`])(?:client\/(?:src|tests|public)|tests)(?:\/|['"`\s]|$)/m
  const actual = tracked
    .filter((path) => operatorSource.test(path))
    .filter((path) => invisibleReference.test(readFileSync(join(repoRoot, path), 'utf8').replace(/\\+/g, '/')))
    .sort()
  assert.deepEqual(actual, Object.keys(OPERATOR_INVISIBLE_REFERENCES).sort(),
    'An operator script names client source, client tests, public assets or a backend test project. Explain it in OPERATOR_INVISIBLE_REFERENCES, or narrow OPERATOR_INVISIBLE_PATHS in classify.mjs.')
  // The planner contract must keep running the planner as a plan only, or the explanation above stops being true.
  const plannerContract = readFileSync(join(repoRoot, 'product/scripts/Get-AeroLinkTestPlan.Tests.ps1'), 'utf8')
  const calls = plannerContract.split('\n').filter((line) => /Invoke-Plan(?:From)?\b/.test(line) && !/^\s*function /.test(line))
  const literalCalls = calls.filter((line) => line.includes('@('))
  assert.ok(literalCalls.length >= 8, 'the planner contract still exercises the planner')
  for (const line of literalCalls) assert.match(line, /'-DryRun'/, line.trim())
  // The only non-literal call is Invoke-PlanFrom forwarding its own arguments.
  assert.deepEqual(calls.filter((line) => !line.includes('@(')).map((line) => line.trim()), ['try { return Invoke-Plan $Arguments }'])
  assert.match(readFileSync(join(repoRoot, 'product/scripts/Test-ProjectSetupPostgres.Tests.ps1'), 'utf8'), /^function dotnet \{/m)
})
