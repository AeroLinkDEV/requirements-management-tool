#Requires -Version 5.1
<#
    The dedicated HOME production source checkout, and the only authority on where it is and whether it may
    be run.

    On 2026-09-03 the HOME PC rebooted at 11:52. The recovery task ran 15 seconds later, PostgreSQL came up,
    and nothing else happened: the only checkout on the machine was being used for #880 development, sitting
    on feat/880-slice6-digital-thread-page with modified and untracked work, and the HOME canonical source
    policy correctly refused to exercise the canonical database from it. Port 5080 never opened, ngrok was
    never started, and the demo answered ERR_NGROK_3200 until somebody noticed.

    The guard was right. The architecture was wrong: HOME production had an operational dependency on whichever
    branch an AI agent happened to be editing. So production gets its own checkout, used by nothing else.

    Separate clone rather than a worktree, deliberately. One Git repository cannot have `main` checked out in
    two worktrees at once, and an acceptance criterion of #881 is that the development checkout stays free to
    check out `main` whenever it likes. A production worktree pinned to a detached origin/main would satisfy
    the letter of that, at the cost of a source posture that no longer means "on main" and a second definition
    of canonical to keep in step. A clone has its own `main`, its own reflog, and reuses the existing HOME
    canonical policy unchanged.

    Source is separated; DATA is not. The clone carries an installation pointer (AeroLinkInstallation.psm1)
    naming the canonical HOME installation, so it starts the same PostgreSQL cluster, reads the same evidence,
    and writes the same backups. Nothing here initializes, copies, or migrates persistent state.

    What this module will never do to reach a canonical state: stash, reset, rebase, merge anything but a
    strict fast-forward, force-checkout, or delete an untracked file — in the production source or anywhere
    else. Least of all in the development checkout, which it does not touch at all.
#>

Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBootstrap.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force

$script:ProductionSourceMarkerName = 'production-source.json'

function Get-AeroLinkProductionSourceConfigPath {
    <#
        .SYNOPSIS Where the per-user production-source configuration lives. Outside source control.
    #>
    return Join-Path $env:LOCALAPPDATA 'AeroLink\Production\production-source.config.psd1'
}

function Get-AeroLinkProductionSourceMarkerPath {
    <#
        .SYNOPSIS The marker that says "this checkout exists to run production, and nothing else".
        .DESCRIPTION
            It lives under product\.local, which is git-ignored, so it is never repository content and can
            never make a canonical source posture dirty. Its purpose is to make the 2026-09-03 mistake
            unrepresentable: a recovery task can be pointed only at a checkout that declares itself
            dedicated, so it can never quietly be aimed back at the development checkout.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SourceRoot)
    return Join-Path (Join-Path $SourceRoot 'product\.local') $script:ProductionSourceMarkerName
}

function Get-AeroLinkProductionSourceConfig {
    <#
        .SYNOPSIS Loads and validates the production-source configuration. Non-secret values only.
    #>
    [CmdletBinding()]
    param([string]$ConfigPath = (Get-AeroLinkProductionSourceConfigPath))

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "AeroLink production-source configuration not found at $ConfigPath. Create it with Initialize-AeroLinkProductionSource, or CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat."
    }
    try { $values = Import-PowerShellDataFile -LiteralPath $ConfigPath }
    catch [System.Management.Automation.CommandNotFoundException] {
        # Not a configuration problem, and saying so would send the reader to a file that is fine. A
        # PowerShell 7 parent leaves the 7.x module directories ahead of Windows PowerShell's own, so 5.1
        # binds Microsoft.PowerShell.Utility out of the wrong tree and this cmdlet is simply absent.
        throw "Windows PowerShell could not find $($_.Exception.CommandName), so the production-source configuration at $ConfigPath was never read. The configuration is not implicated. Start this from Explorer or cmd, or use the repository .bat entry points, which clear PSModulePath first."
    }
    catch { throw "AeroLink production-source configuration at $ConfigPath is malformed: $($_.Exception.Message)" }

    $allowed = @('SourceRoot', 'InstallationRoot', 'RemoteName', 'FetchTimeoutSeconds', 'ReconcileIntervalMinutes')
    foreach ($key in $values.Keys) {
        if ($allowed -notcontains $key) { throw "AeroLink production-source configuration contains an unknown key '$key'. Only non-secret operator values are allowed." }
    }
    if (-not $values.ContainsKey('SourceRoot') -or [string]::IsNullOrWhiteSpace([string]$values['SourceRoot'])) {
        throw 'AeroLink production-source configuration must name SourceRoot.'
    }
    $sourceRoot = [string]$values['SourceRoot']
    if (-not [IO.Path]::IsPathRooted($sourceRoot)) { throw "AeroLink production-source SourceRoot must be an absolute path; it is '$sourceRoot'." }

    return [pscustomobject]@{
        ConfigPath               = $ConfigPath
        SourceRoot               = [IO.Path]::GetFullPath($sourceRoot)
        InstallationRoot         = if ($values.ContainsKey('InstallationRoot')) { [IO.Path]::GetFullPath([string]$values['InstallationRoot']) } else { $null }
        RemoteName               = if ($values.ContainsKey('RemoteName')) { [string]$values['RemoteName'] } else { 'origin' }
        FetchTimeoutSeconds      = if ($values.ContainsKey('FetchTimeoutSeconds')) { [int]$values['FetchTimeoutSeconds'] } else { 45 }
        ReconcileIntervalMinutes = if ($values.ContainsKey('ReconcileIntervalMinutes')) { [int]$values['ReconcileIntervalMinutes'] } else { 30 }
    }
}

