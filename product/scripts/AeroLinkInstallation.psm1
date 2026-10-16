#Requires -Version 5.1
<#
    One authority for "where does this AeroLink installation keep its persistent state".

    Every persistent runtime path AeroLink owns on Windows — the PostgreSQL binaries, the `pgdata` cluster
    behind 127.0.0.1:54329/aerolink, backups, restore working space, dependency stamps and operator logs —
    has always been derived from the repository root as `product\.local`. That was correct while exactly one
    checkout existed. #881's dedicated HOME production source introduces a second checkout of the same
    product, and a second checkout deriving its own `product\.local` would silently `initdb` a second, empty
    canonical installation: the demo would come up, be perfectly healthy, and contain none of Sean's data.

    So the persistent installation is named separately from the source. A checkout may carry a pointer file,
    `product\.local\installation.json`, naming the installation root it belongs to; the dedicated production
    checkout carries one pointing at the HOME installation, and the development checkout carries none and
    therefore *is* the installation. The pointer lives under `product\.local`, which is git-ignored, so it is
    never repository content and can never make a canonical source posture dirty.

    Resolution order, most explicit first:

      1. AEROLINK_INSTALLATION_ROOT       - an override for disposable qualification, never set in normal use.
      2. <ProductRoot>\.local\installation.json  - the pointer a relocated source checkout carries.
      3. <ProductRoot>\.local             - the historical behaviour, and still the answer for a normal clone.

    A pointer that names a path which does not exist is a refusal, not a fallback: falling back would create
    exactly the empty second installation this module exists to prevent.

    Evidence and attachments are deliberately NOT resolved here. They already live outside the repository
    (AeroLinkEvidenceStore resolves Evidence:Root, defaulting to %LOCALAPPDATA%\AeroLink\evidence), so they
    are unaffected by a source move and keeping one resolver for them avoids a second opinion.
#>

Set-StrictMode -Version Latest

$script:AeroLinkInstallationPointerName = 'installation.json'

# The one list of what an installation may declare itself to be. Every guard that protects real data compares
# against these by equality, so a value outside the list is not a label AeroLink does not recognise - it is a
# guard that silently stops applying. Keep this and Set-AeroLinkInstanceConfig's ValidateSet in step.
#
# The two site classifications are the installations on the company site host (#1614 D4, D8). This is the one
# list of them; Test-AeroLinkSiteClassification reads it. Only a -Mode Site read accepts one: the site launcher,
# and the notification-authority scripts when the installation has a site declaration. Every other caller
# refuses them, as every launcher, import and restore refused them as unknown before they existed - which is
# what stops a HOME launcher, running Development with demonstration data, from starting against a site database.
$script:AeroLinkSiteClassifications = @('SiteProduction', 'SiteDevelopment')
$script:AeroLinkInstanceClassifications = @('HomeCanonical', 'WorkLaptopLocal', 'LocalDemo', 'Undeclared') + $script:AeroLinkSiteClassifications

function Get-AeroLinkInstallationPointerPath {
    <#
        .SYNOPSIS The pointer file a source checkout would carry, whether or not it exists.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProductRoot)
    $root = [IO.Path]::GetFullPath($ProductRoot).TrimEnd('\', '/')
    # A signed site release is laid out as <installation>\program\versions\<version>.
    # Its installation pointer is mutable installation metadata, so it belongs beside versions,
    # outside the signed release tree. Source checkouts keep the historical product\\.local path.
    $pointerRoot = if ((Test-Path -LiteralPath (Join-Path $root 'manifest.json') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $root 'app\AeroLink.Api.exe') -PathType Leaf)) {
        Split-Path (Split-Path $root -Parent) -Parent
    } else { $root }
    return Join-Path (Join-Path $pointerRoot '.local') $script:AeroLinkInstallationPointerName
}

function Get-AeroLinkInstallationRoot {
    <#
        .SYNOPSIS Resolves the persistent installation root this source checkout belongs to.
        .DESCRIPTION
            Never creates anything and never guesses. A malformed or dangling pointer fails closed, because
            the failure mode it guards against — silently initializing a second empty AeroLink — is invisible
            until somebody notices their data is missing.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProductRoot)

    $default = [IO.Path]::GetFullPath((Join-Path $ProductRoot '.local'))

    $override = $env:AEROLINK_INSTALLATION_ROOT
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        if (-not [IO.Path]::IsPathRooted($override)) {
            throw "AEROLINK_INSTALLATION_ROOT must be an absolute path; it is '$override'."
        }
        $resolvedOverride = [IO.Path]::GetFullPath($override)
        if (-not (Test-Path -LiteralPath $resolvedOverride -PathType Container)) {
            throw "AEROLINK_INSTALLATION_ROOT names a directory that does not exist: $resolvedOverride"
        }
        return $resolvedOverride
    }

    $pointerPath = Get-AeroLinkInstallationPointerPath -ProductRoot $ProductRoot
    if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) { return $default }

    try { $pointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json }
    catch { throw "The AeroLink installation pointer at $pointerPath is malformed: $($_.Exception.Message)" }

    $named = $null
    if ($pointer -and $pointer.PSObject.Properties['installationRoot']) { $named = [string]$pointer.installationRoot }
    if ([string]::IsNullOrWhiteSpace($named)) {
        throw "The AeroLink installation pointer at $pointerPath does not name an installationRoot."
    }
    if (-not [IO.Path]::IsPathRooted($named)) {
        throw "The AeroLink installation pointer at $pointerPath must name an absolute installationRoot; it names '$named'."
    }
    $resolved = [IO.Path]::GetFullPath($named)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        # Fail closed. Falling back to this checkout's own .local is precisely how a second, empty canonical
        # installation gets created without anybody noticing.
        throw "The AeroLink installation pointer at $pointerPath names an installation root that does not exist: $resolved. AeroLink will not create a second installation; correct or remove the pointer."
    }
    return $resolved
}

