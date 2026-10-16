#Requires -Version 5.1
<#
    Contract coverage for installation identity (#881).

    The failure this suite exists to make impossible: relocating the production SOURCE quietly creating a
    second, empty AeroLink installation - a demo that starts perfectly, passes every health check, and holds
    none of the operator's data.

    Every scenario runs against disposable directories under the machine temp directory. No test connects to
    PostgreSQL, reads or writes the persistent product\.local, touches evidence, or starts a product process.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
$fixtures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -match $Pattern) { return }
        $script:failures.Add("$Message (threw, but the message did not match '$Pattern': $($_.Exception.Message))")
        return
    }
    $script:failures.Add("$Message (nothing was thrown)")
}

function New-FixtureProductRoot {
    param([switch]$WithLocal)
    $root = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-installation-" + [Guid]::NewGuid().ToString('N'))
    $script:fixtures.Add($root)
    $productRoot = Join-Path $root 'product'
    New-Item -ItemType Directory -Path $productRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $productRoot 'src\AeroLink.Api') -Force | Out-Null
    # The connection string the identity fingerprint reads. Loopback, no credential.
    @'
{ "ConnectionStrings": { "AeroLink": "Host=127.0.0.1;Port=54329;Database=aerolink;Username=postgres" } }
'@ | Set-Content -LiteralPath (Join-Path $productRoot 'src\AeroLink.Api\appsettings.Development.json') -Encoding UTF8
    if ($WithLocal) { New-Item -ItemType Directory -Path (Join-Path $productRoot '.local') -Force | Out-Null }
    return $productRoot
}

$previousOverride = $env:AEROLINK_INSTALLATION_ROOT
$env:AEROLINK_INSTALLATION_ROOT = $null

