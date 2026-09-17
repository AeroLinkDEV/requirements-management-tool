#Requires -Version 5.1
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionKernel.psm1') -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'AeroLinkTransitionAuthority.psm1') -DisableNameChecking
<#
    AeroLink protected remote-demo operator mode.

    This is a local demonstration convenience only, not a production deployment:
    AeroLink stays bound to 127.0.0.1:5080, PostgreSQL stays bound to
    127.0.0.1:54329, the ngrok agent opens the outbound HTTPS tunnel, and the
    ngrok Traffic Policy enforces the outer Basic Auth gate backed by an ngrok
    Vault secret. No secret value ever appears in this module, in the launch
    contract, in logs, or in scheduled-task arguments.

    The module reuses the existing production launcher, diagnostics, and process
    management implementations rather than duplicating AeroLink or PostgreSQL
    startup.
#>

$script:RemoteDemoTaskName = 'AeroLinkRemoteDemoRecovery'
$script:ReconcileTaskName = 'AeroLinkProductionSourceReconcile'

# Transition budgets, in one place, because they only mean anything RELATIVE TO EACH OTHER.
#
# They used to be three independent literals - 900 s for the launcher helper, 900 s for recovery, PT30M in
# two separately maintained task XML blocks - and a supported clone-validated upgrade simply costs more than
# that. Measured on the transition in #1043: a 417,902,860-byte archive with 51,573 manifest entries, whose
# verified backup finished about 9.5 minutes in, with isolated extraction and restore still progressing at
# about 16 minutes. The 900-second deadline expired in the middle of correct work, and because the deadline
# is what stops the runtime and tunnel, the machine was left with them down. Recovery then retried the same
# expensive work under the same deadline and reached the same point.
#
# THE SIZING OBSERVATION. One supported upgrade of this kind has been recorded end to end: the manual
# supported upgrade of 2026-09-14 took approximately 21 minutes 35 seconds (1295 s), covering verified
# backup, isolated restore, migrations, current-code read-only proof, launcher restart and tunnel
# restoration. That is ONE completed observation, not a p99, and the budgets below are derived from it with
# deliberate headroom rather than treated as a universal sizing rule.
#
#   supported upgrade deadline   2400 s   ~1.85x that one completed observation
#
# The continuation wrapper is NOT chosen next to that number; it is composed from the stages it contains,
# immediately below. An earlier revision picked 2700 s alongside the upgrade deadline, which was smaller
# than the sum of its own stages and could therefore terminate work still inside its component allowance.
#
# THE SEQUENTIAL ARGUMENT, which a plain "inner < outer" ordering misses. One task run can make more than
# one continuation attempt: the reconciliation path runs a primary handoff and, on failure, a recovery
# handoff, and Start-AeroLinkRemoteDemo has the same shape. The recovery attempt is NOT a tidy-up - it
# restores topology through Start-AeroLinkProduction.ps1, which can itself re-enter
# Invoke-AeroLinkCloneValidatedUpgrade - so it must be budgeted as a second upgrade-capable attempt:
#
#   worst case per task run = 2 x 2700 s   two sequential continuations
#                           +     600 s   outer inspection, teardown, obligation write, cleanup
#                           =    6000 s
#   installed task limit      PT120M = 7200 s, leaving 1200 s of margin above that worst case.
#
# THE ORDERING that must not drift:
#
#   supported upgrade  <  continuation wrapper  <  sum of allowed attempts  <  task ExecutionTimeLimit
#
# The inner deadlines must expire first, because those paths report truthfully, prove the continuation
# actually stopped, discharge the restart obligation and release the lease. Task Scheduler's
# ExecutionTimeLimit is a hard terminate that does none of those things, so it must never be the thing that
# fires. The previous values had this backwards - PT30M over two 900 s attempts - and the two XML blocks
# must stay equal, or the recovery and reconcile tasks disagree about how long a transition may take.
#
# ACCEPTED CONSEQUENCE. Both installed tasks set MultipleInstancesPolicy=IgnoreNew, so a long run does not
# stack instances; the 30-minute triggers during it are skipped instead. A worst-case run therefore skips
# reconciliation triggers for up to two hours. That is the correct trade against hard-terminating a
# transition midway and leaving HOME with its runtime and tunnel down.
# And the OUTER delegation wrapper, which is a different thing again. When Update runs from a checkout that
# is not the dedicated production source it re-runs the whole Update in that source - so its budget must
# exceed a COMPLETE inner Update, recovery attempt included, or the outer wrapper expires while the delegated
# update is legitimately recovering. It sits above the 6000 s worst case and below the task limit.
# THE COMPONENT STAGES, because the continuation budget has to be COMPOSED from what it contains rather
# than picked next to it. Start-AeroLinkRemoteDemo runs these sequentially inside one continuation:
#
#     PostgreSQL recovery        300 s
#     production launcher       2400 s   (the clone-validated upgrade lives here)
#     ngrok protection wait      120 s
#     inspection, identity checks, topology restore, cleanup   300 s
#     --------------------------------
#     continuation             3120 s
#
# A 2700 s continuation - the previous value - was SMALLER than the sum of the stages it contains, so it
# could terminate work that was still inside its own component allowance. That is the same defect as the
# original 900 s, one level up.
$script:AeroLinkPostgresRecoveryTimeoutSeconds = 300
$script:AeroLinkSupportedUpgradeTimeoutSeconds = 2400
$script:AeroLinkNgrokProtectionWaitSeconds = 120
$script:AeroLinkContinuationOverheadSeconds = 300
$script:AeroLinkTransitionContinuationTimeoutSeconds =
    $script:AeroLinkPostgresRecoveryTimeoutSeconds +
    $script:AeroLinkSupportedUpgradeTimeoutSeconds +
    $script:AeroLinkNgrokProtectionWaitSeconds +
    $script:AeroLinkContinuationOverheadSeconds

# The post-advance continuation re-enters the updated script with -Action Update. The handoff guard means it
# sees AlreadyCurrent and takes the restore path rather than advancing again, so it costs one continuation
# plus its own inspection - not another full advance.
$script:AeroLinkOuterOverheadSeconds = 600
$script:AeroLinkPostAdvanceContinuationTimeoutSeconds =
    $script:AeroLinkTransitionContinuationTimeoutSeconds + $script:AeroLinkOuterOverheadSeconds

# One reconcile/start run may make a primary handoff AND a recovery handoff, and the recovery path can
# itself re-enter the clone-validated upgrade through the launcher, so both are upgrade-capable.
$script:AeroLinkSequentialContinuationAttempts = 2
$script:AeroLinkReconcileWorstCaseSeconds =
    ($script:AeroLinkSequentialContinuationAttempts * $script:AeroLinkTransitionContinuationTimeoutSeconds) +
    $script:AeroLinkOuterOverheadSeconds

# The OUTER delegation wraps a COMPLETE inner Update - advance, post-advance continuation, and that Update's
# own compensation. It must strictly outlast it, so it cannot share the inner allowance: an enclosing wrapper
# with the same independently restarted budget as the thing it encloses does not outlast it.
$script:AeroLinkDelegatedUpdateTimeoutSeconds =
    $script:AeroLinkPostAdvanceContinuationTimeoutSeconds + $script:AeroLinkReconcileWorstCaseSeconds

# Finally the installed tasks. They must exceed the work THEY can actually run, because ExecutionTimeLimit
# is a hard terminate that discharges nothing.
#
# Which is the reconcile worst case (6840 s), NOT the delegated update. Both installed tasks invoke
# AeroLinkRemoteDemo.ps1 (-Action Start -Scheduled / -Action Reconcile -Scheduled); neither invokes
# Configure-AeroLinkProductionSource.ps1, so the delegation path is operator-invoked from the BAT and is
# never under a Task Scheduler limit. Sizing the tasks for it would have cost more than three hours of
# skipped reconciliation to bound a path they cannot reach.
#
#     PT135M = 8100 s  >  6840 s reconcile worst case, with 1260 s of margin.
$script:AeroLinkInstalledTaskTimeLimit = 'PT135M'


function Get-AeroLinkTransitionBudget {
    <#
      .SYNOPSIS The transition budgets, so callers outside this module cannot drift from the derivation above.
    #>
    [CmdletBinding()]
    param()
    return [pscustomobject]@{
        PostgresRecoverySeconds      = $script:AeroLinkPostgresRecoveryTimeoutSeconds
        SupportedUpgradeSeconds      = $script:AeroLinkSupportedUpgradeTimeoutSeconds
        NgrokProtectionSeconds       = $script:AeroLinkNgrokProtectionWaitSeconds
        ContinuationOverheadSeconds  = $script:AeroLinkContinuationOverheadSeconds
        ContinuationSeconds          = $script:AeroLinkTransitionContinuationTimeoutSeconds
        OuterOverheadSeconds         = $script:AeroLinkOuterOverheadSeconds
        PostAdvanceContinuationSeconds = $script:AeroLinkPostAdvanceContinuationTimeoutSeconds
        SequentialAttempts           = $script:AeroLinkSequentialContinuationAttempts
        ReconcileWorstCaseSeconds    = $script:AeroLinkReconcileWorstCaseSeconds
        DelegatedUpdateSeconds       = $script:AeroLinkDelegatedUpdateTimeoutSeconds
        InstalledTaskTimeLimit       = $script:AeroLinkInstalledTaskTimeLimit
    }
}

function Get-AeroLinkRemoteDemoConfigPath {
    return Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo\remote-demo.config.psd1'
}

function Get-AeroLinkRemoteDemoConfig {
    <#
      .SYNOPSIS Loads and validates the per-user remote-demo configuration.
      .DESCRIPTION
        The configuration lives outside source control and may contain only
        non-secret values: paths, the public URL, the upstream URL, and optional
        Vault/secret NAMES (never values). Missing or malformed configuration
        fails closed with an actionable error.
    #>
    [CmdletBinding()]
    param(
        [string]$ConfigPath = (Get-AeroLinkRemoteDemoConfigPath)
    )

    $allowedKeys = @(
        'NgrokExecutable',
        'PublicUrl',
        'TrafficPolicyPath',
        'Upstream',
        'LocalApiBaseUri',
        'AeroLinkRoot',
        'LogsPath',
        'StatePath',
        'VaultName',
        'BasicAuthSecretName'
    )
    $requiredKeys = @('NgrokExecutable', 'PublicUrl', 'TrafficPolicyPath')

    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        throw "Remote-demo configuration not found at $ConfigPath. Configure it with CONFIGURE_AEROLINK_REMOTE_DEMO.bat or create the per-user config file."
    }

    try {
        $values = Import-PowerShellDataFile -LiteralPath $ConfigPath
    }
    catch [System.Management.Automation.CommandNotFoundException] {
        # Not a configuration problem, and saying so would send the reader to a file that is fine.
        # Windows PowerShell resolves its own cmdlets through PSModulePath, and a PowerShell 7 parent
        # leaves the 7.x module directories in front, so 5.1 binds Microsoft.PowerShell.Utility out of
        # the wrong tree and this cmdlet is simply absent. Name that, because the reader cannot guess it.
        throw "Windows PowerShell could not find $($_.Exception.CommandName), so the remote-demo configuration at $ConfigPath was never read. The configuration is not implicated. This happens when PSModulePath puts the PowerShell 7 module directories ahead of Windows PowerShell's own: start this from Explorer or cmd, or use the repository .bat entry points, which clear PSModulePath first."
    }
    catch {
        throw "Remote-demo configuration at $ConfigPath is malformed: $($_.Exception.Message)"
    }

    foreach ($key in $values.Keys) {
        if ($allowedKeys -notcontains $key) {
            throw "Remote-demo configuration contains an unknown key '$key'. Only non-secret operator values are allowed."
        }
    }
    foreach ($key in $requiredKeys) {
        if (-not $values.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string]$values[$key])) {
            throw "Remote-demo configuration is missing the required non-secret value '$key'."
        }
    }
    $publicUri = $null
    if (-not [uri]::TryCreate(([string]$values['PublicUrl']).Trim(), [System.UriKind]::Absolute, [ref]$publicUri) -or $publicUri.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($publicUri.Host) -or $publicUri.AbsolutePath -ne '/' -or -not [string]::IsNullOrEmpty($publicUri.UserInfo) -or -not [string]::IsNullOrEmpty($publicUri.Query) -or -not [string]::IsNullOrEmpty($publicUri.Fragment)) {
        throw 'Remote-demo PublicUrl must be an absolute HTTPS origin with no credentials, query, or fragment.'
    }
    if ($values.ContainsKey('AeroLinkRoot') -and -not (Test-Path -LiteralPath ([string]$values['AeroLinkRoot']))) {
        throw "Remote-demo configuration AeroLinkRoot does not exist: $($values['AeroLinkRoot'])"
    }

    $moduleRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $defaultRoot = $moduleRoot
    $defaultLocal = Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo'

    # The dedicated production source wins over any AeroLinkRoot recorded here.
    #
    # On 2026-09-03 the recovery task ran from the only checkout on the machine, which was mid-#880 with
    # dirty WIP on a feature branch, and the canonical guard correctly refused. Resolving the source from the
    # production-source authority rather than from this file means a stale AeroLinkRoot — or a copy of the
    # configuration made before the split — cannot quietly aim recovery back at the development checkout.
    $productionSourceRoot = $null
    $productionSourceReason = 'No dedicated production source is configured; the source root came from the remote-demo configuration.'
    try {
        $productionConfig = Get-AeroLinkProductionSourceConfig
        $productionPosture = Get-AeroLinkProductionSourcePosture -SourceRoot $productionConfig.SourceRoot -RemoteName $productionConfig.RemoteName
        if ($productionPosture.Dedicated) {
            $productionSourceRoot = $productionConfig.SourceRoot
            $productionSourceReason = "The dedicated production source at $($productionConfig.SourceRoot) is authoritative for remote demo."
        }
        else {
            $productionSourceReason = "The configured production source at $($productionConfig.SourceRoot) is not marked as a dedicated AeroLink production source."
        }
    }
    catch { $productionSourceReason = "No usable dedicated production source: $($_.Exception.Message)" }

    return [pscustomobject]@{
        ProductionSourceRoot = $productionSourceRoot
        ProductionSourceReason = $productionSourceReason
        NgrokExecutable = [string]$values['NgrokExecutable']
        PublicUrl = $publicUri.GetLeftPart([System.UriPartial]::Authority).TrimEnd('/')
        TrafficPolicyPath = [string]$values['TrafficPolicyPath']
        Upstream = if ($values.ContainsKey('Upstream')) { [string]$values['Upstream'] } else { 'http://127.0.0.1:5080' }
        LocalApiBaseUri = if ($values.ContainsKey('LocalApiBaseUri')) { [string]$values['LocalApiBaseUri'] } else { 'http://127.0.0.1:5080' }
        AeroLinkRoot = if ($productionSourceRoot) { $productionSourceRoot } elseif ($values.ContainsKey('AeroLinkRoot')) { [string]$values['AeroLinkRoot'] } else { $defaultRoot }
        LogsPath = if ($values.ContainsKey('LogsPath')) { [string]$values['LogsPath'] } else { Join-Path $defaultLocal 'logs' }
        StatePath = if ($values.ContainsKey('StatePath')) { [string]$values['StatePath'] } else { Join-Path $defaultLocal 'state' }
        VaultName = if ($values.ContainsKey('VaultName')) { [string]$values['VaultName'] } else { 'aerolink-demo' }
        BasicAuthSecretName = if ($values.ContainsKey('BasicAuthSecretName')) { [string]$values['BasicAuthSecretName'] } else { 'basic-auth-password' }
    }
}

function Get-AeroLinkRemoteDemoNgrokArguments {
    <#
      .SYNOPSIS Builds the ngrok launch arguments from the non-secret config.
      .DESCRIPTION No secret value is accepted or emitted by this function.
    #>
    param(
        [Parameter(Mandatory)]$Config
    )
    return @(
        'http',
        $Config.Upstream,
        '--url',
        $Config.PublicUrl,
        '--traffic-policy-file',
        $Config.TrafficPolicyPath,
        '--log',
        'stdout'
    )
}

function Get-AeroLinkRemoteDemoNgrokProcess {
    <#
      .SYNOPSIS Finds ngrok processes that match the recorded launch contract.
      .DESCRIPTION
        Ownership requires the exact configured executable AND a command line
        containing the public URL, upstream, and Traffic Policy. Any ngrok process
        that does not match is reported as a mismatch and is never stopped.

        Enumeration failure is UNKNOWN, not NONE. `-ErrorAction SilentlyContinue` on the CIM query turned an
        unavailable or access-denied WMI into an empty process list, which reads downstream as "no ngrok is
        running" - so a caller proving the owned tunnel is down would be told it is down without anything
        having been observed or stopped, and a source transition would proceed while the public endpoint was
        still forwarding. The query now throws, and `Enumerated` records that live enumeration succeeded so a
        caller can require it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [object[]]$ProcessInfos
    )

    $enumerated = $true
    $liveEnumeration = ($null -eq $ProcessInfos)
    if ($null -eq $ProcessInfos) {
        # Injected process lists stay deterministic for the contract suite; only LIVE enumeration can fail.
        try { $ProcessInfos = @(Get-CimInstance Win32_Process -Filter "Name='ngrok.exe'" -ErrorAction Stop) }
        catch {
            throw "AeroLink could not enumerate running processes to determine ngrok ownership: $($_.Exception.Message). Nothing was stopped and no conclusion was drawn - an unreadable process table means unknown, never none."
        }
    }

    $owned = @()
    $mismatched = @()
    $expectedExe = [IO.Path]::GetFullPath($Config.NgrokExecutable)
    foreach ($process in $ProcessInfos) {
        if ($liveEnumeration -and (-not $process.ExecutablePath -or -not $process.CommandLine)) {
            try {
                $native = Get-AeroLinkNativeProcessIdentity -ProcessId $process.ProcessId
                if (-not $process.CreationDate -or
                    ([DateTimeOffset]$process.CreationDate).UtcDateTime.ToString('yyyyMMddHHmmssffffff') -ne
                    ([DateTimeOffset]$native.StartedAt).UtcDateTime.ToString('yyyyMMddHHmmssffffff')) { throw 'Process identity changed during enumeration.' }
                $process = [pscustomobject]@{ ProcessId=$native.ProcessId; ExecutablePath=$native.ExecutablePath; CommandLine=$native.CommandLine; CreationDate=$process.CreationDate }
            } catch { $mismatched += $process; continue }
        }
        $executable = ''
        if ($process.ExecutablePath) { $executable = [IO.Path]::GetFullPath($process.ExecutablePath) }
        $command = [string]$process.CommandLine
        $exeMatches = $executable -eq $expectedExe
        $contractMatches = Test-AeroLinkNgrokLaunchContract -Config $Config -CommandLine $command
        if ($exeMatches -and $contractMatches) {
            if ($liveEnumeration) {
                # Exact OS start identity is retained for the stop boundary. If it cannot be read, this
                # process is unknown even when an earlier command-line read happened to succeed.
                try { $process | Add-Member -NotePropertyName StartedAt -NotePropertyValue (Get-AeroLinkProcessStartIdentity -ProcessId $process.ProcessId) -Force }
                catch { $mismatched += $process; continue }
            }
            $owned += $process
        }
        else {
            $mismatched += $process
        }
    }
    return [pscustomobject]@{ Owned = @($owned); Mismatched = @($mismatched); Enumerated = $enumerated }
}

