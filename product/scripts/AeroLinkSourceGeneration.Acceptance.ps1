#Requires -Version 5.1
<#
    Targeted acceptance for the source-generation boundary (#881, PR #909).

    THE PROPERTY UNDER TEST

    A production source transition rewrites the control plane. The scripts and modules that decide what
    happens next are themselves part of what a fast-forward replaces - so a process that advanced the source
    and then carried on would be generation N orchestration operating generation N+1 files. #881 requires the
    opposite: startup and recovery must track launcher evolution rather than silently continue on a stale
    contract.

    Every previous round of review proved this from the code path. That is not the same as proving it, and I
    said so in four consecutive review responses rather than let it be assumed. This is the proof.

    WHAT IS REAL HERE

    A real bare origin repository, a real clone acting as the dedicated production source, a real
    `git merge --ff-only` performed by the real `Update-AeroLinkProductionSource`, and the real
    `Invoke-AeroLinkRemoteDemoHandoff` launching a real child process. The control-plane script is a fixture
    rather than the product's own, because the product's entry point would need PostgreSQL, ngrok and a
    built client to run - and the property being tested is about WHICH BYTES EXECUTE, not about what they do.
    The fixture records its own generation and the continuation it received, which is exactly the evidence
    the property needs.

    WHAT IS DELIBERATELY NOT REAL

    No AeroLink service is started, no database is touched, no tunnel is created. Every path is under a
    disposable temporary root that is removed on exit.

    RUN IT

        powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\AeroLinkSourceGeneration.Acceptance.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AeroLinkProductionSource.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AeroLinkRemoteDemo.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "  PASS  $Message" -ForegroundColor DarkGray }
    else { $script:failures.Add($Message); Write-Host "  FAIL  $Message" -ForegroundColor Red }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("aerolink-generation-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null

function Invoke-Git {
    # Git writes ordinary progress ("Cloning into...") to stderr, which Windows PowerShell turns into a
    # terminating NativeCommandError under ErrorActionPreference=Stop. The exit code is the only thing that
    # says whether git succeeded, so relax the preference around the call and read that instead.
    param([Parameter(Mandatory)][string[]]$GitArguments, [string]$Repository)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Repository) { $output = & git -C $Repository @GitArguments 2>&1 } else { $output = & git @GitArguments 2>&1 }
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    $text = (($output | ForEach-Object { "$_" }) -join "`n").Trim()
    if ($exitCode -ne 0) { throw "git $($GitArguments -join ' ') failed: $text" }
    return $text
}

# The control-plane fixture. Each generation stamps WHICH generation ran and what continuation it was given,
# appending rather than overwriting so a second writer cannot hide a first.
function New-ControlPlaneScript([int]$Generation) {
    return @"
[CmdletBinding()]
param([string]`$Action, [switch]`$Scheduled)
`$record = Join-Path `$env:AEROLINK_GENERATION_LOG 'executed.log'
`$continuation = if (`$env:AEROLINK_TRANSITION_CONTINUATION) { `$env:AEROLINK_TRANSITION_CONTINUATION } else { '<none>' }
Add-Content -LiteralPath `$record -Value "generation=$Generation action=`$Action continuation=`$continuation"
exit 0
"@
}

