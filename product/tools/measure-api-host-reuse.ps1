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

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [hashtable]$Environment
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
    $process.WaitForExit()
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdoutTask.GetAwaiter().GetResult()
        Stderr = $stderrTask.GetAwaiter().GetResult()
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
    $cpuAvailable = $true
    foreach ($id in $ids) {
        try { $cpu += (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime.TotalMilliseconds } catch { $cpuAvailable = $false }
    }
    $io = Get-IoRate ([System.Collections.Generic.HashSet[int]]::new([int[]]$ids))
    [pscustomobject]@{ ids = $ids; cpuMs = $cpu; cpuAvailable = $cpuAvailable; io = $io; at = [DateTimeOffset]::UtcNow }
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
        lastSampleAt = $startedAt
        samples = 0
    }
}

function Wait-TestProcesses([object[]]$Shards) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
    while ($true) {
        $active = @($Shards | Where-Object { -not $_.process.HasExited })
        foreach ($shard in $Shards) {
            $sample = Get-ProcessSample @($shard.process.Id)
            $shard.samples++
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
                $shard.cpuError = 'One or more process-tree CPU samples were unavailable.'
            }
            $shard.lastSampleAt = $sample.at
        }
        if ($active.Count -eq 0) { break }
        if ([DateTimeOffset]::UtcNow -gt $deadline) {
            foreach ($shard in $active) {
                $ids = Get-ProcessTreeIds @($shard.process.Id)
                foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
                $shard.timedOut = $true
            }
            break
        }
        Start-Sleep -Milliseconds 500
    }
    foreach ($shard in $Shards) {
        $shard.process.WaitForExit()
        $shard.stdout = $shard.stdoutTask.GetAwaiter().GetResult()
        $shard.stderr = $shard.stderrTask.GetAwaiter().GetResult()
        $shard.endedAt = [DateTimeOffset]::UtcNow
        Set-Content -LiteralPath $shard.stdoutPath -Value $shard.stdout -Encoding utf8
        Set-Content -LiteralPath $shard.stderrPath -Value $shard.stderr -Encoding utf8
        $shard.exitCode = $shard.process.ExitCode
        $shard.wallMs = ($shard.endedAt - $shard.shardStartedAt).TotalMilliseconds
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
        return [pscustomobject]@{ report = $null; malformed = $null; truncated = $null; output = 'telemetry or TRX missing' }
    }
    $aggregator = Join-Path $Worktree 'product/ci-metrics/bin/aggregate-api-telemetry.mjs'
    $result = Invoke-CapturedProcess -FileName $NodeExecutable -Arguments @($aggregator, $TelemetryPath, $TrxPath, $OutputPath) -WorkingDirectory $Worktree
    $combined = "$($result.Stdout)`n$($result.Stderr)"
    $malformed = $null
    $truncated = $null
    if ($combined -match '\((\d+) malformed lines, truncated=(true|false)\)') {
        $malformed = [int]$Matches[1]
        $truncated = [bool]::Parse($Matches[2])
    }
    $reportPath = Join-Path $OutputPath 'api-telemetry.json'
    $report = if (Test-Path -LiteralPath $reportPath -PathType Leaf) { Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json } else { $null }
    [pscustomobject]@{ report = $report; malformed = $malformed; truncated = $truncated; output = $combined; exitCode = $result.ExitCode }
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
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
    $shards = @()
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

    $invalidReasons = [System.Collections.Generic.List[string]]::new()
    $errorPattern = '(?i)(SQLITE_BUSY|SQLITE_LOCKED|database is locked|MSB3027|UnauthorizedAccessException|file.*locked|testhost.*crash)'
    $shardReports = foreach ($shard in $shards) {
        $trx = Join-Path $shard.resultsPath 'shard.trx'
        $counts = Get-TrxCounts $trx
        $aggregateDirectory = Join-Path $shard.resultsPath 'api-telemetry'
        $aggregate = Invoke-TelemetryAggregator -Worktree $Worktree -TelemetryPath $shard.telemetryPath -TrxPath $trx -OutputPath $aggregateDirectory
        $signals = @(("$($shard.stdout)`n$($shard.stderr)") | Select-String -Pattern $errorPattern -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique)
        if ($shard.exitCode -ne 0) { $invalidReasons.Add("shard $($shard.shard) exit code $($shard.exitCode)") }
        if ($shard.timedOut) { $invalidReasons.Add("shard $($shard.shard) exceeded the $TimeoutMinutes minute timeout") }
        if ($null -eq $counts) { $invalidReasons.Add("shard $($shard.shard) TRX missing") }
        elseif ($counts.total -ne $shard.expectedCases) { $invalidReasons.Add("shard $($shard.shard) expected $($shard.expectedCases) cases but TRX has $($counts.total)") }
        if ($counts -and ($counts.failed -gt 0 -or $counts.skipped -gt 0 -or $counts.other -gt 0)) { $invalidReasons.Add("shard $($shard.shard) has failed/skipped/other outcomes") }
        if ($null -eq $aggregate.report) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregation missing") }
        elseif ($aggregate.report.totals.trxTests -ne $shard.expectedCases) { $invalidReasons.Add("shard $($shard.shard) telemetry/TRX count mismatch") }
        if ($aggregate.exitCode -ne 0) { $invalidReasons.Add("shard $($shard.shard) telemetry aggregator exit code $($aggregate.exitCode)") }
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
            cpuMs = [math]::Round($shard.maxCpuMs, 3)
            cpuAvailable = $shard.cpuAvailable
            cpuError = $shard.cpuError
            diskReadBytes = [math]::Round($shard.diskReadBytes, 3)
            diskWriteBytes = [math]::Round($shard.diskWriteBytes, 3)
            ioAvailable = $shard.ioAvailable
            ioError = $shard.ioError
            samples = $shard.samples
            errorSignals = $signals
        }
    }
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
        startedAt = ($shards | ForEach-Object shardStartedAt | Sort-Object | Select-Object -First 1).ToString('o')
        endedAt = ($shards | ForEach-Object endedAt | Sort-Object -Descending | Select-Object -First 1).ToString('o')
        valid = ($invalidReasons.Count -eq 0)
        invalidReasons = @($invalidReasons)
        metricsComplete = (@($shardReports | Where-Object { -not $_.ioAvailable -or -not $_.cpuAvailable }).Count -eq 0)
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
        $quantiles[$metric] = Get-Quantiles @($Observations | ForEach-Object { [double]$_.metrics.$metric })
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