function Test-AeroLinkNgrokLaunchContract {
    param([Parameter(Mandatory)]$Config, [AllowEmptyString()][string]$CommandLine)
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    $arguments = @([AeroLink.ProcessAccess]::Arguments($CommandLine))
    $expected = @(Get-AeroLinkRemoteDemoNgrokArguments -Config $Config)
    # Only our supported invocation is attributable. A duplicated URL, alternate config or extra policy
    # override must not satisfy a substring check while executing a different contract.
    if ($arguments.Count -ne $expected.Count + 1) { return $false }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        if (-not [string]::Equals($arguments[$index + 1], $expected[$index], [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }
    return $true
}

function Test-AeroLinkRemoteDemoPublicProtection {
    <#
      .SYNOPSIS Proves the outer Basic Auth gate without knowing the password.
      .DESCRIPTION
        An unauthenticated request with ngrok-skip-browser-warning must return 401
        at the ngrok edge. 2xx, AeroLink 400, 404, or any other responder means the
        endpoint is not protected.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [scriptblock]$ProbeScriptBlock
    )

    if ($null -eq $ProbeScriptBlock) {
        $ProbeScriptBlock = {
            param($PublicUrl)
            Invoke-WebRequest -Uri $PublicUrl -Headers @{ 'ngrok-skip-browser-warning' = '1' } `
                -UseBasicParsing -TimeoutSec 20 -MaximumRedirection 0
        }
    }

    try {
        $response = & $ProbeScriptBlock $Config.PublicUrl
        $status = [int]$response.StatusCode
        return [pscustomobject]@{
            Protected = $false
            StatusCode = $status
            Detail = "Public endpoint returned HTTP $status; expected 401 from the ngrok Basic Auth edge."
        }
    }
    catch {
        $status = $null
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        if ($status -eq 401) {
            return [pscustomobject]@{
                Protected = $true
                StatusCode = 401
                Detail = 'Unauthenticated public request returned 401 at the ngrok edge.'
            }
        }
        if ($null -ne $status) {
            return [pscustomobject]@{
                Protected = $false
                StatusCode = $status
                Detail = "Public endpoint returned HTTP $status; expected 401 from the ngrok Basic Auth edge."
            }
        }
        return [pscustomobject]@{
            Protected = $false
            StatusCode = $null
            Detail = "Public endpoint was unreachable: $($_.Exception.GetType().Name)"
        }
    }
}

function Test-AeroLinkRemoteDemoLocalReady {
    <#
      .SYNOPSIS Confirms the canonical local AeroLink is ready and serves the built client.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config
    )
    try {
        $ready = Invoke-RestMethod ($Config.LocalApiBaseUri.TrimEnd('/') + '/health/ready') -TimeoutSec 5
        if ($ready.status -ne 'ready' -or $ready.database -ne 'connected') {
            return [pscustomobject]@{ Ready = $false; Detail = "AeroLink readiness reports status=$($ready.status) database=$($ready.database)." }
        }
        $root = Invoke-WebRequest ($Config.LocalApiBaseUri.TrimEnd('/') + '/') -UseBasicParsing -TimeoutSec 10
        if ($root.StatusCode -ne 200 -or $root.Content -notmatch '/assets/index-[\w-]+\.js') {
            return [pscustomobject]@{ Ready = $false; Detail = 'AeroLink is not serving the built client from its root.' }
        }
        return [pscustomobject]@{ Ready = $true; Detail = 'AeroLink is ready locally and serves the built client.' }
    }
    catch {
        return [pscustomobject]@{ Ready = $false; Detail = "AeroLink local check failed: $($_.Exception.GetType().Name)" }
    }
}

function Get-AeroLinkRemoteDemoLocalRuntimeIdentity {
    <#
      .SYNOPSIS Identifies the single process that owns the configured local API listener.
      .DESCRIPTION
        This is runtime attribution, not a general process search. A missing or
        ambiguous listener fails closed because it cannot prove which process is
        producing notification links.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [scriptblock]$RuntimeProbe
    )
    if ($null -ne $RuntimeProbe) { return & $RuntimeProbe $Config }

    try {
        $localUri = [uri]$Config.LocalApiBaseUri
        $ownerIds = @(Get-NetTCPConnection -State Listen -LocalPort $localUri.Port -ErrorAction Stop |
            Select-Object -ExpandProperty OwningProcess -Unique)
        if ($ownerIds.Count -ne 1) {
            return [pscustomobject]@{ Found = $false; Detail = "Expected one owner for local API port $($localUri.Port), found $($ownerIds.Count)." }
        }
        $process = Get-Process -Id $ownerIds[0] -ErrorAction Stop
        return [pscustomobject]@{
            Found = $true
            ProcessId = [int]$process.Id
            StartedAt = $process.StartTime.ToUniversalTime().ToString('o')
            Detail = "Local API port $($localUri.Port) is owned by PID $($process.Id)."
        }
    }
    catch {
        return [pscustomobject]@{ Found = $false; Detail = "Local API runtime identity could not be established: $($_.Exception.GetType().Name)." }
    }
}

function Test-AeroLinkRemoteDemoNotificationOriginProof {
    <#
      .SYNOPSIS Proves that the current local API was started with the protected public notification origin.
      .DESCRIPTION
        A successful remote-demo start records the public origin together with
        the exact local listener PID and process start time. Repeated starts and
        status checks accept that evidence only while the same process still owns
        the listener. Restarting AeroLink therefore invalidates the proof.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [scriptblock]$RuntimeProbe
    )
    $stateFile = Join-Path $Config.StatePath 'remote-demo-state.json'
    if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
        return [pscustomobject]@{ Valid = $false; Detail = 'No attributable notification-origin proof is recorded for the current local API.' }
    }
    try {
        $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        $runtime = Get-AeroLinkRemoteDemoLocalRuntimeIdentity -Config $Config -RuntimeProbe $RuntimeProbe
        if (-not $runtime.Found) {
            return [pscustomobject]@{ Valid = $false; Detail = $runtime.Detail }
        }
        $originMatches = [string]::Equals([string]$state.NotificationBaseUrl, [string]$Config.PublicUrl, [StringComparison]::OrdinalIgnoreCase)
        # PowerShell 7 deserializes an ISO JSON timestamp as DateTime while Windows PowerShell can leave it as
        # text. Compare the exact UTC instant, not those host-specific string representations. Invalid or missing
        # values still fall into the fail-closed catch below.
        $stateStartedAt = [DateTimeOffset]$state.LocalApiStartedAt
        $runtimeStartedAt = [DateTimeOffset]$runtime.StartedAt
        $processMatches = [int]$state.LocalApiPid -eq [int]$runtime.ProcessId `
            -and $stateStartedAt.UtcDateTime.Ticks -eq $runtimeStartedAt.UtcDateTime.Ticks
        if (-not $originMatches -or -not $processMatches) {
            return [pscustomobject]@{ Valid = $false; Detail = 'Recorded notification origin does not belong to the current local API process.' }
        }
        return [pscustomobject]@{ Valid = $true; Detail = "Current local API PID $($runtime.ProcessId) is attributed to notification origin $($Config.PublicUrl)." }
    }
    catch {
        return [pscustomobject]@{ Valid = $false; Detail = "Notification-origin proof is invalid: $($_.Exception.GetType().Name)." }
    }
}

function Set-AeroLinkRemoteDemoNotificationOriginProof {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Config, [Parameter(Mandatory)]$ExpectedProcess)
    $runtime = Get-AeroLinkRemoteDemoLocalRuntimeIdentity -Config $Config
    if (-not $runtime.Found -or $runtime.ProcessId -ne $ExpectedProcess.ProcessId -or
        ([DateTimeOffset]$runtime.StartedAt).UtcDateTime.Ticks -ne ([DateTimeOffset]$ExpectedProcess.StartedAt).UtcDateTime.Ticks) {
        throw 'The newly launched API no longer owns the listener; notification origin was not attributed.'
    }
    if (-not (Test-Path -LiteralPath $Config.StatePath)) { New-Item -ItemType Directory -Path $Config.StatePath -Force | Out-Null }
    $statePath = Join-Path $Config.StatePath 'remote-demo-state.json'
    $state = @{}
    if (Test-Path -LiteralPath $statePath) {
        try { (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $state[$_.Name] = $_.Value } }
        catch { $state = @{} }
    }
    $state.NotificationBaseUrl = $Config.PublicUrl
    $state.LocalApiPid = $runtime.ProcessId
    $state.LocalApiStartedAt = $runtime.StartedAt
    $temporary = $statePath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $statePath -Force
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Get-AeroLinkRemoteDemoStartDecision {
    <#
      .SYNOPSIS Deterministic idempotence/fail-closed decision for remote-demo start.
    #>
    param(
        [bool]$LocalReady,
        [bool]$OwnedProcessPresent,
        [bool]$Protected,
        [object]$ProbeStatusCode
    )
    if (-not $LocalReady) {
        return [pscustomobject]@{ Decision = 'BlockedLocalNotReady'; Message = 'AeroLink is not locally ready; the public endpoint must not be exposed.' }
    }
    if ($OwnedProcessPresent) {
        if ($Protected) {
            return [pscustomobject]@{ Decision = 'AlreadyReady'; Message = 'The expected protected tunnel is already running and returning 401.' }
        }
        return [pscustomobject]@{ Decision = 'BlockedOwnedNotProtected'; Message = 'The owned ngrok process is running but the public endpoint is not returning 401; refusing to start a second tunnel.' }
    }
    if ($null -ne $ProbeStatusCode -and $ProbeStatusCode -ne 404 -and $ProbeStatusCode -ne 502 -and $ProbeStatusCode -ne 503) {
        return [pscustomobject]@{ Decision = 'BlockedForeignResponder'; Message = "Public endpoint is occupied by an unexpected responder (HTTP $ProbeStatusCode); refusing to replace it." }
    }
    return [pscustomobject]@{ Decision = 'CanStart'; Message = 'No owned tunnel exists and the public endpoint is free; starting the protected tunnel.' }
}

function New-AeroLinkRemoteDemoRun {
    <#
      .SYNOPSIS A correlation context for one recovery attempt.
    #>
    param([switch]$Scheduled)
    return [pscustomobject]@{
        CorrelationId = [guid]::NewGuid().ToString('N')
        Invocation = if ($Scheduled) { 'scheduled' } else { 'manual' }
        StartedAt = (Get-Date).ToUniversalTime()
    }
}

function Write-AeroLinkRemoteDemoLog {
    param(
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)][string]$Message,
        $Run
    )
    $logDirectory = $Config.LogsPath
    if (-not (Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
    $context = if ($Run) { "$($Run.CorrelationId) [$($Run.Invocation)]" } else { 'manual' }
    $line = "$((Get-Date).ToUniversalTime().ToString('o')) [$context] $Message"
    Add-Content -LiteralPath (Join-Path $logDirectory 'remote-demo.log') -Value $line -Encoding UTF8
}

function Get-AeroLinkRemoteDemoPostgresBin {
    <#
      .SYNOPSIS The PostgreSQL client binaries of the installation this source root belongs to.
      .DESCRIPTION
        Resolved through the installation authority rather than composed from the source root, so the
        dedicated HOME production checkout probes the canonical HOME cluster instead of an empty one beside
        its own source. Without a pointer the answer is the historical <root>\product\.local location.
    #>
    param([Parameter(Mandatory)]$Config)
    return (Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $Config.AeroLinkRoot 'product')).PostgresBin
}

function Test-AeroLinkRemoteDemoPostgresReady {
    <#
      .SYNOPSIS PostgreSQL readiness = pg_isready success AND a bounded real query.
      .DESCRIPTION
        A listening socket alone is never treated as healthy. Both probes are
        file-redirected and bounded so a scheduled invocation cannot block on
        inherited stdio handles.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [string]$PostgresBin = '',
        [string]$DatabaseHost = '127.0.0.1',
        [int]$DatabasePort = 0,
        [string]$DatabaseUser = 'postgres',
        [string]$DatabaseName = 'aerolink',
        [scriptblock]$PgIsreadyProbe,
        [scriptblock]$QueryProbe
    )
    # Parameter default expressions run in the CALLER's scope, so a module-internal
    # helper is not resolvable there. Resolve the binary path inside the function
    # instead; an empty PostgresBin previously made the probes fail with an empty
    # executable path even when PostgreSQL was healthy (#483 handover).
    if (-not $DatabasePort) { $DatabasePort = (Get-AeroLinkServiceEndpoints).PostgresPort }
    if (-not $PostgresBin) { $PostgresBin = Get-AeroLinkRemoteDemoPostgresBin -Config $Config }
    $logDirectory = $Config.LogsPath
    if (-not (Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
    if ($null -eq $PgIsreadyProbe) {
        $PgIsreadyProbe = {
            param($Bin, $DbHost, $DbPort, $DbUser, $Db, $Out, $Err)
            $result = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $Bin 'pg_isready.exe') `
                -ArgumentList @('-h', $DbHost, '-p', "$DbPort", '-U', $DbUser, '-d', $Db) `
                -StandardOutput $Out -StandardError $Err -TimeoutSeconds 30 -StepName 'pg_isready' -CaptureOutput
            return $result.ExitCode -eq 0
        }
    }
    if ($null -eq $QueryProbe) {
        $QueryProbe = {
            param($Bin, $DbHost, $DbPort, $DbUser, $Db, $Out, $Err)
            $result = Invoke-AeroLinkNativeCommand -FilePath (Join-Path $Bin 'psql.exe') `
                -ArgumentList @('-X', '-h', $DbHost, '-p', "$DbPort", '-U', $DbUser, '-d', $Db, '-tA', '-q', '-c', 'SELECT 1') `
                -StandardOutput $Out -StandardError $Err -TimeoutSeconds 30 -StepName 'postgres real query' -CaptureOutput
            if ($result.ExitCode -ne 0) { return $false }
            $value = ($result.StdOutText -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 1)
            return ([string]$value).Trim() -eq '1'
        }
    }
    $readyOk = & $PgIsreadyProbe $PostgresBin $DatabaseHost $DatabasePort $DatabaseUser 'postgres' `
        (Join-Path $logDirectory 'pg-ready-pg_isready.stdout.log') (Join-Path $logDirectory 'pg-ready-pg_isready.stderr.log')
    $queryOk = $false
    if ($readyOk) {
        $queryOk = & $QueryProbe $PostgresBin $DatabaseHost $DatabasePort $DatabaseUser $DatabaseName `
            (Join-Path $logDirectory 'pg-ready-query.stdout.log') (Join-Path $logDirectory 'pg-ready-query.stderr.log')
    }
    $detail = if ($readyOk -and $queryOk) {
        'pg_isready and a real read-only SELECT 1 both succeeded.'
    }
    elseif (-not $readyOk) {
        'pg_isready did not report accepting connections (listener alone is not health).'
    }
    else {
        'pg_isready succeeded but a real read-only SELECT 1 did not return 1.'
    }
    return [pscustomobject]@{ Ready = ($readyOk -and $queryOk); PgIsreadyOk = $readyOk; QueryOk = $queryOk; Detail = $detail }
}

function Start-AeroLinkRemoteDemoPostgresHelper {
    <#
      .SYNOPSIS Launches Start-Postgres.ps1 as an owned, file-redirected child and
        returns a live process handle the orchestrator can poll and terminate.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        $Run
    )
    $logDirectory = $Config.LogsPath
    if (-not (Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
    $script = Join-Path $Config.AeroLinkRoot 'product\scripts\Start-Postgres.ps1'
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $stdout = Join-Path $logDirectory 'postgres-helper.stdout.log'
    $stderr = Join-Path $logDirectory 'postgres-helper.stderr.log'
    $argumentLine = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -WaitSeconds 300"
    $process = Start-Process -FilePath $powershell -ArgumentList $argumentLine -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $helper = [pscustomobject]@{
        Id = $process.Id
        Process = $process
        NativeHandle = $process.Handle
        HasExited = $false
        ExitCode = $null
        StdOutPath = $stdout
        StdErrPath = $stderr
    }
    $helper | Add-Member -MemberType ScriptMethod -Name Refresh -Value {
        $this.Process.Refresh()
        $this.HasExited = $this.Process.HasExited
        if ($this.Process.HasExited -and $null -eq $this.ExitCode) { $this.ExitCode = [AeroLink.ProcessAccess]::ExitCode($this.NativeHandle) }
    }
    return $helper
}

function Stop-AeroLinkRemoteDemoOwnedProcess {
    <#
      .SYNOPSIS Terminates only a helper PID this recovery attempt launched.
      .DESCRIPTION Refuses anything whose process name is not a PowerShell helper.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][Diagnostics.Process]$ExpectedProcess)
    if ($ExpectedProcess.Id -ne $ProcessId) { throw 'The helper handle does not match the requested process.' }
    if ($ExpectedProcess.HasExited) { return }
    # Use the creator's retained process handle, never a fresh name/PID lookup that can target a reused PID.
    $ExpectedProcess.Kill()
    $ExpectedProcess.WaitForExit()

}

