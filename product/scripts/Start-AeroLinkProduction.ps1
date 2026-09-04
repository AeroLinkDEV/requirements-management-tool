[CmdletBinding()]
param(
    [switch]$DoNotOpenBrowser,
    [switch]$SkipClientBuild,
    [switch]$Shared,
    [string]$NotificationBaseUrl,
    # Run HOME production from THIS checkout even though a dedicated production source is configured
    # elsewhere. Deliberately awkward: the only supported reasons are qualifying the launcher itself and
    # operating a machine where the dedicated source is temporarily unavailable.
    [switch]$AllowNonDedicatedSource
)

# Starts AeroLink the way a demonstration or an on-premises workstation should run it: the client compiled, and
# served by the API from one origin on one port.
#
# `Start-AeroLink.ps1` runs the Vite dev server, which is right for development and wrong for anything watched
# by other people. It recompiles on every keystroke, prints its own diagnostics into the page, serves unbundled
# modules, and needs a second process supervised on a second port with a CORS policy joining them.
#
# This is the production *build*, run with local demonstration configuration: PostgreSQL, the FMSLIVE dataset
# and the demonstration identities, over plain HTTP. It is not a production *deployment* — TLS, certificates,
# secret management, reverse-proxy topology and off-device backups are organization-specific work recorded in
# SECURITY_AND_IDENTITY_MODEL.md and not done here.
#
# `-Shared` lets colleagues on the same network open it from their own machines. Off by default, because the
# same run also prints a known administrator password and loads demonstration data, and a launcher somebody
# double-clicks out of habit should not put that on an office network without being asked to.
#
# The probing, waiting, port reclaiming and PostgreSQL plumbing live in AeroLinkLaunch.ps1, shared with the
# development launcher.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AeroLinkPrerequisites.ps1')
. (Join-Path $PSScriptRoot 'AeroLinkLaunch.ps1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkBootstrap.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkUpgrade.psm1') -Force

$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $productRoot '..')).Path
$apiProject = Join-Path $productRoot 'src\AeroLink.Api\AeroLink.Api.csproj'
# Process ownership is judged on the project DIRECTORY, not the .csproj: the process that holds 5080 is the
# apphost under bin\, whose command line never contains the .csproj path. It is checkout-specific, which is
# what keeps another checkout's AeroLink from being treated as ours to stop (#881).
$apiProjectDirectory = Split-Path $apiProject -Parent
$clientRoot = Join-Path $productRoot 'client'
$distRoot = Join-Path $clientRoot 'dist'
# The persistent installation this source belongs to. On HOME that is the canonical installation, whether
# this source is the development checkout or the dedicated production clone beside it (#881).
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
$logs = $installation.Logs
$launcherMode = 'HOME-PRODUCTION'