function Get-Decision([object]$Baseline, [object]$Treatment) {
    $baseValues = @($Baseline.observations | ForEach-Object { [double]$_.metrics.worstShardWallMs })
    $treatmentValues = @($Treatment.observations | ForEach-Object { [double]$_.metrics.worstShardWallMs })
    $baseMedian = (Get-Quantiles $baseValues).median
    $treatmentMedian = (Get-Quantiles $treatmentValues).median
    $improvement = if ($baseMedian -gt 0) { 1 - ($treatmentMedian / $baseMedian) } else { $null }
    $paired = @()
    $pairCount = [math]::Min($baseValues.Count, $treatmentValues.Count)
    for ($index = 0; $index -lt $pairCount; $index++) { $paired += 1 - ($treatmentValues[$index] / $baseValues[$index]) }
    $pairedMedian = if ($paired.Count -gt 0) { (Get-Quantiles $paired).median } else { $null }
    $complete = $Baseline.allValid -and $Treatment.allValid -and $Baseline.metricsComplete -and $Treatment.metricsComplete
    $pass = $complete -and $improvement -ge 0.15 -and $pairedMedian -ge 0.15
    [ordered]@{
        rule = 'Both conditions require the configured number of valid, fully instrumented observations. The treatment median worst-shard wall must be at least 15% lower, and the paired-seed median must also be at least 15% lower.'
        baselineMedianWorstShardWallMs = $baseMedian
        treatmentMedianWorstShardWallMs = $treatmentMedian
        aggregateImprovement = $improvement
        pairedImprovement = Get-Quantiles $paired
        completeEvidence = $complete
        status = if (-not $complete) { 'inconclusive' } elseif ($pass) { 'pass' } else { 'fail' }
    }
}

