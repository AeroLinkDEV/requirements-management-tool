#requires -Version 5.1
<#
.SYNOPSIS
  READ-ONLY deployed-acceptance check for issue #1040 (R12) on HOME.

.DESCRIPTION
  Collects every fact R12 needs:
    - the deployed source identity;
    - its relation to the #1066 merge (92ffbb6c);
    - health and runtime identity;
    - migration state;
    - the picker triggers and sequence;
    - the legacy cohort;
    - the latest upgrade and backup receipts.

  It writes one report file, outside every repository, for Sean to paste back.

  It changes nothing:
    - Git reads use GIT_OPTIONAL_LOCKS=0.
    - The PostgreSQL session is forced read-only (PGOPTIONS default_transaction_read_only=on), and every
      statement is a SELECT.
    - HTTP calls are GETs to loopback only.
    - It does not start, stop or update anything, run the application, build, or run migrations.
  The picker browser check stays a manual, read-only step. Do not save a link.
#>
[CmdletBinding()]
param(
    [string]$ProductionSource = 'C:\Sean Project\AeroLink Production',
    [string]$ApiBase = 'http://127.0.0.1:5080',
    [int]$PostgresPort = 54329,
    [string]$Database = 'aerolink',
    [string]$Psql = '',
    [string]$OutFile = ('C:\Sean Project\RMT-1040-glm-evidence\home-acceptance-' +
        (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '.txt')
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$env:GIT_OPTIONAL_LOCKS = '0'
$MergeSha = '92ffbb6cefa58a36b9573ea59ceb155704c3538b'
$Migration = '20260921140636_AddReleasePickerMembership'

if (Test-Path -LiteralPath $OutFile) { throw "Refusing to overwrite $OutFile" }
$report = New-Object System.Collections.Generic.List[string]
$verdicts = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$line) { $report.Add($line); Write-Host $line }
function Verdict([string]$name, [bool]$ok, [string]$detail) {
    $v = '{0,-4} {1} :: {2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail
    $verdicts.Add($v); Write-Host $v
}
$gitExe = (Get-Command git -CommandType Application | Select-Object -First 1).Path
function Invoke-SourceGit([string[]]$GitArgs) {
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & $gitExe -C $ProductionSource @GitArgs 2>&1 | ForEach-Object { "$_" }; return [pscustomobject]@{ Exit = $LASTEXITCODE; Out = (@($o) -join "`n").Trim() } }
    finally { $ErrorActionPreference = $saved }
}

Add-Line "#1040 HOME acceptance report  $((Get-Date).ToUniversalTime().ToString('o'))"
Add-Line "Production source: $ProductionSource"