function Start-AeroLinkRemoteDemoPostgres {
    <#
      .SYNOPSIS Bounded, self-healing PostgreSQL start for one recovery attempt.
      .DESCRIPTION
        Waits (bounded) for pg_isready plus a real read-only query. If PostgreSQL
        becomes independently healthy while the helper child is still running, the
        owned helper is terminated and startup proceeds. If the deadline expires
        without query-ready health, the owned helper is terminated and the attempt
        fails with step/PID/log details. Never touches an unowned process.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        $Run,
        [scriptblock]$ReadyTest,
        [scriptblock]$HelperLauncher,
        [scriptblock]$HelperStopper,
        [int]$RecoveryTimeoutSeconds = 300,
        [int]$PollIntervalSeconds = 2,
        [int]$GraceSeconds = 5
    )
    if ($null -eq $ReadyTest) { $ReadyTest = { param($C, $R) Test-AeroLinkRemoteDemoPostgresReady -Config $C } }
    if ($null -eq $HelperLauncher) { $HelperLauncher = { param($C, $R) Start-AeroLinkRemoteDemoPostgresHelper -Config $C -Run $R } }
    if ($null -eq $HelperStopper) { $HelperStopper = { param($C, $R, $ProcessId, $Helper) Stop-AeroLinkRemoteDemoOwnedProcess -ProcessId $ProcessId -ExpectedProcess $Helper.Process } }

    $ready = & $ReadyTest $Config $Run
    if ($ready.Ready) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "PostgreSQL already query-ready: $($ready.Detail)"
        return [pscustomobject]@{ Healthy = $true; HelperUsed = $false; ProcessId = $null; Step = 'postgres-ready'; Detail = $ready.Detail; LogPath = (Join-Path $Config.LogsPath 'remote-demo.log') }
    }

    $helper = & $HelperLauncher $Config $Run
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "PostgreSQL helper started (PID $($helper.Id), step postgres-helper, stdout $($helper.StdOutPath), stderr $($helper.StdErrPath))."
    $deadline = (Get-Date).AddSeconds($RecoveryTimeoutSeconds)
    $helperExited = $false
    $ready = $null
    do {
        Start-Sleep -Seconds $PollIntervalSeconds
        $helper.Refresh()
        if ($helper.HasExited -and -not $helperExited) {
            $helperExited = $true
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "PostgreSQL helper exited with code $($helper.ExitCode)."
        }
        $ready = & $ReadyTest $Config $Run
    } while (-not $ready.Ready -and (Get-Date) -lt $deadline)

    if ($ready.Ready) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "PostgreSQL became query-ready: $($ready.Detail)"
        Start-Sleep -Seconds $GraceSeconds
        if (-not $helper.HasExited) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Terminating owned PostgreSQL helper PID $($helper.Id) because PostgreSQL is independently query-ready."
            & $HelperStopper $Config $Run $helper.Id $helper
        }
        return [pscustomobject]@{ Healthy = $true; HelperUsed = $true; ProcessId = $helper.Id; Step = 'postgres-recovery'; Detail = $ready.Detail; LogPath = (Join-Path $Config.LogsPath 'remote-demo.log') }
    }

    if (-not $helper.HasExited) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "PostgreSQL helper PID $($helper.Id) exceeded $RecoveryTimeoutSeconds seconds; terminating the owned helper."
        & $HelperStopper $Config $Run $helper.Id $helper
    }
    $detail = if ($helperExited) {
        "PostgreSQL helper exited with code $($helper.ExitCode) but the database never became query-ready within $RecoveryTimeoutSeconds seconds. Step: postgres-recovery. Helper PID: $($helper.Id). Logs: $($helper.StdOutPath), $($helper.StdErrPath), $(Join-Path $Config.LogsPath 'remote-demo.log')"
    }
    else {
        "PostgreSQL helper PID $($helper.Id) exceeded $RecoveryTimeoutSeconds seconds and was terminated; the database never became query-ready. Step: postgres-recovery. Logs: $($helper.StdOutPath), $($helper.StdErrPath), $(Join-Path $Config.LogsPath 'remote-demo.log')"
    }
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "AEROLINK REMOTE DEMO NOT READY: $detail"
    return [pscustomobject]@{ Healthy = $false; HelperUsed = $true; ProcessId = $helper.Id; Step = 'postgres-recovery'; Detail = $detail; LogPath = (Join-Path $Config.LogsPath 'remote-demo.log') }
}

function Start-AeroLinkRemoteDemoProductionHelper {
    <#
      .SYNOPSIS Launches Start-AeroLinkProduction.ps1 -DoNotOpenBrowser as an owned,
        file-redirected child the orchestrator can poll.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        $Run
    )
    $logDirectory = $Config.LogsPath
    if (-not (Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
    $script = Join-Path $Config.AeroLinkRoot 'product\scripts\Start-AeroLinkProduction.ps1'
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $stdout = Join-Path $logDirectory 'production-helper.stdout.log'
    $stderr = Join-Path $logDirectory 'production-helper.stderr.log'
    # The tunnel's protected public origin is the only honest mail-link origin for a remote recipient.
    # Pass it before the API starts; an already-running local process is handled below rather than silently
    # claiming a loopback-configured process can produce reachable remote links.
    $argumentLine = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -DoNotOpenBrowser -NotificationBaseUrl `"$($Config.PublicUrl)`""
    $process = Start-Process -FilePath $powershell -ArgumentList $argumentLine -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $helper = [pscustomobject]@{
        Id = $process.Id
        Process = $process
        NativeHandle = $process.Handle
        HasExited = $false
        ExitCode = $null
        StdOutPath = $stdout
        StdErrPath = $stderr
    }
    $helper | Add-Member -MemberType ScriptMethod -Name Refresh -Value {
        $this.Process.Refresh()
        $this.HasExited = $this.Process.HasExited
        if ($this.Process.HasExited -and $null -eq $this.ExitCode) { $this.ExitCode = [AeroLink.ProcessAccess]::ExitCode($this.NativeHandle) }
    }
    return $helper
}

function Get-AeroLinkProductionLauncherRefusal {
    <#
      .SYNOPSIS The launcher's own refusal line from a helper's captured output, or $null.
      .DESCRIPTION
        Bounded and redacted. Only lines the launcher writes as refusals are considered, and any line that
        could carry a credential, token or connection string is dropped rather than quoted — a diagnostic is
        not worth leaking a secret into a log an operator may paste into an issue.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][string]$StandardOutputPath,
        [AllowNull()][string]$StandardErrorPath,
        [int]$TailLines = 40
    )
    $banner = $null
    foreach ($path in @($StandardOutputPath, $StandardErrorPath)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $lines = @(Get-Content -LiteralPath $path -Tail $TailLines -ErrorAction SilentlyContinue)
        foreach ($line in $lines) {
            if ($line -notmatch '(?i)refus|not canonical|cannot characterize|identity mismatch') { continue }
            if ($line -match '(?i)(password|secret|token|authorization|authtoken|connectionstrings|connection string|postgresql://|User Id=|Password=)') { continue }
            $trimmed = ([string]$line).Trim()
            if (-not $trimmed) { continue }
            if ($trimmed.Length -gt 300) { $trimmed = $trimmed.Substring(0, 300) + '...' }
            # "AEROLINK PRODUCTION START REFUSED" is the heading, not the reason. The line after it names the
            # branch, the dirt or the divergence, and that is the only part an operator can act on.
            if ($trimmed -cmatch '^[A-Z0-9 ]+$') { if (-not $banner) { $banner = $trimmed }; continue }
            return $trimmed
        }
    }
    return $banner
}

function Invoke-AeroLinkProductionLauncher {
    <#
      .SYNOPSIS Bounded production-launcher invocation for one recovery attempt.
      .DESCRIPTION
        Polls local AeroLink readiness independently. If the launcher helper is
        still running when AeroLink is already ready, the owned helper is
        terminated after a grace period. On timeout the owned helper is terminated
        and the attempt fails with step/PID/log details.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        $Run,
        [scriptblock]$LocalReadyTest,
        [scriptblock]$HelperLauncher,
        [scriptblock]$HelperStopper,
        # Must cover a supported clone-validated upgrade, not just a plain start. See the budget derivation
        # at the top of this module; injectable so the contract suite can drive the deadline in seconds.
        [int]$TimeoutSeconds = $script:AeroLinkSupportedUpgradeTimeoutSeconds,
        [int]$PollIntervalSeconds = 3,
        [int]$GraceSeconds = 5,
        # How long to keep polling readiness AFTER the launcher child has exited. A launcher that has already
        # exited will not open a port; the only reason to wait at all is that a process it started may still
        # be finishing its own startup, and that is seconds, not minutes.
        [int]$PostExitGraceSeconds = 20,
        [switch]$ForceLaunch
    )
    if ($null -eq $LocalReadyTest) { $LocalReadyTest = { param($C) Test-AeroLinkRemoteDemoLocalReady -Config $C } }
    if ($null -eq $HelperLauncher) { $HelperLauncher = { param($C, $R) Start-AeroLinkRemoteDemoProductionHelper -Config $C -Run $R } }
    if ($null -eq $HelperStopper) { $HelperStopper = { param($C, $R, $ProcessId, $Helper) Stop-AeroLinkRemoteDemoOwnedProcess -ProcessId $ProcessId -ExpectedProcess $Helper.Process } }

    $local = & $LocalReadyTest $Config
    if ($local.Ready -and -not $ForceLaunch) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Local AeroLink already ready; launcher helper not needed."
        return [pscustomobject]@{ Healthy = $true; HelperUsed = $false; ProcessId = $null; Step = 'production-launcher'; Detail = $local.Detail; LogPath = (Join-Path $Config.LogsPath 'remote-demo.log') }
    }

    $helper = & $HelperLauncher $Config $Run
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Production launcher helper started (PID $($helper.Id), step production-launcher, stdout $($helper.StdOutPath), stderr $($helper.StdErrPath))."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $helperExited = $false
    $exitDeadline = $null
    $local = $null
    do {
        Start-Sleep -Seconds $PollIntervalSeconds
        $helper.Refresh()
        if ($helper.HasExited -and -not $helperExited) {
            $helperExited = $true
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Production launcher helper exited with code $($helper.ExitCode)."
            # The 2026-09-03 defect, in one line. The child had already exited with a canonical-source
            # refusal within seconds, and the parent went on polling port 5080 for the full 900. A launcher
            # that has exited is not going to open a port: the wait now ends shortly after it does, and the
            # reason it gave is the reason reported.
            $exitDeadline = (Get-Date).AddSeconds($PostExitGraceSeconds)
            if ($exitDeadline -lt $deadline) { $deadline = $exitDeadline }
        }
        $local = & $LocalReadyTest $Config
        if ($helperExited -and $helper.ExitCode -ne 0) { break }
    } while ((-not $helperExited -or -not $local.Ready) -and (Get-Date) -lt $deadline)

    if ($local.Ready -and $helperExited -and $helper.ExitCode -eq 0) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Local AeroLink became ready: $($local.Detail)"
        return [pscustomobject]@{ Healthy = $true; HelperUsed = $true; ProcessId = $helper.Id; Step = 'production-launcher'; Detail = $local.Detail; LogPath = (Join-Path $Config.LogsPath 'remote-demo.log') }
    }

    if (-not $helper.HasExited) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Production launcher helper PID $($helper.Id) exceeded $TimeoutSeconds seconds; terminating the owned helper."
        & $HelperStopper $Config $Run $helper.Id $helper
    }
    $detail = if ($helperExited) {
        # The reason the child gave, not just the fact that it stopped. A refusal is printed by the launcher
        # and is exactly what the operator needs; "never became ready" is true and useless.
        $refusal = Get-AeroLinkProductionLauncherRefusal -StandardOutputPath $helper.StdOutPath -StandardErrorPath $helper.StdErrPath
        $reason = if ($refusal) { " Reason: $refusal" } else { '' }
        "Production launcher exited with code $($helper.ExitCode) and AeroLink did not become ready.$reason Step: production-launcher. Helper PID: $($helper.Id). Logs: $($helper.StdOutPath), $($helper.StdErrPath), $(Join-Path $Config.LogsPath 'remote-demo.log')"
    }
    else {
        "Production launcher helper PID $($helper.Id) exceeded $TimeoutSeconds seconds and was terminated; AeroLink never became ready. Step: production-launcher. Logs: $($helper.StdOutPath), $($helper.StdErrPath), $(Join-Path $Config.LogsPath 'remote-demo.log')"
    }
    return [pscustomobject]@{ Healthy = $false; HelperUsed = $true; ProcessId = $helper.Id; Step = 'production-launcher'; Detail = $detail; LogPath = (Join-Path $Config.LogsPath 'remote-demo.log') }
}

function Start-AeroLinkRemoteDemoNgrok {
    <#
      .SYNOPSIS Starts the protected ngrok tunnel for one recovery attempt.
      .DESCRIPTION
        Inside a HOME transition the tunnel is a preserved service: it is obtained by LAUNCH REQUEST from the outer
        authority, which creates it outside the transition job with only its own log handles, grants operator
        access, and commits it only after the public endpoint returns 401. What comes back behaves like the process
        object callers already use (Id, HasExited, Kill, WaitForExit), with Kill bound to the exact registered
        identity. Outside a transition the process is started directly, as before.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        $Run
    )
    $logDirectory = $Config.LogsPath
    if (-not (Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null }
    if (-not (Test-Path -LiteralPath $Config.StatePath)) { New-Item -ItemType Directory -Path $Config.StatePath -Force | Out-Null }
    $stdout = Join-Path $logDirectory 'ngrok.stdout.log'
    $stderr = Join-Path $logDirectory 'ngrok.stderr.log'
    $contract = @(Get-AeroLinkRemoteDemoNgrokArguments -Config $Config)
    $argumentLine = ($contract | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $handoff = Get-AeroLinkTransitionHandoffFromEnvironment
    if ($handoff) {
        $response = Request-AeroLinkServiceLaunch -Handoff $handoff -Role tunnel -FilePath ([IO.Path]::GetFullPath($Config.NgrokExecutable)) -Arguments $argumentLine `
            -StandardOutput $stdout -StandardError $stderr -Readiness @{ kind = 'tunnel'; publicUrl = $Config.PublicUrl } `
            -ReadinessTimeoutSeconds $script:AeroLinkNgrokProtectionWaitSeconds -GrantOperatorAccessArguments $contract
        if ([string]$response.outcome -ne 'Succeeded' -or -not $response.restored) {
            throw "The protected tunnel was not restored by the transition authority ($($response.outcome)/$($response.currentHealth)): $($response.detail)"
        }
        $tunnel = [pscustomobject]@{ Id = [int]$response.processId; StartedAt = [string]$response.startedAt; Image = [string]$response.image }
        $tunnel | Add-Member -MemberType ScriptProperty -Name HasExited -Value {
            [AeroLink.TransitionV1.Kernel]::Classify($this.Id, (ConvertTo-AeroLinkUtcIso $this.StartedAt), $this.Image) -ne 'RunningMatch'
        }
        $tunnel | Add-Member -MemberType ScriptMethod -Name Kill -Value {
            if (-not $this.HasExited) {
                Stop-AeroLinkProvenProcess -Process ([pscustomobject]@{ ProcessId = $this.Id; StartedAt = $this.StartedAt; ExecutablePath = $this.Image })
            }
        }
        $tunnel | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($Milliseconds) return $this.HasExited }
        return $tunnel
    }
    $process = Start-Process -FilePath $Config.NgrokExecutable -ArgumentList $argumentLine -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        Grant-AeroLinkCreatedProcessAccess -ProcessId $process.Id -StartedAt $process.StartTime.ToUniversalTime() `
            -ExpectedExecutable $Config.NgrokExecutable -ExpectedArguments $contract
    }
    catch {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        throw
    }
    return $process
}

function Test-AeroLinkRemoteDemoRuntimeMatchesSource {
    <#
      .SYNOPSIS Whether the API on the configured local port is running the verified production source, in
        HOME production mode.
      .DESCRIPTION
        Fails closed. A process that publishes no identity is an older build and cannot be proven to be the
        right one; a process reporting another mode or another source identity is not the one the public
        tunnel should be put in front of.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)][string]$ExpectedSourceIdentity,
        [scriptblock]$RuntimeIdentityProbe
    )
    if (-not $RuntimeIdentityProbe) {
        $instance = Get-AeroLinkInstanceConfig -ProductRoot (Join-Path $Config.AeroLinkRoot 'product') -Mode HomeCanonical
        $disposition = Resolve-AeroLinkRuntimeDisposition -Port ([uri]$Config.LocalApiBaseUri).Port -BaseUri $Config.LocalApiBaseUri `
            -ExpectedMode 'HOME-PRODUCTION' -ExpectedSourceIdentity $ExpectedSourceIdentity `
            -ExpectedInstanceId $instance.InstanceId -ExpectedClassification $instance.Classification `
            -OwnershipFragments @((Join-Path $Config.AeroLinkRoot 'product\src\AeroLink.Api'))
        return [pscustomobject]@{ Matches = ($disposition.Disposition -eq 'Reuse'); Detail = $disposition.Detail }
    }
    $identity = & $RuntimeIdentityProbe $Config
    if ($null -eq $identity) {
        return [pscustomobject]@{ Matches = $false; Detail = 'The local AeroLink publishes no runtime identity, so it cannot be proven to be the verified production source. It will be restarted rather than exposed.' }
    }
    if ([string]$identity.mode -ne 'HOME-PRODUCTION') {
        return [pscustomobject]@{ Matches = $false; Detail = "The local AeroLink reports mode $($identity.mode); HOME-PRODUCTION was required before the public tunnel may be started." }
    }
    if ([string]$identity.sourceIdentity -ne $ExpectedSourceIdentity) {
        $running = [string]$identity.sourceShortSha
        $expected = $ExpectedSourceIdentity.Substring(0, [Math]::Min(8, $ExpectedSourceIdentity.Length))
        return [pscustomobject]@{ Matches = $false; Detail = "The local AeroLink is running source $running; the verified production source is $expected." }
    }
    return [pscustomobject]@{ Matches = $true; Detail = "Local AeroLink runs the verified production source $($identity.sourceShortSha) in $($identity.mode) mode." }
}

