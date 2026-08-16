#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Base,
    [string]$Head = 'HEAD',
    [string[]]$Paths,
    [switch]$SinceOriginMain,
    [ValidateSet('Fast', 'Full')]
    [string]$Mode = 'Fast',
    [switch]$Explain,
    [switch]$Json,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# This is the Windows-friendly entry point for #568. It asks the shared Node planner for one JSON decision,
# prints the safety preamble before any optional command, and only executes known repository commands. It never
# fetches, rebases, resets, connects to the persistent PostgreSQL instance, or writes under product/.local.

if ($SinceOriginMain -and $Base) {
    throw '-SinceOriginMain cannot be combined with -Base.'
}
if ($Paths -and ($Base -or $SinceOriginMain -or $PSBoundParameters.ContainsKey('Head'))) {
    throw '-Paths cannot be combined with -Base, -Head or -SinceOriginMain.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$planner = Join-Path $repositoryRoot 'product\test-planner\tools\plan.mjs'
$node = Get-Command node.exe -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js is required to run the shared AeroLink test planner.' }
if (-not (Test-Path -LiteralPath $planner -PathType Leaf)) { throw "Shared planner not found: $planner" }

function Invoke-GitText {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & git -C $repositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed: $($output -join ' ')" }
    return ($output -join "`n").Trim()
}

$nodeArguments = @($planner, '--json', '--dry-run')
if ($Paths) {
    $nodeArguments += '--files'
    $nodeArguments += '--'
    $nodeArguments += $Paths
}
else {
    if ($SinceOriginMain) {
        $nodeArguments += '--since-origin-main'
    }
    else {
        $nodeArguments += '--base'
        $nodeArguments += $(if ($Base) { $Base } else { 'origin/main' })
    }
    if ($Head) {
        $nodeArguments += '--head'
        $nodeArguments += $Head
    }
}

Push-Location $repositoryRoot
try {
    # The planner intentionally writes actionable failures to stderr. Capture all of that output before
    # restoring Stop semantics so a missing local origin/main ref is reported clearly instead of PowerShell
    # terminating on the first native stderr record.
    $plannerErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $jsonOutput = & $node.Source @nodeArguments 2>&1
        $plannerExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $plannerErrorPreference
    }
    if ($plannerExitCode -ne 0) { throw "The shared planner failed: $($jsonOutput -join ' ')" }
    $plan = ($jsonOutput -join "`n") | ConvertFrom-Json
}
finally {
    Pop-Location
}

$baseRef = $plan.compact.source.base
$staleWarning = $null
if ($baseRef -eq 'origin/main') {
    $originMainSha = Invoke-GitText -Arguments @('rev-parse', 'origin/main')
    $staleWarning = "origin/main is a local remote-tracking ref at $originMainSha and may be stale. No fetch or rebase was performed; refresh it deliberately before relying on this plan."
}

$wrapperSafety = [ordered]@{
    persistentPostgreSqlTouched = $false
    persistentEvidenceRootTouched = $false
    fetchOrRebasePerformed = $false
    networkAccessPerformed = $false
    # JSON is a reporting mode and returns before optional execution, so it is intrinsically plan-only.
    dryRun = [bool]($DryRun -or $Json)
    remainingFullEvidence = 'GitHub Actions full gate remains authoritative; local Fast or Full output never satisfies merge evidence.'
}
$resourcePosture = [ordered]@{
    postgresql = if ($plan.classification.postgresql) { 'Full mode requires an isolated disposable Docker service; persistent PostgreSQL is never touched' } else { 'not selected' }
    sqlite = 'Full mode uses the repository broader disposable SQLite/browser subset; Fast mode does not claim SQLite evidence'
    browser = if ($plan.classification.browser) { 'selected browser smoke lane may start browser processes when execution is allowed' } else { 'not selected' }
    filesystemEvidence = 'persistent product/.local evidence roots are untouched; any disposable lane owns its temporary paths'
}

$executionSteps = [System.Collections.Generic.List[object]]::new()
$executionStatus = 'not-run'
$executionError = $null
$executionClock = $null
$persistentEvidenceRootTouched = $false
$evidenceFingerprintBefore = $null

function Get-StringArray {
    param($Values)
    if ($null -eq $Values) { return @() }
    return @($Values | ForEach-Object { [string]$_ })
}

function Get-PersistentEvidenceFingerprint {
    param([Parameter(Mandatory)][string]$Root)
    if (-not (Test-Path -LiteralPath $Root -ErrorAction Stop)) { return @('<absent>') }
    if (-not (Test-Path -LiteralPath $Root -PathType Container -ErrorAction Stop)) { throw 'Persistent evidence root was not a directory.' }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $rootItem = Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
    $items = @(Get-ChildItem -LiteralPath $Root -Force -Recurse -ErrorAction Stop | Sort-Object FullName)
    if ($items.Count -gt 10000) { throw 'Persistent evidence fingerprint exceeded its entry bound.' }
    $fingerprint = [System.Collections.Generic.List[string]]::new()
    [void]$fingerprint.Add("<root>|D|$($rootItem.CreationTimeUtc.Ticks)|$($rootItem.LastWriteTimeUtc.Ticks)|$([int64]$rootItem.Attributes)")
    $totalBytes = [int64]0
    foreach ($item in $items) {
        $relative = $item.FullName.Substring($rootFull.Length).TrimStart('\', '/')
        if ($item.PSIsContainer) {
            [void]$fingerprint.Add("$relative|D|$($item.CreationTimeUtc.Ticks)|$($item.LastWriteTimeUtc.Ticks)|$([int64]$item.Attributes)")
        }
        else {
            $totalBytes += [int64]$item.Length
            if ($totalBytes -gt 268435456) { throw 'Persistent evidence fingerprint exceeded its byte bound.' }
            $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256 -ErrorAction Stop).Hash
            [void]$fingerprint.Add("$relative|F|$($item.Length)|$($item.CreationTimeUtc.Ticks)|$($item.LastWriteTimeUtc.Ticks)|$([int64]$item.Attributes)|$hash")
        }
    }
    return @($fingerprint.ToArray())
}

function Get-CiJobsForStep {
    param([Parameter(Mandatory)][string]$Label)
    switch ($Label) {
        'Build the solution' { return @('backend-api', 'backend-core') }
        'Domain suite' { return @('backend-core') }
        'Infrastructure suite' { return @('backend-core') }
        'Client lint, type-check and build' { return @('client') }
        'Browser smoke journeys' { return @('browser-pr', 'browser-production', 'browser-full') }
        'Operator and recovery script contracts' { return @('script-contracts') }
        'PostgreSQL migration and secure bootstrap' { return @('postgresql-smoke') }
        default { return @() }
    }
}

