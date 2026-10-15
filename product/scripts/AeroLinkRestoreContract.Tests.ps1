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
if (-not $qualificationSource.Contains("Wait-QualificationApiReady -Process `$process -Port `$port -Attempts 120 -Name ")) { throw 'The notification API readiness wait no longer uses the qualification wait with its unchanged 120-attempt budget.' }
if (-not $qualificationSource.Contains("elseif (`$env:GITHUB_ACTIONS -eq 'true') { Join-Path `$repositoryRoot 'TestResults' }") -or -not $qualificationSource.Contains('Save-QualificationDiagnostics -Sources @($root,$backupRoot)')) { throw 'A failed hosted restore qualification no longer copies its owned diagnostics into TestResults.' }
# The run's failure path: its top-level try must rethrow from its catch, because after the finally's pg_ctl stop
# $LASTEXITCODE is 0 and a swallowed failure would pass; and the diagnostics copy must sit on the failed branch
# of that finally, not on the passing one.
if (@($qualificationErrors).Count -ne 0) { throw "Restore qualification does not parse: $($qualificationErrors[0].Message)" }
$qualificationTry = @($qualificationAst.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] -and $_.Finally -and $_.Finally.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Save-QualificationDiagnostics' }, $true) })
if ($qualificationTry.Count -ne 1) { throw "Restore qualification no longer has exactly one top-level try whose finally copies diagnostics (found $($qualificationTry.Count))." }
$qualificationTry = $qualificationTry[0]
if ($qualificationTry.CatchClauses.Count -ne 1 -or -not $qualificationTry.CatchClauses[0].IsCatchAll) { throw 'Restore qualification no longer records its failure in one catch-all clause of its top-level try.' }
$lastCatchStatement = @($qualificationTry.CatchClauses[0].Body.Statements)[-1]
if (-not ($lastCatchStatement -is [Management.Automation.Language.ThrowStatementAst]) -or $lastCatchStatement.Pipeline) { throw 'The top-level catch of restore qualification no longer ends in a bare rethrow, so a failed qualification could pass.' }
$outcomeBranch = @($qualificationTry.Finally.Statements | Where-Object { $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses.Count -eq 1 -and $_.Clauses[0].Item1.Extent.Text -eq '$qualificationPassed' })
if ($outcomeBranch.Count -ne 1 -or -not $outcomeBranch[0].ElseClause) { throw 'Restore qualification no longer separates its passed and failed cleanup in one if ($qualificationPassed) ... else.' }
$copyOnFailure = $outcomeBranch[0].ElseClause.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Save-QualificationDiagnostics' }, $true)
$copyOnPass = $outcomeBranch[0].Clauses[0].Item2.Find({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Save-QualificationDiagnostics' }, $true)
if (-not $copyOnFailure -or $copyOnPass) { throw 'A failed restore qualification no longer copies its diagnostics on the failure branch of its cleanup.' }
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
        # CPU and wall time separate a slow seeder from a stuck one, since the seeders do not log.
        foreach ($expected in 'after 2 of 2 attempts','is still running with ','s CPU time in ','s wall time since start',"Listener on port ${closedPort}: none.",'Probe outcomes: 2x','no listening address','still seeding') {
            if (-not $message.Contains($expected)) { throw "The hung seed API failure message does not carry '$expected': $message" }
        }
        $hungLog = Get-Content -LiteralPath (Join-Path $ownedRun 'hung.readiness.json') -Raw | ConvertFrom-Json
        if (@($hungLog.history).Count -ne 2 -or @($hungLog.history | Where-Object { $_.cpuMs -isnot [long] -and $_.cpuMs -isnot [int] }).Count -ne 0) { throw 'The hung seed API readiness record does not carry the API CPU time for every attempt.' }
    } finally { if (-not $hung.HasExited) { Stop-Process -Id $hung.Id -Force; $hung.WaitForExit(10000) | Out-Null } }

    # Only "no listener" reads as none: any other listener-query failure is reported as such. The stand-in
    # shadows the cmdlet inside this script only.
    function Get-NetTCPConnection { throw 'CIM provider unavailable' }
    try {
        $message = $null
        try { Wait-QualificationApiReady -Process $exiting -Port $closedPort -Attempts 1 -Name 'Disposable seed API' -StdoutPath $stdout -StderrPath $stderr -AttemptLogPath (Join-Path $ownedRun 'listener.readiness.json') | Out-Null }
        catch { $message = $_.Exception.Message }
        if (-not $message -or -not $message.Contains("Listener on port ${closedPort}: unknown (Get-NetTCPConnection failed: CIM provider unavailable)")) { throw "A failed listener query was not reported as such: $message" }
    } finally { Remove-Item -LiteralPath Function:\Get-NetTCPConnection }

    # #1633: the existing operator owner needs native PowerShell, not a Python installation.
    # Substitute only process launch in this owned runner; return genuine OS processes. All readiness,
    # observation and cleanup logic still comes from the production AST. Full qualification uses Python.
    $relayDefinition = $qualificationAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Start-QualificationTlsRelay' }, $true)
    if (-not $relayDefinition) { throw 'The qualification relay launcher is missing.' }
    $tailDefinition = $qualificationAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-QualificationLogTail' }, $true)
    $relayRunnerRoot = Join-Path $readinessFixture 'relay runner'
    New-Item -ItemType Directory -Path (Join-Path $relayRunnerRoot 'test-support') | Out-Null
    foreach ($pathCase in 'path-first','path-second','path-empty') {
        New-Item -ItemType Directory -Path (Join-Path $relayRunnerRoot $pathCase) | Out-Null
        if ($pathCase -ne 'path-empty') {
            Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\cmd.exe') -Destination (Join-Path $relayRunnerRoot "$pathCase\python.exe")
        }
    }
    $relayRunner = Join-Path $relayRunnerRoot 'run.ps1'
    $runnerBody = @'
param([string]$Directory, [int]$Port, [string]$Resolution)
$ErrorActionPreference = 'Stop'
$smtpPort = $Port
$nativeEngine = (Get-Process -Id $PID).Path
$env:AEROLINK_TEST_PYTHON = $nativeEngine
if ($Resolution -ne 'Absolute') {
    $env:PATH = Join-Path $PSScriptRoot 'path-first'
    if ($Resolution -in @('Multiple','Override')) { $env:PATH += ';' + (Join-Path $PSScriptRoot 'path-second') }
    if ($Resolution -eq 'Missing') { $env:PATH = Join-Path $PSScriptRoot 'path-empty' }
    Remove-Item Env:\AEROLINK_TEST_PYTHON
    if ($Resolution -eq 'Override') { $env:AEROLINK_TEST_PYTHON = Join-Path $PSScriptRoot 'path-second\python.exe' }
    $commandName = if ($env:AEROLINK_TEST_PYTHON) { $env:AEROLINK_TEST_PYTHON } else { 'python.exe' }
    $nativePaths = @(Get-Command $commandName -CommandType Application -ErrorAction SilentlyContinue | ForEach-Object Source)
    @{paths=$nativePaths} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'native-resolution.json') -Encoding UTF8
}
function Start-Process {
    param($FilePath, $ArgumentList, $RedirectStandardOutput, $RedirectStandardError, $WindowStyle, [switch]$PassThru)
    $arguments = @('-NoProfile','-File',('"'+(Join-Path $PSScriptRoot 'child.ps1')+'"'),'-Directory',$ArgumentList[1],'-Port',$ArgumentList[3])
    if ($Resolution -ne 'Absolute') {
        # Launch the selected real cmd.exe copy, not a bypass engine. Its child observes this native
        # parent's exact executable/PID/birth, and the owner separately cleans up the descendant.
        $command = '"' + $nativeEngine + '" ' + ($arguments -join ' ')
        $arguments = @('/d','/s','/c',('"'+$command+'"'))
    }
    $launched = Microsoft.PowerShell.Management\Start-Process -FilePath $FilePath -ArgumentList $arguments `
        -RedirectStandardOutput $RedirectStandardOutput -RedirectStandardError $RedirectStandardError -WindowStyle $WindowStyle -PassThru
    $script:launchedHandle = $launched.SafeHandle
    return $launched
}
'@
    $runnerBody += "`n" + $tailDefinition.Extent.Text + "`n" + $relayDefinition.Extent.Text + "`n" + @'
$ownedRelay = $null
try {
    $ownedRelay = Start-QualificationTlsRelay $Directory
    @{ ready=$true; handleClosed=$script:launchedHandle.IsClosed } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'runner-result.json') -Encoding UTF8
} catch {
    @{ ready=$false; message=$_.Exception.Message; handleClosed=$script:launchedHandle.IsClosed } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'runner-result.json') -Encoding UTF8
} finally {
    if ($ownedRelay -and -not $ownedRelay.HasExited) { $ownedRelay.Kill(); if (-not $ownedRelay.WaitForExit(10000)) { throw 'Ready control cleanup failed.' } }
    if ($ownedRelay) { $ownedRelay.Dispose() }
}
'@
    Set-Content -LiteralPath $relayRunner -Value $runnerBody -Encoding UTF8
    $childSource = @'
