#Requires -Version 5.1
<#
    Contract coverage for the A3 site updater (#1614).

    Authoring gate: these tests protect the updater's admission boundaries, exact current target boundary, public
    instance binding joined to the redacted response's owned process and signed tree, and secret-free recovery
    journal. The regressions are package validation after a pointer mutation, old-binary execution after a schema
    change, and a health check that proves only readiness without the promoted source/installation identity or
    same-process evidence. Existing release, backup and runtime-identity suites own their underlying contracts;
    this suite injects only normal operation boundaries and uses disposable folders.

    The former full-order fixture supplied destructive callbacks that production no longer accepts. Its package,
    clone, and maintenance ordering is owned by the release/upgrade contract suites; this file retains the
    updater-specific lease, public-proof, WhatIf, and interrupted-journal failures that those suites cannot see.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteUpdater.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteServer.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1') -Force

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

    # The updater owns one transition lease through backup/clone/real maintenance. The real backup helper is
    # called in this PowerShell process and must authenticate/reuse that lease rather than deadlocking on a second
    # read/write handle or releasing the outer lease on return.
    $leaseRoot = Join-Path $root 'lease-reentry'
    New-Item -ItemType Directory -Path $leaseRoot -Force | Out-Null
    $outerLease = Enter-AeroLinkTransition -InstallationRoot $leaseRoot -Policy Preserve
    try {
        Assert-True (-not [string]::IsNullOrWhiteSpace($outerLease.OperationId) -and $outerLease.OwnerProcessId -eq $PID -and
            -not [string]::IsNullOrWhiteSpace($outerLease.OwnerCreatedAt) -and $outerLease.Phase -eq 'Transition' -and
            [IO.Path]::GetFileName($outerLease.LeasePath) -eq 'home-transition.lock') `
            'A transition lease must carry operation, owner lifetime, phase and exact lock-path identity.'
        Set-AeroLinkTransitionPhase -Lease $outerLease -Phase 'NestedBackup'
        $nestedLease = Enter-AeroLinkTransition -InstallationRoot $leaseRoot -Policy Preserve
        Assert-True ($nestedLease.Shared -and $nestedLease.OperationId -eq $outerLease.OperationId -and
            $nestedLease.OwnerProcessId -eq $outerLease.OwnerProcessId -and $nestedLease.OwnerCreatedAt -eq $outerLease.OwnerCreatedAt -and
            $nestedLease.Phase -eq 'NestedBackup' -and $nestedLease.LeasePath -eq $outerLease.LeasePath) `
            'A nested same-process backup helper must reuse and revalidate the authenticated outer transition lease.'
        Exit-AeroLinkTransition -Lease $nestedLease
        Assert-True ($outerLease.Stream.CanRead) 'The outer transition lease remains owned after nested helper return.'
    }
    finally { Exit-AeroLinkTransition -Lease $outerLease }

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

    # A public proof must refuse redirects at the HTTP boundary while retaining normal TLS validation.
    $updaterSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AeroLinkSiteUpdater.psm1') -Raw
    Assert-True ($updaterSource -match 'Invoke-WebRequest[\s\S]*MaximumRedirection\s+0') 'The public HTTP probe must disable redirects.'

    # WhatIf is a read-only preflight and must return before lease, journal, package, service or pointer mutation.
    $package = Join-Path $root 'incoming-package.zip'; 'package' | Set-Content $package
    $certificate = Join-Path $root 'trusted.cer'; 'certificate' | Set-Content $certificate
    $whatIf = Invoke-AeroLinkSiteUpdate -ProductRoot (Split-Path -Parent $PSScriptRoot) -InstallationRoot $root `
        -Package $package -TrustedCertificate $certificate -WhatIf
    Assert-True ($whatIf.WhatIf -and -not (Test-Path -LiteralPath (Get-AeroLinkSiteUpdateJournalPath -InstallationRoot $root))) `
        'WhatIf must return before creating an update journal or performing a mutation.'

    # Only a completed journal admits another update. A crash after switch/start/health remains an attended
    # recovery obligation even when the transition lease itself is no longer held.
    $journalPath = Get-AeroLinkSiteUpdateJournalPath -InstallationRoot $root
    New-Item -ItemType Directory -Path (Split-Path -Parent $journalPath) -Force | Out-Null
    @{ stage = 'StartingAndCheckingPublicHealth'; recovery = 'retain the package and inspect the running service' } |
        ConvertTo-Json | Set-Content -LiteralPath $journalPath -Encoding UTF8
    Assert-Throws {
        Invoke-AeroLinkSiteUpdate -ProductRoot (Split-Path -Parent $PSScriptRoot) -InstallationRoot $root `
            -Package $package -TrustedCertificate $certificate
    } 'incomplete.*attended recovery' 'An interrupted updater journal must refuse a second update before package or service mutation.'
}
finally { foreach ($fixture in $fixtures) { if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue } } }

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }; exit 1 }
Write-Host 'AeroLink site-updater contract passed.' -ForegroundColor Green
exit 0
