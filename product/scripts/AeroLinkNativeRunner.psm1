#Requires -Version 5.1
<#
    Bounded, file-redirected invocation of native executables and child PowerShell
    scripts, shared by the AeroLink launchers, PostgreSQL helpers, and the
    remote-demo operator mode.

    Why this exists: Windows PowerShell 5.1, when invoked from Task Scheduler, can
    block indefinitely waiting on stdio handles that a spawned grandchild keeps
    open. In the #483 incident, the postmaster spawned by `pg_ctl start` inherited
    the scheduled PowerShell's pipe handles, so the logon recovery task remained
    "Running" forever even after PostgreSQL finished crash recovery and answered
    real queries. Every invocation here redirects stdout/stderr to files and is
    bounded by an explicit timeout; on timeout only the exact process this call
    launched is terminated.
#>

function Invoke-AeroLinkNativeCommand {
    <#
      .SYNOPSIS Runs a native executable with file-redirected output and a hard timeout.
      .DESCRIPTION
        Returns a result object with ExitCode, TimedOut, ProcessId, StdOutPath,
        StdErrPath and Detail. On timeout the exact owned PID is terminated; no
        other process is ever touched.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
    [Parameter(Mandatory = $true)][string]$StandardOutput,
    [Parameter(Mandatory = $true)][string]$StandardError,
    [int]$TimeoutSeconds = 300,
    [string]$StepName = 'native command',
    # Only for leaf commands (no long-lived grandchildren such as a postmaster):
    # captures stdout/stderr through pipes. pg_ctl-style launches must leave this
    # off so a spawned postmaster can never hold a pipe open.
    [switch]$CaptureOutput
    )

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        return [pscustomobject]@{
            ExitCode = $null
            TimedOut = $false
            ProcessId = $null
            StdOutPath = $StandardOutput
            StdErrPath = $StandardError
            StdOutText = $null
            StdErrText = $null
            Detail = "Step '$StepName': executable not found at $FilePath"
        }
    }

    $outputDirectory = Split-Path -Parent $StandardOutput
    if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $argumentLine = (($ArgumentList | ForEach-Object {
        if ($_ -match '\s' -and -not ($_ -match '^".*"$')) { '"' + $_ + '"' } else { $_ }
    }) -join ' ')

    # System.Diagnostics.Process with inherited stdio: every caller of this runner
    # is required to run under file-redirected stdio (or an interactive console),
    # so a spawned grandchild (e.g. the postmaster) can never hold a scheduled
    # task's pipe open. WaitForExit(timeoutMs) gives both the bound and a reliable
    # ExitCode, unlike the polled Start-Process object in Windows PowerShell 5.1.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $argumentLine
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if ($CaptureOutput) {
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
    }
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    try {
        $process = [System.Diagnostics.Process]::Start($psi)
    }
    catch {
        return [pscustomobject]@{
            ExitCode = $null
            TimedOut = $false
            ProcessId = $null
            StdOutPath = $StandardOutput
            StdErrPath = $StandardError
            Detail = "Step '$StepName' failed to start ${FilePath}: $($_.Exception.Message)"
        }
    }

    $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $process.Kill(); $process.WaitForExit() } catch { }
        return [pscustomobject]@{
            ExitCode = $null
            TimedOut = $true
            ProcessId = $process.Id
            StdOutPath = $StandardOutput
            StdErrPath = $StandardError
            StdOutText = $null
            StdErrText = $null
            Detail = "Step '$StepName' (PID $($process.Id)) exceeded $TimeoutSeconds seconds; the owned helper was terminated. Logs: stdout=$StandardOutput stderr=$StandardError"
        }
    }

    $stdoutText = $null
    $stderrText = $null
    if ($CaptureOutput) {
        $stdoutText = $process.StandardOutput.ReadToEnd()
        $stderrText = $process.StandardError.ReadToEnd()
    }
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        TimedOut = $false
        ProcessId = $process.Id
        StdOutPath = $StandardOutput
        StdErrPath = $StandardError
        StdOutText = $stdoutText
        StdErrText = $stderrText
        Detail = "Step '$StepName' (PID $($process.Id)) exited with code $($process.ExitCode). Logs: stdout=$StandardOutput stderr=$StandardError"
    }
}

function Invoke-AeroLinkChildScript {
    <#
      .SYNOPSIS Runs a PowerShell script in a child Windows PowerShell 5.1 process,
        file-redirected and bounded by a hard timeout.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$StandardOutput,
        [Parameter(Mandatory = $true)][string]$StandardError,
        [int]$TimeoutSeconds = 300,
        [string]$StepName = 'child script'
    )
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $ArgumentList
    $argumentLine = (($arguments | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }) -join ' ')
    $outputDirectory = Split-Path -Parent $StandardOutput
    if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $flag = Join-Path $outputDirectory ("timeout-" + [guid]::NewGuid().ToString('N') + ".flag")
    $child = Start-Process -FilePath $powershell -ArgumentList $argumentLine -WindowStyle Hidden `
        -RedirectStandardOutput $StandardOutput -RedirectStandardError $StandardError -PassThru
    $watchdogCommand = "Start-Sleep -Seconds $TimeoutSeconds; Stop-Process -Id $($child.Id) -Force -ErrorAction SilentlyContinue; New-Item -ItemType File -Path '$flag' -Force | Out-Null"
    $watchdog = Start-Process -FilePath $powershell `
        -ArgumentList ("-NoProfile -Command `"$watchdogCommand`"") -WindowStyle Hidden -PassThru
    $child.WaitForExit()
    Stop-Process -Id $watchdog.Id -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $flag) {
        return [pscustomobject]@{
            ExitCode = $null
            TimedOut = $true
            ProcessId = $child.Id
            StdOutPath = $StandardOutput
            StdErrPath = $StandardError
            Detail = "Step '$StepName' (PID $($child.Id)) exceeded $TimeoutSeconds seconds; the owned helper was terminated. Logs: stdout=$StandardOutput stderr=$StandardError"
        }
    }
    return [pscustomobject]@{
        ExitCode = $child.ExitCode
        TimedOut = $false
        ProcessId = $child.Id
        StdOutPath = $StandardOutput
        StdErrPath = $StandardError
        Detail = "Step '$StepName' (PID $($child.Id)) exited with code $($child.ExitCode). Logs: stdout=$StandardOutput stderr=$StandardError"
    }
}

Export-ModuleMember -Function Invoke-AeroLinkNativeCommand, Invoke-AeroLinkChildScript
