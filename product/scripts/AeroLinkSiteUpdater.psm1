#Requires -Version 5.1
<#
    A3 site updater (#1614).

    This module owns the package-to-current state machine. It deliberately delegates package verification,
    backup/restore/clone validation, maintenance, transition leasing and runtime identity to the existing
    authorities. A site update keeps one transition lease from the first package check through the final public
    HTTPS proof; the journal contains recovery facts only and never credentials.
#>

Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkReleasePackage.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteInstallation.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkSiteServer.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransition.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkUpgrade.psm1')

function Get-AeroLinkSiteUpdateJournalPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot)
    return Join-Path ([IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\', '/')) 'upgrade\update-state.json'
}

function Write-AeroLinkSiteUpdateJournal {
    <# Writes only non-secret recovery facts, atomically enough for an interrupted operator action. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [Parameter(Mandatory)][string]$Stage,
        [string]$Version,
        [string]$SourceSha,
        [string]$PriorTarget,
        [string]$BackupArchive,
        [string]$BackupSha256,
        [bool]$DatabaseModified = $false,
        [string]$Detail,
        [string]$Recovery
    )
    $path = Get-AeroLinkSiteUpdateJournalPath -InstallationRoot $InstallationRoot
    $directory = Split-Path -Parent $path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $existing = [ordered]@{}
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        try {
            $old = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            foreach ($property in $old.PSObject.Properties) { $existing[$property.Name] = $property.Value }
        } catch { throw "The site updater journal at $path is malformed; recover it before starting another update. $($_.Exception.Message)" }
    }
    $existing['installationRoot'] = [IO.Path]::GetFullPath($InstallationRoot)
    $existing['stage'] = $Stage
    if ($Version) { $existing['version'] = $Version }
    if ($SourceSha) { $existing['sourceSha'] = $SourceSha }
    if ($PriorTarget) { $existing['priorTarget'] = [IO.Path]::GetFullPath($PriorTarget) }
    if ($BackupArchive) { $existing['backupArchive'] = [IO.Path]::GetFullPath($BackupArchive) }
    if ($BackupSha256) { $existing['backupSha256'] = $BackupSha256.ToLowerInvariant() }
    $existing['databaseModified'] = [bool]$DatabaseModified
    if ($Detail) { $existing['detail'] = $Detail }
    if ($Recovery) { $existing['recovery'] = $Recovery }
    if (-not $existing.Contains('startedAtUtc')) { $existing['startedAtUtc'] = [DateTime]::UtcNow.ToString('o') }
    $existing['updatedAtUtc'] = [DateTime]::UtcNow.ToString('o')
    $temporary = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, ($existing | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination $path -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    return $path
}

function Get-AeroLinkSiteCurrentTarget {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Layout)
    if (-not (Test-Path -LiteralPath $Layout.Current)) {
        throw "The current junction '$($Layout.Current)' is missing; refusing to update without an exact prior target for recovery."
    }
    $item = Get-Item -LiteralPath $Layout.Current -Force -ErrorAction Stop
    if (-not $item.PSObject.Properties['Target'] -or [string]::IsNullOrWhiteSpace([string]$item.Target)) {
        throw "The current path '$($Layout.Current)' is not a readable junction; refusing to update it."
    }
    $target = [IO.Path]::GetFullPath([string]$item.Target).TrimEnd('\', '/')
    $versions = [IO.Path]::GetFullPath($Layout.Versions).TrimEnd('\', '/') + '\'
    if (-not $target.StartsWith($versions, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $target -PathType Container)) {
        throw "The current junction '$($Layout.Current)' points outside the installation's version directory or at a missing release: $target"
    }
    return $target
}

function Assert-AeroLinkSiteVersionRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Layout, [Parameter(Mandatory)][string]$VersionRoot, [Parameter(Mandatory)]$Verified)
    $root = [IO.Path]::GetFullPath($VersionRoot).TrimEnd('\', '/')
    $versions = [IO.Path]::GetFullPath($Layout.Versions).TrimEnd('\', '/') + '\'
    if (-not $root.StartsWith($versions, [StringComparison]::OrdinalIgnoreCase)) { throw "The release root '$root' is outside the installation's versions directory." }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "The verified release root '$root' does not exist." }
    $program = Get-AeroLinkProgramLayout -ProductRoot $root
    if ($program.SourceSha -cne [string]$Verified.SourceSha) { throw "The extracted release source identity $($program.SourceSha) does not match the verified package $($Verified.SourceSha)." }
    return $program
}

function Set-AeroLinkSiteCurrentTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Layout,
        [Parameter(Mandatory)][string]$VersionRoot,
        [string]$PriorTarget,
        [scriptblock]$Switcher
    )
    $target = [IO.Path]::GetFullPath($VersionRoot).TrimEnd('\', '/')
    $versions = [IO.Path]::GetFullPath($Layout.Versions).TrimEnd('\', '/') + '\'
    if (-not $target.StartsWith($versions, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $target -PathType Container)) {
        throw "Refusing to point current at '$target': it is not an existing release under $($Layout.Versions)."
    }
    if ($Switcher) { & $Switcher $Layout.Current $target; return $target }
    try {
        if (Test-Path -LiteralPath $Layout.Current) {
            $current = Get-Item -LiteralPath $Layout.Current -Force
            if (-not $current.PSObject.Properties['Target'] -or [string]::IsNullOrWhiteSpace([string]$current.Target)) { throw "The current path '$($Layout.Current)' is not a junction." }
            $current.Delete()
        }
        New-Item -ItemType Junction -Path $Layout.Current -Target $target | Out-Null
        return $target
    } catch {
        if ($PriorTarget -and -not (Test-Path -LiteralPath $Layout.Current) -and (Test-Path -LiteralPath $PriorTarget -PathType Container)) {
            try { New-Item -ItemType Junction -Path $Layout.Current -Target $PriorTarget | Out-Null } catch { }
        }
        throw
    }
}

function Test-AeroLinkSiteUpdateHealth {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUri,
        [Parameter(Mandatory)][string]$ExpectedSourceSha,
        [Parameter(Mandatory)][string]$ExpectedInstanceId,
        [Parameter(Mandatory)][string]$ExpectedClassification,
        [scriptblock]$ReadyProbe,
        [scriptblock]$IdentityProbe
    )
    $ready = if ($ReadyProbe) { [bool](& $ReadyProbe $BaseUri) } else { Test-AeroLinkReadyEndpoint -BaseUri $BaseUri }
    if (-not $ready) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS readiness failed at $BaseUri." } }
    $identity = if ($IdentityProbe) { & $IdentityProbe $BaseUri } else { Get-AeroLinkRuntimeIdentity -BaseUri $BaseUri }
    if (-not $identity) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS identity was unavailable at $BaseUri." } }
    $source = if ($identity.PSObject.Properties['sourceIdentity']) { [string]$identity.sourceIdentity } elseif ($identity.PSObject.Properties['sourceSha']) { [string]$identity.sourceSha } else { '' }
    if ($source -cne $ExpectedSourceSha) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS identity source '$source' does not match verified package '$ExpectedSourceSha'." } }
    $instance = if ($identity.PSObject.Properties['instance']) { $identity.instance } else { $null }
    $actualId = if ($instance -and $instance.PSObject.Properties['id']) { [string]$instance.id } else { '' }
    $actualClass = if ($instance -and $instance.PSObject.Properties['classification']) { [string]$instance.classification } else { '' }
    if ($actualId -ine $ExpectedInstanceId -or $actualClass -ine $ExpectedClassification) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS identity does not match the declared installation ($actualClass/$actualId)." } }
    return [pscustomobject]@{ Passed = $true; Detail = "Public HTTPS readiness and identity match $ExpectedSourceSha for $ExpectedClassification/$ExpectedInstanceId." }
}