function Start-AeroLinkRemoteDemo {
    <#
      .SYNOPSIS Starts the local production AeroLink (if needed) and the protected ngrok tunnel.
      .DESCRIPTION
        Idempotent: if the expected AeroLink and protected tunnel are already
        healthy, it reports READY without creating duplicates. Fails closed and
        tears down only a just-started tunnel if the public endpoint is not
        protected.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [switch]$Scheduled,
        [scriptblock]$PostgresReadyTest,
        [scriptblock]$PostgresHelperLauncher,
        [scriptblock]$PostgresHelperStopper,
        [scriptblock]$LocalReadyTest,
        [scriptblock]$ProductionHelperLauncher,
        [scriptblock]$ProductionHelperStopper,
        [scriptblock]$NgrokLauncher,
        [scriptblock]$PublicProbe,
        [scriptblock]$LocalRuntimeProbe,
        # Brings the dedicated production source to current approved origin/main before anything is started.
        # Injectable so the contract suite can drive every source outcome without a clone or a network.
        [scriptblock]$SourceReconciler,
        # Reads /health/identity from the running local API. Injectable for the same reason.
        [scriptblock]$RuntimeIdentityProbe,
        [switch]$SkipSourceReconciliation,
        # These three are the component stages the continuation budget is composed from; they share its
        # constants so the composition cannot drift apart from the value derived at the top of this module.
        [int]$PostgresRecoveryTimeoutSeconds = $script:AeroLinkPostgresRecoveryTimeoutSeconds,
        # Same budget as the initiating attempt: recovery restores topology through the production launcher,
        # which can itself re-enter the clone-validated upgrade, so this is a second upgrade-capable attempt.
        [int]$ProductionTimeoutSeconds = $script:AeroLinkSupportedUpgradeTimeoutSeconds,
        [int]$NgrokProtectionWaitSeconds = $script:AeroLinkNgrokProtectionWaitSeconds
    )

    $run = New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'Remote demo start requested.'
    if ($null -eq $LocalReadyTest) { $LocalReadyTest = { param($C) Test-AeroLinkRemoteDemoLocalReady -Config $C } }

    # Source first, before PostgreSQL and long before ngrok.
    #
    # This is the whole 2026-09-03 correction: the source a recovery runs is the dedicated production
    # checkout, reconciled to current approved origin/main by strict fast-forward and revalidated — never
    # whichever branch the development checkout happens to be on, and never repaired into shape.
    $expectedSourceIdentity = $null
    $sourceFailure = $null
    if ($SkipSourceReconciliation -and -not $PSBoundParameters.ContainsKey('LocalReadyTest')) {
        $expectedSourceIdentity = (Get-AeroLinkSourceFingerprint -RepositoryRoot $Config.AeroLinkRoot).Identity
        if (-not $expectedSourceIdentity) { throw 'The continuation source identity cannot be established.' }
    }
    if (-not $SkipSourceReconciliation) {
        $reconcile = if ($SourceReconciler) { & $SourceReconciler $Config } else {
            Assert-AeroLinkDedicatedProductionSource -SourceRoot $Config.AeroLinkRoot | Out-Null
            # Two phases for the same reason the timed pass has them: an operator can run this with the demo
            # up, and fast-forwarding the working tree first would rewrite the files that process is executing.
            # Decide with a fetch (remote-tracking refs only), stop what is running out of the tree, advance.
            $inspect = Update-AeroLinkProductionSource -SourceRoot $Config.AeroLinkRoot -InspectOnly
            if ($inspect.Canonical -and $inspect.Action -eq 'UpdateAvailable') {
                Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Production source is behind; stopping the local runtime and the owned tunnel before advancing to $($inspect.TargetSha)."
                # The tunnel first, and it must actually come down. It forwards the public URL at
                # 127.0.0.1:5080, so leaving it up across the transition publishes whatever occupies that port
                # next - including a process whose identity has not been re-proved. Only the AeroLink-owned
                # tunnel is stopped; a foreign ngrok is a refusal, never a casualty.
                #
                # And that refusal has to stop the transition. Stop-AeroLinkRemoteDemo throws on a mismatched
                # ngrok BEFORE it reaches the loop that stops owned ones, so catching and continuing left the
                # owned tunnel publishing an absent API through the entire advance. Fail closed instead: the
                # source stays where it is, which is a state this machine already runs in.
                # The compensation boundary starts BEFORE the tunnel proof, not after it.
                #
                # Assert-AeroLinkOwnedTunnelStopped records the obligation between its stop and its proof, so
                # a post-stop enumeration failure leaves TeardownBegan true and throws. Entering the try only
                # after that call returned meant precisely that throw bypassed compensation: public endpoint
                # down, source unchanged, nothing restored. The obligation, not the call's position, decides
                # whether anything is owed.
                $obligation = New-AeroLinkProductionObligation -SourceRoot $Config.AeroLinkRoot -Config $Config -Policy KeepReady
                $sourceAdvancedIrreversibly = $false
                $priorTopology = [pscustomobject]@{ TunnelRunning = $obligation.PriorTunnel; RuntimeRunning = $obligation.PriorRuntime }
                $obligation.Stage = 'Quiescing'
                Save-AeroLinkProductionObligation -Obligation $obligation
                $advanced = $null
                try {
                    Assert-AeroLinkOwnedTunnelStopped -Config $Config -Run $run -Obligation $obligation | Out-Null
                    # Unconditionally, not "if it looks ready". A running process is what the tree is about to
                    # be rewritten under; an owned process that is up but unhealthy is exactly the one that
                    # must not be left executing deleted files. Each stop is recorded as it succeeds, and
                    # PostgreSQL is left alone: it does not execute out of the source working tree.
                    Stop-AeroLinkSourceExecutingProcesses -Config $Config -Obligation $obligation -Run $run | Out-Null
                    Save-AeroLinkProductionObligation -Obligation $obligation
                    $advanced = Update-AeroLinkProductionSource -SourceRoot $Config.AeroLinkRoot -AdvanceToSha $inspect.TargetSha
                    if ($advanced.Action -eq 'Updated' -and $advanced.Canonical) { $sourceAdvancedIrreversibly = $true }
                    if ($advanced.Action -eq 'Updated' -and $advanced.Canonical -and $env:AEROLINK_REMOTE_DEMO_HANDOFF -ne "$($Config.AeroLinkRoot)|$($advanced.HeadSha)") {
                        # The source generation changed, and this module is the old one. Everything after this
                        # point - readiness, identity, the tunnel, the 401 proof - must run from the updated
                        # entry point rather than from functions loaded before the advance.
                        #
                        # Handled HERE rather than by the outer catch, and deliberately. Once the advance has
                        # succeeded, a failure must not fall back into this module: the outer catch would
                        # report "the source was not advanced", which is untrue, and then resume the rest of
                        # the start path in pre-advance code - the exact generation the handoff prevents.
                        try {
                            Invoke-AeroLinkRemoteDemoHandoff -Config $Config -Scheduled:$Scheduled -Run $run -Topology $priorTopology -HeadSha $advanced.HeadSha | Out-Null
                        }
                        catch {
                            # Marked before throwing, and the outer catch re-throws on it: a throw here is still
                            # physically inside that try, and its compensation would report "the source was not
                            # advanced" - false - and resume the start path in pre-advance code.
                            #
                            # Not retried here. A second restoration from inside this attempt could run over the
                            # first continuation's live descendants; the outer authority collects this attempt,
                            # proves it quiescent, and only then admits a recovery attempt on the current source.
                            $sourceAdvancedIrreversibly = $true
                            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "The post-advance continuation failed: $($_.Exception.Message). Recovery belongs to the outer authority's next attempt."
                            throw "The production source WAS advanced to $($advanced.HeadSha), but the updated code could not complete the start: $($_.Exception.Message)"
                        }
                        return [pscustomobject]@{
                            # PublicUrl is populated because the CLI prints it unconditionally; a handed-off
                            # start that omitted it left the operator a blank "Public URL:" line after an
                            # otherwise successful update.
                            Ready = $true; Action = 'HandedOff'; PublicUrl = $Config.PublicUrl
                            Detail = "The production source was advanced to $($advanced.HeadSha) and the transition was completed by a fresh process running the updated source."
                        }
                    }
                }
                catch {
                    Save-AeroLinkProductionObligation -Obligation $obligation
                    # Past the point of no return: the source HAS advanced, so this module is the wrong
                    # generation to recover with and the compensation below would both lie about what
                    # happened and resume in pre-advance code. Propagate.
                    if ($sourceAdvancedIrreversibly) { throw }
                    # Nothing was taken down, so nothing is owed: the tunnel refused to stop, or a mismatched
                    # ngrok made the stop refuse. Fail closed, exactly as before.
                    if (-not $obligation.TeardownBegan) { throw }
                    $failure = $_.Exception.Message
                    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "The transition failed after teardown began: $failure. Falling back to the verified revision on disk."
                    $onDisk = Get-AeroLinkProductionSourcePosture -SourceRoot $Config.AeroLinkRoot
                    if (-not $onDisk.Canonical) {
                        throw "The production source transition failed after the tunnel was taken down ($failure), and the revision on disk is not canonical either: $($onDisk.Reason)"
                    }
                    if ($onDisk.Posture.HeadSha -ne $obligation.SourceBefore) {
                        try { Invoke-AeroLinkRemoteDemoHandoff -Config $Config -Scheduled:$Scheduled -Run $run -Topology $priorTopology -HeadSha $onDisk.Posture.HeadSha | Out-Null }
                        catch { throw "Transition failed ($failure). Current source is $($onDisk.Posture.HeadSha); fresh-source recovery failed: $($_.Exception.Message)" }
                        throw "Transition failed ($failure). Current source is $($onDisk.Posture.HeadSha); fresh-source recovery restored the prior topology."
                    }
                    $advanced = [pscustomobject]@{
                        Action = 'TransitionFailed'; Canonical = $true; HeadSha = $onDisk.Posture.HeadSha
                        TargetSha = $inspect.TargetSha; RemoteReachable = $true
                        Reason = "The source was not advanced because the transition failed after teardown began ($failure), so production is being started on the revision already on disk, main @ $($onDisk.Posture.ShortSha)."
                    }
                }
                if ($advanced.Canonical) { $advanced }
                else {
                    # The runtime is already down. A refused advance - origin/main moved again between the
                    # phases, most likely - must not also mean the demo stays off; the revision on disk is
                    # still the verified canonical one, so bring that back up and say what happened.
                    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "The source advance was refused after the runtime was stopped: $($advanced.Reason)"
                    $onDisk = Get-AeroLinkProductionSourcePosture -SourceRoot $Config.AeroLinkRoot
                    if ($onDisk.Canonical) {
                        [pscustomobject]@{
                            Action = 'AdvanceRefused'; Canonical = $true; HeadSha = $onDisk.Posture.HeadSha
                            TargetSha = $inspect.TargetSha; RemoteReachable = $true
                            Reason = "The source was not advanced ($($advanced.Reason)) so production is being started on the revision already on disk, main @ $($onDisk.Posture.ShortSha)."
                        }
                    }
                    else { $advanced }
                }
            }
            else { $inspect }
        }
        if (-not $reconcile.Canonical) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($reconcile.Reason)"
            throw "AEROLINK REMOTE DEMO NOT READY: $($reconcile.Reason)"
        }
        if ($reconcile.Action -in @('TransitionFailed', 'AdvanceRefused')) { $sourceFailure = $reconcile.Reason }
        $expectedSourceIdentity = [string]$reconcile.HeadSha
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Production source: $($reconcile.Action) - $($reconcile.Reason)"
    }

    $startedLocalForThisRun = $false
    $local = & $LocalReadyTest $Config
    if ($local.Ready -and $expectedSourceIdentity) {
        # A ready API is not necessarily THIS API. A healthy process from a previous revision is stale, and
        # reusing it would put the public tunnel in front of source nobody asked for; treating it as not-ready
        # sends it through the launcher, which stops only the process it owns and starts the right one.
        $match = Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $Config -ExpectedSourceIdentity $expectedSourceIdentity -RuntimeIdentityProbe $RuntimeIdentityProbe
        if (-not $match.Matches) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Local AeroLink is ready but does not match the production source: $($match.Detail)"
            $local = [pscustomobject]@{ Ready = $false; Detail = $match.Detail }
        }
    }
    if (-not $local.Ready) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'Local AeroLink not ready; starting/confirming PostgreSQL with a bounded recovery window.'
        $postgres = Start-AeroLinkRemoteDemoPostgres -Config $Config -Run $run `
            -ReadyTest $PostgresReadyTest -HelperLauncher $PostgresHelperLauncher -HelperStopper $PostgresHelperStopper `
            -RecoveryTimeoutSeconds $PostgresRecoveryTimeoutSeconds
        if (-not $postgres.Healthy) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($postgres.Detail)"
            throw "AEROLINK REMOTE DEMO NOT READY: $($postgres.Detail)"
        }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "PostgreSQL ready: $($postgres.Detail)"
        $launcher = Invoke-AeroLinkProductionLauncher -Config $Config -Run $run `
            -LocalReadyTest $LocalReadyTest -HelperLauncher $ProductionHelperLauncher -HelperStopper $ProductionHelperStopper `
            -TimeoutSeconds $ProductionTimeoutSeconds -ForceLaunch
        if (-not $launcher.Healthy) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($launcher.Detail)"
            throw "AEROLINK REMOTE DEMO NOT READY: $($launcher.Detail)"
        }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Production AeroLink ready: $($launcher.Detail)"
        $startedLocalForThisRun = [bool]$launcher.HelperUsed
        $local = & $LocalReadyTest $Config
        if (-not $local.Ready) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'AEROLINK REMOTE DEMO NOT READY: local AeroLink readiness lost after launcher.'
            throw 'AEROLINK REMOTE DEMO NOT READY: local AeroLink readiness lost after launcher.'
        }
    }
    # The last gate before the tunnel: the API that is about to be exposed publicly must be provably the
    # production source, in production mode. Readiness alone has never proven either.
    if ($expectedSourceIdentity) {
        $match = Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $Config -ExpectedSourceIdentity $expectedSourceIdentity -RuntimeIdentityProbe $RuntimeIdentityProbe
        if (-not $match.Matches) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($match.Detail)"
            throw "AEROLINK REMOTE DEMO NOT READY: $($match.Detail)"
        }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Runtime identity verified: $($match.Detail)"
    }
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Local AeroLink ready: $($local.Detail)"

    if (-not (Test-Path -LiteralPath $Config.NgrokExecutable -PathType Leaf)) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: ngrok executable missing $($Config.NgrokExecutable)."
        throw "Configured ngrok executable not found: $($Config.NgrokExecutable)"
    }
    if (-not (Test-Path -LiteralPath $Config.TrafficPolicyPath -PathType Leaf)) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: traffic policy missing $($Config.TrafficPolicyPath)."
        throw "Configured ngrok Traffic Policy not found: $($Config.TrafficPolicyPath)"
    }

    $processes = Get-AeroLinkRemoteDemoNgrokProcess -Config $Config
    if (@($processes.Owned).Count -gt 1) { throw 'Multiple owned ngrok processes are ambiguous; no READY state was asserted.' }
    if (@($processes.Mismatched).Count -gt 0) {
        $mismatchPids = (@($processes.Mismatched) | ForEach-Object { $_.ProcessId }) -join ', '
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: unexpected ngrok process(es) PID $mismatchPids."
        throw "An unexpected ngrok process (PID $mismatchPids) does not match the AeroLink remote-demo contract. Refusing to start or stop it."
    }

    if ($null -eq $PublicProbe) { $PublicProbe = { param($C) Test-AeroLinkRemoteDemoPublicProtection -Config $C } }
    $probe = & $PublicProbe $Config
    $decision = Get-AeroLinkRemoteDemoStartDecision `
        -LocalReady $local.Ready `
        -OwnedProcessPresent (@($processes.Owned).Count -gt 0) `
        -Protected $probe.Protected `
        -ProbeStatusCode $probe.StatusCode

    if ($decision.Decision -eq 'AlreadyReady') {
        $originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $Config -RuntimeProbe $LocalRuntimeProbe
        if (-not $originProof.Valid) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($originProof.Detail)"
            throw "The tunnel is protected, but reachable notification links are not attributable to the current AeroLink process. $($originProof.Detail) Stop the owned local stack, then start the remote demo again."
        }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'Remote demo already ready; no new processes started.'
        if ($sourceFailure) { throw "The source transition failed ($sourceFailure). The protected prior topology is ready again." }
        return [pscustomobject]@{ Ready = $true; PublicUrl = $Config.PublicUrl; Detail = "$($decision.Message) $($originProof.Detail)" }
    }
    if ($local.Ready -and $decision.Decision -eq 'CanStart' -and -not $startedLocalForThisRun) {
        $originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $Config -RuntimeProbe $LocalRuntimeProbe
        if (-not $originProof.Valid) {
            throw 'AeroLink is already running locally with an unknown notification-link origin. Stop the owned local stack, then start the remote demo so the protected PublicUrl is applied before mail is dispatched.'
        }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Existing local AeroLink has attributable notification origin; replacing its missing tunnel. $($originProof.Detail)"
    }
    if ($decision.Decision -ne 'CanStart') {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($decision.Message)"
        throw $decision.Message
    }

    $originRuntime = Get-AeroLinkRemoteDemoLocalRuntimeIdentity -Config $Config -RuntimeProbe $LocalRuntimeProbe
    if ($expectedSourceIdentity -and -not $LocalRuntimeProbe) {
        $originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $Config
        if (-not $originProof.Valid) { throw "The launch did not prove the notification origin before publication. $($originProof.Detail)" }
    }
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'Starting the protected ngrok tunnel.'
    if ($null -eq $NgrokLauncher) { $NgrokLauncher = { param($C, $R) Start-AeroLinkRemoteDemoNgrok -Config $C -Run $R } }
    $launched = & $NgrokLauncher $Config $run

    $probeResult = $null
    $ngrokDeadline = (Get-Date).AddSeconds($NgrokProtectionWaitSeconds)
    do {
        Start-Sleep -Milliseconds 1000
        $alive = Get-Process -Id $launched.Id -ErrorAction SilentlyContinue
        if (-not $alive) {
            $tail = Get-Content -LiteralPath (Join-Path $Config.LogsPath 'ngrok.stderr.log') -Tail 15 -ErrorAction SilentlyContinue
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: ngrok exited before becoming protected. $($tail -join ' ')"
            throw "The ngrok tunnel exited before becoming protected. $($tail -join ' ')"
        }
        $probeResult = & $PublicProbe $Config
        if ($probeResult.Protected) { break }
    } while ((Get-Date) -lt $ngrokDeadline)

    if (-not $probeResult.Protected) {
        if (-not $launched.HasExited) { $launched.Kill(); $launched.WaitForExit() }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: just-started tunnel not protected (expected 401, got $($probeResult.StatusCode)); torn down."
        throw "The just-started tunnel was not protected (expected 401, got $($probeResult.StatusCode)). It was torn down; nothing was left exposed."
    }

    $localAfter = & $LocalReadyTest $Config
    if (-not $localAfter.Ready) {
        if (-not $launched.HasExited) { $launched.Kill(); $launched.WaitForExit() }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: local AeroLink readiness lost after tunnel start."
        throw "The tunnel became protected but local AeroLink readiness was lost, so the just-started tunnel was stopped. $($localAfter.Detail)"
    }

    $runtime = Get-AeroLinkRemoteDemoLocalRuntimeIdentity -Config $Config -RuntimeProbe $LocalRuntimeProbe
    if (-not $runtime.Found) {
        if (-not $launched.HasExited) { $launched.Kill(); $launched.WaitForExit() }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($runtime.Detail)"
        throw "The tunnel became protected but the notification-link origin could not be attributed to the local AeroLink process, so the just-started tunnel was stopped. $($runtime.Detail)"
    }

    if (-not $originRuntime.Found -or $runtime.ProcessId -ne $originRuntime.ProcessId -or
        ([DateTimeOffset]$runtime.StartedAt).UtcDateTime.Ticks -ne ([DateTimeOffset]$originRuntime.StartedAt).UtcDateTime.Ticks) {
        if (-not $launched.HasExited) { $launched.Kill(); $launched.WaitForExit() }
        throw 'The API changed after origin proof and before final publication; the created tunnel was stopped.'
    }
    if ($expectedSourceIdentity) {
        $finalMatch = Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $Config -ExpectedSourceIdentity $expectedSourceIdentity -RuntimeIdentityProbe $RuntimeIdentityProbe
        $finalNgrok = Get-AeroLinkRemoteDemoNgrokProcess -Config $Config
        if (-not $finalMatch.Matches -or @($finalNgrok.Mismatched).Count -or @($finalNgrok.Owned).Count -ne 1 -or
            $finalNgrok.Owned[0].ProcessId -ne $launched.Id) {
            if (-not $launched.HasExited) { $launched.Kill(); $launched.WaitForExit() }
            throw 'The final runtime/tunnel ownership proof changed after publication; the created tunnel was stopped.'
        }
    }

    $state = [pscustomobject]@{
        Pid = $launched.Id
        NgrokExecutable = $Config.NgrokExecutable
        PublicUrl = $Config.PublicUrl
        NotificationBaseUrl = $Config.PublicUrl
        LocalApiPid = $runtime.ProcessId
        LocalApiStartedAt = $runtime.StartedAt
        Upstream = $Config.Upstream
        TrafficPolicyPath = $Config.TrafficPolicyPath
        StartedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    if (-not (Test-Path -LiteralPath $Config.StatePath)) { New-Item -ItemType Directory -Path $Config.StatePath -Force | Out-Null }
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Config.StatePath 'remote-demo-state.json') -Encoding UTF8

    if ($sourceFailure) { throw "The source transition failed ($sourceFailure). The protected prior topology is ready again." }
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO READY; protected tunnel PID $($launched.Id); $($probeResult.Detail)"
    return [pscustomobject]@{ Ready = $true; PublicUrl = $Config.PublicUrl; Detail = $probeResult.Detail }
}

