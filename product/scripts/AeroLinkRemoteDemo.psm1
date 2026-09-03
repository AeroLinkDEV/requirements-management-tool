#Requires -Version 5.1
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1')
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRuntimeIdentity.psm1')
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
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [object[]]$ProcessInfos
    )

    if ($null -eq $ProcessInfos) {
        $ProcessInfos = @(Get-CimInstance Win32_Process -Filter "Name='ngrok.exe'" -ErrorAction SilentlyContinue)
    }

    $owned = @()
    $mismatched = @()
    $expectedExe = [IO.Path]::GetFullPath($Config.NgrokExecutable)
    foreach ($process in $ProcessInfos) {
        $executable = ''
        if ($process.ExecutablePath) { $executable = [IO.Path]::GetFullPath($process.ExecutablePath) }
        $command = [string]$process.CommandLine
        $exeMatches = $executable -eq $expectedExe
        $contractMatches = $command.IndexOf($Config.PublicUrl, [StringComparison]::OrdinalIgnoreCase) -ge 0 `
            -and $command.IndexOf($Config.Upstream, [StringComparison]::OrdinalIgnoreCase) -ge 0 `
            -and $command.IndexOf('--traffic-policy-file', [StringComparison]::OrdinalIgnoreCase) -ge 0 `
            -and $command.IndexOf($Config.TrafficPolicyPath, [StringComparison]::OrdinalIgnoreCase) -ge 0
        if ($exeMatches -and $contractMatches) {
            $owned += $process
        }
        else {
            $mismatched += $process
        }
    }
    return [pscustomobject]@{ Owned = @($owned); Mismatched = @($mismatched) }
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
        [int]$DatabasePort = 54329,
        [string]$DatabaseUser = 'postgres',
        [string]$DatabaseName = 'aerolink',
        [scriptblock]$PgIsreadyProbe,
        [scriptblock]$QueryProbe
    )
    # Parameter default expressions run in the CALLER's scope, so a module-internal
    # helper is not resolvable there. Resolve the binary path inside the function
    # instead; an empty PostgresBin previously made the probes fail with an empty
    # executable path even when PostgreSQL was healthy (#483 handover).
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
        HasExited = $false
        ExitCode = $null
        StdOutPath = $stdout
        StdErrPath = $stderr
    }
    $helper | Add-Member -MemberType ScriptMethod -Name Refresh -Value {
        $this.Process.Refresh()
        $this.HasExited = $this.Process.HasExited
        if ($this.Process.HasExited -and $null -eq $this.ExitCode) { $this.ExitCode = $this.Process.ExitCode }
    }
    return $helper
}

function Stop-AeroLinkRemoteDemoOwnedProcess {
    <#
      .SYNOPSIS Terminates only a helper PID this recovery attempt launched.
      .DESCRIPTION Refuses anything whose process name is not a PowerShell helper.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    if ($process.ProcessName -ne 'powershell' -and $process.ProcessName -ne 'pwsh') {
        throw "Refusing to stop PID ${ProcessId}: it is $($process.ProcessName), not an owned PowerShell helper."
    }
    Stop-Process -Id $ProcessId -Force
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
    if ($null -eq $HelperStopper) { $HelperStopper = { param($C, $R, $ProcessId) Stop-AeroLinkRemoteDemoOwnedProcess -ProcessId $ProcessId } }

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
            & $HelperStopper $Config $Run $helper.Id
        }
        return [pscustomobject]@{ Healthy = $true; HelperUsed = $true; ProcessId = $helper.Id; Step = 'postgres-recovery'; Detail = $ready.Detail; LogPath = (Join-Path $Config.LogsPath 'remote-demo.log') }
    }

    if (-not $helper.HasExited) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "PostgreSQL helper PID $($helper.Id) exceeded $RecoveryTimeoutSeconds seconds; terminating the owned helper."
        & $HelperStopper $Config $Run $helper.Id
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
        HasExited = $false
        ExitCode = $null
        StdOutPath = $stdout
        StdErrPath = $stderr
    }
    $helper | Add-Member -MemberType ScriptMethod -Name Refresh -Value {
        $this.Process.Refresh()
        $this.HasExited = $this.Process.HasExited
        if ($this.Process.HasExited -and $null -eq $this.ExitCode) { $this.ExitCode = $this.Process.ExitCode }
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
        [int]$TimeoutSeconds = 900,
        [int]$PollIntervalSeconds = 3,
        [int]$GraceSeconds = 5,
        # How long to keep polling readiness AFTER the launcher child has exited. A launcher that has already
        # exited will not open a port; the only reason to wait at all is that a process it started may still
        # be finishing its own startup, and that is seconds, not minutes.
        [int]$PostExitGraceSeconds = 20
    )
    if ($null -eq $LocalReadyTest) { $LocalReadyTest = { param($C) Test-AeroLinkRemoteDemoLocalReady -Config $C } }
    if ($null -eq $HelperLauncher) { $HelperLauncher = { param($C, $R) Start-AeroLinkRemoteDemoProductionHelper -Config $C -Run $R } }
    if ($null -eq $HelperStopper) { $HelperStopper = { param($C, $R, $ProcessId) Stop-AeroLinkRemoteDemoOwnedProcess -ProcessId $ProcessId } }

    $local = & $LocalReadyTest $Config
    if ($local.Ready) {
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
    } while (-not $local.Ready -and (Get-Date) -lt $deadline)

    if ($local.Ready) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Local AeroLink became ready: $($local.Detail)"
        Start-Sleep -Seconds $GraceSeconds
        if (-not $helper.HasExited) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Terminating owned production-launcher helper PID $($helper.Id) because AeroLink is independently ready."
            & $HelperStopper $Config $Run $helper.Id
        }
        return [pscustomobject]@{ Healthy = $true; HelperUsed = $true; ProcessId = $helper.Id; Step = 'production-launcher'; Detail = $local.Detail; LogPath = (Join-Path $Config.LogsPath 'remote-demo.log') }
    }

    if (-not $helper.HasExited) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Production launcher helper PID $($helper.Id) exceeded $TimeoutSeconds seconds; terminating the owned helper."
        & $HelperStopper $Config $Run $helper.Id
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
    $argumentLine = ((Get-AeroLinkRemoteDemoNgrokArguments -Config $Config) | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }) -join ' '
    return Start-Process -FilePath $Config.NgrokExecutable -ArgumentList $argumentLine -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
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
    $identity = if ($RuntimeIdentityProbe) { & $RuntimeIdentityProbe $Config } else { Get-AeroLinkRuntimeIdentity -BaseUri $Config.LocalApiBaseUri }
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
        [int]$PostgresRecoveryTimeoutSeconds = 300,
        [int]$ProductionTimeoutSeconds = 900,
        [int]$NgrokProtectionWaitSeconds = 120
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
    if (-not $SkipSourceReconciliation) {
        $reconcile = if ($SourceReconciler) { & $SourceReconciler $Config } else {
            Assert-AeroLinkDedicatedProductionSource -SourceRoot $Config.AeroLinkRoot | Out-Null
            Update-AeroLinkProductionSource -SourceRoot $Config.AeroLinkRoot
        }
        if (-not $reconcile.Canonical) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($reconcile.Reason)"
            throw "AEROLINK REMOTE DEMO NOT READY: $($reconcile.Reason)"
        }
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
            -TimeoutSeconds $ProductionTimeoutSeconds
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
        Stop-Process -Id $launched.Id -Force -ErrorAction SilentlyContinue
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: just-started tunnel not protected (expected 401, got $($probeResult.StatusCode)); torn down."
        throw "The just-started tunnel was not protected (expected 401, got $($probeResult.StatusCode)). It was torn down; nothing was left exposed."
    }

    $localAfter = & $LocalReadyTest $Config
    if (-not $localAfter.Ready) {
        Stop-Process -Id $launched.Id -Force -ErrorAction SilentlyContinue
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: local AeroLink readiness lost after tunnel start."
        throw "The tunnel became protected but local AeroLink readiness was lost, so the just-started tunnel was stopped. $($localAfter.Detail)"
    }

    $runtime = Get-AeroLinkRemoteDemoLocalRuntimeIdentity -Config $Config -RuntimeProbe $LocalRuntimeProbe
    if (-not $runtime.Found) {
        Stop-Process -Id $launched.Id -Force -ErrorAction SilentlyContinue
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: $($runtime.Detail)"
        throw "The tunnel became protected but the notification-link origin could not be attributed to the local AeroLink process, so the just-started tunnel was stopped. $($runtime.Detail)"
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

    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO READY; protected tunnel PID $($launched.Id); $($probeResult.Detail)"
    return [pscustomobject]@{ Ready = $true; PublicUrl = $Config.PublicUrl; Detail = $probeResult.Detail }
}

function Stop-AeroLinkRemoteDemo {
    <#
      .SYNOPSIS Stops only the AeroLink-owned ngrok tunnel, and optionally the local stack.
      .DESCRIPTION
        Never kills an arbitrary ngrok process: ownership requires the exact
        executable plus public URL, upstream, and Traffic Policy contract.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [switch]$IncludeLocalStack
    )

    $processes = Get-AeroLinkRemoteDemoNgrokProcess -Config $Config
    if (@($processes.Mismatched).Count -gt 0) {
        $mismatchPids = (@($processes.Mismatched) | ForEach-Object { $_.ProcessId }) -join ', '
        throw "Refusing to stop: ngrok process(es) PID $mismatchPids do not match the AeroLink remote-demo contract."
    }
    foreach ($process in @($processes.Owned)) {
        Write-Host "Stopping the AeroLink-owned ngrok tunnel (PID $($process.ProcessId))."
        Stop-Process -Id $process.ProcessId -Force
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
    <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
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
    <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
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

function Invoke-AeroLinkProductionSourceReconciliation {
    <#
      .SYNOPSIS One bounded reconciliation pass: advance the production source, and restart into it if it moved.
      .DESCRIPTION
        Does nothing when origin/main has not moved, which is the overwhelmingly common case and the reason
        this can afford to run on a timer at all. When it has moved, the restart goes through the ordinary
        start path so every existing gate still applies - canonical source, database upgrade posture, runtime
        identity, and the 401 proof before the tunnel is declared ready.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config,
        [switch]$Scheduled,
        [scriptblock]$SourceReconciler,
        [scriptblock]$Restarter
    )
    $run = New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled
    $reconcile = if ($SourceReconciler) { & $SourceReconciler $Config } else {
        Assert-AeroLinkDedicatedProductionSource -SourceRoot $Config.AeroLinkRoot | Out-Null
        Update-AeroLinkProductionSource -SourceRoot $Config.AeroLinkRoot
    }
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Production-source reconciliation: $($reconcile.Action) - $($reconcile.Reason)"
    if (-not $reconcile.Canonical) {
        return [pscustomobject]@{ Action = $reconcile.Action; Restarted = $false; HeadSha = $reconcile.HeadSha; Detail = $reconcile.Reason }
    }
    if ($reconcile.Action -ne 'Updated') {
        return [pscustomobject]@{ Action = $reconcile.Action; Restarted = $false; HeadSha = $reconcile.HeadSha; Detail = $reconcile.Reason }
    }
    # The full start, not a shortcut: it re-runs reconciliation (now a no-op), finds a runtime whose source
    # identity no longer matches, restarts only the process it owns, and re-proves the protected endpoint.
    $result = if ($Restarter) { & $Restarter $Config $reconcile } else { Start-AeroLinkRemoteDemo -Config $Config -Scheduled:$Scheduled }
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "Production restarted onto $($reconcile.HeadSha)."
    return [pscustomobject]@{ Action = 'Updated'; Restarted = $true; HeadSha = $reconcile.HeadSha; Detail = "Production now runs $($reconcile.HeadSha). $($result.Detail)" }
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
    Get-AeroLinkRemoteDemoTaskXml, `
    Get-AeroLinkReconcileTaskXml, `
    Install-AeroLinkReconcileTask, `
    Invoke-AeroLinkProductionSourceReconciliation, `
    Save-AeroLinkRemoteDemoTaskXml, `
    Install-AeroLinkRemoteDemoTask, `
    Remove-AeroLinkRemoteDemoTask, `
    Get-AeroLinkRemoteDemoTaskStatus, `
    Get-AeroLinkRemoteDemoStatus
