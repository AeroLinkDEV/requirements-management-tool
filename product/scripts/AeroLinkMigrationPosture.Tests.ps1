#Requires -Version 5.1
<#
    Deterministic regression coverage for the migration-posture probe in
    AeroLinkMigrationPosture.psm1. Self-contained (no Pester dependency) so it runs
    under the repository's Windows PowerShell 5.1 gate and under PowerShell 7.

    It substitutes a fake psql script that records the exact SQL it received on stdin,
    so the mixed-case identifier preservation, numeric parsing, native-command failure
    handling, non-numeric handling, and the no-mutation guarantee are all proven
    without a live PostgreSQL instance.
#>
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'AeroLinkMigrationPosture.psm1'
Import-Module $modulePath -Force

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("aerolink-migration-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

$stubPath = Join-Path $tempRoot 'fake-psql.ps1'
$capturedSqlPath = Join-Path $tempRoot 'captured-sql.txt'

@'
$ErrorActionPreference = 'Continue'
$sql = ($input | Out-String)
[System.IO.File]::WriteAllText($env:AEROLINK_FAKE_CAPTURE, $sql)
$behavior = $env:AEROLINK_FAKE_BEHAVIOR
if ($sql -match '(?i)\b(INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|TRUNCATE|GRANT|REVOKE)\b') {
    'MUTATION-SQL'
    exit 2
}
switch ($behavior) {
    'exit1' { Write-Error 'relation "__efmigrationshistory" does not exist'; exit 1 }
    'bad'   { 'not-a-number'; exit 0 }
    'empty' { exit 0 }
    default { '89'; exit 0 }
}
'@ | Set-Content -LiteralPath $stubPath -Encoding UTF8

$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

# 1. Success path: quoted mixed-case identifier preserved, numeric result parsed.
$env:AEROLINK_FAKE_BEHAVIOR = 'success'
$env:AEROLINK_FAKE_CAPTURE = $capturedSqlPath
$result = Get-AeroLinkMigrationCount -PsqlPath $stubPath
Assert-True ($result.Healthy -eq $true) 'Success path should be healthy.'
Assert-True ($result.Count -eq 89) "Expected count 89, got $($result.Count)."
Assert-True ($result.Detail -eq '89 applied migration(s)') "Unexpected detail: $($result.Detail)"
$captured = [System.IO.File]::ReadAllText($capturedSqlPath)
$capturedTrimmed = $captured.Trim()
Assert-True ($capturedTrimmed -eq 'SELECT COUNT(*) FROM "__EFMigrationsHistory"') "Unexpected SQL captured: $capturedTrimmed"
Assert-True ($captured -cnotmatch '__efmigrationshistory') 'Lowercase table-name fallback was used.'
Assert-True ($captured -notmatch '(?i)\b(INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|TRUNCATE|GRANT|REVOKE)\b') 'Mutation SQL was issued.'

# 2. Nonzero native-command exit handled as failure.
$env:AEROLINK_FAKE_BEHAVIOR = 'exit1'
$result = Get-AeroLinkMigrationCount -PsqlPath $stubPath
Assert-True ($result.Healthy -eq $false) 'Nonzero psql exit should be unhealthy.'
Assert-True ($result.Detail -match 'psql exited 1') "Unexpected detail: $($result.Detail)"

# 3. Malformed/non-numeric result handled as failure.
$env:AEROLINK_FAKE_BEHAVIOR = 'bad'
$result = Get-AeroLinkMigrationCount -PsqlPath $stubPath
Assert-True ($result.Healthy -eq $false) 'Non-numeric output should be unhealthy.'
Assert-True ($result.Detail -match 'non-numeric') "Unexpected detail: $($result.Detail)"

# 4. Empty result handled as failure.
$env:AEROLINK_FAKE_BEHAVIOR = 'empty'
$result = Get-AeroLinkMigrationCount -PsqlPath $stubPath
Assert-True ($result.Healthy -eq $false) 'Empty output should be unhealthy.'

# 5. Missing executable handled as failure.
$result = Get-AeroLinkMigrationCount -PsqlPath (Join-Path $tempRoot 'missing-psql.exe')
Assert-True ($result.Healthy -eq $false) 'Missing psql should be unhealthy.'
Assert-True ($result.Detail -match 'not found') "Unexpected detail: $($result.Detail)"

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "Migration posture regression FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}

Write-Host 'Migration posture regression passed.' -ForegroundColor Green
exit 0