function Stop-AeroLinkRemoteDemo {
    <#
      .SYNOPSIS Stops only the AeroLink-owned ngrok tunnel, and optionally the local stack.
      .DESCRIPTION
        Never kills an arbitrary ngrok process: ownership requires the exact
        executable plus public URL, upstream, and Traffic Policy contract.

        -Obligation is recorded INCREMENTALLY, after each individual stop succeeds. Nothing guarantees a
        single owned tunnel: the start path collapses any positive count to "owned process present", so two
        can exist. Recording only when the whole loop returned meant that with two tunnels - stop one
        succeeds, stop two throws, including the ordinary race where a process exits between enumeration and
        Stop-Process - the caller was told nothing had been torn down while the machine was half torn down.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [switch]$IncludeLocalStack,
        $Obligation
    )

    $processes = Get-AeroLinkRemoteDemoNgrokProcess -Config $Config
    if (@($processes.Mismatched).Count -gt 0) {
        $mismatchPids = (@($processes.Mismatched) | ForEach-Object { $_.ProcessId }) -join ', '
        throw "Refusing to stop: ngrok process(es) PID $mismatchPids do not match the AeroLink remote-demo contract."
    }
    foreach ($process in @($processes.Owned)) {
        Write-Host "Stopping the AeroLink-owned ngrok tunnel (PID $($process.ProcessId))."
        Stop-AeroLinkProvenProcess -Process $process -OnStopped {
            if ($Obligation) { $Obligation.TunnelWasRunning = $true; $Obligation.TeardownBegan = $true }
        }
    }
    if (@($processes.Owned).Count -eq 0) {
        Write-Host 'No AeroLink-owned ngrok tunnel is running.'
    }
    if ($IncludeLocalStack) {
        Write-Host 'Stopping the local AeroLink stack and repository-owned PostgreSQL.'
        & (Join-Path $Config.AeroLinkRoot 'product\scripts\Stop-AeroLink.ps1')
    }
    Write-Host 'AeroLink remote demo stopped. Configuration, evidence, database content, and credentials were not deleted.'
}

function Get-AeroLinkRemoteDemoTaskXml {
    <#
      .SYNOPSIS The current-user Scheduled Task XML for automatic recovery.
      .DESCRIPTION Contains no secrets: only the task identity, triggers,
        start-when-available settings, and the command that invokes the same
        tested start implementation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [string]$TaskName = $script:RemoteDemoTaskName,
        # Two coherent shapes, not four combinations.
        #
        # Unattended is a boot trigger under an S4U principal, which is what makes a reboot with nobody
        # signed in recover the demo. Measured on the HOME machine: Windows refuses BOTH a boot trigger and
        # an S4U principal to a non-elevated caller, so this shape needs one elevated install. Attended is
        # the shape that installs without administrator - a logon trigger under an interactive token, which
        # is the pre-#881 behaviour. It recovers after sign-in and NOT after an unattended reboot, so it is
        # a fallback that must be said out loud, never a default.
        [switch]$Attended
    )
    $scriptPath = Join-Path $Config.AeroLinkRoot 'product\scripts\AeroLinkRemoteDemo.ps1'
    # Boot AND logon in the unattended shape, both firing the same idempotent start.
    #
    # A LogonTrigger alone recovers after Sean signs in, which is not what "the machine rebooted" means. On
    # 2026-09-03 the reboot happened while nobody was at the keyboard. The boot trigger is the primary path;
    # the logon trigger stays as a second chance for the case where boot recovery could not complete (no
    # network yet, credentials not available), and overlapping runs are harmless because
    # MultipleInstancesPolicy is IgnoreNew and Start-AeroLinkRemoteDemo reports READY without creating a
    # duplicate API or a second tunnel.
    #
    # The principal stays the operator's own account rather than becoming SYSTEM: ngrok's agent
    # configuration and its credential store are per-user, and a SYSTEM task would find neither. S4U is the
    # way to run in that account without an interactive session and without storing a password.
    #
    # PT1M delay on the boot trigger: at fifteen seconds after boot the network stack, the user profile and
    # the disk are all still settling, and every prerequisite check would fail for reasons that resolve
    # themselves a minute later.
    $logonType = if ($Attended) { 'InteractiveToken' } else { 'S4U' }
    $bootTrigger = if ($Attended) { '' } else {
        @"
    <BootTrigger>
      <Enabled>true</Enabled>
      <Delay>PT1M</Delay>
    </BootTrigger>
"@
    }
    $description = if ($Attended) {
        'AeroLink protected remote-demo recovery (logon only, current user, no admin). Does NOT recover an unattended reboot.'
    } else {
        'AeroLink protected remote-demo recovery (boot and logon, current user).'
    }
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>$description</Description>
    <URI>\$TaskName</URI>
  </RegistrationInfo>
  <Triggers>
$bootTrigger    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$env:USERDOMAIN\$env:USERNAME</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$env:USERDOMAIN\$env:USERNAME</UserId>
      <LogonType>$logonType</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <RestartOnFailure>
      <Interval>PT5M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>$($script:AeroLinkInstalledTaskTimeLimit)</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -ExecutionPolicy Bypass -File "$scriptPath" -Action Start -Scheduled</Arguments>
    </Exec>
  </Actions>
</Task>
"@
}

function Get-AeroLinkReconcileTaskXml {
    <#
      .SYNOPSIS Scheduled Task XML for bounded production-source reconciliation while HOME stays up.
      .DESCRIPTION
        A machine that never reboots would otherwise run yesterday's main forever. This polls on a low-
        frequency cadence - thirty minutes by default, which is far below the rate at which anybody notices a
        demo is a merge behind, and far above the rate at which polling is a cost - and does nothing at all
        when origin/main has not moved.

        Polling rather than a webhook, deliberately: an inbound public endpoint to learn about a merge would
        be a far larger security surface than the problem justifies, and #881 rules it out.

        The reconcile action never modifies files underneath a running process without then restarting it:
        it fast-forwards the dedicated source, and the start it invokes sees a runtime whose source identity
        no longer matches and restarts the API it owns before re-proving the protected endpoint.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [string]$TaskName = $script:ReconcileTaskName,
        [ValidateRange(5, 1440)][int]$IntervalMinutes = 30,
        [ValidateSet('S4U', 'InteractiveToken')][string]$LogonType = 'S4U'
    )
    $scriptPath = Join-Path $Config.AeroLinkRoot 'product\scripts\AeroLinkRemoteDemo.ps1'
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>AeroLink production-source reconciliation (bounded polling, current user, no admin).</Description>
    <URI>\$TaskName</URI>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <Enabled>true</Enabled>
      <StartBoundary>2026-01-01T03:00:00</StartBoundary>
      <Repetition>
        <Interval>PT${IntervalMinutes}M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$env:USERDOMAIN\$env:USERNAME</UserId>
      <LogonType>$LogonType</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>$($script:AeroLinkInstalledTaskTimeLimit)</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -ExecutionPolicy Bypass -File "$scriptPath" -Action Reconcile -Scheduled</Arguments>
    </Exec>
  </Actions>
</Task>
"@
}

function Install-AeroLinkReconcileTask {
    <#
      .SYNOPSIS Registers the bounded reconciliation task against the dedicated production source, or refuses.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [string]$TaskName = $script:ReconcileTaskName,
        [ValidateRange(5, 1440)][int]$IntervalMinutes = 30,
        [switch]$AllowNonDedicatedSource
    )
    if (-not $AllowNonDedicatedSource) { Assert-AeroLinkDedicatedProductionSource -SourceRoot $Config.AeroLinkRoot | Out-Null }
    if (-not (Test-Path -LiteralPath $Config.StatePath)) { New-Item -ItemType Directory -Path $Config.StatePath -Force | Out-Null }
    $xmlPath = Join-Path $Config.StatePath 'production-source-reconcile-task.xml'
    # Unlike recovery, reconciliation loses nothing by falling back: a time trigger under an interactive
    # token registers without administrator and still polls while the operator is signed in, which is when a
    # HOME machine is running anyway. S4U is preferred only so it keeps polling across a lock or sign-out.
    $logonType = 'S4U'
    Set-Content -LiteralPath $xmlPath -Encoding Unicode -Value (Get-AeroLinkReconcileTaskXml -Config $Config -TaskName $TaskName -IntervalMinutes $IntervalMinutes -LogonType $logonType)
    & schtasks.exe /Create /TN $TaskName /XML $xmlPath /F
    if ($LASTEXITCODE -ne 0) {
        $logonType = 'InteractiveToken'
        Set-Content -LiteralPath $xmlPath -Encoding Unicode -Value (Get-AeroLinkReconcileTaskXml -Config $Config -TaskName $TaskName -IntervalMinutes $IntervalMinutes -LogonType $logonType)
        & schtasks.exe /Create /TN $TaskName /XML $xmlPath /F
        if ($LASTEXITCODE -ne 0) { throw "schtasks /Create failed for the reconciliation task with exit code $LASTEXITCODE." }
    }
    return [pscustomobject]@{ TaskName = $TaskName; IntervalMinutes = $IntervalMinutes; LogonType = $logonType; SourceRoot = $Config.AeroLinkRoot }
}

function Assert-AeroLinkOwnedTunnelStopped {
    <#
      .SYNOPSIS Takes the AeroLink-owned tunnel down and PROVES it is down, or refuses.
      .DESCRIPTION
        The transition that follows replaces whatever is listening on the local port the tunnel forwards to.
        If the owned tunnel survives that, the protected public URL points first at an absent API and then at
        whichever process takes the port - one whose runtime identity nothing has re-proved. So this is a
        precondition, not a courtesy.

        Two ways it used to fail open, both fixed here. Stop-AeroLinkRemoteDemo throws on a mismatched ngrok
        BEFORE it reaches the loop that stops owned tunnels, so a single unrelated ngrok on the machine meant
        the owned one was never stopped - and the caller caught that, logged it, and advanced anyway. And a
        stop that silently did nothing was indistinguishable from one that worked, so the result is re-read
        and required to show no owned process left.

        An ngrok that does not match the AeroLink contract is still never killed. It stops this transition
        instead, which is the correct trade: not updating the source is a state the machine already runs in.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        $Run,
        # The caller's transition obligation. Recorded INTO rather than returned, because the interesting
        # failures happen after the obligation exists and a return value never arrives - see below.
        $Obligation,
        # Test seams, so the failure this function exists to survive can be driven deterministically: an
        # enumeration that succeeds, a stop that succeeds, and a post-stop proof that then throws.
        [scriptblock]$ProcessProbe,
        [scriptblock]$Stopper
    )
    # Was a tunnel actually up? The caller needs to know, because a transition that takes one down owes the
    # operator one back - and a transition must never start a tunnel that was not running to begin with.
    $before = if ($ProcessProbe) { & $ProcessProbe 'before' } else { Get-AeroLinkRemoteDemoNgrokProcess -Config $Config }
    $wasRunning = @($before.Owned).Count -gt 0

    # The obligation is threaded INTO the stop, so a partial teardown of several owned tunnels is recorded as
    # it happens rather than only if the whole loop returns.
    try { if ($Stopper) { & $Stopper $Config | Out-Null } else { Stop-AeroLinkRemoteDemo -Config $Config -Obligation $Obligation | Out-Null } }
    catch {
        if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "The owned tunnel could not be stopped, so the source transition was abandoned: $($_.Exception.Message)" }
        throw "The AeroLink-owned public tunnel could not be stopped, so the production source was NOT advanced and nothing was restarted: $($_.Exception.Message)"
    }

    # The obligation is recorded HERE, between the stop and the proof, and that placement is the point.
    #
    # Fail-closed enumeration is right: an unreadable process table is unknown, never none. But it means the
    # post-stop re-read can throw on a transient WMI failure AFTER a tunnel has actually been taken down -
    # and if the caller only learns `WasRunning` from a return value, that throw loses the fact that anything
    # is owed. The caller then unwinds believing it took nothing down, with the public endpoint dark.
    if ($Obligation) {
        $Obligation.TunnelWasRunning = $wasRunning
        if ($wasRunning) { $Obligation.TeardownBegan = $true }
    }

    $remaining = if ($ProcessProbe) { & $ProcessProbe 'after' } else { Get-AeroLinkRemoteDemoNgrokProcess -Config $Config }
    if (@($remaining.Owned).Count -gt 0) {
        $pids = (@($remaining.Owned) | ForEach-Object { $_.ProcessId }) -join ', '
        if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "The owned tunnel is still running (PID $pids); the source transition was abandoned." }
        throw "The AeroLink-owned public tunnel is still running (PID $pids) after being asked to stop, so the production source was NOT advanced. The public endpoint must not forward to a port whose process is being replaced."
    }
    if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "The owned public tunnel is down (it was $(if ($wasRunning) { 'running' } else { 'not running' }) before this)." }
    return [pscustomobject]@{ Stopped = $true; WasRunning = $wasRunning }
}

function New-AeroLinkTransitionObligation {
    <#
      .SYNOPSIS What a source transition took down, and therefore owes back.
      .DESCRIPTION
        One invariant, written down once: once a transition takes a supported service down, every later exit
        path either completes the transition or restores exactly what was running before - and only what was
        running before. Three paths advance the production source (the production launcher's bootstrap hook,
        the operator remote-demo start, and the scheduled reconciliation), and each had grown its own
        almost-equivalent compensation with a different hole in it.

        Mutable and passed by reference on purpose. The obligation has to be recorded at the moment a teardown
        step SUCCEEDS, not at the moment the whole teardown returns, because the failures that matter happen
        in between and a return value never arrives for those.
    #>
    [CmdletBinding()]
    param()
    return [pscustomobject]@{
        TeardownBegan     = $false
        TunnelWasRunning  = $false
        RuntimeWasRunning = $false
        # Exact prior topology, recorded before anything is touched. Distinct from the "WasRunning" fields
        # above, which record what teardown ACTUALLY stopped: the two can differ when a stop fails partway,
        # and discharge needs the prior topology rather than the teardown's own account of itself.
        PriorTunnel       = $false
        PriorRuntime      = $false
        Discharged        = $false
        SourceRoot        = $null
        SourceBefore      = $null
        Policy            = 'Preserve'
        PublicOrigin      = $null
        Stage             = 'Captured'
        RuntimeIdentity   = $null
        LocalReady        = $false
        PublicProtected   = $false
        OriginValid       = $false
    }
}

function Get-AeroLinkServiceTopology {
    <#
      .SYNOPSIS What is actually RUNNING, by ownership - not what is healthy, and not what is configured.
      .DESCRIPTION
        Three things were being confused, and each confusion published or destroyed a service.

        Configuration is not topology: a remote-demo configuration file outlives a demo somebody deliberately
        stopped, so treating its existence as "a tunnel is up" let an update publish an endpoint nobody asked
        to publish.

        Readiness is not topology either, and this is the subtler one. The rule that governs a source advance
        is about a RUNNING process - an owned API that is up but unhealthy or stale is exactly the process
        that must not be left executing files that have been replaced. Recording topology from a readiness
        probe meant such a process was stopped while the record said nothing had been running, so discharge
        then faithfully restored the wrong topology.

        So topology is ownership: is there an AeroLink-owned ngrok matching the contract, and is there an
        AeroLink-owned listener on the local port attributable to this source's API project directory.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [int]$Port = 0,
        # Contract callers may provide the already-enumerated process fixture used by
        # Get-AeroLinkRemoteDemoNgrokProcess. Omitting it preserves the live ownership
        # probe used by production callers.
        [object[]]$ProcessInfos
    )
    if (-not $Port) { $Port = (Get-AeroLinkServiceEndpoints).ApiPort }
    $apiProjectDirectory = Join-Path $Config.AeroLinkRoot 'product\src\AeroLink.Api'
    $owner = Get-AeroLinkPortOwner -Port $Port

    # A listener that is PRESENT but cannot be attributed is unknown, and unknown fails closed here rather
    # than collapsing into "no runtime is running".
    #
    # The Boolean hid a third state and the third state is the dangerous one. Ambiguous, or attributable=false
    # because the process could not be read, produced exactly the same topology as a free port - while
    # teardown re-queries ownership later, so a transient first failure followed by a successful second read
    # would stop the owned API against a record saying nothing had been running, and discharge would then
    # faithfully leave it down. Refusing at the snapshot means the two reads can never disagree about
    # something this obligation depends on.
    if ($owner.Found -and (-not $owner.Attributable -or $owner.Ambiguous)) {
        throw "AeroLink cannot establish what is listening on port ${Port}: $($owner.Detail) The production source was NOT advanced and nothing was stopped - a listener whose ownership cannot be read is unknown, never absent."
    }

    # Owned means attributable to THIS source's API project directory. A listener attributable to something
    # else is not ours to count as our topology, and is not ours to stop either.
    $ownedRuntime = $owner.Found -and
        (Test-AeroLinkProcessOwnership -CommandLine $owner.CommandLine -ExecutablePath $owner.ExecutablePath -OwnershipFragments @($apiProjectDirectory))
    if ($owner.Found -and -not $ownedRuntime) { throw 'The local listener belongs to another source or application. Nothing was stopped.' }
    $tunnels = if ($PSBoundParameters.ContainsKey('ProcessInfos')) {
        Get-AeroLinkRemoteDemoNgrokProcess -Config $Config -ProcessInfos $ProcessInfos
    }
    else {
        Get-AeroLinkRemoteDemoNgrokProcess -Config $Config
    }
    if (@($tunnels.Mismatched).Count -gt 0) { throw 'Ngrok ownership is unknown or contradicts the configured launch contract. Nothing was stopped.' }
    if (@($tunnels.Owned).Count -gt 1) { throw 'Multiple owned ngrok tunnels are ambiguous. Nothing was stopped.' }
    return [pscustomobject]@{
        TunnelRunning  = (@($tunnels.Owned).Count -eq 1)
        RuntimeRunning = [bool]$ownedRuntime
        RuntimeDetail  = $owner.Detail
    }
}

function Save-AeroLinkProductionObligation {
    param([Parameter(Mandatory)]$Obligation)
    $env:AEROLINK_PRODUCTION_OBLIGATION = $Obligation | ConvertTo-Json -Depth 12 -Compress
    if ($env:AEROLINK_TRANSITION_JOURNAL) {
        $path = $env:AEROLINK_TRANSITION_JOURNAL
        $root = Split-Path -Parent (Split-Path -Parent $path)
        $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            @{ InstallationRoot = $root; Obligation = $Obligation } | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $temporary -Encoding UTF8
            Move-Item -LiteralPath $temporary -Destination $path -Force
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    }
}