param([string]$Directory, [int]$Port)
$self = Get-Process -Id $PID
$mode = Split-Path $Directory -Leaf
$native = $self
if ($mode.StartsWith('resolve-')) { $native = Get-Process -Id (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId }
@{pid=$native.Id;creationFileTime=$native.StartTime.ToUniversalTime().ToFileTimeUtc();executablePath=$native.Path
    descendantPid=$PID;descendantFileTime=$self.StartTime.ToUniversalTime().ToFileTimeUtc()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'child.json') -Encoding UTF8
if ($mode.StartsWith('exit')) { [Console]::Error.WriteLine('controlled early exit 7'); exit 7 }
if ($mode -eq 'ready' -or $mode.StartsWith('resolve-')) { [Console]::Out.WriteLine($Port) }
if ($mode -eq 'wrong') { [Console]::Out.WriteLine('wrong-first-line'); [Console]::Out.WriteLine($Port) }
Start-Sleep -Seconds 60
'@
    Set-Content -LiteralPath (Join-Path $relayRunnerRoot 'child.ps1') -Value $childSource -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $relayRunnerRoot 'test-support\NotificationTlsRelay.py') -Value '# Owned launcher path; the process stimulus is child.ps1.' -Encoding UTF8
    function Invoke-RelayStartupControl([string]$Mode, [bool]$BlockReceipt = $false, [string]$Resolution = 'Absolute') {
        $caseRoot = Join-Path $ownedRun $Mode
        New-Item -ItemType Directory -Path $caseRoot | Out-Null
        $receiptPath = Join-Path $caseRoot 'relay.readiness.jsonl'
        if ($BlockReceipt) { New-Item -ItemType Directory -Path $receiptPath | Out-Null }
        $runner = $null; $childIdentity = $null
        try {
            $runner = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile','-File',('"'+$relayRunner+'"'),'-Directory',('"'+$caseRoot+'"'),'-Port',"$closedPort",'-Resolution',$Resolution) `
                -RedirectStandardOutput (Join-Path $caseRoot 'runner.stdout.log') -RedirectStandardError (Join-Path $caseRoot 'runner.stderr.log') -WindowStyle Hidden -PassThru
            [void]$runner.Handle
            if (-not $runner.WaitForExit(35000)) { throw "Relay $Mode control runner did not finish." }
            if ($runner.ExitCode -ne 0) { throw "Relay $Mode control runner failed: $(Get-QualificationLogTail (Join-Path $caseRoot 'runner.stderr.log'))" }
            $result = Get-Content -LiteralPath (Join-Path $caseRoot 'runner-result.json') -Raw | ConvertFrom-Json
            if ($Resolution -ne 'Absolute') {
                $nativeResolution = Get-Content -LiteralPath (Join-Path $caseRoot 'native-resolution.json') -Raw | ConvertFrom-Json
                $resolutionReceipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
                Write-Host ('Native command resolution ' + $Resolution + ': ' + ($nativeResolution | ConvertTo-Json -Compress))
                if ($Resolution -eq 'Missing') {
                    if (@($nativeResolution.paths).Count -ne 0 -or $result.ready -or $resolutionReceipt.outcome -ne 'StartFailed' -or $null -ne $resolutionReceipt.processId -or $resolutionReceipt.attempts -ne 0 -or (Test-Path -LiteralPath (Join-Path $caseRoot 'child.json'))) { throw 'A missing executable did not refuse before process creation.' }
                    Write-Host 'Native missing-command control passed without creating a child.'
                    return
                }
                $expectedDirectory = if ($Resolution -eq 'Override') { 'path-second' } else { 'path-first' }
                $expectedExecutable = Join-Path $relayRunnerRoot "$expectedDirectory\python.exe"
                $expectedCount = if ($Resolution -eq 'Multiple') { 2 } else { 1 }
                if (@($nativeResolution.paths).Count -ne $expectedCount -or $nativeResolution.paths[0] -ne $expectedExecutable) { throw 'The real native command-resolution fixture did not establish the intended ordered matches.' }
                if (-not $result.ready) { throw "Actual launcher refused $Resolution native resolution before ready: $($result.message); receipt=$($resolutionReceipt | ConvertTo-Json -Depth 7 -Compress)" }
            }
            $childIdentity = Get-Content -LiteralPath (Join-Path $caseRoot 'child.json') -Raw | ConvertFrom-Json
            $survivor = Get-Process -Id $childIdentity.pid -ErrorAction SilentlyContinue
            if ($survivor -and $survivor.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $childIdentity.creationFileTime) { throw "Relay $Mode left its exact owned child alive." }
            if ($BlockReceipt) {
                if ($result.ready -or $result.message -notlike 'Owned STARTTLS relay did not become ready.*' -or -not $result.handleClosed) { throw 'Diagnostic write failure replaced the relay refusal or prevented failed-handle release.' }
                return
            }
            if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
                throw "Relay $Mode is missing attributable startup receipt; child=$($childIdentity | ConvertTo-Json -Compress); stderr=$(Get-QualificationLogTail (Join-Path $caseRoot 'relay.stderr.log')); result=$($result | ConvertTo-Json -Compress)"
            }
            $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
            if ($Resolution -ne 'Absolute' -and ($receipt.pythonPath -isnot [string] -or $receipt.pythonPath -ne $expectedExecutable -or $childIdentity.executablePath -ne $expectedExecutable)) { throw 'The selected scalar path does not identify the actual first native executable.' }
            # PowerShell 7 parses ISO JSON timestamps as DateTime; reparsing that object as a culture
            # string drops subsecond birth identity. Windows PowerShell 5 leaves the JSON value a string.
            $birth = if ($receipt.processStartUtc -is [DateTime]) { $receipt.processStartUtc } else { [DateTime]::Parse($receipt.processStartUtc) }
            if ($receipt.processId -ne $childIdentity.pid -or $birth.ToFileTimeUtc() -ne $childIdentity.creationFileTime) { throw "Relay $Mode receipt does not name the actual child birth identity." }
            if ($receipt.helperPath -ne (Join-Path $relayRunnerRoot 'test-support\NotificationTlsRelay.py') -or -not [IO.Path]::IsPathRooted($receipt.pythonPath)) { throw "Relay $Mode receipt lacks resolved executable/helper paths." }
            if ($receipt.attemptBudget -ne 100 -or $receipt.sleepMilliseconds -ne 100 -or $receipt.expectedFirstLine -ne "$closedPort") { throw "Relay $Mode changed its readiness policy." }
            if ($Mode -eq 'ready' -or $Mode.StartsWith('resolve-')) {
                if (-not $result.ready -or $result.handleClosed -or $receipt.outcome -ne 'Ready' -or $receipt.observedState -ne 'Running' -or $receipt.cleanup.outcome -ne 'NotRequired' -or $receipt.streamObservationPhase -ne 'ReadyNoCleanup') { throw 'The ready child was refused, misreported, or its returned handle was disposed.' }
            } else {
                if ($receipt.streamObservationPhase -ne 'BeforeCleanup' -or $receipt.streamObservationCompletedMilliseconds -gt $receipt.cleanup.startedElapsedMilliseconds) { throw "Relay $Mode lacks a pre-cleanup stream observation; actual failed handle closed=$($result.handleClosed)." }
                if (-not $result.handleClosed -or -not $receipt.cleanup.handleReleased) { throw "Relay $Mode did not release its actual failed child handle." }
                if ($result.ready -or $result.message -notlike 'Owned STARTTLS relay did not become ready.*' -or $receipt.cleanup.outcome -notin @('AlreadyExited','Terminated')) { throw "Relay $Mode lost refusal or confirmed owned cleanup." }
                if ($Mode -eq 'exit') {
                    if ($receipt.outcome -ne 'ExitedBeforeReady' -or $receipt.observedState -ne 'Exited' -or $receipt.exitCode -ne 7 -or $receipt.stderr.tail -notlike '*controlled early exit 7*') { throw 'The early exit outcome/code/stderr was lost.' }
                } elseif ($receipt.outcome -ne 'ReadinessNotObserved' -or $receipt.observedState -ne 'Running' -or $receipt.attempts -ne 100 -or $null -ne $receipt.exitCode -or $receipt.elapsedMilliseconds -lt 9000) { throw "Relay $Mode did not retain its unchanged refusal and pre-cleanup running state." }
            }
            Write-Host "Relay $Mode control passed: $($receipt.outcome), pid $($receipt.processId), cleanup $($receipt.cleanup.outcome)."
            Write-Host ('Relay startup observation: ' + ($receipt | Select-Object * -ExcludeProperty history | ConvertTo-Json -Depth 6 -Compress))
        } finally {
            if ($runner -and -not $runner.HasExited) { $runner.Kill(); $runner.WaitForExit(10000) | Out-Null }
            $marker = Join-Path $caseRoot 'child.json'
            if (Test-Path -LiteralPath $marker) {
                $identity = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
                $child = Get-Process -Id $identity.pid -ErrorAction SilentlyContinue
                if ($child -and $child.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $identity.creationFileTime) { [void]$child.Handle; $child.Kill(); $child.WaitForExit(10000) | Out-Null }
                $descendant = Get-Process -Id $identity.descendantPid -ErrorAction SilentlyContinue
                if ($descendant -and $descendant.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $identity.descendantFileTime) { [void]$descendant.Handle; $descendant.Kill(); $descendant.WaitForExit(10000) | Out-Null; $descendant.Dispose() }
            }
        }
    }
    Invoke-RelayStartupControl 'resolve-multiple' $false 'Multiple'
    Invoke-RelayStartupControl 'resolve-single' $false 'Single'
    Invoke-RelayStartupControl 'resolve-override' $false 'Override'
    Invoke-RelayStartupControl 'resolve-missing' $false 'Missing'
    Invoke-RelayStartupControl 'exit'
    Invoke-RelayStartupControl 'ready'
    Invoke-RelayStartupControl 'silent'
    Invoke-RelayStartupControl 'wrong'
    Invoke-RelayStartupControl 'exit-write-blocked' $true

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
    foreach ($mode in 'resolve-multiple','resolve-single','resolve-override','resolve-missing','exit','ready','silent','wrong') {
        $relativeReceipt = Join-Path $mode 'relay.readiness.jsonl'
        $original = [IO.File]::ReadAllText((Join-Path $ownedRun $relativeReceipt))
        if ([IO.File]::ReadAllText((Join-Path $leaf $relativeReceipt)) -ne $original) { throw "The actual $mode relay receipt was lost or changed by diagnostic retention." }
    }
}
finally {
    Remove-Item -LiteralPath $readinessFixture -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE=0