function Invoke-AeroLinkSiteUpdate {
    <# The production entry point. All destructive callbacks are injectable only to exercise contracts. #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [Parameter(Mandatory)][string]$InstallationRoot,
        [Parameter(Mandatory)][string]$Package,
        [Parameter(Mandatory)][string]$TrustedCertificate,
        [string]$ProtectedConfigRoot,
        [switch]$AllowUnverifiedDevelopment,
        [scriptblock]$VerifyPackage,
        [scriptblock]$CloneRunner,
        [scriptblock]$ServiceStopper,
        [scriptblock]$ServiceStarter,
        [scriptblock]$HealthProver,
        [scriptblock]$Switcher
    )
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "The installation '$InstallationRoot' has no site declaration." }
    $declared = Get-Content -LiteralPath $site.ConfigPath -Raw | ConvertFrom-Json
    $declaredInstanceId = if ($declared.PSObject.Properties['instanceId']) { [string]$declared.instanceId } else { '' }
    if ([string]::IsNullOrWhiteSpace($declaredInstanceId)) { throw "The installation '$InstallationRoot' has no persisted instanceId." }
    if ([string]::IsNullOrWhiteSpace($site.PublicHostName)) { throw "The installation '$InstallationRoot' has no configured full-DNS publicHostName." }
    Assert-AeroLinkSiteServiceSid -Site $site -Role Api | Out-Null
    $layout = Get-AeroLinkServerLayout -InstallationRoot $site.InstallationRoot
    $lease = $null; $incoming = $null; $versionRoot = $null; $priorTarget = $null; $verified = $null; $clone = $null; $backupSha256 = $null
    $state = [pscustomobject]@{ Switched = $false; ServiceStopped = $false }
    try {
        $lease = Enter-AeroLinkTransition -InstallationRoot $site.InstallationRoot -Policy Preserve
        if ($lease.Pending) { throw 'An interrupted site update requires attended recovery before another update can start.' }
        $priorTarget = Get-AeroLinkSiteCurrentTarget -Layout $layout
        $incoming = Join-Path $layout.Versions ('.incoming-' + [guid]::NewGuid().ToString('N'))
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'VerifyingPackage' -PriorTarget $priorTarget -Recovery 'No database change; remove any incomplete incoming tree after inspection.' | Out-Null
        $verified = if ($VerifyPackage) { & $VerifyPackage $Package $TrustedCertificate $incoming } else {
            Test-AeroLinkReleasePackage -Package $Package -TrustedCertificate $TrustedCertificate -Destination $incoming -AllowUnverifiedDevelopment:$AllowUnverifiedDevelopment
        }
        $versionRoot = Join-Path $layout.Versions ([string]$verified.Version)
        if (Test-Path -LiteralPath $versionRoot) { throw "Version $($verified.Version) is already present at '$versionRoot'; refusing to reuse an unverified retained tree." }
        $packageProgram = Assert-AeroLinkSiteVersionRoot -Layout $layout -VersionRoot $incoming -Verified $verified
        Move-Item -LiteralPath $incoming -Destination $versionRoot
        $incoming = $null
        $packageExecutable = Join-Path $versionRoot 'app\AeroLink.Api.exe'
        if (-not (Test-Path -LiteralPath $packageExecutable -PathType Leaf)) {
            throw "The retained release '$versionRoot' has no packaged API executable at '$packageExecutable'."
        }
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'ValidatingClone' -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -Recovery 'The current junction is unchanged; the retained package can be removed after review.' | Out-Null
        $beforeRealUpgrade = {
                Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'SwitchingBeforeRealUpgrade' -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -BackupArchive $null -Recovery 'The verified package is current; if maintenance fails after mutation, use the retained backup and attended restore procedure.' | Out-Null
                if ($ServiceStopper) { & $ServiceStopper $site } else { Stop-Service -Name $site.ServiceName -ErrorAction Stop }
                $state.ServiceStopped = $true
                Set-AeroLinkSiteCurrentTarget -Layout $layout -VersionRoot $versionRoot -PriorTarget $priorTarget -Switcher $Switcher | Out-Null
                $state.Switched = $true
        }.GetNewClosure()
        $clone = if ($CloneRunner) { & $CloneRunner $packageExecutable $beforeRealUpgrade } else {
            Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $ProductRoot -InstallationRoot $site.InstallationRoot -ProtectedConfigRoot $ProtectedConfigRoot -ApiExecutable $packageExecutable -BeforeRealUpgrade $beforeRealUpgrade
        }
        if ($clone -and $clone.Archive -and (Test-Path -LiteralPath $clone.Archive -PathType Leaf)) {
            $backupSha256 = (Get-FileHash -LiteralPath $clone.Archive -Algorithm SHA256).Hash
        }
        if (-not $clone -or -not $clone.Applied) {
            $databaseModified = [bool]($clone -and $clone.DatabaseModified)
            $failureStage = if ($databaseModified) { 'RealMaintenanceFailed' } else { 'CloneOrMaintenanceFailed' }
            $recovery = if ($databaseModified) {
                'The real database may have changed; keep the new binary and service stopped, retain this backup, and use attended RESTORE-AEROLINK before any old-binary start.'
            } else {
                'Current junction and service were left unchanged; remove the retained unstarted version after review.'
            }
            Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage $failureStage -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -BackupArchive ([string]$clone.Archive) -BackupSha256 $backupSha256 -DatabaseModified $databaseModified -Detail ([string]$clone.Detail) -Recovery $recovery | Out-Null
            return [pscustomobject]@{ Applied = $false; Version = $verified.Version; Detail = if ($clone) { $clone.Detail } else { 'The clone validation did not return a result.' } }
        }
        if (-not $state.Switched) {
            Set-AeroLinkSiteCurrentTarget -Layout $layout -VersionRoot $versionRoot -PriorTarget $priorTarget -Switcher $Switcher | Out-Null
            $state.Switched = $true
        }
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'StartingAndCheckingPublicHealth' -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -BackupArchive ([string]$clone.Archive) -BackupSha256 $backupSha256 -DatabaseModified $true -Recovery 'The database was upgraded; do not start the old binary. Retain the backup and use the attended RESTORE-AEROLINK procedure.' | Out-Null
        if ($ServiceStarter) { & $ServiceStarter $site } else { Start-Service -Name $site.ServiceName -ErrorAction Stop }
        $baseUri = "https://$($site.PublicHostName):$($site.ApiPort)"
        $health = if ($HealthProver) { & $HealthProver $baseUri $verified $site } else { Test-AeroLinkSiteUpdateHealth -BaseUri $baseUri -ExpectedSourceSha $verified.SourceSha -ExpectedInstanceId $declaredInstanceId -ExpectedClassification $site.Classification }
        if (-not $health.Passed) { throw $health.Detail }
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'Completed' -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -BackupArchive ([string]$clone.Archive) -BackupSha256 $backupSha256 -DatabaseModified $true -Detail $health.Detail -Recovery 'No recovery action required.' | Out-Null
        return [pscustomobject]@{ Applied = $true; Version = $verified.Version; SourceSha = $verified.SourceSha; Archive = $clone.Archive; Detail = $health.Detail }
    } catch {
        if ($state.Switched -and -not $clone) {
            try { Set-AeroLinkSiteCurrentTarget -Layout $layout -VersionRoot $priorTarget -Switcher $Switcher | Out-Null } catch { }
        }
        $failedVersion = if ($verified) { [string]$verified.Version } else { $null }
        $failedSourceSha = if ($verified) { [string]$verified.SourceSha } else { $null }
        if ($state.ServiceStopped -and -not $state.Switched) { try { if ($ServiceStarter) { & $ServiceStarter $site } else { Start-Service -Name $site.ServiceName -ErrorAction SilentlyContinue } } catch { } }
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'Failed' -Version $failedVersion -SourceSha $failedSourceSha -PriorTarget $priorTarget -BackupArchive ([string]$clone.Archive) -BackupSha256 $backupSha256 -DatabaseModified ([bool]($clone -and ($clone.Applied -or $clone.DatabaseModified))) -Detail $_.Exception.Message -Recovery 'Inspect update-state.json. If the database was modified, keep the new binary and use the retained backup with attended RESTORE-AEROLINK; never start the old binary against changed schema.' | Out-Null
        throw
    } finally {
        if ($lease) { Exit-AeroLinkTransition -Lease $lease }
        if ($incoming -and (Test-Path -LiteralPath $incoming)) { Remove-Item -LiteralPath $incoming -Recurse -Force }
    }
}

Export-ModuleMember -Function @(
    'Get-AeroLinkSiteUpdateJournalPath',
    'Write-AeroLinkSiteUpdateJournal',
    'Get-AeroLinkSiteCurrentTarget',
    'Assert-AeroLinkSiteVersionRoot',
    'Set-AeroLinkSiteCurrentTarget',
    'Test-AeroLinkSiteUpdateHealth',
    'Invoke-AeroLinkSiteUpdate'
)