function Get-SelectedCiJobs {
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Candidates)
    $selected = Get-StringArray $plan.compact.ci.selected
    return @($Candidates | Where-Object { $_ -in $selected })
}

function New-ExecutionResult {
    $selected = Get-StringArray $plan.compact.ci.selected
    $executed = @($executionSteps.ToArray() | ForEach-Object { Get-StringArray $_.ciJobs }) | Where-Object { $_ }
    $executed = @($executed | Select-Object -Unique)
    $ciOnly = @($selected | Where-Object { $_ -notin $executed })
    $timedSteps = @($executionSteps.ToArray() | ForEach-Object {
        [ordered]@{
            label = $_.label
            status = $_.status
            elapsedMs = [int64]$_.elapsedMs
            ciJobs = @(Get-StringArray $_.ciJobs)
        }
    })
    $totalMs = if ($executionClock) { [int64]$executionClock.ElapsedMilliseconds } else { [int64]0 }
    return [ordered]@{
        schemaVersion = 1
        mode = $Mode
        status = $executionStatus
        authoritative = $false
        selectedCiJobs = $selected
        executedCiJobs = $executed
        ciOnlyJobs = $ciOnly
        resources = [ordered]@{
            persistentPostgreSqlTouched = $false
            persistentEvidenceRootTouched = $persistentEvidenceRootTouched
            disposableDockerPostgreSql = if ($selected -contains 'postgresql-smoke') { 'required for Full; unique container, Docker-assigned loopback port and labeled volume' } else { 'not selected' }
            networkAccessPossible = [bool]($selected -contains 'postgresql-smoke')
        }
        timing = [ordered]@{ totalMs = $totalMs; steps = $timedSteps }
        error = $executionError
    }
}

function Write-CompactResult {
    $plan.compact | Add-Member -Force -NotePropertyName execution -NotePropertyValue (New-ExecutionResult)
    Write-Host ("AEROLINK_TEST_PLAN_RESULT=" + ($plan.compact | ConvertTo-Json -Compress -Depth 15)) -ForegroundColor DarkCyan
}

if ($Json) {
    $plan | Add-Member -Force -NotePropertyName wrapper -NotePropertyValue ([ordered]@{ mode = $Mode; explain = [bool]$Explain; safety = $wrapperSafety; resources = $resourcePosture; staleWarning = $staleWarning; execution = (New-ExecutionResult) })
    $plan | ConvertTo-Json -Depth 30
    exit 0
}

Write-Host "AeroLink changed validation plan ($Mode)" -ForegroundColor Cyan
Write-Host "Planner: $($plan.compact.planner.version) / $($plan.compact.planner.hash)"
$mergeBaseLabel = if ($plan.mergeBase) { $plan.mergeBase } else { '(explicit paths)' }
Write-Host "Changed paths: $($plan.changedPaths.Count); merge base: $mergeBaseLabel"
if ($staleWarning) { Write-Warning $staleWarning }
Write-Host 'Safety: persistent PostgreSQL and product/.local evidence roots are untouched; the planner performs no fetch or rebase.' -ForegroundColor Green
if ($DryRun) {
    Write-Host 'Dry-run: no build, test, database, evidence, package restore, or network operation was started.' -ForegroundColor Green
}
else {
    Write-Host 'Execution note: a fresh-checkout build may restore configured packages; no Git fetch or rebase is performed.' -ForegroundColor Yellow
}
Write-Host 'Full merge evidence remains with GitHub Actions.' -ForegroundColor Yellow
Write-Host 'Resource posture:' -ForegroundColor Cyan
Write-Host "  PostgreSQL: $($resourcePosture.postgresql)"
Write-Host "  SQLite: $($resourcePosture.sqlite)"
Write-Host "  Browser: $($resourcePosture.browser)"
Write-Host "  Evidence filesystem: $($resourcePosture.filesystemEvidence)"

if ($Explain) {
    Write-Host ''
    Write-Host 'Changed paths and selected areas:' -ForegroundColor Cyan
    foreach ($row in $plan.explain) {
        $areas = if ($row.areas.Count -gt 0) { $row.areas -join ', ' } elseif (-not $row.product) { 'documentation/non-product' } else { 'unclassified fallback' }
        Write-Host "  $($row.path) -> $areas"
    }
}

Write-Host ''
Write-Host 'Classification:' -ForegroundColor Cyan
foreach ($area in @('docsOnly', 'backend', 'client', 'browser', 'postgresql')) {
    Write-Host ("  {0,-12} {1}" -f $area, $plan.classification.$area)
}
if ($plan.classification.reason) { Write-Host "  Reason: $($plan.classification.reason)" }

Write-Host ''
Write-Host 'Local plan:' -ForegroundColor Cyan
foreach ($step in $plan.local) {
    Write-Host "  - $($step.label)"
    if ($step.command) { Write-Host "      $($step.command)" }
    Write-Host "      $($step.why)"
}
Write-Host ''

function Invoke-CheckedProcess {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath exited with code $LASTEXITCODE." }
}

function Invoke-TimedAction {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$CiJobs,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $stepStatus = 'passed'
    try {
        & $Action
    }
    catch {
        $stepStatus = 'failed'
        throw
    }
    finally {
        $watch.Stop()
        [void]$executionSteps.Add([ordered]@{
            label = $Label
            status = $stepStatus
            elapsedMs = [int64]$watch.ElapsedMilliseconds
            ciJobs = @($CiJobs)
        })
    }
}

function Invoke-CheckedPowerShellScript {
    param([Parameter(Mandatory)][string]$ScriptPath)
    $shell = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if (-not $shell) { $shell = Get-Command pwsh.exe -ErrorAction SilentlyContinue }
    if (-not $shell) { throw 'PowerShell is required for the script-contract family.' }
    Invoke-CheckedProcess $shell.Source @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath)
}