# If this machine has a dedicated production source, HOME production runs from THAT checkout - not from
# whichever one this launcher happens to live in.
#
# The canonical-main gate below already refuses a dirty or feature-branch development checkout, so this is
# not about running unreviewed code. It is about which working tree the resulting long-lived process is
# executing out of. A development checkout that is momentarily on clean main passes every gate, serves the
# demo happily, and is then one `git checkout` away from having its assemblies and client bundle swapped
# underneath it - which is the 2026-09-03 failure with a different first step.
#
# Checked before the re-entry bootstrap, so the wrong checkout is never fetched or fast-forwarded on the way
# to being refused.
if (-not $AllowNonDedicatedSource) {
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1') -Force
    # Throws on a configuration that exists and is unusable, or a binding that has broken. Returns a
    # DelegateTo when this simply is not the dedicated checkout.
    $sourceDecision = Assert-AeroLinkRunningFromProductionSource -RepositoryRoot $repositoryRoot
    if ($sourceDecision.DelegateTo) {
        # The stable front door stays a front door. Desktop shortcuts, scheduled tasks and references from
        # other machines point at THIS BAT, and none of them can be enumerated from inside the repository,
        # so refusing with a different path to type would take the documented entry point away rather than
        # move it. Re-exec the dedicated source's own launcher instead, and let every gate run there.
        #
        # AEROLINK_PRODUCTION_DELEGATED is the recursion guard, and it is one-shot: it exists only for the
        # duration of the child process. If the child still is not the dedicated source - a configuration
        # pointing somewhere that is not what it claims, or two checkouts pointing at each other - it
        # refuses instead of bouncing, because a launcher that loops is worse than one that stops.
        $delegateScript = Join-Path $sourceDecision.DelegateTo 'product\scripts\Start-AeroLinkProduction.ps1'
        if ($env:AEROLINK_PRODUCTION_DELEGATED -eq '1') {
            throw @"
AeroLink HOME production refused: delegation did not reach the dedicated production source.

  $($sourceDecision.Reason)

This launch was already delegated once, so it is not being delegated again. Check the production-source
configuration with CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat Status. Nothing was started or changed.
"@
        }
        if (-not (Test-Path -LiteralPath $delegateScript -PathType Leaf)) {
            throw @"
AeroLink HOME production refused: the configured dedicated production source has no launcher.

  $($sourceDecision.Reason)
  Expected:            $delegateScript

Re-create it with CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat Install, or correct the configuration. Nothing
was started and nothing was changed.
"@
        }
        Write-Host 'This checkout is not the dedicated production source.' -ForegroundColor Yellow
        Write-Host "      Running from:      $repositoryRoot" -ForegroundColor DarkGray
        Write-Host "      Production source: $($sourceDecision.DelegateTo)" -ForegroundColor DarkGray
        Write-Host '      Starting HOME production from the production source instead...' -ForegroundColor Cyan
        $forwarded = @(Get-AeroLinkBootstrapScriptArguments $PSBoundParameters)
        $previousDelegated = $env:AEROLINK_PRODUCTION_DELEGATED
        try {
            $env:AEROLINK_PRODUCTION_DELEGATED = '1'
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $delegateScript @forwarded
            exit $LASTEXITCODE
        }
        finally { $env:AEROLINK_PRODUCTION_DELEGATED = $previousDelegated }
    }
}

