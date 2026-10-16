#Requires -Version 5.1
<#
    Contract coverage for the A3 site updater (#1614).

    Authoring gate: these tests protect the updater's phase ordering, exact current target boundary, public
    instance binding joined to the redacted response's owned process and signed tree, and secret-free recovery
    journal. The regressions are package validation after a pointer mutation, old-binary execution after a schema
    change, and a health check that proves only readiness without the promoted source/installation identity or
    same-process evidence. Existing release, backup and runtime-identity suites own their underlying contracts;
    this suite injects only normal operation boundaries and uses disposable folders.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteUpdater.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteServer.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
$fixtures = [System.Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { $script:failures.Add($Message) } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action | Out-Null }
    catch { if ($_.Exception.Message -match $Pattern) { return }; $script:failures.Add("$Message (message: $($_.Exception.Message))"); return }
    $script:failures.Add("$Message (nothing was thrown)")
}
function Get-FreePort { $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();try{return $listener.LocalEndpoint.Port}finally{$listener.Stop()} }

try {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-site-updater-' + [guid]::NewGuid().ToString('N'))
    $fixtures.Add($root)
    $program = Join-Path $root 'program'
    $versions = Join-Path $program 'versions'
    New-Item -ItemType Directory -Path $versions -Force | Out-Null
    $production = Join-Path $root 'instance.json'
    @{ classification = 'SiteProduction'; label = 'SITE PRODUCTION' } | ConvertTo-Json | Set-Content -LiteralPath $production -Encoding UTF8
    $apiPort = Get-FreePort; $postgresPort = Get-FreePort; while ($postgresPort -eq $apiPort) { $postgresPort = Get-FreePort }
    $site = Set-AeroLinkSiteDeclaration -InstallationRoot $root -ApiPort $apiPort -PostgresPort $postgresPort -PublicHostName 'aerolink-updater.example.test'
    $instanceId = [string]((Get-Content -LiteralPath $production -Raw | ConvertFrom-Json).instanceId)
    $layout = Get-AeroLinkServerLayout -InstallationRoot $root

    # Exact version-root/current boundary: a current alias outside versions is refused before mutation.
    $oldRoot = Join-Path $versions '1.0.0'
    New-Item -ItemType Directory -Path (Join-Path $oldRoot 'app') -Force | Out-Null
    @{ sourceSha = ('b' * 40); latestMigration = 'old-migration' } | ConvertTo-Json | Set-Content (Join-Path $oldRoot 'manifest.json')
    '' | Set-Content (Join-Path $oldRoot 'app\AeroLink.Api.exe')
    New-Item -ItemType Junction -Path $layout.Current -Target $oldRoot | Out-Null
    $outside = Join-Path $root 'outside-release'
    New-Item -ItemType Directory -Path $outside -Force | Out-Null
    $outsideCurrent = Join-Path $outside 'current'
    New-Item -ItemType Junction -Path $outsideCurrent -Target $outside | Out-Null
    $outsideLayout = [pscustomobject]@{ Current = $outsideCurrent; Versions = $layout.Versions }
    Assert-Throws { Get-AeroLinkSiteCurrentTarget -Layout $outsideLayout } 'outside|missing release' `
        'A current alias outside the installation versions directory must fail closed.'
    $missingLayout = [pscustomobject]@{ Current = (Join-Path $outside 'missing-current'); Versions = $layout.Versions }
    Assert-Throws { Get-AeroLinkSiteCurrentTarget -Layout $missingLayout } 'missing.*exact prior target' `
        'An installation without a current junction must fail closed because no exact rollback target exists.'

    # Public full-DNS health owns the installation identity and joins the intentionally redacted response to the
    # exact service PID, process birth time, image, current target and signed-tree source identity.
    $publicStarted = '2026-10-15T12:00:00.0000000Z'
    $publicIdentity = [pscustomobject]@{
        version = '1.0.0'; startedAtUtc = $publicStarted
        instance = [pscustomobject]@{ id = $instanceId; classification = 'SiteProduction' }
    }
    $owner = [pscustomobject]@{ Found = $true; Ambiguous = $false; Attributable = $true; ProcessId = 1701; StartedAt = $publicStarted; ExecutablePath = (Join-Path $oldRoot 'app\AeroLink.Api.exe') }
    $service = [pscustomobject]@{ ProcessId = 1701; State = 'Running'; PathName = '"' + $layout.ApiExecutable + '"' }
    $ownerProbe = { param($Port) $owner }.GetNewClosure()
    $serviceProbe = { param($Name) $service }.GetNewClosure()
    $health = Test-AeroLinkSiteUpdateHealth -BaseUri 'https://aerolink-updater.example.test:5443' -ExpectedSourceSha ('b' * 40) `
        -ExpectedVersion '1.0.0' -ExpectedInstanceId $instanceId -ExpectedClassification 'SiteProduction' -Site $site `
        -ExpectedExecutable $owner.ExecutablePath -ExpectedCurrentTarget $oldRoot `
        -ReadyProbe { param($Uri) $Uri -eq 'https://aerolink-updater.example.test:5443' } `
        -IdentityProbe { param($Uri) $publicIdentity } -ProcessOwnerProbe $ownerProbe -ServiceProbe $serviceProbe
    Assert-True ($health.Passed) 'A full-DNS readiness and redacted identity joined to the owned service must pass.'
    $httpHealth = Test-AeroLinkSiteUpdateHealth -BaseUri 'http://aerolink-updater.example.test:5443' -ExpectedSourceSha ('b' * 40) -ExpectedInstanceId $instanceId -ExpectedClassification 'SiteProduction' -ReadyProbe { $true } -IdentityProbe { $null }
    Assert-True (-not $httpHealth.Passed -and $httpHealth.Detail -match 'HTTPS origin') 'A substituted non-HTTPS origin must fail before readiness or identity probes.'
    Assert-Throws { Assert-AeroLinkSiteUpdatePublicHostName -PublicHostName '127.0.0.1' } 'IP address|full-DNS' 'A loopback IP must not qualify as the production HTTPS origin.'
    Assert-Throws { Assert-AeroLinkSiteUpdatePublicHostName -PublicHostName 'localhost' } 'loopback|localhost' 'A localhost name must not qualify as the production HTTPS origin.'
    $sourceMismatch = Test-AeroLinkSiteUpdateHealth -BaseUri 'https://aerolink-updater.example.test:5443' -ExpectedSourceSha ('b' * 40) `
        -ExpectedVersion '1.0.0' -ExpectedInstanceId $instanceId -ExpectedClassification 'SiteProduction' -Site $site `
        -ExpectedExecutable $owner.ExecutablePath -ExpectedCurrentTarget $oldRoot -ReadyProbe { $true } `
        -IdentityProbe { [pscustomobject]@{ sourceSha = ('c' * 40); version = '1.0.0'; startedAtUtc = $publicStarted; instance = [pscustomobject]@{ id = $instanceId; classification = 'SiteProduction' } } } `
        -ProcessOwnerProbe $ownerProbe -ServiceProbe $serviceProbe
    Assert-True (-not $sourceMismatch.Passed) 'An explicitly exposed source mismatch must fail even when readiness is green.'
    $treeMismatch = Test-AeroLinkSitePublicProcessBinding -Identity $publicIdentity -Site $site -ExpectedSourceSha ('c' * 40) `
        -ExpectedExecutable $owner.ExecutablePath -ExpectedCurrentTarget $oldRoot -ProcessOwnerProbe $ownerProbe -ServiceProbe $serviceProbe
    Assert-True (-not $treeMismatch.Passed -and $treeMismatch.Detail -match 'current tree source') 'A signed-tree source mismatch must fail when public source is redacted.'
    $wrongInstance = [pscustomobject]@{ version = '1.0.0'; startedAtUtc = $publicStarted; instance = [pscustomobject]@{ id = ([guid]::NewGuid().ToString()); classification = 'SiteProduction' } }
    $wrongInstanceHealth = Test-AeroLinkSiteUpdateHealth -BaseUri 'https://aerolink-updater.example.test:5443' -ExpectedSourceSha ('b' * 40) `
        -ExpectedVersion '1.0.0' -ExpectedInstanceId $instanceId -ExpectedClassification 'SiteProduction' -Site $site `
        -ExpectedExecutable $owner.ExecutablePath -ExpectedCurrentTarget $oldRoot -ReadyProbe { $true } `
        -IdentityProbe { $wrongInstance } -ProcessOwnerProbe $ownerProbe -ServiceProbe $serviceProbe
    Assert-True (-not $wrongInstanceHealth.Passed -and $wrongInstanceHealth.Detail -match 'declared installation') 'A wrong public instance must fail before process proof.'
    $wrongProcess = [pscustomobject]@{ Found = $true; Ambiguous = $false; Attributable = $true; ProcessId = 1702; StartedAt = '2026-10-15T12:00:01.0000000Z'; ExecutablePath = $owner.ExecutablePath }
    $wrongProcessProbe = { param($Port) $wrongProcess }.GetNewClosure()
    $wrongProcessHealth = Test-AeroLinkSiteUpdateHealth -BaseUri 'https://aerolink-updater.example.test:5443' -ExpectedSourceSha ('b' * 40) `
        -ExpectedVersion '1.0.0' -ExpectedInstanceId $instanceId -ExpectedClassification 'SiteProduction' -Site $site `
        -ExpectedExecutable $owner.ExecutablePath -ExpectedCurrentTarget $oldRoot -ReadyProbe { $true } `
        -IdentityProbe { $publicIdentity } -ProcessOwnerProbe $wrongProcessProbe -ServiceProbe $serviceProbe
    Assert-True (-not $wrongProcessHealth.Passed -and $wrongProcessHealth.Detail -match 'service process|birth time') 'A stale or substituted process must fail the same-process proof.'

    # Full updater ordering: package verification, clone proof, stop, current switch, start, public proof.
    $events = [System.Collections.Generic.List[string]]::new()
    $package = Join-Path $root 'incoming-package.zip'; 'package' | Set-Content $package
    $certificate = Join-Path $root 'trusted.cer'; 'certificate' | Set-Content $certificate
    $backup = Join-Path $root 'backups\aerolink-20261015-120000.zip'
    New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
    'verified backup fixture' | Set-Content -LiteralPath $backup
    $verify = {
        param($PackagePath, $TrustedPath, $Destination)
        $events.Add('verify')
        New-Item -ItemType Directory -Path (Join-Path $Destination 'app') -Force | Out-Null
        @{ sourceSha = ('a' * 40); latestMigration = 'new-migration' } | ConvertTo-Json | Set-Content (Join-Path $Destination 'manifest.json')
        '' | Set-Content (Join-Path $Destination 'app\AeroLink.Api.exe')
        [pscustomobject]@{ Version = '2.0.0'; SourceSha = ('a' * 40); LatestMigration = 'new-migration' }
    }.GetNewClosure()
    $switcher = { param($Current, $Target) $events.Add("switch:$([IO.Path]::GetFileName($Target))") }.GetNewClosure()
    $stopper = { param($DeclaredSite) $events.Add('stop') }.GetNewClosure()
    $starter = { param($DeclaredSite) $events.Add('start') }.GetNewClosure()
    $healthProver = { param($BaseUri, $Verified, $DeclaredSite) $events.Add("health:$BaseUri"); [pscustomobject]@{ Passed = $true; Detail = 'public proof passed' } }.GetNewClosure()
    $clone = {
        param($Executable, $BeforeReal)
        if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'clone received no packaged executable' }
        $events.Add('clone')
        & $BeforeReal
        [pscustomobject]@{ Applied = $true; DatabaseModified = $true; Archive = $backup; Detail = 'clone and real upgrade passed' }
    }.GetNewClosure()
    $result = Invoke-AeroLinkSiteUpdate -ProductRoot (Split-Path -Parent $PSScriptRoot) -InstallationRoot $root -Package $package -TrustedCertificate $certificate `
        -VerifyPackage $verify -CloneRunner $clone -ServiceStopper $stopper -ServiceStarter $starter -HealthProver $healthProver -Switcher $switcher
    Assert-True ($result.Applied) 'A fully proven update must report Applied.'
    Assert-True (($events -join ' -> ') -eq 'verify -> clone -> stop -> switch:2.0.0 -> start -> health:https://aerolink-updater.example.test:' + $apiPort) `
        "The updater must verify, prove the clone, stop before real mutation, switch, start and prove public health. Order: $($events -join ' -> ')"
    $journalPath = Get-AeroLinkSiteUpdateJournalPath -InstallationRoot $root
    $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    Assert-True ($journal.stage -eq 'Completed' -and $journal.sourceSha -eq ('a' * 40) -and $journal.databaseModified -and
        $journal.backupSha256 -match '^[0-9a-f]{64}$' -and
        ($journal.PSObject.Properties.Name -notcontains 'Password') -and ($journal.PSObject.Properties.Name -notcontains 'ConnectionString')) `
        'The completed journal must retain package, backup hash and recovery identity without credentials.'
}
finally { foreach ($fixture in $fixtures) { if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue } } }

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }; exit 1 }
Write-Host 'AeroLink site-updater contract passed.' -ForegroundColor Green
exit 0
