[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Run', 'Evaluate')]
    [string]$Mode = 'Plan',
    [string]$BaselinePath,
    [string]$TreatmentPath,
    [string]$BaselineSummaryPath,
    [string]$TreatmentSummaryPath,
    [string]$OutputRoot,
    [int]$Runs = 10,
    [int]$ShardCount = 3,
    [int]$TimeoutMinutes = 30,
    [int]$ProcessTimeoutMinutes = 60,
    [string]$Seeds,
    [string]$ProjectPath = 'product/tests/AeroLink.Api.Tests/AeroLink.Api.Tests.csproj',
    [string]$SolutionPath = 'product/AeroLink.slnx',
    [string]$TestListPath,
    [switch]$Warmup,
    [switch]$SkipBuild,
    [string]$DotnetExecutable = 'dotnet',
    [string]$NodeExecutable = 'node'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Fail([string]$Message) {
    throw "[api-host-reuse] $Message"
}

function Write-JsonFile([string]$Path, [object]$Value) {
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-LiveProcessIds([int[]]$Ids) {
    @($Ids | Where-Object {
        try { Get-Process -Id $_ -ErrorAction Stop | Out-Null; $true } catch { $false }
    })
}

function Stop-ProcessSafely {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds = 5000
    )

    $ownedIds = @()
    try { $ownedIds = @(Get-ProcessTreeIds @($Process.Id)) } catch { $ownedIds = @($Process.Id) }
    $killError = $null
    try {
        if (-not $Process.HasExited) {
            try { $Process.Kill($true) }
            catch {
                foreach ($id in @($ownedIds | Sort-Object -Descending)) {
                    Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
                }
            }
        }
    } catch { $killError = $_.Exception.Message }
    $exited = $false
    try { $exited = $Process.WaitForExit($TimeoutMilliseconds) } catch { $killError = $_.Exception.Message }
    $remaining = @(Get-LiveProcessIds $ownedIds)
    [pscustomobject]@{
        ownedIds = $ownedIds
        exited = ($exited -and $remaining.Count -eq 0)
        remainingIds = $remaining
        error = $killError
    }
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [hashtable]$Environment,
        [int]$TimeoutSeconds = ($ProcessTimeoutMinutes * 60)
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($null -eq $startInfo.ArgumentList) { Fail 'PowerShell 7 or later is required for safe argument passing.' }
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    if ($Environment) {
        foreach ($entry in $Environment.GetEnumerator()) { $startInfo.Environment[$entry.Key] = [string]$entry.Value }
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $startedAt = [DateTimeOffset]::UtcNow
    if (-not $process.Start()) { Fail "Could not start $FileName." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    $cleanup = $null
    if ($timedOut) { $cleanup = Stop-ProcessSafely -Process $process }
    else { $process.WaitForExit() }
    $stdout = if ($stdoutTask.Wait(5000)) { $stdoutTask.GetAwaiter().GetResult() } else { '' }
    $stderr = if ($stderrTask.Wait(5000)) { $stderrTask.GetAwaiter().GetResult() } else { '' }
    [pscustomobject]@{
        ExitCode = if ($timedOut) { 124 } else { $process.ExitCode }
        Stdout = $stdout
        Stderr = $stderr
        TimedOut = $timedOut
        Cleanup = $cleanup
        StartedAt = $startedAt
        EndedAt = [DateTimeOffset]::UtcNow
    }
}

function Get-RepoInfo([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { Fail "Worktree does not exist: $Path" }
    $head = (& git -C $Path rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "Could not read the Git head for ${Path}: $head" }
    $status = @(& git -C $Path status --porcelain 2>&1)
    if ($LASTEXITCODE -ne 0) { Fail "Could not read Git status for $Path." }
    $branch = (& git -C $Path branch --show-current 2>&1 | Out-String).Trim()
    [pscustomobject]@{
        path = (Resolve-Path -LiteralPath $Path).Path
        head = $head
        branch = $branch
        clean = ($status.Count -eq 0)
        status = @($status)
    }
}

function Get-EnvironmentInfo([string]$Path) {
    $dotnetResult = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('--version') -WorkingDirectory $Path
    if ($dotnetResult.ExitCode -ne 0) { Fail "Could not read the .NET SDK version in $Path.`n$($dotnetResult.Stderr)" }
    $nodeResult = Invoke-CapturedProcess -FileName $NodeExecutable -Arguments @('--version') -WorkingDirectory $Path
    if ($nodeResult.ExitCode -ne 0) { Fail "Could not read the Node.js version in $Path.`n$($nodeResult.Stderr)" }
    $dotnet = $dotnetResult.Stdout.Trim()
    $node = $nodeResult.Stdout.Trim()
    $os = try { Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber } catch { $null }
    $cpu = try { @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors) } catch { $null }
    [ordered]@{
        os = $os
        cpu = $cpu
        powershell = $PSVersionTable.PSVersion.ToString()
        dotnet = $dotnet
        node = $node
        processorCount = [Environment]::ProcessorCount
        machine = [Environment]::MachineName
        user = [Environment]::UserName
        worktree = (Resolve-Path -LiteralPath $Path).Path
    }
}

function Get-TestManifest([string]$Worktree, [string]$ListFile) {
    $raw = if ($ListFile) {
        if (-not (Test-Path -LiteralPath $ListFile -PathType Leaf)) { Fail "Test-list file does not exist: $ListFile" }
        @(Get-Content -LiteralPath $ListFile)
    } else {
        $project = Join-Path $Worktree $ProjectPath
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { Fail "API test project does not exist: $project" }
        $result = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('test', $project, '--configuration', 'Release', '--no-build', '--list-tests') -WorkingDirectory $Worktree
        if ($result.ExitCode -ne 0) { Fail "Test discovery failed in $Worktree.`n$($result.Stdout)`n$($result.Stderr)" }
        @($result.Stdout -split "`r?`n")
    }

    $cases = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $raw) {
        $name = ([string]$line).Trim()
        if (-not $name.StartsWith('AeroLink.Api.Tests.', [StringComparison]::Ordinal)) { continue }
        $baseName = $name.Split('(')[0]
        $lastDot = $baseName.LastIndexOf('.')
        if ($lastDot -lt 1 -or $lastDot -ge $baseName.Length - 1) { continue }
        $className = $baseName.Substring(0, $lastDot)
        $methodName = $baseName.Substring($lastDot + 1)
        if ($className -eq 'AeroLink.Api.Tests') { continue }
        $cases.Add([pscustomobject]@{ name = $name; className = $className; methodName = $methodName })
    }
    if ($cases.Count -eq 0) { Fail 'No AeroLink.Api.Tests cases were discovered.' }

    $classes = @($cases | Group-Object className | ForEach-Object {
        [pscustomobject]@{
            name = $_.Name
            cases = $_.Count
        }
    } | Sort-Object name)
    [pscustomobject]@{
        cases = @($cases)
        classes = $classes
        caseCount = $cases.Count
        classCount = $classes.Count
    }
}

function Shuffle-Items([object[]]$Items, [System.Random]$Random) {
    $copy = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $Items) { $copy.Add($item) }
    for ($index = $copy.Count - 1; $index -gt 0; $index--) {
        $swap = $Random.Next($index + 1)
        $value = $copy[$index]
        $copy[$index] = $copy[$swap]
        $copy[$swap] = $value
    }
    @($copy)
}

function New-Partition([object[]]$Classes, [int]$Seed, [int]$Count) {
    if ($Count -gt $Classes.Count) { Fail "ShardCount ($Count) cannot exceed discovered class count ($($Classes.Count)); an empty filter would run the full suite." }
    $random = [System.Random]::new($Seed)
    $shuffled = Shuffle-Items $Classes $random
    $loads = [int[]](0..($Count - 1) | ForEach-Object { 0 })
    $shards = [object[]]::new($Count)
    for ($index = 0; $index -lt $Count; $index++) { $shards[$index] = [System.Collections.Generic.List[object]]::new() }
    foreach ($class in $shuffled) {
        $lowest = ($loads | Measure-Object -Minimum).Minimum
        $candidates = @(0..($Count - 1) | Where-Object { $loads[$_] -eq $lowest })
        $shard = $candidates[$random.Next($candidates.Count)]
        $shards[$shard].Add($class)
        $loads[$shard] += [int]$class.cases
    }
    $entries = @(0..($Count - 1) | ForEach-Object {
        $ordered = @($shards[$_] | Sort-Object name)
        [pscustomobject]@{
            shard = $_ + 1
            expectedCases = $loads[$_]
            classes = @($ordered | ForEach-Object { $_.name })
            filters = (@($ordered | ForEach-Object { "FullyQualifiedName~$($_.name)." }) -join '|')
        }
    })
    [pscustomobject]@{
        algorithm = 'fisher-yates-then-lightest-shard'
        seed = $Seed
        shardCount = $Count
        totalCases = ($loads | Measure-Object -Sum).Sum
        shards = $entries
    }
}

function Assert-SameManifest([object]$Expected, [object]$Actual) {
    $expectedNames = @($Expected.classes | ForEach-Object name | Sort-Object)
    $actualNames = @($Actual.classes | ForEach-Object name | Sort-Object)
    if (Compare-Object $expectedNames $actualNames) { Fail 'Baseline and treatment discovered different API class sets.' }
    $expectedCases = @($Expected.cases | ForEach-Object name | Sort-Object)
    $actualCases = @($Actual.cases | ForEach-Object name | Sort-Object)
    if (Compare-Object $expectedCases $actualCases) { Fail 'Baseline and treatment discovered different API test-case names.' }
    if ($Expected.caseCount -ne $Actual.caseCount) { Fail "Baseline/treatment case counts differ: $($Expected.caseCount) vs $($Actual.caseCount)." }
}

function Get-ProcessTreeIds([int[]]$RootIds) {
    $all = try { @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId) } catch { @() }
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($root in $RootIds) { [void]$ids.Add([int]$root) }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $all) {
            if ($ids.Contains([int]$process.ParentProcessId) -and $ids.Add([int]$process.ProcessId)) { $changed = $true }
        }
    }
    @($ids)
}

