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

function Assert-AeroLinkSiteUpdateAdmission {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstallationRoot)
    $path = Get-AeroLinkSiteUpdateJournalPath -InstallationRoot $InstallationRoot
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
    try { $journal = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { throw "The site updater journal at $path is malformed; attended recovery is required before another update. $($_.Exception.Message)" }
    $stage = if ($journal.PSObject.Properties['stage']) { [string]$journal.stage } else { '' }
    if ($stage -ne 'Completed') {
        $recovery = if ($journal.PSObject.Properties['recovery']) { [string]$journal.recovery } else { 'Inspect the journal and complete the attended recovery procedure.' }
        throw "The prior site update at $path is incomplete (stage '$stage'); refusing another package, backup or service mutation until attended recovery is complete. $recovery"
    }
}

function Set-AeroLinkSiteCurrentTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Layout,
        [Parameter(Mandatory)][string]$VersionRoot,
        [string]$PriorTarget
    )
    $target = [IO.Path]::GetFullPath($VersionRoot).TrimEnd('\', '/')
    $versions = [IO.Path]::GetFullPath($Layout.Versions).TrimEnd('\', '/') + '\'
    if (-not $target.StartsWith($versions, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $target -PathType Container)) {
        throw "Refusing to point current at '$target': it is not an existing release under $($Layout.Versions)."
    }
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
        [string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$ExpectedInstanceId,
        [Parameter(Mandatory)][string]$ExpectedClassification,
        [AllowNull()][pscustomobject]$Site,
        [AllowEmptyString()][string]$ExpectedExecutable,
        [AllowEmptyString()][string]$ExpectedCurrentTarget,
        [scriptblock]$ReadyProbe,
        [scriptblock]$IdentityProbe,
        [scriptblock]$ProcessOwnerProbe,
        [scriptblock]$ServiceProbe,
        [scriptblock]$CurrentTargetProbe
    )
    try {
        $origin = [Uri]$BaseUri
        if ($origin.Scheme -ne 'https' -or $origin.UserInfo -or $origin.AbsolutePath -ne '/' -or $origin.Query -or $origin.Fragment) {
            throw "The update health URI '$BaseUri' is not a normal HTTPS origin."
        }
        $expectedHost = Assert-AeroLinkSiteUpdatePublicHostName -PublicHostName $origin.Host
        if ($origin.Host -ine $expectedHost) { throw "The update health URI '$BaseUri' did not preserve the configured full-DNS host." }
    } catch {
        return [pscustomobject]@{ Passed = $false; Detail = $_.Exception.Message }
    }
    if ($ReadyProbe) {
        if (-not [bool](& $ReadyProbe $BaseUri)) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS readiness failed at $BaseUri." } }
    } else {
        $readyResponse = Invoke-AeroLinkSitePublicJson -BaseUri $BaseUri -Path '/health/ready'
        if (-not $readyResponse.Succeeded -or $readyResponse.Value.status -ne 'ready' -or $readyResponse.Value.database -ne 'connected') {
            return [pscustomobject]@{ Passed = $false; Detail = if ($readyResponse.Succeeded) { "Public HTTPS readiness failed at $BaseUri." } else { $readyResponse.Detail } }
        }
    }
    $identity = if ($IdentityProbe) { & $IdentityProbe $BaseUri } else {
        $identityResponse = Invoke-AeroLinkSitePublicJson -BaseUri $BaseUri -Path '/health/identity'
        if (-not $identityResponse.Succeeded) { return [pscustomobject]@{ Passed = $false; Detail = $identityResponse.Detail } }
        $identityResponse.Value
    }
    if (-not $identity) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS identity was unavailable at $BaseUri." } }
    if ($ExpectedVersion) {
        $actualVersion = if ($identity.PSObject.Properties['version']) { [string]$identity.version } else { '' }
        if ($actualVersion -cne $ExpectedVersion) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS identity version '$actualVersion' does not match verified package '$ExpectedVersion'." } }
    }
    # Public callers intentionally receive no source revision. If a loopback or qualification probe does expose
    # one, compare it as an additional observation; the production proof below binds the redacted response to the
    # owned service process and the signed tree on disk instead of weakening redaction or trusting semver alone.
    $source = if ($identity.PSObject.Properties['sourceIdentity'] -and $identity.sourceIdentity) { [string]$identity.sourceIdentity }
        elseif ($identity.PSObject.Properties['sourceSha'] -and $identity.sourceSha) { [string]$identity.sourceSha } else { '' }
    if ($source -and $source -cne $ExpectedSourceSha) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS identity source '$source' does not match verified package '$ExpectedSourceSha'." } }
    $instance = if ($identity.PSObject.Properties['instance']) { $identity.instance } else { $null }
    $actualId = if ($instance -and $instance.PSObject.Properties['id']) { [string]$instance.id } else { '' }
    $actualClass = if ($instance -and $instance.PSObject.Properties['classification']) { [string]$instance.classification } else { '' }
    if ($actualId -ine $ExpectedInstanceId -or $actualClass -ine $ExpectedClassification) { return [pscustomobject]@{ Passed = $false; Detail = "Public HTTPS identity does not match the declared installation ($actualClass/$actualId)." } }
    $proof = if ($Site -and $ExpectedExecutable -and $ExpectedCurrentTarget) {
        Test-AeroLinkSitePublicProcessBinding -Identity $identity -Site $Site -ExpectedSourceSha $ExpectedSourceSha `
            -ExpectedExecutable $ExpectedExecutable -ExpectedCurrentTarget $ExpectedCurrentTarget `
            -ProcessOwnerProbe $ProcessOwnerProbe -ServiceProbe $ServiceProbe -CurrentTargetProbe $CurrentTargetProbe
    } else {
        [pscustomobject]@{ Passed = $false; Detail = 'Public identity is redacted and no owned-process proof was supplied.' }
    }
    if (-not $proof -or -not $proof.Passed) { return [pscustomobject]@{ Passed = $false; Detail = if ($proof) { $proof.Detail } else { 'The public response could not be joined to the owned API process.' } } }
    return [pscustomobject]@{ Passed = $true; Detail = "Public HTTPS readiness and identity match the verified package for $ExpectedClassification/$ExpectedInstanceId; $($proof.Detail)" }
}

function Test-AeroLinkSitePublicProcessBinding {
    <#
        Joins a redacted public identity response to the exact service process and verified release tree.
        Public source SHA remains redacted. A semver or instance label alone is insufficient: the service PID,
        listener, executable, current junction, signed-tree source SHA and operating-system process birth time all
        have to agree with the response.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Identity,
        [Parameter(Mandatory)]$Site,
        [Parameter(Mandatory)][string]$ExpectedSourceSha,
        [Parameter(Mandatory)][string]$ExpectedExecutable,
        [Parameter(Mandatory)][string]$ExpectedCurrentTarget,
        [scriptblock]$ProcessOwnerProbe,
        [scriptblock]$ServiceProbe,
        [scriptblock]$CurrentTargetProbe
    )
    $fail = { param($Detail) [pscustomobject]@{ Passed = $false; Detail = $Detail } }
    try {
        $expectedExe = [IO.Path]::GetFullPath($ExpectedExecutable)
        $expectedTarget = [IO.Path]::GetFullPath($ExpectedCurrentTarget).TrimEnd('\', '/')
        $targetExe = [IO.Path]::GetFullPath((Join-Path $expectedTarget 'app\AeroLink.Api.exe'))
        if ($targetExe -ine $expectedExe) { return & $fail "The expected API executable '$expectedExe' is not under the expected current target '$expectedTarget'." }
        $layout = Get-AeroLinkServerLayout -InstallationRoot $Site.InstallationRoot

        $owner = if ($ProcessOwnerProbe) { & $ProcessOwnerProbe $Site.ApiPort } else { Get-AeroLinkPortOwner -Port $Site.ApiPort }
        if (-not $owner -or -not $owner.Found -or $owner.Ambiguous -or -not $owner.Attributable) {
            return & $fail "The declared API port does not have one attributable owned listener; public identity cannot be joined to this installation."
        }
        if ([int]$owner.ProcessId -le 0 -or [string]::IsNullOrWhiteSpace([string]$owner.StartedAt) -or [string]::IsNullOrWhiteSpace([string]$owner.ExecutablePath)) {
            return & $fail 'The API listener did not provide a complete PID, birth-time and image proof.'
        }
        if ([IO.Path]::GetFullPath([string]$owner.ExecutablePath) -ine $expectedExe) {
            return & $fail "The API listener runs '$($owner.ExecutablePath)', not the verified package executable '$expectedExe'."
        }

        $service = if ($ServiceProbe) { & $ServiceProbe $Site.ServiceName } else {
            Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f ([string]$Site.ServiceName -replace "'", "''")) -ErrorAction Stop
        }
        if (-not $service -or [int]$service.ProcessId -ne [int]$owner.ProcessId -or [string]$service.State -ne 'Running') {
            return & $fail 'The public response was not joined to the running API Windows service process.'
        }
        $serviceExecutable = ''
        $servicePath = [string]$service.PathName
        if ($servicePath -match '^\s*"([^"]+)"') { $serviceExecutable = $Matches[1] }
        elseif ($servicePath -match '^\s*(\S+)') { $serviceExecutable = $Matches[1] }
        if ([string]::IsNullOrWhiteSpace($serviceExecutable) -or [IO.Path]::GetFullPath($serviceExecutable) -ine [IO.Path]::GetFullPath($layout.ApiExecutable)) {
            return & $fail "The API service is configured with '$servicePath', not the stable current executable '$($layout.ApiExecutable)'."
        }

        $currentTarget = if ($CurrentTargetProbe) { & $CurrentTargetProbe $layout } else { Get-AeroLinkSiteCurrentTarget -Layout $layout }
        $currentTarget = [IO.Path]::GetFullPath([string]$currentTarget).TrimEnd('\', '/')
        if ($currentTarget -ine $expectedTarget) { return & $fail "The current junction resolves to '$currentTarget', not the verified target '$expectedTarget'." }
        $program = Get-AeroLinkProgramLayout -ProductRoot $currentTarget
        if ($program.SourceSha -cne $ExpectedSourceSha) { return & $fail "The live current tree source '$($program.SourceSha)' does not match the verified package '$ExpectedSourceSha'." }

        $publicStarted = if ($Identity.PSObject.Properties['startedAtUtc']) { [string]$Identity.startedAtUtc } else { '' }
        if ([string]::IsNullOrWhiteSpace($publicStarted)) { return & $fail 'The public identity has no process birth time to join to the owned service.' }
        $processStarted = [DateTimeOffset]::Parse([string]$owner.StartedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        $responseStarted = [DateTimeOffset]::Parse($publicStarted, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        if ($processStarted.UtcDateTime.Ticks -ne $responseStarted.UtcDateTime.Ticks) {
            return & $fail "The public response birth time '$publicStarted' does not match the owned API process '$($owner.StartedAt)'."
        }
        return [pscustomobject]@{ Passed = $true; Detail = "public response joined to service PID $([int]$owner.ProcessId), exact package image and current target" }
    }
    catch { return & $fail "The public response could not be joined to the owned API process: $($_.Exception.Message)" }
}

function Assert-AeroLinkSiteUpdatePublicHostName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublicHostName)
    $hostName = $PublicHostName.Trim().ToLowerInvariant()
    if ($hostName -in @('localhost', 'localhost.localdomain') -or $hostName.EndsWith('.localhost', [StringComparison]::OrdinalIgnoreCase)) {
        throw "The site update public host '$PublicHostName' is a loopback/localhost name; refusing to treat it as the production HTTPS origin."
    }
    $address = [Net.IPAddress]::None
    if ([Net.IPAddress]::TryParse($hostName, [ref]$address)) {
        throw "The site update public host '$PublicHostName' is an IP address; refusing to treat it as the production full-DNS HTTPS origin."
    }
    if ($hostName -notmatch '^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$') {
        throw "The site update public host '$PublicHostName' is not a valid full-DNS HTTPS origin."
    }
    return $hostName
}

function Invoke-AeroLinkSitePublicJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUri, [Parameter(Mandatory)][string]$Path)
    $uri = $BaseUri.TrimEnd('/') + $Path
    try {
        # A public proof must remain on the configured origin. PowerShell otherwise follows a 3xx and can prove a
        # different host while the configured full-DNS endpoint itself never answered.
        $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 0
        $status = [int]$response.StatusCode
        if ($status -ge 300 -and $status -lt 400) { throw "The public endpoint redirected at $uri; refusing a substituted origin." }
        if ($status -ne 200) { throw "The public endpoint at $uri returned HTTP $status, not 200." }
        return [pscustomobject]@{ Succeeded = $true; Value = ([string]$response.Content | ConvertFrom-Json); Detail = '' }
    }
    catch {
        $status = $null
        try { if ($_.Exception.Response -and $_.Exception.Response.StatusCode) { $status = [int]$_.Exception.Response.StatusCode } } catch { }
        if ($status -ge 300 -and $status -lt 400) {
            return [pscustomobject]@{ Succeeded = $false; Value = $null; Detail = "The public endpoint at $uri redirected (HTTP $status); refusing a substituted origin." }
        }
        return [pscustomobject]@{ Succeeded = $false; Value = $null; Detail = "The public endpoint at $uri could not be proved: $($_.Exception.Message)" }
    }
}

