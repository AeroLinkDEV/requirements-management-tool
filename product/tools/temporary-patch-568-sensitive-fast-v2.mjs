import { readFileSync, writeFileSync } from 'node:fs'

function replaceOnce(path, oldText, newText, label) {
  const source = readFileSync(path, 'utf8')
  const count = source.split(oldText).length - 1
  if (count !== 1) throw new Error(`${label}: expected one match, found ${count}`)
  writeFileSync(path, source.replace(oldText, newText), 'utf8')
}

const classifyPath = 'product/test-planner/lib/classify.mjs'
replaceOnce(
  classifyPath,
  "export function isBroadPath(path) {\n  return BROAD_PATHS.some((pattern) => pattern.test(normalizePath(path)))\n}\n\nfunction matchingBroadPath(paths) {",
  "export function isBroadPath(path) {\n  return BROAD_PATHS.some((pattern) => pattern.test(normalizePath(path)))\n}\n\n// The normal Fast lane defers six synthetic showcase seed/upgrade maintenance cases to authoritative\n// Full/CI. Direct edits to those tests, their shared fixture, or the seeder they prove must restore the\n// complete Infrastructure suite locally rather than filtering the most relevant coverage.\nconst FAST_FULL_INFRASTRUCTURE_PATHS = [\n  /^product\\/src\\/AeroLink\\.Infrastructure\\/Persistence\\/FmsShowcaseSeeder\\.cs$/i,\n  /^product\\/tests\\/AeroLink\\.Infrastructure\\.Tests\\/(?:FmsShowcaseSeederTests|ShowcaseUpgradeTests|ShowcaseDatabaseFixture)\\.cs$/i,\n]\n\nexport function needsFullFastInfrastructure(paths) {\n  return (Array.isArray(paths) ? paths : []).some((path) =>\n    FAST_FULL_INFRASTRUCTURE_PATHS.some((pattern) => pattern.test(normalizePath(path))),\n  )\n}\n\nfunction matchingBroadPath(paths) {",
  'insert sensitive Fast Infrastructure manifest',
)

replaceOnce(
  classifyPath,
  "      unclassified: false,\n      broad: true,\n    }\n  }\n\n  const paths =",
  "      unclassified: false,\n      broad: true,\n      fastFullInfrastructure: true,\n    }\n  }\n\n  const paths =",
  'broad event fastFullInfrastructure',
)

replaceOnce(
  classifyPath,
  "      unclassified: false,\n      broad: true,\n    }\n  }\n\n  const result = {",
  "      unclassified: false,\n      broad: true,\n      fastFullInfrastructure: true,\n    }\n  }\n\n  const result = {",
  'broad path fastFullInfrastructure',
)

replaceOnce(
  classifyPath,
  "    unclassified: false,\n    broad: false,\n  }",
  "    unclassified: false,\n    broad: false,\n    fastFullInfrastructure: needsFullFastInfrastructure(paths),\n  }",
  'normal classification fastFullInfrastructure',
)

replaceOnce(
  classifyPath,
  "    result.unclassified = true\n    result.broad = true\n    result.reason =",
  "    result.unclassified = true\n    result.broad = true\n    result.fastFullInfrastructure = true\n    result.reason =",
  'unknown fallback fastFullInfrastructure',
)

replaceOnce(
  classifyPath,
  "    steps.push({\n      label: 'Infrastructure suite',\n      command: 'dotnet test product/tests/AeroLink.Infrastructure.Tests --configuration Release --no-build --filter=FullyQualifiedName!~AeroLink.Infrastructure.Tests.FmsShowcaseSeederTests&FullyQualifiedName!~AeroLink.Infrastructure.Tests.ShowcaseUpgradeTests',\n      why: 'Fast persistence/provider coverage excludes six synthetic showcase seed/upgrade maintenance cases; the authoritative GitHub backend-core lane still runs the complete infrastructure suite.',\n    })",
  "    const fullFastInfrastructure = classification.fastFullInfrastructure === true\n    steps.push({\n      label: 'Infrastructure suite',\n      command: fullFastInfrastructure\n        ? 'dotnet test product/tests/AeroLink.Infrastructure.Tests --configuration Release --no-build'\n        : 'dotnet test product/tests/AeroLink.Infrastructure.Tests --configuration Release --no-build --filter=FullyQualifiedName!~AeroLink.Infrastructure.Tests.FmsShowcaseSeederTests&FullyQualifiedName!~AeroLink.Infrastructure.Tests.ShowcaseUpgradeTests',\n      why: fullFastInfrastructure\n        ? 'This change directly affects showcase seed/upgrade coverage or broad test-planner behavior, so Fast restores the complete Infrastructure suite locally.'\n        : 'Fast persistence/provider coverage excludes six synthetic showcase seed/upgrade maintenance cases; the authoritative GitHub backend-core lane still runs the complete infrastructure suite.',\n    })",
  'conditional local Infrastructure plan',
)

const psPath = 'product/scripts/Get-AeroLinkTestPlan.ps1'
replaceOnce(
  psPath,
  "        'Infrastructure suite' { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build', '--filter', 'FullyQualifiedName!~AeroLink.Infrastructure.Tests.FmsShowcaseSeederTests&FullyQualifiedName!~AeroLink.Infrastructure.Tests.ShowcaseUpgradeTests') }",
  "        'Infrastructure suite' {\n            if ($plan.classification.fastFullInfrastructure) {\n                Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build')\n            }\n            else {\n                Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build', '--filter', 'FullyQualifiedName!~AeroLink.Infrastructure.Tests.FmsShowcaseSeederTests&FullyQualifiedName!~AeroLink.Infrastructure.Tests.ShowcaseUpgradeTests')\n            }\n        }",
  'conditional PowerShell Infrastructure execution',
)