function Get-IoRate([System.Collections.Generic.HashSet[int]]$Ids) {
    try {
        $idSamples = @(Get-Counter -Counter '\Process(*)\ID Process' -ErrorAction Stop).CounterSamples
        $pidByInstance = @{}
        foreach ($sample in $idSamples) { $pidByInstance[$sample.InstanceName] = [int]$sample.CookedValue }
        $ioSamples = @(Get-Counter -Counter '\Process(*)\IO Read Bytes/sec', '\Process(*)\IO Write Bytes/sec' -ErrorAction Stop).CounterSamples
        $read = 0.0
        $write = 0.0
        foreach ($sample in $ioSamples) {
            $pid = $pidByInstance[$sample.InstanceName]
            if ($null -eq $pid -or -not $Ids.Contains([int]$pid)) { continue }
            if ($sample.Path -match 'IO Read Bytes/sec$') { $read += [double]$sample.CookedValue }
            if ($sample.Path -match 'IO Write Bytes/sec$') { $write += [double]$sample.CookedValue }
        }
        [pscustomobject]@{ available = $true; readPerSecond = $read; writePerSecond = $write; error = $null }
    } catch {
        [pscustomobject]@{ available = $false; readPerSecond = $null; writePerSecond = $null; error = $_.Exception.Message }
    }
}