function Get-AeroLinkProductionSourcePosture {
    <#
        .SYNOPSIS Whether a checkout may be run as HOME canonical production, without throwing and without
          touching anything.
        .DESCRIPTION
            The reconciliation and status paths need to ASK this question; the launcher needs to REFUSE on it.
            Both must get the same answer from the same policy, so this wraps the existing HOME canonical
            assertion rather than re-deriving it — one authority, two calling styles.

            Canonicality is "clean source corresponding to an approved origin/main revision", not "the branch
            is called main": the assertion also refuses local-only commits, divergence, tracked modifications
            and untracked (potentially executable) source.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [string]$RemoteName = 'origin'
    )
    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        return [pscustomobject]@{ Canonical = $false; Dedicated = $false; Reason = "The production source directory does not exist: $SourceRoot"; Posture = $null }
    }
    $dedicated = Test-Path -LiteralPath (Get-AeroLinkProductionSourceMarkerPath -SourceRoot $SourceRoot) -PathType Leaf
    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $SourceRoot -RemoteName $RemoteName
    try {
        Assert-AeroLinkHomeCanonicalSourcePolicy -Posture $posture -Context 'startup' 6>$null | Out-Null
        return [pscustomobject]@{ Canonical = $true; Dedicated = $dedicated; Reason = "Clean canonical main @ $($posture.ShortSha)."; Posture = $posture }
    }
    catch {
        return [pscustomobject]@{ Canonical = $false; Dedicated = $dedicated; Reason = $_.Exception.Message; Posture = $posture }
    }
}

function Assert-AeroLinkDedicatedProductionSource {
    <#
        .SYNOPSIS Refuses any production/recovery use of a checkout that is not the dedicated one.
        .DESCRIPTION
            This is the contract that makes 2026-09-03 unrepeatable. Recovery may only be aimed at a checkout
            that declares itself dedicated production source; the active development checkout does not, so it
            cannot be aimed there by a stale configuration, a copy-paste, or a helpful correction.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [string]$RemoteName = 'origin'
    )
    $posture = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
    if (-not $posture.Dedicated) {
        throw "AeroLink production/recovery refused: $SourceRoot is not a dedicated AeroLink production source. HOME production must never run from the active development checkout, whatever branch it is on. Create the dedicated source with Initialize-AeroLinkProductionSource. Nothing was changed."
    }
    if (-not $posture.Canonical) {
        throw "AeroLink production/recovery refused: the dedicated production source at $SourceRoot is not canonical. $($posture.Reason) Nothing was changed."
    }
    return $posture
}

