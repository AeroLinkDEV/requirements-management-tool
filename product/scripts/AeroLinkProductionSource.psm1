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
        return [pscustomobject]@{ Canonical = $false; Dedicated = $false; BindingReason = $null; Reason = "The production source directory does not exist: $SourceRoot"; Posture = $null }
    }

    # "Dedicated" is a claim the marker makes, so the marker is READ rather than merely counted.
    #
    # File-existence alone was too weak for what it authorises. It said nothing about which repository this
    # checkout is, so an existing clone of some other remote could be blessed as dedicated and then judged
    # canonical against ITS own origin/main; and nothing about which installation it belongs to, so a source
    # could stay perfectly canonical while its pointer was moved to a different existing installation. Source
    # identity and data identity are one binding or they are no binding at all.
    $marker = Read-AeroLinkProductionSourceMarker -SourceRoot $SourceRoot
    $dedicated = $marker.Valid
    $bindingReason = $marker.Reason

    if ($dedicated) {
        $actualOrigin = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $SourceRoot -GitArguments @('remote', 'get-url', $RemoteName)
        $actualOrigin = if ($actualOrigin) { $actualOrigin.Trim() } else { '' }
        if (-not (Test-AeroLinkSameRemote -Left $actualOrigin -Right $marker.OriginUrl)) {
            $dedicated = $false
            $bindingReason = "The production source's '$RemoteName' remote is '$actualOrigin', but it was created against '$($marker.OriginUrl)'. This is not the repository it claims to be."
        }
        else {
            $resolvedInstallation = try { (Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $SourceRoot 'product')).InstallationRoot } catch { $null }
            if (-not $resolvedInstallation -or -not [string]::Equals($resolvedInstallation, $marker.InstallationRoot, [StringComparison]::OrdinalIgnoreCase)) {
                $dedicated = $false
                $bindingReason = "The production source now resolves to installation '$resolvedInstallation', but it was bound to '$($marker.InstallationRoot)'. Source and data identity must agree."
            }
        }
    }

    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $SourceRoot -RemoteName $RemoteName
    try {
        Assert-AeroLinkHomeCanonicalSourcePolicy -Posture $posture -Context 'startup' 6>$null | Out-Null
        return [pscustomobject]@{ Canonical = $true; Dedicated = $dedicated; BindingReason = $bindingReason; Reason = "Clean canonical main @ $($posture.ShortSha)."; Posture = $posture }
    }
    catch {
        return [pscustomobject]@{ Canonical = $false; Dedicated = $dedicated; BindingReason = $bindingReason; Reason = $_.Exception.Message; Posture = $posture }
    }
}

function Test-AeroLinkSameRemote {
    <#
        .SYNOPSIS Whether two remote URLs name the same repository, allowing for trivial spelling differences.
        .DESCRIPTION
            A trailing .git, a trailing slash and case on the host are not different repositories. Anything
            beyond that is treated as different, because the point is to catch a wrong-origin checkout rather
            than to normalise every way a remote can be written.
    #>
    [CmdletBinding()]
    param([AllowNull()][string]$Left, [AllowNull()][string]$Right)
    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
    $normalize = {
        param($value)
        $trimmed = ([string]$value).Trim().TrimEnd('/')
        if ($trimmed.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase)) { $trimmed = $trimmed.Substring(0, $trimmed.Length - 4) }
        return $trimmed.TrimEnd('/')
    }
    return [string]::Equals((& $normalize $Left), (& $normalize $Right), [StringComparison]::OrdinalIgnoreCase)
}