function Get-AeroLinkInstallationPaths {
    <#
        .SYNOPSIS Every persistent path derived from the installation root, in one object.
        .DESCRIPTION
            Callers ask this rather than composing `.local` themselves, so a relocated source checkout cannot
            reach half of the real installation and half of its own empty one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [string]$InstallationRoot
    )
    if ([string]::IsNullOrWhiteSpace($InstallationRoot)) {
        $InstallationRoot = Get-AeroLinkInstallationRoot -ProductRoot $ProductRoot
    }
    $InstallationRoot = [IO.Path]::GetFullPath($InstallationRoot)
    $postgresHome = Join-Path $InstallationRoot 'postgresql'
    return [pscustomobject]@{
        ProductRoot        = [IO.Path]::GetFullPath($ProductRoot)
        InstallationRoot   = $InstallationRoot
        IsRelocated        = -not [string]::Equals($InstallationRoot, [IO.Path]::GetFullPath((Join-Path $ProductRoot '.local')), [StringComparison]::OrdinalIgnoreCase)
        PostgresHome       = $postgresHome
        PostgresBin        = Join-Path $postgresHome 'pgsql\bin'
        PostgresCatalogue  = Join-Path $postgresHome 'pgsql\share\postgres.bki'
        PostgresArchive    = Join-Path $InstallationRoot 'postgresql-18.4.zip'
        PostgresData       = Join-Path $InstallationRoot 'pgdata'
        PostgresLog        = Join-Path $InstallationRoot 'postgresql.log'
        Logs               = Join-Path $InstallationRoot 'logs'
        Backups            = Join-Path $InstallationRoot 'backups'
        BackupVerification = Join-Path $InstallationRoot 'backup-verification'
        RestoreWork        = Join-Path $InstallationRoot 'restore-work'
        RestoreValidation  = Join-Path $InstallationRoot 'restore-validation'
        BootstrapState     = Join-Path $InstallationRoot 'bootstrap'
        DocumentConnector  = Join-Path $InstallationRoot 'document-connector'
        UpgradeState       = Join-Path $InstallationRoot 'upgrade'
        SnapshotInbox      = Join-Path $InstallationRoot 'snapshots'
        PointerPath        = Get-AeroLinkInstallationPointerPath -ProductRoot $ProductRoot
    }
}

function Set-AeroLinkInstallationPointer {
    <#
        .SYNOPSIS Records, idempotently, which installation a source checkout belongs to.
        .DESCRIPTION
            Used when the dedicated HOME production source is created, so that checkout runs against the one
            canonical HOME installation instead of initializing its own. Writing a pointer never moves,
            copies, or initializes any persistent data; it only records where the data already is.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [Parameter(Mandatory)][string]$InstallationRoot,
        [string]$Note = 'AeroLink dedicated production source: persistent state belongs to the canonical installation named here.'
    )
    if (-not [IO.Path]::IsPathRooted($InstallationRoot)) {
        throw "An installation pointer must name an absolute path; it was given '$InstallationRoot'."
    }
    $resolved = [IO.Path]::GetFullPath($InstallationRoot)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Refusing to point at an installation root that does not exist: $resolved"
    }
    $pointerPath = Get-AeroLinkInstallationPointerPath -ProductRoot $ProductRoot
    $pointerDirectory = Split-Path -Parent $pointerPath
    if (-not (Test-Path -LiteralPath $pointerDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $pointerDirectory -Force | Out-Null
    }
    [pscustomobject]@{
        installationRoot = $resolved
        note             = $Note
        recordedAtUtc    = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $pointerPath -Encoding UTF8
    return $pointerPath
}

function Get-AeroLinkInstallationIdentity {
    <#
        .SYNOPSIS A comparable, non-secret fingerprint of which installation this source would actually use.
        .DESCRIPTION
            The whole point of the source/installation split is that relocating source changes nothing about
            identity. This is what a regression test compares before and after a move: the cluster directory,
            whether that cluster already exists, the database endpoint the configuration names, and the
            evidence root. No credential, connection string password, or token is read or emitted — the
            database endpoint is reduced to host/port/name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [string]$InstallationRoot
    )
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot -InstallationRoot $InstallationRoot

    $databaseHost = $null; $databasePort = $null; $databaseName = $null
    $settingsPath = Join-Path $ProductRoot 'src\AeroLink.Api\appsettings.Development.json'
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $connection = [string]$settings.ConnectionStrings.AeroLink
            foreach ($part in ($connection -split ';')) {
                $pair = $part.Split('=', 2)
                if ($pair.Count -ne 2) { continue }
                switch ($pair[0].Trim().ToLowerInvariant()) {
                    'host'     { $databaseHost = $pair[1].Trim() }
                    'port'     { $databasePort = $pair[1].Trim() }
                    'database' { $databaseName = $pair[1].Trim() }
                }
            }
        }
        catch { }
    }

    $evidenceRoot = $null
    $evidenceModule = Join-Path $PSScriptRoot 'AeroLinkEvidenceStore.psm1'
    if (Test-Path -LiteralPath $evidenceModule -PathType Leaf) {
        Import-Module $evidenceModule -Force
        $evidenceRoot = Get-AeroLinkEvidenceRoot -ProductRoot $ProductRoot
    }

    return [pscustomobject]@{
        InstallationRoot   = $paths.InstallationRoot
        IsRelocated        = $paths.IsRelocated
        PostgresData       = $paths.PostgresData
        PostgresClusterInitialized = (Test-Path -LiteralPath (Join-Path $paths.PostgresData 'PG_VERSION') -PathType Leaf)
        PostgresInstalled  = (Test-Path -LiteralPath $paths.PostgresCatalogue -PathType Leaf)
        BackupRoot         = $paths.Backups
        BackupArchiveCount = if (Test-Path -LiteralPath $paths.Backups -PathType Container) { @(Get-ChildItem -LiteralPath $paths.Backups -File -Filter 'aerolink-*.zip').Count } else { 0 }
        EvidenceRoot       = $evidenceRoot
        DatabaseHost       = $databaseHost
        DatabasePort       = $databasePort
        DatabaseName       = $databaseName
    }
}