function Invoke-AeroLinkSiteUpdate {
    <# The production entry point. Its destructive boundaries are always the installed production authorities. #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [Parameter(Mandatory)][string]$InstallationRoot,
        [Parameter(Mandatory)][string]$Package,
        [Parameter(Mandatory)][string]$TrustedCertificate,
        [string]$ProtectedConfigRoot,
        [switch]$AllowUnverifiedDevelopment
    )
    $site = Get-AeroLinkSiteDeclaration -InstallationRoot $InstallationRoot
    if (-not $site) { throw "The installation '$InstallationRoot' has no site declaration." }
    $declared = Get-Content -LiteralPath $site.ConfigPath -Raw | ConvertFrom-Json
    $declaredInstanceId = if ($declared.PSObject.Properties['instanceId']) { [string]$declared.instanceId } else { '' }
    if ([string]::IsNullOrWhiteSpace($declaredInstanceId)) { throw "The installation '$InstallationRoot' has no persisted instanceId." }
    if ([string]::IsNullOrWhiteSpace($site.PublicHostName)) { throw "The installation '$InstallationRoot' has no configured full-DNS publicHostName." }
    $publicHostName = Assert-AeroLinkSiteUpdatePublicHostName -PublicHostName $site.PublicHostName
    Assert-AeroLinkSiteServiceSid -Site $site -Role Api | Out-Null
    $layout = Get-AeroLinkServerLayout -InstallationRoot $site.InstallationRoot
    Assert-AeroLinkSiteUpdateAdmission -InstallationRoot $site.InstallationRoot
    if (-not $PSCmdlet.ShouldProcess($site.InstallationRoot, "Verify and apply signed AeroLink package '$Package'")) {
        return [pscustomobject]@{ Applied = $false; WhatIf = $true; Detail = 'WhatIf requested; no package, database, service, junction or journal mutation was performed.' }
    }
    $operationId = [guid]::NewGuid().ToString('N')
    $lease = $null; $incoming = $null; $versionRoot = $null; $priorTarget = $null; $verified = $null; $clone = $null; $backupSha256 = $null
    $state = [pscustomobject]@{ Switched = $false; ServiceStopped = $false }
    try {
        $lease = Enter-AeroLinkTransition -InstallationRoot $site.InstallationRoot -Policy Preserve -OperationId $operationId -Phase 'Admission'
        if ($lease.Pending) { throw 'An interrupted site update requires attended recovery before another update can start.' }
        $priorTarget = Get-AeroLinkSiteCurrentTarget -Layout $layout
        $incoming = Join-Path $layout.Versions ('.incoming-' + [guid]::NewGuid().ToString('N'))
        Set-AeroLinkTransitionPhase -Lease $lease -Phase 'VerifyingPackage'
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'VerifyingPackage' -PriorTarget $priorTarget -Recovery 'No database change; remove any incomplete incoming tree after inspection.' | Out-Null
        $verified = Test-AeroLinkReleasePackage -Package $Package -TrustedCertificate $TrustedCertificate -Destination $incoming -AllowUnverifiedDevelopment:$AllowUnverifiedDevelopment
        $versionRoot = Join-Path $layout.Versions ([string]$verified.Version)
        if (Test-Path -LiteralPath $versionRoot) { throw "Version $($verified.Version) is already present at '$versionRoot'; refusing to reuse an unverified retained tree." }
        $packageProgram = Assert-AeroLinkSiteVersionRoot -Layout $layout -VersionRoot $incoming -Verified $verified
        Move-Item -LiteralPath $incoming -Destination $versionRoot
        $incoming = $null
        $packageExecutable = Join-Path $versionRoot 'app\AeroLink.Api.exe'
        if (-not (Test-Path -LiteralPath $packageExecutable -PathType Leaf)) {
            throw "The retained release '$versionRoot' has no packaged API executable at '$packageExecutable'."
        }
        Set-AeroLinkTransitionPhase -Lease $lease -Phase 'ValidatingClone'
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'ValidatingClone' -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -Recovery 'The current junction is unchanged; the retained package can be removed after review.' | Out-Null
        $beforeRealUpgrade = {
                Set-AeroLinkTransitionPhase -Lease $lease -Phase 'SwitchingBeforeRealUpgrade'
                Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'SwitchingBeforeRealUpgrade' -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -BackupArchive $null -Recovery 'The verified package is current; if maintenance fails after mutation, use the retained backup and attended restore procedure.' | Out-Null
                Stop-Service -Name $site.ServiceName -ErrorAction Stop
                $state.ServiceStopped = $true
                Set-AeroLinkSiteCurrentTarget -Layout $layout -VersionRoot $versionRoot -PriorTarget $priorTarget | Out-Null
                $state.Switched = $true
        }.GetNewClosure()
        $clone = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $ProductRoot -InstallationRoot $site.InstallationRoot -ProtectedConfigRoot $ProtectedConfigRoot -ApiExecutable $packageExecutable -BeforeRealUpgrade $beforeRealUpgrade
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
            Set-AeroLinkSiteCurrentTarget -Layout $layout -VersionRoot $versionRoot -PriorTarget $priorTarget | Out-Null
            $state.Switched = $true
        }
        Set-AeroLinkTransitionPhase -Lease $lease -Phase 'StartingAndCheckingPublicHealth'
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'StartingAndCheckingPublicHealth' -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -BackupArchive ([string]$clone.Archive) -BackupSha256 $backupSha256 -DatabaseModified $true -Recovery 'The database was upgraded; do not start the old binary. Retain the backup and use the attended RESTORE-AEROLINK procedure.' | Out-Null
        Start-Service -Name $site.ServiceName -ErrorAction Stop
        $baseUri = "https://$($publicHostName):$($site.ApiPort)"
        $health = Test-AeroLinkSiteUpdateHealth -BaseUri $baseUri -ExpectedSourceSha $verified.SourceSha -ExpectedVersion $verified.Version `
            -ExpectedInstanceId $declaredInstanceId -ExpectedClassification $site.Classification -Site $site `
            -ExpectedExecutable $packageExecutable -ExpectedCurrentTarget $versionRoot
        if (-not $health.Passed) { throw $health.Detail }
        Set-AeroLinkTransitionPhase -Lease $lease -Phase 'Completed'
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'Completed' -Version $verified.Version -SourceSha $verified.SourceSha -PriorTarget $priorTarget -BackupArchive ([string]$clone.Archive) -BackupSha256 $backupSha256 -DatabaseModified $true -Detail $health.Detail -Recovery 'No recovery action required.' | Out-Null
        return [pscustomobject]@{ Applied = $true; Version = $verified.Version; SourceSha = $verified.SourceSha; Archive = $clone.Archive; Detail = $health.Detail }
    } catch {
        if ($state.Switched -and -not $clone) {
            try { Set-AeroLinkSiteCurrentTarget -Layout $layout -VersionRoot $priorTarget | Out-Null } catch { }
        }
        $failedVersion = if ($verified) { [string]$verified.Version } else { $null }
        $failedSourceSha = if ($verified) { [string]$verified.SourceSha } else { $null }
        if ($state.ServiceStopped -and -not $state.Switched) { try { Start-Service -Name $site.ServiceName -ErrorAction SilentlyContinue } catch { } }
        if ($lease -and $lease.Owner) {
            try { Set-AeroLinkTransitionPhase -Lease $lease -Phase 'Failed' } catch { }
        }
        $recovery = if ($_.Exception.Message -match 'cleanup') {
            'An isolated validation copy or evidence tree may remain; inspect and remove that generated copy before retry. The persistent database was not changed.'
        } else {
            'Inspect update-state.json. If the database was modified, keep the new binary and use the retained backup with attended RESTORE-AEROLINK; never start the old binary against changed schema.'
        }
        Write-AeroLinkSiteUpdateJournal -InstallationRoot $site.InstallationRoot -Stage 'Failed' -Version $failedVersion -SourceSha $failedSourceSha -PriorTarget $priorTarget -BackupArchive ([string]$clone.Archive) -BackupSha256 $backupSha256 -DatabaseModified ([bool]($clone -and ($clone.Applied -or $clone.DatabaseModified))) -Detail $_.Exception.Message -Recovery $recovery | Out-Null
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
    'Test-AeroLinkSitePublicProcessBinding',
    'Assert-AeroLinkSiteUpdatePublicHostName',
    'Invoke-AeroLinkSiteUpdate'
)
