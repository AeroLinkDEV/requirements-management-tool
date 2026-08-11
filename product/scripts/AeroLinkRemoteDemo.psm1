#Requires -Version 5.1
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1')
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
    if ($values.ContainsKey('AeroLinkRoot') -and -not (Test-Path -LiteralPath ([string]$values['AeroLinkRoot']))) {
        throw "Remote-demo configuration AeroLinkRoot does not exist: $($values['AeroLinkRoot'])"
    }

    $moduleRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $defaultRoot = $moduleRoot
    $defaultLocal = Join-Path $env:LOCALAPPDATA 'AeroLink\RemoteDemo'

    return [pscustomobject]@{
        NgrokExecutable = [string]$values['NgrokExecutable']
        PublicUrl = [string]$values['PublicUrl']
        TrafficPolicyPath = [string]$values['TrafficPolicyPath']
        Upstream = if ($values.ContainsKey('Upstream')) { [string]$values['Upstream'] } else { 'http://127.0.0.1:5080' }
        LocalApiBaseUri = if ($values.ContainsKey('LocalApiBaseUri')) { [string]$values['LocalApiBaseUri'] } else { 'http://127.0.0.1:5080' }
        AeroLinkRoot = if ($values.ContainsKey('AeroLinkRoot')) { [string]$values['AeroLinkRoot'] } else { $defaultRoot }
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
    param([Parameter(Mandatory)]$Config)
    return Join-Path $Config.AeroLinkRoot 'product\.local\postgresql\pgsql\bin'
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
    $argumentLine = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -DoNotOpenBrowser"
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
        [int]$GraceSeconds = 5
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
    $local = $null
    do {
        Start-Sleep -Seconds $PollIntervalSeconds
        $helper.Refresh()
        if ($helper.HasExited -and -not $helperExited) {
            $helperExited = $true
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $Run -Message "Production launcher helper exited with code $($helper.ExitCode)."
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
        "Production launcher exited with code $($helper.ExitCode) but AeroLink never became ready within $TimeoutSeconds seconds. Step: production-launcher. Helper PID: $($helper.Id). Logs: $($helper.StdOutPath), $($helper.StdErrPath), $(Join-Path $Config.LogsPath 'remote-demo.log')"
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
        [int]$PostgresRecoveryTimeoutSeconds = 300,
        [int]$ProductionTimeoutSeconds = 900,
        [int]$NgrokProtectionWaitSeconds = 120
    )

    $run = New-AeroLinkRemoteDemoRun -Scheduled:$Scheduled
    Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'Remote demo start requested.'
    if ($null -eq $LocalReadyTest) { $LocalReadyTest = { param($C) Test-AeroLinkRemoteDemoLocalReady -Config $C } }

    $local = & $LocalReadyTest $Config
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
        $local = & $LocalReadyTest $Config
        if (-not $local.Ready) {
            Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'AEROLINK REMOTE DEMO NOT READY: local AeroLink readiness lost after launcher.'
            throw 'AEROLINK REMOTE DEMO NOT READY: local AeroLink readiness lost after launcher.'
        }
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
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message 'Remote demo already ready; no new processes started.'
        return [pscustomobject]@{ Ready = $true; PublicUrl = $Config.PublicUrl; Detail = $decision.Message }
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

    $state = [pscustomobject]@{
        Pid = $launched.Id
        NgrokExecutable = $Config.NgrokExecutable
        PublicUrl = $Config.PublicUrl
        Upstream = $Config.Upstream
        TrafficPolicyPath = $Config.TrafficPolicyPath
        StartedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    if (-not (Test-Path -LiteralPath $Config.StatePath)) { New-Item -ItemType Directory -Path $Config.StatePath -Force | Out-Null }
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Config.StatePath 'remote-demo-state.json') -Encoding UTF8

    $localAfter = & $LocalReadyTest $Config
    if (-not $localAfter.Ready) {
        Write-AeroLinkRemoteDemoLog -Config $Config -Run $run -Message "AEROLINK REMOTE DEMO NOT READY: local AeroLink readiness lost after tunnel start."
        throw "The tunnel is protected but local AeroLink readiness was lost. $($localAfter.Detail)"
    }

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
      .DESCRIPTION Contains no secrets: only the task identity, logon trigger,
        start-when-available settings, and the command that invokes the same
        tested start implementation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config
    )
    $scriptPath = Join-Path $Config.AeroLinkRoot 'product\scripts\AeroLinkRemoteDemo.ps1'
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>AeroLink protected remote-demo recovery (current user, no admin).</Description>
    <URI>\$script:RemoteDemoTaskName</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>$env:USERDOMAIN\$env:USERNAME</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$env:USERDOMAIN\$env:USERNAME</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
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
        [Parameter(Mandatory)][string]$Path
    )
    $xml = Get-AeroLinkRemoteDemoTaskXml -Config $Config
    Set-Content -LiteralPath $Path -Value $xml -Encoding Unicode
    return $Path
}