function ConvertTo-AeroLinkRoundTripUtc {
    <#
        .SYNOPSIS One UTC instant in round-trip form, whichever way the host deserialized it.
    #>
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) { return ([datetime]$Value).ToUniversalTime().ToString('o') }
    if ($Value -is [datetimeoffset]) { return ([datetimeoffset]$Value).UtcDateTime.ToString('o') }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text
}

function Get-AeroLinkInstanceConfig {
    <#
        .SYNOPSIS The operator-declared identity of this installation, or an honest mode-derived default.
        .DESCRIPTION
            #881 G: an operator must never mistake WORK-LAPTOP LOCAL for HOME CANONICAL. Canonical status is
            declared, in `instance.json` under the installation root — never inferred from the hostname,
            which is exactly the kind of guess that gets a change request typed into the wrong database.

            When nothing is declared the answer is deliberately modest rather than flattering: a development
            launch is LOCAL DEVELOPMENT and a production-mode launch is LOCAL PRODUCTION. Neither claims to
            be canonical, because nobody said it was.

            This READS. It used to mint and persist a missing instance id on the way past, which made a
            getter a writer - and made `REFRESH_AEROLINK_FROM_HOME.bat` Preview write to disk while telling
            the operator that nothing had been changed. Minting is now -EnsureInstanceId, which the launchers
            and explicit setup pass, and which anything advertising itself as read-only does not.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        # Site is the site-server launcher's mode: it accepts only a SiteProduction or SiteDevelopment
        # declaration, and no other mode accepts one.
        [ValidateSet('Development', 'HomeCanonical', 'Site')][string]$Mode = 'Development',
        [string]$InstallationRoot,
        # Mint and persist a stable instance id when this installation has none.
        [switch]$EnsureInstanceId
    )
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot -InstallationRoot $InstallationRoot
    $configPath = Join-Path $paths.InstallationRoot 'instance.json'

    $label = if ($Mode -eq 'HomeCanonical') { 'LOCAL PRODUCTION' } else { 'LOCAL DEVELOPMENT' }
    $classification = 'Undeclared'
    $snapshot = $null
    $declared = $null
    $instanceId = $null

    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        try { $declared = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json }
        catch { throw "The AeroLink instance declaration at $configPath is malformed: $($_.Exception.Message)" }
        if ($declared.PSObject.Properties['label'] -and -not [string]::IsNullOrWhiteSpace([string]$declared.label)) { $label = [string]$declared.label }
        if ($declared.PSObject.Properties['classification'] -and -not [string]::IsNullOrWhiteSpace([string]$declared.classification)) {
            $classification = [string]$declared.classification
            # Fail closed on a classification nobody supports.
            #
            # Every guard that protects real data compares this string for equality: the development launcher
            # refuses `HomeCanonical`, and the snapshot import refuses to overwrite one. A typo - HomeCanonica
            # - makes both comparisons false, so a file that LOOKS like it declares the canonical installation
            # silently removes the protection it appears to establish, while the badge shows the typo back to
            # the operator as if it meant something. An unreadable declaration is a refusal, not a default.
            if ($classification -notin $script:AeroLinkInstanceClassifications) {
                throw "The AeroLink instance declaration at $configPath has classification '$classification', which is not one of: $($script:AeroLinkInstanceClassifications -join ', '). Correct it with DECLARE_AEROLINK_INSTANCE.bat; nothing is assumed about this installation while it is unreadable."
            }
        }
        if ($declared.PSObject.Properties['snapshot'] -and $declared.snapshot) { $snapshot = $declared.snapshot }
        if ($declared.PSObject.Properties['instanceId']) { $instanceId = [string]$declared.instanceId }
    }

    $isSite = Test-AeroLinkSiteClassification $classification
    if ($isSite -and $Mode -ne 'Site') {
        throw "The AeroLink installation at $($paths.InstallationRoot) is declared $classification, a site server installation. Only a site-aware caller (the site launcher, Start-AeroLinkSite.ps1) handles it; this $Mode read was refused and nothing was changed."
    }
    if ($Mode -eq 'Site' -and -not $isSite) {
        throw "A Site read accepts only an installation declared SiteProduction or SiteDevelopment; the one at $($paths.InstallationRoot) is $classification. Nothing was started or changed."
    }

    # A stable identifier for this installation, minted once and then never changing.
    #
    # #881's runtime identity contract asks for one alongside source, mode and classification, and it answers
    # a question a label cannot: two installations may both be labelled WORK-LAPTOP LOCAL, and a snapshot
    # restored onto a third carries the source's label with it. A plain GUID identifies without describing —
    # no machine name, no user, nothing about the network.
    if ($EnsureInstanceId -and [string]::IsNullOrWhiteSpace($instanceId) -and (Test-Path -LiteralPath $paths.InstallationRoot -PathType Container)) {
        $instanceId = [guid]::NewGuid().ToString('D')
        $minted = [ordered]@{}
        if ($declared) { foreach ($property in $declared.PSObject.Properties) { $minted[$property.Name] = $property.Value } }
        $minted['instanceId'] = $instanceId
        $minted['updatedAtUtc'] = (Get-Date).ToUniversalTime().ToString('o')
        [pscustomobject]$minted | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configPath -Encoding UTF8
    }

    return [pscustomobject]@{
        ConfigPath           = $configPath
        Declared             = ($null -ne $declared)
        InstanceId           = $instanceId
        Label                = $label
        Classification       = $classification
        SnapshotSourceLabel  = if ($snapshot -and $snapshot.PSObject.Properties['sourceLabel']) { [string]$snapshot.sourceLabel } else { $null }
        SnapshotSourceSha    = if ($snapshot -and $snapshot.PSObject.Properties['sourceSha']) { [string]$snapshot.sourceSha } else { $null }
        # PowerShell 7 deserializes an ISO 8601 string in JSON as a DateTime while Windows PowerShell leaves
        # it as text, so a plain cast produces a locale-formatted string on one host and the original on the
        # other. Normalize to a round-trip UTC instant, which is what every consumer of this actually wants.
        SnapshotCreatedAtUtc = if ($snapshot -and $snapshot.PSObject.Properties['createdAtUtc']) { ConvertTo-AeroLinkRoundTripUtc $snapshot.createdAtUtc } else { $null }
        SnapshotActivatedAtUtc = if ($snapshot -and $snapshot.PSObject.Properties['activatedAtUtc']) { ConvertTo-AeroLinkRoundTripUtc $snapshot.activatedAtUtc } else { $null }
    }
}

