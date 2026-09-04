#Requires -Version 5.1
<#
    The launcher's side of the database upgrade contract.

    PowerShell orchestrates; it does not decide domain truth. Every question about what an upgrade means —
    what is pending, what is deterministic, what is a conflict, what a conflict's supported decisions are — is
    answered by the AeroLink maintenance host, which is the application's own code with its own authorities.
    Nothing here inspects a table or writes SQL.

    The flow #881 asks for, and why each step is where it is:

        persistent local database
            |  read-only analysis                     seconds, not a readiness timeout
            v
        upgrade needed?  -- no --> start normally
            |
            |  supported verified backup              the recoverable point, taken while the original is intact
            v
        isolated restored copy                        the real upgrade is never attempted first on real data
            |  apply THIS build's migrations + semantic upgrades
            |  verify the copy is then current
            v
        PASS? -- no --> real database untouched, exact reason reported
            |
            yes
            v
        apply the same upgrade to the real database, then verify

    A failed clone validation leaves the persistent database and evidence exactly as they were, because
    nothing had touched them yet. That is a property of the ordering, not a promise made afterwards.
#>

Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force

function Invoke-AeroLinkMaintenanceCommand {
    <#
        .SYNOPSIS Runs the AeroLink maintenance host and returns its exit code and captured output.
        .DESCRIPTION
            -ConnectionString targets another database (the isolated clone) without touching configuration
            files, through the same ConnectionStrings__AeroLink the application already reads. The value is
            never echoed: it is a credentialed string, and this module's output is written to operator logs.

            `dotnet run` builds before it runs, into the same output directory a live AeroLink API holds
            open, so a running instance makes the build fail with a file lock and the caller sees no analysis
            at all. The launchers therefore stop a stale API before asking, and skip asking entirely when
            they are reusing a matching one. When a build failure does occur, its output is returned rather
            than swallowed, so the caller can say why instead of reporting an unexplained "unreachable".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments,
        [string]$ConnectionString,
        [string]$DotnetPath = 'dotnet',
        [int]$TimeoutSeconds = 1800
    )
    $project = Join-Path $ProductRoot 'src\AeroLink.Api\AeroLink.Api.csproj'
    $previousConnection = $env:ConnectionStrings__AeroLink
    $previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
    if ($ConnectionString) { $env:ConnectionStrings__AeroLink = $ConnectionString }
    if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }
    try {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $DotnetPath
        $startInfo.Arguments = "run --project `"$project`" --no-launch-profile -- " + ($Arguments -join ' ')
        $startInfo.WorkingDirectory = $ProductRoot
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($startInfo)
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            return [pscustomobject]@{ ExitCode = -1; StdOut = ''; StdErr = "The maintenance host exceeded $TimeoutSeconds seconds and was stopped." }
        }
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = $stdoutTask.Result; StdErr = $stderrTask.Result }
    }
    finally {
        $env:ConnectionStrings__AeroLink = $previousConnection
        $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    }
}

function Get-AeroLinkUpgradeAnalysis {
    <#
        .SYNOPSIS Read-only upgrade posture, in seconds, without starting a web server.
        .DESCRIPTION
            Returns the parsed maintenance analysis plus Status: current, upgrade-required, conflict or
            unreachable. The exit code is the contract, so a known refusal never becomes a readiness wait.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [string]$ConnectionString,
        [string]$DotnetPath = 'dotnet',
        [scriptblock]$CommandRunner
    )
    $run = if ($CommandRunner) { & $CommandRunner @('maintenance', 'analyze', '--json') $ConnectionString } else {
        Invoke-AeroLinkMaintenanceCommand -ProductRoot $ProductRoot -Arguments @('maintenance', 'analyze', '--json') -ConnectionString $ConnectionString -DotnetPath $DotnetPath
    }
    # The host may print build or startup noise before its JSON, so take the object rather than the whole
    # transcript. Anchored on the LINE whose trimmed content is an opening brace, and rejoined from that
    # line's index: searching the transcript for the text of that line finds the first '{' anywhere, so a
    # brace inside an earlier MSBuild or restore message would hand ConvertFrom-Json a substring starting
    # mid-log-line, and the analysis would be reported as unreachable for no reason.
    $lines = $run.StdOut -split "`r?`n"
    $jsonLineIndex = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index].Trim() -eq '{') { $jsonLineIndex = $index; break }
    }
    if ($jsonLineIndex -lt 0) {
        # A build failure prints to stdout, not stderr, so quoting only stderr would report "no analysis"
        # with no reason at all - which is exactly how a file-locked build would look.
        $tail = (@($lines | Where-Object { $_ -and $_.Trim() }) | Select-Object -Last 5) -join ' '
        return [pscustomobject]@{
            Status = 'unreachable'; Analysis = $null; ExitCode = $run.ExitCode
            Detail = "The maintenance host produced no analysis. $($run.StdErr) $tail".Trim()
        }
    }
    $jsonText = ($lines[$jsonLineIndex..($lines.Count - 1)] -join "`n")
    try { $analysis = $jsonText | ConvertFrom-Json }
    catch {
        return [pscustomobject]@{ Status = 'unreachable'; Analysis = $null; ExitCode = $run.ExitCode; Detail = "The maintenance analysis could not be read: $($_.Exception.Message)" }
    }
    # Strict mode: read through PSObject.Properties so a host that omits an optional field is a missing
    # detail rather than a launcher crash.
    $reason = if ($analysis.PSObject.Properties['unreachableReason']) { [string]$analysis.unreachableReason } else { $null }
    return [pscustomobject]@{
        Status   = [string]$analysis.status
        Analysis = $analysis
        ExitCode = $run.ExitCode
        Detail   = if ($reason) { $reason } else { "Upgrade posture: $($analysis.status)." }
    }
}