function Initialize-AeroLinkProductionSource {
    <#
        .SYNOPSIS Creates (or confirms) the dedicated production source and points it at the canonical
          installation. Idempotent.
        .DESCRIPTION
            Clones from the same origin the development checkout uses, marks the result as dedicated
            production source, and records the installation pointer so the clone runs the canonical HOME
            PostgreSQL, evidence, attachments and backups rather than initializing an empty second
            installation beside its own source.

            It never writes to the development checkout, and never initializes persistent data. A clone that
            already exists is validated, not re-created.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$InstallationRoot,
        [string]$OriginUrl,
        [string]$ReferenceRepositoryRoot,
        [string]$RemoteName = 'origin',
        [switch]$WriteConfig,
        [string]$ConfigPath = (Get-AeroLinkProductionSourceConfigPath)
    )

    $SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
    $InstallationRoot = [IO.Path]::GetFullPath($InstallationRoot)
    if (-not (Test-Path -LiteralPath $InstallationRoot -PathType Container)) {
        throw "Refusing to create a production source pointed at an installation root that does not exist: $InstallationRoot. AeroLink will not initialize a second installation."
    }
    # A production clone inside the installation it points at would put source under a persistent data root,
    # where a backup, a restore or a prune could reach it.
    $installationPrefix = $InstallationRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (($SourceRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($installationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to create the production source inside the persistent installation root ($InstallationRoot). Source and data must not nest."
    }

    if (-not $OriginUrl) {
        if (-not $ReferenceRepositoryRoot) { throw 'Initialize-AeroLinkProductionSource needs -OriginUrl, or -ReferenceRepositoryRoot to read one from.' }
        $OriginUrl = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $ReferenceRepositoryRoot -GitArguments @('remote', 'get-url', $RemoteName)
        if ([string]::IsNullOrWhiteSpace($OriginUrl)) { throw "Could not read the '$RemoteName' URL from $ReferenceRepositoryRoot." }
        $OriginUrl = $OriginUrl.Trim()
    }

    $cloned = $false
    $isRepository = (Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $SourceRoot -GitArguments @('rev-parse', '--is-inside-work-tree')) -eq 'true'
    if (-not $isRepository) {
        if ((Test-Path -LiteralPath $SourceRoot -PathType Container) -and @(Get-ChildItem -LiteralPath $SourceRoot -Force).Count -gt 0) {
            throw "Refusing to clone the production source into ${SourceRoot}: the directory exists and is not empty, and is not a Git working tree. Inspect it; nothing was changed."
        }
        $parent = Split-Path $SourceRoot -Parent
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Write-Host "Cloning the dedicated AeroLink production source into $SourceRoot..." -ForegroundColor Cyan
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & git clone --branch main --origin $RemoteName -- $OriginUrl $SourceRoot 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previous }
        if ($exitCode -ne 0) { throw "The production source clone failed: $((($output | ForEach-Object { "$_" }) -join ' ').Trim())" }
        $cloned = $true
    }

    # The marker and the pointer, both idempotent, both under the ignored .local area.
    $localRoot = Join-Path $SourceRoot 'product\.local'
    if (-not (Test-Path -LiteralPath $localRoot -PathType Container)) { New-Item -ItemType Directory -Path $localRoot -Force | Out-Null }
    [pscustomobject]@{
        dedicatedProductionSource = $true
        note                      = 'AeroLink HOME canonical production/remote-demo source. Not for development: no feature branches, no local commits, no untracked source.'
        recordedAtUtc             = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Get-AeroLinkProductionSourceMarkerPath -SourceRoot $SourceRoot) -Encoding UTF8

    Set-AeroLinkInstallationPointer -ProductRoot (Join-Path $SourceRoot 'product') -InstallationRoot $InstallationRoot | Out-Null

    if ($WriteConfig) {
        $configDirectory = Split-Path $ConfigPath -Parent
        if (-not (Test-Path -LiteralPath $configDirectory -PathType Container)) { New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null }
        @(
            '@{',
            "    SourceRoot       = '$SourceRoot'",
            "    InstallationRoot = '$InstallationRoot'",
            "    RemoteName       = '$RemoteName'",
            '}'
        ) -join [Environment]::NewLine | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
    }

    $posture = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
    return [pscustomobject]@{
        SourceRoot       = $SourceRoot
        InstallationRoot = $InstallationRoot
        Cloned           = $cloned
        Canonical        = $posture.Canonical
        Reason           = $posture.Reason
        HeadSha          = if ($posture.Posture) { $posture.Posture.HeadSha } else { $null }
        ConfigPath       = if ($WriteConfig) { $ConfigPath } else { $null }
    }
}