function Set-AeroLinkInstanceConfig {
    <#
        .SYNOPSIS Declares (or updates) this installation's operator-visible identity. Non-secret only.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [string]$Label,
        # A literal, because ValidateSet cannot read a variable; keep it equal to $script:AeroLinkInstanceClassifications.
        [ValidateSet('HomeCanonical', 'WorkLaptopLocal', 'LocalDemo', 'Undeclared', 'SiteProduction', 'SiteDevelopment')][string]$Classification,
        [hashtable]$Snapshot,
        [string]$InstallationRoot
    )
    $paths = Get-AeroLinkInstallationPaths -ProductRoot $ProductRoot -InstallationRoot $InstallationRoot
    if (-not (Test-Path -LiteralPath $paths.InstallationRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $paths.InstallationRoot -Force | Out-Null
    }
    $configPath = Join-Path $paths.InstallationRoot 'instance.json'
    $existing = [ordered]@{}
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        try {
            $current = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            foreach ($property in $current.PSObject.Properties) { $existing[$property.Name] = $property.Value }
        }
        catch { }
    }
    if ($PSBoundParameters.ContainsKey('Label')) { $existing['label'] = $Label }
    if ($PSBoundParameters.ContainsKey('Classification')) { $existing['classification'] = $Classification }
    if ($PSBoundParameters.ContainsKey('Snapshot')) { $existing['snapshot'] = $Snapshot }
    $existing['updatedAtUtc'] = (Get-Date).ToUniversalTime().ToString('o')
    [pscustomobject]$existing | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configPath -Encoding UTF8
    return $configPath
}

# The ports every HOME, laptop and demo installation has always used. A site server installation (#1614) declares
# its own in instance.json instead, because two of them share one host (D8) with other applications (D3).
$script:AeroLinkDefaultApiPort = 5080
$script:AeroLinkDefaultPostgresPort = 54329