function New-AeroLinkProductionObligation {
    param([Parameter(Mandatory)][string]$SourceRoot, $Config, [string]$Policy = 'Preserve')
    $obligation = New-AeroLinkTransitionObligation
    $obligation.SourceRoot = $SourceRoot
    $obligation.SourceBefore = (Get-AeroLinkSourceFingerprint -RepositoryRoot $SourceRoot).Sha
    $obligation.Policy = $Policy
    if ($Config) {
        $topology = Get-AeroLinkServiceTopology -Config $Config
        $obligation.PriorTunnel = $topology.TunnelRunning
        $obligation.PriorRuntime = $topology.RuntimeRunning
        $obligation.RuntimeIdentity = Get-AeroLinkRuntimeIdentity -BaseUri $Config.LocalApiBaseUri
        $obligation.LocalReady = (Test-AeroLinkRemoteDemoLocalReady -Config $Config).Ready
        if ($topology.TunnelRunning) {
            $obligation.PublicProtected = (Test-AeroLinkRemoteDemoPublicProtection -Config $Config).Protected
            $obligation.OriginValid = (Test-AeroLinkRemoteDemoNotificationOriginProof -Config $Config).Valid
            if (-not $obligation.PublicProtected -or -not $obligation.OriginValid) {
                throw "An owned tunnel is running but its safe restoration contract is incomplete (public 401=$($obligation.PublicProtected), notification origin=$($obligation.OriginValid), local ready=$($obligation.LocalReady)). Source/runtime replacement was refused; the running topology was not treated as OFF."
            }
            $obligation.PublicOrigin = $Config.PublicUrl
        }
    }
    else {
        if (@(Get-CimInstance Win32_Process -Filter "Name='ngrok.exe'" -ErrorAction Stop).Count) {
            throw 'Ngrok is running but there is no usable AeroLink tunnel contract. Source/runtime replacement was refused.'
        }
        $owner = Get-AeroLinkPortOwner -Port (Get-AeroLinkServiceEndpoints).ApiPort
        if ($owner.Found -and ($owner.Ambiguous -or -not $owner.Attributable -or
            -not (Test-AeroLinkProcessOwnership -CommandLine $owner.CommandLine -ExecutablePath $owner.ExecutablePath -OwnershipFragments @((Join-Path $SourceRoot 'product\src\AeroLink.Api'))))) {
            throw 'Local listener ownership cannot be established; no transition was started.'
        }
        $obligation.PriorRuntime = $owner.Found
    }
    if ($obligation.PriorRuntime) {
        $identity = Get-AeroLinkRuntimeIdentity -BaseUri (Get-AeroLinkServiceEndpoints).ApiBaseUri
        $expected = Get-AeroLinkInstanceConfig -ProductRoot (Join-Path $SourceRoot 'product') -Mode HomeCanonical
        if ($identity -and (-not $identity.PSObject.Properties['instance'] -or
            $identity.instance.id -ne $expected.InstanceId -or $identity.instance.classification -ne $expected.Classification)) {
            throw 'The running API does not prove the expected installation binding. Source/runtime replacement was refused.'
        }
    }
    if ($env:AEROLINK_TRANSITION_JOURNAL -and (Test-Path -LiteralPath $env:AEROLINK_TRANSITION_JOURNAL)) {
        $saved = (Get-Content -LiteralPath $env:AEROLINK_TRANSITION_JOURNAL -Raw | ConvertFrom-Json).Obligation
        if (-not $saved.Discharged -and $saved.Stage -in @('Quiescing','Quiesced')) {
            if ($saved.SourceRoot -ine $SourceRoot -or ($saved.PriorTunnel -and
                (-not $Config -or $saved.PublicOrigin -ine $Config.PublicUrl))) { throw 'Interrupted transition source/public-origin binding contradicts current configuration.' }
            $obligation.PriorTunnel = [bool]$saved.PriorTunnel
            $obligation.PriorRuntime = [bool]$saved.PriorRuntime
            $obligation.PublicOrigin = $saved.PublicOrigin
            $obligation.Policy = $saved.Policy
            $obligation.TeardownBegan = $true
            $obligation.Stage = 'Quiesced'
        }
    }
    return $obligation
}

function Stop-AeroLinkProductionTransition {
    param([Parameter(Mandatory)]$Obligation, $Config)
    try {
        $Obligation.Stage = 'Quiescing'
        Save-AeroLinkProductionObligation -Obligation $Obligation
        if ($Config) { Assert-AeroLinkOwnedTunnelStopped -Config $Config -Obligation $Obligation | Out-Null }
        Stop-AeroLinkOwnedListener -Port (Get-AeroLinkServiceEndpoints).ApiPort -OwnershipFragments @((Join-Path $Obligation.SourceRoot 'product\src\AeroLink.Api')) -OnStopped {
            $Obligation.RuntimeWasRunning = $true
            $Obligation.TeardownBegan = $true
        } | Out-Null
        $Obligation.Stage = 'Quiesced'
    }
    finally { Save-AeroLinkProductionObligation -Obligation $Obligation }
}

function Restore-AeroLinkServiceTopology {
    <#
      .SYNOPSIS Puts back exactly what was running, and nothing that was not.
      .DESCRIPTION
        The discharge half of the transition obligation, and the only place that decides what "restore" means.
        It was previously spelled out at each exit path, which is why the failure paths and the success paths
        disagreed: a failed advance under the preserve policy still called Start-AeroLinkRemoteDemo, so a
        runtime-only installation came back as runtime + public tunnel, and an installation with nothing
        running at all could acquire a whole demo from a compensation branch.

        KeepReady is the scheduled recovery policy and is deliberately different: having the demo up is the
        entire job of a recovery timer, so it starts the demo whatever the prior topology was.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)]$Topology,
        [switch]$KeepReady,
        [switch]$Scheduled,
        $Run,
        # A continuation already runs the source its delegate advanced to; reconciling again from inside it would
        # start a second advance within the same attempt.
        [switch]$SkipSourceReconciliation
    )
    if ($KeepReady -or $Topology.TunnelRunning) {
        $why = if ($KeepReady) { 'recovery policy is keep-ready' } else { 'a public tunnel was running before this transition' }
        if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Restoring the protected remote demo ($why)." }
        return Start-AeroLinkRemoteDemo -Config $Config -Scheduled:$Scheduled -SkipSourceReconciliation:$SkipSourceReconciliation
    }
    if ($Topology.RuntimeRunning) {
        if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message 'Restoring the local production runtime only; no public tunnel was running before this transition.' }
        & (Join-Path $Config.AeroLinkRoot 'product\scripts\Start-AeroLinkProduction.ps1') -DoNotOpenBrowser | Out-Null
        return [pscustomobject]@{ Detail = 'Local production was restarted. No tunnel was running before this transition, so none was started.' }
    }
    if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message 'Nothing was running before this transition, so nothing was started.' }
    return [pscustomobject]@{ Detail = 'Nothing was running before this transition, so nothing was started.' }
}

function Stop-AeroLinkSourceExecutingProcesses {
    <#
      .SYNOPSIS Stops what is executing out of the working tree, recording each stop as it succeeds.
      .DESCRIPTION
        Deliberately NOT Stop-AeroLink.ps1. Two reasons, and the second is the interesting one.

        It is not atomic: it stops the listeners and then stops PostgreSQL, so a successful stop of port 5080
        followed by a PostgreSQL failure meant the caller's obligation - recorded only when the whole script
        returned - said nothing had been taken down while production was already gone.

        And PostgreSQL does not execute out of the source working tree. Its binaries and cluster live under
        the INSTALLATION (`product\.local\postgresql`), which the production clone reaches through a pointer;
        a fast-forward of the source cannot touch either. Stopping it for a source transition was never
        necessary, and stopping it made the teardown both slower and impossible to compensate cleanly. The
        thing that must not be executing replaced files is the API - and the Vite server, in development.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        $Obligation,
        $Run
    )
    $apiProjectDirectory = Join-Path $Config.AeroLinkRoot 'product\src\AeroLink.Api'
    $clientRoot = Join-Path $Config.AeroLinkRoot 'product\client'
    $stoppedAnything = $false

    $api = Stop-AeroLinkOwnedListener -Port (Get-AeroLinkServiceEndpoints).ApiPort -OwnershipFragments @($apiProjectDirectory) -OnStopped {
        if ($Obligation) { $Obligation.RuntimeWasRunning = $true; $Obligation.TeardownBegan = $true }
    }
    if ($api.Stopped) {
        $stoppedAnything = $true
        # Recorded HERE, not after the last stop below: a later failure must not erase the fact that this one
        # succeeded and production is already down.
        if ($Obligation) { $Obligation.RuntimeWasRunning = $true; $Obligation.TeardownBegan = $true }
        if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Stopped the owned production runtime: $($api.Detail)" }
    }

    $vite = Stop-AeroLinkOwnedListener -Port 5173 -OwnershipFragments @($clientRoot)
    if ($vite.Stopped) {
        $stoppedAnything = $true
        if ($Obligation) { $Obligation.TeardownBegan = $true }
        if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Stopped the owned client dev server: $($vite.Detail)" }
    }

    return [pscustomobject]@{ Stopped = $stoppedAnything; Api = $api; Client = $vite }
}

function Invoke-AeroLinkRemoteDemoHandoff {
    <#
      .SYNOPSIS Continues a post-advance transition in a FRESH process from the source that was just written,
        CONTAINED in the outer authority's transition job.
      .DESCRIPTION
        A source advance rewrites the control plane, and this module is part of it: after the advance, OLD
        orchestration is still resident, invoking NEW subordinate scripts, and in-memory functions cannot be
        reloaded in place. So after an advance the rest of the transition runs in a new process from the updated
        source - the continuation actor.

        What changed (#1041, #1043, #1053). The continuation used to be an unowned child whose descendants nothing
        contained: a "timed out" continuation could leave its migrations running, recovery then started over them,
        and a restored API held this process's output pipe so the wait never returned. Now this runs only inside a
        HOME transition whose outer authority owns ONE kill-on-close job:

          * the continuation is created in that job (it inherits this delegate's membership), so every descendant
            it starts is collected with the attempt and "stopped" is one kernel observation by the outer;
          * it obtains every service that must outlive the job only by LAUNCH REQUEST to the outer's authority,
            so nothing it starts holds this process's output open;
          * it runs at most ONCE per attempt. A failed continuation is reported, never retried here: a second
            restoration belongs to the outer's next attempt, admitted only after this attempt is proven
            quiescent - a retry from inside the job could run over the first continuation's live descendants.

        The continuation proves it runs the exact source this delegate left on disk before it touches anything,
        and its result is read against the result contract (exit code and outcome must agree).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [switch]$Scheduled,
        $Run,
        # The prior topology and the policy the continuation must discharge.
        $Topology,
        [switch]$PreserveServiceState,
        # The revision handed off, recorded with the request.
        [string]$HeadSha,
        # Bounded by the attempt's own deadline; a smaller value exists only for the contract suite.
        [int]$TimeoutSeconds = $script:AeroLinkTransitionContinuationTimeoutSeconds
    )
    $handoff = Get-AeroLinkTransitionHandoffFromEnvironment
    if (-not $handoff) {
        throw 'A post-advance continuation runs only inside a HOME transition owned by an outer authority, and this process has none. Nothing was started.'
    }
    $attempt = Get-AeroLinkAttemptPaths ([string]$handoff.attemptRoot)
    $requestPath = Join-Path $attempt.Root 'continuation-request.json'
    if (Test-Path -LiteralPath $requestPath) {
        throw "This attempt already ran its continuation. A further restoration belongs to the outer authority's next attempt, after this one is proven quiescent; nothing was started."
    }
    $script = Join-Path $Config.AeroLinkRoot 'product\scripts\Invoke-AeroLinkTransitionActor.ps1'
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
        throw "The source on disk has no transition actor at $script, so the transition cannot be continued on it."
    }
    $fingerprint = Get-AeroLinkSourceFingerprint -RepositoryRoot $Config.AeroLinkRoot
    if (-not $fingerprint -or [string]::IsNullOrWhiteSpace([string]$fingerprint.Identity)) {
        throw 'The source on disk cannot be identified, so no continuation can prove it runs it.'
    }
    if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message 'Handing the rest of the transition to a fresh, contained process running the source on disk.' }
    Publish-AeroLinkJsonAtomic -Path $requestPath -Value ([ordered]@{
            sourceRoot = $Config.AeroLinkRoot; sourceIdentity = [string]$fingerprint.Identity; headSha = $HeadSha
            keepReady = (-not $PreserveServiceState); scheduled = [bool]$Scheduled
            topology = [ordered]@{ tunnelRunning = [bool]($Topology -and $Topology.TunnelRunning); runtimeRunning = [bool]($Topology -and $Topology.RuntimeRunning) }
            at = (Get-Date).ToUniversalTime().ToString('o') })
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # File-redirected to logs unique to this attempt, and waited on the PROCESS HANDLE. Created by plain
    # CreateProcess from inside the transition job, so it is a member of that job from its first instruction.
    $child = Start-Process -FilePath $powershell -WindowStyle Hidden -PassThru `
        -ArgumentList ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $script + '" -HandoffFile "' + $attempt.Handoff + '" -Phase Restore') `
        -RedirectStandardOutput (Join-Path $attempt.Logs 'continuation.stdout.log') -RedirectStandardError (Join-Path $attempt.Logs 'continuation.stderr.log')
    $childHandle = $child.Handle
    $deadlineUtc = ConvertTo-AeroLinkUtcDate $handoff.deadlineUtc
    $bound = [Math]::Min([double]$TimeoutSeconds, [Math]::Max(1, ($deadlineUtc - (Get-Date).ToUniversalTime()).TotalSeconds - 30))
    if (-not $child.WaitForExit([int]($bound * 1000))) {
        # Not terminated here: the outer's job collects it with everything it started, and observes that.
        throw "The transition continuation (PID $($child.Id)) did not finish within $([int]$bound) seconds. The outer authority collects the attempt; this delegate did not retry."
    }
    $exitCode = $null
    try { $exitCode = [AeroLink.ProcessAccess]::ExitCode($childHandle) } catch { $exitCode = $null }
    $outcome = Test-AeroLinkActorOutcome -Path $attempt.ContinuationOutcome -Role continuation
    $mismatch = Test-AeroLinkActorExitMatchesOutcome -ExitCode $exitCode -Outcome $outcome -Actor 'Continuation'
    if ($Run) { Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "The transition continuation exited $exitCode with outcome $($outcome.Class)/$($outcome.Decision)." }
    if ($outcome.Class -ne 'Valid') { throw "The transition continuation's result is $($outcome.Class.ToLower()) ($($outcome.Detail)); exit code $exitCode." }
    if ($mismatch) { throw "The transition continuation's exit code contradicts its outcome ($mismatch)." }
    if ($outcome.Decision -ne 'Completed') { throw "The updated source could not complete the transition: $($outcome.Failures -join '; ')" }
    return [pscustomobject]@{ Detail = 'The transition was completed by a fresh, contained process running the updated source.'; ExitCode = $exitCode }
}

function Get-AeroLinkTransitionContinuation {
    <#
      .SYNOPSIS Reads and CONSUMES a continuation handed to this process by the one that advanced the source.
      .DESCRIPTION
        One-shot: cleared as it is read, and bound to a source root so a value left in an environment cannot
        be picked up by an unrelated launcher. Returns $null when there is nothing to continue.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SourceRoot)
    $raw = $env:AEROLINK_TRANSITION_CONTINUATION
    $env:AEROLINK_TRANSITION_CONTINUATION = $null
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $parsed = $null
    try { $parsed = $raw | ConvertFrom-Json }
    catch { throw "A transition continuation was handed to this process but could not be read: $($_.Exception.Message). Nothing was started; the prior service topology is unknown to this process." }
    $handedRoot = [string]$parsed.sourceRoot
    if ([IO.Path]::GetFullPath($handedRoot).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\', '/')) { return $null }
    return [pscustomobject]@{
        KeepReady = [bool]$parsed.keepReady
        Topology  = [pscustomobject]@{ TunnelRunning = [bool]$parsed.priorTunnel; RuntimeRunning = [bool]$parsed.priorRuntime }
    }
}