try {
    # --- 1. An ordinary clone with no pointer resolves exactly as it always did ---
    $productRoot = New-FixtureProductRoot
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
    Assert-True ($paths.InstallationRoot -eq [IO.Path]::GetFullPath((Join-Path $productRoot '.local'))) `
        'A checkout with no pointer must resolve to its own product\.local, unchanged from before #881.'
    Assert-True (-not $paths.IsRelocated) 'A checkout with no pointer is not relocated.'
    Assert-True ($paths.PostgresData -eq (Join-Path $paths.InstallationRoot 'pgdata')) 'pgdata hangs off the installation root.'
    Assert-True ($paths.PostgresBin -eq (Join-Path $paths.InstallationRoot 'postgresql\pgsql\bin')) 'The PostgreSQL client binaries hang off the installation root.'

    # --- 2. A pointer relocates every persistent path together ---
    $canonicalProduct = New-FixtureProductRoot -WithLocal
    $canonicalInstallation = Join-Path $canonicalProduct '.local'
    New-Item -ItemType Directory -Path (Join-Path $canonicalInstallation 'pgdata') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $canonicalInstallation 'pgdata\PG_VERSION') -Value '18' -Encoding ASCII
    New-Item -ItemType Directory -Path (Join-Path $canonicalInstallation 'backups') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $canonicalInstallation 'backups\aerolink-20260901-120000.zip') -Value 'x' -Encoding ASCII

    $productionProduct = New-FixtureProductRoot
    Set-AeroLinkInstallationPointer -ProductRoot $productionProduct -InstallationRoot $canonicalInstallation | Out-Null
    $productionPaths = Get-AeroLinkInstallationPaths -ProductRoot $productionProduct
    Assert-True ($productionPaths.InstallationRoot -eq [IO.Path]::GetFullPath($canonicalInstallation)) `
        'A pointer must make a second source checkout resolve to the canonical installation.'
    Assert-True ($productionPaths.IsRelocated) 'A pointed checkout reports itself relocated.'
    Assert-True ($productionPaths.PostgresData -eq (Join-Path $canonicalInstallation 'pgdata')) `
        'The relocated checkout must address the canonical cluster, not one of its own.'

    # --- 3. Relocation changes nothing about installation IDENTITY. This is acceptance 66-70. ---
    $canonicalIdentity = Get-AeroLinkInstallationIdentity -ProductRoot $canonicalProduct
    $productionIdentity = Get-AeroLinkInstallationIdentity -ProductRoot $productionProduct
    Assert-True ($productionIdentity.PostgresData -eq $canonicalIdentity.PostgresData) 'Moving source must not move the PostgreSQL cluster.'
    Assert-True ($productionIdentity.PostgresClusterInitialized -and $canonicalIdentity.PostgresClusterInitialized) `
        'The relocated checkout must see the SAME initialized cluster; a fresh one would be the second installation this guards against.'
    Assert-True ($productionIdentity.BackupRoot -eq $canonicalIdentity.BackupRoot) 'Moving source must not move the backup root.'
    Assert-True ($productionIdentity.BackupArchiveCount -eq 1 -and $canonicalIdentity.BackupArchiveCount -eq 1) `
        'Existing backups must remain visible from the relocated source.'
    Assert-True ($productionIdentity.DatabaseName -eq 'aerolink' -and $productionIdentity.DatabaseName -eq $canonicalIdentity.DatabaseName) `
        'The canonical database name must be identical before and after the source move.'
    Assert-True ($productionIdentity.DatabasePort -eq $canonicalIdentity.DatabasePort) 'The canonical database endpoint must not change with the source.'
    Assert-True ($productionIdentity.EvidenceRoot -eq $canonicalIdentity.EvidenceRoot) 'The evidence root must not change with the source.'

    # --- 4. Identity carries no credential ---
    $identityText = ($productionIdentity | ConvertTo-Json -Depth 4)
    Assert-True ($identityText -notmatch '(?i)username|password|user id') `
        'The installation identity must report the database by name and endpoint only, never a credential.'

    # --- 5. A dangling pointer FAILS CLOSED. Falling back is how the empty installation gets created. ---
    $danglingProduct = New-FixtureProductRoot -WithLocal
    [pscustomobject]@{ installationRoot = 'C:\AeroLink\definitely-not-here' } | ConvertTo-Json |
        Set-Content -LiteralPath (Get-AeroLinkInstallationPointerPath -ProductRoot $danglingProduct) -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $danglingProduct } 'will not create a second installation' `
        'A pointer naming a missing installation root must refuse, never silently fall back to this checkout.'

    # --- 6. A malformed or empty pointer refuses too ---
    $malformedProduct = New-FixtureProductRoot -WithLocal
    Set-Content -LiteralPath (Get-AeroLinkInstallationPointerPath -ProductRoot $malformedProduct) -Value '{ not json' -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $malformedProduct } 'malformed' 'A malformed pointer must refuse.'
    Set-Content -LiteralPath (Get-AeroLinkInstallationPointerPath -ProductRoot $malformedProduct) -Value '{}' -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $malformedProduct } 'does not name an installationRoot' 'A pointer naming nothing must refuse.'
    [pscustomobject]@{ installationRoot = 'relative\path' } | ConvertTo-Json |
        Set-Content -LiteralPath (Get-AeroLinkInstallationPointerPath -ProductRoot $malformedProduct) -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $malformedProduct } 'absolute' 'A relative installation root must refuse.'

    # --- 7. Pointing at a root that does not exist is refused at WRITE time as well as at read time ---
    Assert-Throws { Set-AeroLinkInstallationPointer -ProductRoot (New-FixtureProductRoot) -InstallationRoot 'C:\AeroLink\nowhere' } `
        'does not exist' 'Writing a pointer to a missing installation root must refuse.'

    # --- 8. Instance identity is declared, never inferred ---
    $undeclared = New-FixtureProductRoot -WithLocal
    $devInstance = Get-AeroLinkInstanceConfig -ProductRoot $undeclared -Mode Development
    Assert-True ($devInstance.Classification -eq 'Undeclared') 'An installation that declared nothing must not claim a classification.'
    Assert-True ($devInstance.Label -eq 'LOCAL DEVELOPMENT') 'An undeclared development launch gets a modest default label.'
    $prodInstance = Get-AeroLinkInstanceConfig -ProductRoot $undeclared -Mode HomeCanonical
    Assert-True ($prodInstance.Label -eq 'LOCAL PRODUCTION' -and $prodInstance.Classification -eq 'Undeclared') `
        'A production-mode launch of an undeclared installation must NOT call itself HOME CANONICAL.'

    Set-AeroLinkInstanceConfig -ProductRoot $undeclared -Label 'HOME CANONICAL' -Classification 'HomeCanonical' | Out-Null
    $declared = Get-AeroLinkInstanceConfig -ProductRoot $undeclared -Mode HomeCanonical
    Assert-True ($declared.Label -eq 'HOME CANONICAL' -and $declared.Classification -eq 'HomeCanonical' -and $declared.Declared) `
        'A declared instance must report exactly what the operator declared.'

    # A stable instance identifier, minted once and never changing.
    #
    # #881 asks for one alongside source, mode and classification. It answers what a label cannot: two
    # installations can both be labelled WORK-LAPTOP LOCAL, and a restored snapshot carries the source's
    # label with it.
    # Minting is deliberate, not a side effect of reading. A getter that writes made
    # REFRESH_AEROLINK_FROM_HOME.bat Preview change a file on disk while reporting nothing had changed, and
    # "read-only" has to be literally true to be worth saying.
    $identified = New-FixtureProductRoot -WithLocal
    $pureRead = Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development
    Assert-True ([string]::IsNullOrWhiteSpace($pureRead.InstanceId)) 'Reading an installation must not mint an identifier for it.'
    Assert-True (-not (Test-Path -LiteralPath $pureRead.ConfigPath -PathType Leaf)) 'A read of an undeclared installation must not create its declaration file.'

    $firstRead = Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development -EnsureInstanceId
    Assert-True (-not [string]::IsNullOrWhiteSpace($firstRead.InstanceId)) 'An installation must have a stable instance identifier once one is ensured.'
    $secondRead = Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development -EnsureInstanceId
    Assert-True ($secondRead.InstanceId -eq $firstRead.InstanceId) 'The instance identifier must be minted once, not regenerated on every read.'
    Assert-True ((Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development).InstanceId -eq $firstRead.InstanceId) `
        'A plain read must still report the identifier that already exists.'
    Set-AeroLinkInstanceConfig -ProductRoot $identified -Label 'WORK-LAPTOP LOCAL' -Classification 'WorkLaptopLocal' | Out-Null
    Assert-True ((Get-AeroLinkInstanceConfig -ProductRoot $identified -Mode Development).InstanceId -eq $firstRead.InstanceId) `
        'Declaring a label must not change the instance identifier.'
    Assert-True ($firstRead.InstanceId -notmatch [regex]::Escape($env:COMPUTERNAME)) 'The instance identifier must identify without describing the machine.'
    Assert-True ((Get-AeroLinkInstanceConfig -ProductRoot (New-FixtureProductRoot -WithLocal) -Mode Development -EnsureInstanceId).InstanceId -ne $firstRead.InstanceId) `
        'Two installations must not share an identifier.'

    # Site classifications are part of the same read/write authority as HOME and local modes. The installer writes
    # both values, and every subsequent guard must read them back rather than rejecting an otherwise valid site.
    foreach ($siteClassification in @('SiteProduction', 'SiteDevelopment')) {
        $siteIdentity = New-FixtureProductRoot -WithLocal
        Set-AeroLinkInstanceConfig -ProductRoot $siteIdentity -Label $siteClassification -Classification $siteClassification | Out-Null
        $readSiteIdentity = Get-AeroLinkInstanceConfig -ProductRoot $siteIdentity -Mode Development
        Assert-True ($readSiteIdentity.Classification -eq $siteClassification) "The $siteClassification classification must survive the shared instance write/read path."
    }

    # A classification outside the supported set fails closed.
    #
    # Every guard that protects real data compares this string for equality: the development launcher refuses
    # HomeCanonical, the snapshot import refuses to overwrite one. A typo - HomeCanonica - makes both
    # comparisons false, so a file that LOOKS like it declares the canonical installation silently removes the
    # protection it appears to establish, while the badge shows the typo back as though it meant something.
    $typo = New-FixtureProductRoot -WithLocal
    Set-AeroLinkInstanceConfig -ProductRoot $typo -Label 'HOME CANONICAL' -Classification 'HomeCanonical' | Out-Null
    $typoPath = (Get-AeroLinkInstanceConfig -ProductRoot $typo -Mode Development).ConfigPath
    (Get-Content -LiteralPath $typoPath -Raw).Replace('HomeCanonical', 'HomeCanonica') | Set-Content -LiteralPath $typoPath -Encoding UTF8
    Assert-Throws { Get-AeroLinkInstanceConfig -ProductRoot $typo -Mode Development } 'not one of' `
        'A classification outside the supported set must fail closed rather than silently disable every guard that reads it.'

    # LocalDemo is advertised by DECLARE_AEROLINK_INSTANCE.bat, so it must actually be writable. It was not:
    # Preview succeeded and Declare died in parameter binding, recording nothing.
    $demo = New-FixtureProductRoot -WithLocal
    Set-AeroLinkInstanceConfig -ProductRoot $demo -Label 'LOCAL DEMO' -Classification 'LocalDemo' | Out-Null
    Assert-True ((Get-AeroLinkInstanceConfig -ProductRoot $demo -Mode Development).Classification -eq 'LocalDemo') `
        'Every classification the operator is offered must be one the shared authority accepts.'

    # A declaration survives an unrelated update, so snapshot metadata cannot erase the label.
    Set-AeroLinkInstanceConfig -ProductRoot $undeclared -Snapshot @{ sourceLabel = 'HOME CANONICAL'; createdAtUtc = '2026-09-01T10:00:00Z' } | Out-Null
    $withSnapshot = Get-AeroLinkInstanceConfig -ProductRoot $undeclared -Mode Development
    Assert-True ($withSnapshot.Label -eq 'HOME CANONICAL') 'Recording snapshot metadata must not discard the declared label.'
    # Compared as an instant, not as text: PowerShell 7 deserializes an ISO timestamp in JSON as a DateTime
    # while Windows PowerShell leaves it as a string, and a contract that passes on one host and fails on the
    # other is worse than no contract. Both supported launcher hosts must agree on the instant.
    Assert-True ([DateTimeOffset]::Parse($withSnapshot.SnapshotCreatedAtUtc).UtcDateTime -eq ([datetime]'2026-09-01T10:00:00Z').ToUniversalTime()) `
        "Snapshot age must be inspectable as an exact instant on both PowerShell hosts; got '$($withSnapshot.SnapshotCreatedAtUtc)'."

    # --- 8b. Site server installations (#1614): only the site launcher runs one, and it runs nothing else ---
    # A HOME launcher runs Development with demonstration data. Pointed at a site installation's database it would
    # seed demo accounts into production, so every non-site mode must refuse a site declaration, exactly as it
    # refused one as an unknown classification before these existed.
    $sitePath = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-site-' + [Guid]::NewGuid().ToString('N'))
    $script:fixtures.Add($sitePath)
    $siteProduct = New-FixtureProductRoot
    $siteInstallation = Join-Path $sitePath 'AeroLink Production'
    New-Item -ItemType Directory -Path $siteInstallation -Force | Out-Null
    Set-AeroLinkInstanceConfig -ProductRoot $siteProduct -InstallationRoot $siteInstallation -Label 'SITE PRODUCTION' -Classification 'SiteProduction' | Out-Null
    foreach ($mode in @('Development', 'HomeCanonical')) {
        Assert-Throws { Get-AeroLinkInstanceConfig -ProductRoot $siteProduct -InstallationRoot $siteInstallation -Mode $mode } 'Only a site-aware caller' `
            "A $mode launch must refuse a SiteProduction installation."
    }
    Assert-True ((Get-AeroLinkInstanceConfig -ProductRoot $siteProduct -InstallationRoot $siteInstallation -Mode Site).Classification -eq 'SiteProduction') `
        'The site launcher must read a SiteProduction declaration.'
    Assert-Throws { Get-AeroLinkInstanceConfig -ProductRoot $demo -Mode Site } 'accepts only an installation declared Site' `
        'A Site read must refuse an installation that is not declared SiteProduction or SiteDevelopment.'
    Assert-Throws { Get-AeroLinkInstanceConfig -ProductRoot (New-FixtureProductRoot -WithLocal) -Mode Site } 'accepts only an installation declared Site' `
        'A Site read must refuse an undeclared installation rather than assume it is a site server.'

    function Get-FreeLoopbackPortForLauncher {
        $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start()
        try { return $probe.LocalEndpoint.Port } finally { $probe.Stop() }
    }
    # The launcher composes Production and refuses to run without a database of its own. -ValidateOnly starts nothing.
    $developmentSite = Join-Path $sitePath 'AeroLink Development'
    New-Item -ItemType Directory -Path $developmentSite -Force | Out-Null
    Set-AeroLinkInstanceConfig -ProductRoot $siteProduct -InstallationRoot $developmentSite -Label 'SITE DEVELOPMENT' -Classification 'SiteDevelopment' | Out-Null
    $siteServerJson = Join-Path $developmentSite 'site-server.json'
    $siteServerSettings = '{ "bindAddress": "0.0.0.0", "http1Only": true, "certificate": { "thumbprint": "00112233445566778899AABBCCDDEEFF00112233" } }'
    $siteServerSettings | Set-Content -LiteralPath $siteServerJson -Encoding UTF8
    $fakeApi = Join-Path $developmentSite 'AeroLink.Api.dll'
    Set-Content -LiteralPath $fakeApi -Value '' -Encoding ASCII
    $siteLauncher = Join-Path $PSScriptRoot 'Start-AeroLinkSite.ps1'
    # Without a site declaration there is no port or public host name to serve: refused before anything else.
    Assert-Throws { & $siteLauncher -InstallationRoot $developmentSite -ApiPath $fakeApi -ValidateOnly } 'no instanceId|no site declaration' `
        'A site installation without a site declaration must refuse.'
    # The API port and the public host name are declared once, in the site declaration (#1614 1.3).
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
    $launcherApiPort = Get-FreeLoopbackPortForLauncher
    $launcherPostgresPort = Get-FreeLoopbackPortForLauncher
    while ($launcherPostgresPort -eq $launcherApiPort) { $launcherPostgresPort = Get-FreeLoopbackPortForLauncher }
    $launcherAuthority = Join-Path $sitePath 'notification-authority'
    $developmentDeclaration = Set-AeroLinkSiteDeclaration -InstallationRoot $developmentSite -ApiPort $launcherApiPort -PostgresPort $launcherPostgresPort `
        -PublicHostName 'aerolink-site.example.test' -NotificationAuthorityRoot $launcherAuthority
    $previousConnection = $env:ConnectionStrings__AeroLink
    try {
        $env:ConnectionStrings__AeroLink = $null
        Assert-Throws { & $siteLauncher -InstallationRoot $developmentSite -ApiPath $fakeApi -ValidateOnly } 'ConnectionStrings__AeroLink is not set' `
            'The site launcher must never fall back to a default database.'
        $env:ConnectionStrings__AeroLink = 'Host=127.0.0.1;Port=55432;Database=aerolink_site_dev;Username=aerolink'
        $plan = & $siteLauncher -InstallationRoot $developmentSite -ApiPath $fakeApi -ValidateOnly
        $s = $plan.Settings
        Assert-True ($s['ASPNETCORE_ENVIRONMENT'] -eq 'Production' -and $s['Runtime__Mode'] -eq 'SITE-DEVELOPMENT' -and $s['Instance__Classification'] -eq 'SiteDevelopment') `
            'A SiteDevelopment installation runs the Production profile and says which installation it is.'
        Assert-True ($s['SiteServer__Enabled'] -eq 'true' -and $s['SiteServer__Port'] -eq [string]$launcherApiPort -and $s['SiteServer__PublicHost'] -eq 'aerolink-site.example.test' -and
            $s['SiteServer__Http1Only'] -eq 'true' -and $s['SiteServer__Certificate__Thumbprint'] -eq '00112233445566778899AABBCCDDEEFF00112233') `
            'The site settings reach the API as the SiteServer section, with the port and host name from the site declaration.'
        Assert-True ($s['DemoData__Enabled'] -eq 'false' -and $s['Identity__SeedDemoAccounts'] -eq 'false' -and $s['Identity__AllowDemoAccounts'] -eq 'false' -and $s['Identity__CookieSecure'] -eq 'true' -and $s['Integrations__AllowInsecureWebhookTargets'] -eq 'false') `
            'A site launch has no demonstration data or accounts, secure cookies and no insecure webhooks.'
        # Installation-owned state (#1614 1.5) reaches the API; the password-less connection string does not.
        Assert-True ($s['Evidence__Root'] -eq $developmentDeclaration.EvidenceRoot -and $s['DataProtection__KeyRingPath'] -eq $developmentDeclaration.KeyRingPath -and
            $s['Connector__DeploymentId'] -eq $developmentDeclaration.ConnectorDeploymentId -and $s['AEROLINK_NOTIFICATION_AUTHORITY_ROOT'] -eq $developmentDeclaration.NotificationAuthorityRoot -and
            -not $s.Contains('ConnectionStrings__AeroLink')) `
            'The launcher must hand the API its installation-owned state and leave the inherited connection string alone.'
        Assert-Throws { & $siteLauncher -InstallationRoot $siteInstallation -ApiPath $fakeApi -ValidateOnly } 'no instanceId|no site declaration' `
            'A site installation without a site declaration must refuse.'
        foreach ($duplicate in @('"port": 5444', '"publicHost": "other.example.test"')) {
            ('{ ' + $duplicate + ', "bindAddress": "0.0.0.0", "certificate": { "thumbprint": "00112233445566778899AABBCCDDEEFF00112233" } }') |
                Set-Content -LiteralPath $siteServerJson -Encoding UTF8
            Assert-Throws { & $siteLauncher -InstallationRoot $developmentSite -ApiPath $fakeApi -ValidateOnly } 'declared once' `
                "site-server.json must not declare a second source beside the site declaration: $duplicate."
        }
        Remove-Item -LiteralPath $siteServerJson -Force
        Assert-Throws { & $siteLauncher -InstallationRoot $developmentSite -ApiPath $fakeApi -ValidateOnly } 'site-server.json' `
            'A site installation without site-server.json has no HTTPS endpoint and must refuse.'
        $siteServerSettings | Set-Content -LiteralPath $siteServerJson -Encoding UTF8

        # Revoke reads a declared site in Site mode and resolves its authority under the site's own root.
        $previousRoot = $env:AEROLINK_INSTALLATION_ROOT
        try {
            $env:AEROLINK_INSTALLATION_ROOT = $developmentSite
            Import-Module (Join-Path $PSScriptRoot 'AeroLinkNotificationAuthority.psm1') -Force
            Revoke-AeroLinkNotificationSendGeneration -ProductRoot $siteProduct | Out-Null
            $instanceId = (Get-Content -LiteralPath (Join-Path $developmentSite 'instance.json') -Raw | ConvertFrom-Json).instanceId
            Assert-True (Test-Path -LiteralPath (Join-Path $developmentDeclaration.NotificationAuthorityRoot "$instanceId.json") -PathType Leaf) `
                'Revoking on a declared site must succeed and write under the site notification authority root.'
        }
        finally {
            $env:AEROLINK_INSTALLATION_ROOT = $previousRoot
            # Revoke force-imports the installation module into its own scope, which unloads this script's copy.
            Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
        }
    }
    finally { $env:ConnectionStrings__AeroLink = $previousConnection }

    # --- 9. The environment override is absolute-and-must-exist, so qualification cannot point at nothing ---
    $env:AEROLINK_INSTALLATION_ROOT = 'not-absolute'
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $productRoot } 'absolute' 'A relative AEROLINK_INSTALLATION_ROOT must refuse.'
    $env:AEROLINK_INSTALLATION_ROOT = 'C:\AeroLink\nowhere-at-all'
    Assert-Throws { Get-AeroLinkInstallationRoot -ProductRoot $productRoot } 'does not exist' 'A missing AEROLINK_INSTALLATION_ROOT must refuse.'
    $env:AEROLINK_INSTALLATION_ROOT = $canonicalInstallation
    Assert-True ((Get-AeroLinkInstallationRoot -ProductRoot $productRoot) -eq [IO.Path]::GetFullPath($canonicalInstallation)) `
        'A valid override wins over the pointer and the default.'

    # --- 10. Service ports move ONLY for a disposable qualification installation, and never onto a persistent port ---
    $previousPorts = @($env:AEROLINK_QUALIFICATION_API_PORT, $env:AEROLINK_QUALIFICATION_POSTGRES_PORT)
    try {
        $env:AEROLINK_QUALIFICATION_API_PORT = '5196'; $env:AEROLINK_QUALIFICATION_POSTGRES_PORT = '55329'
        $moved = Get-AeroLinkServiceEndpoints
        Assert-True ($moved.Qualification -and $moved.ApiPort -eq 5196 -and $moved.PostgresPort -eq 55329 -and $moved.ConnectionString -match 'Port=55329') 'A disposable installation may move both service ports, and the connection string moves with them.'
        $env:AEROLINK_INSTALLATION_ROOT = $null
        $fixed = Get-AeroLinkServiceEndpoints
        Assert-True (-not $fixed.Qualification -and $fixed.ApiPort -eq 5080 -and $fixed.PostgresPort -eq 54329) 'Without AEROLINK_INSTALLATION_ROOT the port override is ignored: a real installation stays on 5080/54329.'
        $env:AEROLINK_INSTALLATION_ROOT = $canonicalInstallation
        $env:AEROLINK_QUALIFICATION_POSTGRES_PORT = '54329'
        Assert-Throws { Get-AeroLinkServiceEndpoints } 'persistent' 'A disposable installation may never be pointed at the persistent PostgreSQL port.'
        $env:AEROLINK_QUALIFICATION_POSTGRES_PORT = '55329'; $env:AEROLINK_QUALIFICATION_API_PORT = '5080'
        Assert-Throws { Get-AeroLinkServiceEndpoints } 'persistent' 'A disposable installation may never be pointed at the persistent API port.'
        $env:AEROLINK_QUALIFICATION_API_PORT = $null
        Assert-Throws { Get-AeroLinkServiceEndpoints } 'both' 'A half-specified override refuses rather than mixing a disposable and a persistent port.'
    }
    finally { $env:AEROLINK_QUALIFICATION_API_PORT = $previousPorts[0]; $env:AEROLINK_QUALIFICATION_POSTGRES_PORT = $previousPorts[1] }
    $env:AEROLINK_INSTALLATION_ROOT = $null

    # --- 11. A site server installation (#1614) declares its own ports; HOME keeps 5080/54329 ---
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteServer.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkNotificationAuthority.psm1') -Force
    function Get-FreeLoopbackPort {
        $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start()
        try { return $probe.LocalEndpoint.Port } finally { $probe.Stop() }
    }
    $siteFixture = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-site-' + [Guid]::NewGuid().ToString('N'))
    $siteRoot = Join-Path $siteFixture 'AeroLink Production'
    $siteAuthority = Join-Path $siteFixture 'notification-authority'
    New-Item -ItemType Directory -Path $siteRoot -Force | Out-Null
    $siteConfig = Join-Path $siteRoot 'instance.json'
    $sitePlan = @()
    try {
        @{ classification = 'SiteProduction'; label = 'SITE PRODUCTION' } | ConvertTo-Json | Set-Content -LiteralPath $siteConfig -Encoding UTF8
        $siteApiPort = Get-FreeLoopbackPort
        $sitePostgresPort = Get-FreeLoopbackPort
        while ($sitePostgresPort -eq $siteApiPort) { $sitePostgresPort = Get-FreeLoopbackPort }
        $declared = Set-AeroLinkSiteDeclaration -InstallationRoot $siteRoot -ApiPort $siteApiPort -PostgresPort $sitePostgresPort -NotificationAuthorityRoot $siteAuthority
        $siteEndpoints = Get-AeroLinkServiceEndpoints -InstallationRoot $siteRoot
        Assert-True ($siteEndpoints.Site -and $siteEndpoints.ApiPort -eq $siteApiPort -and $siteEndpoints.PostgresPort -eq $sitePostgresPort -and
            $siteEndpoints.DeclaredApiPort -eq $siteApiPort -and $siteEndpoints.DeclaredPostgresPort -eq $sitePostgresPort) `
            'A site installation must resolve the API and PostgreSQL ports its instance.json declares.'
        Assert-True ($siteEndpoints.ConnectionString -eq "Host=127.0.0.1;Port=$sitePostgresPort;Database=aerolink;Username=aerolink_app") `
            "A site installation connects as its non-superuser role on its own port, with no password in the endpoint: $($siteEndpoints.ConnectionString)"
        Assert-True ($declared.ConnectorDeploymentId -match '^aerolink-site-[0-9a-f]{16}$') "A site declaration must carry an explicit connector deployment id; got '$($declared.ConnectorDeploymentId)'."
        # D2 revised: per-installation virtual accounts, named from the installation, SIDs computed as Windows does.
        Assert-True ($declared.RunAsAccount -eq 'NT SERVICE\AeroLink-Production' -and $declared.PostgresRunAsAccount -eq 'NT SERVICE\AeroLink-Production-PostgreSQL' -and
            $declared.RunAsSid -eq (Get-AeroLinkServiceSid 'AeroLink-Production') -and $declared.PostgresRunAsSid -eq (Get-AeroLinkServiceSid 'AeroLink-Production-PostgreSQL') -and
            $declared.RunAsSid -ne $declared.PostgresRunAsSid) `
            "A site must default to its own API and PostgreSQL virtual accounts; got $($declared.RunAsAccount) $($declared.RunAsSid) / $($declared.PostgresRunAsAccount) $($declared.PostgresRunAsSid)."
        Assert-True (Assert-AeroLinkSiteServiceSid -Site $declared -Role Api) 'The production API binding must be the computed virtual service SID.'
        Assert-True (Assert-AeroLinkSiteServiceSid -Site $declared -Role Postgres) 'The production PostgreSQL binding must be the computed virtual service SID.'
        Assert-Throws {
            Assert-AeroLinkSiteServiceSid -Site ([pscustomobject]@{
                ServiceName = 'AeroLink-Production'; RunAsSid = 'S-1-5-32-545'
            }) -Role Api
        } 'override|computed' 'An arbitrary Users SID must not authorize a production ACL writer.'
        Assert-True ((Get-AeroLinkServiceSid 'TrustedInstaller') -eq 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') 'The service SID must match the documented NT SERVICE\TrustedInstaller SID.'
        $translated = (New-Object Security.Principal.NTAccount('NT SERVICE', 'EventLog')).Translate([Security.Principal.SecurityIdentifier]).Value
        Assert-True ((Get-AeroLinkServiceSid 'EventLog') -eq $translated) "The computed service SID must equal Windows' own for an installed service; Windows says $translated."
        $instanceId = (Get-Content -LiteralPath $siteConfig -Raw | ConvertFrom-Json).instanceId
        $parsedInstanceId = [guid]::Empty
        Assert-True ([guid]::TryParse([string]$instanceId, [ref]$parsedInstanceId))'Declaring a site must mint its instanceId, so nothing writes instance.json once the root is read-only.'
        $redeclared = Set-AeroLinkSiteDeclaration -InstallationRoot $siteRoot -ApiPort $siteApiPort -PostgresPort $sitePostgresPort
        Assert-True ($redeclared.ConnectorDeploymentId -eq $declared.ConnectorDeploymentId -and $redeclared.NotificationAuthorityRoot -eq [IO.Path]::GetFullPath($siteAuthority) -and
            (Get-Content -LiteralPath $siteConfig -Raw | ConvertFrom-Json).instanceId -eq $instanceId) `
            'Re-declaring a site must keep its connector deployment id, instance id and notification authority root; a new id would orphan every enrolled connector.'
        # #1614 1.9 (D7): the daily verified backup runs as the installation's own API account, never a person.
        $backupTask = New-AeroLinkSiteBackupTaskDefinition -InstallationRoot $siteRoot -ScriptRoot 'C:\AeroLink\AeroLink Production\program\product\scripts'
        Assert-True ($backupTask.Principal.UserId -eq $declared.RunAsAccount -and [string]$backupTask.Principal.LogonType -eq 'ServiceAccount' -and [string]$backupTask.Principal.RunLevel -eq 'Limited') `
            "The site backup task must run as the API's virtual account with no password; got $($backupTask.Principal.UserId) $($backupTask.Principal.LogonType) $($backupTask.Principal.RunLevel)."
        Assert-True ($backupTask.Action.Arguments -match [regex]::Escape('Invoke-AeroLinkScheduledBackup.ps1') -and $backupTask.Action.Arguments -match [regex]::Escape("-InstallationRoot `"$([IO.Path]::GetFullPath($siteRoot))`"") -and
            $backupTask.Action.Arguments -match '-NonInteractive') `
            "The site backup task must run the verified scheduled backup for exactly this installation, non-interactively: $($backupTask.Action.Arguments)"
        # A root written with a trailing backslash must not end its quoted argument with \", which Windows
        # PowerShell reads as an escaped quote, swallowing the arguments after it into the path.
        $slashTask = New-AeroLinkSiteBackupTaskDefinition -InstallationRoot ($siteRoot.TrimEnd('\') + '\') -ScriptRoot 'C:\AeroLink\AeroLink Production\program\product\scripts'
        Assert-True ($slashTask.Action.Arguments -notmatch '\\"' -and $slashTask.Action.Arguments -match [regex]::Escape("-InstallationRoot `"$([IO.Path]::GetFullPath($siteRoot).TrimEnd('\'))`" -RetentionDays")) `
            "A trailing backslash on the root must not reach the task's quoted argument: $($slashTask.Action.Arguments)"
        # A runAsSid override (qualification only) names an account the task would not run as: refused before anything
        # is registered, whether or not this run is elevated.
        $overrideDocument = Get-Content -LiteralPath $siteConfig -Raw | ConvertFrom-Json
        $overrideDocument.site | Add-Member -NotePropertyName runAsSid -NotePropertyValue ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -Force
        $overrideDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $siteConfig -Encoding UTF8
        try { Assert-Throws { Register-AeroLinkSiteBackupTask -InstallationRoot $siteRoot } 'is an override' 'Registering the backup task must refuse a runAsSid override.' }
        finally {
            $restored = Get-Content -LiteralPath $siteConfig -Raw | ConvertFrom-Json
            $restored.site.PSObject.Properties.Remove('runAsSid')
            $restored | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $siteConfig -Encoding UTF8
        }
        Assert-Throws { Set-AeroLinkSiteDeclaration -InstallationRoot $siteRoot -ApiPort $siteApiPort -PostgresPort $sitePostgresPort -ConnectorDeploymentId 'aerolink-other' } 'orphan' `
            'A site declaration must refuse to replace an existing connector deployment id.'

        $runtime = Get-AeroLinkSiteRuntimeEnvironment -InstallationRoot $siteRoot
        Assert-True ($runtime['DataProtection__KeyRingPath'] -eq (Join-Path $siteRoot 'keys\data-protection') -and $runtime['DataProtection__ApplicationName'] -eq 'AeroLink' -and
            $runtime['DataProtection__ProtectKeysWith'] -eq 'DpapiLocalMachine' -and $runtime['Evidence__Root'] -eq (Join-Path $siteRoot 'evidence') -and
            $runtime['SiteServer__InstallationRoot'] -eq [IO.Path]::GetFullPath($siteRoot) -and $runtime['SiteServer__ServiceSid'] -eq $declared.RunAsSid -and
            $runtime['Connector__SigningKeyPath'] -eq (Join-Path $siteRoot 'keys\connector\connector-signing-key.pem') -and
            $runtime['Connector__DeploymentId'] -eq $declared.ConnectorDeploymentId -and
            $runtime['AEROLINK_NOTIFICATION_AUTHORITY_ROOT'] -eq [IO.Path]::GetFullPath($siteAuthority) -and
            $runtime['ConnectionStrings__AeroLink'] -notmatch 'Password') `
            'A site API must be given installation-owned key ring, evidence, connector and notification-authority state, and no password unless asked.'

        # A machine certificate key may arrive with a broad inherited grant. The installer must replace that DACL,
        # not add the API SID beside Users. This uses only a disposable file; the machine certificate store is not
        # touched by the contract test.
        $certificateKeyRoot = Join-Path $siteFixture 'certificate-key'
        $fixtures.Add($certificateKeyRoot)
        New-Item -ItemType Directory -Path $certificateKeyRoot -Force | Out-Null
        $certificateKey = Join-Path $certificateKeyRoot 'private-key'
        [IO.File]::WriteAllText($certificateKey, 'temporary private-key fixture')
        $broad = Get-Acl -LiteralPath $certificateKey
        $broad.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),
            [Security.AccessControl.FileSystemRights]::Read,
            [Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $certificateKey -AclObject $broad
        Set-AeroLinkCertificatePrivateKeyAcl -Path $certificateKey -Sid $declared.RunAsSid | Out-Null
        $certificateAcl = Get-Acl -LiteralPath $certificateKey
        $certificateRules = @($certificateAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
        $certificateRuleSids = @($certificateRules | ForEach-Object IdentityReference | ForEach-Object Value | Sort-Object -Unique)
        Assert-True ($certificateAcl.AreAccessRulesProtected -and ($certificateRuleSids -join ',') -eq ((@($declared.RunAsSid, 'S-1-5-18', 'S-1-5-32-544') | Sort-Object -Unique) -join ',')) `
            'The certificate private-key DACL must be protected and contain only the API, SYSTEM and Administrators.'
        Assert-True (-not @($certificateRules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' }).Count) `
            'A pre-existing Users grant must not survive certificate private-key ACL hardening.'

        # #1647: both independent notification-authority files must be readable by the site API SID while retaining
        # the owner/SYSTEM/Administrators write boundary. The authority is outside the installation and disposable.
        $siteSource = Join-Path $siteFixture 'source'
        New-Item -ItemType Directory -Path $siteSource -Force | Out-Null
        Set-AeroLinkInstallationPointer -ProductRoot $siteSource -InstallationRoot $siteRoot | Out-Null
        $authorityResult = Revoke-AeroLinkNotificationSendGeneration -ProductRoot $siteSource
        $authorityPath = Get-AeroLinkNotificationAuthorityPath -InstanceId $instanceId -InstallationRoot $siteRoot
        foreach ($authorityFile in @($authorityPath, "$authorityPath.generation")) {
            $authorityAcl = Get-Acl -LiteralPath $authorityFile
            $authorityRules = @($authorityAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
            $siteRead = @($authorityRules | Where-Object { $_.IdentityReference.Value -eq $declared.RunAsSid -and $_.AccessControlType -eq 'Allow' })
            Assert-True ($authorityAcl.AreAccessRulesProtected -and $siteRead.Count -eq 1 -and
                ($siteRead[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::Read) -eq [Security.AccessControl.FileSystemRights]::Read) `
                "The site API must read the notification authority file '$authorityFile'."
            Assert-True (-not @($authorityRules | Where-Object { $_.IdentityReference.Value -in @('S-1-5-32-545', 'S-1-5-11', 'S-1-1-0') }).Count) `
                "The notification authority file '$authorityFile' must not grant broad Users/Authenticated Users/Everyone access."
        }

        # A disposable declaration may carry an override for low-level qualification, but a production authority
        # revoke must refuse before replacing either authority file. This is the fail-before proof for #1647's
        # caller boundary: the old caller-supplied SID path would have written a Users read ACE here.
        $validSiteConfigBytes = [IO.File]::ReadAllBytes($siteConfig)
        $authorityBefore = [IO.File]::ReadAllBytes($authorityPath)
        $generationBefore = [IO.File]::ReadAllBytes("$authorityPath.generation")
        $overriddenSite = Get-Content -LiteralPath $siteConfig -Raw | ConvertFrom-Json
        $overriddenSite.site | Add-Member -NotePropertyName runAsSid -NotePropertyValue 'S-1-5-32-545' -Force
        $overriddenSite | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $siteConfig -Encoding UTF8
        try {
            Assert-Throws { Revoke-AeroLinkNotificationSendGeneration -ProductRoot $siteSource } 'override|computed' `
                'Notification authority provisioning must reject an arbitrary Users SID before writing.'
            Assert-True (([IO.File]::ReadAllBytes($authorityPath) -join ',') -eq ($authorityBefore -join ',') -and
                ([IO.File]::ReadAllBytes("$authorityPath.generation") -join ',') -eq ($generationBefore -join ',')) `
                'A rejected notification authority SID must leave both authority files unchanged.'
        }
        finally { [IO.File]::WriteAllBytes($siteConfig, $validSiteConfigBytes) }

        # The PostgreSQL bootstrap is another production caller that writes installation folders and protected
        # secrets. Its guard must run before it even inspects the binary path when the declaration names an override.
        $postgresOverrideBytes = [IO.File]::ReadAllBytes($siteConfig)
        $postgresOverride = Get-Content -LiteralPath $siteConfig -Raw | ConvertFrom-Json
        $postgresOverride.site | Add-Member -NotePropertyName postgresRunAsSid -NotePropertyValue 'S-1-5-32-545' -Force
        $postgresOverride | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $siteConfig -Encoding UTF8
        try {
            Assert-Throws {
                Initialize-AeroLinkSitePostgres -InstallationRoot $siteRoot -PostgresBin (Join-Path $siteFixture 'no-postgres')
            } 'override|computed' 'PostgreSQL bootstrap must reject an arbitrary SID before folder or secret writes.'
        }
        finally { [IO.File]::WriteAllBytes($siteConfig, $postgresOverrideBytes) }

        # #1649: the installer must define the daily verified backup against the installed version's scripts,
        # with the API virtual account as the only task identity and the accepted fifteen-day retention. The
        # registration override case is the regression owner: before the caller guard a qualification SID could
        # reach Task Scheduler and receive access to the application password.
        $backupScriptRoot = Join-Path $siteFixture 'program\current\scripts'
        New-Item -ItemType Directory -Path $backupScriptRoot -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $backupScriptRoot 'Invoke-AeroLinkScheduledBackup.ps1') -Value '# disposable task-definition fixture' -Encoding UTF8
        $backupTask = New-AeroLinkSiteBackupTaskDefinition -InstallationRoot $siteRoot -ScriptRoot $backupScriptRoot
        Assert-True ($backupTask.Principal.UserId -eq $declared.RunAsAccount -and [string]$backupTask.Principal.LogonType -eq 'ServiceAccount' -and [string]$backupTask.Principal.RunLevel -eq 'Limited') `
            "The site backup task must run as the API's computed virtual account with no password; got $($backupTask.Principal.UserId) $($backupTask.Principal.LogonType) $($backupTask.Principal.RunLevel)."
        Assert-True ($backupTask.Action.Arguments -match [regex]::Escape('Invoke-AeroLinkScheduledBackup.ps1') -and
            $backupTask.Action.Arguments -match [regex]::Escape("-InstallationRoot `"$([IO.Path]::GetFullPath($siteRoot))`" -RetentionDays 15") -and
            $backupTask.Action.Arguments -match '-NonInteractive') `
            "The site backup task must run the versioned verified runner for exactly this installation with fifteen-day retention: $($backupTask.Action.Arguments)"
        $slashTask = New-AeroLinkSiteBackupTaskDefinition -InstallationRoot ($siteRoot.TrimEnd('\') + '\') -ScriptRoot $backupScriptRoot
        Assert-True ($slashTask.Action.Arguments -notmatch '\\"' -and $slashTask.Action.Arguments -match [regex]::Escape("-InstallationRoot `"$([IO.Path]::GetFullPath($siteRoot).TrimEnd('\'))`" -RetentionDays 15")) `
            "A trailing backslash on the installation root must not corrupt the scheduled task arguments: $($slashTask.Action.Arguments)"
        # Authoring gate: production registration must refuse a missing versioned runner before Task Scheduler.
        # The immutable pre-fix witness reaches Register-ScheduledTask with that runner absent; the task-definition
        # assertions above cannot see this registration boundary, and the production guard needs no test-only seam.
        Assert-Throws { Register-AeroLinkSiteBackupTask -InstallationRoot $siteRoot -ScriptRoot (Join-Path $siteFixture 'program\current\missing-scripts') } 'runner does not exist' `
            'Registration must reject a missing versioned backup runner before touching Task Scheduler.'
        $backupOverrideBytes = [IO.File]::ReadAllBytes($siteConfig)
        $backupOverride = Get-Content -LiteralPath $siteConfig -Raw | ConvertFrom-Json
        $backupOverride.site | Add-Member -NotePropertyName runAsSid -NotePropertyValue 'S-1-5-32-545' -Force
        $backupOverride | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $siteConfig -Encoding UTF8
        try {
            Assert-Throws { Register-AeroLinkSiteBackupTask -InstallationRoot $siteRoot -ScriptRoot $backupScriptRoot } 'override|computed' `
                'Registering a site backup task must reject an arbitrary API SID before Task Scheduler.'
        }
        finally { [IO.File]::WriteAllBytes($siteConfig, $backupOverrideBytes) }

        # HOME keeps exactly what it had: no site block, 5080/54329 as postgres.
        $homeRoot = Join-Path $siteFixture 'home-installation'
        New-Item -ItemType Directory -Path $homeRoot -Force | Out-Null
        @{ classification = 'HomeCanonical'; label = 'HOME CANONICAL' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $homeRoot 'instance.json') -Encoding UTF8
        $homeEndpoints = Get-AeroLinkServiceEndpoints -InstallationRoot $homeRoot
        Assert-True (-not $homeEndpoints.Site -and $homeEndpoints.ApiPort -eq 5080 -and $homeEndpoints.PostgresPort -eq 54329 -and $homeEndpoints.DatabaseUser -eq 'postgres') `
            'An installation without a site block must stay on 5080/54329 as postgres.'

        # A declaration that would not apply, or applies wrongly, refuses rather than being ignored.
        $valid = Get-Content -LiteralPath $siteConfig -Raw
        foreach ($case in @(
            @{ Edit = { param($d) $d.classification = 'HomeCanonical' }; Pattern = 'classification'; Message = 'A site block on a HOME installation must refuse.' },
            @{ Edit = { param($d) $d.site.postgresPort = $d.site.apiPort }; Pattern = 'same port'; Message = 'A site declaration giving both services one port must refuse.' },
            @{ Edit = { param($d) $d.site.connectorDeploymentId = '' }; Pattern = 'connectorDeploymentId'; Message = 'A site declaration without an explicit deployment id must refuse.' },
            @{ Edit = { param($d) $d.site.notificationAuthorityRoot = (Join-Path $siteRoot 'authority') }; Pattern = 'outside'; Message = 'A notification authority inside the installation must refuse.' })) {
            $document = $valid | ConvertFrom-Json
            & $case.Edit $document
            $document | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $siteConfig -Encoding UTF8
            Assert-Throws { Get-AeroLinkServiceEndpoints -InstallationRoot $siteRoot } $case.Pattern $case.Message
        }
        Set-Content -LiteralPath $siteConfig -Value $valid -Encoding UTF8

        # A port somebody is listening on is refused, naming the holder.
        $holder = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $holder.Start()
        $busyPort = $holder.LocalEndpoint.Port
        try {
            Assert-Throws { Assert-AeroLinkPortAvailable -Port $busyPort -Purpose 'a test service' } "already in use by .*pid $PID" 'A listening port must be refused, naming the process that holds it.'
            Assert-Throws { Set-AeroLinkSiteDeclaration -InstallationRoot $siteRoot -ApiPort $busyPort -PostgresPort $sitePostgresPort } 'already in use' 'A site may not declare a port another process listens on.'
        }
        finally { $holder.Stop() }
        try { Assert-AeroLinkPortAvailable -Port $busyPort -Purpose 'a test service' } catch { $failures.Add("A released port must be accepted: $($_.Exception.Message)") }

        # --- 12. On a site, operator scripts fail fast instead of prompting, and Diagnostics uses the site block ---
        $env:AEROLINK_INSTALLATION_ROOT = $siteRoot
        # Backup and restore support a site (#1614 1.9) with the site's own credentials. Without them they refuse
        # before connecting, rather than sit at a hidden password prompt against the SCRAM cluster or fall back to
        # postgres over trust. The end-to-end site round trip is AeroLinkSiteBackup.Acceptance.ps1.
        Assert-Throws { & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive (Join-Path $siteFixture 'absent.zip') -DisposableQualification -PostgresPort $sitePostgresPort } `
            'protected site secret .* is not configured' 'Restore, including the isolated drill, must refuse a site without its protected credentials before connecting.'
        Assert-Throws { & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -PostgresAlreadyRunning } 'maintenance identity.*protected site secret .* is not configured' 'Backup must refuse a site without its protected credentials before connecting, naming who may run it.'
        # -InstallationRoot names a site; on any other root it is refused rather than half-applied.
        $notASite = Join-Path $siteFixture 'not-a-site'
        New-Item -ItemType Directory -Path $notASite -Force | Out-Null
        Assert-Throws { & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -InstallationRoot $notASite -PostgresAlreadyRunning } 'has no site declaration' 'Backup -InstallationRoot must refuse a root that is not a site.'
        Assert-Throws { & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive (Join-Path $siteFixture 'absent.zip') -InstallationRoot $notASite } 'has no site declaration' 'Restore -InstallationRoot must refuse a root that is not a site.'
        # Import, upgrade and the HOME repair tools stay HOME-only.
        Assert-Throws { Assert-AeroLinkNotSiteInstallation -InstallationRoot $siteRoot -Operation 'Snapshot import' } 'HOME tool and is not supported on a site' 'HOME-only operations must still refuse a site.'
        # Diagnostics never probes a site at a loopback HTTP address: its API is HTTPS-only on the public name.
        $diagnosticsOutput = Join-Path $siteFixture 'diagnostics.json'
        $shell = if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh' }
        $child = Start-Process -FilePath $shell -NoNewWindow -PassThru -RedirectStandardOutput $diagnosticsOutput -RedirectStandardError (Join-Path $siteFixture 'diagnostics.err') `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + (Join-Path $PSScriptRoot 'Get-AeroLinkDiagnostics.ps1') + '"'), '-Json')
        if (-not $child.WaitForExit(90000)) { $child.Kill(); throw 'Diagnostics did not finish within 90 seconds.' }
        $diagnostics = Get-Content -LiteralPath $diagnosticsOutput -Raw | ConvertFrom-Json
        $apiCheck = @($diagnostics.Checks | Where-Object Name -eq 'API process')[0]
        $evidenceCheck = @($diagnostics.Checks | Where-Object Name -eq 'Evidence root')[0]
        Assert-True (-not $apiCheck.Healthy -and $apiCheck.Detail -match "^Not probed: .*API port $siteApiPort .*no publicHostName") "Diagnostics must report a site without a public name as not probed, naming its declared port; got $($apiCheck | ConvertTo-Json -Compress)."
        Assert-True ($evidenceCheck.Detail -eq (Join-Path $siteRoot 'evidence')) "Diagnostics must inspect the site's own evidence root; got '$($evidenceCheck.Detail)'."
        $env:AEROLINK_INSTALLATION_ROOT = $null

        # --- 13. Folder ownership and permissions by SID: each folder grants exactly its own account ---
        $apiSid = $declared.RunAsSid; $postgresSid = $declared.PostgresRunAsSid
        $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $expected = @{
            ReadOnly = @{ Principals = @('S-1-5-18', 'S-1-5-32-544', $apiSid); Writers = @('S-1-5-18', 'S-1-5-32-544'); Reader = $apiSid }
            # Users may read and execute the binaries: initdb and pg_ctl run with Administrators deny-only (review P1-1).
            Binaries = @{ Principals = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545'); Writers = @('S-1-5-18', 'S-1-5-32-544'); Reader = 'S-1-5-32-545' }
            Cluster  = @{ Principals = @('S-1-5-32-544', $postgresSid); Writers = @('S-1-5-32-544', $postgresSid) }
            Data     = @{ Principals = @('S-1-5-32-544', $apiSid); Writers = @('S-1-5-32-544', $apiSid) }
        }
        $keyRingData = @{ Principals = @('S-1-5-18', 'S-1-5-32-544', $apiSid); Writers = @('S-1-5-18', 'S-1-5-32-544', $apiSid) }
        $sitePlan = @(Set-AeroLinkSiteFolderAcl -InstallationRoot $siteRoot)
        foreach ($entry in $sitePlan) {
            $access = Get-AeroLinkDirectoryAccessBySid -Path $entry.Path
            $want = if ($entry.Path -eq $declared.KeyRingPath) { $keyRingData } else { $expected[$entry.Kind] }
            $writers = @($access.Rules | Where-Object { $_.Allow -and $_.CanWrite } | ForEach-Object Sid | Sort-Object -Unique)
            $principals = @($access.Rules | ForEach-Object Sid | Sort-Object -Unique)
            Assert-True $access.Protected "$($entry.Path) must not inherit permissions."
            Assert-True ($access.Owner -in @('S-1-5-32-544', $me)) "$($entry.Path) must be owned by Administrators (elevated) or the installing user; it is owned by $($access.Owner)."
            Assert-True (-not @($access.Rules | Where-Object { -not $_.Allow }).Count) "$($entry.Path) carries a deny entry."
            Assert-True (($principals -join ',') -eq ((@($want.Principals) | Sort-Object -Unique) -join ',') -and ($writers -join ',') -eq ((@($want.Writers) | Sort-Object -Unique) -join ',')) `
                "$($entry.Kind) folder $($entry.Path): principals $($principals -join ','), writers $($writers -join ',')."
            if ($want.ContainsKey('Reader')) {
                $reader = @($access.Rules | Where-Object Sid -eq $want.Reader)
                Assert-True ($reader.Count -eq 1 -and -not $reader[0].CanWrite -and ($reader[0].Rights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute) -eq [Security.AccessControl.FileSystemRights]::ReadAndExecute) `
                    "$($entry.Path) must give $($want.Reader) read and execute, not write."
            }
        }
        foreach ($pair in @(@($siteRoot, 'ReadOnly'), @((Join-Path $siteRoot 'postgresql'), 'Binaries'), @((Join-Path $siteRoot 'pgdata'), 'Cluster'), @((Join-Path $siteRoot 'keys\data-protection'), 'Data'))) {
            Assert-True (@($sitePlan | Where-Object { $_.Path -eq $pair[0] -and $_.Kind -eq $pair[1] }).Count -eq 1) "$($pair[0]) must be in the permission plan as $($pair[1])."
        }
        Assert-Throws { Set-AeroLinkSiteFolderAcl -InstallationRoot $homeRoot } 'site declaration' 'Folder permissions must never be applied to an installation without a site declaration.'
    }
    finally {
        $env:AEROLINK_INSTALLATION_ROOT = $null
        # This process owns every folder, so it can always hand them back to itself before removing them.
        $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $certificateCleanup = Join-Path $siteFixture 'certificate-key'
        if (Test-Path -LiteralPath $certificateCleanup -PathType Container) {
            $certificateFile = Join-Path $certificateCleanup 'private-key'
            if (Test-Path -LiteralPath $certificateFile -PathType Leaf) {
                $certificateFileAcl = Get-Acl -LiteralPath $certificateFile
                $certificateFileAcl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                    [Security.Principal.SecurityIdentifier]::new($me), [Security.AccessControl.FileSystemRights]::FullControl,
                    [Security.AccessControl.AccessControlType]::Allow))
                $certificateAccess = [Security.AccessControl.AccessControlSections]::Access
                $certificateDacl = [Security.AccessControl.FileSecurity]::new()
                $certificateDacl.SetSecurityDescriptorSddlForm($certificateFileAcl.GetSecurityDescriptorSddlForm($certificateAccess), $certificateAccess)
                if ($PSVersionTable.PSEdition -eq 'Desktop') { [IO.File]::SetAccessControl($certificateFile, $certificateDacl) }
                else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($certificateFile), $certificateDacl) }
            }
            Set-AeroLinkDirectoryAccessBySid -Path $certificateCleanup -Grants @(@{ Sid = $me; Access = 'FullControl' })
        }
        foreach ($entry in @($sitePlan | Sort-Object { $_.Path.Length })) {
            try { Set-AeroLinkDirectoryAccessBySid -Path $entry.Path -Grants @(@{ Sid = $me; Access = 'FullControl' }) } catch { }
        }
        Remove-Item -LiteralPath $siteFixture -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $siteFixture) { $failures.Add("The site fixture could not be removed: $siteFixture") }
    }

    # --- 13. The site server installer's pure parts (#1614 1.7, 1.8, 1.10): names, plans, the service, the parsers ---
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteServer.psm1') -Force
    $server = Get-AeroLinkServerIdentity -Name Production -MachineRoot 'C:\AeroLink'
    Assert-True ($server.InstallationRoot -eq 'C:\AeroLink\AeroLink Production' -and $server.Classification -eq 'SiteProduction' -and
        $server.ServiceName -eq 'AeroLink-Production' -and $server.PostgresServiceName -eq 'AeroLink-Production-PostgreSQL') `
        "D8 names: one root, classification and service pair per installation; got $($server.InstallationRoot) $($server.ServiceName) $($server.PostgresServiceName)."
    Assert-True ((Get-AeroLinkServerIdentity -Name Development).ServiceName -eq 'AeroLink-Development') 'The Development installation has its own services.'

    # The machine roots are locked before anything is created inside them: no write for Users.
    $rootPlan = @(Get-AeroLinkServerRootPlan -MachineRoot 'C:\AeroLink' -ProgramDataRoot 'C:\ProgramData\AeroLink')
    Assert-True ($rootPlan.Count -eq 2 -and $rootPlan[0].Path -eq 'C:\AeroLink' -and $rootPlan[1].Path -eq 'C:\ProgramData\AeroLink') 'Both machine roots are in the lock plan.'
    foreach ($entry in $rootPlan) {
        $grantText = (@($entry.Grants | ForEach-Object { "$($_.Sid)=$($_.Access)" }) | Sort-Object) -join ';'
        Assert-True ($grantText -eq 'S-1-5-18=FullControl;S-1-5-32-544=FullControl;S-1-5-32-545=Read') "$($entry.Path) is writable only by Administrators and SYSTEM: $grantText"
    }

    $layout = Get-AeroLinkServerLayout -InstallationRoot 'C:\AeroLink\AeroLink Production\' -Version '1.2.3'
    Assert-True ($layout.VersionRoot -eq 'C:\AeroLink\AeroLink Production\program\versions\1.2.3' -and $layout.Current -eq 'C:\AeroLink\AeroLink Production\program\current' -and
        $layout.ApiExecutable -eq 'C:\AeroLink\AeroLink Production\program\current\app\AeroLink.Api.exe' -and $layout.TrustedCertificate -eq 'C:\AeroLink\AeroLink Production\trust\release-signing.cer') `
        'The layout: versions\<version>, a current junction, the API under current\app, the trusted certificate under trust\.'

    # --- 13a. A2 entry-point release preflight (#1614): exact identity/idempotence and no unsafe update path ---
    # Authoring gate: this protects the observable refusal boundary before backup, service stop, junction switch,
    # migration, or trust promotion. The credible regressions are the pre-fix version-only reuse and update branch;
    # existing installation tests never execute Install-AeroLinkServer.ps1 because it is elevated and mutates host
    # services. These assertions extract the production functions and inspect the production release block, with no
    # test-only production seam. The immutable pre-fix execution witness is retained at host-move\installer-a2-entrypoint-audit-20261016.
    $installerPath = Join-Path $PSScriptRoot 'Install-AeroLinkServer.ps1'
    $installerTokens = $null; $installerParseErrors = $null
    $installerAst = [System.Management.Automation.Language.Parser]::ParseFile($installerPath, [ref]$installerTokens, [ref]$installerParseErrors)
    Assert-True (@($installerParseErrors).Count -eq 0) 'Install-AeroLinkServer.ps1 must parse for its focused release contracts.'
    foreach ($helperName in @('Get-AeroLinkReleaseTreeFingerprint', 'Compare-AeroLinkRetainedRelease', 'Assert-AeroLinkInstallReleasePreflight')) {
        $helperAst = $installerAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $helperName }, $true)
        Assert-True ($null -ne $helperAst) "Installer helper $helperName must remain source-visible for its release contract fixture."
        if ($helperAst) { . ([scriptblock]::Create($helperAst.Extent.Text)) }
    }
    $installerFixture = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-installer-release-contract-' + [Guid]::NewGuid().ToString('N'))
    $fixtures.Add($installerFixture)
    $incoming = Join-Path $installerFixture 'incoming'
    $retained = Join-Path $installerFixture 'versions\1.0.0'
    $different = Join-Path $installerFixture 'versions\0.9.0'
    New-Item -ItemType Directory -Path $incoming, $retained, $different -Force | Out-Null
    'payload' | Set-Content -LiteralPath (Join-Path $incoming 'app.bin') -NoNewline
    'payload' | Set-Content -LiteralPath (Join-Path $retained 'app.bin') -NoNewline
    $payloadHash = (Get-FileHash -LiteralPath (Join-Path $incoming 'app.bin') -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{ formatVersion = 1; version = '1.0.0'; sourceSha = ('a' * 40); files = @(@{ path = 'app.bin'; sha256 = $payloadHash }) } | ConvertTo-Json -Depth 5
    $manifest | Set-Content -LiteralPath (Join-Path $incoming 'manifest.json') -NoNewline
    $manifest | Set-Content -LiteralPath (Join-Path $retained 'manifest.json') -NoNewline
    $verifiedRelease = [pscustomobject]@{ Version = '1.0.0'; SourceSha = ('a' * 40) }
    Assert-Throws { Assert-AeroLinkInstallReleasePreflight -CurrentTarget $different -VersionRoot $retained -IncomingRoot $incoming -Verified $verifiedRelease } 'A2 refuses' `
        'A2 must refuse a different current release before backup, service stop, junction switch, or migration.'
    try { Assert-AeroLinkInstallReleasePreflight -CurrentTarget $retained -VersionRoot $retained -IncomingRoot $incoming -Verified $verifiedRelease; $sameRelease = $true } catch { $sameRelease = $false }
    Assert-True $sameRelease 'An exact signed identity and byte-identical retained tree must be idempotently reusable.'
    Set-Content -LiteralPath (Join-Path $incoming 'app.bin') -Value 'changed' -NoNewline
    Assert-Throws { Assert-AeroLinkInstallReleasePreflight -VersionRoot $retained -IncomingRoot $incoming -Verified $verifiedRelease } 'cannot be reused' `
        'A same-version release with changed bytes must fail closed before reuse.'
    Set-Content -LiteralPath (Join-Path $incoming 'app.bin') -Value 'payload' -NoNewline
    'extra' | Set-Content -LiteralPath (Join-Path $retained 'extra.bin') -NoNewline
    Assert-Throws { Assert-AeroLinkInstallReleasePreflight -VersionRoot $retained -IncomingRoot $incoming -Verified $verifiedRelease } 'extra file' `
        'A retained release with extra content must fail closed before reuse.'
    Remove-Item -LiteralPath $retained -Recurse -Force
    'not-a-directory' | Set-Content -LiteralPath $retained -NoNewline
    Assert-Throws { Assert-AeroLinkInstallReleasePreflight -VersionRoot $retained -IncomingRoot $incoming -Verified $verifiedRelease } 'not a directory' `
        'A release path occupied by a file must fail closed before replacement.'
    $installerSource = [IO.File]::ReadAllText($installerPath)
    $releaseStart = $installerSource.IndexOf('$layout = Get-AeroLinkServerLayout -InstallationRoot $root')
    $pointerStart = $installerSource.IndexOf("Step 'Installation pointer'")
    $releaseBlock = if ($releaseStart -ge 0 -and $pointerStart -gt $releaseStart) { $installerSource.Substring($releaseStart, $pointerStart - $releaseStart) } else { '' }
    $offeredVerifyOffset = $releaseBlock.IndexOf('Test-AeroLinkReleasePackage -Package $Package -TrustedCertificate $TrustedCertificate')
    $trustPromotionOffset = $releaseBlock.IndexOf('Copy-Item -LiteralPath $TrustedCertificate -Destination $layout.TrustedCertificate')
    Assert-True ($offeredVerifyOffset -ge 0 -and $trustPromotionOffset -gt $offeredVerifyOffset) `
        'The offered package must verify before an installed trust certificate can be promoted.'
    Assert-True ($releaseBlock -notmatch 'Stop-Service -Name \$site\.ServiceName|Backup-AeroLink\.ps1|\(Get-Item -LiteralPath \$layout\.Current\)\.Delete\(\)') `
        'The A2 entry point must not perform the A3 backup, stop, or junction replacement transition.'
    Assert-True ($installerSource -match 'Installs or repairs one AeroLink site server installation') `
        'The installer synopsis must describe the A2 fresh-install/repair boundary.'

    # The API service, as data: the executable itself (UseWindowsService), as the virtual account, after PostgreSQL.
    $fakeSite = [pscustomobject]@{ ServiceName = 'AeroLink-Production'; PostgresServiceName = 'AeroLink-Production-PostgreSQL'; Classification = 'SiteProduction'
        RunAsAccount = 'NT SERVICE\AeroLink-Production'; RunAsSid = (Get-AeroLinkServiceSid 'AeroLink-Production') }
    $environment = [ordered]@{ 'SiteServer__Enabled' = 'true'; 'ASPNETCORE_URLS' = $null; 'ConnectionStrings__AeroLink' = 'Host=127.0.0.1;Port=55432;Database=aerolink;Username=aerolink_app'; 'ASPNETCORE_ENVIRONMENT' = 'Production' }
    $service = New-AeroLinkApiServiceDefinition -Site $fakeSite -CurrentProgram $layout.Current -Environment $environment
    Assert-True ($service.PathName -eq '"C:\AeroLink\AeroLink Production\program\current\app\AeroLink.Api.exe"' -and $service.StartName -eq 'NT SERVICE\AeroLink-Production' -and
        $service.DelayedAutoStart -and ($service.Dependencies -join ',') -eq 'AeroLink-Production-PostgreSQL' -and ($service.FailureActions -join ',') -eq 'restart/60000,restart/60000,restart/60000') `
        "The API service runs current\app\AeroLink.Api.exe as its virtual account, delayed-automatic, after its cluster, restarted on failure: $($service.PathName) $($service.StartName)"
    Assert-True (($service.Environment -join ';') -eq 'ASPNETCORE_ENVIRONMENT=Production;ConnectionStrings__AeroLink=Host=127.0.0.1;Port=55432;Database=aerolink;Username=aerolink_app;SiteServer__Enabled=true') `
        "The service environment is the composed settings, sorted, without empty entries: $($service.Environment -join ' | ')"
    Assert-Throws { New-AeroLinkApiServiceDefinition -Site $fakeSite -CurrentProgram $layout.Current -Environment ([ordered]@{ 'ConnectionStrings__AeroLink' = 'Host=x;Username=a;Password=secret' }) } 'may not carry a secret' `
        'A password in the connection string must never reach the service key.'
    Assert-Throws { New-AeroLinkApiServiceDefinition -Site $fakeSite -CurrentProgram $layout.Current -Environment ([ordered]@{ 'Identity__BootstrapSecret' = 'abc' }) } 'may not carry a secret' `
        'The bootstrap secret must never reach the service key.'
    Assert-True (Test-AeroLinkServiceSidMatchesAccount -Site $fakeSite) 'A declaration with the computed virtual-account SID is installable.'
    Assert-True (-not (Test-AeroLinkServiceSidMatchesAccount -Site ([pscustomobject]@{ ServiceName = 'AeroLink-Production'; RunAsSid = 'S-1-5-21-1-2-3-1001' }))) 'A runAsSid override is not installable.'

    # Host-check parsers.
    $goodHba = "# AeroLink`r`n# TYPE DATABASE USER ADDRESS METHOD`r`nhost    all       all   127.0.0.1/32  scram-sha-256`r`n"
    Assert-True ((ConvertFrom-AeroLinkPgHba -Text $goodHba).Passed) 'Loopback SCRAM only passes.'
    Assert-True ((ConvertFrom-AeroLinkPgHba -Text 'host all all 127.0.0.1 255.255.255.255 scram-sha-256').Passed) 'An address-and-mask loopback rule is loopback.'
    $trustHba = ConvertFrom-AeroLinkPgHba -Text ($goodHba + "host all all 127.0.0.1/32 trust`r`n")
    Assert-True (-not $trustHba.Passed -and $trustHba.WeakRules.Count -eq 1) 'A trust rule fails the check.'
    Assert-True (-not (ConvertFrom-AeroLinkPgHba -Text ($goodHba + "host all all ::1/128 scram-sha-256`r`nlocal all all scram-sha-256`r`n")).Passed) 'IPv6 loopback and local rules are outside the site''s single loopback rule.'
    Assert-True (-not (ConvertFrom-AeroLinkPgHba -Text '').Passed) 'An empty pg_hba admits nothing and is not a pass.'
    $conf = "listen_addresses = 'localhost'`r`n#listen_addresses = '*'`r`nport = 5432`r`nlisten_addresses = '127.0.0.1'`r`nport = 55432   # AeroLink`r`n"
    Assert-True ((Get-AeroLinkPostgresSetting -Text $conf -Name 'listen_addresses') -eq '127.0.0.1' -and (Get-AeroLinkPostgresSetting -Text $conf -Name 'port') -eq '55432') `
        'The last uncommented postgresql.conf assignment wins, quoted or not.'
    $policyText = "[Unicode]`r`nUnicode=yes`r`n[Privilege Rights]`r`nSeServiceLogonRight = *S-1-5-80-0,*S-1-5-20`r`nSeBatchLogonRight = *S-1-5-32-544`r`n"
    $holders = Get-AeroLinkUserRightSids -Text $policyText -Right 'SeServiceLogonRight'
    Assert-True (($holders -join ',') -eq 'S-1-5-80-0,S-1-5-20' -and (Test-AeroLinkServiceLogonRight -Holders $holders -ServiceSid $fakeSite.RunAsSid).Allowed) 'NT SERVICE\ALL SERVICES in the policy admits the virtual account.'
    Assert-True (-not (Test-AeroLinkServiceLogonRight -Holders @('S-1-5-20') -ServiceSid $fakeSite.RunAsSid -ServiceAccount 'NT SERVICE\AeroLink-Production').Allowed) 'A policy without ALL SERVICES or the account predicts error 1069.'
    Assert-True ($null -eq (Get-AeroLinkUserRightSids -Text "[Privilege Rights]`r`nSeBatchLogonRight = *S-1-5-32-544`r`n" -Right 'SeServiceLogonRight') -and
        (Test-AeroLinkServiceLogonRight -Holders $null -ServiceSid $fakeSite.RunAsSid).Allowed) 'No policy for the right means the Windows default, which admits services.'

    # --- 14. Host readiness false-ready contracts: token groups, exact DACLs, listener ancestry, backups ---
    # The old helper-boundary fail-before is retained externally at host-move\hostcheck-edf4a0fe-evidence-20261015\probe-fail-before-helper-boundary-edf4a0fe.txt.
    $hostCheckPath = Join-Path $PSScriptRoot 'Test-AeroLinkHost.ps1'
    $hostCheckTokens = $null; $hostCheckParseErrors = $null
    $hostCheckAst = [System.Management.Automation.Language.Parser]::ParseFile($hostCheckPath, [ref]$hostCheckTokens, [ref]$hostCheckParseErrors)
    Assert-True (@($hostCheckParseErrors).Count -eq 0) 'HostCheck source must parse for its focused readiness contracts.'
    foreach ($helperName in @('Test-AeroLinkHostServiceLogonRight', 'Get-AeroLinkHostAclRuleKey', 'Test-AeroLinkHostExactProtectedDacl',
                              'Test-AeroLinkHostPostgresDataRootArgument', 'Test-AeroLinkHostPostgresListenerOwnership', 'Test-AeroLinkHostVerifiedBackup')) {
        $helperAst = $hostCheckAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $helperName }, $true)
        Assert-True ($null -ne $helperAst) "HostCheck helper $helperName must remain source-visible for its contract fixture."
        if ($helperAst) { . ([scriptblock]::Create($helperAst.Extent.Text)) }
    }
    function New-HostCheckFixtureAclRule {
        param([string]$Sid, [int]$Rights, [object]$Type, [object]$Inheritance, [object]$Propagation, [bool]$Inherited)
        [pscustomobject]@{ IdentityReference = [pscustomobject]@{ Value = $Sid }; FileSystemRights = $Rights; AccessControlType = $Type; InheritanceFlags = $Inheritance; PropagationFlags = $Propagation; IsInherited = $Inherited }
    }
    function New-HostCheckFixtureAcl {
        param([bool]$Protected, [object[]]$Rules)
        $acl = [pscustomobject]@{ AreAccessRulesProtected = $Protected; Rules = $Rules }
        $acl | Add-Member -MemberType ScriptMethod -Name GetAccessRules -Value { param($IncludeExplicit, $IncludeInherited, $TargetType) return $this.Rules }
        return $acl
    }
    $tokenGroups = @('S-1-5-80-0', 'S-1-5-6', 'S-1-5-11', 'S-1-1-0')
    foreach ($denyGroup in $tokenGroups) {
        $decision = Test-AeroLinkHostServiceLogonRight -AllowHolders @('S-1-5-80-0') -DenyHolders @($denyGroup) -ServiceSid 'S-1-5-80-1' -ServiceAccount 'NT SERVICE\AeroLink-Production' -EffectiveTokenGroups $tokenGroups -EffectiveTokenGroupsKnown:$true
        Assert-True (-not $decision.Allowed -and $decision.Complete) "Applicable deny group $denyGroup must override an allowed service right."
    }
    $unknown = Test-AeroLinkHostServiceLogonRight -AllowHolders $null -DenyHolders @() -ServiceSid 'S-1-5-80-1' -EffectiveTokenGroups @() -EffectiveTokenGroupsKnown:$false
    Assert-True (-not $unknown.Allowed -and -not $unknown.Complete) 'Unknown effective token membership must remain incomplete, never an allow.'

    $none = [Security.AccessControl.InheritanceFlags]::None
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $noPropagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $deny = [Security.AccessControl.AccessControlType]::Deny
    $read = [int]([Security.AccessControl.FileSystemRights]::Read -bor [Security.AccessControl.FileSystemRights]::Synchronize)
    $modify = [int]([Security.AccessControl.FileSystemRights]::Modify -bor [Security.AccessControl.FileSystemRights]::Synchronize)
    $full = [int][Security.AccessControl.FileSystemRights]::FullControl
    $certExpected = @(
        @{ Sid = 'S-1-5-80-1'; Rights = $read; Type = $allow; Inheritance = $none; Propagation = $noPropagation; Inherited = $false }
        @{ Sid = 'S-1-5-18'; Rights = $full; Type = $allow; Inheritance = $none; Propagation = $noPropagation; Inherited = $false }
        @{ Sid = 'S-1-5-32-544'; Rights = $full; Type = $allow; Inheritance = $none; Propagation = $noPropagation; Inherited = $false }
    )
    $certRules = @($certExpected | ForEach-Object { New-HostCheckFixtureAclRule $_.Sid $_.Rights $_.Type $_.Inheritance $_.Propagation $_.Inherited })
    Assert-True ((Test-AeroLinkHostExactProtectedDacl -Acl (New-HostCheckFixtureAcl $true $certRules) -ExpectedRules $certExpected).Passed) 'The exact protected certificate-key DACL must pass.'
    Assert-True (-not (Test-AeroLinkHostExactProtectedDacl -Acl (New-HostCheckFixtureAcl $true @($certRules + (New-HostCheckFixtureAclRule 'S-1-5-6' $read $allow $none $noPropagation $false))) -ExpectedRules $certExpected).Passed) 'A wildcard service allow on the certificate key must fail.'
    Assert-True (-not (Test-AeroLinkHostExactProtectedDacl -Acl (New-HostCheckFixtureAcl $true @($certRules + (New-HostCheckFixtureAclRule 'S-1-5-11' $read $deny $none $noPropagation $false))) -ExpectedRules $certExpected).Passed) 'A broad certificate-key deny must fail.'
    $keyExpected = @(
        @{ Sid = 'S-1-5-80-1'; Rights = $modify; Type = $allow; Inheritance = $inherit; Propagation = $noPropagation; Inherited = $false }
        @{ Sid = 'S-1-5-18'; Rights = $full; Type = $allow; Inheritance = $inherit; Propagation = $noPropagation; Inherited = $false }
        @{ Sid = 'S-1-5-32-544'; Rights = $full; Type = $allow; Inheritance = $inherit; Propagation = $noPropagation; Inherited = $false }
    )
    $keyRules = @($keyExpected | ForEach-Object { New-HostCheckFixtureAclRule $_.Sid $_.Rights $_.Type $_.Inheritance $_.Propagation $_.Inherited })
    Assert-True ((Test-AeroLinkHostExactProtectedDacl -Acl (New-HostCheckFixtureAcl $true $keyRules) -ExpectedRules $keyExpected).Passed) 'The exact protected key-ring DACL must pass.'
    Assert-True (-not (Test-AeroLinkHostExactProtectedDacl -Acl (New-HostCheckFixtureAcl $true @($keyRules + (New-HostCheckFixtureAclRule 'S-1-5-80-99' $modify $allow $inherit $noPropagation $false))) -ExpectedRules $keyExpected).Passed) 'A neighboring service allow on the key ring must fail.'

    $postgresExe = 'C:\AeroLink\Production\postgresql\pgsql\bin\postgres.exe'; $dataRoot = 'C:\AeroLink\Production\pgdata'
    $goodListener = [pscustomobject]@{ ProcessId = 100; ParentProcessId = 200; ExecutablePath = $postgresExe; CommandLine = '"' + $postgresExe + '" -D "' + $dataRoot + '"' }
    $serviceProcess = [pscustomobject]@{ ProcessId = 200; ParentProcessId = 4; ExecutablePath = 'C:\AeroLink\Production\postgresql\pgsql\bin\pg_ctl.exe'; CommandLine = 'pg_ctl.exe runservice -D "' + $dataRoot + '"' }
    $goodOwnership = Test-AeroLinkHostPostgresListenerOwnership -ListenerProcess $goodListener -ServiceProcessId 200 -InstalledPostgresExecutable $postgresExe -DataRoot $dataRoot -ProcessTable @($goodListener, $serviceProcess)
    Assert-True $goodOwnership.Passed 'A postgres.exe listener with exact data root and declared service ancestry must pass.'
    Assert-True (-not (Test-AeroLinkHostPostgresListenerOwnership -ListenerProcess $goodListener -ServiceProcessId 300 -InstalledPostgresExecutable $postgresExe -DataRoot $dataRoot -ProcessTable @($goodListener, $serviceProcess)).Passed) 'Listener ancestry that does not reach the declared service must fail.'
    Assert-True (-not (Test-AeroLinkHostPostgresListenerOwnership -ListenerProcess ([pscustomobject]@{ ProcessId = 100; ParentProcessId = 200; ExecutablePath = $postgresExe; CommandLine = 'postgres.exe -D "C:\AeroLink\Production\pgdata-copy"' }) -ServiceProcessId 200 -InstalledPostgresExecutable $postgresExe -DataRoot $dataRoot -ProcessTable @($goodListener, $serviceProcess)).Passed) 'A data-root prefix must not satisfy ownership.'
    Assert-True (-not (Test-AeroLinkHostPostgresListenerOwnership -ListenerProcess ([pscustomobject]@{ ProcessId = 100; ParentProcessId = 200; ExecutablePath = 'C:\Temp\postgres.exe'; CommandLine = 'postgres.exe -D "' + $dataRoot + '"' }) -ServiceProcessId 200 -InstalledPostgresExecutable $postgresExe -DataRoot $dataRoot -ProcessTable @($goodListener, $serviceProcess)).Passed) 'A same-named postgres.exe outside the installed bin must fail.'

    $hostFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('aerolink-hostcheck-contract-' + [Guid]::NewGuid().ToString('N'))
    $hostBackupRoot = Join-Path $hostFixtureRoot 'backups'; $hostLogPath = Join-Path $hostFixtureRoot 'logs\scheduled-backup.log'; $fixtures.Add($hostFixtureRoot)
    New-Item -ItemType Directory -Path $hostBackupRoot,(Split-Path $hostLogPath) -Force | Out-Null
    $absent = Test-AeroLinkHostVerifiedBackup -BackupRoot $hostBackupRoot -LogPath $hostLogPath -WarningHours 30 -Now ([datetime]'2026-01-03T00:00:00')
    Assert-True (-not $absent.Passed) 'An absent backup must fail verification.'
    $unverifiedArchive = Join-Path $hostBackupRoot 'aerolink-unverified.zip'; Set-Content -LiteralPath $unverifiedArchive -Value 'unverified' -Encoding ASCII
    $unverified = Test-AeroLinkHostVerifiedBackup -BackupRoot $hostBackupRoot -LogPath $hostLogPath -WarningHours 30 -Now ([datetime]'2026-01-03T00:00:00')
    Assert-True (-not $unverified.Passed) 'An archive without matching sidecar and scheduled verification must fail.'
    $staleArchive = Join-Path $hostBackupRoot 'aerolink-stale.zip'; Set-Content -LiteralPath $staleArchive -Value 'stale' -Encoding ASCII
    $staleHash = (Get-FileHash -LiteralPath $staleArchive -Algorithm SHA256).Hash
    "$staleHash  aerolink-stale.zip" | Set-Content -LiteralPath "$staleArchive.sha256" -Encoding ASCII
    (Get-Item -LiteralPath $staleArchive).LastWriteTime = [datetime]'2026-01-01T00:00:00'
    "[2026-01-01T00:00:00] Scheduled backup verified successfully: $staleArchive" | Set-Content -LiteralPath $hostLogPath -Encoding UTF8
    $stale = Test-AeroLinkHostVerifiedBackup -BackupRoot $hostBackupRoot -LogPath $hostLogPath -WarningHours 30 -Now ([datetime]'2026-01-03T00:00:00')
    Assert-True ($stale.Passed -and -not $stale.Fresh) 'A verified archive older than the freshness bound must fail freshness.'
    $sanKey = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=not-the-host.example.test', $sanKey, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = New-Object Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
        $san.AddDnsName('aerolink.example.test'); $san.AddDnsName('*.apps.example.test')
        $request.CertificateExtensions.Add($san.Build())
        $sanCertificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        $dnsNames = Get-AeroLinkCertificateDnsNames -Certificate $sanCertificate
        Assert-True (($dnsNames -join ',') -eq 'aerolink.example.test,*.apps.example.test') "SAN DNS names are read from the extension's bytes: $($dnsNames -join ',')"
        Assert-True ((Test-AeroLinkHostNameCovered -HostName 'AeroLink.Example.Test' -DnsNames $dnsNames) -and (Test-AeroLinkHostNameCovered -HostName 'x.apps.example.test' -DnsNames $dnsNames) -and
            -not (Test-AeroLinkHostNameCovered -HostName 'not-the-host.example.test' -DnsNames $dnsNames) -and -not (Test-AeroLinkHostNameCovered -HostName 'a.b.apps.example.test' -DnsNames $dnsNames)) `
            'The host name is matched against the SAN only (never the CN), with one wildcard label.'
    }
    finally { $sanKey.Dispose() }
}
finally {
    $env:AEROLINK_INSTALLATION_ROOT = $previousOverride
    foreach ($fixture in $fixtures) {
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    Write-Host "AeroLink installation-identity contract FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    exit 1
}
Write-Host 'AeroLink installation-identity contract passed.' -ForegroundColor Green
exit 0