# ---- 1. Deployed source identity and relation to the #1066 merge -------------------------------------
$head = Invoke-SourceGit @('rev-parse', 'HEAD'); Add-Line "source HEAD: $($head.Out)"
$branch = Invoke-SourceGit @('symbolic-ref', '-q', '--short', 'HEAD'); Add-Line "source branch: $($branch.Out)"
$dirty = Invoke-SourceGit @('--no-optional-locks', 'status', '--porcelain'); Add-Line "source status entries: $(@($dirty.Out -split "`n" | Where-Object { $_ }).Count)"
$tracking = Invoke-SourceGit @('rev-parse', 'refs/remotes/origin/main'); Add-Line "source origin/main (cached): $($tracking.Out)"
$anc = Invoke-SourceGit @('merge-base', '--is-ancestor', $MergeSha, 'HEAD')
Verdict 'deployed source contains #1066 merge 92ffbb6c' ($anc.Exit -eq 0) "merge-base --is-ancestor exit $($anc.Exit)"
Add-Line ("commits after the #1066 merge: " + (Invoke-SourceGit @('rev-list', '--count', "$MergeSha..HEAD")).Out)
$currency = Join-Path $ProductionSource 'product\.local\main-currency.json'
if (Test-Path -LiteralPath $currency) { Add-Line "main-currency.json: $((Get-Content -LiteralPath $currency -Raw).Trim())" } else { Add-Line 'main-currency.json: not present' }

# ---- 2. Serving health and runtime identity (loopback GETs only) -------------------------------------
foreach ($path in '/health/ready', '/health/identity') {
    try {
        $r = Invoke-WebRequest -Uri ($ApiBase + $path) -Method Get -UseBasicParsing -TimeoutSec 15
        Add-Line "GET $path -> $($r.StatusCode) $($r.Content)"
        if ($path -eq '/health/ready') { Verdict 'API ready' ($r.StatusCode -eq 200) "$($r.StatusCode)" }
        if ($path -eq '/health/identity') { Verdict 'running identity names the deployed source HEAD' ($r.Content -match [regex]::Escape($head.Out)) 'identity content contains source HEAD' }
    } catch { Add-Line "GET $path -> ERROR $($_.Exception.Message)"; Verdict "GET $path" $false $_.Exception.Message }
}

# ---- 3. Database state through a forced read-only session -------------------------------------------
if ([string]::IsNullOrWhiteSpace($Psql)) {
    Import-Module (Join-Path $ProductionSource 'product\scripts\AeroLinkInstallation.psm1') -Force
    $install = Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $ProductionSource 'product')
    $Psql = Join-Path $install.PostgresBin 'psql.exe'
    Add-Line "installation root: $($install.InstallationRoot)"
    foreach ($dir in @($install.UpgradeState, $install.Backups)) {
        if (Test-Path -LiteralPath $dir) {
            Add-Line "latest entries in ${dir}:"
            Get-ChildItem -LiteralPath $dir | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 6 |
                ForEach-Object { Add-Line ('  {0:o}  {1}' -f $_.LastWriteTimeUtc, $_.Name) }
        }
    }
}
$env:PGOPTIONS = '-c default_transaction_read_only=on'
function Sql([string]$query) {
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $o = & $Psql -h 127.0.0.1 -p $PostgresPort -U postgres -d $Database -v ON_ERROR_STOP=1 -At -c $query 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) { throw ("psql failed: " + (@($o) -join ' ')) }
        return (@($o) -join "`n").Trim()
    } finally { $ErrorActionPreference = $saved }
}
$ro = Sql 'SHOW default_transaction_read_only'
Verdict 'database session is read-only' ($ro -eq 'on') $ro
$latest = Sql 'SELECT max("MigrationId") FROM "__EFMigrationsHistory"'
$applied = Sql "SELECT count(*) FROM ""__EFMigrationsHistory"" WHERE ""MigrationId"" = '$Migration'"
Add-Line "latest migration: $latest"
Verdict "migration $Migration applied" ($applied -eq '1') "rows=$applied"
$triggers = Sql "SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid = 'software_releases'::regclass AND NOT tgisinternal"
Add-Line "software_releases triggers: $triggers"
Verdict 'picker triggers installed and enabled' ($triggers -match 'aerolink_release_picker_alloc_ins:O' -and $triggers -match 'aerolink_release_picker_immutable_upd:O') $triggers
$seq = Sql "SELECT last_value || ',' || is_called FROM aerolink_release_picker_ordinal_seq"
Add-Line "ordinal sequence last_value,is_called: $seq"
$counts = Sql 'SELECT count(*) || '','' || count(*) FILTER (WHERE "PickerInsertionOrdinal" IS NULL) || '','' || count(*) FILTER (WHERE "PickerInsertionOrdinal" IS NOT NULL) FROM software_releases'
$parts = $counts -split ','
Add-Line "releases total,legacy(NULL),allocated: $counts"
Verdict 'existing releases kept as legacy cohort' ([int]$parts[1] -ge 1 -and [int]$parts[2] -eq 0) 'expect every pre-upgrade row NULL; allocated > 0 only if builds were created after the upgrade (then report them)'
if ([int]$parts[2] -gt 0) {
    Add-Line 'builds that received an ordinal (created after the upgrade; expected only for genuinely new builds):'
    (Sql 'SELECT "Version" || '' | ordinal '' || "PickerInsertionOrdinal" || '' | project '' || "ProjectId" FROM software_releases WHERE "PickerInsertionOrdinal" IS NOT NULL ORDER BY "PickerInsertionOrdinal"') -split "`n" | ForEach-Object { Add-Line "  $_" }
}
Add-Line 'per-project release counts (read-only):'
(Sql 'SELECT p."Name" || '' | '' || count(r.*) || '' builds | legacy '' || count(r.*) FILTER (WHERE r."PickerInsertionOrdinal" IS NULL) FROM projects p LEFT JOIN software_releases r ON r."ProjectId" = p."Id" GROUP BY p."Name" ORDER BY p."Name"') -split "`n" | ForEach-Object { Add-Line "  $_" }

# ---- 4. Manual, read-only browser step --------------------------------------------------------------
Add-Line ''
Add-Line 'MANUAL (read-only): open an existing FMS document, then + Link artifact -> Build. Existing builds should'
Add-Line 'appear in canonical order (e.g. 1.5 before 1.6) with correct Released/In-work labels. Do NOT save a link.'
Add-Line ''
Add-Line 'VERDICTS:'
$verdicts | ForEach-Object { $report.Add($_) }
$report | Set-Content -LiteralPath $OutFile -Encoding UTF8
Write-Host "Report written to $OutFile (read-only run; nothing was changed)."