function Install-AeroLinkRemoteDemoTask {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Config
    )
    if (-not (Test-Path -LiteralPath $Config.StatePath)) { New-Item -ItemType Directory -Path $Config.StatePath -Force | Out-Null }
    $xmlPath = Join-Path $Config.StatePath 'remote-demo-task.xml'
    Save-AeroLinkRemoteDemoTaskXml -Config $Config -Path $xmlPath
    & schtasks.exe /Create /TN $script:RemoteDemoTaskName /XML $xmlPath /F
    if ($LASTEXITCODE -ne 0) { throw "schtasks /Create failed with exit code $LASTEXITCODE." }
    return Get-AeroLinkRemoteDemoTaskStatus
}

function Remove-AeroLinkRemoteDemoTask {
    & schtasks.exe /Delete /TN $script:RemoteDemoTaskName /F
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 267011) {
        throw "schtasks /Delete failed with exit code $LASTEXITCODE."
    }
    return [pscustomobject]@{ TaskName = $script:RemoteDemoTaskName; State = 'Removed' }
}

function Get-AeroLinkRemoteDemoTaskStatus {
    $task = Get-ScheduledTask -TaskName $script:RemoteDemoTaskName -ErrorAction SilentlyContinue
    if (-not $task) {
        return [pscustomobject]@{ TaskName = $script:RemoteDemoTaskName; Installed = $false; State = 'NotInstalled'; Detail = 'The AeroLink remote-demo recovery task is not installed.' }
    }
    $info = $task | Get-ScheduledTaskInfo
    return [pscustomobject]@{
        TaskName = $script:RemoteDemoTaskName
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
        [Parameter(Mandatory)]$Config
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

    $local = Test-AeroLinkRemoteDemoLocalReady -Config $Config
    $checks.Add([pscustomobject]@{ Name = 'Local AeroLink ready + built client'; Healthy = $local.Ready; Detail = $local.Detail })

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
    New-AeroLinkRemoteDemoRun, `
    Test-AeroLinkRemoteDemoPostgresReady, `
    Start-AeroLinkRemoteDemoPostgres, `
    Start-AeroLinkRemoteDemoPostgresHelper, `
    Stop-AeroLinkRemoteDemoOwnedProcess, `
    Start-AeroLinkRemoteDemoProductionHelper, `
    Invoke-AeroLinkProductionLauncher, `
    Start-AeroLinkRemoteDemoNgrok, `
    Get-AeroLinkRemoteDemoStartDecision, `
    Write-AeroLinkRemoteDemoLog, `
    Start-AeroLinkRemoteDemo, `
    Stop-AeroLinkRemoteDemo, `
    Get-AeroLinkRemoteDemoTaskXml, `
    Save-AeroLinkRemoteDemoTaskXml, `
    Install-AeroLinkRemoteDemoTask, `
    Remove-AeroLinkRemoteDemoTask, `
    Get-AeroLinkRemoteDemoTaskStatus, `
    Get-AeroLinkRemoteDemoStatus