function Read-AeroLinkProductionSourceMarker {
    <#
        .SYNOPSIS Reads and validates the dedicated-production-source marker, rather than counting the file.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SourceRoot)
    $path = Get-AeroLinkProductionSourceMarkerPath -SourceRoot $SourceRoot
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]@{ Valid = $false; Reason = "$SourceRoot carries no dedicated production-source marker."; OriginUrl = $null; InstallationRoot = $null }
    }
    try { $marker = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { return [pscustomobject]@{ Valid = $false; Reason = "The dedicated production-source marker at $path is malformed: $($_.Exception.Message)"; OriginUrl = $null; InstallationRoot = $null } }

    if (-not $marker.PSObject.Properties['dedicatedProductionSource'] -or -not [bool]$marker.dedicatedProductionSource) {
        return [pscustomobject]@{ Valid = $false; Reason = "The marker at $path does not assert that this is a dedicated production source."; OriginUrl = $null; InstallationRoot = $null }
    }
    $originUrl = if ($marker.PSObject.Properties['originUrl']) { [string]$marker.originUrl } else { $null }
    $installationRoot = if ($marker.PSObject.Properties['installationRoot']) { [string]$marker.installationRoot } else { $null }
    if ([string]::IsNullOrWhiteSpace($originUrl) -or [string]::IsNullOrWhiteSpace($installationRoot)) {
        return [pscustomobject]@{ Valid = $false; Reason = "The marker at $path does not record the repository and installation it was bound to. Re-create the production source."; OriginUrl = $originUrl; InstallationRoot = $installationRoot }
    }
    return [pscustomobject]@{ Valid = $true; Reason = 'Marker asserts a dedicated production source bound to a named repository and installation.'; OriginUrl = $originUrl; InstallationRoot = [IO.Path]::GetFullPath($installationRoot) }
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
        $detail = if ($posture.BindingReason) { " $($posture.BindingReason)" } else { '' }
        throw "AeroLink production/recovery refused: $SourceRoot is not a dedicated AeroLink production source.$detail HOME production must never run from the active development checkout, whatever branch it is on. Create the dedicated source with Initialize-AeroLinkProductionSource. Nothing was changed."
    }
    if (-not $posture.Canonical) {
        throw "AeroLink production/recovery refused: the dedicated production source at $SourceRoot is not canonical. $($posture.Reason) Nothing was changed."
    }
    return $posture
}

