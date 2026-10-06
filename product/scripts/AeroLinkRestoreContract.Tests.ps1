$ErrorActionPreference = 'Stop'
$restore = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -Raw
$download = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-AeroLinkRestoredDownloads.ps1') -Raw
$environmentTests = Join-Path $PSScriptRoot 'AeroLinkProcessEnvironment.Tests.ps1'
$environmentTestsSource = Get-Content -LiteralPath $environmentTests -Raw
$program = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\AeroLink.Api\Program.cs') -Raw
foreach ($path in @('Backup-AeroLink.ps1','Restore-AeroLink.ps1','Test-AeroLinkRestoredDownloads.ps1','AeroLinkRestoreQualification.Tests.ps1','AeroLinkProcessEnvironment.psm1','AeroLinkProcessEnvironment.Tests.ps1')) {
    $errors=$null;$tokens=$null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $path),[ref]$tokens,[ref]$errors)|Out-Null
    if($errors.Count -gt 0){throw "$path has a PowerShell parse error: $($errors[0].Message)"}
}
# AeroLinkRestoreQualification.Tests.ps1 now executes activation and rollback, but always as a disposable
# qualification. These source checks stay because they also guard the production-only branches that run skips (#1128).
foreach ($required in @('aerolink_restore_stage_','Rename-Database ''aerolink'' $oldDatabase','AfterEvidenceActivation','Test-RestoredApi ''aerolink''','$activationPassed = $true')) {
    if(-not $restore.Contains($required)){throw "Restore activation contract is missing: $required"}
}
foreach ($rollbackRequired in @('$originalDatabaseRenamed = $true','AfterOriginalDatabaseRename','if ($databaseActivated) { Rename-Database ''aerolink'' $failedDatabase }','Rename-Database $oldDatabase ''aerolink''','SELECT COUNT(*) FROM programs;')) {
    if(-not $restore.Contains($rollbackRequired)){throw "Restore rollback/query contract is missing: $rollbackRequired"}
}
if($restore.Contains("if (-not `$DisposableQualification) { & (Join-Path `$PSScriptRoot 'Stop-AeroLink.ps1') }")){throw 'Rollback still stops PostgreSQL before its compensating database renames.'}
if(-not $restore.Contains('Stop-AeroLinkApplicationProcesses')){throw 'Rollback does not stop the application processes independently of PostgreSQL.'}
if(-not $restore.Contains('if ($command -notlike "*$productRoot*") { continue }')){throw 'Rollback does not preserve unrelated listeners while recovering the database pair.'}
if(-not $restore.Contains("Disposable restore qualification is forbidden on the persistent AeroLink PostgreSQL port 54329.")){throw 'Disposable restore qualification is not fenced from the persistent database.'}
if(-not $download.Contains('X-AeroLink-Restore-Validation') -or -not $program.Contains('restore_validation_read_only') -or -not $program.Contains('typeof(IHostedService)')){throw 'The isolated API-download validation token/read-only boundary is incomplete.'}
if($download.Contains("Start-Process -FilePath 'dotnet'")){throw 'Restore validation still tracks a dotnet-run parent instead of the API listener process.'}
if(-not $download.Contains('$apiExecutable') -or -not $download.Contains('remained in use after process cleanup')){throw 'Restore validation does not launch the built API directly and prove its port is released.'}
if(-not $download.Contains('if (-not (Get-Module -Name AeroLinkProcessEnvironment))') -or $download.Contains("AeroLinkProcessEnvironment.psm1') -Force")){throw 'Restore validation must reuse the loaded process-environment helper rather than force-reloading it in a nested scope.'}
if(-not $download.Contains('Get-AeroLinkProcessEnvironmentSnapshot -Name @($settings.Keys)')){throw 'Restore validation does not snapshot exact process-environment presence before overriding settings.'}
if(-not $download.Contains('Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $previous')){throw 'Restore validation does not restore the exact process-environment snapshot.'}
if($download.Contains('foreach ($entry in $previous.GetEnumerator()) { [Environment]::SetEnvironmentVariable')){throw 'Restore validation still conflates absent and empty process variables.'}
if(-not $environmentTestsSource.Contains('if (-not (Get-Module -Name AeroLinkProcessEnvironment))') -or $environmentTestsSource.Contains("AeroLinkProcessEnvironment.psm1') -Force")){throw 'The in-process process-environment regression must reuse the caller-loaded helper rather than force-reloading it.'}
# The build to validate with is named by the caller and never chosen here. Preferring whichever configuration
# had output on disk let an established installation validate an upgraded clone with a stale Release binary
# from its previous production run; a binary predating the read-only boundary would ignore these settings and
# start the ordinary mutating host, with its outbound workers, over copied production data.
if(-not $download.Contains('[Parameter(Mandatory)][string]$ApiExecutable')){throw 'Restore validation still selects its own API build instead of requiring the caller to name the current one.'}
if($download.Contains('bin\Release') -or $download.Contains('bin\Debug')){throw 'Restore validation must not know about build configurations; the caller names the executable.'}
if(-not $restore.Contains('-ApiExecutable')){throw 'Restore does not name the build it validates with.'}
# Authentication endpoint availability, proved before the real database is mutated. The read-only middleware
# short-circuits every non-health route BEFORE endpoint routing, so an absent /api/auth/login answered 403
# exactly as a present one did - the 403 proves the boundary, not the route. /health/routes reads the built
# EndpointDataSource: it reaches routing, invokes nothing, and fails if this build has lost the auth routes.
if(-not $program.Contains('/health/routes')){throw 'The build does not expose a read-only route-presence proof, so authentication endpoint availability cannot be established inside the validation boundary.'}
# Path AND method: a path that survives with the wrong verb is a route nobody can use, and the proof must
# fail for it. HttpMethodMetadata is what routing itself matches on.
if(-not $program.Contains('HttpMethodMetadata')){throw 'The route-presence proof reads paths only, so an authentication route that changed method would still pass it.'}
foreach($route in @('POST /api/auth/login','GET /api/auth/me','POST /api/auth/logout')){
    if(-not $download.Contains($route)){throw "Isolated validation does not require the authentication route $route to be present with its method."}
    if(-not $program.Contains($route)){throw "The build does not require $route in its own route-presence contract."}
}
if(-not $download.Contains('does not declare the required authentication routes')){throw 'Isolated validation does not fail when the required authentication routes are missing.'}
if($download.IndexOf('finally {', $download.IndexOf('finally {') + 1) -lt 0 -or -not $download.Contains('Production rollback/restart must never inherit')){throw 'Restore validation does not restore its parent environment in a nested cleanup finally.'}
if (-not (Get-Module -Name AeroLinkProcessEnvironment)) {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessEnvironment.psm1')
}
$callerProbeNames = @('ConnectionStrings__AeroLink', 'Evidence__Root', 'ASPNETCORE_ENVIRONMENT')
$callerBefore = Get-AeroLinkProcessEnvironmentSnapshot -Name $callerProbeNames
$emptyProbeBefore = Get-AeroLinkProcessEnvironmentSnapshot -Name @('AEROLINK_981_EMPTY_PROBE')
function Assert-CallerEnvironment {
    param([hashtable]$Expected, [string]$Context)
    foreach ($name in $callerProbeNames) {
        $actual = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ($Expected[$name].Present) {
            if ($actual -ne [string]$Expected[$name].Value) {
                throw "$Context did not restore $name exactly."
            }
        }
        elseif ($null -ne $actual) {
            throw "$Context left $name present when the caller had it absent."
        }
    }
}
try {
    $emptyStateSupported = $false
    [Environment]::SetEnvironmentVariable('AEROLINK_981_EMPTY_PROBE', '', 'Process')
    if ([Environment]::GetEnvironmentVariable('AEROLINK_981_EMPTY_PROBE', 'Process') -eq '') {
        $emptyStateSupported = $true
    }
    [Environment]::SetEnvironmentVariable('AEROLINK_981_EMPTY_PROBE', $null, 'Process')

    [Environment]::SetEnvironmentVariable('ConnectionStrings__AeroLink', 'caller-populated-sentinel', 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $null, 'Process')
    if ($emptyStateSupported) {
        [Environment]::SetEnvironmentVariable('Evidence__Root', '', 'Process')
    }
    else {
        [Environment]::SetEnvironmentVariable('Evidence__Root', 'caller-populated-sentinel', 'Process')
    }
    $callerDuringTest = Get-AeroLinkProcessEnvironmentSnapshot -Name $callerProbeNames

    & $environmentTests
    Assert-CallerEnvironment $callerDuringTest 'In-process environment test'

    $otherEngine = if ($PSVersionTable.PSEdition -eq 'Core') { Get-Command powershell.exe -ErrorAction SilentlyContinue } else { Get-Command pwsh.exe -ErrorAction SilentlyContinue }
    if ($otherEngine) {
        & $otherEngine.Source -NoProfile -ExecutionPolicy Bypass -File $environmentTests
        if ($LASTEXITCODE -ne 0) { throw "AeroLink process-environment tests failed under $($otherEngine.Source)." }
        Assert-CallerEnvironment $callerDuringTest 'Child-process environment test'
    }
}
finally {
    Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $callerBefore
    Restore-AeroLinkProcessEnvironmentSnapshot -Snapshot $emptyProbeBefore
}