function Invoke-ScriptContractSuite {
    $previewScript = Join-Path $repositoryRoot 'product/scripts/Configure-AeroLinkBackupSchedule.ps1'
    $previewShell = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if (-not $previewShell) { $previewShell = Get-Command pwsh.exe -ErrorAction SilentlyContinue }
    if (-not $previewShell) { throw 'PowerShell is required for the script-contract family.' }
    $preview = & $previewShell.Source -NoProfile -ExecutionPolicy Bypass -File $previewScript -Action Preview -DailyAt 02:00 -RetentionDays 30 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Backup schedule preview contract failed.' }
    $previewText = $preview -join "`n"
    foreach ($expected in @('Schedule\s*:\s*Daily', 'DailyAt\s*:\s*02:00', 'RetentionDays\s*:\s*30', 'Invoke-AeroLinkScheduledBackup\.ps1', '-RetentionDays 30')) {
        if ($previewText -notmatch $expected) { throw "Backup schedule preview did not contain '$expected'." }
    }
    foreach ($scriptName in @(
        'AeroLinkEvidenceStore.Tests.ps1',
        'AeroLinkBackupVerification.Tests.ps1',
        'AeroLinkRestoreContract.Tests.ps1',
        'AeroLinkMigrationPosture.Tests.ps1',
        'AeroLinkRemoteDemo.Tests.ps1',
        'AeroLinkRemoteDemoRecovery.Tests.ps1',
        'Get-AeroLinkTestPlan.Tests.ps1'
    )) {
        Invoke-CheckedPowerShellScript (Join-Path $repositoryRoot "product/scripts/$scriptName")
    }
}

