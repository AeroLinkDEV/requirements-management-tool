[CmdletBinding()]
param(
    [switch]$DoNotOpenBrowser,
    # Run development against an installation declared HOME CANONICAL. Deliberately awkward, and only for
    # qualifying this launcher against a disposable HOME-classified installation.
    [switch]$AllowHomeCanonicalDatabase
)

# Starts AeroLink for development: the API on 5080 and the Vite dev server on 5173, each supervised separately.
#
# For anything anybody else is going to look at, use Start-AeroLinkProduction.ps1 instead — this one recompiles
# on every keystroke and serves unbundled modules.
#
# The probing, waiting, port reclaiming and PostgreSQL plumbing live in AeroLinkLaunch.ps1, shared with the
# production launcher.

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
$installation = Get-AeroLinkInstallationPaths -ProductRoot $productRoot
$logs = $installation.Logs
$apiUrl = 'http://127.0.0.1:5080'
$websiteUrl = 'http://127.0.0.1:5173'
$launcherMode = 'LOCAL-DEV'

# Source posture before anything else. Development mode preserves deliberate local work — feature branches,
# dirt, local-only commits, untracked files — and only fast-forwards a clean main; it never polices the
# checkout.
#
# The re-entry identity must list every launcher implementation file already loaded into memory before this
# call (or invoked on the way here): the running script, everything it dot-sourced or imported, the cmd/bat
# entry chain, and the bootstrap module itself. A fast-forward that changes any of them must restart the
# launch from the updated files rather than continue half-old/half-new.
$bootstrapResult = Invoke-AeroLinkSourceBootstrap -Mode Development `
    -RepositoryRoot $repositoryRoot `
    -CurrentScriptPath $PSCommandPath `
    -ScriptArguments (Get-AeroLinkBootstrapScriptArguments $PSBoundParameters) `
    -LauncherFiles @(
        'START_AEROLINK.bat',
        'product\scripts\launch.cmd',
        'product\scripts\Start-AeroLink.ps1',
        'product\scripts\AeroLinkPrerequisites.ps1',
        'product\scripts\AeroLinkLaunch.ps1',
        'product\scripts\AeroLinkNativeRunner.psm1',
        'product\scripts\AeroLinkBootstrap.psm1',
        'product\scripts\AeroLinkInstallation.psm1',
        'product\scripts\AeroLinkRuntimeIdentity.psm1'
    )
if ($bootstrapResult.Action -eq 'Reentered') { exit $bootstrapResult.ExitCode }

# The source identity this launch runs, computed AFTER any update so it describes the files that will
# actually execute. For a dirty development tree it folds in a bounded worktree fingerprint, because a SHA
# says nothing about uncommitted bytes and claiming otherwise is how a stale process survives an edit.
$sourceFingerprint = Get-AeroLinkSourceFingerprint -RepositoryRoot $repositoryRoot
$instance = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode Development -EnsureInstanceId

# The development launcher must not be pointed at the HOME canonical database.
#
# The three supported modes are not three moods: LOCAL DEV deliberately permits feature branches, dirty
# worktrees and half-finished migrations, and that is safe against a work-laptop database it is allowed to
# ruin. HOME CANONICAL is the one that carries real controlled history. On the HOME machine the development
# checkout is the installation that owns the canonical product/.local, so without this the whole stronger
# HOME source policy could be bypassed by double-clicking the other BAT - the same class of mistake #881
# exists to remove, reached through a different door.
#
# The classification is what decides, not the hostname: an installation says what it is, and a machine name
# is not a fact about a database.
if ($instance.Classification -eq 'HomeCanonical' -and -not $AllowHomeCanonicalDatabase) {
    throw @"
AeroLink development start refused: this installation is declared HOME CANONICAL.

  Installation: $($installation.InstallationRoot)
  Instance:     $($instance.Label) ($($instance.Classification))

The development launcher permits feature branches and an uncommitted worktree, which is safe against a
work-laptop database and not against the canonical one. Use START_AEROLINK_PRODUCTION.bat from the dedicated
production source to run this installation, or point development at its own installation.

Nothing was started and nothing was changed. -AllowHomeCanonicalDatabase exists only to qualify this
launcher against a disposable HOME-classified installation.
"@
}