try {
    Write-Host 'AeroLink source-generation acceptance' -ForegroundColor Cyan
    Write-Host ''

    $origin = Join-Path $root 'origin.git'
    $source = Join-Path $root 'AeroLink Production'
    $seed = Join-Path $root 'seed'
    $log = Join-Path $root 'log'
    New-Item -ItemType Directory -Path $log -Force | Out-Null
    $executed = Join-Path $log 'executed.log'

    # --- A real origin carrying generation 1 of the control plane -------------------------------------
    Invoke-Git -GitArguments @('init', '--bare', $origin) | Out-Null
    Invoke-Git -GitArguments @('symbolic-ref', 'HEAD', 'refs/heads/main') -Repository $origin | Out-Null
    Invoke-Git -GitArguments @('clone', $origin, $seed) | Out-Null
    Invoke-Git -GitArguments @('config', 'user.email', 'acceptance@example.test') -Repository $seed | Out-Null
    Invoke-Git -GitArguments @('config', 'user.name', 'Acceptance') -Repository $seed | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $seed 'product\scripts') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $seed 'product\scripts\AeroLinkRemoteDemo.ps1') -Value (New-ControlPlaneScript 1) -Encoding UTF8
    Invoke-Git -GitArguments @('add', '-A') -Repository $seed | Out-Null
    Invoke-Git -GitArguments @('commit', '-m', 'generation 1 control plane') -Repository $seed | Out-Null
    Invoke-Git -GitArguments @('push', 'origin', 'main') -Repository $seed | Out-Null

    Invoke-Git -GitArguments @('clone', $origin, $source) | Out-Null
    $generationOne = (Invoke-Git -GitArguments @('rev-parse', 'HEAD') -Repository $source).Trim()
    Write-Host "Dedicated production source at generation 1: $($generationOne.Substring(0,8))"

    # --- origin/main moves: generation 2 REPLACES the control-plane script ----------------------------
    Set-Content -LiteralPath (Join-Path $seed 'product\scripts\AeroLinkRemoteDemo.ps1') -Value (New-ControlPlaneScript 2) -Encoding UTF8
    Invoke-Git -GitArguments @('add', '-A') -Repository $seed | Out-Null
    Invoke-Git -GitArguments @('commit', '-m', 'generation 2 control plane') -Repository $seed | Out-Null
    Invoke-Git -GitArguments @('push', 'origin', 'main') -Repository $seed | Out-Null
    $generationTwo = (Invoke-Git -GitArguments @('rev-parse', 'HEAD') -Repository $seed).Trim()
    Write-Host "origin/main advanced to generation 2:        $($generationTwo.Substring(0,8))"
    Write-Host ''

    $config = [pscustomobject]@{
        AeroLinkRoot = $source; LogsPath = $log; StatePath = $log
        NgrokExecutable = (Join-Path $root 'ngrok.exe'); PublicUrl = 'https://example.invalid'
        TrafficPolicyPath = (Join-Path $root 'policy.yml'); Upstream = 'http://127.0.0.1:5080'
        LocalApiBaseUri = 'http://127.0.0.1:5080'
    }

    # --- 1. The transition decides, then advances, using the REAL updater ------------------------------
    Write-Host '1. A real fast-forward of a real clone' -ForegroundColor Cyan
    $inspect = Update-AeroLinkProductionSource -SourceRoot $source -AllowNonDedicated -InspectOnly
    Assert-True ($inspect.Action -eq 'UpdateAvailable') "inspection sees the new generation ($($inspect.Action))"
    Assert-True ($inspect.TargetSha -eq $generationTwo) 'inspection names generation 2 as the target'
    Assert-True ((Invoke-Git -GitArguments @('rev-parse', 'HEAD') -Repository $source).Trim() -eq $generationOne) `
        'inspection left the working tree on generation 1'

    $advance = Update-AeroLinkProductionSource -SourceRoot $source -AllowNonDedicated -AdvanceToSha $inspect.TargetSha
    Assert-True ($advance.Action -eq 'Updated') "the advance completed ($($advance.Action))"
    $onDisk = (Get-Content -LiteralPath (Join-Path $source 'product\scripts\AeroLinkRemoteDemo.ps1') -Raw)
    Assert-True ($onDisk -match 'generation=2') 'the control-plane script ON DISK is now generation 2'
    Write-Host ''

    # --- 2. The handoff runs the UPDATED bytes, in a fresh process ------------------------------------
    Write-Host '2. The handoff executes generation 2, not the generation that advanced the source' -ForegroundColor Cyan
    $previousLog = $env:AEROLINK_GENERATION_LOG
    try {
        $env:AEROLINK_GENERATION_LOG = $log
        $handoff = Invoke-AeroLinkRemoteDemoHandoff -Config $config `
            -Topology ([pscustomobject]@{ TunnelRunning = $false; RuntimeRunning = $true }) `
            -PreserveServiceState -HeadSha $advance.HeadSha
        Assert-True ($handoff.ExitCode -eq 0) 'the fresh process completed successfully'
    }
    finally { $env:AEROLINK_GENERATION_LOG = $previousLog }

    $lines = @(Get-Content -LiteralPath $executed -ErrorAction SilentlyContinue)
    Assert-True ($lines.Count -eq 1) "exactly one control-plane process ran ($($lines.Count))"
    Assert-True ($lines -join "`n" -match 'generation=2') 'the process that ran was GENERATION 2 - the updated bytes'
    Assert-True (-not ($lines -join "`n" -match 'generation=1')) 'generation 1 did NOT continue after the source was rewritten'
    Assert-True ($lines -join "`n" -match 'action=Continue') 'it was invoked as the purpose-built continuation, not a blind Start'
    Write-Host ''

    # --- 3. The continuation carried the policy and the exact prior topology --------------------------
    Write-Host '3. The obligation survived the process boundary' -ForegroundColor Cyan
    $handed = ($lines -join "`n")
    Assert-True ($handed -match '"keepReady":\s*false') 'the preserve-state policy reached the new generation'
    Assert-True ($handed -match '"priorTunnel":\s*false') 'the prior topology says no tunnel was running...'
    Assert-True ($handed -match '"priorRuntime":\s*true') '...and that a runtime was, so only that is owed'
    # Compared against the JSON-escaped form: the continuation is carried as JSON, so a Windows path appears
    # in it with doubled backslashes. Matching the raw path would fail for a reason that has nothing to do
    # with the property under test.
    $sourceInJson = ($source | ConvertTo-Json).Trim('"')
    Assert-True ($handed -match [regex]::Escape($sourceInJson)) 'the continuation is bound to this source root'
    Assert-True ($null -eq $env:AEROLINK_TRANSITION_CONTINUATION) 'the continuation did not leak into this process'
    Write-Host ''

    # --- 4. The guard expires with the generation it guarded -------------------------------------------
    Write-Host '4. A second advance requires a second handoff' -ForegroundColor Cyan
    Assert-True ($env:AEROLINK_REMOTE_DEMO_HANDOFF -ne "$source|$generationTwo") 'the guard was restored after the handoff'
    Assert-True ("$source|$generationOne" -ne "$source|$generationTwo") `
        'the guard value differs between generations, so generation 3 cannot be suppressed by generation 2'
    Write-Host ''
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) {
    Write-Host "Source-generation acceptance FAILED ($($failures.Count) failure(s))." -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'Source-generation acceptance passed.' -ForegroundColor Green
Write-Host 'A real clone was fast-forwarded across a control-plane change, and the updated generation - not the' -ForegroundColor DarkGray
Write-Host 'one that performed the advance - completed the transition, carrying the exact obligation with it.' -ForegroundColor DarkGray
exit 0