function Invoke-AeroLinkProductionSourceReconciliation {
    <#
      .SYNOPSIS One bounded reconciliation pass: decide, stop, advance, restart - in that order.
      .DESCRIPTION
        Does nothing when origin/main has not moved, which is the overwhelmingly common case and the reason
        this can afford to run on a timer at all. When it has moved, the restart goes through the ordinary
        start path so every existing gate still applies - canonical source, database upgrade posture, runtime
        identity, and the 401 proof before the tunnel is declared ready.

        The order is the safety property, not a style choice. Fast-forwarding first and restarting afterwards
        rewrites assemblies, EF migrations and the built client bundle underneath a process that is serving
        the public demo, over however long the restart takes. Between those two moments the running AeroLink
        is executing deleted or replaced files while its database is one revision behind what is now on disk -
        the exact class of half-swapped state #881 exists to remove. So: inspect (a fetch writes only
        remote-tracking refs, which nothing running reads), decide, stop the runtime we own, and only then
        advance the working tree and start the new revision.

        If the advance refuses after the runtime has been stopped, the pass still starts AeroLink again on the
        revision that is actually on disk. A refused update must not leave the machine down.

        Two policies, named rather than assumed. The SCHEDULED pass is keep-ready: recovery exists to have the
        demo up, so it ends by starting it whether or not it was up when the timer fired. An OPERATOR update
        (-PreserveServiceState) must not do that: a remote-demo configuration file persists after somebody
        deliberately stops the demo, so treating its existence as service state would let `Update` publish an
        ngrok endpoint nobody asked to publish. Configuration existence is not service-state evidence.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [switch]$Scheduled,
        # Restore exactly what was running, and nothing that was not. For explicit operator actions; the
        # scheduled pass deliberately leaves this off, because keeping the demo up is its whole job.
        [switch]$PreserveServiceState,
        # Reports what is up before teardown. Injectable so the contract suite can drive both policies.
        [scriptblock]$ServiceStateProbe,
        # Phase 1: fetch and decide, changing nothing. Injectable so the contract suite can drive every
        # source outcome and assert the ordering without a clone or a network.
        [scriptblock]$SourceInspector,
        # Phase 2a: take down the owned public tunnel before the endpoint it forwards to disappears.
        [scriptblock]$TunnelStopper,
        # Phase 2b: stop the runtime executing out of the working tree about to be rewritten.
        [scriptblock]$RuntimeStopper,
        # Phase 3: advance the working tree to the revision phase 1 decided on.
        [scriptblock]$SourceAdvancer,
        # Phase 4: the full start path, with all of its gates.
        [scriptblock]$Restarter
    )
    $run = New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled
    $inspect = if ($SourceInspector) { & $SourceInspector $Config } else {
        Assert-AeroLinkDedicatedProductionSource -SourceRoot $Config.AeroLinkRoot | Out-Null
        Update-AeroLinkProductionSource -SourceRoot $Config.AeroLinkRoot -InspectOnly
    }
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Production-source inspection: $($inspect.Action) - $($inspect.Reason)"
    if ((-not $inspect.Canonical) -or $inspect.Action -ne 'UpdateAvailable') {
        # Deliberately does NOTHING when the source has not moved, including when the demo is down.
        #
        # I added an unconditional self-heal here in the previous round and it was wrong: this task is a
        # bounded SOURCE reconciler, not a desired-state controller. `STOP_AEROLINK_REMOTE_DEMO.bat` is a
        # supported operator command, nothing persists a desired-up state, and the task's own documentation
        # says it does nothing when origin/main has not moved - so healing here meant an explicit STOP was
        # silently undone within thirty minutes, republishing a public endpoint nobody had asked to reopen.
        #
        # The problem that self-heal was reaching for is real, and is solved where it happens instead: a
        # transition whose handoff fails recovers the prior topology in that same pass, from a fresh process
        # on the current source. It does not leave the repair to a later tick that cannot tell an operator's
        # STOP from an incomplete transition.
        return [pscustomobject]@{ Action = $inspect.Action; Restarted = $false; HeadSha = $inspect.HeadSha; Detail = $inspect.Reason }
    }

    # From here the working tree is going to be rewritten, so nothing may still be running out of it - and
    # nothing may still be publishing the port it was serving on.
    #
    # The tunnel comes down first and deliberately. It forwards the public URL to 127.0.0.1:5080, so leaving
    # it up across the transition means the protected public endpoint is pointed at an absent API and then at
    # whatever occupies that port next - a process whose identity has not been re-proved. Only the
    # AeroLink-owned tunnel is stopped; an ngrok that does not match the contract is a refusal, never a
    # casualty. The restart below brings a tunnel back and re-proves the 401 edge contract before declaring
    # the demo ready.
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Stopping the owned tunnel and the local production runtime before advancing the source to $($inspect.TargetSha)."
    # Fails closed, deliberately. Logging a failed tunnel stop and advancing anyway leaves the protected
    # public URL forwarding to a port whose process is being replaced, which is the outcome this ordering
    # exists to prevent; not updating the source is the safe half of that choice.
    # Everything from the tunnel proof onward is inside the compensation boundary.
    #
    # It has to START before Assert-AeroLinkOwnedTunnelStopped, not after it. That helper records the
    # obligation between its stop and its proof precisely because fail-closed enumeration can throw after a
    # tunnel has genuinely come down - and entering the try only once it returned meant that throw bypassed
    # compensation entirely: public endpoint down, source unchanged, restarter never reached. What decides
    # whether anything is owed is the obligation, not where the call sits.
    $obligation = New-AeroLinkTransitionObligation
    # Exact prior topology, by OWNERSHIP, before anything is touched. Not readiness: the rule that governs a
    # source advance is about a running process, and an owned API that is up but unhealthy is precisely the
    # one that must not be left executing replaced files. Recording topology from a readiness probe meant
    # such a process was stopped while the record said nothing had been running.
    $priorState = if ($ServiceStateProbe) { & $ServiceStateProbe $Config } else {
        $policy = if ($PreserveServiceState) { 'Preserve' } else { 'KeepReady' }
        $obligation = New-AeroLinkProductionObligation -SourceRoot $Config.AeroLinkRoot -Config $Config -Policy $policy
        [pscustomobject]@{ TunnelRunning = $obligation.PriorTunnel; RuntimeRunning = $obligation.PriorRuntime }
    }
    $obligation.PriorTunnel = [bool]$priorState.TunnelRunning
    $obligation.PriorRuntime = [bool]$priorState.RuntimeRunning
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Before teardown: tunnel $(if ($priorState.TunnelRunning) { 'running' } else { 'not running' }), owned runtime $(if ($priorState.RuntimeRunning) { 'running' } else { 'not running' })."

    $obligation.Stage = 'Quiescing'
    if (-not $ServiceStateProbe) { Save-AeroLinkProductionObligation -Obligation $obligation }
    $advance = $null
    try {
        if ($TunnelStopper) { & $TunnelStopper $Config | Out-Null; $obligation.TeardownBegan = $true }
        else { Assert-AeroLinkOwnedTunnelStopped -Config $Config -Run $run -Obligation $obligation | Out-Null }

        # Records each stop as it succeeds, and does not touch PostgreSQL: it does not execute out of the
        # source working tree, and folding it in made teardown non-atomic for no safety gain.
        if ($RuntimeStopper) { & $RuntimeStopper $Config $inspect | Out-Null; $obligation.TeardownBegan = $true }
        else { Stop-AeroLinkSourceExecutingProcesses -Config $Config -Obligation $obligation -Run $run | Out-Null }

        if (-not $ServiceStateProbe) { Save-AeroLinkProductionObligation -Obligation $obligation }
        $advance = if ($SourceAdvancer) { & $SourceAdvancer $Config $inspect } else {
            Update-AeroLinkProductionSource -SourceRoot $Config.AeroLinkRoot -AdvanceToSha $inspect.TargetSha
        }
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Production-source advance: $($advance.Action) - $($advance.Reason)"
    }
    catch {
        if (-not $ServiceStateProbe) { Save-AeroLinkProductionObligation -Obligation $obligation }
        # Nothing was taken down, so nothing is owed - the tunnel refused to stop, or a mismatched ngrok made
        # the stop refuse. Fail closed and leave the machine exactly as it was.
        if (-not $obligation.TeardownBegan) { throw }
        $failure = $_.Exception.Message
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "The transition failed after teardown began: $failure. Restoring the prior topology."
        $restored = $null
        $actualHead = $inspect.HeadSha
        try {
            if (-not $Restarter) {
                $onDisk = Get-AeroLinkProductionSourcePosture -SourceRoot $Config.AeroLinkRoot
                if (-not $onDisk.Canonical) { throw "Current source is not canonical: $($onDisk.Reason)" }
                $actualHead = $onDisk.Posture.HeadSha
            }
            # The SAME discharge as the success path, driven by the same prior topology. Compensation used to
            # call Start-AeroLinkRemoteDemo unconditionally, so a runtime-only installation came back as
            # runtime plus a public tunnel, and an installation with nothing running could acquire a whole
            # demo from a failure branch. "Restore what was running, and only that" has to hold on the paths
            # nobody watches, or it does not hold.
            $restored = if ($Restarter) { & $Restarter $Config $null }
            else { Invoke-AeroLinkRemoteDemoHandoff -Config $Config -Topology $priorState -PreserveServiceState:$PreserveServiceState -Scheduled:$Scheduled -Run $run -HeadSha $actualHead }
            $obligation.Discharged = $true
            if (-not $ServiceStateProbe) { Save-AeroLinkProductionObligation -Obligation $obligation }
        }
        catch {
            throw "The production source transition failed after teardown began ($failure), and current-source recovery could not be completed: $($_.Exception.Message). Last verified source: $actualHead; service/schema completion is not asserted."
        }
        return [pscustomobject]@{
            Action = 'TransitionFailed'; Restarted = $true; HeadSha = $actualHead
            Detail = "The transition failed after teardown began: $failure Actual source: $actualHead. The prior service topology was restored by verified source. $($restored.Detail)"
        }
    }

    # Discharge. One decision, made from the prior topology and the policy, on every path.
    $result = if ($Restarter) { & $Restarter $Config $advance }
    elseif ($advance.Action -eq 'Updated' -and $advance.Canonical -and $env:AEROLINK_REMOTE_DEMO_HANDOFF -ne $Config.AeroLinkRoot) {
        # The source moved, so this module is stale: continue in a fresh process from the updated script
        # rather than letting the version already in memory drive the rest of the transition.
        #
        # The handoff is a process boundary, and an obligation that does not survive it is not an obligation.
        # If the updated child cannot build, start or prove the service, the tunnel and runtime are already
        # down and the source is already current - so a later pass would see AlreadyCurrent and do nothing,
        # leaving the demo dark indefinitely. Git is never rolled backward to compensate; the recovery is on
        # the verified CURRENT source, to the topology that was running before.
        try { Invoke-AeroLinkRemoteDemoHandoff -Config $Config -Scheduled:$Scheduled -Run $run -Topology $priorState -PreserveServiceState:$PreserveServiceState -HeadSha $advance.HeadSha }
        catch {
            # Not retried from inside this attempt: its first continuation's descendants may still be running in the
            # transition job. The outer authority collects the attempt, proves it quiescent, and admits a recovery
            # attempt on the current source only then.
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "The post-advance continuation failed: $($_.Exception.Message). Recovery belongs to the outer authority's next attempt."
            throw "The production source WAS advanced to $($advance.HeadSha), but the updated code could not complete the transition: $($_.Exception.Message). The source is current; final service/schema readiness is not asserted."
        }
    }
    else { Restore-AeroLinkServiceTopology -Config $Config -Topology $priorState -KeepReady:(-not $PreserveServiceState) -Scheduled:$Scheduled -Run $run }
    $obligation.Discharged = $true
    if (-not $ServiceStateProbe) { Save-AeroLinkProductionObligation -Obligation $obligation }

    if ($advance.Action -ne 'Updated' -or -not $advance.Canonical) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'The advance was refused; production was restarted on the revision already on disk.'
        return [pscustomobject]@{
            Action = $advance.Action; Restarted = $true; HeadSha = $advance.HeadSha
            Detail = "The production source was not advanced: $($advance.Reason) Production was restarted on the revision already on disk rather than left down. $($result.Detail)"
        }
    }
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Production restarted onto $($advance.HeadSha)."
    return [pscustomobject]@{ Action = 'Updated'; Restarted = $true; HeadSha = $advance.HeadSha; Detail = "Production now runs $($advance.HeadSha). $($result.Detail)" }
}

function Get-AeroLinkRemoteDemoStartAssessment {
    <#
      .SYNOPSIS Read-only: is the protected remote demo already exactly ready, or does starting it need a transition?
      .DESCRIPTION
        An idempotent Start that finds everything ready must not create an attempt, a witness or a job. This decides
        that from observation alone - source inspection (a fetch writes only remote-tracking refs), local readiness,
        runtime identity against the inspected revision, one owned protected tunnel and a valid notification origin.
        Anything short of all of it is NeedsTransition; a non-canonical source is Refused before anything is touched.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Config)
    Assert-AeroLinkDedicatedProductionSource -SourceRoot $Config.AeroLinkRoot | Out-Null
    $inspect = Update-AeroLinkProductionSource -SourceRoot $Config.AeroLinkRoot -InspectOnly
    if (-not $inspect.Canonical) { return [pscustomobject]@{ Decision = 'Refused'; Inspect = $inspect; Detail = "AEROLINK REMOTE DEMO NOT READY: $($inspect.Reason)" } }
    if ($inspect.Action -eq 'UpdateAvailable') { return [pscustomobject]@{ Decision = 'NeedsTransition'; Inspect = $inspect; Detail = "the production source is behind origin/main ($($inspect.TargetSha))" } }
    $local = Test-AeroLinkRemoteDemoLocalReady -Config $Config
    if (-not $local.Ready) { return [pscustomobject]@{ Decision = 'NeedsTransition'; Inspect = $inspect; Detail = $local.Detail } }
    $match = Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $Config -ExpectedSourceIdentity ([string]$inspect.HeadSha)
    if (-not $match.Matches) { return [pscustomobject]@{ Decision = 'NeedsTransition'; Inspect = $inspect; Detail = $match.Detail } }
    $tunnels = Get-AeroLinkRemoteDemoNgrokProcess -Config $Config
    if (@($tunnels.Mismatched).Count) { return [pscustomobject]@{ Decision = 'Refused'; Inspect = $inspect; Detail = 'AEROLINK REMOTE DEMO NOT READY: an ngrok process does not match the AeroLink remote-demo contract. Refusing to start or stop it.' } }
    if (@($tunnels.Owned).Count -ne 1) { return [pscustomobject]@{ Decision = 'NeedsTransition'; Inspect = $inspect; Detail = "$(@($tunnels.Owned).Count) owned tunnel(s) are running" } }
    $protection = Test-AeroLinkRemoteDemoPublicProtection -Config $Config
    if (-not $protection.Protected) { return [pscustomobject]@{ Decision = 'NeedsTransition'; Inspect = $inspect; Detail = $protection.Detail } }
    $origin = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $Config
    if (-not $origin.Valid) { return [pscustomobject]@{ Decision = 'NeedsTransition'; Inspect = $inspect; Detail = $origin.Detail } }
    return [pscustomobject]@{ Decision = 'AlreadyReady'; Inspect = $inspect; Detail = "The expected protected tunnel is already running and returning 401. $($origin.Detail)" }
}

function Get-AeroLinkHomeTransitionRequiredRoles {
    <#
      .SYNOPSIS The roles an attempt must leave restored, verified by the OUTER after its job is collected.
      .DESCRIPTION
        Required state is the prior topology under the policy, never what a child says it did: keep-ready restores
        the whole demo; preserve restores exactly what was running (the API if it or a tunnel was up; the tunnel
        only if it was up). PostgreSQL is required wherever the API is. Each role carries how the outer finds the
        running instance and the readiness that instance must prove NOW - evaluated after the attempt, so the API is
        checked against the source identity actually on disk at that moment.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$InstallationRoot,
        $Config,
        [Parameter(Mandatory)][ValidateSet('KeepReady', 'Preserve')][string]$Policy,
        [Parameter(Mandatory)]$Topology,
        [switch]$RequireRuntime
    )
    $tunnelRequired = [bool]$Config -and ($Policy -eq 'KeepReady' -or [bool]$Topology.TunnelRunning)
    $apiRequired = $RequireRuntime -or $tunnelRequired -or [bool]$Topology.RuntimeRunning
    $roles = @()
    if (-not $apiRequired) { return $roles }
    $installation = Get-AeroLinkInstallationPaths -ProductRoot (Join-Path $SourceRoot 'product') -InstallationRoot $InstallationRoot
    $postgresReadiness = @{ kind = 'postgres'; dataDirectory = $installation.PostgresData; port = (Get-AeroLinkServiceEndpoints).PostgresPort; binDir = $installation.PostgresBin }
    $roles += [pscustomobject]@{ role = 'postgres'; launchRequired = $false; readiness = $postgresReadiness
        discover = { $instance = Get-AeroLinkPostgresInstance -DataDirectory $postgresReadiness.dataDirectory; if ($instance.Class -eq 'Valid') { [pscustomobject]@{ ProcessId = $instance.ProcessId } } }.GetNewClosure() }
    $apiDirectory = Join-Path $SourceRoot 'product\src\AeroLink.Api'
    $productRoot = Join-Path $SourceRoot 'product'
    $roles += [pscustomobject]@{ role = 'api'; launchRequired = $false
        readiness = {
            $instance = Get-AeroLinkInstanceConfig -ProductRoot $productRoot -Mode HomeCanonical
            @{ kind = 'api'; port = (Get-AeroLinkServiceEndpoints).ApiPort; baseUri = (Get-AeroLinkServiceEndpoints).ApiBaseUri; expectedMode = 'HOME-PRODUCTION'
                expectedSourceIdentity = [string](Get-AeroLinkSourceFingerprint -RepositoryRoot $SourceRoot).Identity
                expectedInstanceId = [string]$instance.InstanceId; expectedClassification = [string]$instance.Classification }
        }.GetNewClosure()
        discover = {
            $owner = Get-AeroLinkPortOwner -Port (Get-AeroLinkServiceEndpoints).ApiPort
            if ($owner.Found -and -not $owner.Ambiguous -and $owner.Attributable -and
                (Test-AeroLinkProcessOwnership -CommandLine $owner.CommandLine -ExecutablePath $owner.ExecutablePath -OwnershipFragments @($apiDirectory))) { [pscustomobject]@{ ProcessId = $owner.ProcessId } }
        }.GetNewClosure() }
    if ($tunnelRequired) {
        $demo = $Config
        $roles += [pscustomobject]@{ role = 'tunnel'; launchRequired = $false; readiness = @{ kind = 'tunnel'; publicUrl = $demo.PublicUrl }
            discover = { $tunnels = Get-AeroLinkRemoteDemoNgrokProcess -Config $demo; if (@($tunnels.Owned).Count -eq 1 -and -not @($tunnels.Mismatched).Count) { [pscustomobject]@{ ProcessId = [int]$tunnels.Owned[0].ProcessId } } }.GetNewClosure() }
    }
    return $roles
}