function Get-SafeFailureMessage {
    param([AllowNull()][string]$Message)
    if ([string]::IsNullOrWhiteSpace($Message)) { return 'Local validation failed.' }
    if ($Message -match '(?i)(password|secret|token|authorization|connectionstrings|connection string|postgresql://|--env|env-file|Host=127\.0\.0\.1|User Id=|Password=)') { return 'Local validation failed; sensitive details were redacted.' }
    if ($Message.Length -gt 512) { return $Message.Substring(0, 512) + '...' }
    return $Message
}
function ConvertTo-WindowsArgument {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $result = New-Object System.Text.StringBuilder
    [void]$result.Append('"'); $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') { [void]$result.Append((('\' * ($slashes * 2 + 1)) -join '')); [void]$result.Append('"'); $slashes = 0; continue }
        if ($slashes -gt 0) { [void]$result.Append((('\' * $slashes) -join '')); $slashes = 0 }
        [void]$result.Append($character)
    }
    if ($slashes -gt 0) { [void]$result.Append((('\' * ($slashes * 2)) -join '')) }
    [void]$result.Append('"'); return $result.ToString()
}
function Invoke-CheckedDocker {
    param([Parameter(Mandatory)][string]$Docker, [Parameter(Mandatory)][string]$Operation, [Parameter(Mandatory)][string[]]$Arguments)
    try { & $Docker @Arguments *> $null; if ($LASTEXITCODE -ne 0) { throw 'native command failed' } }
    catch { throw "Disposable Docker operation '$Operation' failed." }
}
function Invoke-DockerText {
    param([Parameter(Mandatory)][string]$Docker, [Parameter(Mandatory)][string]$Operation, [Parameter(Mandatory)][string[]]$Arguments)
    try { $output = @(& $Docker @Arguments 2>&1); if ($LASTEXITCODE -ne 0) { throw 'native command failed' }; return ($output -join [Environment]::NewLine).Trim() }
    catch { throw "Disposable Docker operation '$Operation' failed." }
}
function Get-DockerOwnedResource {
    param([Parameter(Mandatory)][string]$Docker, [Parameter(Mandatory)][ValidateSet('container', 'volume')][string]$Kind, [Parameter(Mandatory)][string]$Name)
    if ([string]::IsNullOrEmpty($Name) -or $Name -match '[\r\n]') { throw "Disposable Docker $Kind ownership could not be verified." }
    $arguments = if ($Kind -eq 'container') { @('inspect', '--format', '{{ index .Config.Labels "com.aerolink.planner.run" }}', $Name) } else { @('volume', 'inspect', '--format', '{{ index .Labels "com.aerolink.planner.run" }}', $Name) }
    try {
        $output = @(& $Docker @arguments 2>&1)
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0) { return (($output -join [Environment]::NewLine).Trim()) }
        if ($output.Count -lt 1 -or $output.Count -gt 2) { throw 'inspect returned an unexpected diagnostic count' }
        $lines = @($output | ForEach-Object { [string]$_ })
        if (@($lines | Where-Object { $_ -match '[\r\n]' }).Count -ne 0) { throw 'inspect returned a multiline diagnostic' }
        $diagnostics = @($lines | Where-Object { $_ -ne '[]' })
        if ($diagnostics.Count -ne 1 -or ($lines.Count - $diagnostics.Count) -gt 1) { throw 'inspect returned an unexpected companion record' }
        $diagnostic = $diagnostics[0]
        $escapedName = [regex]::Escape($Name)
        $absent = if ($Kind -eq 'container') {
            $diagnostic -cmatch "\AError: No such object: $escapedName\z"
        }
        else {
            $diagnostic -cmatch "\AError response from daemon: (?:get ${escapedName}: no such volume|no such volume: $escapedName)\z"
        }
        if ($absent) { return $null }
        throw 'inspect was not conclusive'
    } catch { throw "Disposable Docker $Kind ownership could not be verified." }
}
function Remove-DockerOwnedResource {
    param([Parameter(Mandatory)][string]$Docker, [Parameter(Mandatory)][ValidateSet('container', 'volume')][string]$Kind, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$RunId, [Parameter(Mandatory)]$CleanupErrors)
    try {
        $owner = Get-DockerOwnedResource -Docker $Docker -Kind $Kind -Name $Name
        if ($null -eq $owner) { return }
        if ($owner -ne $RunId) { throw 'ownership label did not match this run' }
        $args = if ($Kind -eq 'container') { @('rm', '--force', $Name) } else { @('volume', 'rm', '--force', $Name) }
        Invoke-CheckedDocker -Docker $Docker -Operation "remove-$Kind" -Arguments $args
        if ($null -ne (Get-DockerOwnedResource -Docker $Docker -Kind $Kind -Name $Name)) { throw 'resource remained after removal' }
    } catch { [void]$CleanupErrors.Add("Disposable Docker $Kind cleanup was not proven.") }
}
function Test-IsExpectedEmptyListenerDiagnostic {
    param(
        [AllowEmptyString()][string]$ExceptionType,
        [AllowEmptyString()][string]$FullyQualifiedErrorId,
        [AllowEmptyString()][string]$Category,
        [AllowEmptyString()][string]$Reason,
        [AllowEmptyString()][string]$TargetName,
        [AllowEmptyString()][string]$TargetType,
        [AllowEmptyString()][string]$Message,
        [Parameter(Mandatory)][int]$Port
    )
    if ($ExceptionType -cne 'Microsoft.PowerShell.Cmdletization.Cim.CimJobException' -or
        $FullyQualifiedErrorId -cne 'CmdletizationQuery_NotFound,Get-NetTCPConnection' -or
        $Category -cne 'ObjectNotFound' -or
        $Reason -cne 'CimJobException' -or
        $TargetName -cne 'MSFT_NetTCPConnection' -or
        $TargetType -cne 'String' -or
        $Message -match '[\r\n]') { return $false }
    $escapedPort = [regex]::Escape([string]$Port)
    $messagePattern = "\ANo matching MSFT_NetTCPConnection objects found by CIM query for instances of the ROOT/StandardCimv2/MSFT_NetTCPConnection class on the[ \t]+CIM server: SELECT \* FROM[ \t]+MSFT_NetTCPConnection[ \t]+WHERE \(\(LocalPort = $escapedPort\)\) AND \(\(State = 2\)\)\. Verify query parameters and retry\.\z"
    return $Message -cmatch $messagePattern
}
function Get-BoundedListenerConnections {
    param([Parameter(Mandatory)][int]$Port)
    try {
        $connections = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop)
    }
    catch {
        $expectedEmpty = Test-IsExpectedEmptyListenerDiagnostic `
            -ExceptionType $_.Exception.GetType().FullName `
            -FullyQualifiedErrorId $_.FullyQualifiedErrorId `
            -Category ([string]$_.CategoryInfo.Category) `
            -Reason $_.CategoryInfo.Reason `
            -TargetName $_.CategoryInfo.TargetName `
            -TargetType $_.CategoryInfo.TargetType `
            -Message $_.Exception.Message `
            -Port $Port
        if (-not $expectedEmpty) { throw 'The bounded listener query failed.' }
        return @()
    }
    if ($connections.Count -gt 128) { throw 'The bounded listener query exceeded its result limit.' }
    return @($connections)
}
function Get-RestrictedSecretFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$Lines)
    foreach ($line in $Lines) { if ($line -match '[\r\n]') { throw 'Disposable secret values contained a line break.' } }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, (($Lines -join [Environment]::NewLine) + [Environment]::NewLine), $utf8)
    $acl = Get-Acl -LiteralPath $Path; $acl.SetAccessRuleProtection($true, $false)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'Allow')
    $acl.SetAccessRule($rule); Set-Acl -LiteralPath $Path -AclObject $acl
}
function Remove-ExactTemporaryFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$CleanupErrors)
    try {
        if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force -ErrorAction Stop }
        if (Test-Path -LiteralPath $Path) { throw 'temporary file remained' }
    } catch { [void]$CleanupErrors.Add('A disposable temporary file could not be removed.') }
}
function Read-BoundedTextFile {
    param([Parameter(Mandatory)][string]$Path)
    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
        if ((Get-Item -LiteralPath $Path -ErrorAction Stop).Length -gt 131072) { return '' }
        return [IO.File]::ReadAllText($Path)
    } catch { return '' }
}
function Invoke-SafeApiRequest {
    param([Parameter(Mandatory)][string]$Label, [Parameter(Mandatory)][string]$Uri, [ValidateSet('Get', 'Post')][string]$Method = 'Get', [AllowNull()]$Body, [AllowNull()]$Headers, [AllowNull()]$WebSession)
    $parameters = @{ Uri = $Uri; Method = $Method; ErrorAction = 'Stop'; TimeoutSec = 5 }
    if ($null -ne $Body) { $parameters.Body = $Body; $parameters.ContentType = 'application/json' }
    if ($null -ne $Headers) { $parameters.Headers = $Headers }
    if ($null -ne $WebSession) { $parameters.WebSession = $WebSession }
    try { return Invoke-RestMethod @parameters } catch { throw "Disposable API request '$Label' failed." }
}
function Get-DisposableDockerCommand {
    $dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
    if (-not $dockerCommand) { $dockerCommand = Get-Command docker -ErrorAction SilentlyContinue }
    if (-not $dockerCommand) { throw 'Docker is unavailable; the PostgreSQL gate is not-proven and Full mode cannot report success.' }
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
}
function Invoke-DisposablePostgreSqlGate {
    $docker = Get-DisposableDockerCommand
    $runId = ([Guid]::NewGuid().ToString('N'))
    $containerName = "aerolink-planner-pg-$runId"; $volumeName = "aerolink-planner-pg-$runId"
    $database = "aerolink_ci_$runId"; $databaseUser = 'aerolink'; $databasePassword = "ci-$runId"; $apiSecret = "planner-bootstrap-$runId"
    $labelKey = 'com.aerolink.planner.run'
    if ($containerName -notmatch '^aerolink-planner-pg-[0-9a-f]{32}$' -or $volumeName -notmatch '^aerolink-planner-pg-[0-9a-f]{32}$') { throw 'Disposable resource name validation failed.' }
    $containerIntent = $true; $volumeIntent = $true; $secretFileIntent = $true
    $apiOwnershipIntent = $false; $apiProcessStarted = $false; $helper = $null; $apiPid = $null; $apiStart = $null; $apiPort = $null
    $cleanupErrors = [System.Collections.Generic.List[string]]::new()
    $tempRoot = [IO.Path]::GetTempPath()
    $dockerEnvFile = Join-Path $tempRoot "aerolink-planner-$runId-docker.env"; $apiEnvFile = Join-Path $tempRoot "aerolink-planner-$runId-api.env"
    $apiStatus = Join-Path $tempRoot "aerolink-planner-$runId-api.status"; $apiOutput = Join-Path $tempRoot "aerolink-planner-$runId-api.out.log"; $apiError = Join-Path $tempRoot "aerolink-planner-$runId-api.err.log"
    try {
        if ($null -ne (Get-DockerOwnedResource -Docker $docker -Kind container -Name $containerName)) { throw 'Refusing to use a pre-existing disposable container name.' }
        if ($null -ne (Get-DockerOwnedResource -Docker $docker -Kind volume -Name $volumeName)) { throw 'Refusing to use a pre-existing disposable volume name.' }
        Get-RestrictedSecretFile -Path $dockerEnvFile -Lines @("POSTGRES_DB=$database", "POSTGRES_USER=$databaseUser", "POSTGRES_PASSWORD=$databasePassword")
        Invoke-CheckedDocker -Docker $docker -Operation 'create-volume' -Arguments @('volume', 'create', '--label', "$labelKey=$runId", $volumeName)
        if ((Get-DockerOwnedResource -Docker $docker -Kind volume -Name $volumeName) -ne $runId) { throw 'Disposable volume ownership was not verified.' }
        Invoke-CheckedDocker -Docker $docker -Operation 'start-container' -Arguments @('run', '--detach', '--name', $containerName, '--label', "$labelKey=$runId", '--env-file', $dockerEnvFile, '--publish', '127.0.0.1::5432', '--volume', ($volumeName + ':/var/lib/postgresql/data'), 'postgres:17')
        if ((Get-DockerOwnedResource -Docker $docker -Kind container -Name $containerName) -ne $runId) { throw 'Disposable container ownership was not verified.' }
        $mappingJson = Invoke-DockerText -Docker $docker -Operation 'inspect-port-mapping' -Arguments @('inspect', '--format', '{{json (index .NetworkSettings.Ports "5432/tcp")}}', $containerName)
        $mapping = @($mappingJson | ConvertFrom-Json)
        if ($mapping.Count -ne 1 -or $mapping[0].HostIp -ne '127.0.0.1' -or $mapping[0].HostPort -notmatch '^[1-9][0-9]{0,4}$') { throw 'Disposable PostgreSQL loopback mapping was not verified.' }
        $hostPostgreSqlPort = [int]$mapping[0].HostPort
        if ($hostPostgreSqlPort -lt 1024 -or $hostPostgreSqlPort -gt 65535) { throw 'Disposable PostgreSQL mapped port was outside the bounded range.' }
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) { & $docker exec $containerName pg_isready -U $databaseUser -d $database *> $null; if ($LASTEXITCODE -eq 0) { $ready = $true; break }; Start-Sleep -Seconds 1 }
        if (-not $ready) { throw 'The disposable PostgreSQL container did not become ready within 60 seconds.' }
        $apiDll = Join-Path $repositoryRoot 'product/src/AeroLink.Api/bin/Release/net10.0/AeroLink.Api.dll'
        if (-not (Test-Path -LiteralPath $apiDll -PathType Leaf)) { throw 'The disposable PostgreSQL API build is missing.' }
        Get-RestrictedSecretFile -Path $apiEnvFile -Lines @(
            'ASPNETCORE_ENVIRONMENT=Production', 'ASPNETCORE_URLS=http://127.0.0.1:0', 'Database__Provider=PostgreSql',
            "ConnectionStrings__AeroLink=Host=127.0.0.1;Port=$hostPostgreSqlPort;Database=$database;Username=$databaseUser;Password=$databasePassword",
            'DemoData__Enabled=false', 'Identity__SeedDemoAccounts=false', 'Identity__AllowDemoAccounts=false', 'Identity__CookieSecure=false', "Identity__BootstrapSecret=$apiSecret"
        )
        $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if (-not $dotnetCommand) { $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue }
        if (-not $dotnetCommand) { throw 'dotnet is required for the disposable API process boundary.' }
        $ownedProject = Join-Path $repositoryRoot 'product/test-planner/tools/OwnedProcess/OwnedProcess.csproj'; $ownedDll = Join-Path $repositoryRoot 'product/test-planner/tools/OwnedProcess/bin/Release/net10.0/OwnedProcess.dll'
        if (-not (Test-Path -LiteralPath $ownedDll -PathType Leaf)) { Invoke-CheckedProcess $dotnetCommand.Source @('build', $ownedProject, '--configuration', 'Release') }
        if (-not (Test-Path -LiteralPath $ownedDll -PathType Leaf)) { throw 'The owned process helper build was not produced.' }
        $helperArguments = @($ownedDll, '--executable', $dotnetCommand.Source, '--arg', $apiDll, '--arg', '--urls', '--arg', 'http://127.0.0.1:0', '--status-file', $apiStatus, '--stdout-file', $apiOutput, '--stderr-file', $apiError, '--env-file', $apiEnvFile)
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo; $startInfo.FileName = $dotnetCommand.Source; $startInfo.Arguments = (($helperArguments | ForEach-Object { ConvertTo-WindowsArgument ([string]$_) }) -join ' ')
        $startInfo.WorkingDirectory = $repositoryRoot; $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true; $startInfo.RedirectStandardInput = $true; $startInfo.RedirectStandardOutput = $true; $startInfo.RedirectStandardError = $true
        $helper = New-Object System.Diagnostics.Process; $helper.StartInfo = $startInfo; $apiOwnershipIntent = $true
        if (-not $helper.Start()) { throw 'The owned disposable API process helper could not start.' }
        $apiProcessStarted = $true; $null = $helper.StandardOutput.ReadToEndAsync(); $null = $helper.StandardError.ReadToEndAsync()
        $started = $false
        for ($attempt = 0; $attempt -lt 120; $attempt++) {
            $statusText = Read-BoundedTextFile -Path $apiStatus
            if ($statusText -match '(?m)^STARTED\|pid=(?<pid>[0-9]+)\|start=(?<start>[0-9]+)\|job=assigned$') { $apiPid = [int]$Matches['pid']; $apiStart = [Int64]$Matches['start']; $started = $true; break }
            if ($statusText -match '(?m)^ERROR\|') { throw 'The owned disposable API process failed before job assignment.' }
            if ($helper.HasExited) { throw 'The owned disposable API process helper exited before job assignment.' }
            Start-Sleep -Milliseconds 250
        }
        if (-not $started) { throw 'The owned disposable API process did not report bounded job ownership.' }
        $listenerOwned = $false
        for ($attempt = 0; $attempt -lt 120; $attempt++) {
            $apiText = Read-BoundedTextFile -Path $apiOutput
            if ($apiText -match 'Now listening on:\s*http://127\.0\.0\.1:(?<port>[0-9]{1,5})') {
                $apiPort = [int]$Matches['port']
                if ($apiPort -lt 1024 -or $apiPort -gt 65535) { throw 'The disposable API listener port was outside the bounded range.' }
                try {
                    $connections = @(Get-BoundedListenerConnections -Port $apiPort | Where-Object { $_.LocalAddress -eq '127.0.0.1' })
                    $target = Get-Process -Id $apiPid -ErrorAction Stop; $cimTarget = @(Get-CimInstance Win32_Process -Filter "ProcessId=$apiPid" -ErrorAction Stop)
                    if ($connections.Count -eq 1 -and [int]$connections[0].OwningProcess -eq $apiPid -and $cimTarget.Count -eq 1 -and ([Int64]$target.StartTime.ToFileTimeUtc() -eq $apiStart)) { $listenerOwned = $true; break }
                } catch { }
            }
            if ($helper.HasExited) { throw 'The owned disposable API process exited before listener ownership was proven.' }
            Start-Sleep -Milliseconds 250
        }
        if (-not $listenerOwned) { throw 'The disposable API listener could not be proven to belong to the exact job-owned API process.' }
        $baseUri = "http://127.0.0.1:$apiPort"; $health = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try { $healthResponse = Invoke-SafeApiRequest -Label 'health' -Uri "$baseUri/health"; if ($healthResponse.status -eq 'healthy') { $health = $true; break } } catch { }
            Start-Sleep -Seconds 1
        }
        if (-not $health) { throw 'Disposable PostgreSQL API did not become healthy.' }
        $setup = Invoke-SafeApiRequest -Label 'setup-status-before-bootstrap' -Uri "$baseUri/api/setup/status"
        if (-not $setup.bootstrapRequired -or -not $setup.bootstrapEnabled) { throw 'Disposable PostgreSQL setup did not report bootstrapRequired/bootstrapEnabled.' }
        $bootstrapBody = @{ displayName = 'CI Administrator'; email = 'ci-admin@example.invalid'; password = "CiOnly!$runId" } | ConvertTo-Json -Compress
        Invoke-SafeApiRequest -Label 'bootstrap' -Uri "$baseUri/api/setup/bootstrap" -Method Post -Headers @{ 'X-AeroLink-Bootstrap-Secret' = $apiSecret } -Body $bootstrapBody | Out-Null
        $setupAfter = Invoke-SafeApiRequest -Label 'setup-status-after-bootstrap' -Uri "$baseUri/api/setup/status"
        if ($setupAfter.bootstrapRequired -or $setupAfter.bootstrapEnabled) { throw 'Disposable PostgreSQL bootstrap did not close the setup window.' }
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $loginBody = @{ userName = 'admin'; password = "CiOnly!$runId" } | ConvertTo-Json -Compress
        $login = Invoke-SafeApiRequest -Label 'login' -Uri "$baseUri/api/auth/login" -Method Post -Body $loginBody -WebSession $session
        if ($login.userName -ne 'admin' -or -not $login.isAdministrator) { throw 'Disposable PostgreSQL administrator login did not succeed.' }
        $me = Invoke-SafeApiRequest -Label 'authenticated-identity' -Uri "$baseUri/api/auth/me" -WebSession $session
        if ($me.userName -ne 'admin' -or -not $me.isAdministrator) { throw 'Disposable PostgreSQL authenticated identity did not persist.' }
        $providerBody = @{ key = 'ci-entra'; displayName = 'CI Entra'; protocol = 'OpenIdConnect'; issuer = 'https://login.ci.example/tenant/'; subjectClaim = 'sub'; groupClaim = 'groups' } | ConvertTo-Json -Compress
        $provider = Invoke-SafeApiRequest -Label 'external-identity-provider-create' -Uri "$baseUri/api/admin/external-identity/providers" -Method Post -Body $providerBody -WebSession $session
        if (-not $provider.enabled -or $provider.issuer -ne 'https://login.ci.example/tenant') { throw 'Disposable PostgreSQL external-identity provider was not normalized and enabled.' }
        $providers = Invoke-SafeApiRequest -Label 'external-identity-provider-list' -Uri "$baseUri/api/admin/external-identity/providers" -WebSession $session
        if (@($providers).Count -ne 1) { throw 'Disposable PostgreSQL provider listing did not contain exactly one provider.' }
        $duplicateStatus = $null
        try {
            $duplicateBody = @{ key = 'ci-entra-two'; displayName = 'CI Duplicate'; protocol = 'OpenIdConnect'; issuer = 'HTTPS://LOGIN.CI.EXAMPLE:443/tenant'; subjectClaim = 'sub'; groupClaim = 'groups' } | ConvertTo-Json -Compress
            Invoke-SafeApiRequest -Label 'external-identity-duplicate' -Uri "$baseUri/api/admin/external-identity/providers" -Method Post -Body $duplicateBody -WebSession $session | Out-Null
        } catch { try { $duplicateStatus = [int]$_.Exception.Response.StatusCode } catch { $duplicateStatus = $null } }
        if ($duplicateStatus -ne 409) { throw 'Disposable PostgreSQL duplicate trust anchor did not return the expected conflict.' }
        $workspaceCode = "CIP$($runId.Substring(0, 8))"
        $workspaceBody = @{ programName = 'CI Program'; programCode = $workspaceCode; projectName = 'CI Project'; softwareProduct = 'CI Product'; initialRelease = '1.0'; initialReleaseIsReleased = $false } | ConvertTo-Json -Compress
        $workspace = Invoke-SafeApiRequest -Label 'workspace-create' -Uri "$baseUri/api/workspaces" -Method Post -Body $workspaceBody -WebSession $session
        $programId = [string]$workspace.program.id
        if ([string]::IsNullOrWhiteSpace($programId)) { throw 'Disposable PostgreSQL workspace did not return a program identifier.' }
        $mappingBody = @{ providerId = $provider.id; externalGroup = 'CI-Approvers'; programId = $programId; role = 'Approver' } | ConvertTo-Json -Compress
        Invoke-SafeApiRequest -Label 'external-identity-mapping' -Uri "$baseUri/api/admin/external-identity/mappings" -Method Post -Body $mappingBody -WebSession $session | Out-Null
        $resolveBody = @{ providerId = $provider.id; issuer = 'https://login.ci.example/tenant'; externalGroups = @('CI-APPROVERS'); programId = $programId } | ConvertTo-Json -Compress
        $resolved = Invoke-SafeApiRequest -Label 'external-identity-resolve' -Uri "$baseUri/api/admin/external-identity/resolve" -Method Post -Body $resolveBody -WebSession $session
        if (@($resolved.roles) -notcontains 'Approver') { throw 'Disposable PostgreSQL external-identity mapping did not resolve the expected role.' }
        $attackerBody = @{ providerId = $provider.id; issuer = 'https://login.ci.example.attacker.test/tenant'; externalGroups = @('CI-APPROVERS'); programId = $programId } | ConvertTo-Json -Compress
        $attackerResolved = Invoke-SafeApiRequest -Label 'external-identity-attacker-resolve' -Uri "$baseUri/api/admin/external-identity/resolve" -Method Post -Body $attackerBody -WebSession $session
        if (@($attackerResolved.roles).Count -ne 0) { throw 'Disposable PostgreSQL look-alike issuer unexpectedly resolved a role.' }
        Write-Host '  Disposable PostgreSQL gate passed after exact process/listener ownership proof.' -ForegroundColor Green
    }
    finally {
        if ($apiOwnershipIntent -and $null -ne $helper) {
            try {
                if ($apiProcessStarted) {
                    if (-not $helper.HasExited) { $helper.StandardInput.WriteLine('stop'); $helper.StandardInput.Flush() }
                    $helperExited = $helper.WaitForExit(10000)
                    if (-not $helperExited) { try { $helper.Kill() } catch { }; [void]$cleanupErrors.Add('The owned API process helper did not exit within the bounded cleanup wait.') }
                    if ($helper.HasExited -and $helper.ExitCode -ne 0) { [void]$cleanupErrors.Add('The owned API process helper exited nonzero.') }
                    $statusAfter = Read-BoundedTextFile -Path $apiStatus
                    if ($statusAfter -notmatch '(?m)^(STOPPED|EXITED)\|.*\|jobCount=0$' -or $statusAfter -notmatch '(?m)^CLEANUP\|handles=closed$') { [void]$cleanupErrors.Add('Owned API job cleanup was not proven.') }
                    if ($null -ne $apiPid) { try { if ($null -ne (Get-Process -Id $apiPid -ErrorAction SilentlyContinue)) { [void]$cleanupErrors.Add('The owned API process remained after cleanup.') } } catch { [void]$cleanupErrors.Add('The owned API process exit could not be verified.') } }
                    if ($null -ne $apiPort) { try { if (@(Get-BoundedListenerConnections -Port $apiPort | Where-Object { [int]$_.OwningProcess -eq $apiPid }).Count -gt 0) { [void]$cleanupErrors.Add('The owned API listener remained after cleanup.') } } catch { [void]$cleanupErrors.Add('The owned API listener cleanup could not be verified.') } }
                }
                else {
                    try { if (-not $helper.HasExited) { $helper.Kill(); [void]$cleanupErrors.Add('The API helper start outcome was uncertain.') } } catch { [void]$cleanupErrors.Add('The API helper start outcome was uncertain.') }
                }
            } catch { [void]$cleanupErrors.Add('Owned API process cleanup was not proven.') }
        }
        if ($containerIntent) { Remove-DockerOwnedResource -Docker $docker -Kind container -Name $containerName -RunId $runId -CleanupErrors $cleanupErrors }
        if ($volumeIntent) { Remove-DockerOwnedResource -Docker $docker -Kind volume -Name $volumeName -RunId $runId -CleanupErrors $cleanupErrors }
        if ($secretFileIntent) { foreach ($path in @($dockerEnvFile, $apiEnvFile, $apiStatus, $apiOutput, $apiError)) { Remove-ExactTemporaryFile -Path $path -CleanupErrors $cleanupErrors } }
        if ($cleanupErrors.Count -gt 0) { throw 'Disposable PostgreSQL cleanup was not proven; Full mode is non-authoritative.' }
    }
}

