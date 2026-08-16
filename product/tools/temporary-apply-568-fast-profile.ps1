# Temporary branch-only helper used to apply the bounded #568 Fast-profile patch.
$ErrorActionPreference = 'Stop'
$filter = 'FullyQualifiedName!~AeroLink.Infrastructure.Tests.FmsShowcaseSeederTests&FullyQualifiedName!~AeroLink.Infrastructure.Tests.ShowcaseUpgradeTests'

function Replace-ExactlyOnce([string]$Path, [string]$Old, [string]$New) {
    $text = [IO.File]::ReadAllText($Path)
    $count = ($text.Split($Old).Count - 1)
    if ($count -ne 1) { throw "Expected exactly one patch anchor in $Path; found $count." }
    [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
}

$plannerScript = 'product/scripts/Get-AeroLinkTestPlan.ps1'
$oldFast = "        'Infrastructure suite' { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build') }"
$newFast = "        'Infrastructure suite' { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build', '--filter', '$filter') }"
Replace-ExactlyOnce $plannerScript $oldFast $newFast

$classify = 'product/test-planner/lib/classify.mjs'
$oldPlan = @'
    steps.push({
      label: 'Infrastructure suite',
      command: 'dotnet test product/tests/AeroLink.Infrastructure.Tests --configuration Release --no-build',
      why: 'Persistence and EF behaviour, still without building an API host.',
    })
'@
$newPlan = @"
    steps.push({
      label: 'Infrastructure suite',
      command: 'dotnet test product/tests/AeroLink.Infrastructure.Tests --configuration Release --no-build --filter `"$filter`"',
      why: 'Fast persistence/provider coverage excludes six synthetic showcase seed/upgrade maintenance cases; the authoritative GitHub backend-core lane still runs the complete infrastructure suite.',
    })
"@
Replace-ExactlyOnce $classify $oldPlan $newPlan

$plannerTests = 'product/test-planner/tests/classify.test.mjs'
$anchor = "test('the CI forecast is read from the workflow, not restated', () => {"
$insert = @"
test('the local Fast infrastructure profile leaves only synthetic showcase maintenance to Full CI', () => {
  const plan = localPlan(of(['product/src/AeroLink.Domain/Requirements/Requirement.cs']))
  const infrastructure = plan.find((step) => step.label === 'Infrastructure suite')
  assert.ok(infrastructure)
  assert.match(infrastructure.command, /--filter/)
  assert.match(infrastructure.command, /FmsShowcaseSeederTests/)
  assert.match(infrastructure.command, /ShowcaseUpgradeTests/)
  assert.match(infrastructure.why, /authoritative GitHub backend-core/)
})

$anchor
"@
Replace-ExactlyOnce $plannerTests $anchor $insert

$psTests = 'product/scripts/Get-AeroLinkTestPlan.Tests.ps1'
$psAnchor = '$plannerSource = Get-Content -Raw -LiteralPath $scriptPath'
$psInsert = @"
$psAnchor
`$fastInfrastructureFilter = '$filter'
Assert-True (([regex]::Matches(`$plannerSource, [regex]::Escape(`$fastInfrastructureFilter))).Count -eq 1) 'The six-case Fast infrastructure filter must appear exactly once so Full remains unfiltered.'
"@
Replace-ExactlyOnce $psTests $psAnchor $psInsert

git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
Write-Host 'Applied bounded Fast-profile edits.'
