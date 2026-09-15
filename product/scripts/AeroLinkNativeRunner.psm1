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

# For identity-proven termination and native-handle exit codes in Invoke-AeroLinkOwnedTransitionScript.
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1')

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
        if ($_ -match '\s' -and -not ($_ -match '^".*"$')) { '"' + $_ + '"' } else { $_ }
    }) -join ' ')
    $outputDirectory = Split-Path -Parent $StandardOutput
    if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $flag = Join-Path $outputDirectory ("timeout-" + [guid]::NewGuid().ToString('N') + ".flag")
    $marker = Join-Path $outputDirectory ("exit-" + [guid]::NewGuid().ToString('N') + ".txt")
    $cmd = Join-Path $env:WINDIR 'System32\cmd.exe'
    # cmd /v:on with delayed expansion (!ERRORLEVEL!) captures the child PowerShell
    # exit code at execution time into a marker file; Start-Process object ExitCode
    # is unreliable in Windows PowerShell 5.1 for a polled process.
    $inner = '"' + $powershell + '" ' + $argumentLine + ' & echo !ERRORLEVEL!> "' + $marker + '"'
    $wrapped = '/v:on /d /s /c "' + $inner + '"'
    $child = Start-Process -FilePath $cmd -ArgumentList $wrapped -WindowStyle Hidden `
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
    $exitCode = $null
    if (Test-Path -LiteralPath $marker) {
        $markerText = (Get-Content -LiteralPath $marker -Raw -ErrorAction SilentlyContinue).Trim()
        if ($markerText -match '^\d+$') { $exitCode = [int]$markerText }
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        TimedOut = $false
        ProcessId = $child.Id
        StdOutPath = $StandardOutput
        StdErrPath = $StandardError
        Detail = "Step '$StepName' (PID $($child.Id)) exited with code $exitCode. Logs: stdout=$StandardOutput stderr=$StandardError"
    }
}

function New-AeroLinkLogTail {
    <#
      .SYNOPSIS A stateful tail over a file another process is writing, safe to poll repeatedly.
      .DESCRIPTION
        Stateful on purpose, and the state is the whole point.

        The obvious version - open the file at a byte offset on each poll, decode, print - is wrong twice.
        A UTF-8 character split across a polling boundary is decoded by two different decoders and destroyed
        (measured: a 2-byte character straddling the boundary emitted the leading bytes as one fragment and
        the trailing byte as U+FFFD). And a line that has not been fully written yet is emitted in pieces, so
        one log line becomes several, which is worse than useless when the operator is reading it for
        progress.

        So a single Decoder and a partial-line buffer live for the whole wait. Complete lines are emitted;
        an unterminated tail is held until its newline arrives or Flush is called at the end.

        Sharing is FileShare ReadWrite + Delete because the child is writing this file concurrently. Anything
        narrower would turn progress reporting into a write failure inside the child, which is exactly
        backwards: reporting on the work must never be able to break the work.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Prefix = ''
    )
    $tail = [pscustomobject]@{
        Path      = $Path
        Prefix    = $Prefix
        Position  = [long]0
        Decoder   = [System.Text.Encoding]::UTF8.GetDecoder()
        Pending   = ''
        Emitted   = [long]0
    }
    $tail | Add-Member -MemberType ScriptMethod -Name Read -Value {
        if (-not (Test-Path -LiteralPath $this.Path -PathType Leaf)) { return @() }
        $stream = $null
        try {
            $stream = [System.IO.File]::Open($this.Path, [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        }
        catch { return @() }
        try {
            if ($stream.Length -le $this.Position) { return @() }
            [void]$stream.Seek($this.Position, [System.IO.SeekOrigin]::Begin)
            $count = [int][Math]::Min([long]65536, $stream.Length - $this.Position)
            $buffer = New-Object byte[] $count
            $read = $stream.Read($buffer, 0, $count)
            if ($read -le 0) { return @() }
            $this.Position = $this.Position + $read
            # The retained decoder carries any incomplete multi-byte sequence into the next call.
            $chars = New-Object char[] ($this.Decoder.GetCharCount($buffer, 0, $read))
            $decoded = $this.Decoder.GetChars($buffer, 0, $read, $chars, 0)
            $this.Pending = $this.Pending + (New-Object string($chars, 0, $decoded))
        }
        catch { return @() }
        finally { if ($stream) { $stream.Dispose() } }
        $lines = @()
        # Only complete lines leave the buffer; the remainder waits for its newline.
        while ($true) {
            $index = $this.Pending.IndexOf("`n")
            if ($index -lt 0) { break }
            $line = $this.Pending.Substring(0, $index).TrimEnd("`r")
            $this.Pending = $this.Pending.Substring($index + 1)
            $lines += $line
        }
        $this.Emitted = $this.Emitted + $lines.Count
        return $lines
    }
    $tail | Add-Member -MemberType ScriptMethod -Name Flush -Value {
        $lines = @($this.Read())
        if (-not [string]::IsNullOrEmpty($this.Pending)) {
            $lines += $this.Pending.TrimEnd("`r")
            $this.Pending = ''
        }
        return $lines
    }
    return $tail
}