$runtimeEnvironment = @{
    Runtime__SourceSha        = $sourceFingerprint.Sha
    Runtime__SourceIdentity   = $sourceFingerprint.Identity
    Runtime__Mode             = $launcherMode
    Instance__Label           = $instance.Label
    Instance__Classification  = $instance.Classification
    Instance__InstanceId      = $instance.InstanceId
}
if ($instance.SnapshotSourceLabel) { $runtimeEnvironment['Instance__SnapshotSourceLabel'] = $instance.SnapshotSourceLabel }
if ($instance.SnapshotSourceSha) { $runtimeEnvironment['Instance__SnapshotSourceSha'] = $instance.SnapshotSourceSha }
if ($instance.SnapshotCreatedAtUtc) { $runtimeEnvironment['Instance__SnapshotCreatedAtUtc'] = $instance.SnapshotCreatedAtUtc }
if ($instance.SnapshotActivatedAtUtc) { $runtimeEnvironment['Instance__SnapshotActivatedAtUtc'] = $instance.SnapshotActivatedAtUtc }

New-Item -ItemType Directory -Path $logs -Force | Out-Null

# Prerequisites first, before anything that takes minutes, so a missing SDK is reported in seconds rather than
# after an npm install and a two-minute wait on a health endpoint that could never answer.
Write-Host '[0/4] Checking prerequisites...' -ForegroundColor Cyan
$dotnet = Resolve-AeroLinkDotnet
Assert-AeroLinkNode
Write-Host "      .NET SDK: $dotnet" -ForegroundColor Green

# The Vite dev server needs node_modules; this is the same fingerprinted path the production launcher uses, so
# npm ci runs only when package-lock.json actually changed since the last successful preparation.
Update-AeroLinkClientDependencies -ClientRoot $clientRoot -StateDirectory $installation.BootstrapState

Write-Host '[1/4] Checking PostgreSQL...' -ForegroundColor Cyan
Assert-AeroLinkPostgres -ProductRoot $productRoot