# Source posture first, before any prerequisite, build, or PostgreSQL start. The canonical HOME database must
# only ever be exercised by a clean, current main — clean also means no untracked, non-ignored source, which
# merged main does not attest to; anything else is refused here, and Git is never mutated to make an unsafe
# posture go away.
#
# Remote Demo inherits this policy whenever it invokes this launcher. A healthy already-running HOME process
# from an older source revision is a separate gap, deferred to the later #881 runtime-identity /
# stale-process slice.
#
# The re-entry identity must list every launcher implementation file already loaded into memory before this
# call (or invoked on the way here): the running script, everything it dot-sourced or imported, the cmd/bat
# entry chain, and the bootstrap module itself. A fast-forward that changes any of them must restart the
# launch from the updated files rather than continue half-old/half-new.
#
# -PreAdvanceAction is the ordering that makes this safe. The bootstrap may fast-forward THIS working tree,
# and on HOME there can already be a production AeroLink serving the public demo out of it. Advancing first
# would replace its assemblies, migrations and client bundle while it answers requests. So anything this
# repository can positively attribute as its own on 5080 is stopped in the moment before the tree moves;
# a listener that cannot be attributed is left alone, and the ordinary disposition check below still refuses
# to start over it.
#
# It fails CLOSED. Logging that the stop did not work and letting the fast-forward proceed is the same outcome
# as not having the hook: the tree is rewritten under a process that is still executing it. A stop that fails,
# or a listener whose ownership cannot be read, blocks the advance - the source stays where it is, which is a
# state the machine already runs in perfectly well.
#
# It also quiesces the owned tunnel, not only port 5080. Leaving the public URL forwarding at a port whose
# process is about to be replaced publishes whatever takes that port next.
#
# AEROLINK_TUNNEL_OWED carries the obligation across the bootstrap's re-entry.
#
# A source update that changes one of the launcher files deliberately re-execs a fresh child and exits on
# Action='Reentered' - which is BEFORE the restoration block at the end of this script. The child starts with
# an empty obligation and no way to know its parent had taken a public tunnel down, so a perfectly successful
# update of any commit touching this file, AeroLinkBootstrap.psm1 or the other launcher files would complete,
# start production, and leave the protected endpoint dark. An environment variable is the right carrier here
# because the child is a new process and inherits it; the child clears it, so it is one-shot.
$script:preAdvanceStopPerformed = $false
$script:tunnelWasRunning = ($env:AEROLINK_TUNNEL_OWED -eq '1')
$env:AEROLINK_TUNNEL_OWED = $null
$script:demoConfig = $null
$stopOwnedProductionRuntime = {
    param($Root, $Posture)
    Write-Host '      A source advance is due; quiescing the production stack that is executing out of this tree first.' -ForegroundColor Yellow
    # No -ErrorAction SilentlyContinue: a remote-demo module that will not load is not the same as a machine
    # without a remote demo, and swallowing the difference is how a tunnel survives a transition.
    Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force

    # ABSENT is not the same as UNREADABLE.
    #
    # No configuration means this machine has no remote demo, and there is nothing to tear down. A
    # configuration that EXISTS but is malformed means a tunnel may well have been started while the file was
    # valid and is still running now - and treating that as "no tunnel" skips the teardown, advances the
    # source, and leaves the public endpoint forwarding to a port whose process has just been replaced. So
    # only a definitively missing file takes the no-tunnel path; a read or validation failure stops the
    # advance.
    $demoConfigPath = Get-AeroLinkRemoteDemoConfigPath
    if (Test-Path -LiteralPath $demoConfigPath -PathType Leaf) {
        try { $script:demoConfig = Get-AeroLinkRemoteDemoConfig -ConfigPath $demoConfigPath }
        catch {
            throw "The production source cannot be advanced: this machine has a remote-demo configuration at $demoConfigPath that could not be read ($($_.Exception.Message)). A tunnel started while it was valid may still be publishing port 5080, and advancing without proving it is down would leave the public endpoint in front of a replaced runtime. Nothing was stopped and nothing was changed."
        }
        # Only the AeroLink-owned tunnel, and it must be provably down. Stop-AeroLinkRemoteDemo refuses on a
        # mismatched ngrok rather than killing it, and that refusal stops the advance too.
        #
        # The obligation is recorded INSIDE the helper the instant the stop succeeds, rather than read from
        # its return value: fail-closed enumeration means the post-stop proof can throw after a tunnel has
        # actually come down, and a return value never arrives for that case.
        $obligation = New-AeroLinkTransitionObligation
        try {
            Assert-AeroLinkOwnedTunnelStopped -Config $script:demoConfig -Obligation $obligation | Out-Null
        }
        finally {
            $script:tunnelWasRunning = [bool]$obligation.TunnelWasRunning
            # Compensation is owed from the first teardown step that SUCCEEDS, not from the last one. The
            # tunnel can come down and the listener stop below can then throw on an ownership it cannot
            # establish; marking the obligation only afterwards meant the outer handler rethrew without
            # compensating, with the public endpoint already dark.
            if ($obligation.TeardownBegan) { $script:preAdvanceStopPerformed = $true }
            # Inherited by the re-entry child the bootstrap may spawn immediately after this.
            if ($script:tunnelWasRunning) { $env:AEROLINK_TUNNEL_OWED = '1' }
        }
    }

    Stop-AeroLinkOwnedListener -Port 5080 -OwnershipFragments @($apiProjectDirectory) | Out-Null
    $script:preAdvanceStopPerformed = $true
}.GetNewClosure()
try {
    $bootstrapResult = Invoke-AeroLinkSourceBootstrap -Mode HomeCanonical `
        -RepositoryRoot $repositoryRoot `
        -PreAdvanceAction $stopOwnedProductionRuntime `
        -CurrentScriptPath $PSCommandPath `
        -ScriptArguments (Get-AeroLinkBootstrapScriptArguments $PSBoundParameters) `
        -LauncherFiles @(
            'START_AEROLINK_PRODUCTION.bat',
            'product\scripts\launch.cmd',
            'product\scripts\Start-AeroLinkProduction.ps1',
            'product\scripts\AeroLinkPrerequisites.ps1',
            'product\scripts\AeroLinkLaunch.ps1',
            'product\scripts\AeroLinkNativeRunner.psm1',
            'product\scripts\AeroLinkBootstrap.psm1',
            'product\scripts\AeroLinkInstallation.psm1',
            'product\scripts\AeroLinkRuntimeIdentity.psm1',
            'product\scripts\AeroLinkUpgrade.psm1'
        )
}
catch {
    # Compensation, for the one window where failing is not enough.
    #
    # If the advance failed BEFORE the stop, nothing was running and nothing is owed: rethrow. If it failed
    # AFTER the stop - a fast-forward Git refused, most likely - production is already down because this
    # launcher took it down, and stopping here would leave the machine off to report an update that did not
    # happen. The revision on disk is untouched and was canonical a moment ago, so re-prove that and carry on
    # with it, saying plainly that the update did not occur. This is the same invariant the scheduled
    # reconciliation pass already has.
    if (-not $script:preAdvanceStopPerformed) { throw }
    Write-Host ''
    Write-Host 'THE SOURCE UPDATE DID NOT HAPPEN' -ForegroundColor Yellow
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    $onDisk = Get-AeroLinkProductionSourcePosture -SourceRoot $repositoryRoot
    if (-not $onDisk.Canonical) {
        throw "The source update failed after the production runtime was stopped, and the revision on disk is not canonical either: $($onDisk.Reason) AeroLink was not restarted. Nothing was changed."
    }
    Write-Host "Starting production on the revision already on disk: main @ $($onDisk.Posture.ShortSha)." -ForegroundColor Yellow
    Write-Host 'Nothing was left running from before, and no persistent data was changed.' -ForegroundColor Yellow
    $bootstrapResult = [pscustomobject]@{ Action = 'AdvanceRefused'; HeadSha = $onDisk.Posture.HeadSha; ExitCode = 0 }
}
if ($bootstrapResult.Action -eq 'Reentered') { exit $bootstrapResult.ExitCode }

# The verified source identity this launch runs. HOME canonical refuses a dirty tree outright, so this is
# always a bare commit SHA here — and it is the value the runtime publishes and remote demo checks before it
# will put a public tunnel in front of this process.
$sourceFingerprint = Get-AeroLinkSourceFingerprint -RepositoryRoot $repositoryRoot
$instance = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode HomeCanonical -EnsureInstanceId

# Reaching this machine from another one takes two changes, not one, and the second is the one nobody expects.
#
# Binding to 0.0.0.0 makes the socket accept connections from off this box. That alone is not enough: ASP.NET
# Core's host filtering compares the Host header against `AllowedHosts`, which appsettings.json sets to
# "localhost;127.0.0.1". So a colleague typing this machine's address reaches a server that is listening
# perfectly well and gets back a bare HTTP 400 with no body — which reads exactly like a binding problem, and
# is not one. Both settings move together or neither should.
if ($Shared) {
    $bindUrl = 'http://0.0.0.0:5080'
    $allowedHosts = '*'
}
else {
    $bindUrl = 'http://127.0.0.1:5080'
    $allowedHosts = 'localhost;127.0.0.1'
}
# Everything this script checks for itself goes over loopback, whichever mode is in force: it is reachable in
# both, and it does not depend on which network this machine happens to be on today.
$url = 'http://127.0.0.1:5080'

New-Item -ItemType Directory -Path $logs -Force | Out-Null

function Get-AeroLinkLanAddress {
    # The IPv4 address a colleague would type. Physical adapters that are actually up, preferred over the
    # virtual ones Hyper-V, WSL, Docker and VPN clients leave behind — those have real-looking addresses that
    # nobody else on the network can route to, and handing one out sends someone off debugging their own machine.
    $candidates = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' })
    foreach ($candidate in $candidates) {
        $adapter = Get-NetAdapter -InterfaceIndex $candidate.InterfaceIndex -ErrorAction SilentlyContinue
        if ($adapter -and $adapter.Status -eq 'Up' -and -not $adapter.Virtual) { return $candidate.IPAddress }
    }
    if ($candidates.Count -gt 0) { return $candidates[0].IPAddress }
    return $null
}

function Test-AeroLinkFirewallRule {
    # Whether Windows will let those connections in at all. Kestrel binding to every interface is a decision
    # this process can make on its own; the firewall is not, and its default answer to an inbound connection on
    # port 5080 is to drop it silently. Reported rather than fixed: opening a port is an administrator's
    # decision about their machine, and a launcher that quietly edits firewall rules has overstepped.
    $rules = @(Get-NetFirewallRule -DisplayName 'AeroLink*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' })
    return $rules.Count -gt 0
}

function Resolve-AeroLinkNotificationBaseUrl([string]$Candidate) {
    if ([string]::IsNullOrWhiteSpace($Candidate)) { return $null }
    $uri = $null
    if (-not [uri]::TryCreate($Candidate.Trim(), [System.UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https') -or [string]::IsNullOrWhiteSpace($uri.Host) -or $uri.AbsolutePath -ne '/' -or -not [string]::IsNullOrEmpty($uri.UserInfo) -or -not [string]::IsNullOrEmpty($uri.Query) -or -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw 'NotificationBaseUrl must be an absolute http/https origin with no credentials, query, or fragment.'
    }
    return $uri.GetLeftPart([System.UriPartial]::Authority).TrimEnd('/')
}

$lan = $null
if ($Shared) {
    # Resolve before process creation: an address printed after startup is not enough when outbound mail
    # needs the very same reachable origin. Do not guess a loopback fallback for another person's message.
    $lan = Get-AeroLinkLanAddress
    if (-not $lan) { throw 'Shared mode requires a reachable LAN IPv4 address so notification links are not fabricated.' }
    $effectiveNotificationBaseUrl = "http://${lan}:5080"
}
else {
    $effectiveNotificationBaseUrl = Resolve-AeroLinkNotificationBaseUrl $NotificationBaseUrl
}

# Prerequisites first, before anything that takes minutes. Without this the launcher installed npm packages,
# compiled the client, started the API and waited two minutes for a health endpoint that could never answer,
# then reported "No .NET SDKs were found" — the right diagnosis, four minutes after it was knowable.
Write-Host '[0/4] Checking prerequisites...' -ForegroundColor Cyan
$dotnet = Resolve-AeroLinkDotnet
Assert-AeroLinkNode
Write-Host "      .NET SDK: $dotnet" -ForegroundColor Green

Write-Host '[1/4] Checking PostgreSQL...' -ForegroundColor Cyan
Assert-AeroLinkPostgres -ProductRoot $productRoot

# Ownership, mode and exact source identity, in that order, and the stop, before the canonical database is
# touched. A healthy production API from an older revision is stale; a development API answering here is a
# mode mismatch; and a process this repository does not own is a refusal with its PID, never a casualty.
#
# This runs ahead of the upgrade, not after the client build. Migrating the canonical database while an
# old-schema process still serves requests against it is the condition the clone-validation path exists to
# avoid, and the client build would have widened that window by tens of seconds.
Write-Host '      Checking what is already on 127.0.0.1:5080...' -ForegroundColor Cyan
$disposition = Resolve-AeroLinkRuntimeDisposition -Port 5080 -BaseUri $url `
    -ExpectedMode $launcherMode -ExpectedSourceIdentity $sourceFingerprint.Identity `
    -OwnershipFragments @($apiProjectDirectory)
if ($disposition.Disposition -eq 'Refuse') { throw $disposition.Detail }
$reuseExisting = ($disposition.Disposition -eq 'Reuse')
if ($reuseExisting) {
    Write-Host "      $($disposition.Detail)" -ForegroundColor Green
}
elseif ($disposition.Disposition -ne 'Free') {
    Write-Host "      $($disposition.Detail)" -ForegroundColor Yellow
    Stop-AeroLinkOwnedListener -Port 5080 -OwnershipFragments @($apiProjectDirectory) | Out-Null
}

# Upgrade posture before the client build, so a database this build cannot operate on costs seconds rather
# than a build plus a readiness timeout. HOME canonical is the same contract as development: a conflict is
# reported with its supported decisions and nothing is guessed; a deterministic upgrade is backed up,
# validated on an isolated restored copy, and only then applied.
#
# Skipped when a matching, ready API is being reused: that process migrated this database at its own startup,
# and asking anyway would run `dotnet run`, whose build cannot write over the assemblies the live process
# holds.
if ($reuseExisting) {
    Write-Host '      Database upgrade posture already established by the running AeroLink.' -ForegroundColor DarkGray
}
else {
    Write-Host '      Checking database upgrade posture...' -ForegroundColor Cyan
    $upgradePosture = Get-AeroLinkUpgradeAnalysis -ProductRoot $productRoot -DotnetPath $dotnet
    switch ($upgradePosture.Status) {
        'current' { Write-Host '      Database is current; no upgrade is pending.' -ForegroundColor Green }
        'upgrade-required' {
            Write-Host "      Upgrade pending: $(@($upgradePosture.Analysis.pendingEfMigrations).Count) schema migration(s), $(@($upgradePosture.Analysis.pendingSemanticUpgrades).Count) semantic upgrade(s)." -ForegroundColor Yellow
            $upgrade = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $productRoot -DotnetPath $dotnet
            if (-not $upgrade.Applied) {
                Write-Host ''
                Write-Host 'DATABASE UPGRADE NOT APPLIED' -ForegroundColor Red
                Write-Host $upgrade.Detail -ForegroundColor Red
                throw 'AeroLink was not started because the canonical database could not be safely upgraded.'
            }
            Write-Host "      $($upgrade.Detail)" -ForegroundColor Green
        }
        'conflict' {
            Write-AeroLinkUpgradeConflictReport -Analysis $upgradePosture.Analysis
            throw 'AeroLink was not started: the canonical database needs an explicit decision that AeroLink is not entitled to make.'
        }
        default {
            # Fail closed, as development does. The canonical database has even less business being exercised
            # by a build whose compatibility with it could not be established.
            Write-Host ''
            Write-Host 'DATABASE UPGRADE POSTURE COULD NOT BE ESTABLISHED' -ForegroundColor Red
            Write-Host $upgradePosture.Detail -ForegroundColor Red
            Write-Host 'No persistent data was changed.' -ForegroundColor Red
            throw "AeroLink was not started: this build's compatibility with the canonical database could not be established."
        }
    }
}

Write-Host '[2/4] Building the client...' -ForegroundColor Cyan
if ($SkipClientBuild) {
    if (-not (Test-Path (Join-Path $distRoot 'index.html'))) {
        throw "-SkipClientBuild was given but $distRoot holds no built client. Run without the switch once."
    }
    Write-Host '      Reusing the existing build.' -ForegroundColor Green
}
else {
    # Fingerprinted dependency refresh: npm ci runs only when package-lock.json changed since the last
    # successful preparation, or node_modules is missing. A failed refresh never records the fingerprint.
    Update-AeroLinkClientDependencies -ClientRoot $clientRoot -StateDirectory $installation.BootstrapState
    Push-Location $clientRoot
    try {
        # Type checking is part of `npm run build`, and on purpose: a build that compiles is the whole claim
        # this script exists to make good on.
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw 'The client build failed. AeroLink was not started.' }
    }
    finally { Pop-Location }
    Write-Host '      Client built.' -ForegroundColor Green
}

Write-Host '[3/4] Starting AeroLink...' -ForegroundColor Cyan
# The port decision and any stop already happened above, before the database was touched.

Write-Host '[4/4] Waiting for AeroLink to be ready...' -ForegroundColor Cyan
# Release, and --no-launch-profile so launchSettings.json cannot quietly substitute development configuration.
# Client:StaticFiles is named rather than discovered, so this serves the build made moments ago and no other.
#
# Readiness is /health/ready, not /health. Liveness answers "is the process up", which it is even when the
# database is unreachable — so waiting on it reports a working product over a dead database. Readiness opens a
# connection, which is the question an operator is actually asking.
if ($reuseExisting) {
    Write-Host '      Reusing the matching AeroLink already running on this port.' -ForegroundColor Green
}
else {
    # Runtime identity travels with the process, so a later launcher, remote-demo recovery, or an operator
    # can ask what source it is running rather than inferring it from a healthy port.
    $runtimeEnvironment = @{
        ASPNETCORE_ENVIRONMENT   = 'Development'
        Client__StaticFiles      = $distRoot
        AllowedHosts             = $allowedHosts
        Notifications__BaseUrl   = $effectiveNotificationBaseUrl
        Runtime__SourceSha       = $sourceFingerprint.Sha
        Runtime__SourceIdentity  = $sourceFingerprint.Identity
        Runtime__Mode            = $launcherMode
        Instance__Label          = $instance.Label
        Instance__Classification = $instance.Classification
        Instance__InstanceId     = $instance.InstanceId
    }
    if ($instance.SnapshotSourceLabel) { $runtimeEnvironment['Instance__SnapshotSourceLabel'] = $instance.SnapshotSourceLabel }
    if ($instance.SnapshotSourceSha) { $runtimeEnvironment['Instance__SnapshotSourceSha'] = $instance.SnapshotSourceSha }
    if ($instance.SnapshotCreatedAtUtc) { $runtimeEnvironment['Instance__SnapshotCreatedAtUtc'] = $instance.SnapshotCreatedAtUtc }
    if ($instance.SnapshotActivatedAtUtc) { $runtimeEnvironment['Instance__SnapshotActivatedAtUtc'] = $instance.SnapshotActivatedAtUtc }

    Start-AeroLinkService `
        -FilePath $dotnet `
        -ArgumentList "run --configuration Release --no-launch-profile --project `"$apiProject`" --urls `"$bindUrl`"" `
        -WorkingDirectory $repositoryRoot `
        -StandardOutput (Join-Path $logs 'production.stdout.log') `
        -StandardError (Join-Path $logs 'production.stderr.log') `
        -ReadyUri "$url/health/ready" `
        -ServiceName 'AeroLink' `
        -Environment $runtimeEnvironment
}

