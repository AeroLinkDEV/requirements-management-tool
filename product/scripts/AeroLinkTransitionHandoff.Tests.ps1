#Requires -Version 5.1
<#
    Regressions for the transition handoff and its budgets (#1041, #1043, #1053).

    Everything here runs on owned disposable state: temporary scripts under this run's own temp root, and
    child processes this suite starts and stops itself. Nothing touches the persistent PostgreSQL instance,
    production evidence, HOME services, or any installed scheduled task.

    The long-lived descendants below are the point of the #1053 cases, not incidental. A test whose child
    exits quickly passes against the defective implementation too, because the defect is precisely that the
    wrapper waits for a SURVIVOR rather than for the child.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkNativeRunner.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-handoff-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$startedProcessIds = [System.Collections.Generic.List[int]]::new()

try {
    # A survivor the wrapper must NOT wait for, and must NOT kill.
    $survivorScript = Join-Path $root 'survivor.ps1'
    Set-Content -LiteralPath $survivorScript -Encoding ASCII -Value @'
param([int]$Seconds = 45)
Start-Sleep -Seconds $Seconds
'@

    # A child that starts a long-lived, output-redirected grandchild and then exits immediately. This is the
    # exact shape of a transition continuation that restores the API and tunnel and returns.
    $handoffScript = Join-Path $root 'handoff.ps1'
    Set-Content -LiteralPath $handoffScript -Encoding ASCII -Value @'
param([string]$SurvivorScript, [string]$LogDirectory, [int]$ExitCode = 0)
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$out = Join-Path $LogDirectory 'survivor.stdout.log'
$err = Join-Path $LogDirectory 'survivor.stderr.log'
$p = Start-Process -FilePath $powershell `
    -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$SurvivorScript`" -Seconds 45" `
    -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
Set-Content -LiteralPath (Join-Path $LogDirectory 'survivor.pid') -Value $p.Id -Encoding ASCII
Write-Output "continuation restored a long-lived service (pid $($p.Id))"
exit $ExitCode
'@

    # ---------------------------------------------------------------------------------------------
    # #1053: the wrapper returns when the CHILD exits, not when its survivors do.
    # ---------------------------------------------------------------------------------------------
    $logs = Join-Path $root 'survive'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-AeroLinkOwnedTransitionScript -ScriptPath $handoffScript `
        -ArgumentList @('-SurvivorScript', $survivorScript, '-LogDirectory', $logs, '-ExitCode', '0') `
        -StandardOutput (Join-Path $logs 'child.stdout.log') -StandardError (Join-Path $logs 'child.stderr.log') `
        -TimeoutSeconds 120 -StepName 'survivor handoff'
    $clock.Stop()
    $survivorPid = $null
    if (Test-Path -LiteralPath (Join-Path $logs 'survivor.pid')) {
        $survivorPid = [int](Get-Content -LiteralPath (Join-Path $logs 'survivor.pid') -Raw).Trim()
        $startedProcessIds.Add($survivorPid)
    }
    Assert-True ($result.Outcome -eq 'Completed') "Scenario 1: a handoff leaving a survivor must complete, got '$($result.Outcome)'."
    Assert-True ($result.ExitCode -eq 0) "Scenario 1: the child's exit code must be preserved, got '$($result.ExitCode)'."
    # The survivor sleeps 45 s. Anything near that means the wrapper waited on the inherited handle again.
    Assert-True ($clock.Elapsed.TotalSeconds -lt 20) "Scenario 1: the wrapper must return when the child exits, not when its survivor does; took $([int]$clock.Elapsed.TotalSeconds)s."
    Assert-True ($null -ne $survivorPid) 'Scenario 1: the survivor PID should have been recorded.'
    if ($survivorPid) {
        $stillRunning = $null -ne (Get-Process -Id $survivorPid -ErrorAction SilentlyContinue)
        # Survivor identity preserved: a restored service is the PRODUCT of the transition, never cleaned up.
        Assert-True $stillRunning 'Scenario 1: the restored survivor must still be running after a successful handoff.'
    }

    # ---------------------------------------------------------------------------------------------
    # #1053 / #1043: a timeout must PROVE the continuation stopped before any recovery begins.
    # ---------------------------------------------------------------------------------------------
    $slowScript = Join-Path $root 'slow.ps1'
    Set-Content -LiteralPath $slowScript -Encoding ASCII -Value @'
param([string]$Marker)
Set-Content -LiteralPath $Marker -Value $PID -Encoding ASCII
Start-Sleep -Seconds 120
'@
    $timeoutLogs = Join-Path $root 'timeout'
    New-Item -ItemType Directory -Path $timeoutLogs -Force | Out-Null
    $marker = Join-Path $timeoutLogs 'slow.pid'
    $timeoutClock = [Diagnostics.Stopwatch]::StartNew()
    # Shortened injectable budget: the timeout and recovery paths are driven in seconds rather than by
    # waiting out a real supported-upgrade deadline.
    $timedOut = Invoke-AeroLinkOwnedTransitionScript -ScriptPath $slowScript -ArgumentList @('-Marker', $marker) `
        -StandardOutput (Join-Path $timeoutLogs 'slow.stdout.log') -StandardError (Join-Path $timeoutLogs 'slow.stderr.log') `
        -TimeoutSeconds 3 -StepName 'slow continuation'
    $timeoutClock.Stop()
    Assert-True ($timedOut.Outcome -eq 'TimedOut') "Scenario 2: an over-budget continuation must report TimedOut, got '$($timedOut.Outcome)'."
    Assert-True ($timedOut.TimedOut) 'Scenario 2: TimedOut must be set.'
    Assert-True ($null -eq $timedOut.ExitCode) 'Scenario 2: a timed-out continuation has no exit code to report.'
    Assert-True ($timeoutClock.Elapsed.TotalSeconds -lt 30) "Scenario 2: the timeout must be bounded; took $([int]$timeoutClock.Elapsed.TotalSeconds)s."
    Assert-True ($timedOut.CleanupProven) 'Scenario 2: the continuation shutdown must be PROVEN before recovery may begin.'
    if (Test-Path -LiteralPath $marker) {
        $slowPid = [int](Get-Content -LiteralPath $marker -Raw).Trim()
        $startedProcessIds.Add($slowPid)
        Start-Sleep -Milliseconds 750
        $slowAlive = $null -ne (Get-Process -Id $slowPid -ErrorAction SilentlyContinue)
        # This is the case the previous runner failed: it reported a terminal timeout while the PowerShell
        # continuation carried on running, so the caller released its lease over live work.
        Assert-True (-not $slowAlive) 'Scenario 2: the continuation process must actually be stopped when a timeout is reported.'
    }
    else { $script:failures.Add('Scenario 2: the slow continuation never recorded its PID.') }

    # ---------------------------------------------------------------------------------------------
    # #1053 / R4: exit results stay truthful, including negative codes and launch failure.
    # ---------------------------------------------------------------------------------------------
    foreach ($code in @(0, 7, -1)) {
        $exitScript = Join-Path $root ("exit" + ([Math]::Abs($code)) + ".ps1")
        Set-Content -LiteralPath $exitScript -Value "exit $code" -Encoding ASCII
        $exitResult = Invoke-AeroLinkOwnedTransitionScript -ScriptPath $exitScript `
            -StandardOutput (Join-Path $root 'exit.stdout.log') -StandardError (Join-Path $root 'exit.stderr.log') `
            -TimeoutSeconds 60 -StepName "exit $code"
        Assert-True ($exitResult.Outcome -eq 'Completed') "Scenario 3 ($code): a child that exits must report Completed, got '$($exitResult.Outcome)'."
        # A negative exit code used to be reported as "no exit code and no timeout", which reads as success.
        Assert-True ($exitResult.ExitCode -eq $code) "Scenario 3 ($code): the exact child exit code must survive, got '$($exitResult.ExitCode)'."
    }
    $missing = Invoke-AeroLinkOwnedTransitionScript -ScriptPath (Join-Path $root 'no-such-script.ps1') `
        -StandardOutput (Join-Path $root 'missing.stdout.log') -StandardError (Join-Path $root 'missing.stderr.log') `
        -TimeoutSeconds 30 -StepName 'missing continuation'
    Assert-True ($missing.Outcome -eq 'LaunchFailed') "Scenario 4: an absent continuation script must report LaunchFailed, got '$($missing.Outcome)'."
    Assert-True ($missing.ExitCode -ne 0) 'Scenario 4: a launch failure must never present as a zero exit code.'

    # ---------------------------------------------------------------------------------------------
    # #1043 / R5: progress reporting must not corrupt or fragment the work's own output.
    # ---------------------------------------------------------------------------------------------
    $tailPath = Join-Path $root 'tail.log'
    Set-Content -LiteralPath $tailPath -Value '' -NoNewline -Encoding ASCII
    $tail = New-AeroLinkLogTail -Path $tailPath
    $line = "restore 50% complete - caf" + [char]0xE9 + " ready`r`n"
    $bytes = [Text.Encoding]::UTF8.GetBytes($line)
    $split = [Array]::IndexOf($bytes, [byte]0xC3) + 1
    $stream = [IO.File]::Open($tailPath, 'Append', 'Write', 'ReadWrite'); $stream.Write($bytes, 0, $split); $stream.Dispose()
    $firstPoll = @($tail.Read())
    # A line that is not yet terminated must be held, not emitted in pieces.
    Assert-True ($firstPoll.Count -eq 0) "Scenario 5: an unterminated line must not be emitted early, got $($firstPoll.Count) line(s)."
    $stream = [IO.File]::Open($tailPath, 'Append', 'Write', 'ReadWrite'); $stream.Write($bytes, $split, $bytes.Length - $split); $stream.Dispose()
    $secondPoll = @($tail.Read())
    Assert-True ($secondPoll.Count -eq 1) "Scenario 5: the completed line must be emitted once, got $($secondPoll.Count)."
    # A multi-byte character split across a polling boundary was previously destroyed by a fresh decoder.
    Assert-True ($secondPoll.Count -eq 1 -and $secondPoll[0] -eq $line.TrimEnd("`r", "`n")) `
        "Scenario 5: a multi-byte character split across a poll must survive intact, got '$($secondPoll -join '|')'."

    # ---------------------------------------------------------------------------------------------
    # #1043: the budgets must stay coherent RELATIVE TO EACH OTHER, not merely large.
    # ---------------------------------------------------------------------------------------------
    $budget = Get-AeroLinkTransitionBudget
    Assert-True ($budget.SupportedUpgradeSeconds -lt $budget.ContinuationSeconds) `
        'Scenario 6: the supported-upgrade deadline must expire before the continuation wrapper.'
    Assert-True ($budget.DelegatedUpdateSeconds -gt $budget.ContinuationSeconds) `
        'Scenario 6: the outer delegated update must outlast a single continuation.'
    if ($budget.InstalledTaskTimeLimit -match '^PT(?<minutes>\d+)M$') {
        $taskSeconds = [int]$Matches['minutes'] * 60
        # Two sequential continuations are reachable: a primary handoff and then a recovery handoff, and the
        # recovery path can itself re-enter the clone-validated upgrade.
        $sequentialWorstCase = (2 * $budget.ContinuationSeconds) + 600
        Assert-True ($taskSeconds -gt $sequentialWorstCase) `
            "Scenario 6: the installed task limit ($taskSeconds s) must exceed the sequential worst case ($sequentialWorstCase s), or Task Scheduler hard-terminates a transition that is still inside its own budget."
        Assert-True ($taskSeconds -gt $budget.DelegatedUpdateSeconds) `
            'Scenario 6: the installed task limit must exceed the outer delegated-update budget.'
    }
    else { $script:failures.Add("Scenario 6: the installed task limit '$($budget.InstalledTaskTimeLimit)' is not in PT<minutes>M form.") }

    # Both installed task definitions must agree, or the two tasks disagree about how long a transition may take.
    $demoConfig = [pscustomobject]@{
        AeroLinkRoot = (Join-Path $root 'source'); StatePath = (Join-Path $root 'state')
        LogsPath = (Join-Path $root 'logs'); PublicUrl = 'https://handoff-tests.invalid'
    }
    $reconcileXml = Get-AeroLinkReconcileTaskXml -Config $demoConfig -IntervalMinutes 30
    $recoveryXml = Get-AeroLinkRemoteDemoTaskXml -Config $demoConfig
    foreach ($pair in @(@{ Name = 'reconcile'; Xml = $reconcileXml }, @{ Name = 'recovery'; Xml = $recoveryXml })) {
        Assert-True ($pair.Xml -match [regex]::Escape("<ExecutionTimeLimit>$($budget.InstalledTaskTimeLimit)</ExecutionTimeLimit>")) `
            "Scenario 7: the $($pair.Name) task XML must carry the shared execution time limit $($budget.InstalledTaskTimeLimit)."
        Assert-True ($pair.Xml -match '<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>') `
            "Scenario 7: the $($pair.Name) task must keep IgnoreNew, so a long run skips triggers rather than stacking instances."
    }

    # ---------------------------------------------------------------------------------------------
    # #1041: the stop token's bytes must not depend on the console codepage of the host that wrote it.
    # ---------------------------------------------------------------------------------------------
    $byteProbe = Join-Path $root 'token-bytes.ps1'
    Set-Content -LiteralPath $byteProbe -Encoding ASCII -Value @'
param([string]$OutFile, [string]$Writer, [string]$ModulePath)
chcp 65001 | Out-Null
Import-Module $ModulePath -Force
$si = New-Object System.Diagnostics.ProcessStartInfo
$si.FileName = Join-Path $env:WINDIR 'System32\cmd.exe'
$si.Arguments = "/c more > `"$OutFile`""
$si.UseShellExecute = $false; $si.CreateNoWindow = $true; $si.RedirectStandardInput = $true
$p = New-Object System.Diagnostics.Process; $p.StartInfo = $si
if ($Writer -eq 'legacy') {
    [void]$p.Start()
    $p.StandardInput.WriteLine('stop'); $p.StandardInput.Flush(); $p.StandardInput.Close()
}
else {
    $previous = Push-AeroLinkDeterministicProcessInputEncoding
    try { [void]$p.Start(); $null = $p.StandardInput }
    finally { Pop-AeroLinkDeterministicProcessInputEncoding -Previous $previous }
    Write-AeroLinkProcessControlToken -Process $p -Token 'stop'
    $p.StandardInput.BaseStream.Close()
}
[void]$p.WaitForExit(15000)
(([IO.File]::ReadAllBytes($OutFile) | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')
'@
    $processControlModule = Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1'
    foreach ($shellHost in @(
            @{ Name = 'Windows PowerShell 5.1'; Exe = $powershell },
            @{ Name = 'PowerShell 7'; Exe = (Get-Command pwsh.exe -ErrorAction SilentlyContinue).Source })) {
        if (-not $shellHost.Exe) { continue }
        $byteFile = Join-Path $root ("token-" + ($shellHost.Name -replace '[^A-Za-z0-9]', '') + ".bin")
        $resultFile = Join-Path $root ("token-" + ($shellHost.Name -replace '[^A-Za-z0-9]', '') + ".txt")
        # A dedicated console per case: chcp changes the codepage of the console the process owns, and a
        # shared one would carry the change into unrelated cases.
        $inner = "& '$($shellHost.Exe)' -NoProfile -ExecutionPolicy Bypass -File '$byteProbe' -OutFile '$byteFile' -Writer 'current' -ModulePath '$processControlModule' | Set-Content -LiteralPath '$resultFile'"
        $probe = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $inner) -WindowStyle Hidden -PassThru
        $probe.WaitForExit()
        $bytes = if (Test-Path -LiteralPath $resultFile) { (Get-Content -LiteralPath $resultFile -Raw).Trim() } else { '' }
        # 'stop' + CRLF, with no encoding preamble. `more` appends its own trailing CRLF.
        Assert-True ($bytes -like '73 74 6F 70 0D 0A*') `
            "Scenario 8 ($($shellHost.Name)): the stop token must be written as exact preamble-free bytes, got '$bytes'."
        Assert-True ($bytes -notlike 'EF BB BF*') `
            "Scenario 8 ($($shellHost.Name)): the stop token must not carry a UTF-8 preamble, got '$bytes'."
    }
}
finally {
    foreach ($processId in $startedProcessIds) {
        try { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue } catch { }
    }
    # Owned disposable state only; this suite created this root and removes exactly it.
    try { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAIL: $failure" -ForegroundColor Red }
    throw "Transition handoff contracts failed ($($failures.Count))."
}
Write-Host 'Transition handoff contracts passed (survivor handoff, proven timeout cleanup, truthful exit results, tail integrity, budget coherence, stop-token bytes).' -ForegroundColor Green
