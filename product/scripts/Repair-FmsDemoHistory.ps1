[CmdletBinding()]
param(
    [string]$Database = 'aerolink',
    [int]$PostgresPort = 54329,
    [Parameter(Mandatory)][string]$PostgresBin,
    [Parameter(Mandatory)][string]$EvidenceRoot,
    [Parameter(Mandatory)][string]$ReceiptDirectory,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if ($Database -notmatch '^[A-Za-z][A-Za-z0-9_]{0,62}$') { throw 'Unsafe database name.' }
if ($PostgresPort -lt 1 -or $PostgresPort -gt 65535) { throw 'Invalid PostgreSQL port.' }
$PostgresBin = (Resolve-Path -LiteralPath $PostgresBin).Path
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$receiptRoot = [IO.Path]::GetFullPath($ReceiptDirectory)
New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
$psql = Join-Path $PostgresBin 'psql.exe'
if (-not (Test-Path -LiteralPath $psql)) { throw 'PostgreSQL client is unavailable.' }
$runId = [Guid]::NewGuid().ToString('N')
$sql = Join-Path $receiptRoot "$runId-correction.sql"
[IO.File]::WriteAllText($sql, [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Repair-FmsDemoHistory.sql')))
$scriptHash = (Get-FileHash -LiteralPath $sql -Algorithm SHA256).Hash
$preview = Join-Path $receiptRoot "$runId-preview.txt"
$arguments = @('-X', '-w', '-h', '127.0.0.1', '-p', "$PostgresPort", '-U', 'postgres', '-d', $Database,
    '-v', 'ON_ERROR_STOP=1', '-v', 'apply_correction=false', '-f', $sql)
& $psql @arguments *> $preview
if ($LASTEXITCODE -ne 0) { throw "Correction preview refused. Evidence: $preview" }
$manifestMatch = [regex]::Match([IO.File]::ReadAllText($preview), 'MANIFEST:([a-f0-9]{32})')
if (-not $manifestMatch.Success) { throw 'The exact preview manifest hash is missing.' }
Get-Content -LiteralPath $preview | Write-Output
if (-not $Apply) { Write-Output "Preview complete; exact manifest: $preview"; return }
$targetCount = [regex]::Match([IO.File]::ReadAllText($preview), 'TARGET_COUNT:([0-9]+)')
if (-not $targetCount.Success) { throw 'The exact manifest row count is missing.' }
if ([long]$targetCount.Groups[1].Value -eq 0) { Write-Output 'No historical correction remains; no changes made.'; return }

# This operation intentionally supersedes history preservation for the eight owned synthetic
# Interface aggregates only (#1006). There is no SkipBackup or normal application bypass.
$backupRoot = Join-Path $receiptRoot "$runId-backup"
$backup = & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -Database $Database -PostgresPort $PostgresPort `
    -PostgresBin $PostgresBin -EvidenceRoot $EvidenceRoot -BackupRoot $backupRoot -RetentionDays 0 -PostgresAlreadyRunning
$artifact = @($backup | Where-Object { $_.PSObject.Properties.Name -contains 'Archive' })
if ($artifact.Count -ne 1 -or -not $artifact[0].Archive) { throw 'The exact backup archive was not returned; correction refused.' }
$verified = & (Join-Path $PSScriptRoot 'Verify-AeroLinkBackup.ps1') -BackupArchive $artifact[0].Archive `
    -VerificationRoot (Join-Path $receiptRoot "$runId-verification")
if (-not $verified.Valid) { throw 'Backup verification failed; correction refused.' }
$result = Join-Path $receiptRoot "$runId-applied.txt"
if ((Get-FileHash -LiteralPath $sql -Algorithm SHA256).Hash -ne $scriptHash) { throw 'The pinned correction SQL changed; correction refused.' }
$arguments = @('-X', '-w', '-h', '127.0.0.1', '-p', "$PostgresPort", '-U', 'postgres', '-d', $Database,
    '-v', 'ON_ERROR_STOP=1', '-v', 'apply_correction=true', '-v', "expected_manifest=$($manifestMatch.Groups[1].Value)", '-f', $sql)
& $psql @arguments *> $result
if ($LASTEXITCODE -ne 0) { throw "Correction rolled back. Evidence: $result; backup: $($artifact[0].Archive)" }
[pscustomobject]@{
    Applied = $true; Database = $Database; PostgresPort = $PostgresPort
    Preview = $preview; Result = $result; BackupArchive = $artifact[0].Archive
    CorrectionSqlSha256 = $scriptHash
    BackupSha256 = $verified.ArchiveSha256; CompletedAt = [DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $receiptRoot "$runId-receipt.json") -Encoding UTF8
Write-Output "Correction completed with unchanged-row and trigger-mode proof. Receipt: $receiptRoot"