function Update-AeroLinkProductionSource {
    <#
        .SYNOPSIS Brings the dedicated production source to the current approved origin/main, or explains why
          it did not.
        .DESCRIPTION
            Fetch, then advance only by strict fast-forward, then revalidate the whole HOME canonical policy
            against the result — the same assertion the launcher applies, so the two cannot disagree. When
            GitHub is unreachable a previously verified clean cached main is allowed to run with an explicit
            "not verified against the remote" diagnostic; a network failure alone must never make a valid
            HOME installation unusable.

            Nothing here is a repair. A production source that has somehow acquired dirt, an untracked file,
            or a local commit is reported and refused, exactly as found.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [string]$RemoteName = 'origin',
        [int]$FetchTimeoutSeconds = 45,
        [switch]$AllowNonDedicated,
        [scriptblock]$FetchOverride
    )

    if (-not $AllowNonDedicated) {
        $marker = Get-AeroLinkProductionSourceMarkerPath -SourceRoot $SourceRoot
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw "AeroLink production source update refused: $SourceRoot is not a dedicated AeroLink production source. Nothing was changed."
        }
    }

    $before = Get-AeroLinkRepositoryPosture -RepositoryRoot $SourceRoot -RemoteName $RemoteName
    if (-not $before.IsGitRepository) {
        throw "AeroLink cannot characterize the production source: $SourceRoot is not a Git working tree. Nothing was changed."
    }
    # Refuse before the network. A posture that is already unacceptable stays unacceptable after a fetch, and
    # refusing first means an unexplained state is never given a chance to be quietly fast-forwarded.
    $preFetch = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
    if (-not $preFetch.Canonical -and $before.Relationship -ne 'Behind') {
        return [pscustomobject]@{
            Action = 'Refused'; Canonical = $false; HeadSha = $before.HeadSha
            RemoteReachable = $null; Reason = $preFetch.Reason
        }
    }

    $reached = if ($FetchOverride) { [bool](& $FetchOverride $SourceRoot) } else { Sync-AeroLinkRemoteRefs -RepositoryRoot $SourceRoot -RemoteName $RemoteName -TimeoutSeconds $FetchTimeoutSeconds }
    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $SourceRoot -RemoteName $RemoteName

    if (-not $reached) {
        $offline = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
        if (-not $offline.Canonical) {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha
                RemoteReachable = $false; Reason = $offline.Reason
            }
        }
        if ($null -eq $posture.RemoteMainSha) {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha
                RemoteReachable = $false; Reason = "GitHub is unavailable and no cached origin/main exists, so the canonical source posture cannot be verified for main @ $($posture.ShortSha)."
            }
        }
        return [pscustomobject]@{
            Action = 'CachedCanonical'; Canonical = $true; HeadSha = $posture.HeadSha
            RemoteReachable = $false; Reason = "GitHub is unavailable. Running the previously verified cached clean main @ $($posture.ShortSha); the latest remote revision could not be verified."
        }
    }

    if ($null -eq $posture.RemoteMainSha) {
        return [pscustomobject]@{
            Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha
            RemoteReachable = $true; Reason = "The remote was reachable but $RemoteName/main was not found. Nothing was changed."
        }
    }

    if ($posture.Relationship -eq 'Behind') {
        try { Invoke-AeroLinkBootstrapGit -RepositoryRoot $SourceRoot -GitArguments @('merge', '--ff-only', "$RemoteName/main") | Out-Null }
        catch {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha
                RemoteReachable = $true; Reason = "The strict fast-forward of the production source was refused by Git and nothing was changed: $($_.Exception.Message)"
            }
        }
        $updated = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
        if (-not $updated.Canonical) {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = if ($updated.Posture) { $updated.Posture.HeadSha } else { $null }
                RemoteReachable = $true; Reason = $updated.Reason
            }
        }
        if ($updated.Posture.HeadSha -ne $posture.RemoteMainSha) {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $updated.Posture.HeadSha
                RemoteReachable = $true; Reason = "The production source changed during the update: HEAD is $($updated.Posture.ShortSha) but the verified update target was $($posture.ShortRemoteMainSha). Inspect the source; no automatic action was taken."
            }
        }
        return [pscustomobject]@{
            Action = 'Updated'; Canonical = $true; HeadSha = $updated.Posture.HeadSha
            RemoteReachable = $true; Reason = "The production source was strictly fast-forwarded to $RemoteName/main @ $($updated.Posture.ShortSha) and revalidated."
        }
    }

    $current = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
    if (-not $current.Canonical) {
        return [pscustomobject]@{
            Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha
            RemoteReachable = $true; Reason = $current.Reason
        }
    }
    return [pscustomobject]@{
        Action = 'AlreadyCurrent'; Canonical = $true; HeadSha = $posture.HeadSha
        RemoteReachable = $true; Reason = "The production source is current with $RemoteName/main @ $($posture.ShortSha)."
    }
}

Export-ModuleMember -Function @(
    'Get-AeroLinkProductionSourceConfigPath',
    'Get-AeroLinkProductionSourceMarkerPath',
    'Get-AeroLinkProductionSourceConfig',
    'Get-AeroLinkProductionSourcePosture',
    'Assert-AeroLinkDedicatedProductionSource',
    'Initialize-AeroLinkProductionSource',
    'Update-AeroLinkProductionSource'
)