# Decide what happens to whatever is already on 5080 BEFORE the database is touched.
#
# Readiness is necessary and not sufficient. This used to be "/health/ready answers 200, therefore reuse".
# Liveness alone was worse still — it reported a working product over a database that was not there — but
# readiness only proves the process can reach a database, not that it was built from the source about to be
# launched. #816 is the case: a healthy API from an older revision survived a repository update while the
# client moved forward, and this launcher declared success. Ownership, mode and exact source identity all
# have to agree before a process is reused, and a process this repository does not own is a refusal rather
# than a casualty.
#
# The stop happens here, ahead of the upgrade, and not where the API is started. Migrating a database that an
# older build still holds open — hosted workers, the notification outbox, integrity sweeps — is precisely the
# unsupervised mutation the clone-validation path exists to avoid, and stopping the stale process afterwards
# is too late.
Write-Host '      Checking what is already on 127.0.0.1:5080...' -ForegroundColor Cyan
$disposition = Resolve-AeroLinkRuntimeDisposition -Port 5080 -BaseUri $apiUrl `
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

# Upgrade posture before the API, not through it.
#
# #747 and #816 both ended the same way: dependencies installed, a client built, an API started, seventy-five
# seconds of readiness polling, and then a stack trace about persisted data that had been knowable the moment
# PostgreSQL accepted a connection. This asks first. A conflict stops here with the exact records and the
# supported decisions; a deterministic upgrade is backed up, validated on an isolated copy, and only then
# applied.
#
# Skipped entirely when a matching, ready API is being reused: that process migrated this database at its own
# startup, so there is nothing to find — and asking anyway would run `dotnet run`, whose build cannot write
# over the assemblies the live process holds, turning the preflight into an unexplained "posture could not be
# established" in the one case where nothing was wrong.
if ($reuseExisting) {
    Write-Host '      Database upgrade posture already established by the running AeroLink.' -ForegroundColor DarkGray
}
else {
    Write-Host '      Checking database upgrade posture...' -ForegroundColor Cyan
    $upgradePosture = Get-AeroLinkUpgradeAnalysis -ProductRoot $productRoot -DotnetPath $dotnet
    switch ($upgradePosture.Status) {
        'current' {
            Write-Host '      Database is current; no upgrade is pending.' -ForegroundColor Green
        }
        'upgrade-required' {
            $pendingMigrations = @($upgradePosture.Analysis.pendingEfMigrations).Count
            $pendingSemantic = @($upgradePosture.Analysis.pendingSemanticUpgrades).Count
            Write-Host "      Upgrade pending: $pendingMigrations schema migration(s), $pendingSemantic semantic upgrade(s)." -ForegroundColor Yellow
            $upgrade = Invoke-AeroLinkCloneValidatedUpgrade -ProductRoot $productRoot -DotnetPath $dotnet
            if (-not $upgrade.Applied) {
                Write-Host ''
                Write-Host 'DATABASE UPGRADE NOT APPLIED' -ForegroundColor Red
                Write-Host $upgrade.Detail -ForegroundColor Red
                throw 'AeroLink was not started because the local database could not be safely upgraded.'
            }
            Write-Host "      $($upgrade.Detail)" -ForegroundColor Green
        }
        'conflict' {
            Write-AeroLinkUpgradeConflictReport -Analysis $upgradePosture.Analysis
            throw 'AeroLink was not started: the local database needs an explicit decision that AeroLink is not entitled to make.'
        }
        default {
            # Fail closed. Being unable to establish the posture is itself a result, not permission to hand
            # the question back to API startup — which is exactly the #747/#816 behaviour #881 exists to end:
            # the launcher carried on, and the operator learned about the database from a readiness timeout
            # and a stack trace instead of from the check that had just failed.
            Write-Host ''
            Write-Host 'DATABASE UPGRADE POSTURE COULD NOT BE ESTABLISHED' -ForegroundColor Red
            Write-Host $upgradePosture.Detail -ForegroundColor Red
            Write-Host 'No persistent data was changed.' -ForegroundColor Red
            throw "AeroLink was not started: this build's compatibility with the local database could not be established."
        }
    }
}

Write-Host '[2/4] Checking AeroLink API...' -ForegroundColor Cyan
if ($reuseExisting) {
    Write-Host '      Reusing the matching AeroLink already running on this port.' -ForegroundColor Green
}
else {
    # Windows PowerShell flattens ArgumentList into a single command line, so paths containing spaces must be
    # quoted explicitly.
    Start-AeroLinkService `
        -FilePath $dotnet `
        -ArgumentList "run --project `"$apiProject`" --urls `"$apiUrl`"" `
        -WorkingDirectory $repositoryRoot `
        -StandardOutput (Join-Path $logs 'api.stdout.log') `
        -StandardError (Join-Path $logs 'api.stderr.log') `
        -ReadyUri "$apiUrl/health/ready" `
        -ServiceName 'AeroLink API' `
        -TailLines 20 `
        -Environment $runtimeEnvironment
}
Write-Host '      API ready on 127.0.0.1:5080, database reachable.' -ForegroundColor Green

Write-Host '[3/4] Checking website...' -ForegroundColor Cyan
# The Vite dev server publishes no identity of its own, so its freshness is judged by the source identity
# recorded at the last successful launch. #881 is explicit that the alternative to a real fingerprint is an
# honest restart, not a claim: if the source moved since this machine last launched successfully, the dev
# server is restarted rather than assumed to have kept up.
$launchStatePath = Join-Path $installation.BootstrapState 'last-launch.json'
$lastLaunch = $null
if (Test-Path -LiteralPath $launchStatePath -PathType Leaf) {
    try { $lastLaunch = Get-Content -LiteralPath $launchStatePath -Raw | ConvertFrom-Json } catch { $lastLaunch = $null }
}
$clientSourceMoved = (-not $lastLaunch) -or ([string]$lastLaunch.sourceIdentity -ne $sourceFingerprint.Identity)
if ($clientSourceMoved -and (Test-HttpEndpoint -Uri $websiteUrl -SuccessBelow 500)) {
    Write-Host '      Source changed since the last successful launch; restarting the development website.' -ForegroundColor Yellow
}
# SuccessBelow 500 here, unlike the readiness probes: this asks whether the dev server is up and serving at all,
# and any answer that is not a server error means it is. The API checks above want 2xx and nothing else.
if ($clientSourceMoved -or -not (Test-HttpEndpoint -Uri $websiteUrl -SuccessBelow 500)) {
    Clear-StaleAeroLinkPort -Port 5173 -ExpectedCommandFragments @($clientRoot)
    Start-AeroLinkService `
        -FilePath 'npm.cmd' `
        -ArgumentList @('run', 'dev', '--', '--host', '127.0.0.1', '--port', '5173', '--strictPort') `
        -WorkingDirectory $clientRoot `
        -StandardOutput (Join-Path $logs 'client.stdout.log') `
        -StandardError (Join-Path $logs 'client.stderr.log') `
        -ReadyUri $websiteUrl `
        -ServiceName 'AeroLink website' `
        -TimeoutSeconds 75 `
        -SuccessBelow 500 `
        -TailLines 20
}
Write-Host '      Website healthy on 127.0.0.1:5173.' -ForegroundColor Green

Write-Host '[4/4] Verifying authentication service...' -ForegroundColor Cyan
# Not Test-HttpEndpoint: 401 is the *expected* answer from an unauthenticated caller, so this has to tell a 401
# apart from a connection failure rather than folding both into false. Anything else means the endpoint is
# there but not behaving, which is worth failing on now instead of at the sign-in screen.
$authStatus = $null
try {
    $meResponse = Invoke-WebRequest -Uri "$apiUrl/api/auth/me" -UseBasicParsing -TimeoutSec 3
    $authStatus = [int]$meResponse.StatusCode
}
catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
        $authStatus = [int]$_.Exception.Response.StatusCode
    }
    else {
        throw "The API health check passed, but the authentication endpoint is unreachable: $($_.Exception.Message)"
    }
}
if ($authStatus -notin 200, 401) {
    throw "The authentication endpoint returned unexpected HTTP status $authStatus."
}
Write-Host '      Authentication endpoint is responding.' -ForegroundColor Green

if (-not $DoNotOpenBrowser) {
    Start-Process $websiteUrl
}

# Operational metadata, never proof. It answers "did the source move since AeroLink last worked here?" and
# nothing about whether the database is healthy, which is verified directly above every time.
if (-not (Test-Path -LiteralPath $installation.BootstrapState -PathType Container)) {
    New-Item -ItemType Directory -Path $installation.BootstrapState -Force | Out-Null
}
[pscustomobject]@{
    sourceSha       = $sourceFingerprint.Sha
    sourceIdentity  = $sourceFingerprint.Identity
    mode            = $launcherMode
    instanceLabel   = $instance.Label
    succeededAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath $launchStatePath -Encoding UTF8

Write-Host ''
Write-Host "AeroLink - $($instance.Label)" -ForegroundColor Green
Write-Host "Source: $($sourceFingerprint.Detail)"
Write-Host "Database: $($installation.PostgresData)"
Write-Host "Website: $websiteUrl"
Write-Host "Sign in: admin / AeroLink!2026"
Write-Host "Logs: $logs"