function Test-AeroLinkSiteClassification {
    <# .SYNOPSIS Whether a classification is one of the site server classifications ($script:AeroLinkSiteClassifications, at the top of this module). #>
    param([AllowNull()][AllowEmptyString()][string]$Classification)
    return ($Classification -in $script:AeroLinkSiteClassifications)
}

function Get-AeroLinkServiceSid {
    <#
        .SYNOPSIS The SID of the virtual account NT SERVICE\<ServiceName>: S-1-5-80 plus the SHA-1 of the upper-cased name.
        .DESCRIPTION
            The same derivation Windows uses (sc showsid), so a folder can be granted to an installation's service
            account before that service exists; the grant takes effect when the service runs as the account.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,256}$')][string]$ServiceName)
    $sha = [Security.Cryptography.SHA1]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::Unicode.GetBytes($ServiceName.ToUpperInvariant())) }
    finally { $sha.Dispose() }
    $parts = for ($i = 0; $i -lt 20; $i += 4) { [BitConverter]::ToUInt32($hash, $i) }
    return 'S-1-5-80-' + ($parts -join '-')
}

function Assert-AeroLinkSiteServiceSid {
    <#
        .SYNOPSIS Refuses a production site declaration whose ACL reader is not its computed virtual service SID.
        .DESCRIPTION
            Site declarations accept SID overrides for disposable, non-elevated qualification. An operator entry
            point that writes an installation-owned ACL must establish the real virtual-account binding first;
            otherwise a value such as the Users SID would become a durable reader. Low-level ACL helpers remain
            SID-oriented so disposable fixtures can qualify them without touching a production installation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Site,
        [ValidateSet('Api', 'Postgres')][string]$Role = 'Api'
    )
    $serviceName = if ($Role -eq 'Api') { [string]$Site.ServiceName } else { [string]$Site.PostgresServiceName }
    $declaredSid = if ($Role -eq 'Api') { [string]$Site.RunAsSid } else { [string]$Site.PostgresRunAsSid }
    if ([string]::IsNullOrWhiteSpace($serviceName) -or [string]::IsNullOrWhiteSpace($declaredSid)) {
        throw "The site declaration has no $Role virtual service binding to authorize an installation-owned ACL."
    }
    $computedSid = Get-AeroLinkServiceSid -ServiceName $serviceName
    if ($declaredSid -ne $computedSid) {
        throw "The site's $Role SID '$declaredSid' is an override, not the computed NT SERVICE\$serviceName SID '$computedSid'; refusing an installation-owned ACL write."
    }
    return $true
}

