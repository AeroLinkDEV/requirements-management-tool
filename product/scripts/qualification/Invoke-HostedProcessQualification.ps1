#Requires -Version 5.1
<# Real Task Scheduler/S4U acceptance on a disposable GitHub-hosted Windows VM.
   Never run this fixture on an operator installation. Records bind this VM's principal and context;
   they are retained as acceptance evidence, never copied to another installation as qualifications. #>
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Recovery','Reconcile','FirstDeployment')][string]$Definition)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This fixture requires an ephemeral GitHub-hosted Windows runner; operator machines are refused.'
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The disposable runner must support S4U task registration.' }
foreach ($name in @('AeroLinkRemoteDemoRecovery','AeroLinkProductionSourceReconcile','AeroLink Daily Backup')) {
    if (@(Get-ScheduledTask -ErrorAction Stop | Where-Object TaskName -eq $name).Count) { throw "Existing controller ${name}: this is not an empty qualification machine." }
}
foreach ($port in @(5080,54329)) {
    if (@(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq $port).Count) { throw "Port $port already has a listener on this runner." }
}
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$scripts = Join-Path $repository 'product\scripts'
$sha = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or (& git -C $repository status --porcelain)) { throw 'Candidate checkout must be clean.' }
$stamp = [guid]::NewGuid().ToString('N').Substring(0,8)
$world = Join-Path ([IO.Path]::GetPathRoot($env:RUNNER_TEMP)) "aq-$stamp"
$evidence = Join-Path $env:RUNNER_TEMP 'process-qualification-evidence'
$installation = Join-Path $world 'inst'
New-Item -ItemType Directory -Path $world,$evidence,$installation -ErrorAction Stop | Out-Null
$result = [ordered]@{ candidate = $sha; definition = $Definition; machine = $env:COMPUTERNAME; user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    startedAt = (Get-Date).ToUniversalTime().ToString('o'); world = $world; verdict = 'Incomplete'; stages = @(); cleanupErrors = @() }
$ownedTasks = [System.Collections.Generic.List[string]]::new()
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$code = 1
Start-Transcript -Path (Join-Path $evidence 'transcript.log') | Out-Null
function Invoke-FixtureGit([string[]]$Arguments) {
    $prior = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $output = & git -c core.safecrlf=false @Arguments 2>&1; $gitCode = $LASTEXITCODE }
    finally { $ErrorActionPreference = $prior }
    if ($gitCode -ne 0) { throw "Fixture git failed: $(($output | ForEach-Object { [string]$_ }) -join ' ')" }
}
try {
    Import-Module (Join-Path $scripts 'AeroLinkRemoteDemo.psm1') -Force
    Import-Module (Join-Path $scripts 'AeroLinkTransitionAuthority.psm1') -Force -DisableNameChecking
    Import-Module (Join-Path $scripts 'AeroLinkTransitionKernel.psm1') -Force -DisableNameChecking
    $config = [pscustomobject]@{ AeroLinkRoot = $repository }
    $sourceName = "AeroLinkQualification-$stamp-$Definition"
    if ($Definition -eq 'FirstDeployment') {
        # Same action image, principal and settings as Initialize-AeroLinkHomeProcessControl. The action is
        # never started: only its trigger-less probe twins run until the real initializer is exercised below.
        $action = New-ScheduledTaskAction -Execute $powershell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $scripts 'Invoke-AeroLinkFirstDeployment.ps1') + '" -InstallationRoot "' + $installation + '"')
        $taskPrincipal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 135) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $sourceName -Action $action -Principal $taskPrincipal -Settings $settings | Out-Null
        $ownedTasks.Add($sourceName)
        $definitionXml = Export-ScheduledTask -TaskName $sourceName
        Unregister-ScheduledTask -TaskName $sourceName -Confirm:$false
    } else {
        $definitionXml = if ($Definition -eq 'Recovery') { Get-AeroLinkRemoteDemoTaskXml -Config $config -TaskName $sourceName }
                         else { Get-AeroLinkReconcileTaskXml -Config $config -TaskName $sourceName -LogonType S4U }
    }
    [xml]$document = $definitionXml
    $ns = New-Object Xml.XmlNamespaceManager($document.NameTable)
    $ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    $triggers = $document.SelectSingleNode('//t:Triggers',$ns)
    if ($triggers) { [void]$triggers.ParentNode.RemoveChild($triggers) }
    $limit = $document.SelectSingleNode('//t:Settings/t:ExecutionTimeLimit',$ns)
    if ($limit.InnerText -ne 'PT2H15M' -and $limit.InnerText -ne 'PT135M') { throw "Unexpected production limit $($limit.InnerText)." }
    $originalLimit = $limit.InnerText
    if ($Definition -eq 'FirstDeployment') {
        # The local bare origin makes this candidate the approved main of THIS disposable repository.
        # It changes neither GitHub main nor any operator checkout. No HOME controllers exist on this VM.
        $origin = Join-Path $world 'origin.git'; $dev = Join-Path $world 'dev'; $prod = Join-Path $world 'prod'
        Invoke-FixtureGit @('init','--bare','-b','main',$origin)
        Invoke-FixtureGit @('-C',$repository,'push',$origin,"${sha}:refs/heads/main")
        Invoke-FixtureGit @('clone','--quiet',$origin,$dev)
        $env:AEROLINK_INSTALLATION_ROOT = $installation
        & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dev 'product\scripts\Configure-AeroLinkProductionSource.ps1') -Action Install -SourceRoot $prod -InstallationRoot $installation
        if ($LASTEXITCODE -ne 0) { throw 'Disposable production-source install failed.' }
        & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $prod 'product\scripts\Setup-Postgres.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL setup failed.' }
    }
    # First prove the real powershell-image S4U path cheaply. This is diagnostic evidence only, never the
    # PT135M qualification. Then restore the exact generated definition and exercise its actual hard limit.
    foreach ($stage in @('Preflight','Final')) {
        $limit.InnerText = if ($stage -eq 'Preflight') { 'PT5M' } else { $originalLimit }
        Register-ScheduledTask -TaskName $sourceName -Xml $document.OuterXml -Force | Out-Null
        if (-not $ownedTasks.Contains($sourceName)) { $ownedTasks.Add($sourceName) }
        $exported = Export-ScheduledTask -TaskName $sourceName
        $exported | Set-Content -LiteralPath (Join-Path $evidence "$stage.definition.xml") -Encoding Unicode
        $prefix = "AeroLinkQualification-$stamp-$stage-"
        $experiment = Join-Path $world $stage
        Write-Host "Beginning $Definition $stage at $((Get-Date).ToUniversalTime().ToString('o'))"
        & (Join-Path $scripts 'Invoke-AeroLinkLaunchContextQualification.ps1') -InstallationRoot $installation -TaskName $sourceName `
            -TwinNamePrefix $prefix -ProbeStateRoot $experiment -EndingTimeoutSeconds $(if ($stage -eq 'Final') { 9000 } else { 420 })
        $qualifierExit = $LASTEXITCODE
        $summaries = @(Get-ChildItem -LiteralPath (Join-Path $installation 'bootstrap\transitions\qualification-runs') -Filter "$prefix*.summary.json")
        if ($summaries.Count -ne 1) { throw "$stage produced $($summaries.Count) summaries." }
        $summary = Get-Content -LiteralPath $summaries[0].FullName -Raw | ConvertFrom-Json
        Copy-Item -LiteralPath $summaries[0].FullName -Destination (Join-Path $evidence "$stage.summary.json")
        $result.stages += [ordered]@{ stage=$stage; exitCode=$qualifierExit; summary=$summary }
        if ($qualifierExit -ne 0 -or $summary.verdict -notin @('Qualified','QualifiedPlacementOnly')) { throw "$stage qualification failed: $($summary.verdict) $($summary.detail)" }
        $hashes = @($summary.runDescriptorHashes.PSObject.Properties | ForEach-Object { [string]$_.Value })
        if ($hashes.Count -ne 3 -or @($hashes | Select-Object -Unique).Count -ne 1 -or -not $hashes[0]) { throw "$stage descriptor agreement failed." }
        $path = Get-AeroLinkQualificationPath -InstallationRoot $installation -DescriptorHash $hashes[0]
        if (-not (Test-Path -LiteralPath "$path.sha256") -or (Get-Content -LiteralPath "$path.sha256" -Raw).Trim() -ne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) { throw "$stage qualification integrity failed." }
        Copy-Item -LiteralPath $path,"$path.sha256" -Destination $evidence
        if ($stage -eq 'Final') {
            # Task Scheduler normalizes defaults and duration spelling on registration. Bind to the real
            # exported definition, as the production descriptor does, rather than comparing raw XML spellings.
            $expectedHash = Get-AeroLinkSha256Text (Get-AeroLinkTaskDefinitionCanonical $exported)
            if ($summary.sourceDefinitionHash -ne $expectedHash) { throw 'Final evidence names a different registered definition.' }
        }
    }
    if ($Definition -eq 'FirstDeployment') {
        $beforeTasks = @(Get-ScheduledTask -ErrorAction Stop | ForEach-Object TaskName)
        & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dev 'product\scripts\Initialize-AeroLinkHomeProcessControl.ps1')
        $initializerExit = $LASTEXITCODE
        $requests = @(Get-ChildItem -LiteralPath (Join-Path $installation 'bootstrap\first-deployment') -Filter '*.result.json')
        if ($requests.Count -ne 1) { throw "First deployment produced $($requests.Count) results." }
        $deployment = Get-Content -LiteralPath $requests[0].FullName -Raw | ConvertFrom-Json
        $request = Get-Content -LiteralPath (Join-Path $installation 'bootstrap\first-deployment\request.json') -Raw | ConvertFrom-Json
        $result.deployment = $deployment
        $result.initializerExit = $initializerExit
        if ($initializerExit -ne 0 -or $deployment.decision -ne 'Completed' -or $deployment.exitCode -ne 0 -or $deployment.requestId -ne $request.requestId) { throw 'First-deployment caller and correlated result did not both complete.' }
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health/ready' -TimeoutSec 15
        $identity = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health/identity' -TimeoutSec 15
        if ($identity.sourceIdentity -ne $sha) { throw 'First deployment did not serve the exact candidate.' }
        $result.runtimeIdentity = $identity; $result.readiness = $health
        $left = @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -like 'AeroLinkHomeFirstDeployment_*' -and $beforeTasks -notcontains $_.TaskName })
        if ($left.Count) { throw 'The initializer left a first-deployment task registered.' }
    }
    $result.verdict = 'Complete'; $code = 0
} catch { $result.error = $_.Exception.Message; $result.stack = $_.ScriptStackTrace; Write-Host $_ -ForegroundColor Red }
finally {
    if ($Definition -eq 'FirstDeployment' -and $prod -and (Test-Path -LiteralPath $prod)) {
        try {
            $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq 5080 | Select-Object -ExpandProperty OwningProcess -Unique)
            if ($listeners.Count) {
                Import-Module (Join-Path $scripts 'AeroLinkProcessTermination.psm1') -Force -DisableNameChecking
                $runtime = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health/identity' -TimeoutSec 15
                if ($listeners.Count -ne 1 -or $runtime.sourceIdentity -ne $sha) { throw 'Cleanup cannot attribute the API listener to this candidate.' }
                $apiIdentity = New-AeroLinkProcessIdentity -ProcessId $listeners[0]
                $apiProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($listeners[0])" -ErrorAction Stop
                if ([string]$apiProcess.CommandLine -notlike "*$prod*") { throw 'Cleanup cannot attribute the API command to this disposable source.' }
                $stopped = Stop-AeroLinkVerifiedIdentity -Identity $apiIdentity -WaitSeconds 30
                if ($stopped.state -notin @('Stopped','AlreadyGone')) { throw "API cleanup is $($stopped.state)." }
            }
            $pgControl = Join-Path $installation 'postgresql\pgsql\bin\pg_ctl.exe'
            # Obtain the exact installation layout instead of guessing the downloaded distribution directory.
            Import-Module (Join-Path $scripts 'AeroLinkInstallation.psm1') -Force
            $paths = Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $prod 'product') -InstallationRoot $installation
            $pgControl = Join-Path $paths.PostgresBin 'pg_ctl.exe'
            if (Test-Path -LiteralPath (Join-Path $paths.PostgresData 'postmaster.pid')) {
                & $pgControl -D $paths.PostgresData -m fast -w -t 60 stop
                if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL did not stop cleanly.' }
            }
            if (@(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -in @(5080,54329)).Count) { throw 'A disposable service listener remains.' }
        } catch { $result.cleanupErrors += $_.Exception.Message }
    }
    foreach ($name in $ownedTasks) {
        try {
            $task = @(Get-ScheduledTask -ErrorAction Stop | Where-Object TaskName -eq $name)
            if ($task.Count) { Stop-ScheduledTask -TaskName $name -ErrorAction Stop; Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction Stop }
        } catch { $result.cleanupErrors += $_.Exception.Message }
    }
    try {
        $left = @(Get-ScheduledTask -ErrorAction Stop | Where-Object TaskName -like "AeroLinkQualification-$stamp-*")
        if ($left.Count) { $result.cleanupErrors += "Owned tasks remain: $($left.TaskName -join ', ')" }
        # Retain transition receipts, probe identities and all diagnostics; exclude PostgreSQL binaries/data.
        if (Test-Path (Join-Path $installation 'bootstrap')) { Copy-Item -LiteralPath (Join-Path $installation 'bootstrap') -Destination (Join-Path $evidence 'installation-bootstrap') -Recurse }
        if (Test-Path (Join-Path $installation 'logs')) { Copy-Item -LiteralPath (Join-Path $installation 'logs') -Destination (Join-Path $evidence 'installation-logs') -Recurse }
        foreach ($stage in @('Preflight','Final')) { if (Test-Path (Join-Path $world $stage)) { Copy-Item -LiteralPath (Join-Path $world $stage) -Destination $evidence -Recurse } }
    } catch { $result.cleanupErrors += $_.Exception.Message }
    if ($result.cleanupErrors.Count) { $result.verdict = 'Incomplete'; $code = 1 }
    $result.finishedAt = (Get-Date).ToUniversalTime().ToString('o')
    $result | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
exit $code
