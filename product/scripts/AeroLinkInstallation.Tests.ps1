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
        $redeclared = Set-AeroLinkSiteDeclaration -InstallationRoot $siteRoot -ApiPort $siteApiPort -PostgresPort $sitePostgresPort
        Assert-True ($redeclared.ConnectorDeploymentId -eq $declared.ConnectorDeploymentId -and $redeclared.NotificationAuthorityRoot -eq [IO.Path]::GetFullPath($siteAuthority)) `
            'Re-declaring a site must keep its connector deployment id and notification authority root; a new id would orphan every enrolled connector.'
        Assert-Throws { Set-AeroLinkSiteDeclaration -InstallationRoot $siteRoot -ApiPort $siteApiPort -PostgresPort $sitePostgresPort -ConnectorDeploymentId 'aerolink-other' } 'orphan' `
            'A site declaration must refuse to replace an existing connector deployment id.'

        $runtime = Get-AeroLinkSiteRuntimeEnvironment -InstallationRoot $siteRoot
        Assert-True ($runtime['DataProtection__KeyRingPath'] -eq (Join-Path $siteRoot 'keys\data-protection') -and $runtime['DataProtection__ApplicationName'] -eq 'AeroLink' -and
            $runtime['DataProtection__ProtectKeysWith'] -eq 'DpapiLocalMachine' -and $runtime['Evidence__Root'] -eq (Join-Path $siteRoot 'evidence') -and
            $runtime['Connector__SigningKeyPath'] -eq (Join-Path $siteRoot 'keys\connector\connector-signing-key.pem') -and
            $runtime['Connector__DeploymentId'] -eq $declared.ConnectorDeploymentId -and
            $runtime['AEROLINK_NOTIFICATION_AUTHORITY_ROOT'] -eq [IO.Path]::GetFullPath($siteAuthority) -and
            $runtime['ConnectionStrings__AeroLink'] -notmatch 'Password') `
            'A site API must be given installation-owned key ring, evidence, connector and notification-authority state, and no password unless asked.'

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

        # --- 12. Operator scripts read the declared port, not 5080/54329 ---
        $env:AEROLINK_INSTALLATION_ROOT = $siteRoot
        Assert-Throws { & (Join-Path $PSScriptRoot 'Restore-AeroLink.ps1') -BackupArchive (Join-Path $siteFixture 'absent.zip') -DisposableQualification -PostgresPort $sitePostgresPort } `
            "persistent AeroLink PostgreSQL port $sitePostgresPort" 'Restore must fence disposable qualification off the site installation''s own declared PostgreSQL port.'
        # Diagnostics, unprompted, probes the declared API port and the site evidence root.
        $api = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $siteApiPort); $api.Start()
        $diagnosticsOutput = Join-Path $siteFixture 'diagnostics.json'
        $shell = if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh' }
        try {
            $child = Start-Process -FilePath $shell -NoNewWindow -PassThru -RedirectStandardOutput $diagnosticsOutput -RedirectStandardError (Join-Path $siteFixture 'diagnostics.err') `
                -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + (Join-Path $PSScriptRoot 'Get-AeroLinkDiagnostics.ps1') + '"'), '-Json')
            $deadline = (Get-Date).AddSeconds(90)
            while (-not $child.HasExited -and (Get-Date) -lt $deadline) {
                if (-not $api.Pending()) { Start-Sleep -Milliseconds 50; continue }
                $client = $api.AcceptTcpClient()
                try {
                    $stream = $client.GetStream()
                    $reader = New-Object IO.StreamReader($stream)
                    $requestLine = $reader.ReadLine()
                    while ($reader.ReadLine()) { }
                    $body = if ($requestLine -match ' /health/ready ') { '{"status":"ready","database":"connected"}' } else { '{"status":"healthy"}' }
                    $bytes = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n$body")
                    $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
                }
                finally { $client.Close() }
            }
            if (-not $child.HasExited) { $child.Kill(); throw 'Diagnostics did not finish within 90 seconds.' }
        }
        finally { $api.Stop() }
        $diagnostics = Get-Content -LiteralPath $diagnosticsOutput -Raw | ConvertFrom-Json
        $apiCheck = @($diagnostics.Checks | Where-Object Name -eq 'API process')[0]
        $evidenceCheck = @($diagnostics.Checks | Where-Object Name -eq 'Evidence root')[0]
        Assert-True ($apiCheck.Healthy -and $apiCheck.Detail -eq "http://127.0.0.1:$siteApiPort/") "Diagnostics must probe the site's declared API port by default; got $($apiCheck | ConvertTo-Json -Compress)."
        Assert-True ($evidenceCheck.Detail -eq (Join-Path $siteRoot 'evidence')) "Diagnostics must inspect the site's own evidence root; got '$($evidenceCheck.Detail)'."
        $env:AEROLINK_INSTALLATION_ROOT = $null

        # --- 13. Folder permissions are set by SID: program read-only to the run-as identity, data writable by it and Administrators only ---
        $sitePlan = @(Set-AeroLinkSiteFolderAcl -InstallationRoot $siteRoot)
        foreach ($entry in $sitePlan) {
            $access = Get-AeroLinkDirectoryAccessBySid -Path $entry.Path
            $writers = @($access.Rules | Where-Object { $_.Allow -and $_.CanWrite } | ForEach-Object Sid | Sort-Object -Unique)
            $principals = @($access.Rules | ForEach-Object Sid | Sort-Object -Unique)
            $runAs = @($access.Rules | Where-Object Sid -eq 'S-1-5-20')
            Assert-True $access.Protected "$($entry.Path) must not inherit permissions."
            Assert-True (-not @($access.Rules | Where-Object { -not $_.Allow }).Count) "$($entry.Path) carries a deny entry."
            if ($entry.Kind -eq 'Data') {
                Assert-True (($writers -join ',') -eq 'S-1-5-20,S-1-5-32-544' -and ($principals -join ',') -eq 'S-1-5-20,S-1-5-32-544') `
                    "Data folder $($entry.Path) must be writable by the run-as identity and Administrators only; principals $($principals -join ','), writers $($writers -join ',')."
            }
            else {
                Assert-True ($runAs.Count -eq 1 -and -not $runAs[0].CanWrite -and ($writers -join ',') -eq 'S-1-5-18,S-1-5-32-544' -and ($principals -join ',') -eq 'S-1-5-18,S-1-5-20,S-1-5-32-544') `
                    "Read-only folder $($entry.Path) must be readable, not writable, by the run-as identity; principals $($principals -join ','), writers $($writers -join ',')."
            }
        }
        Assert-True (@($sitePlan | Where-Object Path -eq $siteRoot).Count -eq 1 -and @($sitePlan | Where-Object Path -eq (Join-Path $siteRoot 'pgdata')).Kind -eq 'Data' -and
            @($sitePlan | Where-Object Path -eq (Join-Path $siteRoot 'keys\data-protection')).Kind -eq 'Data') 'The installation root, cluster and key ring must all be in the permission plan.'
        Assert-Throws { Set-AeroLinkSiteFolderAcl -InstallationRoot $homeRoot } 'site declaration' 'Folder permissions must never be applied to an installation without a site declaration.'
    }
    finally {
        $env:AEROLINK_INSTALLATION_ROOT = $null
        # This process owns every folder, so it can always hand them back to itself before removing them.
        $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        foreach ($entry in @($sitePlan | Sort-Object { $_.Path.Length })) {
            try { Set-AeroLinkDirectoryAccessBySid -Path $entry.Path -Grants @(@{ Sid = $me; Access = 'FullControl' }) } catch { }
        }
        Remove-Item -LiteralPath $siteFixture -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $siteFixture) { $failures.Add("The site fixture could not be removed: $siteFixture") }
    }
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