function Assert-AeroLinkNotSiteInstallation {
    <#
        .SYNOPSIS Refuses, without prompting, a HOME tool run against a site server installation.
        .DESCRIPTION
            A site cluster requires SCRAM passwords; these HOME tools (snapshot import, the clone-validated upgrade,
            showcase pruning, the FMS history repair) connect as postgres with none, so psql would sit at a hidden
            password prompt. Backup and restore are site-aware (#1614 1.9) and do not call this.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot, [Parameter(Mandatory)][string]$Operation)
    if (Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot) {
        throw "$Operation is a HOME tool and is not supported on a site server installation: it connects to PostgreSQL without the site's credentials. Nothing was changed."
    }
}

function Get-AeroLinkSiteDeclaration {
    <#
        .SYNOPSIS The site server declaration in this installation's instance.json, or $null when it has none.
        .DESCRIPTION
            #1614 1.3-1.5. A site server installation names its own ports, run-as identity, database role and
            connector deployment id in the `site` block of instance.json, and owns every piece of runtime state
            under its installation root. HOME, laptop and demo installations have no `site` block and resolve
            exactly as before.

            Fail closed: a `site` block on an installation that is not classified SiteProduction or
            SiteDevelopment, or one with an unusable value, is a refusal. A declaration that silently does not
            apply would move nothing while appearing to move everything.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot)
    $root = [IO.Path]::GetFullPath($InstallationRoot)
    $configPath = Join-Path $root 'instance.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { return $null }
    try { $declared = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json }
    catch { throw "The AeroLink instance declaration at $configPath is malformed: $($_.Exception.Message)" }
    if (-not $declared -or -not $declared.PSObject.Properties['site'] -or $null -eq $declared.site) { return $null }
    $site = $declared.site
    $classification = if ($declared.PSObject.Properties['classification']) { [string]$declared.classification } else { '' }
    if (-not (Test-AeroLinkSiteClassification $classification)) {
        throw "The AeroLink instance declaration at $configPath has a site block but classification '$classification'. Only $($script:AeroLinkSiteClassifications -join ' and ') installations may declare one."
    }
    function Read-SitePort([string]$Name) {
        $raw = if ($site.PSObject.Properties[$Name]) { [string]$site.$Name } else { '' }
        $value = 0
        if (-not [int]::TryParse($raw, [ref]$value) -or $value -lt 1024 -or $value -gt 65535) {
            throw "The site declaration at $configPath must set $Name to a port from 1024 to 65535; it is '$raw'."
        }
        return $value
    }
    $schema = if ($site.PSObject.Properties['schemaVersion']) { [string]$site.schemaVersion } else { '' }
    if ($schema -ne '1') { throw "The site declaration at $configPath has schemaVersion '$schema'; this AeroLink reads version 1." }
    $apiPort = Read-SitePort 'apiPort'
    $postgresPort = Read-SitePort 'postgresPort'
    if ($apiPort -eq $postgresPort) { throw "The site declaration at $configPath gives the API and PostgreSQL the same port $apiPort." }
    # D2 (revised 15 Oct): each installation runs as its own virtual service accounts, NT SERVICE\<service name>,
    # one for the API and one for PostgreSQL. The SIDs are computed from the names, so they need no lookup and
    # cannot drift from them; an explicit SID is accepted only as an override for disposable qualification.
    function Read-SiteServiceName([string]$Name) {
        $raw = if ($site.PSObject.Properties[$Name]) { [string]$site.$Name } else { '' }
        if ($raw -notmatch '^[A-Za-z0-9_-]{3,80}$') { throw "The site declaration at $configPath must set $Name to a Windows service name of 3-80 letters, digits, '_' or '-'; it is '$raw'." }
        return $raw
    }
    function Read-SiteSid([string]$Name, [string]$ServiceName) {
        if (-not $site.PSObject.Properties[$Name] -or [string]::IsNullOrWhiteSpace([string]$site.$Name)) { return (Get-AeroLinkServiceSid -ServiceName $ServiceName) }
        $raw = [string]$site.$Name
        try { return (New-Object Security.Principal.SecurityIdentifier($raw)).Value }
        catch { throw "The site declaration at $configPath must give $Name as a SID; it is '$raw'." }
    }
    $serviceName = Read-SiteServiceName 'serviceName'
    $postgresServiceName = Read-SiteServiceName 'postgresServiceName'
    if ($serviceName -eq $postgresServiceName) { throw "The site declaration at $configPath gives the API and PostgreSQL the same service account '$serviceName'." }
    $runAsSid = Read-SiteSid 'runAsSid' $serviceName
    $postgresRunAsSid = Read-SiteSid 'postgresRunAsSid' $postgresServiceName
    $publicHostName = $null
    if ($site.PSObject.Properties['publicHostName'] -and -not [string]::IsNullOrWhiteSpace([string]$site.publicHostName)) {
        $publicHostName = ([string]$site.publicHostName).Trim().ToLowerInvariant()
        # D3: the full host name, never the short one, which neither resolves from other sites nor is on the certificate.
        if ($publicHostName -notmatch '^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$') {
            throw "The site declaration at $configPath must give publicHostName as a fully qualified host name; it is '$publicHostName'."
        }
    }
    $role =if ($site.PSObject.Properties['databaseRole']) { [string]$site.databaseRole } else { '' }
    if ($role -cnotmatch '^[a-z_][a-z0-9_]{2,62}$' -or $role -eq 'postgres') {
        throw "The site declaration at $configPath must name a lower-case, non-superuser databaseRole; it is '$role'."
    }
    $deploymentId = if ($site.PSObject.Properties['connectorDeploymentId']) { [string]$site.connectorDeploymentId } else { '' }
    if ($deploymentId -notmatch '^[A-Za-z0-9._-]{3,100}$') {
        throw "The site declaration at $configPath must set an explicit connectorDeploymentId of 3-100 letters, digits, '.', '_' or '-'; it is '$deploymentId'."
    }
    $keys = Join-Path $root 'keys'
    # The notification authority must stay OUTSIDE the installation (AeroLinkNotificationAuthority refuses one
    # inside it), so a restore or snapshot can never carry it. It is per installation all the same: one machine
    # folder per installation root, named by the same root hash the protected-config store uses.
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $rootHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.TrimEnd('\').ToLowerInvariant())))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $machineState = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'AeroLink'
    $authorityRoot = Join-Path (Join-Path $machineState 'notification-authority') $rootHash
    if ($site.PSObject.Properties['notificationAuthorityRoot'] -and -not [string]::IsNullOrWhiteSpace([string]$site.notificationAuthorityRoot)) {
        $authorityRoot = [string]$site.notificationAuthorityRoot
        if (-not [IO.Path]::IsPathRooted($authorityRoot)) { throw "The site declaration at $configPath must name an absolute notificationAuthorityRoot; it is '$authorityRoot'." }
        $authorityRoot = [IO.Path]::GetFullPath($authorityRoot)
    }
    if (($authorityRoot.TrimEnd('\') + '\').StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "The site declaration at $configPath places the notification authority inside the installation ($authorityRoot); it must stay outside it, beyond the reach of a restore."
    }
    return [pscustomobject]@{
        ConfigPath                = $configPath
        Classification            = $classification
        InstallationRoot          = $root
        ApiPort                   = $apiPort
        PostgresPort              = $postgresPort
        ServiceName               = $serviceName
        RunAsAccount              = "NT SERVICE\$serviceName"
        RunAsSid                  = $runAsSid
        PostgresServiceName       = $postgresServiceName
        PostgresRunAsAccount      = "NT SERVICE\$postgresServiceName"
        PostgresRunAsSid          = $postgresRunAsSid
        PublicHostName            = $publicHostName
        DatabaseRole              = $role
        ConnectorDeploymentId     = $deploymentId
        # Installation-owned state (#1614 1.5). Derived from the root rather than stored, so a declaration can
        # never point one installation's runtime at another installation's keys or evidence.
        ProgramRoot               = Join-Path $root 'program'
        EvidenceRoot              = Join-Path $root 'evidence'
        KeyRingPath               = Join-Path $keys 'data-protection'
        ConnectorKeyDirectory     = Join-Path $keys 'connector'
        ConnectorSigningKeyPath   = Join-Path (Join-Path $keys 'connector') 'connector-signing-key.pem'
        NotificationAuthorityRoot = $authorityRoot
    }
}

function Get-AeroLinkServiceEndpoints {
    <#
        .SYNOPSIS The local ports this installation's services use.
        .DESCRIPTION
            HOME, laptop and demo installations use 5080 for the API and 54329 for PostgreSQL, unchanged. A site
            server installation (#1614) uses the ports its instance.json declares.

            A DISPOSABLE qualification installation - AEROLINK_INSTALLATION_ROOT set, which normal use never does -
            may move them with AEROLINK_QUALIFICATION_API_PORT and AEROLINK_QUALIFICATION_POSTGRES_PORT, so the real
            launchers and transition callers can be qualified on a machine whose HOME services are running. An
            override that names a persistent port is refused: a disposable installation must never be able to
            reach the canonical API or database.

            DeclaredApiPort and DeclaredPostgresPort are the installation's own ports with any qualification
            override ignored. Guards that protect the persistent database compare against those.
    #>
    [CmdletBinding()]
    param(
        # Defaults to the installation this module's own checkout resolves to.
        [string]$InstallationRoot
    )
    if ([string]::IsNullOrWhiteSpace($InstallationRoot)) {
        $InstallationRoot = Get-AeroLinkInstallationRoot -ProductRoot (Split-Path -Parent $PSScriptRoot)
    }
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    $declaredApi = if ($site) { $site.ApiPort } else { $script:AeroLinkDefaultApiPort }
    $declaredPostgres = if ($site) { $site.PostgresPort } else { $script:AeroLinkDefaultPostgresPort }
    $databaseUser = if ($site) { $site.DatabaseRole } else { 'postgres' }
    $api = $declaredApi
    $postgres = $declaredPostgres
    $qualification = $false
    if ($env:AEROLINK_INSTALLATION_ROOT -and ($env:AEROLINK_QUALIFICATION_API_PORT -or $env:AEROLINK_QUALIFICATION_POSTGRES_PORT)) {
        $persistent = @($script:AeroLinkDefaultApiPort, $script:AeroLinkDefaultPostgresPort, $declaredApi, $declaredPostgres) | Select-Object -Unique
        foreach ($pair in @(@('AEROLINK_QUALIFICATION_API_PORT', 'api'), @('AEROLINK_QUALIFICATION_POSTGRES_PORT', 'postgres'))) {
            $raw = [Environment]::GetEnvironmentVariable($pair[0], 'Process')
            if (-not $raw) { throw "A disposable qualification installation must set both AEROLINK_QUALIFICATION_API_PORT and AEROLINK_QUALIFICATION_POSTGRES_PORT." }
            $value = 0
            if (-not [int]::TryParse($raw, [ref]$value) -or $value -lt 1024 -or $value -gt 65535 -or $value -in $persistent) {
                throw "$($pair[0]) must be a port from 1024 to 65535 other than the persistent $($persistent -join ' and '); it is '$raw'."
            }
            if ($pair[1] -eq 'api') { $api = $value } else { $postgres = $value }
        }
        $qualification = $true
    }
    return [pscustomobject]@{
        ApiPort              = $api
        PostgresPort         = $postgres
        DeclaredApiPort      = $declaredApi
        DeclaredPostgresPort = $declaredPostgres
        ApiBaseUri           = "http://127.0.0.1:$api"
        DatabaseUser         = $databaseUser
        # Never a password. A site installation's password lives in the protected-config store and is added
        # by the process that starts the API (Get-AeroLinkSiteRuntimeEnvironment).
        ConnectionString     = "Host=127.0.0.1;Port=$postgres;Database=aerolink;Username=$databaseUser"
        Qualification        = $qualification
        Site                 = ($null -ne $site)
    }
}

function Get-AeroLinkPortListenerProcessIds {
    <#
        .SYNOPSIS The ids of the processes listening on a local TCP port, on any address. Empty when it is free.
        .DESCRIPTION Read from the TCP table, never from localised netstat or netsh text.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port)
    return @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
}

function Assert-AeroLinkPortAvailable {
    <#
        .SYNOPSIS Refuses a port another process is already listening on (#1614 1.3).
        .DESCRIPTION
            On a shared host a port in use belongs to somebody: one of the other applications, or the other
            AeroLink installation. Starting anyway either fails half-way or, worse, finds the other service and
            talks to it. The caller learns which process holds the port.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port,
        [Parameter(Mandatory)][string]$Purpose
    )
    $owners = @(Get-AeroLinkPortListenerProcessIds -Port $Port)
    if ($owners.Count -gt 0) {
        $described = foreach ($owner in $owners) {
            $process = Get-Process -Id $owner -ErrorAction SilentlyContinue
            if ($process) { "$($process.ProcessName) (pid $owner)" } else { "pid $owner" }
        }
        throw "Port $Port, declared for $Purpose, is already in use by $($described -join ', '). Choose a free port for this installation or stop the process that holds it; AeroLink will not share it."
    }
}

function Get-AeroLinkProgramLayout {
    <#
        .SYNOPSIS Where this program's settings, source identity, schema id and API executable come from.
        .DESCRIPTION
            Two layouts run these scripts:
              Package  a release package (#1614 1.6): <program>\manifest.json, app\ (the published API, its
                       appsettings.json and release-identity.json) and scripts\. No source tree, no git, no
                       appsettings.Development.json. This is what a site installation runs.
              Source   a repository checkout: src\, git and the migrations folder. HOME, the laptop and qualification,
                       exactly as before.
            ProductRoot is the folder above the scripts folder in both.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProductRoot)
    $root = [IO.Path]::GetFullPath($ProductRoot)
    $manifestPath = Join-Path $root 'manifest.json'
    $app = Join-Path $root 'app'
    if ((Test-Path -LiteralPath $manifestPath -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $app 'AeroLink.Api.exe') -PathType Leaf)) {
        try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
        catch { throw "The release manifest at $manifestPath is malformed: $($_.Exception.Message)" }
        $sha = if ($manifest.PSObject.Properties['sourceSha']) { [string]$manifest.sourceSha } else { '' }
        $identityPath = Join-Path $app 'release-identity.json'
        if (-not $sha -and (Test-Path -LiteralPath $identityPath -PathType Leaf)) {
            $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
            if ($identity.PSObject.Properties['Runtime'] -and $identity.Runtime.PSObject.Properties['SourceSha']) { $sha = [string]$identity.Runtime.SourceSha }
        }
        $migration = if ($manifest.PSObject.Properties['latestMigration']) { [string]$manifest.latestMigration } else { '' }
        if (-not $sha -or -not $migration) { throw "The release manifest at $manifestPath names no source SHA or latest migration; refusing to record an archive whose application identity is unknown." }
        return [pscustomobject]@{
            Kind          = 'Package'
            SettingsFiles = @(@('appsettings.json', 'appsettings.Production.json') | ForEach-Object { Join-Path $app $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
            SourceSha     = $sha
            SchemaVersion = $migration
            ApiExecutable = Join-Path $app 'AeroLink.Api.exe'
        }
    }
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $root '..'))
    $sha = try { (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim() } catch { 'unknown' }
    $schema = (Get-ChildItem -LiteralPath (Join-Path $root 'src\AeroLink.Infrastructure\Persistence\Migrations') -Filter '*.cs' -File | Where-Object Name -notmatch 'Designer|Snapshot' | Sort-Object Name | Select-Object -Last 1).BaseName
    return [pscustomobject]@{
        Kind          = 'Source'
        SettingsFiles = @((Join-Path $root 'src\AeroLink.Api\appsettings.json'), (Join-Path $root 'src\AeroLink.Api\appsettings.Development.json'))
        SourceSha     = $sha
        SchemaVersion = $schema
        ApiExecutable = Join-Path $root 'src\AeroLink.Api\bin\Release\net10.0\AeroLink.Api.exe'
    }
}

function Invoke-AeroLinkWithPostgresAccess {
    <#
        .SYNOPSIS Runs a script block with PGPASSWORD set to one login's password, then restores the previous value.
        .DESCRIPTION
            A site installation's PostgreSQL requires SCRAM passwords (#1614 1.4). Every PostgreSQL tool started inside
            the block inherits PGPASSWORD, never a command-line argument; the callers pass -w, which makes a missing
            or wrong password a refusal instead of a hidden prompt. With no access object (HOME, trust over loopback)
            the block runs exactly as before. The access object comes from Get-AeroLinkSitePostgresAccess.

            The block runs in a child scope: return values from it, do not assign variables inside it.
    #>
    param([AllowNull()]$Access, [Parameter(Mandatory)][scriptblock]$Action)
    if ($null -eq $Access) { return (& $Action) }
    $previous = $env:PGPASSWORD
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Access.Password)
    try {
        $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        return (& $Action)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        if ($null -eq $previous) { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue } else { $env:PGPASSWORD = $previous }
    }
}

Export-ModuleMember -Function @(
    'Get-AeroLinkProgramLayout',
    'Invoke-AeroLinkWithPostgresAccess',
    'Get-AeroLinkInstallationPointerPath',
    'Get-AeroLinkInstallationRoot',
    'Get-AeroLinkInstallationPaths',
    'Set-AeroLinkInstallationPointer',
    'Get-AeroLinkInstallationIdentity',
    'Get-AeroLinkInstanceConfig',
    'Get-AeroLinkServiceEndpoints',
    'Get-AeroLinkSiteDeclaration',
    'Test-AeroLinkSiteClassification',
    'Get-AeroLinkServiceSid',
    'Assert-AeroLinkSiteServiceSid',
    'Assert-AeroLinkNotSiteInstallation',
    'Get-AeroLinkPortListenerProcessIds',
    'Assert-AeroLinkPortAvailable',
    'Set-AeroLinkInstanceConfig'
)