# The document itself, because a ready API that serves no client is the failure this script was written to
# prevent: the previous launcher reported success while the site behind it was unusable.
$document = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
if ($document.Content -notmatch '/assets/index-[\w-]+\.js') {
    throw "AeroLink is running but is not serving the built client from $distRoot. Check Client:StaticFiles."
}
Write-Host '      Ready, and serving the built client.' -ForegroundColor Green

if (-not $DoNotOpenBrowser) { Start-Process $url }

Write-Host ''
Write-Host "AeroLink - $($instance.Label)" -ForegroundColor Green
Write-Host "Source: main @ $($sourceFingerprint.Sha.Substring(0, 8))"
Write-Host "Production source: $repositoryRoot"
Write-Host "Website and API: $url  (one origin, production build)"
Write-Host "Sign in: admin / AeroLink!2026"
Write-Host "Logs: $logs"
if ($effectiveNotificationBaseUrl) {
    Write-Host "Notification link origin: $effectiveNotificationBaseUrl" -ForegroundColor DarkGray
}
else {
    Write-Host 'Notification link origin: not configured (mail remains truthful but omits direct links).' -ForegroundColor DarkGray
}
Write-Host 'SMTP diagnostics: configure Notifications__Smtp__Host (and optional port/TLS/account settings) outside source control.' -ForegroundColor DarkGray