function Invoke-AeroLinkHomeTransitionOuter {
    <#
      .SYNOPSIS Runs a HOME source/service transition as its OUTER authority: at most one attempt plus one admitted
        recovery attempt, then an exact, truthful result.
      .DESCRIPTION
        The caller holds the HOME transition lease as OWNER. This process never tears anything down itself. It
        qualifies its own launch context, captures and journals the restoration obligation from observation, and
        runs the attempt: the delegate actor - from the checkout whose identity it proves - performs teardown and
        advance inside the attempt's job, a continuation from the advanced source restores services by launch
        request, and this process verifies every required role itself once the job is collected.

        Recovery is an ADMISSION decision, never a catch. When the attempt began mutation and left a required role
        unrestored, one recovery attempt restores the prior topology on the source now on disk - but only if the
        failed attempt is proven quiescent and every launch it requested is resolved. Otherwise the obligation is
        retained and the result says why.

        The obligation is discharged only by this process, only after it re-verified every required role now.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallationRoot,
        [Parameter(Mandatory)]$Lease,
        [Parameter(Mandatory)][ValidateSet('RemoteDemoStart', 'Reconcile', 'Update', 'RuntimeUpdate', 'FirstDeployment')][string]$Operation,
        [Parameter(Mandatory)][string]$SourceRoot,
        # The checkout whose actor performs the delegate step. The dedicated source, except where it must be proved.
        [string]$DelegateSourceRoot = $SourceRoot,
        $Config,
        [Parameter(Mandatory)][ValidateSet('KeepReady', 'Preserve')][string]$Policy,
        [switch]$Scheduled,
        [int]$AttemptDeadlineSeconds = $script:AeroLinkTransitionContinuationTimeoutSeconds,
        [switch]$StreamToHost,
        # Contract-suite seams.
        [hashtable]$DescriptorOverride,
        [scriptblock]$ChainRunner
    )
    $qualification = Test-AeroLinkLaunchContextQualification -InstallationRoot $InstallationRoot -DescriptorOverride $DescriptorOverride
    if (-not $qualification.Supported) {
        return [pscustomobject]@{ Decision = 'Refused'; ExitCode = 20; Restored = $false; RestorationRequired = $false; Attempts = @()
            Detail = "No service was stopped: $($qualification.Detail). Qualify this launch context before it may run a HOME transition." }
    }
    # The obligation, from observation, journaled before any attempt exists. An interrupted transition's journal
    # supplies policy and prior topology; every running process is re-proved regardless.
    $obligation = New-AeroLinkProductionObligation -SourceRoot $SourceRoot -Config $Config -Policy $Policy
    if ($Lease.PSObject.Properties['Pending'] -and $Lease.Pending) {
        $pending = $Lease.Pending
        if ($pending.SourceRoot -ine $SourceRoot -or ($pending.PriorTunnel -and (-not $Config -or $pending.PublicOrigin -ine $Config.PublicUrl))) {
            return [pscustomobject]@{ Decision = 'Refused'; ExitCode = 22; Restored = $false; RestorationRequired = $true; Attempts = @()
                Detail = 'An interrupted transition journal contradicts this source or public origin. Nothing was stopped.' }
        }
        $obligation.PriorTunnel = [bool]$pending.PriorTunnel
        $obligation.PriorRuntime = [bool]$pending.PriorRuntime
        $obligation.PublicOrigin = $pending.PublicOrigin
        $obligation.Policy = $pending.Policy
        $obligation.TeardownBegan = $true
    }
    $obligation.Stage = 'Captured'
    Save-AeroLinkProductionObligation -Obligation $obligation
    $topology = [pscustomobject]@{ TunnelRunning = [bool]$obligation.PriorTunnel; RuntimeRunning = [bool]$obligation.PriorRuntime }
    $effectivePolicy = [string]$obligation.Policy
    $required = @(Get-AeroLinkHomeTransitionRequiredRoles -SourceRoot $SourceRoot -InstallationRoot $InstallationRoot -Config $Config -Policy $effectivePolicy `
            -Topology $topology -RequireRuntime:($Operation -eq 'FirstDeployment'))
    $configPath = if ($Config) { Get-AeroLinkRemoteDemoConfigPath } else { $null }
    $run = {
        param([string]$AttemptOperation, [string]$DelegateRoot)
        $identity = [string](Get-AeroLinkSourceFingerprint -RepositoryRoot $DelegateRoot).Identity
        $plan = [ordered]@{ operation = $AttemptOperation; sourceRoot = $SourceRoot; configPath = $configPath; policy = $effectivePolicy; scheduled = [bool]$Scheduled
            topology = [ordered]@{ tunnelRunning = $topology.TunnelRunning; runtimeRunning = ($topology.RuntimeRunning -or $Operation -eq 'FirstDeployment') } }
        $delegateScript = Join-Path $DelegateRoot 'product\scripts\Invoke-AeroLinkTransitionActor.ps1'
        if ($ChainRunner) { return & $ChainRunner $plan $delegateScript $identity $required }
        if (-not (Test-Path -LiteralPath $delegateScript -PathType Leaf)) { throw "The checkout at $DelegateRoot has no transition actor; this source cannot run a HOME transition." }
        return Invoke-AeroLinkTransitionChain -InstallationRoot $InstallationRoot -Lease $Lease -Caller $Operation -Plan $plan -DelegateScript $delegateScript `
            -DelegateSourceIdentity $identity -RequiredRoles $required -DeadlineSeconds $AttemptDeadlineSeconds -Qualification $qualification `
            -StreamToHost:$StreamToHost -SharedProgressLog @($(if ($Config) { Join-Path $Config.LogsPath 'remote-demo.log' }))
    }
    $attempts = @()
    $first = & $run $Operation $DelegateSourceRoot
    $attempts += $first
    $final = $first
    if ($first.Decision -ne 'Completed' -and $first.RestorationRequired) {
        $recoveryAdmissible = [bool](Get-AeroLinkProperty (Get-AeroLinkProperty $first.Outcome 'recovery' $null) 'admissible' $false)
        if ($recoveryAdmissible -and $required.Count -gt 0) {
            if ($StreamToHost) { Write-Host "      The attempt failed after mutation began ($($first.Decision)); it is proven quiescent, so one recovery attempt restores the prior topology on the source now on disk." -ForegroundColor Yellow }
            $final = & $run 'Restore' $SourceRoot
            $attempts += $final
        }
    }
    $restored = ($final.Decision -eq 'Completed')
    if ($restored) {
        $obligation.Discharged = $true
        $obligation.Stage = 'Discharged'
        Save-AeroLinkProductionObligation -Obligation $obligation
    }
    $exitCode = if ($first.Decision -eq 'Completed') { 0 } elseif ($first.ExitCode) { $first.ExitCode } else { 1 }
    $detail = if ($first.Decision -eq 'Completed') { $first.Detail }
        elseif ($attempts.Count -gt 1 -and $restored) { "The transition failed ($($first.Decision): $($first.Detail)). A recovery attempt restored the prior service topology on the source now on disk." }
        elseif ($attempts.Count -gt 1) { "The transition failed ($($first.Decision): $($first.Detail)), and the recovery attempt failed too ($($final.Decision): $($final.Detail)). The restoration obligation is retained." }
        elseif ($first.RestorationRequired) { "The transition failed ($($first.Decision): $($first.Detail)). Recovery was not admitted ($((@(Get-AeroLinkProperty (Get-AeroLinkProperty $first.Outcome 'recovery' $null) 'problems' @())) -join '; ')); the restoration obligation is retained." }
        else { "The transition did not complete ($($first.Decision)): $($first.Detail)" }
    return [pscustomobject]@{ Decision = $first.Decision; ExitCode = $exitCode; Restored = $restored; RestorationRequired = (-not $restored -and [bool]$final.RestorationRequired)
        Attempts = @($attempts | ForEach-Object { [pscustomobject]@{ AttemptId = $_.AttemptId; Decision = $_.Decision; ExitCode = $_.ExitCode; Detail = $_.Detail } }); Detail = $detail }
}

function Resolve-AeroLinkFirstDeploymentResult {
    <#
      .SYNOPSIS The truthful result of ONE brokered first-deployment invocation. { Succeeded, Unknown, Detail }
      .DESCRIPTION
        A consumer must reconcile the exact invocation's terminal outcome AND its exit status. Success requires all of:
        the task instance ended after this setup started it, a result bound to this request id, decision Completed,
        exit code 0 and a task result of 0. A missing or foreign result is Unknown - never "not running", never a reason
        to deploy again - and a stale task result from an earlier run cannot stand in for this one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ResultPath,
        [Parameter(Mandatory)][string]$RequestId,
        [Parameter(Mandatory)][bool]$TaskEnded,
        [AllowNull()]$LastTaskResult
    )
    if (-not $TaskEnded) { return [pscustomobject]@{ Succeeded = $false; Unknown = $true; Detail = 'the deployment task instance has not ended; its result is unknown' } }
    $read = Read-AeroLinkJsonRecord -Path $ResultPath
    if ($read.Class -ne 'Valid') { return [pscustomobject]@{ Succeeded = $false; Unknown = $true; Detail = "the task ended (task result $LastTaskResult) but its result for this request is $($read.Class.ToLower())" } }
    $result = $read.Value
    if ([string](Get-AeroLinkProperty $result 'requestId' '') -ne $RequestId) { return [pscustomobject]@{ Succeeded = $false; Unknown = $true; Detail = "the result names request '$(Get-AeroLinkProperty $result 'requestId' '')', not this one" } }
    $decision = [string](Get-AeroLinkProperty $result 'decision' '')
    $exitCode = Get-AeroLinkProperty $result 'exitCode' $null
    if ($decision -ne 'Completed' -or -not (Test-AeroLinkIntegral $exitCode) -or [int]$exitCode -ne 0 -or $null -eq $LastTaskResult -or [int64]$LastTaskResult -ne 0) {
        return [pscustomobject]@{ Succeeded = $false; Unknown = $false; Detail = "decision $decision, exit $exitCode, task result $LastTaskResult. $(Get-AeroLinkProperty $result 'detail' '')" }
    }
    return [pscustomobject]@{ Succeeded = $true; Unknown = $false; Detail = [string](Get-AeroLinkProperty $result 'detail' '') }
}

function Save-AeroLinkRemoteDemoTaskXml {
    <#
      .SYNOPSIS Writes the task XML in the encoding its declaration promises.
      .DESCRIPTION
        The XML declares UTF-16; schtasks rejects a file whose actual encoding
        does not match. Set-Content -Encoding Unicode writes UTF-16 LE with BOM,
        which is what the declaration describes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)][string]$Path,
        [string]$TaskName = $script:RemoteDemoTaskName,
        [switch]$Attended
    )
    $xml = Get-AeroLinkRemoteDemoTaskXml -Config $Config -TaskName $TaskName -Attended:$Attended
    Set-Content -LiteralPath $Path -Value $xml -Encoding Unicode
    return $Path
}

function Install-AeroLinkRemoteDemoTask {
    <#
      .SYNOPSIS Registers the recovery task against the DEDICATED production source, or refuses.
      .DESCRIPTION
        The assertion is the point. The 2026-09-03 outage was possible because the task's script path and
        source root both pointed at the one checkout on the machine, which was mid-feature with dirty WIP.
        A task may now be registered only against a checkout that declares itself the dedicated production
        source, so it cannot be aimed back at the development checkout by a stale configuration or a
        well-meant edit.

        -TaskName exists so the installer can be qualified against a disposable task without touching the
        real one.

        The unattended shape is attempted first and the attended one is the fallback, because Windows will
        not register a boot trigger or an S4U principal for a non-elevated caller - measured on the HOME
        machine, where every combination involving either was refused with "Access is denied" while logon
        and time triggers under an interactive token registered fine. Falling back keeps the installer
        working without administrator, at the cost of the very property #881 is adding; the result says so
        in as many words rather than reporting success and leaving the operator to discover it at the next
        reboot.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [string]$TaskName = $script:RemoteDemoTaskName,
        [switch]$AllowNonDedicatedSource
    )
    if (-not $AllowNonDedicatedSource) {
        Assert-AeroLinkDedicatedProductionSource -SourceRoot $Config.AeroLinkRoot | Out-Null
    }
    if (-not (Test-Path -LiteralPath $Config.StatePath)) { New-Item -ItemType Directory -Path $Config.StatePath -Force | Out-Null }
    $xmlPath = Join-Path $Config.StatePath 'remote-demo-task.xml'
    $unattended = $true
    Save-AeroLinkRemoteDemoTaskXml -Config $Config -Path $xmlPath -TaskName $TaskName
    & schtasks.exe /Create /TN $TaskName /XML $xmlPath /F
    if ($LASTEXITCODE -ne 0) {
        Write-Host '' -ForegroundColor Yellow
        Write-Host 'Windows refused to register unattended recovery. A boot trigger and a password-less (S4U)' -ForegroundColor Yellow
        Write-Host 'principal both require an elevated install; this one was not elevated.' -ForegroundColor Yellow
        Write-Host 'Falling back to logon recovery, which recovers after you sign in and NOT after a reboot' -ForegroundColor Yellow
        Write-Host 'with nobody logged in. To get unattended recovery, run this configuration once from an' -ForegroundColor Yellow
        Write-Host 'elevated PowerShell.' -ForegroundColor Yellow
        $unattended = $false
        Save-AeroLinkRemoteDemoTaskXml -Config $Config -Path $xmlPath -TaskName $TaskName -Attended
        & schtasks.exe /Create /TN $TaskName /XML $xmlPath /F
        if ($LASTEXITCODE -ne 0) { throw "schtasks /Create failed with exit code $LASTEXITCODE." }
    }
    $status = Get-AeroLinkRemoteDemoTaskStatus -TaskName $TaskName
    $status | Add-Member -MemberType NoteProperty -Name LogonType -Value $(if ($unattended) { 'S4U' } else { 'InteractiveToken' }) -Force
    $status | Add-Member -MemberType NoteProperty -Name UnattendedBootRecovery -Value $unattended -Force
    $status | Add-Member -MemberType NoteProperty -Name SourceRoot -Value $Config.AeroLinkRoot -Force
    return $status
}

function Remove-AeroLinkRemoteDemoTask {
    [CmdletBinding()]
    param([string]$TaskName = $script:RemoteDemoTaskName)
    & schtasks.exe /Delete /TN $TaskName /F
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 267011) {
        throw "schtasks /Delete failed with exit code $LASTEXITCODE."
    }
    return [pscustomobject]@{ TaskName = $TaskName; State = 'Removed' }
}

function Get-AeroLinkRemoteDemoTaskStatus {
    [CmdletBinding()]
    param([string]$TaskName = $script:RemoteDemoTaskName)
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) {
        return [pscustomobject]@{ TaskName = $TaskName; Installed = $false; State = 'NotInstalled'; Detail = 'The AeroLink remote-demo recovery task is not installed.' }
    }
    $info = $task | Get-ScheduledTaskInfo
    return [pscustomobject]@{
        TaskName = $TaskName
        Installed = $true
        State = $task.State.ToString()
        LastRunTime = $info.LastRunTime
        NextRunTime = $info.NextRunTime
        LastTaskResult = $info.LastTaskResult
        Detail = "Task '$($task.TaskPath)$($task.TaskName)' state $($task.State)."
    }
}

function Get-AeroLinkRemoteDemoStatus {
    <#
      .SYNOPSIS Read-only operator status: AEROLINK REMOTE DEMO READY or NOT READY.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [scriptblock]$LocalRuntimeProbe
    )

    $checks = [System.Collections.Generic.List[object]]::new()
    $diagnosticsScript = Join-Path $Config.AeroLinkRoot 'product\scripts\Get-AeroLinkDiagnostics.ps1'
    $diagnostics = $null
    if (Test-Path -LiteralPath $diagnosticsScript) {
        try {
            $json = & $diagnosticsScript -Json
            $diagnostics = $json | ConvertFrom-Json
        }
        catch {
            $checks.Add([pscustomobject]@{ Name = 'AeroLink diagnostics'; Healthy = $false; Detail = "Diagnostics failed: $($_.Exception.GetType().Name)" })
        }
    }
    if ($diagnostics) {
        foreach ($check in $diagnostics.checks) {
            $checks.Add([pscustomobject]@{ Name = $check.name; Healthy = [bool]$check.healthy; Detail = $check.detail })
        }
    }

    # Where the source came from is a status question, not an implementation detail: on 2026-09-03 every
    # other check would have looked fine, and the answer to "why is the demo down" was the source root.
    $sourcePosture = Get-AeroLinkProductionSourcePosture -SourceRoot $Config.AeroLinkRoot
    $checks.Add([pscustomobject]@{
        Name = 'Dedicated canonical production source'
        Healthy = ($sourcePosture.Dedicated -and $sourcePosture.Canonical)
        Detail = "$($Config.AeroLinkRoot): $($sourcePosture.Reason)"
    })

    $local = Test-AeroLinkRemoteDemoLocalReady -Config $Config
    $checks.Add([pscustomobject]@{ Name = 'Local AeroLink ready + built client'; Healthy = $local.Ready; Detail = $local.Detail })

    if ($sourcePosture.Canonical -and $sourcePosture.Posture) {
        # Deliberately not $LocalRuntimeProbe: that seam attributes the listening PROCESS, while this one
        # reads the process's published identity. Two different questions, two different probes.
        $runtimeMatch = Test-AeroLinkRemoteDemoRuntimeMatchesSource -Config $Config -ExpectedSourceIdentity $sourcePosture.Posture.HeadSha
        $checks.Add([pscustomobject]@{ Name = 'Runtime matches production source and mode'; Healthy = $runtimeMatch.Matches; Detail = $runtimeMatch.Detail })
    }

    $processes = Get-AeroLinkRemoteDemoNgrokProcess -Config $Config
    $ownedCount = @($processes.Owned).Count
    $mismatchCount = @($processes.Mismatched).Count
    $checks.Add([pscustomobject]@{
        Name = 'Owned protected ngrok process'
        Healthy = $ownedCount -ge 1 -and $mismatchCount -eq 0
        Detail = if ($ownedCount -ge 1) { "Owned ngrok PID(s): $((@($processes.Owned) | ForEach-Object { $_.ProcessId }) -join ', ')" } elseif ($mismatchCount -gt 0) { "Unexpected ngrok process(es) present: $mismatchCount" } else { 'No owned ngrok tunnel is running.' }
    })

    $probe = Test-AeroLinkRemoteDemoPublicProtection -Config $Config
    $checks.Add([pscustomobject]@{ Name = 'Public endpoint 401 protection'; Healthy = $probe.Protected; Detail = $probe.Detail })

    $originProof = Test-AeroLinkRemoteDemoNotificationOriginProof -Config $Config -RuntimeProbe $LocalRuntimeProbe
    $checks.Add([pscustomobject]@{ Name = 'Reachable notification-link origin'; Healthy = $originProof.Valid; Detail = $originProof.Detail })

    $task = Get-AeroLinkRemoteDemoTaskStatus
    $checks.Add([pscustomobject]@{ Name = 'Automatic recovery task'; Healthy = $task.Installed; Detail = $task.Detail })

    $healthy = -not ($checks | Where-Object { -not $_.Healthy })
    $overall = if ($healthy) { 'AEROLINK REMOTE DEMO READY' } else { 'AEROLINK REMOTE DEMO NOT READY' }
    return [pscustomobject]@{
        Overall = $overall
        PublicUrl = $Config.PublicUrl
        Checks = $checks
    }
}

Export-ModuleMember -Function `
    Get-AeroLinkRemoteDemoConfigPath, `
    Get-AeroLinkRemoteDemoConfig, `
    Get-AeroLinkRemoteDemoNgrokArguments, `
    Get-AeroLinkRemoteDemoNgrokProcess, `
    Test-AeroLinkRemoteDemoPublicProtection, `
    Test-AeroLinkRemoteDemoLocalReady, `
    Get-AeroLinkRemoteDemoLocalRuntimeIdentity, `
    Test-AeroLinkRemoteDemoNotificationOriginProof, `
    New-AeroLinkRemoteDemoRun, `
    Test-AeroLinkRemoteDemoPostgresReady, `
    Start-AeroLinkRemoteDemoPostgres, `
    Start-AeroLinkRemoteDemoPostgresHelper, `
    Stop-AeroLinkRemoteDemoOwnedProcess, `
    Start-AeroLinkRemoteDemoProductionHelper, `
    Invoke-AeroLinkProductionLauncher, `
    Get-AeroLinkProductionLauncherRefusal, `
    Test-AeroLinkRemoteDemoRuntimeMatchesSource, `
    Start-AeroLinkRemoteDemoNgrok, `
    Get-AeroLinkRemoteDemoStartDecision, `
    Write-AeroLinkRemoteDemoLog, `
    Start-AeroLinkRemoteDemo, `
    Stop-AeroLinkRemoteDemo, `
    Assert-AeroLinkOwnedTunnelStopped, `
    New-AeroLinkTransitionObligation, `
    Get-AeroLinkServiceTopology, `
    Restore-AeroLinkServiceTopology, `
    Stop-AeroLinkSourceExecutingProcesses, `
    Get-AeroLinkRemoteDemoTaskXml, `
    Get-AeroLinkReconcileTaskXml, `
    Install-AeroLinkReconcileTask, `
    Invoke-AeroLinkProductionSourceReconciliation, `
    Invoke-AeroLinkRemoteDemoHandoff, `
    Get-AeroLinkTransitionBudget, `
    Resolve-AeroLinkFirstDeploymentResult, `
    Get-AeroLinkTransitionContinuation, `
    Get-AeroLinkRemoteDemoStartAssessment, `
    Get-AeroLinkHomeTransitionRequiredRoles, `
    Invoke-AeroLinkHomeTransitionOuter, `
    Save-AeroLinkRemoteDemoTaskXml, `
    Install-AeroLinkRemoteDemoTask, `
    Remove-AeroLinkRemoteDemoTask, `
    Get-AeroLinkRemoteDemoTaskStatus, `
    Get-AeroLinkRemoteDemoStatus

Export-ModuleMember -Function Save-AeroLinkProductionObligation, New-AeroLinkProductionObligation, Stop-AeroLinkProductionTransition, Set-AeroLinkRemoteDemoNotificationOriginProof