const classifyTestPath = 'product/test-planner/tests/classify.test.mjs'
replaceOnce(
  classifyTestPath,
  "test('the local Fast infrastructure profile leaves only synthetic showcase maintenance to Full CI', () => {\n  const plan = localPlan(of(['product/src/AeroLink.Domain/Requirements/Requirement.cs']))\n  const infrastructure = plan.find((step) => step.label === 'Infrastructure suite')\n  assert.ok(infrastructure)\n  assert.match(infrastructure.command, /--filter=/)\n  assert.match(infrastructure.command, /FmsShowcaseSeederTests/)\n  assert.match(infrastructure.command, /ShowcaseUpgradeTests/)\n  assert.match(infrastructure.why, /authoritative GitHub backend-core/)\n})",
  "test('the normal local Fast infrastructure profile leaves only synthetic showcase maintenance to Full CI', () => {\n  const classification = of(['product/src/AeroLink.Domain/Requirements/Requirement.cs'])\n  assert.equal(classification.fastFullInfrastructure, false)\n  const plan = localPlan(classification)\n  const infrastructure = plan.find((step) => step.label === 'Infrastructure suite')\n  assert.ok(infrastructure)\n  assert.match(infrastructure.command, /--filter=/)\n  assert.match(infrastructure.command, /FmsShowcaseSeederTests/)\n  assert.match(infrastructure.command, /ShowcaseUpgradeTests/)\n  assert.match(infrastructure.why, /authoritative GitHub backend-core/)\n})\n\ntest('showcase-sensitive and broad changes restore the complete Infrastructure suite in Fast', () => {\n  const sensitivePaths = [\n    'product/src/AeroLink.Infrastructure/Persistence/FmsShowcaseSeeder.cs',\n    'product/tests/AeroLink.Infrastructure.Tests/FmsShowcaseSeederTests.cs',\n    'product/tests/AeroLink.Infrastructure.Tests/ShowcaseUpgradeTests.cs',\n    'product/tests/AeroLink.Infrastructure.Tests/ShowcaseDatabaseFixture.cs',\n  ]\n  for (const path of sensitivePaths) {\n    const classification = of([path])\n    assert.equal(classification.fastFullInfrastructure, true, `${path} must restore complete local Infrastructure coverage`)\n    const infrastructure = localPlan(classification).find((step) => step.label === 'Infrastructure suite')\n    assert.ok(infrastructure)\n    assert.doesNotMatch(infrastructure.command, /--filter=/)\n    assert.match(infrastructure.why, /complete Infrastructure suite/)\n  }\n\n  const windows = of(['PRODUCT\\\\SRC\\\\AeroLink.Infrastructure\\\\Persistence\\\\FmsShowcaseSeeder.cs'])\n  assert.equal(windows.fastFullInfrastructure, true, 'Windows path normalization must retain the showcase-sensitive escape hatch')\n\n  const broad = of(['product/test-planner/lib/classify.mjs'])\n  assert.equal(broad.fastFullInfrastructure, true, 'planner changes must use complete local Infrastructure coverage')\n\n  const unknown = of(['product/new-tooling/unknown-format.xyz'])\n  assert.equal(unknown.fastFullInfrastructure, true, 'unknown broad fallback must use complete local Infrastructure coverage')\n})",
  'replace Fast profile classification contract',
)

const psTestPath = 'product/scripts/Get-AeroLinkTestPlan.Tests.ps1'
const psMarker = "$fastInfrastructureFilter = 'FullyQualifiedName!~AeroLink.Infrastructure.Tests.FmsShowcaseSeederTests&FullyQualifiedName!~AeroLink.Infrastructure.Tests.ShowcaseUpgradeTests'"
const psInsert = `$normalFastJsonRun = Invoke-Plan @('-Paths', 'product\\src\\AeroLink.Domain\\Requirements\\Requirement.cs', '-Json', '-DryRun')
Assert-True ($normalFastJsonRun.ExitCode -eq 0) "Normal Fast dry-run should succeed: $($normalFastJsonRun.Output)"
$normalFastJson = $normalFastJsonRun.Output | ConvertFrom-Json
Assert-True ($normalFastJson.classification.fastFullInfrastructure -eq $false) 'An ordinary domain change should retain the bounded Fast Infrastructure profile.'
$normalInfrastructure = @($normalFastJson.local | Where-Object { $_.label -eq 'Infrastructure suite' }) | Select-Object -First 1
Assert-True ($normalInfrastructure.command -match '--filter=') 'An ordinary domain change should use the bounded six-case Fast exclusion.'

$sensitiveFastJsonRun = Invoke-Plan @('-Paths', 'product\\src\\AeroLink.Infrastructure\\Persistence\\FmsShowcaseSeeder.cs', '-Json', '-DryRun')
Assert-True ($sensitiveFastJsonRun.ExitCode -eq 0) "Showcase-sensitive Fast dry-run should succeed: $($sensitiveFastJsonRun.Output)"
$sensitiveFastJson = $sensitiveFastJsonRun.Output | ConvertFrom-Json
Assert-True ($sensitiveFastJson.classification.fastFullInfrastructure -eq $true) 'A showcase seeder change must restore the complete Infrastructure suite in Fast.'
$sensitiveInfrastructure = @($sensitiveFastJson.local | Where-Object { $_.label -eq 'Infrastructure suite' }) | Select-Object -First 1
Assert-True ($sensitiveInfrastructure.command -notmatch '--filter=') 'A showcase seeder change must not filter its directly relevant Infrastructure tests.'

${psMarker}`
replaceOnce(psTestPath, psMarker, psInsert, 'insert Windows dry-run contracts for sensitive profile')

console.log('Applied #568 showcase-sensitive Fast profile safety patch.')