function Get-ProcessSample([int[]]$RootIds) {
    $ids = Get-ProcessTreeIds $RootIds
    $cpu = 0.0
    $cpuSamples = 0
    foreach ($id in $ids) {
        try { $cpu += (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime.TotalMilliseconds; $cpuSamples++ } catch { }
    }
    $rootAvailable = $false
    foreach ($root in $RootIds) {
        try { Get-Process -Id $root -ErrorAction Stop | Out-Null; $rootAvailable = $true; break } catch { }
    }
    $io = Get-IoRate ([System.Collections.Generic.HashSet[int]]::new([int[]]$ids))
    [pscustomobject]@{ ids = $ids; rootAvailable = $rootAvailable; cpuMs = $cpu; cpuAvailable = ($rootAvailable -and $cpuSamples -gt 0); io = $io; at = [DateTimeOffset]::UtcNow }
}

function New-TestProcess {
    param(
        [string]$Worktree,
        [string]$Filter,
        [string]$TelemetryPath,
        [string]$ResultsPath,
        [string]$StdoutPath,
        [string]$StderrPath
    )
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TelemetryPath), $ResultsPath, (Split-Path -Parent $StdoutPath) | Out-Null
    $project = Join-Path $Worktree $ProjectPath
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $DotnetExecutable
    $startInfo.WorkingDirectory = $Worktree
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($null -eq $startInfo.ArgumentList) { Fail 'PowerShell 7 or later is required for the Windows process harness.' }
    $arguments = @('test', $project, '--configuration', 'Release', '--no-build', '--filter', $Filter,
        '--logger', 'console;verbosity=normal', '--logger', 'trx;LogFileName=shard.trx',
        '--results-directory', $ResultsPath)
    foreach ($argument in $arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    $startInfo.Environment['AEROLINK_API_TELEMETRY_JSONL'] = $TelemetryPath
    $startInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $startedAt = [DateTimeOffset]::UtcNow
    try {
        if (-not $process.Start()) { Fail "Could not start the API test shard for $Worktree." }
        [pscustomobject]@{
            process = $process
            shardStartedAt = $startedAt
            stdoutTask = $process.StandardOutput.ReadToEndAsync()
            stderrTask = $process.StandardError.ReadToEndAsync()
            telemetryPath = $TelemetryPath
            resultsPath = $ResultsPath
            stdoutPath = $StdoutPath
            stderrPath = $StderrPath
            maxCpuMs = 0.0
            diskReadBytes = 0.0
            diskWriteBytes = 0.0
            ioAvailable = $true
            ioError = $null
            cpuAvailable = $true
            cpuError = $null
            timedOut = $false
            cleanupFailure = $null
            waitError = $null
            ownedProcessIds = @($process.Id)
            lastSampleAt = $startedAt
            samples = 0
            successfulSamples = 0
        }
    } catch {
        try { if (-not $process.HasExited) { [void](Stop-ProcessSafely -Process $process) } } catch { }
        throw
    }
}

function Wait-TestProcesses([object[]]$Shards) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
    try {
        while ($true) {
            $active = @($Shards | Where-Object { -not $_.process.HasExited })
            foreach ($shard in $active) {
                try {
                    $sample = Get-ProcessSample @($shard.process.Id)
                    if (-not $sample.rootAvailable) { continue }
                    $shard.ownedProcessIds = @($shard.ownedProcessIds + $sample.ids | Sort-Object -Unique)
                    $shard.samples++
                    if ($sample.cpuAvailable) { $shard.successfulSamples++ }
                    if ($sample.cpuMs -gt $shard.maxCpuMs) { $shard.maxCpuMs = $sample.cpuMs }
                    $elapsedSeconds = ($sample.at - $shard.lastSampleAt).TotalSeconds
                    if ($sample.io.available) {
                        $shard.diskReadBytes += $sample.io.readPerSecond * [math]::Max(0, $elapsedSeconds)
                        $shard.diskWriteBytes += $sample.io.writePerSecond * [math]::Max(0, $elapsedSeconds)
                    } else {
                        $shard.ioAvailable = $false
                        $shard.ioError = $sample.io.error
                    }
                    if (-not $sample.cpuAvailable) {
                        $shard.cpuAvailable = $false
                        $shard.cpuError = 'No active process-tree CPU sample was available.'
                    }
                    $shard.lastSampleAt = $sample.at
                } catch {
                    $shard.waitError = $_.Exception.Message
                    $shard.cpuAvailable = $false
                    $shard.ioAvailable = $false
                }
            }
            if ($active.Count -eq 0) { break }
            if ([DateTimeOffset]::UtcNow -gt $deadline) {
                foreach ($shard in $active) {
                    $cleanup = Stop-ProcessSafely -Process $shard.process
                    $shard.timedOut = $true
                    if (-not $cleanup.exited) { $shard.cleanupFailure = "Owned process tree did not exit: $($cleanup.remainingIds -join ',')" }
                    if ($cleanup.error) { $shard.cleanupFailure = $cleanup.error }
                }
                break
            }
            Start-Sleep -Milliseconds 500
        }
    } catch {
        foreach ($shard in $Shards) { $shard.waitError = $_.Exception.Message }
    } finally {
        foreach ($shard in $Shards) {
            try {
                if (-not $shard.process.HasExited) {
                    $cleanup = Stop-ProcessSafely -Process $shard.process
                    if (-not $cleanup.exited) { $shard.cleanupFailure = "Owned process tree did not exit: $($cleanup.remainingIds -join ',')" }
                    if ($cleanup.error) { $shard.cleanupFailure = $cleanup.error }
                }
                $remainingOwned = @(Get-LiveProcessIds $shard.ownedProcessIds)
                if ($remainingOwned.Count -gt 0) {
                    foreach ($id in $remainingOwned) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
                    $stopDeadline = [DateTimeOffset]::UtcNow.AddMilliseconds(5000)
                    do {
                        Start-Sleep -Milliseconds 100
                        $remainingOwned = @(Get-LiveProcessIds $shard.ownedProcessIds)
                    } while ($remainingOwned.Count -gt 0 -and [DateTimeOffset]::UtcNow -lt $stopDeadline)
                    if ($remainingOwned.Count -gt 0) { $shard.cleanupFailure = "Owned descendants remained: $($remainingOwned -join ',')" }
                }
                if (-not $shard.process.WaitForExit(5000)) { $shard.cleanupFailure = 'Process did not exit within the cleanup wait.' }
            } catch { $shard.cleanupFailure = $_.Exception.Message }
            try { $shard.stdout = if ($shard.stdoutTask.Wait(5000)) { $shard.stdoutTask.GetAwaiter().GetResult() } else { '' } } catch { $shard.stdout = '' }
            try { $shard.stderr = if ($shard.stderrTask.Wait(5000)) { $shard.stderrTask.GetAwaiter().GetResult() } else { '' } } catch { $shard.stderr = '' }
            $shard.endedAt = [DateTimeOffset]::UtcNow
            Set-Content -LiteralPath $shard.stdoutPath -Value $shard.stdout -Encoding utf8
            Set-Content -LiteralPath $shard.stderrPath -Value $shard.stderr -Encoding utf8
            try { $shard.exitCode = $shard.process.ExitCode } catch { $shard.exitCode = $null }
            $shard.wallMs = ($shard.endedAt - $shard.shardStartedAt).TotalMilliseconds
        }
    }
}