function Invoke-FastStep {
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

function Invoke-ParallelFastPair {
    param(
        [Parameter(Mandatory)]$InfrastructureStep,
        [Parameter(Mandatory)]$BrowserStep
    )
    $selectedCiJobs = Get-StringArray $plan.compact.ci.selected
    $definitions = @(
        [pscustomobject]@{ step = $InfrastructureStep; label = 'Infrastructure suite' },
        [pscustomobject]@{ step = $BrowserStep; label = 'Browser smoke journeys' }
    )
    $jobs = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($definition in $definitions) {
            $job = Start-Job -ArgumentList $definition.label, $repositoryRoot -ScriptBlock {
                param($Label, $Root)
                Set-Location -LiteralPath $Root
                $watch = [Diagnostics.Stopwatch]::StartNew()
                $output = @()
                $exitCode = 1
                try {
                    switch ($Label) {
                        'Infrastructure suite' {
                            $output = @(& dotnet test product/tests/AeroLink.Infrastructure.Tests --configuration Release --no-build 2>&1)
                            $exitCode = $LASTEXITCODE
                        }
                        'Browser smoke journeys' {
                            $output = @(& npm.cmd --prefix product/client run test:smoke 2>&1)
                            $exitCode = $LASTEXITCODE
                        }
                        default { throw "Unsupported parallel Fast step: $Label" }
                    }
                }
                catch {
                    $output = @($output) + @($_.Exception.Message)
                    $exitCode = 1
                }
                finally { $watch.Stop() }
                [pscustomobject]@{
                    label = $Label
                    exitCode = [int]$exitCode
                    elapsedMs = [int64]$watch.ElapsedMilliseconds
                    output = @($output | ForEach-Object { [string]$_ })
                }
            }
            [void]$jobs.Add([pscustomobject]@{ definition = $definition; job = $job })
        }
        Wait-Job -Job @($jobs | ForEach-Object job) | Out-Null
        $failed = [System.Collections.Generic.List[string]]::new()
        foreach ($entry in $jobs) {
            $result = @(Receive-Job -Job $entry.job -ErrorAction Stop)
            if ($result.Count -ne 1) { throw "Parallel Fast step '$($entry.definition.label)' returned no bounded result." }
            $result = $result[0]
            foreach ($line in @($result.output)) { if (-not [string]::IsNullOrWhiteSpace([string]$line)) { Write-Host ([string]$line) } }
            $status = if ([int]$result.exitCode -eq 0) { 'passed' } else { 'failed' }
            $ciJobs = @(Get-CiJobsForStep $entry.definition.label | Where-Object { $selectedCiJobs -contains $_ })
            [void]$executionSteps.Add([ordered]@{
                label = $entry.definition.label
                status = $status
                elapsedMs = [int64]$result.elapsedMs
                ciJobs = @($ciJobs)
            })
            if ($status -ne 'passed') { [void]$failed.Add($entry.definition.label) }
        }
        if ($failed.Count -gt 0) { throw "Parallel Fast checks failed: $($failed -join ', ')." }
    }
    finally {
        foreach ($entry in $jobs) {
            try { if ($entry.job) { Remove-Job -Job $entry.job -Force -ErrorAction SilentlyContinue } } catch { }
        }
    }
}

