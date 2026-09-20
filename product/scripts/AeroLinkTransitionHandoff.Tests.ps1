#Requires -Version 5.1
<#
    Regressions for the transition handoff boundary, its budgets and the stop-token bytes (#1041, #1043, #1053).

    Containment, completion receipts, admission, launch requests and log streaming are contracted by
    AeroLinkTransitionAuthority.Tests.ps1 against the real authority, and that suite runs at the end of this one. This
    suite keeps what is specific to the remote-demo module: the continuation boundary refusing to run outside a
    transition, the budget composition and installed task limits, and the stop token that must be byte-identical on
    both PowerShell hosts.

    Everything here runs on owned disposable state. Nothing touches the persistent PostgreSQL instance, production
    evidence, HOME services, or any installed scheduled task.
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProcessControl.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-handoff-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'

try {
    # ---------------------------------------------------------------------------------------------
    # #1053: a continuation exists only inside a transition owned by an outer authority.
    # ---------------------------------------------------------------------------------------------
    # Outside one there is no job to contain it and no authority to launch what it restores - which is exactly how a
    # restored API came to hold its caller's output open. The boundary refuses before it starts anything.
    $previousHandoff = $env:AEROLINK_TRANSITION_HANDOFF
    $env:AEROLINK_TRANSITION_HANDOFF = $null
    $refused = $null
    try {
        Invoke-AeroLinkRemoteDemoHandoff -Config ([pscustomobject]@{ AeroLinkRoot = $root; LogsPath = (Join-Path $root 'logs') }) `
            -Topology ([pscustomobject]@{ TunnelRunning = $false; RuntimeRunning = $true }) -HeadSha ('a' * 40) | Out-Null
    }
    catch { $refused = $_.Exception.Message }
    finally { $env:AEROLINK_TRANSITION_HANDOFF = $previousHandoff }
    Assert-True ($refused -match 'only inside a HOME transition') "Scenario 1: a continuation outside a transition must refuse, got '$refused'."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'logs'))) 'Scenario 1: a refused continuation starts and writes nothing.'

    # ---------------------------------------------------------------------------------------------
    # #1043: the budgets must stay coherent RELATIVE TO EACH OTHER, not merely large.
    # ---------------------------------------------------------------------------------------------
    $budget = Get-AeroLinkTransitionBudget
    Assert-True ($budget.ProductionApiReadinessSeconds -gt 170 -and $budget.ProductionApiReadinessSeconds -lt $budget.SupportedUpgradeSeconds) `
        'Scenario 6: cold API seeding exceeded 120 seconds; its readiness allowance must cover the measured cold start while remaining inside the bounded production launcher.'
    $startupTokens = $null; $startupErrors = $null
    $startupAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Start-AeroLinkProduction.ps1'), [ref]$startupTokens, [ref]$startupErrors)
    $serviceCall = @($startupAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Start-AeroLinkService' }, $true))
    Assert-True ($serviceCall.Count -eq 1) 'Scenario 6: the production API must have one service-launch call.'
    if ($serviceCall.Count -eq 1) {
        $timeoutValue = $null
        for ($i = 0; $i -lt $serviceCall[0].CommandElements.Count - 1; $i++) {
            $element = $serviceCall[0].CommandElements[$i]
            if ($element -is [Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -eq 'TimeoutSeconds') {
                $timeoutValue = & ([scriptblock]::Create($serviceCall[0].CommandElements[$i + 1].Extent.Text))
            }
        }
        Assert-True ($timeoutValue -eq $budget.ProductionApiReadinessSeconds) 'Scenario 6: production startup must pass the cold-start budget instead of falling back to the generic 120-second default.'
    }
    # COMPOSITION, not arithmetic over chosen numbers. The continuation must cover every stage it actually
    # contains; a continuation smaller than the sum of its stages terminates work still inside its own
    # component allowance, which is the original 900 s defect one level up.
    $stageSum = $budget.PostgresRecoverySeconds + $budget.SupportedUpgradeSeconds + $budget.NgrokProtectionSeconds
    Assert-True ($budget.ContinuationSeconds -ge $stageSum) `
        "Scenario 6: the continuation budget ($($budget.ContinuationSeconds) s) must cover its sequential stages ($stageSum s: PostgreSQL recovery, production launcher, ngrok protection)."
    Assert-True ($budget.SupportedUpgradeSeconds -lt $budget.ContinuationSeconds) `
        'Scenario 6: the supported-upgrade deadline must expire before the continuation wrapper.'
    # The post-advance continuation is a nested Update. An enclosing wrapper sharing the same independently
    # restarted allowance would not outlast what it encloses.
    Assert-True ($budget.PostAdvanceContinuationSeconds -gt $budget.ContinuationSeconds) `
        'Scenario 6: the post-advance continuation must outlast a single continuation.'
    Assert-True ($budget.DelegatedUpdateSeconds -gt $budget.PostAdvanceContinuationSeconds) `
        'Scenario 6: the outer delegation must strictly outlast the nested update it encloses.'
    Assert-True ($budget.ReconcileWorstCaseSeconds -ge (($budget.SequentialAttempts * $budget.ContinuationSeconds) + $budget.OuterOverheadSeconds)) `
        'Scenario 6: the reconcile worst case must account for every permitted sequential attempt plus outer overhead.'
    Assert-True ($budget.DelegatedUpdateSeconds -ge ($budget.PostAdvanceContinuationSeconds + $budget.ReconcileWorstCaseSeconds)) `
        'Scenario 6: the outer delegation must cover a complete inner Update including its recovery.'
    if ($budget.InstalledTaskTimeLimit -match '^PT(?<minutes>\d+)M$') {
        $taskSeconds = [int]$Matches['minutes'] * 60
        # The installed tasks invoke AeroLinkRemoteDemo.ps1 only, so the reconcile worst case is the bound
        # they must clear. They cannot reach the operator-invoked delegation path.
        Assert-True ($taskSeconds -gt $budget.ReconcileWorstCaseSeconds) `
            "Scenario 6: the installed task limit ($taskSeconds s) must exceed the reconcile worst case ($($budget.ReconcileWorstCaseSeconds) s), or Task Scheduler hard-terminates a transition still inside its own budget."
    }
    else { $script:failures.Add("Scenario 6: the installed task limit '$($budget.InstalledTaskTimeLimit)' is not in PT<minutes>M form.") }
    # The scheduled tasks must not be able to reach the delegation path; if they ever do, the task limit
    # above is sized against the wrong bound.
    foreach ($taskXml in @((Get-AeroLinkReconcileTaskXml -Config ([pscustomobject]@{
                    AeroLinkRoot = 'C:\aerolink'; StatePath = 'C:\state'; LogsPath = 'C:\logs'; PublicUrl = 'https://x.invalid'
                }) -IntervalMinutes 30))) {
        Assert-True ($taskXml -notmatch 'Configure-AeroLinkProductionSource') `
            'Scenario 6: an installed task must not invoke the delegated Update path, which is budgeted for operator invocation.'
    }

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
    # Owned disposable state only; this suite created this root and removes exactly it.
    try { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAIL: $failure" -ForegroundColor Red }
    throw "Transition handoff contracts failed ($($failures.Count))."
}
& $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'AeroLinkTransitionAuthority.Tests.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Transition authority contracts failed.' }
Write-Host ('Transition handoff contracts passed (continuation refused outside a transition, budget composition, installed task ' +
    'limits, stop-token bytes; transition authority contracts passed).') -ForegroundColor Green