function Write-AeroLinkUpgradeConflictReport {
    <#
        .SYNOPSIS The operator block #881 asks for in place of a stack trace.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Analysis)
    Write-Host ''
    Write-Host 'DATABASE ATTENTION REQUIRED' -ForegroundColor Yellow
    foreach ($conflict in @($Analysis.conflicts)) {
        Write-Host ''
        Write-Host "Conflict: $($conflict.Code)" -ForegroundColor Yellow
        foreach ($property in $conflict.Subject.PSObject.Properties) {
            if ($property.Name -like '*Id' -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { continue }
            Write-Host ("  {0}: {1}" -f $property.Name, $property.Value)
        }
        Write-Host "  $($conflict.Summary)"
        if (@($conflict.Choices).Count -gt 0) {
            Write-Host '  Supported decisions:'
            foreach ($choice in @($conflict.Choices)) {
                $note = if ($choice.GrantsNewAuthority) { ' (grants authority somebody does not have today)' } else { '' }
                Write-Host "    [$($choice.Key)] $($choice.Description)$note"
            }
        }
    }
    Write-Host ''
    Write-Host 'AeroLink made NO authority decision automatically.' -ForegroundColor Yellow
    Write-Host 'No persistent data was changed.' -ForegroundColor Yellow
}

function Invoke-AeroLinkCloneValidatedUpgrade {
    <#
        .SYNOPSIS Proves this build's upgrade on an isolated restored copy, then applies it for real.
        .DESCRIPTION
            Backup and restore go through the supported scripts — Backup-AeroLink.ps1, Verify-AeroLinkBackup.ps1
            and Restore-AeroLink.ps1 — rather than a casual pg_dump/pg_restore pair written here. Those already
            verify archive integrity, attachment inventory, evidence hashes and restored downloads, and have
            their own fault-injection qualification; a parallel path would have none of that and would drift.

            Ordering is the safety property. The real database is not touched until the same upgrade has run
            green on a copy, so a failure at any earlier step leaves persistent data exactly as found.

            The disposable database and its isolated evidence are cleaned up on every path. The persistent
            database is never a cleanup target.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [string]$DotnetPath = 'dotnet',
        [int]$PostgresPort = 54329,
        [string]$Database = 'aerolink',
        # Test seams. Each returns the same shape as the real step so the contract suite can drive every
        # failure without a PostgreSQL server.
        [scriptblock]$BackupRunner,
        [scriptblock]$RestoreRunner,
        [scriptblock]$UpgradeRunner,
        [scriptblock]$AnalysisRunner,
        [scriptblock]$CleanupRunner,
        # Stands current AeroLink up against the UPGRADED clone and proves it works there.
        [scriptblock]$CurrentCodeValidator,
        [int]$ValidationApiPort = 5093
    )
    $installation = Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot
    $token = [Guid]::NewGuid().ToString('N').Substring(0, 10)
    $cloneDatabase = "aerolink_upgrade_validation_$token"
    $cloneConnection = "Host=127.0.0.1;Port=$PostgresPort;Database=$cloneDatabase;Username=postgres"

    Write-Host '      Protecting the current database with a verified backup...' -ForegroundColor Cyan
    $archive = if ($BackupRunner) { & $BackupRunner } else {
        & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -PostgresAlreadyRunning | Out-Host
        (Get-ChildItem -LiteralPath $installation.Backups -Filter 'aerolink-*.zip' -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    }
    if (-not $archive) {
        return [pscustomobject]@{ Validated = $false; Applied = $false; Archive = $null; Detail = 'The pre-upgrade backup did not produce an archive. The database was not changed.' }
    }
    Write-Host "      Backup verified: $archive" -ForegroundColor Green

    $cleanedUp = $false
    try {
        Write-Host '      Restoring an isolated copy to validate the upgrade on...' -ForegroundColor Cyan
        # -SkipCurrentCodeValidation: this copy is deliberately NOT current yet. The restore validator stands
        # up current AeroLink read-only and refuses a database with pending migrations, which would make the
        # "several migrations behind" case — the case this whole path exists for — fail before the upgrade
        # was ever attempted. Archive integrity, inventory and evidence are still proved by the restore; what
        # moves later is proving current code against the copy, which happens below once it IS current.
        $restored = if ($RestoreRunner) { & $RestoreRunner $archive $cloneDatabase } else {
            & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive $archive -TargetDatabase $cloneDatabase -PostgresPort $PostgresPort -SkipCurrentCodeValidation | Out-Host
            $true
        }
        if (-not $restored) {
            return [pscustomobject]@{ Validated = $false; Applied = $false; Archive = $archive; Detail = 'The isolated restore failed, so the upgrade was never attempted. The real database and evidence are unchanged.' }
        }

        Write-Host '      Applying this build''s upgrade to the isolated copy...' -ForegroundColor Cyan
        $cloneUpgrade = if ($UpgradeRunner) { & $UpgradeRunner $cloneConnection } else {
            Invoke-AeroLinkMaintenanceCommand -ProductRoot $ProductRoot -Arguments @('maintenance', 'upgrade', '--apply') -ConnectionString $cloneConnection -DotnetPath $DotnetPath
        }
        if ($cloneUpgrade.ExitCode -ne 0) {
            $reason = if ($cloneUpgrade.StdOut) { ($cloneUpgrade.StdOut -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 12) -join ' ' } else { $cloneUpgrade.StdErr }
            return [pscustomobject]@{
                Validated = $false; Applied = $false; Archive = $archive
                Detail = "The upgrade failed on the isolated copy, so the real database was never touched and is unchanged. $reason"
            }
        }

        $cloneAnalysis = if ($AnalysisRunner) { & $AnalysisRunner $cloneConnection } else {
            Get-AeroLinkUpgradeAnalysis -ProductRoot $ProductRoot -ConnectionString $cloneConnection -DotnetPath $DotnetPath
        }
        if ($cloneAnalysis.Status -ne 'current') {
            return [pscustomobject]@{
                Validated = $false; Applied = $false; Archive = $archive
                Detail = "The isolated copy is still not current after the upgrade ($($cloneAnalysis.Status)), so the real database was never touched and is unchanged."
            }
        }

        # "The analyzer says current" is not the same as "current AeroLink works against it". #881 asks for
        # readiness, authentication and storage/evidence invariants to be proved on the isolated copy before
        # the real database is mutated, and the restore validator cannot stand in for that: it ran before the
        # upgrade, against the un-migrated state.
        Write-Host '      Proving current AeroLink against the upgraded copy...' -ForegroundColor Cyan
        $cloneReadiness = if ($CurrentCodeValidator) { & $CurrentCodeValidator $cloneConnection } else {
            Test-AeroLinkUpgradedCloneReadiness -ProductRoot $ProductRoot -ConnectionString $cloneConnection `
                -DotnetPath $DotnetPath -ApiPort $ValidationApiPort
        }
        if (-not $cloneReadiness.Passed) {
            return [pscustomobject]@{
                Validated = $false; Applied = $false; Archive = $archive
                Detail = "The upgraded copy did not pass current-code validation ($($cloneReadiness.Detail)), so the real database was never touched and is unchanged."
            }
        }
        Write-Host "      Isolated upgrade validated: $($cloneReadiness.Detail)" -ForegroundColor Green
    }
    finally {
        # The disposable copy always goes, whether validation passed or failed. The persistent database is
        # never a cleanup target, and this block cannot name it: $cloneDatabase is generated above.
        if (-not $cleanedUp) {
            try {
                if ($CleanupRunner) { & $CleanupRunner $cloneDatabase }
                else { Remove-AeroLinkUpgradeValidationDatabase -ProductRoot $ProductRoot -Database $cloneDatabase -PostgresPort $PostgresPort }
            }
            catch { Write-Host "      The disposable validation copy could not be removed: $($_.Exception.Message)" -ForegroundColor Yellow }
            $cleanedUp = $true
        }
    }

    Write-Host '      Upgrading the local database...' -ForegroundColor Cyan
    $realUpgrade = if ($UpgradeRunner) { & $UpgradeRunner $null } else {
        Invoke-AeroLinkMaintenanceCommand -ProductRoot $ProductRoot -Arguments @('maintenance', 'upgrade', '--apply') -DotnetPath $DotnetPath
    }
    if ($realUpgrade.ExitCode -ne 0) {
        $reason = if ($realUpgrade.StdOut) { ($realUpgrade.StdOut -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 12) -join ' ' } else { $realUpgrade.StdErr }
        return [pscustomobject]@{
            Validated = $true; Applied = $false; Archive = $archive
            Detail = "The upgrade passed on the isolated copy but failed on the local database. The verified pre-upgrade backup is retained at $archive. $reason"
        }
    }
    return [pscustomobject]@{
        Validated = $true; Applied = $true; Archive = $archive
        Detail = "The upgrade was validated on an isolated copy and then applied. The pre-upgrade backup is retained at $archive."
    }
}

function Test-AeroLinkUpgradedCloneReadiness {
    <#
        .SYNOPSIS Stands current AeroLink up against the upgraded isolated copy and proves it actually works.
        .DESCRIPTION
            The last gate before the real database is mutated. The analyzer reporting "current" says the
            schema and the semantic markers line up; it does not say the application can serve anything on
            top of them. #881 asks for readiness, authentication availability and storage/evidence
            invariants on the isolated copy, and this is where that happens — after the clone is upgraded,
            which is the only point at which current code is entitled to expect the current schema.

            Bound to the clone: the connection string is the caller's isolated database, the port is a
            validation port rather than 5080, and the process is stopped again whatever the outcome. It never
            touches the persistent database, and it serves no client.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [Parameter(Mandatory)][string]$ConnectionString,
        [string]$DotnetPath = 'dotnet',
        [int]$ApiPort = 5093,
        [int]$TimeoutSeconds = 300
    )
    $installation = Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot
    New-Item -ItemType Directory -Path $installation.Logs -Force | Out-Null
    $project = Join-Path $ProductRoot 'src\AeroLink.Api\AeroLink.Api.csproj'
    $baseUri = "http://127.0.0.1:$ApiPort"
    $stdout = Join-Path $installation.Logs 'upgrade-validation.stdout.log'
    $stderr = Join-Path $installation.Logs 'upgrade-validation.stderr.log'

    $previousConnection = $env:ConnectionStrings__AeroLink
    $previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $env:ConnectionStrings__AeroLink = $ConnectionString
    if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }
    $process = $null
    try {
        $process = Start-Process -FilePath $DotnetPath `
            -ArgumentList "run --project `"$project`" --no-launch-profile --urls `"$baseUri`"" `
            -WorkingDirectory $ProductRoot -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $ready = $false
        while ((Get-Date) -lt $deadline) {
            if ($process.HasExited) { break }
            try {
                $health = Invoke-RestMethod -Uri "$baseUri/health/ready" -TimeoutSec 5 -UseBasicParsing
                if ($health.status -eq 'ready' -and $health.database -eq 'connected') { $ready = $true; break }
            }
            catch { }
            Start-Sleep -Seconds 3
        }
        if (-not $ready) {
            $tail = if (Test-Path -LiteralPath $stderr) { (Get-Content -LiteralPath $stderr -Tail 8) -join ' ' } else { '' }
            return [pscustomobject]@{ Passed = $false; Detail = "current AeroLink never became ready against the upgraded copy. $tail".Trim() }
        }

        # 401 is the expected answer to an unauthenticated caller, and it is the answer that proves the
        # authentication path is wired rather than merely that the process is listening.
        $authStatus = $null
        try { $authStatus = [int](Invoke-WebRequest -Uri "$baseUri/api/auth/me" -UseBasicParsing -TimeoutSec 10).StatusCode }
        catch {
            if ($_.Exception.Response -and $_.Exception.Response.StatusCode) { $authStatus = [int]$_.Exception.Response.StatusCode }
        }
        if ($authStatus -notin 200, 401) {
            return [pscustomobject]@{ Passed = $false; Detail = "the authentication endpoint answered $authStatus against the upgraded copy." }
        }

        # Controlled storage must still be coherent after the upgrade: every referenced attachment present,
        # the right size and hash, and no half-finished storage operation.
        Import-Module (Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1') -Force
        $builder = New-Object System.Data.Common.DbConnectionStringBuilder
        $builder.set_ConnectionString($ConnectionString)
        $cloneDatabaseName = [string]$builder['Database']
        $clonePort = if ($builder['Port']) { [int]$builder['Port'] } else { 54329 }
        $psql = Join-Path $installation.PostgresBin 'psql.exe'
        Assert-AeroLinkStorageLifecycleHealthy -Psql $psql -Database $cloneDatabaseName -Port $clonePort
        $inventory = @(Get-AeroLinkAttachmentInventory -Psql $psql -Database $cloneDatabaseName -Port $clonePort)
        $evidenceRoot = Join-Path $installation.RestoreValidation "$cloneDatabaseName\evidence"
        if ($inventory.Count -gt 0) {
            [void](Test-AeroLinkAttachmentInventory -Inventory $inventory -EvidenceRoot $evidenceRoot)
        }

        return [pscustomobject]@{
            Passed = $true
            Detail = "ready, authentication answering, $($inventory.Count) controlled attachment(s) verified against the upgraded copy."
        }
    }
    catch {
        return [pscustomobject]@{ Passed = $false; Detail = $_.Exception.Message }
    }
    finally {
        if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        $env:ConnectionStrings__AeroLink = $previousConnection
        $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    }
}

function Remove-AeroLinkUpgradeValidationDatabase {
    <#
        .SYNOPSIS Drops a disposable upgrade-validation database, and refuses to drop anything else.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [Parameter(Mandatory)][string]$Database,
        [int]$PostgresPort = 54329
    )
    # The name is the guard. A cleanup helper that can be handed 'aerolink' is a cleanup helper that will
    # eventually be handed 'aerolink'.
    if ($Database -notmatch '^aerolink_upgrade_validation_[a-f0-9]{10}$') {
        throw "Refusing to drop '$Database': only a generated AeroLink upgrade-validation database may be removed here."
    }
    $bin = (Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot).PostgresBin
    & (Join-Path $bin 'psql.exe') -h 127.0.0.1 -p $PostgresPort -U postgres -d postgres -v ON_ERROR_STOP=1 -tA `
        -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();" | Out-Null
    & (Join-Path $bin 'dropdb.exe') -h 127.0.0.1 -p $PostgresPort -U postgres --if-exists $Database
    if ($LASTEXITCODE -ne 0) { throw "Could not remove the disposable validation database '$Database'." }
    $validationEvidence = Join-Path (Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot).RestoreValidation $Database
    if (Test-Path -LiteralPath $validationEvidence -PathType Container) {
        Remove-Item -LiteralPath $validationEvidence -Recurse -Force
    }
}

function Remove-AeroLinkSnapshotStagingDatabase {
    <#
        .SYNOPSIS Drops a disposable HOME-snapshot staging database, and refuses to drop anything else.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [Parameter(Mandatory)][string]$Database,
        [int]$PostgresPort = 54329
    )
    if ($Database -notmatch '^aerolink_snapshot_validation_[a-f0-9]{10}$') {
        throw "Refusing to drop '$Database': only a generated AeroLink snapshot-staging database may be removed here."
    }
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot
    & (Join-Path $paths.PostgresBin 'psql.exe') -h 127.0.0.1 -p $PostgresPort -U postgres -d postgres -v ON_ERROR_STOP=1 -tA `
        -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();" | Out-Null
    & (Join-Path $paths.PostgresBin 'dropdb.exe') -h 127.0.0.1 -p $PostgresPort -U postgres --if-exists $Database
    if ($LASTEXITCODE -ne 0) { throw "Could not remove the disposable snapshot-staging database '$Database'." }
    $stagingEvidence = Join-Path $paths.RestoreValidation $Database
    if (Test-Path -LiteralPath $stagingEvidence -PathType Container) { Remove-Item -LiteralPath $stagingEvidence -Recurse -Force }
}

Export-ModuleMember -Function @(
    'Invoke-AeroLinkMaintenanceCommand',
    'Remove-AeroLinkSnapshotStagingDatabase',
    'Test-AeroLinkUpgradedCloneReadiness',
    'Get-AeroLinkUpgradeAnalysis',
    'Write-AeroLinkUpgradeConflictReport',
    'Invoke-AeroLinkCloneValidatedUpgrade',
    'Remove-AeroLinkUpgradeValidationDatabase'
)
