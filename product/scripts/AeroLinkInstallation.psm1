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

function Get-AeroLinkInstallationPointerPath {
    <#
        .SYNOPSIS The pointer file a source checkout would carry, whether or not it exists.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProductRoot)
    return Join-Path (Join-Path $ProductRoot '.local') $script:AeroLinkInstallationPointerName
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
    $localRoot = Join-Path $ProductRoot '.local'
    if (-not (Test-Path -LiteralPath $localRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $localRoot -Force | Out-Null
    }
    $pointerPath = Get-AeroLinkInstallationPointerPath -ProductRoot $ProductRoot
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
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [ValidateSet('Development', 'HomeCanonical')][string]$Mode = 'Development',
        [string]$InstallationRoot
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
        if ($declared.PSObject.Properties['classification'] -and -not [string]::IsNullOrWhiteSpace([string]$declared.classification)) { $classification = [string]$declared.classification }
        if ($declared.PSObject.Properties['snapshot'] -and $declared.snapshot) { $snapshot = $declared.snapshot }
        if ($declared.PSObject.Properties['instanceId']) { $instanceId = [string]$declared.instanceId }
    }

    # A stable identifier for this installation, minted once and then never changing.
    #
    # #881's runtime identity contract asks for one alongside source, mode and classification, and it answers
    # a question a label cannot: two installations may both be labelled WORK-LAPTOP LOCAL, and a snapshot
    # restored onto a third carries the source's label with it. A plain GUID identifies without describing —
    # no machine name, no user, nothing about the network.
    if ([string]::IsNullOrWhiteSpace($instanceId) -and (Test-Path -LiteralPath $paths.InstallationRoot -PathType Container)) {
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
        [ValidateSet('HomeCanonical', 'WorkLaptopLocal', 'Undeclared')][string]$Classification,
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

Export-ModuleMember -Function @(
    'Get-AeroLinkInstallationPointerPath',
    'Get-AeroLinkInstallationRoot',
    'Get-AeroLinkInstallationPaths',
    'Set-AeroLinkInstallationPointer',
    'Get-AeroLinkInstallationIdentity',
    'Get-AeroLinkInstanceConfig',
    'Set-AeroLinkInstanceConfig'
)