# #1498: a hosted run's seed API never became ready, and the only clue was a stderr path on runner temp, which no
# job uploads. These drive the qualification's real readiness wait and diagnostics copy (extracted from the
# script by its AST, not restated) against a stand-in process, so they run without PostgreSQL or a Release build.
$qualificationPath = Join-Path $PSScriptRoot 'AeroLinkRestoreQualification.Tests.ps1'
$qualificationSource = Get-Content -LiteralPath $qualificationPath -Raw
$qualificationTokens = $null; $qualificationErrors = $null
$qualificationAst = [Management.Automation.Language.Parser]::ParseFile($qualificationPath, [ref]$qualificationTokens, [ref]$qualificationErrors)
foreach ($functionName in 'Get-QualificationLogTail','Wait-QualificationApiReady','Save-QualificationDiagnostics') {
    $definition = $qualificationAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true)
    if (-not $definition) { throw "Restore qualification no longer defines $functionName." }
    . ([scriptblock]::Create($definition.Extent.Text))
}
# The readiness budget and the uploaded location are part of the contract, not incidental text.
if (-not $qualificationSource.Contains("Wait-QualificationApiReady -Process `$api -Port `$seedApiPort -Attempts 180 -Name 'Disposable seed API'")) { throw 'The seed API readiness wait no longer uses the qualification wait with its unchanged 180-attempt budget.' }
if (-not $qualificationSource.Contains("Join-Path `$repositoryRoot 'TestResults'") -or -not $qualificationSource.Contains('Save-QualificationDiagnostics -Sources @($root,$backupRoot)')) { throw 'A failed restore qualification no longer copies its owned diagnostics into TestResults.' }
$workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\.github\workflows\ci.yml') -Raw
$domainStart = $workflow.IndexOf("`n  backend-core-domain:")
$domainEnd = $workflow.IndexOf("`n  backend-core-infrastructure:", $domainStart + 1)
if ($domainStart -lt 0 -or $domainEnd -lt 0) { throw 'The Domain job that runs restore qualification can no longer be identified in ci.yml.' }
$domainJob = $workflow.Substring($domainStart, $domainEnd - $domainStart)
if (-not $domainJob.Contains('AeroLinkRestoreQualification.Tests.ps1') -or $domainJob -notmatch 'name: domain-test-diagnostics-[^\r\n]*\r?\n\s+path: \$\{\{ github\.workspace \}\}/TestResults/') {
    throw 'The Domain job that runs restore qualification no longer uploads TestResults/, so the copied diagnostics would be lost again.'
}

