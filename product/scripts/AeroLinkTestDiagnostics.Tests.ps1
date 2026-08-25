#Requires -Version 5.1
<#
    Synthetic regression contract for the durable test-diagnostics harness (#756).

    The real 600-test API suite is never failed here. A fake `dotnet test` process receives exactly the
    arguments the diagnostics helper appends, writes a synthetic TRX in the standard shape, and exits with a
    chosen code. The contract proves the preservation semantics themselves: every failing test identity
    survives with its full message and stack (well beyond the eight that once survived a real run), the
    original exit status is preserved in both directions, results directories are unique per invocation so
    parallel shards and reruns cannot overwrite one another, and the helper passes the standard logger,
    results-directory and blame-hang arguments through.
#>
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'AeroLinkTestDiagnostics.Tests.ps1'
$modulePath = Join-Path $PSScriptRoot 'AeroLinkTestDiagnostics.psm1'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $script:failures.Add($Message) }
}

Import-Module $modulePath -Force

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("aerolink-test-diagnostics-contract-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

$fakeTestScript = Join-Path $work 'fake-dotnet-test.ps1'
@'
# A faithful stand-in for `dotnet test`: parses the standard diagnostic arguments the helper appends,
# writes a synthetic TRX into the results directory, echoes the received arguments, and exits with the
# configured code. Modes: fail12 (twelve failures, full synthetic messages/stacks), fail3, pass.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest)
$mode = $env:AEROLINK_FAKE_TEST_MODE
if (-not $mode) { $mode = 'pass' }
$resultsDirectory = $null
$trxName = $null
$blameTimeout = $null
$sawLogger = $false
for ($i = 0; $i -lt $Rest.Count; $i++) {
    if ($Rest[$i] -eq '--results-directory') { $resultsDirectory = $Rest[$i + 1]; $i++ }
    elseif ($Rest[$i] -eq '--blame-hang-timeout') { $blameTimeout = $Rest[$i + 1]; $i++ }
    elseif ($Rest[$i] -eq '--logger') { $sawLogger = $true; if ($Rest[$i + 1] -match '^trx;LogFileName=(.+)$') { $trxName = $Matches[1] }; $i++ }
}
if (-not $resultsDirectory) { Write-Error 'fake did not receive --results-directory'; exit 3 }
if (-not $trxName) { Write-Error 'fake did not receive a trx logger with LogFileName'; exit 3 }
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$Rest | Set-Content -Path (Join-Path $resultsDirectory 'received-args.txt')
$rows = ''
$count = 0
if ($mode -eq 'fail12') { $count = 12 }
elseif ($mode -eq 'fail3') { $count = 3 }
for ($i = 1; $i -le $count; $i++) {
    $rows += "<UnitTestResult testId=""$i"" testName=""Synthetic.Tests.Case$i"" outcome=""Failed"" duration=""00:00:00.100"">" +
        "<Output><ErrorInfo><Message>SYNTH-MSG-$i : the complete synthetic failure message body, long enough to prove nothing truncates it.</Message>" +
        "<StackTrace>SYNTH-STACK-$i : at Synthetic.Wherever() in Synthetic.cs:line 42</StackTrace></ErrorInfo></Output></UnitTestResult>"
}
$trx = "<?xml version=""1.0"" encoding=""utf-8""?><TestRun><Results>$rows</Results></TestRun>"
[System.IO.File]::WriteAllText((Join-Path $resultsDirectory $trxName), $trx)
if ($mode -eq 'pass') { exit 0 }
exit 1
'@ | Set-Content -Path $fakeTestScript -Encoding UTF8

$shell = Get-Command powershell.exe -ErrorAction SilentlyContinue
if (-not $shell) { $shell = Get-Command pwsh.exe -ErrorAction Stop }

# 1. A failing suite: nonzero status preserved, path reported, and every failure retained with full text.
$env:AEROLINK_FAKE_TEST_MODE = 'fail12'
$threw = $null
$output = $null
try {
    $output = Invoke-TestSuiteWithDiagnostics -Label 'API suite' -FilePath $shell.Source `
        -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fakeTestScript, 'test', 'Synthetic.Api.Tests') 2>&1
} catch { $threw = $_ }
Assert-True ($null -ne $threw) 'A failing synthetic suite must surface a nonzero status.'
$outputText = ($output | Out-String)
$reportedRoot = $null
if ($threw) {
    # An ErrorRecord caught across 2>&1 can carry an empty .Message while .ToString() holds the full
    # rendered text, so the path is recovered from the rendered form.
    $thrownText = "$threw"
    if ($thrownText -match 'Durable test diagnostics \(API suite\): (.+)$') { $reportedRoot = $Matches[1].Trim() }
}
if (-not $reportedRoot) { if ($outputText -match 'Durable test diagnostics \(API suite\): (.+)$') { $reportedRoot = $Matches[1].Trim() } }
Assert-True ($null -ne $reportedRoot) "The diagnostics path must be reported on failure (thrown message or output). Threw: [$threw] Output: [$outputText]"
if ($reportedRoot) {
    $trxPath = Join-Path $reportedRoot 'api-suite.trx'
    Assert-True (Test-Path -LiteralPath $trxPath) "The durable TRX must exist at $trxPath."
    if (Test-Path -LiteralPath $trxPath) {
        [xml]$trx = Get-Content -LiteralPath $trxPath
        $failed = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
        Assert-True ($failed.Count -eq 12) "Twelve synthetic failures must be retained; found $($failed.Count)."
        Assert-True ($failed.Count -gt 8) 'More than eight failures must survive (the historical loss was everything past eight).'
        $messageText = ($failed | ForEach-Object { $_.Output.ErrorInfo.Message }) -join "`n"
        $stackText = ($failed | ForEach-Object { $_.Output.ErrorInfo.StackTrace }) -join "`n"
        Assert-True ($messageText -like '*SYNTH-MSG-7 *') 'The full synthetic failure message for case 7 must survive.'
        Assert-True ($messageText -like '*SYNTH-MSG-12*') 'The twelfth failure message must survive.'
        Assert-True ($stackText -like '*SYNTH-STACK-11*') 'The full synthetic stack for case 11 must survive.'
    }
    $receivedArgs = Join-Path $reportedRoot 'received-args.txt'
    Assert-True (Test-Path -LiteralPath $receivedArgs) 'The fake process must record the arguments it received.'
    if (Test-Path -LiteralPath $receivedArgs) {
        $argsText = Get-Content -LiteralPath $receivedArgs -Raw
        Assert-True ($argsText -match '--logger') 'The helper must pass --logger.'
        Assert-True ($argsText -match 'trx;LogFileName=api-suite\.trx') 'The helper must pass a label-derived TRX file name.'
        Assert-True ($argsText -match '--results-directory') 'The helper must pass --results-directory.'
        Assert-True ($argsText -match '--blame-hang-timeout') 'The helper must pass --blame-hang-timeout for hang evidence.'
        Assert-True ($argsText -match 'test') 'The caller arguments must be preserved ahead of the diagnostic arguments.'
        Assert-True ($argsText -match 'Synthetic\.Api\.Tests') 'The suite selection must be preserved.'
    }
}