function Invoke-AeroLinkOwnedTransitionScript {
    <#
      .SYNOPSIS Runs a transition continuation as an OWNED child: file-redirected, bounded, provably stopped.
      .DESCRIPTION
        Deliberately NOT Invoke-AeroLinkChildScript, and the difference is the reason this exists.

        That runner wraps the child in cmd.exe so a marker file can carry the exit code back. Its watchdog
        then terminates the cmd.exe PID - not the PowerShell process actually doing the work. Measured
        against a 2-second budget: the call returned in 2.39 s reporting TimedOut, while the PowerShell
        continuation was still running. For a leaf helper that is survivable. For a TRANSITION it is not: the
        caller would release its lease and begin recovery while the previous continuation was still advancing
        the source, which is two writers on one installation. A reported terminal timeout must not be a lie
        about what is still executing.

        Three consequences shape this function:

        * No cmd.exe wrapper. The child IS the continuation, so its PID is the thing that can be owned,
          terminated and proven stopped. That also removes the marker file, and with it the marker parser's
          inability to represent a negative exit code (`^\d+$` turned exit -1 into "no exit code, did not
          time out" - a failure that reads as a success).
        * The exit code comes from the native handle, the precedent already used by AeroLinkBootstrap.psm1
          and Start-AeroLinkRemoteDemoProductionHelper. GetExitCodeProcess returns the real value, negatives
          included, and holding the handle pins identity across PID reuse.
        * Termination on timeout goes through Stop-AeroLinkProvenProcess, which refuses unless start time and
          executable still match what was launched. If the exit cannot be PROVEN, that is reported as such
          rather than as a clean timeout, so the caller keeps its restoration obligation.

        What this must never do is kill a process tree. A successful continuation deliberately leaves the
        restored API and tunnel running; they are the product of the work, not leftovers of it. Only the
        continuation this call launched is terminated.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$StandardOutput,
        [Parameter(Mandatory = $true)][string]$StandardError,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [string]$StepName = 'transition continuation',
        [switch]$StreamToHost,
        # Emitted alongside streamed output so silence is never mistaken for measured lack of progress.
        [int]$ProgressIntervalSeconds = 30,
        # Additional logs written by the work itself rather than by the child's stdout - the production
        # launcher's own redirected files and remote-demo.log. Without these the operator sees the
        # continuation's stdout only, which is nearly silent during backup, restore and build.
        [string[]]$AdditionalProgressLog = @()
    )
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # An absent script still LAUNCHES powershell.exe successfully; the host then exits -196608. That is
    # non-zero, so compensation would fire, but the outcome an operator reads should say what happened
    # rather than leave them decoding 0xFFFD0000.
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        return [pscustomobject]@{
            Outcome = 'LaunchFailed'; ExitCode = $null; TimedOut = $false; CleanupProven = $true
            ProcessId = $null; ElapsedSeconds = 0; StdOutPath = $StandardOutput; StdErrPath = $StandardError
            Detail = "Step '$StepName' has no script at $ScriptPath. Nothing was started, so nothing was left running by this call."
        }
    }
    foreach ($path in @($StandardOutput, $StandardError)) {
        $directory = Split-Path -Parent $path
        if ($directory -and -not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
    }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath) + $ArgumentList
    $argumentLine = (($arguments | ForEach-Object {
        if ($_ -match '\s' -and -not ($_ -match '^".*"$')) { '"' + $_ + '"' } else { $_ }
    }) -join ' ')

    $started = $null
    try {
        $started = Start-Process -FilePath $powershell -ArgumentList $argumentLine -WindowStyle Hidden `
            -RedirectStandardOutput $StandardOutput -RedirectStandardError $StandardError -PassThru
    }
    catch {
        return [pscustomobject]@{
            Outcome = 'LaunchFailed'; ExitCode = $null; TimedOut = $false; CleanupProven = $true
            ProcessId = $null; ElapsedSeconds = 0; StdOutPath = $StandardOutput; StdErrPath = $StandardError
            Detail = "Step '$StepName' could not start ${ScriptPath}: $($_.Exception.Message) Nothing was left running by this call."
        }
    }
    if ($null -eq $started) {
        return [pscustomobject]@{
            Outcome = 'LaunchFailed'; ExitCode = $null; TimedOut = $false; CleanupProven = $true
            ProcessId = $null; ElapsedSeconds = 0; StdOutPath = $StandardOutput; StdErrPath = $StandardError
            Detail = "Step '$StepName' could not start $ScriptPath. Nothing was left running by this call."
        }
    }

    # Pin identity immediately. The handle keeps the exit code readable after exit and survives PID reuse;
    # start time and image path are what Stop-AeroLinkProvenProcess will require before it terminates.
    $nativeHandle = $started.Handle
    $processId = $started.Id
    $startedAtUtc = $null
    $imagePath = $null
    try { $startedAtUtc = $started.StartTime.ToUniversalTime() } catch { }
    try { $imagePath = $started.Path } catch { }
    if (-not $imagePath) { $imagePath = $powershell }

    $tails = @()
    if ($StreamToHost) {
        $tails += (New-AeroLinkLogTail -Path $StandardOutput -Prefix '      ')
        $tails += (New-AeroLinkLogTail -Path $StandardError -Prefix '      [stderr] ')
        foreach ($extra in $AdditionalProgressLog) {
            if ($extra) { $tails += (New-AeroLinkLogTail -Path $extra -Prefix "      [$(Split-Path -Leaf $extra)] ") }
        }
    }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastProgress = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    while ($true) {
        $started.Refresh()
        if ($started.HasExited) { break }
        if ((Get-Date) -ge $deadline) { $timedOut = $true; break }
        foreach ($tail in $tails) {
            foreach ($line in @($tail.Read())) { Write-Host "$($tail.Prefix)$line" }
        }
        if ($StreamToHost -and $lastProgress.Elapsed.TotalSeconds -ge $ProgressIntervalSeconds) {
            $remaining = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalSeconds)
            # Phase, elapsed and the deadline that applies, so a quiet stretch is legible as "still inside
            # budget" rather than as an unexplained absence of output.
            Write-Host ("      [$StepName] running for $([int]$clock.Elapsed.TotalSeconds)s; " +
                "${remaining}s of the ${TimeoutSeconds}s budget remaining; logs: $StandardOutput") -ForegroundColor DarkGray
            $lastProgress.Restart()
        }
        Start-Sleep -Milliseconds 250
    }
    foreach ($tail in $tails) {
        foreach ($line in @($tail.Flush())) { Write-Host "$($tail.Prefix)$line" }
    }

    if ($timedOut) {
        # Bounded, identity-proven termination of the continuation ONLY. Restored services are the product
        # of the work and are never touched here.
        $cleanupProven = $false
        $cleanupDetail = ''
        try {
            Stop-AeroLinkProvenProcess -Process ([pscustomobject]@{
                    ProcessId = $processId; StartedAt = $startedAtUtc; ExecutablePath = $imagePath
                })
            $cleanupProven = $true
        }
        catch { $cleanupDetail = $_.Exception.Message }
        if ($cleanupProven) {
            # Requested is not stopped. Re-read before claiming it.
            try {
                $started.Refresh()
                if (-not $started.HasExited) { $cleanupProven = $false; $cleanupDetail = 'The continuation was still running after termination was requested.' }
            }
            catch { $cleanupProven = $false; $cleanupDetail = 'The continuation exit could not be verified.' }
        }
        $clock.Stop()
        $detail = if ($cleanupProven) {
            "Step '$StepName' (PID $processId) exceeded $TimeoutSeconds seconds and was terminated; its exit is proven. Logs: stdout=$StandardOutput stderr=$StandardError"
        }
        else {
            "Step '$StepName' (PID $processId) exceeded $TimeoutSeconds seconds and its shutdown could NOT be proven ($cleanupDetail). The transition may still be executing. Logs: stdout=$StandardOutput stderr=$StandardError"
        }
        return [pscustomobject]@{
            Outcome = 'TimedOut'; ExitCode = $null; TimedOut = $true; CleanupProven = $cleanupProven
            ProcessId = $processId; ElapsedSeconds = [int]$clock.Elapsed.TotalSeconds
            StdOutPath = $StandardOutput; StdErrPath = $StandardError; Detail = $detail
        }
    }

    $clock.Stop()
    $exitCode = $null
    try { $exitCode = [AeroLink.ProcessAccess]::ExitCode($nativeHandle) } catch { $exitCode = $null }
    if ($null -eq $exitCode) {
        # The child finished but its result cannot be read. That is not success and must not be reported as
        # one; the caller's compensation has to run.
        return [pscustomobject]@{
            Outcome = 'ExitUnavailable'; ExitCode = $null; TimedOut = $false; CleanupProven = $true
            ProcessId = $processId; ElapsedSeconds = [int]$clock.Elapsed.TotalSeconds
            StdOutPath = $StandardOutput; StdErrPath = $StandardError
            Detail = "Step '$StepName' (PID $processId) exited, but its exit code could not be read. Logs: stdout=$StandardOutput stderr=$StandardError"
        }
    }
    return [pscustomobject]@{
        Outcome = 'Completed'; ExitCode = $exitCode; TimedOut = $false; CleanupProven = $true
        ProcessId = $processId; ElapsedSeconds = [int]$clock.Elapsed.TotalSeconds
        StdOutPath = $StandardOutput; StdErrPath = $StandardError
        Detail = "Step '$StepName' (PID $processId) exited with code $exitCode after $([int]$clock.Elapsed.TotalSeconds)s. Logs: stdout=$StandardOutput stderr=$StandardError"
    }
}

Export-ModuleMember -Function Invoke-AeroLinkNativeCommand, Invoke-AeroLinkChildScript, `
    New-AeroLinkLogTail, Invoke-AeroLinkOwnedTransitionScript