$readinessFixture = Join-Path ([IO.Path]::GetTempPath()) ('arq-contract-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$ownedRun = Join-Path $readinessFixture 'arq-run'
New-Item -ItemType Directory -Path $ownedRun | Out-Null
try {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start(); $closedPort = $probe.LocalEndpoint.Port; $probe.Stop()
    $engine = (Get-Command powershell.exe -ErrorAction Stop).Source
    $stdout = Join-Path $ownedRun 'seed-api.stdout.log'; $stderr = Join-Path $ownedRun 'seed-api.stderr.log'
    # An API that dies during startup: the message must carry its exit code and stderr, not just a path.
    $exiting = Start-Process -FilePath $engine -ArgumentList @('-NoProfile','-Command','[Console]::Out.WriteLine(''applying migrations''); [Console]::Error.WriteLine(''Unhandled exception. Failed to bind to address 1498''); exit 7') `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    [void]$exiting.Handle
    $message = $null
    try { Wait-QualificationApiReady -Process $exiting -Port $closedPort -Attempts 180 -Name 'Disposable seed API' -StdoutPath $stdout -StderrPath $stderr -AttemptLogPath (Join-Path $ownedRun 'seed-api.readiness.json') | Out-Null }
    catch { $message = $_.Exception.Message }
    if (-not $message) { throw 'A seed API that exited during startup was reported ready.' }
    foreach ($expected in 'Disposable seed API did not become ready','exited with code 7','Failed to bind to address 1498','applying migrations') {
        if (-not $message.Contains($expected)) { throw "The seed API failure message does not carry '$expected': $message" }
    }
    $attemptLog = Get-Content -LiteralPath (Join-Path $ownedRun 'seed-api.readiness.json') -Raw | ConvertFrom-Json
    if ($attemptLog.state -notlike '*exited with code 7*' -or $attemptLog.attemptBudget -ne 180) { throw 'The seed API readiness record does not name the exit or the budget.' }

    # An API that stays alive but never answers, the hosted failure's shape: the budget is spent, the process is
    # named as still running, and each probe outcome is counted. Two attempts keep this fast.
    $hungOut = Join-Path $ownedRun 'hung.stdout.log'; $hungErr = Join-Path $ownedRun 'hung.stderr.log'
    $hung = Start-Process -FilePath $engine -ArgumentList @('-NoProfile','-Command','[Console]::Out.WriteLine(''still seeding''); Start-Sleep -Seconds 60') `
        -RedirectStandardOutput $hungOut -RedirectStandardError $hungErr -WindowStyle Hidden -PassThru
    [void]$hung.Handle
    try {
        $message = $null
        try { Wait-QualificationApiReady -Process $hung -Port $closedPort -Attempts 2 -Name 'Disposable seed API' -StdoutPath $hungOut -StderrPath $hungErr -AttemptLogPath (Join-Path $ownedRun 'hung.readiness.json') | Out-Null }
        catch { $message = $_.Exception.Message }
        if (-not $message) { throw 'A seed API that never answered was reported ready.' }
        foreach ($expected in 'after 2 of 2 attempts','is still running','Probe outcomes: 2x','no listening address','still seeding') {
            if (-not $message.Contains($expected)) { throw "The hung seed API failure message does not carry '$expected': $message" }
        }
    } finally { if (-not $hung.HasExited) { Stop-Process -Id $hung.Id -Force; $hung.WaitForExit(10000) | Out-Null } }

    # Retention: logs at any depth and top-level summaries reach the uploaded directory; key material and
    # evidence do not.
    New-Item -ItemType Directory -Path (Join-Path $ownedRun 'owned-starttls'),(Join-Path $ownedRun 'source evidence') | Out-Null
    Set-Content -LiteralPath (Join-Path $ownedRun 'postgres.log') -Value 'database system is ready'
    Set-Content -LiteralPath (Join-Path $ownedRun 'owned-starttls\relay.stderr.log') -Value 'relay'
    Set-Content -LiteralPath (Join-Path $ownedRun 'owned-starttls\key.pem') -Value 'PRIVATE KEY'
    Set-Content -LiteralPath (Join-Path $ownedRun 'source evidence\attachment.txt') -Value 'controlled evidence'
    $uploaded = Join-Path $readinessFixture 'TestResults\restore-qualification-contract'
    $copied = Save-QualificationDiagnostics -Sources @($ownedRun, (Join-Path $readinessFixture 'absent-backup-root')) -Destination $uploaded
    $leaf = Join-Path $uploaded 'arq-run'
    foreach ($kept in 'seed-api.stderr.log','seed-api.stdout.log','seed-api.readiness.json','postgres.log','owned-starttls\relay.stderr.log') {
        if (-not (Test-Path -LiteralPath (Join-Path $leaf $kept) -PathType Leaf)) { throw "Failed restore qualification diagnostics did not retain $kept in the uploaded directory." }
    }
    if ((Get-Content -LiteralPath (Join-Path $leaf 'seed-api.stderr.log') -Raw) -notlike '*Failed to bind to address 1498*') { throw 'The retained seed API stderr is not the original log.' }
    foreach ($excluded in 'owned-starttls\key.pem','source evidence\attachment.txt') {
        if (Test-Path -LiteralPath (Join-Path $leaf $excluded)) { throw "Failed restore qualification diagnostics copied $excluded, which is not a diagnostic." }
    }
    if ($copied -lt 5) { throw "Failed restore qualification diagnostics reported $copied copied files." }
}
finally {
    Remove-Item -LiteralPath $readinessFixture -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE=0
