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

function Get-StringArray {
    param($Values)
    if ($null -eq $Values) { return @() }
    return @($Values | ForEach-Object { [string]$_ })
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
            persistentEvidenceRootTouched = $false
            disposableDockerPostgreSql = if ($selected -contains 'postgresql-smoke') { 'required for Full; unique container, loopback port and labeled volume' } else { 'not selected' }
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

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-CheckedDocker {
    param([Parameter(Mandatory)][string]$Docker, [Parameter(Mandatory)][string[]]$Arguments)
    & $Docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker $($Arguments -join ' ') exited with code $LASTEXITCODE." }
}

function Get-DisposableDockerCommand {
    $dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
    if (-not $dockerCommand) { $dockerCommand = Get-Command docker -ErrorAction SilentlyContinue }
    if (-not $dockerCommand) { throw 'Docker is unavailable; the PostgreSQL gate is not-proven and Full mode cannot report success.' }
    & $dockerCommand.Source version --format '{{.Server.Version}}' *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Docker is unavailable; the daemon could not be queried, so the PostgreSQL gate is not-proven.' }
    return $dockerCommand.Source
}

function Invoke-DisposablePostgreSqlGate {
    $docker = Get-DisposableDockerCommand

    $runId = ([Guid]::NewGuid().ToString('N'))
    $containerName = "aerolink-planner-pg-$runId"
    $volumeName = "aerolink-planner-pg-$runId"
    $database = "aerolink_ci_$runId"
    $databaseUser = 'aerolink'
    $databasePassword = "ci-$runId"
    $labelKey = 'com.aerolink.planner.run'
    $hostPostgreSqlPort = Get-FreeLoopbackPort
    $hostApiPort = Get-FreeLoopbackPort
    $containerStarted = $false
    $volumeCreated = $false
    $apiProcess = $null
    $cleanupErrors = [System.Collections.Generic.List[string]]::new()
    $apiOutput = Join-Path ([IO.Path]::GetTempPath()) "aerolink-planner-$runId-api.out.log"
    $apiError = Join-Path ([IO.Path]::GetTempPath()) "aerolink-planner-$runId-api.err.log"
    $oldEnvironment = @{}
    foreach ($name in @('ASPNETCORE_ENVIRONMENT', 'Database__Provider', 'ConnectionStrings__AeroLink', 'DemoData__Enabled', 'Identity__SeedDemoAccounts', 'Identity__AllowDemoAccounts', 'Identity__CookieSecure', 'Identity__BootstrapSecret')) {
        $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    try {
        $existing = & $docker inspect $containerName 2>$null
        if ($LASTEXITCODE -eq 0) { throw "Refusing to use pre-existing Docker container '$containerName'." }
        Invoke-CheckedDocker $docker @('volume', 'create', '--label', "$labelKey=$runId", $volumeName)
        $volumeCreated = $true
        Invoke-CheckedDocker $docker @(
            'run', '--detach', '--name', $containerName,
            '--label', "$labelKey=$runId",
            '--env', "POSTGRES_DB=$database",
            '--env', "POSTGRES_USER=$databaseUser",
            '--env', "POSTGRES_PASSWORD=$databasePassword",
            '--publish', "127.0.0.1:${hostPostgreSqlPort}:5432",
            '--volume', "${volumeName}:/var/lib/postgresql/data",
            'postgres:17'
        )
        $containerStarted = $true
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            & $docker exec $containerName pg_isready -U $databaseUser -d $database *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true; break }
            Start-Sleep -Seconds 1
        }
        if (-not $ready) { throw 'The disposable PostgreSQL container did not become ready within 60 seconds.' }

        # The API process inherits only these disposable connection settings. The host process is stopped in
        # finally before the owned container and volume are removed, including when a bootstrap assertion fails.
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $env:Database__Provider = 'PostgreSql'
        $env:ConnectionStrings__AeroLink = "Host=127.0.0.1;Port=$hostPostgreSqlPort;Database=$database;Username=$databaseUser;Password=$databasePassword"
        $env:DemoData__Enabled = 'false'
        $env:Identity__SeedDemoAccounts = 'false'
        $env:Identity__AllowDemoAccounts = 'false'
        $env:Identity__CookieSecure = 'false'
        $env:Identity__BootstrapSecret = "planner-bootstrap-$runId"
        $apiDll = Join-Path $repositoryRoot 'product/src/AeroLink.Api/bin/Release/net10.0/AeroLink.Api.dll'
        if (-not (Test-Path -LiteralPath $apiDll -PathType Leaf)) { throw "The disposable PostgreSQL API build is missing: $apiDll." }
        # Launch the built DLL directly so the owned Process object is the API process itself, not a
        # dotnet-run parent that could leave a child behind after cleanup.
        $apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll, '--urls', "http://127.0.0.1:$hostApiPort") -WorkingDirectory $repositoryRoot -RedirectStandardOutput $apiOutput -RedirectStandardError $apiError -PassThru

        $health = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($apiProcess.HasExited) { break }
            try {
                $healthResponse = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/health" -Method Get -TimeoutSec 2
                if ($healthResponse.status -eq 'healthy') { $health = $true; break }
            }
            catch { }
            Start-Sleep -Seconds 1
        }
        if (-not $health) { throw "Disposable PostgreSQL API did not become healthy. See $apiError." }

        $setup = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/setup/status" -Method Get
        if (-not $setup.bootstrapRequired -or -not $setup.bootstrapEnabled) { throw 'Disposable PostgreSQL setup did not report bootstrapRequired/bootstrapEnabled.' }
        $bootstrapBody = @{ displayName = 'CI Administrator'; email = 'ci-admin@example.invalid'; password = "CiOnly!$runId" } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/setup/bootstrap" -Method Post -ContentType 'application/json' -Headers @{ 'X-AeroLink-Bootstrap-Secret' = $env:Identity__BootstrapSecret } -Body $bootstrapBody | Out-Null
        $setupAfter = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/setup/status" -Method Get
        if ($setupAfter.bootstrapRequired -or $setupAfter.bootstrapEnabled) { throw 'Disposable PostgreSQL bootstrap did not close the setup window.' }
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $loginBody = @{ userName = 'admin'; password = "CiOnly!$runId" } | ConvertTo-Json -Compress
        $login = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/auth/login" -Method Post -ContentType 'application/json' -Body $loginBody -WebSession $session
        if ($login.userName -ne 'admin' -or -not $login.isAdministrator) { throw 'Disposable PostgreSQL administrator login did not succeed.' }
        $me = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/auth/me" -Method Get -WebSession $session
        if ($me.userName -ne 'admin' -or -not $me.isAdministrator) { throw 'Disposable PostgreSQL authenticated identity did not persist.' }
        $providerBody = @{ key = 'ci-entra'; displayName = 'CI Entra'; protocol = 'OpenIdConnect'; issuer = 'https://login.ci.example/tenant/'; subjectClaim = 'sub'; groupClaim = 'groups' } | ConvertTo-Json -Compress
        $provider = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/admin/external-identity/providers" -Method Post -ContentType 'application/json' -Body $providerBody -WebSession $session
        if (-not $provider.enabled -or $provider.issuer -ne 'https://login.ci.example/tenant') { throw 'Disposable PostgreSQL external-identity provider was not normalized and enabled.' }
        $providers = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/admin/external-identity/providers" -Method Get -WebSession $session
        if (@($providers).Count -ne 1) { throw 'Disposable PostgreSQL provider listing did not contain exactly one provider.' }
        $duplicateStatus = $null
        try {
            $duplicateBody = @{ key = 'ci-entra-two'; displayName = 'CI Duplicate'; protocol = 'OpenIdConnect'; issuer = 'HTTPS://LOGIN.CI.EXAMPLE:443/tenant'; subjectClaim = 'sub'; groupClaim = 'groups' } | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/admin/external-identity/providers" -Method Post -ContentType 'application/json' -Body $duplicateBody -WebSession $session | Out-Null
        }
        catch { $duplicateStatus = [int]$_.Exception.Response.StatusCode }
        if ($duplicateStatus -ne 409) { throw "Disposable PostgreSQL duplicate trust anchor returned HTTP $duplicateStatus instead of 409." }
        $workspaceCode = "CIP$($runId.Substring(0, 8))"
        $workspaceBody = @{ programName = 'CI Program'; programCode = $workspaceCode; projectName = 'CI Project'; softwareProduct = 'CI Product'; initialRelease = '1.0'; initialReleaseIsReleased = $false } | ConvertTo-Json -Compress
        $workspace = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/workspaces" -Method Post -ContentType 'application/json' -Body $workspaceBody -WebSession $session
        $programId = [string]$workspace.program.id
        if ([string]::IsNullOrWhiteSpace($programId)) { throw 'Disposable PostgreSQL workspace did not return a program identifier.' }
        $mappingBody = @{ providerId = $provider.id; externalGroup = 'CI-Approvers'; programId = $programId; role = 'Approver' } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/admin/external-identity/mappings" -Method Post -ContentType 'application/json' -Body $mappingBody -WebSession $session | Out-Null
        $resolveBody = @{ providerId = $provider.id; issuer = 'https://login.ci.example/tenant'; externalGroups = @('CI-APPROVERS'); programId = $programId } | ConvertTo-Json -Compress
        $resolved = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/admin/external-identity/resolve" -Method Post -ContentType 'application/json' -Body $resolveBody -WebSession $session
        if (@($resolved.roles) -notcontains 'Approver') { throw 'Disposable PostgreSQL external-identity mapping did not resolve the expected role.' }
        $attackerBody = @{ providerId = $provider.id; issuer = 'https://login.ci.example.attacker.test/tenant'; externalGroups = @('CI-APPROVERS'); programId = $programId } | ConvertTo-Json -Compress
        $attackerResolved = Invoke-RestMethod -Uri "http://127.0.0.1:$hostApiPort/api/admin/external-identity/resolve" -Method Post -ContentType 'application/json' -Body $attackerBody -WebSession $session
        if (@($attackerResolved.roles).Count -ne 0) { throw 'Disposable PostgreSQL look-alike issuer unexpectedly resolved a role.' }
        Write-Host "  Disposable PostgreSQL gate passed (container=$containerName port=$hostPostgreSqlPort database=$database)." -ForegroundColor Green
    }
    finally {
        if ($apiProcess) {
            try {
                if (-not $apiProcess.HasExited) {
                    Stop-Process -InputObject $apiProcess -Force -ErrorAction Stop
                    [void]$apiProcess.WaitForExit(5000)
                    if (-not $apiProcess.HasExited) { $cleanupErrors.Add('The disposable API process did not exit after the bounded stop wait.') }
                }
            }
            catch { [void]$cleanupErrors.Add("The disposable API process could not be cleaned up: $($_.Exception.Message)") }
        }
        foreach ($name in $oldEnvironment.Keys) {
            try { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
            catch { [void]$cleanupErrors.Add("The process environment '$name' could not be restored: $($_.Exception.Message)") }
        }
        if ($containerStarted) {
            $owner = (& $docker inspect --format '{{ index .Config.Labels "com.aerolink.planner.run" }}' $containerName 2>$null | Out-String).Trim()
            if ($owner -ne $runId) {
                [void]$cleanupErrors.Add("Refused to remove Docker container '$containerName' because its ownership label was not verified.")
            }
            else {
                & $docker rm --force $containerName *> $null
                if ($LASTEXITCODE -ne 0) { [void]$cleanupErrors.Add("Docker container '$containerName' could not be removed.") }
            }
        }
        if ($volumeCreated) {
            $owner = (& $docker volume inspect --format '{{ index .Labels "com.aerolink.planner.run" }}' $volumeName 2>$null | Out-String).Trim()
            if ($owner -ne $runId) {
                [void]$cleanupErrors.Add("Refused to remove Docker volume '$volumeName' because its ownership label was not verified.")
            }
            else {
                & $docker volume rm --force $volumeName *> $null
                if ($LASTEXITCODE -ne 0) { [void]$cleanupErrors.Add("Docker volume '$volumeName' could not be removed.") }
            }
        }
        try { Remove-Item -LiteralPath $apiOutput, $apiError -Force -ErrorAction Stop }
        catch { [void]$cleanupErrors.Add("Temporary PostgreSQL API logs could not be removed: $($_.Exception.Message)") }
        if ($cleanupErrors.Count -gt 0) { throw "Disposable PostgreSQL cleanup failed: $($cleanupErrors -join '; ')" }
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
Push-Location $repositoryRoot
try {
    try {
        if ($Mode -eq 'Fast') {
            foreach ($step in $plan.local) {
                if ($step.label -eq 'Nothing') { continue }
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
        $executionError = $_.Exception.Message
        throw
    }
}
finally {
    Pop-Location
    if ($executionClock) { $executionClock.Stop() }
    Write-CompactResult
}

Write-Host ''
Write-Host 'Local validation completed. GitHub Actions full evidence is still required for merge.' -ForegroundColor Green