function Invoke-FullPlan {
    $classification = $plan.classification
    $selectedCiJobs = Get-StringArray $plan.compact.ci.selected
    if ($selectedCiJobs -contains 'postgresql-smoke') { $null = Get-DisposableDockerCommand }
    if ($classification.backend) {
        Invoke-TimedAction -Label 'Build the solution' -CiJobs @('backend-api', 'backend-core') -Action { Invoke-CheckedProcess 'dotnet' @('build', 'product/AeroLink.slnx', '--configuration', 'Release') }
        Invoke-TimedAction -Label 'API suite' -CiJobs @('backend-api') -Action { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Api.Tests', '--configuration', 'Release', '--no-build') }
        Invoke-TimedAction -Label 'Domain suite' -CiJobs @('backend-core') -Action { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Domain.Tests', '--configuration', 'Release', '--no-build') }
        Invoke-TimedAction -Label 'Infrastructure suite' -CiJobs @('backend-core') -Action { Invoke-CheckedProcess 'dotnet' @('test', 'product/tests/AeroLink.Infrastructure.Tests', '--configuration', 'Release', '--no-build') }
    }
    elseif ($classification.postgresql) {
        # The CI PostgreSQL lane restores/builds the API even when a migration-only path did not match the
        # backend area pattern. Keep Full's disposable lane able to run the same startup proof.
        Invoke-TimedAction -Label 'Build the solution for PostgreSQL gate' -CiJobs @('postgresql-smoke') -Action { Invoke-CheckedProcess 'dotnet' @('build', 'product/AeroLink.slnx', '--configuration', 'Release') }
    }
    if ($classification.client) {
        Invoke-TimedAction -Label 'Client lint, type-check' -CiJobs @('client') -Action { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'lint') }
        Invoke-TimedAction -Label 'Client build' -CiJobs @('client') -Action { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'build') }
    }
    $browserSmokeJobs = @(Get-SelectedCiJobs @('browser-pr', 'browser-production', 'browser-full'))
    if ($classification.browser -and $browserSmokeJobs.Count -gt 0) {
        # Both Playwright configs use unique temp SQLite files and loopback ports; neither targets product/.local.
        Invoke-TimedAction -Label 'Browser smoke journeys' -CiJobs $browserSmokeJobs -Action { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:smoke') }
        $browserProductionJobs = @(Get-SelectedCiJobs @('browser-production', 'browser-full'))
        if ($browserProductionJobs.Count -gt 0) { Invoke-TimedAction -Label 'Browser production journeys' -CiJobs $browserProductionJobs -Action { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:production') } }
    }
    if ($selectedCiJobs -contains 'script-contracts') {
        Invoke-TimedAction -Label 'Operator and recovery script contracts' -CiJobs @('script-contracts') -Action { Invoke-ScriptContractSuite }
    }
    if ($selectedCiJobs -contains 'postgresql-smoke') {
        Invoke-TimedAction -Label 'PostgreSQL migration and secure bootstrap' -CiJobs @('postgresql-smoke') -Action { Invoke-DisposablePostgreSqlGate }
    }
}

if ($DryRun) {
    Write-Host ''
    Write-Host 'Dry run requested: no build, test, database, evidence, fetch, rebase, or network operation was started.' -ForegroundColor Green
    Write-CompactResult
    exit 0
}

$confirmation = if ($Mode -eq 'Full') { Read-Host 'Full mode will run selected local gates, script contracts, and an isolated disposable Docker PostgreSQL gate when selected; persistent PostgreSQL/evidence remain untouched. Continue? [y/N]' } else { 'y' }
if ($Mode -eq 'Full' -and $confirmation -notmatch '^(?i:y|yes)$') {
    Write-Host 'Full validation cancelled before any command ran.' -ForegroundColor Yellow
    $executionStatus = 'cancelled'
    Write-CompactResult
    exit 2
}

$executionClock = [Diagnostics.Stopwatch]::StartNew()
$evidenceRoot = Join-Path $repositoryRoot 'product\.local'
try {
    $evidenceFingerprintBefore = Get-PersistentEvidenceFingerprint -Root $evidenceRoot
}
catch {
    $persistentEvidenceRootTouched = $true
    throw 'Persistent evidence root could not be fingerprinted; local execution was refused.'
}
Push-Location $repositoryRoot
try {
    try {
        if ($Mode -eq 'Fast') {
    $fastSteps = @($plan.local | Where-Object { $_.label -ne 'Nothing' })
    $infrastructureStep = @($fastSteps | Where-Object { $_.label -eq 'Infrastructure suite' -and -not $_.fullOnly }) | Select-Object -First 1
    $browserStep = @($fastSteps | Where-Object { $_.label -eq 'Browser smoke journeys' -and -not $_.fullOnly }) | Select-Object -First 1
    $parallelPairAvailable = ($null -ne $infrastructureStep -and $null -ne $browserStep)
    $parallelPairCompleted = $false
    foreach ($step in $fastSteps) {
        if ($parallelPairAvailable -and $step.label -in @('Infrastructure suite', 'Browser smoke journeys')) {
            if (-not $parallelPairCompleted) {
                Write-Host '  Running independent Infrastructure and browser smoke Fast checks concurrently.' -ForegroundColor Cyan
                Invoke-ParallelFastPair -InfrastructureStep $infrastructureStep -BrowserStep $browserStep
                $parallelPairCompleted = $true
            }
            continue
        }
        if (-not $step.fullOnly) {
            $ciJobs = @(Get-CiJobsForStep $step.label | Where-Object { (Get-StringArray $plan.compact.ci.selected) -contains $_ })
            Invoke-TimedAction -Label $step.label -CiJobs $ciJobs -Action { Invoke-FastStep -Step $step }
        }
        else { Invoke-FastStep -Step $step }
    }
}
        else {
            Invoke-FullPlan
        }
        $executionStatus = 'passed'
    }
    catch {
        $executionStatus = 'failed'
        $executionError = Get-SafeFailureMessage -Message $_.Exception.Message
        throw
    }
}
finally {
    Pop-Location
    if ($executionClock) { $executionClock.Stop() }
    try {
        $evidenceFingerprintAfter = Get-PersistentEvidenceFingerprint -Root $evidenceRoot
        if ($null -eq $evidenceFingerprintBefore -or $null -ne (Compare-Object -ReferenceObject $evidenceFingerprintBefore -DifferenceObject $evidenceFingerprintAfter)) {
            $persistentEvidenceRootTouched = $true
            $executionStatus = 'failed'
            $executionError = 'Persistent evidence root changed or could not be proven unchanged.'
        }
    }
    catch {
        $persistentEvidenceRootTouched = $true
        $executionStatus = 'failed'
        $executionError = 'Persistent evidence root changed or could not be proven unchanged.'
    }
    $wrapperSafety.persistentEvidenceRootTouched = $persistentEvidenceRootTouched
    Write-CompactResult
    if ($persistentEvidenceRootTouched) { throw 'Persistent evidence root changed or could not be proven unchanged.' }
}

Write-Host ''
Write-Host 'Local validation completed. GitHub Actions full evidence is still required for merge.' -ForegroundColor Green
