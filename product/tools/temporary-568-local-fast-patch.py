from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one match, found {count}")
    return text.replace(old, new, 1)


wrapper_path = Path("product/scripts/Get-AeroLinkTestPlan.ps1")
text = wrapper_path.read_text(encoding="utf-8")

marker = "function Invoke-FastStep {"
loader = r'''function Get-FastValidationManifest {
    $manifestPath = Join-Path $repositoryRoot 'product\test-planner\fast-ci-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Fast validation manifest is missing.' }
    try { $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -Depth 20 }
    catch { throw 'Fast validation manifest could not be parsed.' }
    if ($manifest.id -ne 'aerolink-fast-ci/v1' -or $manifest.authoritative -ne $false -or [int64]$manifest.targetMs -ne 240000) { throw 'Fast validation manifest identity or authority is invalid.' }
    if ($manifest.safety.persistentPostgreSql -ne 'forbidden' -or $manifest.safety.persistentEvidenceRoot -ne 'forbidden') { throw 'Fast validation manifest persistent-resource safety is invalid.' }
    return $manifest
}

'''
text = replace_once(text, marker, loader + marker, "Invoke-FastStep insertion")

old_function = r'''function Invoke-FastStep {
    param([Parameter(Mandatory)]$Step)
    if ($Step.fullOnly) {
        Write-Host "  [CI-only in Fast] $($Step.label): $($Step.why)" -ForegroundColor Yellow
        return
    }
    switch ($Step.label) {
        'Build the solution' { Invoke-CheckedProcess 'dotnet' @('build', 'product/AeroLink.slnx', '--configuration', 'Release') }
        'Domain suite' { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Domain.Tests', '--configuration', 'Release', '--no-build') }
        'Infrastructure suite' { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build') }
        'Client lint, type-check and build' { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'lint'); Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'build') }
        'Browser smoke journeys' { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:smoke') }
        default { Write-Host "  [CI-only] $($Step.label): $($Step.why)" -ForegroundColor Yellow }
    }
}
'''
new_function = r'''function Invoke-FastStep {
    param([Parameter(Mandatory)]$Step)
    if ($Step.fullOnly) {
        Write-Host "  [CI-only in Fast] $($Step.label): $($Step.why)" -ForegroundColor Yellow
        return
    }
    $fastManifest = Get-FastValidationManifest
    switch ($Step.label) {
        'Build the solution' { Invoke-CheckedProcess 'dotnet' @('build', [string]$fastManifest.backend.build, '--configuration', 'Release') }
        'Domain suite' { Invoke-CheckedProcess 'dotnet' @('test', [string]$fastManifest.backend.domainProject, '--configuration', 'Release', '--no-build') }
        'Infrastructure suite' {
            $infraFilter = @($fastManifest.backend.infrastructureClasses | ForEach-Object { "FullyQualifiedName~AeroLink.Infrastructure.Tests.$_" }) -join '|'
            Invoke-CheckedProcess 'dotnet' @('test', [string]$fastManifest.backend.infrastructureProject, '--configuration', 'Release', '--no-build', '--filter', $infraFilter)
            $apiFilter = @($fastManifest.backend.apiClasses | ForEach-Object { "FullyQualifiedName~AeroLink.Api.Tests.$_" }) -join '|'
            Invoke-CheckedProcess 'dotnet' @('test', [string]$fastManifest.backend.apiProject, '--configuration', 'Release', '--no-build', '--filter', $apiFilter)
        }
        'Client lint, type-check and build' {
            Invoke-CheckedProcess 'npm.cmd' @('--prefix', [string]$fastManifest.client.workingDirectory, 'run', 'lint')
            Invoke-CheckedProcess 'npm.cmd' @('--prefix', [string]$fastManifest.client.workingDirectory, 'run', 'typecheck')
        }
        'Browser smoke journeys' { Write-Host '  [Full-only in Fast manifest] Browser smoke journeys remain protected GitHub evidence.' -ForegroundColor Yellow }
        default { Write-Host "  [CI-only] $($Step.label): $($Step.why)" -ForegroundColor Yellow }
    }
}
'''
text = replace_once(text, old_function, new_function, "Invoke-FastStep replacement")

old_loop = r'''        if ($Mode -eq 'Fast') {
            foreach ($step in $plan.local) {
                if ($step.label -eq 'Nothing') { continue }
                if (-not $step.fullOnly) {
                    $ciJobs = @(Get-CiJobsForStep $step.label | Where-Object { (Get-StringArray $plan.compact.ci.selected) -contains $_ })
                    Invoke-TimedAction -Label $step.label -CiJobs $ciJobs -Action { Invoke-FastStep -Step $step }
                }
                else { Invoke-FastStep -Step $step }
            }
        }
'''
new_loop = r'''        if ($Mode -eq 'Fast') {
            $null = Get-FastValidationManifest
            foreach ($step in $plan.local) {
                if ($step.label -eq 'Nothing') { continue }
                if ($step.label -eq 'Browser smoke journeys') { Invoke-FastStep -Step $step; continue }
                if (-not $step.fullOnly) {
                    # Fast is a deliberately bounded subset. It never claims that a complete CI job has
                    # executed locally; every selected GitHub job remains merge evidence in ciOnlyJobs.
                    Invoke-TimedAction -Label $step.label -CiJobs @() -Action { Invoke-FastStep -Step $step }
                }
                else { Invoke-FastStep -Step $step }
            }
        }
'''
text = replace_once(text, old_loop, new_loop, "Fast execution loop replacement")
wrapper_path.write_text(text, encoding="utf-8", newline="")

test_path = Path("product/scripts/Get-AeroLinkTestPlan.Tests.ps1")
tests = test_path.read_text(encoding="utf-8")
test_marker = "Assert-True (Test-Path -LiteralPath (Join-Path $root 'TEST_AEROLINK_CHANGED.bat') -PathType Leaf) 'Friendly root BAT entry point is missing.'"
extra = r'''$plannerSource = Get-Content -Raw -LiteralPath $scriptPath
Assert-True ($plannerSource -match 'fast-ci-manifest\.json') 'Local Fast must consume the shared Fast manifest.'
Assert-True ($plannerSource -match 'infrastructureClasses') 'Local Fast must use the reviewed Infrastructure smoke list.'
Assert-True ($plannerSource -match 'apiClasses') 'Local Fast must use the reviewed hosted API smoke list.'
Assert-True ($plannerSource -match "'run', 'typecheck'") 'Local Fast client validation must include type-check.'
Assert-True ($plannerSource -match 'Full-only in Fast manifest') 'Local Fast must explicitly defer browser smoke to Full evidence.'
Assert-True ($plannerSource -match 'Invoke-TimedAction -Label \$step\.label -CiJobs @\(\)') 'Local Fast must not claim complete CI jobs as locally executed.'
'''.rstrip("\n")
tests = replace_once(tests, test_marker, test_marker + "\n\n" + extra, "Windows planner test insertion")
test_path.write_text(tests, encoding="utf-8", newline="")