function New-Plan([string]$Root, [object]$BaselineInfo, [object]$TreatmentInfo, [object]$Manifest, [object[]]$Partitions) {
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
        mode = 'Plan'
        planOnly = $true
        runs = $Runs
        shardCount = $ShardCount
        warmup = [bool]$Warmup
        baseline = $BaselineInfo
        treatment = $TreatmentInfo
        baselineManifest = $Manifest.baseline
        treatmentManifest = $Manifest.treatment
        observations = $observations
        decisionRule = 'Require ten valid observations per condition, no test/telemetry/lock failures, and at least 15% reduction in median worst-shard wall clock plus paired-seed median improvement.'
        execution = 'This plan does not build, launch test shards, touch PostgreSQL, or change either worktree.'
    }
    Write-JsonFile (Join-Path $Root 'plan.json') $plan
    @(
        '# API host-reuse measurement plan',
        '',
        "- Conditions: $($BaselineInfo.path) and $($TreatmentInfo.path)",
        "- Runs: $Runs; shards per observation: $ShardCount; warmup: $Warmup",
        "- Cases: $($Manifest.baseline.caseCount); classes: $($Manifest.baseline.classCount)",
        '- The plan is deterministic for each seed and reuses the same partition for baseline and treatment.',
        '- No tests, builds, database connections, or GitHub operations were started in plan mode.',
        '',
        'Run mode requires two clean, already prepared worktrees and writes only beneath the selected output directory.'
    ) | Set-Content -LiteralPath (Join-Path $Root 'plan.md') -Encoding utf8
    $plan
}

function Main {
    if (-not $OutputRoot) { $OutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'aerolink-api-host-reuse-measurement' }
    if ($ShardCount -lt 1) { Fail 'ShardCount must be positive.' }
    if ($TimeoutMinutes -lt 1) { Fail 'TimeoutMinutes must be positive.' }
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
    $environment = [ordered]@{
        baseline = Get-EnvironmentInfo $BaselinePath
        treatment = Get-EnvironmentInfo $TreatmentPath
    }
    $baselineList = if ($TestListPath) { $TestListPath } else { $null }
    $baselineManifest = Get-TestManifest $BaselinePath $baselineList
    $treatmentManifest = Get-TestManifest $TreatmentPath $baselineList
    Assert-SameManifest $baselineManifest $treatmentManifest
    $partitions = @($Seeds[0..($Runs - 1)] | ForEach-Object { New-Partition $baselineManifest.classes $_ $ShardCount })
    $manifest = [pscustomobject]@{ baseline = $baselineManifest; treatment = $treatmentManifest }
    if ($Mode -eq 'Plan') {
        $plan = New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions
        $plan | ConvertTo-Json -Depth 20
        return
    }

    if (-not $baselineInfo.clean -or -not $treatmentInfo.clean) { Fail 'Run mode requires clean baseline and treatment worktrees.' }
    $outputFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
    foreach ($worktree in @($baselineInfo.path, $treatmentInfo.path)) {
        $parentPrefix = $worktree.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if ($outputFullPath.Equals($worktree, [StringComparison]::OrdinalIgnoreCase) -or
            $outputFullPath.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Run output must be outside the condition worktrees: $OutputRoot"
        }
    }
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    if (-not $SkipBuild) {
        foreach ($condition in @(@('baseline', $BaselinePath), @('treatment', $TreatmentPath))) {
            $restore = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('restore', (Join-Path $condition[1] $SolutionPath)) -WorkingDirectory $condition[1]
            if ($restore.ExitCode -ne 0) { Fail "$($condition[0]) restore failed.`n$($restore.Stdout)`n$($restore.Stderr)" }
            $build = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('build', (Join-Path $condition[1] $SolutionPath), '--configuration', 'Release', '--no-restore') -WorkingDirectory $condition[1]
            if ($build.ExitCode -ne 0) { Fail "$($condition[0]) build failed.`n$($build.Stdout)`n$($build.Stderr)" }
        }
    }
    $all = @{ baseline = [System.Collections.Generic.List[object]]::new(); treatment = [System.Collections.Generic.List[object]]::new() }
    if ($Warmup) {
        $warmupPartition = $partitions[0]
        Invoke-Observation -Condition baseline -Worktree $BaselinePath -Partition $warmupPartition -RunNumber 0 -Seed $warmupPartition.seed -Root $OutputRoot -Order @('baseline') -Environment $environment -IsWarmup | Out-Null
        Invoke-Observation -Condition treatment -Worktree $TreatmentPath -Partition $warmupPartition -RunNumber 0 -Seed $warmupPartition.seed -Root $OutputRoot -Order @('treatment') -Environment $environment -IsWarmup | Out-Null
    }
    for ($index = 0; $index -lt $Runs; $index++) {
        $partition = $partitions[$index]
        $order = if (($index % 2) -eq 0) { @('baseline', 'treatment') } else { @('treatment', 'baseline') }
        foreach ($condition in $order) {
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