function Get-TrxCounts([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $xml = [xml](Get-Content -Raw -LiteralPath $Path)
    $nodes = @($xml.SelectNodes("//*[local-name()='UnitTestResult']"))
    $counts = @{ total = $nodes.Count; passed = 0; failed = 0; skipped = 0; other = 0 }
    foreach ($node in $nodes) {
        switch ([string]$node.outcome) {
            'Passed' { $counts.passed++ }
            'Failed' { $counts.failed++ }
            'Skipped' { $counts.skipped++ }
            default { $counts.other++ }
        }
    }
    [pscustomobject]$counts
}

function Invoke-TelemetryAggregator([string]$Worktree, [string]$TelemetryPath, [string]$TrxPath, [string]$OutputPath) {
    if (-not (Test-Path -LiteralPath $TelemetryPath -PathType Leaf) -or -not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        return [pscustomobject]@{ report = $null; malformed = $null; truncated = $null; output = 'telemetry or TRX missing'; exitCode = 2; timedOut = $false; cleanup = $null }
    }
    $aggregator = Join-Path $Worktree 'product/ci-metrics/bin/aggregate-api-telemetry.mjs'
    try { $result = Invoke-CapturedProcess -FileName $NodeExecutable -Arguments @($aggregator, $TelemetryPath, $TrxPath, $OutputPath) -WorkingDirectory $Worktree }
    catch { return [pscustomobject]@{ report = $null; malformed = $null; truncated = $null; output = $_.Exception.Message; exitCode = 1; timedOut = $false; cleanup = $null } }
    $combined = "$($result.Stdout)`n$($result.Stderr)"
    $malformed = $null
    $truncated = $null
    if ($combined -match '\((\d+) malformed lines, truncated=(true|false)\)') {
        $malformed = [int]$Matches[1]
        $truncated = [bool]::Parse($Matches[2])
    }
    $reportPath = Join-Path $OutputPath 'api-telemetry.json'
    $report = $null
    try { if (Test-Path -LiteralPath $reportPath -PathType Leaf) { $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json } } catch { $combined = "$combined`n$($_.Exception.Message)" }
    [pscustomobject]@{ report = $report; malformed = $malformed; truncated = $truncated; output = $combined; exitCode = $result.ExitCode; timedOut = $result.TimedOut; cleanup = $result.Cleanup }
}

function Invoke-Observation {
    param(
        [string]$Condition,
        [string]$Worktree,
        [object]$Partition,
        [int]$RunNumber,
        [int]$Seed,
        [string]$Root,
        [string[]]$Order,
        [object]$Environment,
        [switch]$IsWarmup
    )
    $runDirectory = Join-Path $Root (if ($IsWarmup) { "warmup-$Condition-seed-$Seed" } else { "$Condition\run-$('{0:D2}' -f $RunNumber)-seed-$Seed" })
    if (Test-Path -LiteralPath $runDirectory) {
        $existing = @(Get-ChildItem -LiteralPath $runDirectory -Force -ErrorAction Stop)
        if ($existing.Count -gt 0) { Fail "Refusing to reuse non-empty observation directory: $runDirectory" }
    }
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
    $shards = @()
    $launchError = $null
    try {
        foreach ($entry in $Partition.shards) {
            $shardDirectory = Join-Path $runDirectory "shard-$($entry.shard)"
            $telemetry = Join-Path $shardDirectory 'api-telemetry.jsonl'
            $results = Join-Path $shardDirectory 'TestResults'
            $stdout = Join-Path $shardDirectory 'stdout.log'
            $stderr = Join-Path $shardDirectory 'stderr.log'
            $shards += New-TestProcess -Worktree $Worktree -Filter $entry.filters -TelemetryPath $telemetry -ResultsPath $results -StdoutPath $stdout -StderrPath $stderr
            $shards[-1] | Add-Member -NotePropertyName expectedCases -NotePropertyValue $entry.expectedCases
            $shards[-1] | Add-Member -NotePropertyName shard -NotePropertyValue $entry.shard
            $shards[-1] | Add-Member -NotePropertyName classNames -NotePropertyValue @($entry.classes)
        }
        Wait-TestProcesses $shards
    } catch {
        $launchError = $_.Exception.Message
        foreach ($shard in $shards) {
            try {
                if (-not $shard.process.HasExited) {
                    $cleanup = Stop-ProcessSafely -Process $shard.process
                    if (-not $cleanup.exited) { $shard.cleanupFailure = "Owned process tree did not exit: $($cleanup.remainingIds -join ',')" }
                }
            } catch { $shard.cleanupFailure = $_.Exception.Message }
        }
    }

    $invalidReasons = [System.Collections.Generic.List[string]]::new()
    if ($launchError) { $invalidReasons.Add("process launch/wait failed: $launchError") }
    $errorPattern = '(?i)(SQLITE_BUSY|SQLITE_LOCKED|database is locked|MSB3027|UnauthorizedAccessException|file.*locked|testhost.*crash)'
    $shardReports = foreach ($shard in $shards) {
        $trx = Join-Path $shard.resultsPath 'shard.trx'
        $trxError = $null
        try { $counts = Get-TrxCounts $trx } catch { $counts = $null; $trxError = $_.Exception.Message }
        $telemetryHasRecords = (Test-Path -LiteralPath $shard.telemetryPath -PathType Leaf) -and
            (@(Get-Content -LiteralPath $shard.telemetryPath -ErrorAction SilentlyContinue | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0)
        $aggregateDirectory = Join-Path $shard.resultsPath 'api-telemetry'
        $aggregateError = $null
        try { $aggregate = Invoke-TelemetryAggregator -Worktree $Worktree -TelemetryPath $shard.telemetryPath -TrxPath $trx -OutputPath $aggregateDirectory } catch { $aggregate = [pscustomobject]@{ report = $null; malformed = $null; truncated = $null; output = ''; exitCode = 1; timedOut = $false; cleanup = $null }; $aggregateError = $_.Exception.Message }
        $signals = @(("$($shard.stdout)`n$($shard.stderr)") | Select-String -Pattern $errorPattern -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique)
        if ($shard.exitCode -ne 0) { $invalidReasons.Add("shard $($shard.shard) exit code $($shard.exitCode)") }
        if ($shard.timedOut) { $invalidReasons.Add("shard $($shard.shard) exceeded the $TimeoutMinutes minute timeout") }
        if ($shard.waitError) { $invalidReasons.Add("shard $($shard.shard) wait error: $($shard.waitError)") }
        if ($shard.cleanupFailure) { $invalidReasons.Add("shard $($shard.shard) cleanup failure: $($shard.cleanupFailure)") }
        if ($shard.successfulSamples -eq 0) { $invalidReasons.Add("shard $($shard.shard) had no successful active process sample") }
        if ($null -eq $counts) { $invalidReasons.Add("shard $($shard.shard) TRX missing") }
        if ($trxError) { $invalidReasons.Add("shard $($shard.shard) TRX parse failed: $trxError") }
        elseif ($counts.total -ne $shard.expectedCases) { $invalidReasons.Add("shard $($shard.shard) expected $($shard.expectedCases) cases but TRX has $($counts.total)") }
        if ($counts -and ($counts.failed -gt 0 -or $counts.skipped -gt 0 -or $counts.other -gt 0)) { $invalidReasons.Add("shard $($shard.shard) has failed/skipped/other outcomes") }
        if (-not $telemetryHasRecords) { $invalidReasons.Add("shard $($shard.shard) telemetry JSONL was missing or empty") }
        if ($null -eq $aggregate.report) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregation missing") }
        elseif ($aggregate.report.totals.trxTests -ne $shard.expectedCases -or $aggregate.report.totals.tests -ne $shard.expectedCases) { $invalidReasons.Add("shard $($shard.shard) telemetry/TRX count mismatch") }
        if ($aggregate.report -and $aggregate.report.totals.factories -le 0) { $invalidReasons.Add("shard $($shard.shard) telemetry reported zero factories") }
        if ($aggregate.exitCode -ne 0) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregator exit code $($aggregate.exitCode)") }
        if ($aggregateError) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregation failed: $aggregateError") }
        if ($aggregate.timedOut) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregator timed out") }
        if ($aggregate.cleanup -and -not $aggregate.cleanup.exited) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregator cleanup failure") }
        if ($aggregate.malformed -ne 0 -or $aggregate.truncated -eq $true) { $invalidReasons.Add("shard $($shard.shard) telemetry malformed or truncated") }
        if ($signals.Count -gt 0) { $invalidReasons.Add("shard $($shard.shard) lock/cleanup signal: $($signals -join ', ')") }
        [pscustomobject]@{
            shard = $shard.shard
            classes = $shard.classNames
            expectedCases = $shard.expectedCases
            wallMs = [math]::Round($shard.wallMs, 3)
            exitCode = $shard.exitCode
            counts = $counts
            telemetry = if ($aggregate.report) { $aggregate.report.totals } else { $null }
            telemetryPath = $shard.telemetryPath
            trxPath = $trx
            stdoutPath = $shard.stdoutPath
            stderrPath = $shard.stderrPath
            malformedTelemetry = $aggregate.malformed
            telemetryTruncated = $aggregate.truncated
            telemetryHasRecords = $telemetryHasRecords
            cpuMs = [math]::Round($shard.maxCpuMs, 3)
            cpuAvailable = $shard.cpuAvailable
            cpuError = $shard.cpuError
            diskReadBytes = [math]::Round($shard.diskReadBytes, 3)
            diskWriteBytes = [math]::Round($shard.diskWriteBytes, 3)
            ioAvailable = $shard.ioAvailable
            ioError = $shard.ioError
            samples = $shard.samples
            successfulSamples = $shard.successfulSamples
            ownedProcessIds = $shard.ownedProcessIds
            cleanupFailure = $shard.cleanupFailure
            errorSignals = $signals
        }
    }
    if ($shards.Count -ne @($Partition.shards).Count) { $invalidReasons.Add("expected $(@($Partition.shards).Count) shards but launched $($shards.Count)") }
    if (@($shardReports | Where-Object { -not $_.ioAvailable }).Count -gt 0) { $invalidReasons.Add('disk performance counters were unavailable') }
    if (@($shardReports | Where-Object { -not $_.cpuAvailable }).Count -gt 0) { $invalidReasons.Add('process-tree CPU samples were unavailable') }
    $metrics = [ordered]@{
        worstShardWallMs = [math]::Round((@($shardReports | ForEach-Object wallMs | Measure-Object -Maximum).Maximum), 3)
        summedShardWallMs = [math]::Round((@($shardReports | ForEach-Object wallMs | Measure-Object -Sum).Sum), 3)
        cpuMs = [math]::Round((@($shardReports | ForEach-Object cpuMs | Measure-Object -Sum).Sum), 3)
        diskReadBytes = [math]::Round((@($shardReports | ForEach-Object diskReadBytes | Measure-Object -Sum).Sum), 3)
        diskWriteBytes = [math]::Round((@($shardReports | ForEach-Object diskWriteBytes | Measure-Object -Sum).Sum), 3)
        factories = [int](@($shardReports | ForEach-Object { $_.telemetry.factories } | Measure-Object -Sum).Sum)
        startupMs = [math]::Round((@($shardReports | ForEach-Object { $_.telemetry.summedFactoryStartupMs } | Measure-Object -Sum).Sum), 3)
        testCount = [int](@($shardReports | ForEach-Object { $_.counts.total } | Measure-Object -Sum).Sum)
    }
    $startedValues = @($shards | ForEach-Object shardStartedAt | Sort-Object)
    $endedValues = @($shards | ForEach-Object endedAt | Sort-Object -Descending)
    $observation = [ordered]@{
        schemaVersion = 'aerolink-api-host-reuse-measurement/v1'
        condition = $Condition
        run = $RunNumber
        seed = $Seed
        order = @($Order)
        warmup = [bool]$IsWarmup
        worktree = (Get-RepoInfo $Worktree)
        environment = $Environment
        partition = $Partition
        startedAt = if ($startedValues.Count -gt 0) { $startedValues[0].ToString('o') } else { $null }
        endedAt = if ($endedValues.Count -gt 0) { $endedValues[0].ToString('o') } else { $null }
        valid = ($invalidReasons.Count -eq 0)
        invalidReasons = @($invalidReasons)
        metricsComplete = (@($shardReports | Where-Object { -not $_.ioAvailable -or -not $_.cpuAvailable -or $_.successfulSamples -le 0 }).Count -eq 0)
        metrics = $metrics
        shards = @($shardReports)
    }
    Write-JsonFile (Join-Path $runDirectory 'observation.json') $observation
    [pscustomobject]$observation
}

function Get-Quantiles([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return [ordered]@{ p10 = $null; median = $null; p75 = $null; p95 = $null } }
    $at = {
        param([double]$Percentile)
        $position = ($sorted.Count - 1) * ($Percentile / 100.0)
        $lower = [math]::Floor($position)
        $upper = [math]::Ceiling($position)
        if ($lower -eq $upper) { return [double]$sorted[$lower] }
        [double]$sorted[$lower] + (($sorted[$upper] - $sorted[$lower]) * ($position - $lower))
    }
    [ordered]@{ p10 = & $at 10; median = & $at 50; p75 = & $at 75; p95 = & $at 95 }
}

function Get-ConditionSummary([string]$Condition, [object[]]$Observations, [int]$RequiredRuns) {
    $valid = @($Observations | Where-Object valid)
    $metrics = @('worstShardWallMs', 'summedShardWallMs', 'cpuMs', 'diskReadBytes', 'diskWriteBytes', 'factories', 'startupMs')
    $quantiles = [ordered]@{}
    foreach ($metric in $metrics) {
        $quantiles[$metric] = Get-Quantiles @($valid | ForEach-Object { [double]$_.metrics.$metric })
    }
    [ordered]@{
        schemaVersion = 'aerolink-api-host-reuse-measurement/v1'
        condition = $Condition
        requiredRuns = $RequiredRuns
        observationCount = @($Observations).Count
        validObservationCount = $valid.Count
        allValid = (@($Observations).Count -eq $RequiredRuns -and $valid.Count -eq $RequiredRuns)
        metricsComplete = (@($Observations | Where-Object { -not $_.metricsComplete }).Count -eq 0)
        quantiles = $quantiles
        observations = @($Observations)
    }
}

function Test-NonNegativeNumber([object]$Value) {
    $number = 0.0
    if ($null -eq $Value -or -not [double]::TryParse(([string]$Value), [ref]$number)) { return $false }
    -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and $number -ge 0
}

function Get-ValidatedSummary([object]$Summary, [string]$ExpectedCondition) {
    $errors = [System.Collections.Generic.List[string]]::new()
    $schema = [string]$Summary.schemaVersion
    if ($schema -ne 'aerolink-api-host-reuse-measurement/v1') { $errors.Add("$ExpectedCondition summary has unsupported schemaVersion '$schema'.") }
    if ([string]$Summary.condition -ne $ExpectedCondition) { $errors.Add("Summary condition is '$($Summary.condition)', expected '$ExpectedCondition'.") }
    $required = 0
    if (-not [int]::TryParse(([string]$Summary.requiredRuns), [ref]$required) -or $required -lt 1) { $errors.Add("$ExpectedCondition summary has an invalid requiredRuns value.") }
    $observations = @($Summary.observations)
    if ($observations.Count -ne $required) { $errors.Add("$ExpectedCondition summary has $($observations.Count) observations; expected $required.") }
    $seenSeeds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $normalized = [System.Collections.Generic.List[object]]::new()
    foreach ($observation in $observations) {
        $seedText = [string]$observation.seed
        foreach ($property in @('valid', 'metricsComplete', 'invalidReasons', 'metrics', 'shards')) {
            if (-not ($observation.PSObject.Properties.Name -contains $property)) { $errors.Add("Observation seed '$seedText' is missing '$property'.") }
        }
        if (-not $seenSeeds.Add($seedText)) { $errors.Add("$ExpectedCondition summary repeats seed '$seedText'.") }
        if ([string]$observation.condition -ne $ExpectedCondition) { $errors.Add("Observation seed '$seedText' has the wrong condition.") }
        $claimedValid = [bool]$observation.valid
        $claimedMetricsComplete = [bool]$observation.metricsComplete
        $evidenceValid = $true
        $evidenceMetricsComplete = $true
        $shards = @($observation.shards)
        $wallValues = [System.Collections.Generic.List[double]]::new()
        $cpuValues = [System.Collections.Generic.List[double]]::new()
        $readValues = [System.Collections.Generic.List[double]]::new()
        $writeValues = [System.Collections.Generic.List[double]]::new()
        $factoryValues = [System.Collections.Generic.List[double]]::new()
        $startupValues = [System.Collections.Generic.List[double]]::new()
        $testValues = [System.Collections.Generic.List[double]]::new()
        if ($shards.Count -eq 0) { $evidenceValid = $false; $evidenceMetricsComplete = $false }
        foreach ($shard in $shards) {
            if ([int]$shard.exitCode -ne 0 -or [int]$shard.expectedCases -le 0) { $evidenceValid = $false }
            if ($null -eq $shard.counts -or [int]$shard.counts.total -ne [int]$shard.expectedCases -or
                [int]$shard.counts.failed -ne 0 -or [int]$shard.counts.skipped -ne 0 -or [int]$shard.counts.other -ne 0) { $evidenceValid = $false }
            if (-not [bool]$shard.telemetryHasRecords -or [int]$shard.malformedTelemetry -ne 0 -or [bool]$shard.telemetryTruncated) { $evidenceValid = $false }
            if ($null -eq $shard.telemetry -or [int]$shard.telemetry.tests -ne [int]$shard.expectedCases -or [int]$shard.telemetry.factories -le 0) { $evidenceValid = $false }
            if (-not [bool]$shard.cpuAvailable -or -not [bool]$shard.ioAvailable -or [int]$shard.successfulSamples -le 0) { $evidenceMetricsComplete = $false }
            if ($shard.cleanupFailure -or $shard.waitError -or @($shard.errorSignals).Count -gt 0) { $evidenceValid = $false }
            foreach ($pair in @(
                @($wallValues, $shard.wallMs),
                @($cpuValues, $shard.cpuMs),
                @($readValues, $shard.diskReadBytes),
                @($writeValues, $shard.diskWriteBytes),
                @($factoryValues, $shard.telemetry.factories),
                @($startupValues, $shard.telemetry.summedFactoryStartupMs),
                @($testValues, $shard.counts.total))) {
                if (Test-NonNegativeNumber $pair[1]) { $pair[0].Add([double]$pair[1]) } else { $evidenceValid = $false }
            }
        }
        $invalidReasons = @($observation.invalidReasons)
        $actualValid = $evidenceValid -and $invalidReasons.Count -eq 0
        if ($claimedValid -ne $actualValid) { $errors.Add("Observation seed '$seedText' valid flag does not match its evidence.") }
        if ($claimedMetricsComplete -ne $evidenceMetricsComplete) { $errors.Add("Observation seed '$seedText' metricsComplete flag does not match its evidence.") }
        foreach ($metric in @('worstShardWallMs', 'summedShardWallMs', 'cpuMs', 'diskReadBytes', 'diskWriteBytes', 'factories', 'startupMs')) {
            if (-not (Test-NonNegativeNumber $observation.metrics.$metric)) { $errors.Add("Observation seed '$seedText' has invalid metric '$metric'.") }
        }
        $derivedMetrics = @{
            worstShardWallMs = if ($wallValues.Count) { ($wallValues | Measure-Object -Maximum).Maximum } else { 0 }
            summedShardWallMs = ($wallValues | Measure-Object -Sum).Sum
            cpuMs = ($cpuValues | Measure-Object -Sum).Sum
            diskReadBytes = ($readValues | Measure-Object -Sum).Sum
            diskWriteBytes = ($writeValues | Measure-Object -Sum).Sum
            factories = ($factoryValues | Measure-Object -Sum).Sum
            startupMs = ($startupValues | Measure-Object -Sum).Sum
            testCount = ($testValues | Measure-Object -Sum).Sum
        }
        foreach ($metric in $derivedMetrics.Keys) {
            if (-not (Test-NonNegativeNumber $observation.metrics.$metric) -or [math]::Abs([double]$observation.metrics.$metric - [double]$derivedMetrics[$metric]) -gt 0.01) {
                $errors.Add("Observation seed '$seedText' metric '$metric' does not reconcile with shard evidence.")
            }
        }
        $normalized.Add([pscustomobject]@{
            seed = $seedText
            valid = $actualValid
            metricsComplete = $evidenceMetricsComplete
            metrics = $observation.metrics
        })
    }
    $actualValidCount = @($normalized | Where-Object valid).Count
    $actualMetricsComplete = (@($normalized | Where-Object { -not $_.metricsComplete }).Count -eq 0 -and $normalized.Count -eq $required)
    if ([int]$Summary.observationCount -ne $observations.Count) { $errors.Add("$ExpectedCondition summary observationCount does not match its observations.") }
    if ([int]$Summary.validObservationCount -ne $actualValidCount) { $errors.Add("$ExpectedCondition summary validObservationCount does not match recomputed evidence.") }
    if ([bool]$Summary.allValid -ne ($normalized.Count -eq $required -and $actualValidCount -eq $required)) { $errors.Add("$ExpectedCondition summary allValid flag does not match recomputed evidence.") }
    if ([bool]$Summary.metricsComplete -ne $actualMetricsComplete) { $errors.Add("$ExpectedCondition summary metricsComplete flag does not match recomputed evidence.") }
    [pscustomobject]@{
        valid = ($errors.Count -eq 0)
        errors = @($errors)
        condition = $ExpectedCondition
        requiredRuns = $required
        observations = @($normalized)
        validObservationCount = $actualValidCount
        allValid = ($normalized.Count -eq $required -and $actualValidCount -eq $required)
        metricsComplete = $actualMetricsComplete
    }
}

function Get-Decision([object]$Baseline, [object]$Treatment) {
    $base = Get-ValidatedSummary $Baseline 'baseline'
    $treat = Get-ValidatedSummary $Treatment 'treatment'
    $validationErrors = @($base.errors + $treat.errors)
    $baseValid = @($base.observations | Where-Object valid)
    $treatmentValid = @($treat.observations | Where-Object valid)
    $baseValues = @($baseValid | ForEach-Object { [double]$_.metrics.worstShardWallMs })
    $treatmentValues = @($treatmentValid | ForEach-Object { [double]$_.metrics.worstShardWallMs })
    $baseMedian = (Get-Quantiles $baseValues).median
    $treatmentMedian = (Get-Quantiles $treatmentValues).median
    $improvement = if ($baseMedian -gt 0) { 1 - ($treatmentMedian / $baseMedian) } else { $null }
    $paired = @()
    $baseBySeed = @{}
    foreach ($observation in $baseValid) { $baseBySeed[$observation.seed] = $observation }
    $treatmentBySeed = @{}
    foreach ($observation in $treatmentValid) { $treatmentBySeed[$observation.seed] = $observation }
    $baseSeeds = @($baseBySeed.Keys | Sort-Object)
    $treatmentSeeds = @($treatmentBySeed.Keys | Sort-Object)
    if (Compare-Object $baseSeeds $treatmentSeeds) { $validationErrors += 'Baseline and treatment valid observations do not have the same seed set.' }
    foreach ($seed in $baseSeeds) {
        if (-not $treatmentBySeed.ContainsKey($seed)) { continue }
        $baseWall = [double]$baseBySeed[$seed].metrics.worstShardWallMs
        $treatmentWall = [double]$treatmentBySeed[$seed].metrics.worstShardWallMs
        if ($baseWall -le 0) { $validationErrors += "Baseline seed '$seed' has a non-positive worst-shard wall time."; continue }
        if ($treatmentWall -lt 0) { $validationErrors += "Treatment seed '$seed' has a negative worst-shard wall time."; continue }
        $paired += 1 - ($treatmentWall / $baseWall)
    }
    foreach ($value in $baseValues) { if ($value -le 0) { $validationErrors += 'A baseline worst-shard wall time is non-positive.' } }
    foreach ($value in $treatmentValues) { if ($value -lt 0) { $validationErrors += 'A treatment worst-shard wall time is negative.' } }
    $pairedMedian = if ($paired.Count -gt 0) { (Get-Quantiles $paired).median } else { $null }
    $complete = $base.allValid -and $treat.allValid -and $base.metricsComplete -and $treat.metricsComplete -and $validationErrors.Count -eq 0
    $pass = $complete -and $improvement -ge 0.15 -and $pairedMedian -ge 0.15
    [ordered]@{
        rule = 'Both conditions require the configured number of valid, fully instrumented observations. The treatment median worst-shard wall must be at least 15% lower, and the paired-seed median must also be at least 15% lower.'
        baselineMedianWorstShardWallMs = $baseMedian
        treatmentMedianWorstShardWallMs = $treatmentMedian
        aggregateImprovement = $improvement
        pairedImprovement = Get-Quantiles $paired
        completeEvidence = $complete
        validationErrors = @($validationErrors | Sort-Object -Unique)
        status = if (-not $complete) { 'inconclusive' } elseif ($pass) { 'pass' } else { 'fail' }
    }
}

function Assert-OutputOutsideWorktrees([string]$OutputPath, [object[]]$Worktrees) {
    $fullOutput = [System.IO.Path]::GetFullPath($OutputPath).TrimEnd('\', '/')
    foreach ($worktree in $Worktrees) {
        $fullWorktree = ([string]$worktree.path).TrimEnd('\', '/')
        $prefix = $fullWorktree + [System.IO.Path]::DirectorySeparatorChar
        if ($fullOutput.Equals($fullWorktree, [StringComparison]::OrdinalIgnoreCase) -or
            $fullOutput.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Output must be outside the condition worktrees: $OutputPath"
        }
    }
}

function Assert-EmptyOutput([string]$Path, [string]$Mode = 'Run') {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { Fail "Run output path is a file: $Path" }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        $items = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)
        if ($items.Count -gt 0) { Fail "Refusing to reuse non-empty $Mode output directory: $Path" }
    }
}

function Assert-WorktreeStable([object]$Expected) {
    $current = Get-RepoInfo $Expected.path
    if (-not $current.clean) { Fail "Worktree became dirty: $($Expected.path)" }
    if (-not $current.head.Equals($Expected.head, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "Worktree HEAD changed from $($Expected.head) to $($current.head): $($Expected.path)"
    }
}

function Assert-ComparableEnvironment([object]$Baseline, [object]$Treatment) {
    foreach ($property in @('dotnet', 'node', 'powershell', 'processorCount', 'machine', 'os', 'cpu')) {
        $left = $Baseline.$property | ConvertTo-Json -Depth 10 -Compress
        $right = $Treatment.$property | ConvertTo-Json -Depth 10 -Compress
        if ($left -ne $right) { Fail "Baseline/treatment environment differs for '$property'." }
    }
}

function New-Plan([string]$Root, [object]$BaselineInfo, [object]$TreatmentInfo, [object]$Manifest, [object[]]$Partitions, [string]$PlanMode = 'Plan') {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $observations = @($Partitions | ForEach-Object {
        $index = [array]::IndexOf($Partitions, $_)
        [ordered]@{
            run = $index + 1
            seed = $_.seed
            order = if (($index % 2) -eq 0) { @('baseline', 'treatment') } else { @('treatment', 'baseline') }
            partition = $_
        }
    })
    $plan = [ordered]@{
        schemaVersion = 'aerolink-api-host-reuse-measurement/v1'
        mode = $PlanMode
        planOnly = ($PlanMode -eq 'Plan')
        runs = $Runs
        shardCount = $ShardCount
        warmup = [bool]$Warmup
        baseline = $BaselineInfo
        treatment = $TreatmentInfo
        baselineManifest = $Manifest.baseline
        treatmentManifest = $Manifest.treatment
        observations = $observations
        decisionRule = 'Require ten valid observations per condition, no test/telemetry/lock failures, and at least 15% reduction in median worst-shard wall clock plus paired-seed median improvement.'
        execution = if ($PlanMode -eq 'Plan') { 'This plan does not build, launch test shards, touch PostgreSQL, or change either worktree.' } else { 'Saved Run plan. The harness will restore/build sequentially, then launch only the planned shards; baseline and treatment never run concurrently.' }
    }
    Write-JsonFile (Join-Path $Root 'plan.json') $plan
    $planMarkdownExecution = if ($PlanMode -eq 'Plan') { '- No tests, builds, database connections, or GitHub operations were started in plan mode.' } else { '- This is the saved execution plan for Run mode; restore/build and shard execution occur after this file is written.' }
    @(
        '# API host-reuse measurement plan',
        '',
        "- Conditions: $($BaselineInfo.path) and $($TreatmentInfo.path)",
        "- Runs: $Runs; shards per observation: $ShardCount; warmup: $Warmup",
        "- Cases: $($Manifest.baseline.caseCount); classes: $($Manifest.baseline.classCount)",
        '- The plan is deterministic for each seed and reuses the same partition for baseline and treatment.',
        $planMarkdownExecution,
        '',
        'Run mode requires two clean, already prepared worktrees and writes only beneath the selected output directory.'
    ) | Set-Content -LiteralPath (Join-Path $Root 'plan.md') -Encoding utf8
    $plan
}

function Main {
    if (-not $OutputRoot) { $OutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'aerolink-api-host-reuse-measurement' }
    if ($ShardCount -lt 1) { Fail 'ShardCount must be positive.' }
    if ($TimeoutMinutes -lt 1) { Fail 'TimeoutMinutes must be positive.' }
    if ($ProcessTimeoutMinutes -lt 1) { Fail 'ProcessTimeoutMinutes must be positive.' }
    if ($Runs -lt 1) { Fail 'Runs must be positive.' }
    if ($Seeds) {
        $seedValues = @($Seeds -split ',' | ForEach-Object {
            $parsed = 0
            if (-not [int]::TryParse($_.Trim(), [ref]$parsed)) { Fail "Seed '$($_.Trim())' is not an integer." }
            $parsed
        })
    } else {
        $seedValues = @(563000..(563000 + $Runs - 1))
    }
    if ($seedValues.Count -lt $Runs) { Fail "Provide at least $Runs deterministic seeds." }
    $Seeds = $seedValues
    if ($Mode -eq 'Evaluate') {
        if (-not $BaselineSummaryPath -or -not $TreatmentSummaryPath) { Fail 'Evaluate mode requires BaselineSummaryPath and TreatmentSummaryPath.' }
        $baseline = Get-Content -Raw -LiteralPath $BaselineSummaryPath | ConvertFrom-Json
        $treatment = Get-Content -Raw -LiteralPath $TreatmentSummaryPath | ConvertFrom-Json
        $decision = Get-Decision $baseline $treatment
        Write-JsonFile (Join-Path $OutputRoot 'decision.json') $decision
        $decision | ConvertTo-Json -Depth 20
        return
    }
    if (-not $BaselinePath) { $BaselinePath = (Get-Location).Path }
    if (-not $TreatmentPath) { $TreatmentPath = $BaselinePath }
    if ($Mode -eq 'Run' -and $TestListPath) { Fail 'Run mode requires live discovery; TestListPath is allowed only for Plan contract smoke.' }
    if ($Mode -eq 'Run' -and -not $SkipBuild -and -not $Warmup) {
        # A warmup is optional, but a build is never optional unless SkipBuild was explicitly requested.
        # This branch exists only to make the safety warning visible in the generated plan.
    }
    if (-not $IsWindows) { Fail 'This harness is intentionally Windows-only.' }

    $baselineInfo = Get-RepoInfo $BaselinePath
    $treatmentInfo = Get-RepoInfo $TreatmentPath
    if ($Mode -eq 'Run' -and $baselineInfo.path.Equals($treatmentInfo.path, [StringComparison]::OrdinalIgnoreCase)) {
        Fail 'Run mode requires distinct baseline and treatment worktrees.'
    }
    Assert-OutputOutsideWorktrees $OutputRoot @($baselineInfo, $treatmentInfo)
    if ($Mode -eq 'Run' -and $baselineInfo.head.Equals($treatmentInfo.head, [StringComparison]::OrdinalIgnoreCase)) {
        Fail 'Run mode rejects identical baseline and treatment SHAs; provide two distinct commits.'
    }
    $environment = [ordered]@{
        baseline = Get-EnvironmentInfo $BaselinePath
        treatment = Get-EnvironmentInfo $TreatmentPath
    }
    if ($Mode -eq 'Run') { Assert-ComparableEnvironment $environment.baseline $environment.treatment }
    $baselineList = if ($TestListPath) { $TestListPath } else { $null }
    $baselineManifest = Get-TestManifest $BaselinePath $baselineList
    $treatmentManifest = Get-TestManifest $TreatmentPath $baselineList
    Assert-SameManifest $baselineManifest $treatmentManifest
    $partitions = @($Seeds[0..($Runs - 1)] | ForEach-Object { New-Partition $baselineManifest.classes $_ $ShardCount })
    $manifest = [pscustomobject]@{ baseline = $baselineManifest; treatment = $treatmentManifest }
    if ($Mode -eq 'Plan') {
        Assert-EmptyOutput $OutputRoot 'Plan'
        $plan = New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions 'Plan'
        $plan | ConvertTo-Json -Depth 20
        return
    }

    if (-not $baselineInfo.clean -or -not $treatmentInfo.clean) { Fail 'Run mode requires clean baseline and treatment worktrees.' }
    Assert-EmptyOutput $OutputRoot
    $plan = New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions 'Run'
    Assert-WorktreeStable $baselineInfo
    Assert-WorktreeStable $treatmentInfo
    if (-not $SkipBuild) {
        foreach ($condition in @(@('baseline', $BaselinePath), @('treatment', $TreatmentPath))) {
            if ($condition[0] -eq 'baseline') { Assert-WorktreeStable $baselineInfo } else { Assert-WorktreeStable $treatmentInfo }
            $restore = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('restore', (Join-Path $condition[1] $SolutionPath)) -WorkingDirectory $condition[1]
            if ($restore.ExitCode -ne 0) { Fail "$($condition[0]) restore failed.`n$($restore.Stdout)`n$($restore.Stderr)" }
            if ($condition[0] -eq 'baseline') { Assert-WorktreeStable $baselineInfo } else { Assert-WorktreeStable $treatmentInfo }
            $build = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('build', (Join-Path $condition[1] $SolutionPath), '--configuration', 'Release', '--no-restore') -WorkingDirectory $condition[1]
            if ($build.ExitCode -ne 0) { Fail "$($condition[0]) build failed.`n$($build.Stdout)`n$($build.Stderr)" }
        }
    }
    Assert-WorktreeStable $baselineInfo
    Assert-WorktreeStable $treatmentInfo
    $all = @{ baseline = [System.Collections.Generic.List[object]]::new(); treatment = [System.Collections.Generic.List[object]]::new() }
    if ($Warmup) {
        $warmupPartition = $partitions[0]
        Assert-WorktreeStable $baselineInfo
        Assert-WorktreeStable $treatmentInfo
        $warmupBaseline = Invoke-Observation -Condition baseline -Worktree $BaselinePath -Partition $warmupPartition -RunNumber 0 -Seed $warmupPartition.seed -Root $OutputRoot -Order @('baseline') -Environment $environment -IsWarmup
        if (-not $warmupBaseline.valid) { Fail 'Baseline warmup was invalid; no measured observations were started.' }
        Assert-WorktreeStable $baselineInfo
        Assert-WorktreeStable $treatmentInfo
        $warmupTreatment = Invoke-Observation -Condition treatment -Worktree $TreatmentPath -Partition $warmupPartition -RunNumber 0 -Seed $warmupPartition.seed -Root $OutputRoot -Order @('treatment') -Environment $environment -IsWarmup
        if (-not $warmupTreatment.valid) { Fail 'Treatment warmup was invalid; no measured observations were started.' }
    }
    for ($index = 0; $index -lt $Runs; $index++) {
        $partition = $partitions[$index]
        $order = if (($index % 2) -eq 0) { @('baseline', 'treatment') } else { @('treatment', 'baseline') }
        foreach ($condition in $order) {
            Assert-WorktreeStable $baselineInfo
            Assert-WorktreeStable $treatmentInfo
            $path = if ($condition -eq 'baseline') { $BaselinePath } else { $TreatmentPath }
            $observation = Invoke-Observation -Condition $condition -Worktree $path -Partition $partition -RunNumber ($index + 1) -Seed $partition.seed -Root $OutputRoot -Order $order -Environment $environment
            $all[$condition].Add($observation)
        }
    }
    $baselineSummary = Get-ConditionSummary baseline @($all.baseline) $Runs
    $treatmentSummary = Get-ConditionSummary treatment @($all.treatment) $Runs
    $decision = Get-Decision $baselineSummary $treatmentSummary
    Write-JsonFile (Join-Path $OutputRoot 'baseline-summary.json') $baselineSummary
    Write-JsonFile (Join-Path $OutputRoot 'treatment-summary.json') $treatmentSummary
    Write-JsonFile (Join-Path $OutputRoot 'summary.json') ([ordered]@{ schemaVersion = 'aerolink-api-host-reuse-measurement/v1'; environment = $environment; baseline = $baselineSummary; treatment = $treatmentSummary; decision = $decision })
    $decision | ConvertTo-Json -Depth 20
}

Main