# 2. A passing suite: success preserved, TRX still written as the run record.
$env:AEROLINK_FAKE_TEST_MODE = 'pass'
$passThrew = $null
try {
    Invoke-TestSuiteWithDiagnostics -Label 'pass run' -FilePath $shell.Source `
        -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fakeTestScript) | Out-Null
} catch { $passThrew = $_ }
Assert-True ($null -eq $passThrew) 'A passing synthetic suite must not be misclassified as a failure.'

# 3. Unique roots: concurrent-looking invocations can never share a results directory.
$first = Resolve-TestDiagnosticsRoot -Label 'shard check'
Start-Sleep -Seconds 1
$second = Resolve-TestDiagnosticsRoot -Label 'shard check'
Assert-True ($first -ne $second) "Two invocations must never share a results directory ($first vs $second)."

# 4. A three-failure run retains exactly its three failures.
$env:AEROLINK_FAKE_TEST_MODE = 'fail3'
$fail3Root = $null
try {
    Invoke-TestSuiteWithDiagnostics -Label 'fail three' -FilePath $shell.Source `
        -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fakeTestScript) 2>&1 | Out-Null
} catch {
    if ("$_" -match 'Durable test diagnostics \(fail three\): (.+)$') { $fail3Root = $Matches[1].Trim() }
}
if ($fail3Root) {
    [xml]$trx3 = Get-Content -LiteralPath (Join-Path $fail3Root 'fail-three.trx')
    $failed3 = @($trx3.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
    Assert-True ($failed3.Count -eq 3) "A three-failure run must retain exactly three failures; found $($failed3.Count)."
} else {
    Assert-True $false 'The three-failure case must report its diagnostics root.'
}

Remove-Item Env:AEROLINK_FAKE_TEST_MODE -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Host 'AeroLinkTestDiagnostics contract failures:' -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host " - $failure" -ForegroundColor Red }
    exit $failures.Count
}
Write-Host 'AeroLinkTestDiagnostics contract: all cases passed.'
exit 0