function Assert-AeroLinkRunningFromProductionSource {
    <#
        .SYNOPSIS Refuses HOME production from a checkout that is not the machine's dedicated production source.
        .DESCRIPTION
            The canonical-main gate already refuses a dirty or feature-branch development checkout, so this is
            not about running unreviewed code. It is about which working tree the resulting long-lived process
            executes out of. A development checkout momentarily on clean main passes every gate, serves the
            demo happily, and is then one `git checkout` away from having its assemblies and client bundle
            replaced underneath a running process - the 2026-09-03 failure with a different first step.

            NO configuration is the ordinary state on a laptop, and on any machine set up before #881. There,
            this launcher is the only way to run production at all, so refusing would remove the feature
            rather than protect it: the check applies only once a dedicated source has been declared.

            A configuration that is PRESENT but malformed is the opposite case and must fail closed. Treating
            every read error as "not configured" meant a corrupt, truncated or hand-edited config on a HOME
            machine silently disabled the guard it exists to arm, and production fell back to checkout-local
            behaviour precisely when something was already wrong. Absence is decided by the file not being
            there, not by an exception.

            Matching the path is necessary and not sufficient. A checkout can sit at the configured path and
            still have a marker that is malformed, a remote that is not AeroLink, or an installation pointer
            redirected to a different installation - all states the posture check calls non-dedicated, and
            all of which would otherwise pass here and go on to exercise the wrong database.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [string]$ConfigPath = (Get-AeroLinkProductionSourceConfigPath),
        # Injectable so the contract suite can drive every outcome without a machine-wide configuration.
        # Return $null for "no configuration exists"; throw for "it exists and is unusable".
        [scriptblock]$ConfigReader,
        [scriptblock]$BindingAssertion
    )
    if (-not $ConfigReader -and -not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        return [pscustomobject]@{ Checked = $false; DelegateTo = $null; Reason = 'No dedicated production source is configured on this machine.' }
    }
    $configured = if ($ConfigReader) { & $ConfigReader } else { Get-AeroLinkProductionSourceConfig -ConfigPath $ConfigPath }
    if (-not $configured) {
        return [pscustomobject]@{ Checked = $false; DelegateTo = $null; Reason = 'No dedicated production source is configured on this machine.' }
    }

    $thisRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $dedicatedRoot = ([string]$configured.SourceRoot).TrimEnd('\', '/')
    if ($thisRoot.Equals($dedicatedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        # Being at the right path is a claim; the marker, the remote and the installation pointer are what
        # substantiate it. This throws with its own reason when they do not.
        if ($BindingAssertion) { & $BindingAssertion $thisRoot | Out-Null }
        else { Assert-AeroLinkDedicatedProductionSource -SourceRoot $thisRoot -RemoteName $configured.RemoteName | Out-Null }
        return [pscustomobject]@{
            Checked = $true; DelegateTo = $null
            Reason = "This checkout is the dedicated production source ($thisRoot), and its repository and installation binding still hold."
        }
    }
    # Before returning a path the caller is going to EXECUTE POWERSHELL FROM, prove it is what it claims.
    #
    # Delegation means the trusted parent runs a script out of a directory named by a configuration file. If
    # that target is validated only by the child, then a configuration edited to point somewhere else - or a
    # directory that was the production source and no longer is - gets to run first and validate itself
    # afterwards, which is not validation. So the repository, the dedicated marker and the installation
    # binding are checked here, by the caller that still has authority.
    #
    # Dedicated AND canonical, because they answer different questions and only one of them was being asked.
    #
    # Dedicated proves the marker, the origin and the installation binding: this checkout IS the production
    # source. Canonical proves what is IN it - clean main, no local-only commits, no divergence, nothing
    # modified, nothing untracked. A configured clone can hold a perfectly valid dedicated binding while
    # sitting on a feature branch with edited launcher scripts, and delegating to it means the trusted parent
    # executes that PowerShell before the child's canonical guard ever runs. Checking the child's own policy
    # after handing it control is not checking it.
    #
    # Not remote-currency: being cleanly BEHIND origin/main is canonical. That keeps this working offline,
    # which matters because the whole point of the cached-canonical path is that a machine which cannot reach
    # GitHub still starts production.
    $targetPosture = Get-AeroLinkProductionSourcePosture -SourceRoot $dedicatedRoot -RemoteName $configured.RemoteName
    if (-not $targetPosture.Dedicated -or -not $targetPosture.Canonical) {
        $detail = if (-not $targetPosture.Dedicated -and $targetPosture.BindingReason) { " $($targetPosture.BindingReason)" } else { " $($targetPosture.Reason)" }
        throw @"
AeroLink HOME production refused: the configured production source does not prove it is one.

  Running from:        $thisRoot
  Production source:   $dedicatedRoot
  Configured in:       $($configured.ConfigPath)
 $detail

Nothing was delegated to and nothing was started - a path named by a configuration file does not get to run
before it has been shown to be the dedicated AeroLink production source. Re-create or repair it with
CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat.
"@
    }

    # Not a refusal: a redirection.
    #
    # Refusing here was safe and wrong. #881's operating-mode contract names START_AEROLINK_PRODUCTION.bat as
    # the HOME production entry point and #783 pins these root paths, and the desktop shortcuts, scheduled
    # tasks and other-machine references pointing at the old checkout's copy cannot be enumerated by
    # searching this repository - so printing a different path at them is not a compatibility transition, it
    # is the stable front door going dark. The caller re-execs the dedicated source's own front door with a
    # one-shot marker, which keeps the muscle memory working AND keeps production off this checkout.
    return [pscustomobject]@{
        Checked = $false
        DelegateTo = $dedicatedRoot
        Reason = "This checkout ($thisRoot) is not the dedicated production source ($dedicatedRoot), configured in $($configured.ConfigPath)."
    }
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
    if ($isRepository) {
        # Adopting an existing checkout is convenient and was too trusting: whatever repository happened to
        # be sitting at this path got the dedicated marker, and its canonicality was then judged against its
        # OWN origin/main. Prove it is the repository we mean before blessing it.
        $existingOrigin = Invoke-AeroLinkBootstrapGitQuiet -RepositoryRoot $SourceRoot -GitArguments @('remote', 'get-url', $RemoteName)
        $existingOrigin = if ($existingOrigin) { $existingOrigin.Trim() } else { '' }
        if (-not (Test-AeroLinkSameRemote -Left $existingOrigin -Right $OriginUrl)) {
            throw "Refusing to adopt ${SourceRoot} as the dedicated production source: its '$RemoteName' remote is '$existingOrigin', not '$OriginUrl'. Nothing was changed."
        }
    }
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
    # The marker records WHAT this source is bound to, not merely that it exists. Both bindings are checked
    # on every use: the repository it was created from, so a wrong-origin checkout cannot be blessed and then
    # judged canonical against its own remote; and the installation it belongs to, so a source cannot stay
    # canonical while its data pointer is moved somewhere else.
    [pscustomobject]@{
        dedicatedProductionSource = $true
        originUrl                 = $OriginUrl
        installationRoot          = $InstallationRoot
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

            -InspectOnly separates deciding from acting. A fetch writes only remote-tracking refs, which no
            running process reads; the fast-forward rewrites the working tree the production runtime is
            executing out of. A caller with a runtime up must therefore inspect first, stop that runtime, and
            only then advance - otherwise a timer swaps assemblies, migrations and client bundles under a live
            process, which is a worse outage than the stale revision it was fixing. -AdvanceToSha closes the
            gap between the two phases: the advance happens only if origin/main still points where the
            inspection said it did, so a push landing in between cannot silently redirect the update.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [string]$RemoteName = 'origin',
        [int]$FetchTimeoutSeconds = 45,
        [switch]$AllowNonDedicated,
        [scriptblock]$FetchOverride,
        # Fetch and decide, but never touch the working tree.
        [switch]$InspectOnly,
        # Advance only if origin/main is still this exact revision.
        [string]$AdvanceToSha
    )

    if ($InspectOnly -and $AdvanceToSha) {
        throw 'Update-AeroLinkProductionSource: -InspectOnly decides and -AdvanceToSha acts. Asking for both is a contradiction; nothing was changed.'
    }

    # The COMPLETE binding, before anything is fetched or fast-forwarded.
    #
    # Checking only that the marker file exists let a clean-but-misbound source be advanced and then rejected
    # afterwards: Git posture can be Canonical while Dedicated is false, because the marker is malformed, the
    # origin is not AeroLink, or the installation pointer has moved. Mutating a working tree and reporting the
    # problem after the fact is the wrong order for a source that runs the canonical database.
    if (-not $AllowNonDedicated) {
        $binding = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
        if (-not $binding.Dedicated) {
            $detail = if ($binding.BindingReason) { " $($binding.BindingReason)" } else { '' }
            throw "AeroLink production source update refused: $SourceRoot is not a dedicated AeroLink production source.$detail Nothing was fetched and nothing was changed."
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
            Action = 'Refused'; Canonical = $false; HeadSha = $before.HeadSha; TargetSha = $null
            RemoteReachable = $null; Reason = $preFetch.Reason
        }
    }

    $reached = if ($FetchOverride) { [bool](& $FetchOverride $SourceRoot) } else { Sync-AeroLinkRemoteRefs -RepositoryRoot $SourceRoot -RemoteName $RemoteName -TimeoutSeconds $FetchTimeoutSeconds }
    $posture = Get-AeroLinkRepositoryPosture -RepositoryRoot $SourceRoot -RemoteName $RemoteName

    if (-not $reached) {
        $offline = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
        if (-not $offline.Canonical) {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha; TargetSha = $posture.RemoteMainSha
                RemoteReachable = $false; Reason = $offline.Reason
            }
        }
        if ($null -eq $posture.RemoteMainSha) {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha; TargetSha = $null
                RemoteReachable = $false; Reason = "GitHub is unavailable and no cached origin/main exists, so the canonical source posture cannot be verified for main @ $($posture.ShortSha)."
            }
        }
        return [pscustomobject]@{
            Action = 'CachedCanonical'; Canonical = $true; HeadSha = $posture.HeadSha; TargetSha = $posture.RemoteMainSha
            RemoteReachable = $false; Reason = "GitHub is unavailable. Running the previously verified cached clean main @ $($posture.ShortSha); the latest remote revision could not be verified."
        }
    }

    if ($null -eq $posture.RemoteMainSha) {
        return [pscustomobject]@{
            Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha; TargetSha = $null
            RemoteReachable = $true; Reason = "The remote was reachable but $RemoteName/main was not found. Nothing was changed."
        }
    }

    if ($posture.Relationship -eq 'Behind') {
        if ($InspectOnly) {
            # Decided, nothing touched. The working tree is exactly as the caller's runtime left it.
            return [pscustomobject]@{
                Action = 'UpdateAvailable'; Canonical = $true; HeadSha = $posture.HeadSha; TargetSha = $posture.RemoteMainSha
                RemoteReachable = $true; Reason = "$RemoteName/main has moved to $($posture.ShortRemoteMainSha); the production source is still $($posture.ShortSha). Nothing was changed."
            }
        }
        if ($AdvanceToSha -and $posture.RemoteMainSha -ne $AdvanceToSha) {
            $short = $AdvanceToSha.Substring(0, [Math]::Min(8, $AdvanceToSha.Length))
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha; TargetSha = $posture.RemoteMainSha
                RemoteReachable = $true; Reason = "$RemoteName/main moved between inspection and advance: the decision was made against $short but the remote is now $($posture.ShortRemoteMainSha). Nothing was changed; the next pass will re-decide."
            }
        }
        try { Invoke-AeroLinkBootstrapGit -RepositoryRoot $SourceRoot -GitArguments @('merge', '--ff-only', "$RemoteName/main") | Out-Null }
        catch {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha; TargetSha = $posture.RemoteMainSha
                RemoteReachable = $true; Reason = "The strict fast-forward of the production source was refused by Git and nothing was changed: $($_.Exception.Message)"
            }
        }
        $updated = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
        if (-not $updated.Canonical) {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = if ($updated.Posture) { $updated.Posture.HeadSha } else { $null }
                TargetSha = $posture.RemoteMainSha; RemoteReachable = $true; Reason = $updated.Reason
            }
        }
        if ($updated.Posture.HeadSha -ne $posture.RemoteMainSha) {
            return [pscustomobject]@{
                Action = 'Refused'; Canonical = $false; HeadSha = $updated.Posture.HeadSha; TargetSha = $posture.RemoteMainSha
                RemoteReachable = $true; Reason = "The production source changed during the update: HEAD is $($updated.Posture.ShortSha) but the verified update target was $($posture.ShortRemoteMainSha). Inspect the source; no automatic action was taken."
            }
        }
        return [pscustomobject]@{
            Action = 'Updated'; Canonical = $true; HeadSha = $updated.Posture.HeadSha; TargetSha = $posture.RemoteMainSha
            RemoteReachable = $true; Reason = "The production source was strictly fast-forwarded to $RemoteName/main @ $($updated.Posture.ShortSha) and revalidated."
        }
    }

    $current = Get-AeroLinkProductionSourcePosture -SourceRoot $SourceRoot -RemoteName $RemoteName
    if (-not $current.Canonical) {
        return [pscustomobject]@{
            Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha; TargetSha = $posture.RemoteMainSha
            RemoteReachable = $true; Reason = $current.Reason
        }
    }
    if ($AdvanceToSha -and $posture.HeadSha -ne $AdvanceToSha) {
        # Already current, but not with what was decided: someone else advanced the source under us.
        $short = $AdvanceToSha.Substring(0, [Math]::Min(8, $AdvanceToSha.Length))
        return [pscustomobject]@{
            Action = 'Refused'; Canonical = $false; HeadSha = $posture.HeadSha; TargetSha = $posture.RemoteMainSha
            RemoteReachable = $true; Reason = "The production source is current at $($posture.ShortSha), but the advance was decided for $short. Something else moved the source; no automatic action was taken."
        }
    }
    return [pscustomobject]@{
        Action = 'AlreadyCurrent'; Canonical = $true; HeadSha = $posture.HeadSha; TargetSha = $posture.RemoteMainSha
        RemoteReachable = $true; Reason = "The production source is current with $RemoteName/main @ $($posture.ShortSha)."
    }
}

Export-ModuleMember -Function @(
    'Get-AeroLinkProductionSourceConfigPath',
    'Get-AeroLinkProductionSourceMarkerPath',
    'Read-AeroLinkProductionSourceMarker',
    'Test-AeroLinkSameRemote',
    'Get-AeroLinkProductionSourceConfig',
    'Get-AeroLinkProductionSourcePosture',
    'Assert-AeroLinkDedicatedProductionSource',
    'Assert-AeroLinkRunningFromProductionSource',
    'Initialize-AeroLinkProductionSource',
    'Update-AeroLinkProductionSource'
)