if ($Shared) {
    Write-Host ''
    if ($lan) {
        Write-Host 'Other people on this network can open AeroLink here:' -ForegroundColor Cyan
        Write-Host "      http://${lan}:5080" -ForegroundColor Cyan
    }
    else {
        Write-Host 'Shared mode is on, but this machine has no network address to share. Check that it is' -ForegroundColor Yellow
        Write-Host 'connected to a network, then run this launcher again.' -ForegroundColor Yellow
    }
    if (-not (Test-AeroLinkFirewallRule)) {
        Write-Host ''
        Write-Host 'Windows Firewall will block those connections until port 5080 is allowed in. Nothing here' -ForegroundColor Yellow
        Write-Host 'can do that for you - it needs an administrator. Open PowerShell as Administrator, once:' -ForegroundColor Yellow
        Write-Host '      New-NetFirewallRule -DisplayName "AeroLink on this network" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5080 -Profile Private,Domain' -ForegroundColor Gray
    }
}
else {
    Write-Host ''
    Write-Host 'Only this machine can reach it. To let colleagues on the same network open it, use' -ForegroundColor DarkGray
    Write-Host 'START_AEROLINK_SHARED.bat instead.' -ForegroundColor DarkGray
}

# Give back the protected tunnel this launcher took down.
#
# The source transition is one cross-mode operation, not two independent ones. This launcher stops the owned
# tunnel before advancing the working tree, which is correct - but it used to start only the API and client
# afterwards, so an operator running the documented production BAT while the remote demo was live would
# update successfully and leave colleagues' protected endpoint dark indefinitely. The 30-minute reconciler
# would not notice: it sees the source already current and does nothing.
#
# Strictly restoration, never creation. It runs only when an owned tunnel was observed running before the
# teardown, so a machine that had no demo up does not acquire a public endpoint from a launcher nobody asked
# to publish anything. Start-AeroLinkRemoteDemo re-proves the 401 edge contract before declaring it ready,
# and a failure here is reported rather than thrown: local production is up and working, and taking it down
# again because the tunnel did not come back would be the wrong trade.
#
# AEROLINK_TUNNEL_RESTORE is a one-shot guard. Start-AeroLinkRemoteDemo will invoke the production launcher
# if the local API is not ready and matching - normally it is, because this script just started and proved
# it - and a nested launch that tried to restore the tunnel again would be a loop.
if ($script:tunnelWasRunning -and $script:demoConfig -and $env:AEROLINK_TUNNEL_RESTORE -ne '1') {
    Write-Host ''
    Write-Host 'Restoring the protected remote demo this launcher took down for the source update...' -ForegroundColor Cyan
    $previousTunnelRestore = $env:AEROLINK_TUNNEL_RESTORE
    try {
        $env:AEROLINK_TUNNEL_RESTORE = '1'
        Start-AeroLinkRemoteDemo -Config $script:demoConfig | Out-Null
        Write-Host '      The protected public endpoint is back, and its 401 contract was re-proved.' -ForegroundColor Green
    }
    catch {
        Write-Host '      THE PROTECTED REMOTE DEMO DID NOT COME BACK' -ForegroundColor Red
        Write-Host "      $($_.Exception.Message)" -ForegroundColor Red
        Write-Host '      Local production is running. Restore the public endpoint with START_AEROLINK_REMOTE_DEMO.bat.' -ForegroundColor Yellow
    }
    finally { $env:AEROLINK_TUNNEL_RESTORE = $previousTunnelRestore }
}

Write-Host ''
Write-Host 'This is the production build with local demonstration configuration. It is not a production' -ForegroundColor DarkGray
Write-Host 'deployment: no TLS, demonstration credentials, and demonstration data are all enabled.' -ForegroundColor DarkGray
if ($Shared) {
    Write-Host 'Shared over plain HTTP, so anything typed into it crosses the office network unencrypted, and' -ForegroundColor DarkGray
    Write-Host 'anybody who reaches it can sign in with the password printed above.' -ForegroundColor DarkGray
}
