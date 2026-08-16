# Temporary branch-only helper used to transplant the reviewed #618 Docker compatibility guard onto current main.
$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce([string]$Path, [string]$Old, [string]$New) {
    $text = [IO.File]::ReadAllText($Path)
    $count = ($text.Split($Old).Count - 1)
    if ($count -ne 1) { throw "Expected exactly one patch anchor in $Path; found $count." }
    [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), [Text.UTF8Encoding]::new($false))
}

$planner = 'product/scripts/Get-AeroLinkTestPlan.ps1'
$old = @'
    try { & $dockerCommand.Source version --format '{{.Server.Version}}' *> $null; if ($LASTEXITCODE -ne 0) { throw 'daemon unavailable' } }
    catch { throw 'Docker is unavailable; the daemon could not be queried, so the PostgreSQL gate is not-proven.' }
    return $dockerCommand.Source
'@
$new = @'
    try { & $dockerCommand.Source version --format '{{.Server.Version}}' *> $null; if ($LASTEXITCODE -ne 0) { throw 'daemon unavailable' } }
    catch { throw 'Docker is unavailable; the daemon could not be queried, so the PostgreSQL gate is not-proven.' }
    try {
        $serverOsType = ((& $dockerCommand.Source info --format '{{.OSType}}' 2>$null) -join '').Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($serverOsType)) { throw 'server OS type unavailable' }
    }
    catch { throw 'Docker is unavailable; the daemon OS type could not be verified, so the PostgreSQL gate is not-proven.' }
    if ($serverOsType -cne 'linux') {
        throw "Docker server OSType '$serverOsType' cannot run the required Linux postgres:17 image; the PostgreSQL gate is not-proven. Switch Docker Desktop to Linux containers before Full mode."
    }
    return $dockerCommand.Source
'@
Replace-ExactlyOnce $planner $old $new

$tests = 'product/scripts/Get-AeroLinkTestPlan.Tests.ps1'
$anchor = "Assert-True (Test-Path -LiteralPath (Join-Path `$root 'TEST_AEROLINK_CHANGED.bat') -PathType Leaf) 'Friendly root BAT entry point is missing.'"
$insert = @"
`$plannerSource = Get-Content -Raw -LiteralPath `$scriptPath
Assert-True (`$plannerSource -match \"info --format '\{\{\.OSType\}\}'\") 'Full PostgreSQL preflight must inspect the Docker server OS type before expensive gates.'
Assert-True (`$plannerSource -match \"serverOsType -cne 'linux'\") 'Full PostgreSQL preflight must refuse a non-Linux Docker daemon.'
Assert-True (`$plannerSource -match 'Switch Docker Desktop to Linux containers before Full mode') 'Docker incompatibility must give the developer an actionable recovery message.'
$anchor
"@
Replace-ExactlyOnce $tests $anchor $insert

git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
Write-Host 'Applied clean Docker compatibility preflight.'
