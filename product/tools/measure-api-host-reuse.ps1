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
    [int]$MaxProcessTreeCount = 256,
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

function Stop-ProcessSafely {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [object]$ExpectedIdentity,
        [object[]]$KnownRecords = @(),
        [int]$TimeoutMilliseconds = 5000,
        [switch]$Forced
    )

    $rootId = $Process.Id
    if (-not $ExpectedIdentity) {
        return [pscustomobject]@{ ownedIds = @($rootId); exited = $false; remainingIds = @($rootId); error = 'No expected process identity was available; no process was killed.' }
    }
    if ($Forced) {
        $knownIds = @(@($KnownRecords) | ForEach-Object processId | Where-Object { $null -ne $_ } | Sort-Object -Unique)
        if ($knownIds -notcontains $rootId) { $knownIds = @($rootId) + $knownIds }
        return [pscustomobject]@{
            ownedIds = $knownIds
            exited = $false
            remainingIds = $knownIds
            error = 'Forced cleanup is fail-closed without Windows Job Object containment; no process was killed and residual descendants remain unverified.'
        }
    }
    $snapshot = Get-ProcessTreeSnapshot @($rootId)
    if (-not $snapshot.success) {
        return [pscustomobject]@{ ownedIds = @($rootId); exited = $false; remainingIds = @($rootId); error = $snapshot.error }
    }
    if (@($KnownRecords).Count -gt $MaxProcessTreeCount) {
        return [pscustomobject]@{ ownedIds = @($rootId); exited = $false; remainingIds = @($rootId); error = "Known owned process records exceeded MaxProcessTreeCount=$MaxProcessTreeCount." }
    }

    # Keep every identity ever observed.  A descendant can be reparented or the
    # root can exit before cleanup; relying only on a fresh parent-tree walk in
    # that case could either leak an owned process or make a PID-reuse mistake.
    $known = @{}
    $mergeRecords = {
        param([object[]]$Records)
        foreach ($record in @($Records)) {
            if ($null -eq $record -or $null -eq $record.processId -or [string]::IsNullOrWhiteSpace([string]$record.creationDate)) { continue }
            $key = "{0}|{1}" -f [int]$record.processId, [string]$record.creationDate
            if (-not $known.ContainsKey($key)) { $known[$key] = [pscustomobject]@{ processId = [int]$record.processId; parentProcessId = [int]$record.parentProcessId; creationDate = [string]$record.creationDate; name = [string]$record.name } }
        }
    }
    & $mergeRecords @($snapshot.records + $KnownRecords + $ExpectedIdentity)
    if ($known.Count -gt $MaxProcessTreeCount) {
        return [pscustomobject]@{ ownedIds = @($known.Values | ForEach-Object processId | Sort-Object -Unique); exited = $false; remainingIds = @($known.Values | ForEach-Object processId | Sort-Object -Unique); error = "Known owned process records exceeded MaxProcessTreeCount=$MaxProcessTreeCount." }
    }

    $errors = [System.Collections.Generic.List[string]]::new()
    $attempted = @{}
    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        # While the root is still alive, refresh the ancestry and merge it into
        # the immutable identity set. This is the best bounded protection
        # available against a child being spawned between snapshots.
        $rootResult = Get-ProcessIdentityResult $rootId
        if ($rootResult.error) { $errors.Add($rootResult.error); break }
        $rootCurrent = $rootResult.identity
        if ($rootCurrent -and -not (Test-ProcessIdentity $rootCurrent $ExpectedIdentity)) {
            $errors.Add('Root PID identity changed; no kill was attempted.')
            break
        }
        if ($rootCurrent) {
            try {
                $fresh = Get-ProcessTreeSnapshot @($rootId)
                if (-not $fresh.success) { $errors.Add($fresh.error); break }
                & $mergeRecords @($fresh.records)
                if ($known.Count -gt $MaxProcessTreeCount) { $errors.Add("Known owned process records exceeded MaxProcessTreeCount=$MaxProcessTreeCount."); break }
            } catch { $errors.Add("Process-tree refresh failed: $($_.Exception.Message)"); break }
        }

        foreach ($record in @($known.Values | Sort-Object @{ Expression = { if ([int]$_.processId -eq $rootId) { 0 } else { 1 } } }, processId)) {
            $pid = [int]$record.processId
            $currentResult = Get-ProcessIdentityResult $pid
            if ($currentResult.error) { $errors.Add($currentResult.error); continue }
            $current = $currentResult.identity
            if (-not $current) { continue }
            if (-not (Test-ProcessIdentity $current $record)) {
                $errors.Add("Process identity changed for PID $pid; no kill was attempted.")
                $attempted["$pid|$($record.creationDate)"] = $true
                continue
            }
            $key = "$pid|$($record.creationDate)"
            if ($attempted.ContainsKey($key)) { continue }
            try {
                $ownedProcess = if ($pid -eq $rootId) { $Process } else { Get-Process -Id $pid -ErrorAction Stop }
                if (-not (Test-OpenedProcessIdentity $ownedProcess $record)) { throw "Opened process handle identity did not match PID $pid; no kill was attempted." }
                if (-not $ownedProcess.HasExited) { $ownedProcess.Kill() }
                $attempted[$key] = $true
            } catch { $errors.Add("PID $pid cleanup failed: $($_.Exception.Message)"); $attempted[$key] = $true }
        }
        Start-Sleep -Milliseconds 100
        $remainingNow = @()
        foreach ($record in @($known.Values)) {
            $currentResult = Get-ProcessIdentityResult ([int]$record.processId)
            if ($currentResult.error) { $errors.Add($currentResult.error); continue }
            $current = $currentResult.identity
            if ($current -and (Test-ProcessIdentity $current $record)) { $remainingNow += $record }
            elseif ($current) { $errors.Add("Process identity changed for PID $($record.processId); no kill was attempted.") }
        }
        if ($remainingNow.Count -eq 0 -and [DateTimeOffset]::UtcNow -ge $deadline) { break }
        if ($remainingNow.Count -eq 0 -and -not $rootCurrent) { break }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    # Final verification is against every known identity, not merely the
    # current process tree. An absent PID is safe; a changed identity is not.
    $remaining = [System.Collections.Generic.List[int]]::new()
    foreach ($record in @($known.Values)) {
        $currentResult = Get-ProcessIdentityResult ([int]$record.processId)
        if ($currentResult.error) { $errors.Add($currentResult.error); continue }
        $current = $currentResult.identity
        if ($current -and (Test-ProcessIdentity $current $record)) { $remaining.Add([int]$record.processId) }
        elseif ($current) { $errors.Add("Process identity changed for PID $($record.processId); no kill was attempted.") }
    }
    $uniqueErrors = @($errors | Sort-Object -Unique)
    [pscustomobject]@{
        ownedIds = @($known.Values | ForEach-Object processId | Sort-Object -Unique)
        exited = ($remaining.Count -eq 0 -and $uniqueErrors.Count -eq 0)
        remainingIds = @($remaining | Sort-Object -Unique)
        error = if ($uniqueErrors.Count -gt 0) { $uniqueErrors -join ' ' } else { $null }
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
    $rootIdentity = $null
    $stdoutTask = $null
    $stderrTask = $null
    $timedOut = $false
    $cleanup = $null
    $primaryError = $null
    $stdout = ''
    $stderr = ''
    $exitCode = 1
    try {
        $rootIdentity = Get-ProcessIdentity $process.Id
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $cleanup = Stop-ProcessSafely -Process $process -ExpectedIdentity $rootIdentity -Forced }
        else { $process.WaitForExit() }
        $stdout = if ($stdoutTask.Wait(5000)) { $stdoutTask.GetAwaiter().GetResult() } else { '' }
        $stderr = if ($stderrTask.Wait(5000)) { $stderrTask.GetAwaiter().GetResult() } else { '' }
        $exitCode = if ($timedOut) { 124 } else { $process.ExitCode }
    } catch {
        $primaryError = $_
    } finally {
        try {
            if (-not $process.HasExited) {
                $cleanup = Stop-ProcessSafely -Process $process -ExpectedIdentity $rootIdentity -Forced
            }
        } catch {
            if (-not $cleanup) { $cleanup = [pscustomobject]@{ exited = $false; remainingIds = @($process.Id); error = $_.Exception.Message } }
        }
    }
    if ($primaryError) {
        $message = "Process $FileName failed after start: $($primaryError.Exception.Message)"
        $cleanupFailed = ($null -eq $cleanup) -or (-not [bool]$cleanup.exited) -or (@($cleanup.remainingIds).Count -gt 0) -or $cleanup.error
        if ($cleanupFailed) {
            $cleanupMessage = if ($cleanup -and $cleanup.error) { [string]$cleanup.error } elseif ($cleanup) { "remaining owned processes: $(@($cleanup.remainingIds) -join ',')" } else { 'cleanup result was unavailable' }
            $message += " Cleanup failure: $cleanupMessage"
        }
        throw $message
    }
    [pscustomobject]@{
        ExitCode = $exitCode
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
        path = (Get-CanonicalPath $Path)
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
        worktree = (Get-CanonicalPath $Path)
    }
}

function Get-EnvironmentFingerprint([object]$Environment) {
    $canonical = [ordered]@{
        os = $Environment.os
        cpu = $Environment.cpu
        powershell = $Environment.powershell
        dotnet = $Environment.dotnet
        node = $Environment.node
        processorCount = $Environment.processorCount
        machine = $Environment.machine
    } | ConvertTo-Json -Depth 20 -Compress
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { ([System.BitConverter]::ToString($hash.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical)))).Replace('-', '').ToLowerInvariant() } finally { $hash.Dispose() }
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
    $manifest = [pscustomobject]@{
        cases = @($cases)
        classes = $classes
        caseCount = $cases.Count
        classCount = $classes.Count
    }
    $manifestHashInput = ((@($manifest.cases | ForEach-Object name | Sort-Object) -join "`n") + "`n")
    $hash = [System.Security.Cryptography.SHA256]::Create()
    try { $manifestHash = ([System.BitConverter]::ToString($hash.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($manifestHashInput)))).Replace('-', '').ToLowerInvariant() } finally { $hash.Dispose() }
    $manifest | Add-Member -NotePropertyName manifestHash -NotePropertyValue $manifestHash
    $manifest | Add-Member -NotePropertyName classFacts -NotePropertyValue @($manifest.classes | ForEach-Object { [ordered]@{ name = $_.name; cases = $_.cases } })
    $manifest
}

function Get-ManifestFacts([object]$Manifest) {
    [ordered]@{
        manifestHash = [string]$Manifest.manifestHash
        caseCount = [int]$Manifest.caseCount
        classCount = [int]$Manifest.classCount
        classFacts = @($Manifest.classFacts)
        caseNames = @($Manifest.cases | ForEach-Object name | Sort-Object)
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
    if ([string]$Expected.manifestHash -ne [string]$Actual.manifestHash) { Fail 'Baseline and treatment manifest hashes differ.' }
}

function Assert-SummaryManifestMatchesLive([object]$Persisted, [object]$Live, [string]$Condition) {
    $persistedNames = @($Persisted.caseNames | ForEach-Object { [string]$_ } | Sort-Object)
    $liveNames = @($Live.cases | ForEach-Object name | Sort-Object)
    if (Compare-Object $persistedNames $liveNames) { Fail "$Condition persisted case-name manifest differs from live discovery." }
    if ([string]$Persisted.manifestHash -ne [string]$Live.manifestHash) { Fail "$Condition persisted manifest hash differs from live discovery." }
    if ([int]$Persisted.caseCount -ne [int]$Live.caseCount -or [int]$Persisted.classCount -ne [int]$Live.classCount) { Fail "$Condition persisted manifest counts differ from live discovery." }
    $persistedFacts = @($Persisted.classFacts | ForEach-Object { "{0}|{1}" -f $_.name, $_.cases } | Sort-Object)
    $liveFacts = @($Live.classFacts | ForEach-Object { "{0}|{1}" -f $_.name, $_.cases } | Sort-Object)
    if (Compare-Object $persistedFacts $liveFacts) { Fail "$Condition persisted class facts differ from live discovery." }
}

function Assert-SummaryPartitionsMatchLive([object]$Summary, [object]$LiveManifest, [string]$Condition) {
    $liveLoads = @{}
    foreach ($class in @($LiveManifest.classes)) { $liveLoads[[string]$class.name] = [int]$class.cases }
    $liveNames = @($LiveManifest.classes | ForEach-Object name | Sort-Object)
    foreach ($observation in @($Summary.observations)) {
        $seed = 0
        if (-not [int]::TryParse(([string]$observation.seed), [ref]$seed)) { Fail "$Condition partition has a non-integer observation seed." }
        $partition = $observation.partition
        if ($null -eq $partition -or [string]$partition.algorithm -ne 'fisher-yates-then-lightest-shard') { Fail "$Condition seed $seed partition algorithm is not authoritative." }
        if ([int]$partition.seed -ne $seed) { Fail "$Condition seed $seed partition seed does not match the observation." }
        $shardCount = [int]$partition.shardCount
        if ($shardCount -lt 1 -or $shardCount -gt $liveNames.Count) { Fail "$Condition seed $seed partition shardCount is invalid." }
        $shards = @($partition.shards)
        if ($shards.Count -ne $shardCount) { Fail "$Condition seed $seed partition shard count is incomplete." }
        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $recomputedCases = 0
        foreach ($shard in $shards) {
            $classes = @($shard.classes | ForEach-Object { [string]$_ })
            foreach ($className in $classes) {
                if (-not $seen.Add($className)) { Fail "$Condition seed $seed partition repeats class '$className'." }
                if (-not $liveLoads.ContainsKey($className)) { Fail "$Condition seed $seed partition contains unknown class '$className'." }
            }
            $expectedCases = ($classes | ForEach-Object { $liveLoads[$_] } | Measure-Object -Sum).Sum
            if ([int]$shard.expectedCases -ne [int]$expectedCases) { Fail "$Condition seed $seed shard $($shard.shard) expectedCases does not match live class loads." }
            $expectedFilters = (@($classes | ForEach-Object { "FullyQualifiedName~$($_)." }) -join '|')
            if ([string]$shard.filters -cne $expectedFilters) { Fail "$Condition seed $seed shard $($shard.shard) filters do not match its exact class list." }
            $recomputedCases += [int]$expectedCases
        }
        if (Compare-Object @($seen | Sort-Object) $liveNames) { Fail "$Condition seed $seed partition class union does not exactly match live discovery." }
        if ([int]$partition.totalCases -ne [int]$LiveManifest.caseCount -or $recomputedCases -ne [int]$LiveManifest.caseCount) { Fail "$Condition seed $seed partition totalCases does not match live discovery." }
        $expected = New-Partition $LiveManifest.classes $seed $shardCount
        if ([string]$partition.algorithm -ne [string]$expected.algorithm -or [int]$partition.totalCases -ne [int]$expected.totalCases) { Fail "$Condition seed $seed partition does not match the authoritative seeded planner." }
        for ($index = 0; $index -lt $shardCount; $index++) {
            $actualShard = $shards[$index]
            $expectedShard = @($expected.shards)[$index]
            if ([int]$actualShard.shard -ne [int]$expectedShard.shard -or
                [int]$actualShard.expectedCases -ne [int]$expectedShard.expectedCases -or
                (Compare-Object @($actualShard.classes) @($expectedShard.classes)) -or
                [string]$actualShard.filters -cne [string]$expectedShard.filters) {
                Fail "$Condition seed $seed shard $($index + 1) does not match the authoritative seeded planner."
            }
        }
    }
}

function Get-ProcessIdentityResult([int]$Id) {
    try {
        $record = Get-CimInstance Win32_Process -Filter "ProcessId = $Id" -ErrorAction Stop | Select-Object -First 1 ProcessId, ParentProcessId, CreationDate, Name
        if (-not $record) { return [pscustomobject]@{ found = $false; identity = $null; error = $null } }
        return [pscustomobject]@{
            found = $true
            identity = [pscustomobject]@{ processId = [int]$record.ProcessId; parentProcessId = [int]$record.ParentProcessId; creationDate = [string]$record.CreationDate; name = [string]$record.Name }
            error = $null
        }
    } catch {
        return [pscustomobject]@{ found = $false; identity = $null; error = "CIM identity lookup failed for PID ${Id}: $($_.Exception.Message)" }
    }
}

function Get-ProcessIdentity([int]$Id) {
    $result = Get-ProcessIdentityResult $Id
    if ($result.error) { return $null }
    $result.identity
}

function Convert-ProcessCreationDate([string]$Value) {
    try {
        if ($Value -match '^\d{14}\.\d{6}[+-]\d{3}') {
            return [System.Management.ManagementDateTimeConverter]::ToDateTime($Value).ToUniversalTime()
        }
        return [DateTimeOffset]::Parse($Value).UtcDateTime
    } catch { return $null }
}

function Test-OpenedProcessIdentity([System.Diagnostics.Process]$Process, [object]$Expected) {
    try {
        if ($null -eq $Process -or $null -eq $Expected -or [int]$Process.Id -ne [int]$Expected.processId) { return $false }
        $openedStart = $Process.StartTime.ToUniversalTime()
        $expectedStart = Convert-ProcessCreationDate ([string]$Expected.creationDate)
        if ($null -eq $expectedStart) { return $false }
        [math]::Abs(($openedStart - $expectedStart).TotalMilliseconds) -le 1
    } catch { $false }
}

function Get-KnownProcessResidualError([object[]]$Records) {
    $remaining = [System.Collections.Generic.List[int]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($record in @($Records)) {
        $result = Get-ProcessIdentityResult ([int]$record.processId)
        if ($result.error) { $errors.Add($result.error); continue }
        if (-not $result.found) { continue }
        if (Test-ProcessIdentity $result.identity $record) { $remaining.Add([int]$record.processId) }
        else { $errors.Add("Process identity changed for PID $($record.processId); no kill was attempted.") }
    }
    if ($remaining.Count -gt 0) { $errors.Add("Previously observed owned processes remain: $($remaining -join ',').") }
    @($errors | Sort-Object -Unique) -join ' '
}

function Test-ProcessIdentity([object]$Actual, [object]$Expected) {
    $null -ne $Actual -and $null -ne $Expected -and [int]$Actual.processId -eq [int]$Expected.processId -and [string]$Actual.creationDate -eq [string]$Expected.creationDate
}

function Get-ProcessTreeSnapshot([int[]]$RootIds) {
    $all = $null
    try { $all = @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId, CreationDate, Name) }
    catch { return [pscustomobject]@{ success = $false; rootAvailable = $false; records = @(); error = "Process-tree enumeration failed: $($_.Exception.Message)" } }
    if (@($all | Where-Object { $null -eq $_.ProcessId -or $null -eq $_.ParentProcessId -or [string]::IsNullOrWhiteSpace([string]$_.CreationDate) }).Count -gt 0) {
        return [pscustomobject]@{ success = $false; rootAvailable = $false; records = @(); error = 'Process-tree enumeration returned incomplete identity records.' }
    }
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($root in $RootIds) { [void]$ids.Add([int]$root) }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $all) {
            if ($ids.Contains([int]$process.ParentProcessId) -and $ids.Add([int]$process.ProcessId)) {
                if ($ids.Count -gt $MaxProcessTreeCount) {
                    return [pscustomobject]@{ success = $false; rootAvailable = $false; records = @(); error = "Owned process tree exceeded MaxProcessTreeCount=$MaxProcessTreeCount." }
                }
                $changed = $true
            }
        }
    }
    $records = @($all | Where-Object { $ids.Contains([int]$_.ProcessId) } | ForEach-Object {
        [pscustomobject]@{ processId = [int]$_.ProcessId; parentProcessId = [int]$_.ParentProcessId; creationDate = [string]$_.CreationDate; name = [string]$_.Name }
    })
    [pscustomobject]@{
        success = $true
        rootAvailable = (@($records | Where-Object { $RootIds -contains $_.processId }).Count -gt 0)
        records = $records
        error = $null
    }
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
    $snapshot = Get-ProcessTreeSnapshot $RootIds
    if (-not $snapshot.success) {
        return [pscustomobject]@{ ids = @(); records = @(); rootAvailable = $false; processTreeAvailable = $false; processTreeError = $snapshot.error; cpuMs = 0.0; cpuAvailable = $false; io = [pscustomobject]@{ available = $false; error = $snapshot.error }; at = [DateTimeOffset]::UtcNow }
    }
    $records = @($snapshot.records)
    $ids = @($records | ForEach-Object processId)
    $cpu = 0.0
    $cpuSamples = 0
    foreach ($id in $ids) {
        try { $cpu += (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime.TotalMilliseconds; $cpuSamples++ } catch { }
    }
    $rootAvailable = $false
    $rootAvailable = $snapshot.rootAvailable
    $io = Get-IoRate ([System.Collections.Generic.HashSet[int]]::new([int[]]$ids))
    [pscustomobject]@{ ids = $ids; records = $records; rootAvailable = $rootAvailable; processTreeAvailable = $true; processTreeError = $null; cpuMs = $cpu; cpuAvailable = ($rootAvailable -and $cpuSamples -gt 0); io = $io; at = [DateTimeOffset]::UtcNow }
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
        $rootIdentity = Get-ProcessIdentity $process.Id
        $initialTree = Get-ProcessTreeSnapshot @($process.Id)
        [pscustomobject]@{
            process = $process
            rootIdentity = $rootIdentity
            processTreeAvailable = $initialTree.success
            processTreeError = $initialTree.error
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
            ownedProcessIds = @($initialTree.records | ForEach-Object processId)
            ownedProcessRecords = @($initialTree.records)
            lastSampleAt = $startedAt
            samples = 0
            successfulSamples = 0
        }
    } catch {
        $primary = $_.Exception.Message
        $cleanup = $null
        try {
            if (-not $process.HasExited) { $cleanup = Stop-ProcessSafely -Process $process -ExpectedIdentity (Get-ProcessIdentity $process.Id) -Forced }
        } catch { $cleanup = [pscustomobject]@{ exited = $false; error = $_.Exception.Message } }
        $cleanupFailed = ($null -eq $cleanup) -or (-not [bool]$cleanup.exited) -or (@($cleanup.remainingIds).Count -gt 0) -or $cleanup.error
        if ($cleanupFailed) {
            $cleanupMessage = if ($cleanup -and $cleanup.error) { [string]$cleanup.error } elseif ($cleanup) { "remaining owned processes: $(@($cleanup.remainingIds) -join ',')" } else { 'cleanup result was unavailable' }
            throw "API shard launch failed: $primary Cleanup failure: $cleanupMessage"
        }
        throw $primary
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
                    if (-not $sample.processTreeAvailable) {
                        $shard.processTreeAvailable = $false
                        $shard.processTreeError = $sample.processTreeError
                        $shard.cpuAvailable = $false
                        $shard.ioAvailable = $false
                        continue
                    }
                    if (-not $sample.rootAvailable) { continue }
                    $shard.processTreeAvailable = $true
                    $shard.ownedProcessIds = @($shard.ownedProcessIds + $sample.ids | Sort-Object -Unique)
                    $shard.ownedProcessRecords = @($shard.ownedProcessRecords + $sample.records | Sort-Object processId, creationDate -Unique)
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
                    $cleanup = Stop-ProcessSafely -Process $shard.process -ExpectedIdentity $shard.rootIdentity -KnownRecords @($shard.ownedProcessRecords) -Forced
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
                    $cleanup = Stop-ProcessSafely -Process $shard.process -ExpectedIdentity $shard.rootIdentity -KnownRecords @($shard.ownedProcessRecords) -Forced
                    if (-not $cleanup.exited) { $shard.cleanupFailure = "Owned process tree was not force-cleaned: $($cleanup.remainingIds -join ',')" }
                    if ($cleanup.error) { $shard.cleanupFailure = $cleanup.error }
                } elseif (@($shard.ownedProcessRecords).Count -gt 0) {
                    $residualError = Get-KnownProcessResidualError @($shard.ownedProcessRecords)
                    if ($residualError) { $shard.cleanupFailure = $residualError }
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
        [object]$ConditionMetadata,
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
                    $cleanup = Stop-ProcessSafely -Process $shard.process -ExpectedIdentity $shard.rootIdentity -KnownRecords @($shard.ownedProcessRecords) -Forced
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
        if (-not $shard.processTreeAvailable -or $shard.processTreeError) { $invalidReasons.Add("shard $($shard.shard) process-tree enumeration unavailable: $($shard.processTreeError)") }
        if ($shard.successfulSamples -eq 0) { $invalidReasons.Add("shard $($shard.shard) had no successful active process sample") }
        if ([double]$shard.wallMs -le 0) { $invalidReasons.Add("shard $($shard.shard) had non-positive wall time") }
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
            processTreeAvailable = $shard.processTreeAvailable
            processTreeError = $shard.processTreeError
            rootIdentity = $shard.rootIdentity
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
    if ($metrics.worstShardWallMs -le 0 -or $metrics.summedShardWallMs -le 0) { $invalidReasons.Add('observation wall-time metrics were non-positive') }
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
        finalWorktree = (Get-RepoInfo $Worktree)
        conditionMetadata = $ConditionMetadata
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

function Get-ConditionSummary([string]$Condition, [object[]]$Observations, [int]$RequiredRuns, [object]$Manifest, [object]$ConditionMetadata) {
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
        manifest = Get-ManifestFacts $Manifest
        conditionMetadata = $ConditionMetadata
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
    $summaryManifest = $Summary.manifest
    $summaryMetadata = $Summary.conditionMetadata
    if ($null -eq $summaryManifest -or [string]::IsNullOrWhiteSpace([string]$summaryManifest.manifestHash)) { $errors.Add("$ExpectedCondition summary is missing its canonical manifest facts.") }
    $caseNames = @($summaryManifest.caseNames | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ } | Sort-Object)
    if ($caseNames.Count -eq 0) {
        $errors.Add("$ExpectedCondition summary is missing its full sorted case-name manifest.")
    } else {
        $manifestHashInput = (($caseNames -join "`n") + "`n")
        $manifestHashProvider = [System.Security.Cryptography.SHA256]::Create()
        try { $recomputedManifestHash = ([System.BitConverter]::ToString($manifestHashProvider.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($manifestHashInput)))).Replace('-', '').ToLowerInvariant() } finally { $manifestHashProvider.Dispose() }
        if ($recomputedManifestHash -ne [string]$summaryManifest.manifestHash) { $errors.Add("$ExpectedCondition summary manifestHash does not reconcile with its full case-name manifest.") }
        if ($summaryManifest.caseCount -and [int]$summaryManifest.caseCount -ne $caseNames.Count) { $errors.Add("$ExpectedCondition summary caseCount does not reconcile with its full case-name manifest.") }
    }
    if ($null -eq $summaryMetadata -or [string]::IsNullOrWhiteSpace([string]$summaryMetadata.head) -or [string]::IsNullOrWhiteSpace([string]$summaryMetadata.path) -or [string]::IsNullOrWhiteSpace([string]$summaryMetadata.environmentFingerprint) -or -not [bool]$summaryMetadata.cleanAtStart) { $errors.Add("$ExpectedCondition summary is missing condition identity metadata.") }
    if ($summaryMetadata -and [string]$summaryMetadata.condition -ne $ExpectedCondition) { $errors.Add("$ExpectedCondition summary condition metadata is inconsistent.") }
    $required = 0
    if (-not [int]::TryParse(([string]$Summary.requiredRuns), [ref]$required) -or $required -lt 1) { $errors.Add("$ExpectedCondition summary has an invalid requiredRuns value.") }
    elseif ($required -ne 10) { $errors.Add("$ExpectedCondition summary must contain exactly 10 required runs.") }
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
        $observationMetadata = $observation.conditionMetadata
        $finalWorktree = $observation.finalWorktree
        if ($null -eq $observationMetadata -or [string]$observationMetadata.head -ne [string]$summaryMetadata.head -or
            [string]$observationMetadata.path -ne [string]$summaryMetadata.path -or
            [string]$observationMetadata.environmentFingerprint -ne [string]$summaryMetadata.environmentFingerprint -or
            [string]$observationMetadata.manifest.manifestHash -ne [string]$summaryManifest.manifestHash) {
            $errors.Add("Observation seed '$seedText' condition metadata does not match its summary.")
            $evidenceValid = $false
        }
        if ($null -eq $finalWorktree -or [string]$finalWorktree.path -ne [string]$summaryMetadata.path -or
            [string]$finalWorktree.head -ne [string]$summaryMetadata.head -or -not [bool]$finalWorktree.clean) {
            $errors.Add("Observation seed '$seedText' final worktree state is not the expected clean SHA/path.")
            $evidenceValid = $false
        }
        if ($null -eq $observation.partition) { $errors.Add("Observation seed '$seedText' is missing its saved partition."); $evidenceValid = $false }
        elseif ([string]$observation.partition.seed -ne $seedText) { $errors.Add("Observation seed '$seedText' partition seed does not match."); $evidenceValid = $false }
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
            if (-not [bool]$shard.cpuAvailable -or -not [bool]$shard.ioAvailable -or [int]$shard.successfulSamples -le 0 -or -not [bool]$shard.processTreeAvailable -or $shard.processTreeError) { $evidenceMetricsComplete = $false }
            if ($shard.cleanupFailure -or $shard.waitError -or @($shard.errorSignals).Count -gt 0) { $evidenceValid = $false }
            if (-not (Test-NonNegativeNumber $shard.wallMs) -or [double]$shard.wallMs -le 0) { $evidenceValid = $false; $errors.Add("Observation seed '$seedText' has non-positive shard wall time.") }
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
        if ([double]$derivedMetrics.worstShardWallMs -le 0 -or [double]$derivedMetrics.summedShardWallMs -le 0) { $evidenceValid = $false; $errors.Add("Observation seed '$seedText' has non-positive aggregate wall time.") }
        $normalized.Add([pscustomobject]@{
            seed = $seedText
            valid = $actualValid
            metricsComplete = $evidenceMetricsComplete
            metrics = $observation.metrics
            partition = $observation.partition
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
        manifest = $summaryManifest
        conditionMetadata = $summaryMetadata
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
    if ([string]$base.manifest.manifestHash -ne [string]$treat.manifest.manifestHash) { $validationErrors += 'Baseline and treatment canonical manifest hashes differ.' }
    if ([int]$base.manifest.caseCount -ne [int]$treat.manifest.caseCount -or [int]$base.manifest.classCount -ne [int]$treat.manifest.classCount) { $validationErrors += 'Baseline and treatment manifest counts differ.' }
    if ([string]$base.conditionMetadata.head -eq [string]$treat.conditionMetadata.head) { $validationErrors += 'Baseline and treatment SHAs must be distinct.' }
    if ([string]$base.conditionMetadata.path -eq [string]$treat.conditionMetadata.path) { $validationErrors += 'Baseline and treatment paths must be distinct.' }
    if ([string]$base.conditionMetadata.environmentFingerprint -ne [string]$treat.conditionMetadata.environmentFingerprint) { $validationErrors += 'Baseline and treatment environment fingerprints differ.' }
    $basePartitions = @{}
    foreach ($observation in $base.observations) { $basePartitions[[string]$observation.seed] = $observation.partition | ConvertTo-Json -Depth 20 -Compress }
    $treatmentPartitions = @{}
    foreach ($observation in $treat.observations) { $treatmentPartitions[[string]$observation.seed] = $observation.partition | ConvertTo-Json -Depth 20 -Compress }
    if (Compare-Object @($basePartitions.Keys | Sort-Object) @($treatmentPartitions.Keys | Sort-Object)) { $validationErrors += 'Baseline and treatment partition seed sets differ.' }
    foreach ($seed in @($basePartitions.Keys)) {
        if ($treatmentPartitions.ContainsKey($seed) -and $basePartitions[$seed] -ne $treatmentPartitions[$seed]) { $validationErrors += "Partition differs for seed '$seed'." }
    }
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
        if ($treatmentWall -le 0) { $validationErrors += "Treatment seed '$seed' has a non-positive worst-shard wall time."; continue }
        $paired += 1 - ($treatmentWall / $baseWall)
    }
    foreach ($value in $baseValues) { if ($value -le 0) { $validationErrors += 'A baseline worst-shard wall time is non-positive.' } }
    foreach ($value in $treatmentValues) { if ($value -le 0) { $validationErrors += 'A treatment worst-shard wall time is non-positive.' } }
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

function Get-CanonicalPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrEmpty($root)) { Fail "Could not determine a filesystem root for path: $Path" }
    $components = @($full.Substring($root.Length) -split '[\\/]+' | Where-Object { $_ -ne '' })
    $current = $root
    $visited = @{}
    for ($depth = 0; $depth -lt 64; $depth++) {
        $restarted = $false
        for ($index = 0; $index -lt $components.Count; $index++) {
            $candidate = Join-Path $current $components[$index]
            if (-not (Test-Path -LiteralPath $candidate)) {
                $tail = @($components[$index..($components.Count - 1)]) -join [System.IO.Path]::DirectorySeparatorChar
                $result = Join-Path $current $tail
                return $result.TrimEnd('\', '/')
            }
            $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
            $linkType = [string]$item.LinkType
            $isReparse = ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            if (-not $isReparse -and [string]::IsNullOrEmpty($linkType)) {
                $current = [string]$item.FullName
                continue
            }
            $targetValues = @($item.Target | Where-Object { -not [string]::IsNullOrEmpty([string]$_) })
            $target = if ($targetValues.Count -gt 0) { [string]$targetValues[0] } else { [string](Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path }
            if (-not [System.IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path -Parent $candidate) $target }
            $target = [System.IO.Path]::GetFullPath($target)
            $remainingComponents = if (($index + 1) -lt $components.Count) { @($components[($index + 1)..($components.Count - 1)]) } else { @() }
            $remaining = $remainingComponents -join '|'
            $visitKey = ("{0}|{1}" -f $target.ToLowerInvariant(), $remaining.ToLowerInvariant())
            if ($visited.ContainsKey($visitKey)) { Fail "Reparse-point resolution loop detected at: $candidate" }
            $visited[$visitKey] = $true
            $current = $target
            $components = $remainingComponents
            $restarted = $true
            break
        }
        if (-not $restarted) {
            if ($current -match '^[A-Za-z]:\\$' -or $current -match '^\\\\[^\\]+\\[^\\]+\\$') { return $current }
            return $current.TrimEnd('\', '/')
        }
    }
    Fail "Reparse-point resolution exceeded the component/depth bound for: $Path"
}

function Assert-OutputOutsideWorktrees([string]$OutputPath, [object[]]$Worktrees) {
    $fullOutput = Get-CanonicalPath $OutputPath
    foreach ($worktree in $Worktrees) {
        $fullWorktree = Get-CanonicalPath ([string]$worktree.path)
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
        baselineConditionMetadata = $Manifest.baselineMetadata
        treatmentConditionMetadata = $Manifest.treatmentMetadata
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
    if ($MaxProcessTreeCount -lt 1) { Fail 'MaxProcessTreeCount must be positive.' }
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
    if (-not $IsWindows) { Fail 'This harness is intentionally Windows-only.' }
    if ($Mode -eq 'Evaluate') {
        if (-not $BaselineSummaryPath -or -not $TreatmentSummaryPath) { Fail 'Evaluate mode requires BaselineSummaryPath and TreatmentSummaryPath.' }
        $baseline = Get-Content -Raw -LiteralPath $BaselineSummaryPath | ConvertFrom-Json
        $treatment = Get-Content -Raw -LiteralPath $TreatmentSummaryPath | ConvertFrom-Json
        $baselineRecordedPath = [string]$baseline.conditionMetadata.path
        $treatmentRecordedPath = [string]$treatment.conditionMetadata.path
        if ([string]::IsNullOrWhiteSpace($baselineRecordedPath) -or [string]::IsNullOrWhiteSpace($treatmentRecordedPath) -or
            -not (Test-Path -LiteralPath $baselineRecordedPath -PathType Container) -or
            -not (Test-Path -LiteralPath $treatmentRecordedPath -PathType Container)) {
            Fail 'Evaluate requires both recorded condition worktree paths to exist before writing output.'
        }
        $liveBaselineInfo = Get-RepoInfo $baselineRecordedPath
        $liveTreatmentInfo = Get-RepoInfo $treatmentRecordedPath
        Assert-OutputOutsideWorktrees $OutputRoot @($liveBaselineInfo, $liveTreatmentInfo)
        Assert-EmptyOutput $OutputRoot 'Evaluate'
        foreach ($pair in @(@($baseline, $liveBaselineInfo, 'baseline'), @($treatment, $liveTreatmentInfo, 'treatment'))) {
            $summary = $pair[0]
            $liveInfo = $pair[1]
            $condition = $pair[2]
            if (-not $liveInfo.clean) { Fail "Evaluate $condition worktree is dirty; no decision output was written." }
            if ([string]$liveInfo.head -ne [string]$summary.conditionMetadata.head) { Fail "Evaluate $condition worktree HEAD no longer matches the recorded SHA." }
            if (-not $liveInfo.path.Equals([string]$summary.conditionMetadata.path, [StringComparison]::OrdinalIgnoreCase)) { Fail "Evaluate $condition worktree path no longer matches the recorded canonical path." }
            $liveManifest = Get-TestManifest $liveInfo.path $null
            Assert-SummaryManifestMatchesLive $summary.manifest $liveManifest $condition
            Assert-SummaryManifestMatchesLive $summary.conditionMetadata.manifest $liveManifest "$condition condition metadata"
            Assert-SummaryPartitionsMatchLive $summary $liveManifest $condition
            $liveEnvironment = Get-EnvironmentInfo $liveInfo.path
            $liveFingerprint = Get-EnvironmentFingerprint $liveEnvironment
            if ([string]$liveFingerprint -ne [string]$summary.conditionMetadata.environmentFingerprint) { Fail "Evaluate $condition environment fingerprint no longer matches the recorded evidence." }
        }
        $decision = Get-Decision $baseline $treatment
        Write-JsonFile (Join-Path $OutputRoot 'decision.json') $decision
        $decision | ConvertTo-Json -Depth 20
        return
    }
    if (-not $BaselinePath) { $BaselinePath = (Get-Location).Path }
    if (-not $TreatmentPath) { $TreatmentPath = $BaselinePath }
    if ($Mode -eq 'Run' -and $Runs -ne 10) { Fail 'Run mode requires exactly 10 measured observations.' }
    if ($Mode -eq 'Run' -and $TestListPath) { Fail 'Run mode requires live discovery; TestListPath is allowed only for Plan contract smoke.' }
    if ($Mode -eq 'Run' -and $SkipBuild) { Fail 'Run mode always restores and builds each exact clean SHA before live test discovery; SkipBuild is not allowed.' }
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
    if ($Mode -eq 'Run') {
        if (-not $baselineInfo.clean -or -not $treatmentInfo.clean) { Fail 'Run mode requires clean baseline and treatment worktrees before restore/build.' }
        Assert-EmptyOutput $OutputRoot 'Run'
        foreach ($condition in @(@('baseline', $BaselinePath), @('treatment', $TreatmentPath))) {
            if ($condition[0] -eq 'baseline') { Assert-WorktreeStable $baselineInfo } else { Assert-WorktreeStable $treatmentInfo }
            $restore = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('restore', (Join-Path $condition[1] $SolutionPath)) -WorkingDirectory $condition[1]
            if ($restore.ExitCode -ne 0) { Fail "$($condition[0]) restore failed.`n$($restore.Stdout)`n$($restore.Stderr)" }
            if ($condition[0] -eq 'baseline') { Assert-WorktreeStable $baselineInfo } else { Assert-WorktreeStable $treatmentInfo }
            $build = Invoke-CapturedProcess -FileName $DotnetExecutable -Arguments @('build', (Join-Path $condition[1] $SolutionPath), '--configuration', 'Release', '--no-restore') -WorkingDirectory $condition[1]
            if ($build.ExitCode -ne 0) { Fail "$($condition[0]) build failed.`n$($build.Stdout)`n$($build.Stderr)" }
            if ($condition[0] -eq 'baseline') { Assert-WorktreeStable $baselineInfo } else { Assert-WorktreeStable $treatmentInfo }
        }
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
    $baselineMetadata = [ordered]@{
        condition = 'baseline'
        path = $baselineInfo.path
        head = $baselineInfo.head
        cleanAtStart = $baselineInfo.clean
        manifest = Get-ManifestFacts $baselineManifest
        environmentFingerprint = Get-EnvironmentFingerprint $environment.baseline
    }
    $treatmentMetadata = [ordered]@{
        condition = 'treatment'
        path = $treatmentInfo.path
        head = $treatmentInfo.head
        cleanAtStart = $treatmentInfo.clean
        manifest = Get-ManifestFacts $treatmentManifest
        environmentFingerprint = Get-EnvironmentFingerprint $environment.treatment
    }
    $partitions = @($Seeds[0..($Runs - 1)] | ForEach-Object { New-Partition $baselineManifest.classes $_ $ShardCount })
    if ($Mode -eq 'Run') {
        Assert-WorktreeStable $baselineInfo
        Assert-WorktreeStable $treatmentInfo
    }
    $manifest = [pscustomobject]@{ baseline = $baselineManifest; treatment = $treatmentManifest; baselineMetadata = $baselineMetadata; treatmentMetadata = $treatmentMetadata }
    if ($Mode -eq 'Plan') {
        Assert-EmptyOutput $OutputRoot 'Plan'
        $plan = New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions 'Plan'
        $plan | ConvertTo-Json -Depth 20
        return
    }

    $plan = New-Plan $OutputRoot $baselineInfo $treatmentInfo $manifest $partitions 'Run'
    Assert-WorktreeStable $baselineInfo
    Assert-WorktreeStable $treatmentInfo
    Assert-WorktreeStable $baselineInfo
    Assert-WorktreeStable $treatmentInfo
    $all = @{ baseline = [System.Collections.Generic.List[object]]::new(); treatment = [System.Collections.Generic.List[object]]::new() }
    if ($Warmup) {
        $warmupPartition = $partitions[0]
        Assert-WorktreeStable $baselineInfo
        Assert-WorktreeStable $treatmentInfo
        $warmupBaseline = Invoke-Observation -Condition baseline -Worktree $BaselinePath -Partition $warmupPartition -RunNumber 0 -Seed $warmupPartition.seed -Root $OutputRoot -Order @('baseline') -Environment $environment -ConditionMetadata $baselineMetadata -IsWarmup
        if (-not $warmupBaseline.valid) { Fail 'Baseline warmup was invalid; no measured observations were started.' }
        Assert-WorktreeStable $baselineInfo
        Assert-WorktreeStable $treatmentInfo
        $warmupTreatment = Invoke-Observation -Condition treatment -Worktree $TreatmentPath -Partition $warmupPartition -RunNumber 0 -Seed $warmupPartition.seed -Root $OutputRoot -Order @('treatment') -Environment $environment -ConditionMetadata $treatmentMetadata -IsWarmup
        if (-not $warmupTreatment.valid) { Fail 'Treatment warmup was invalid; no measured observations were started.' }
    }
    for ($index = 0; $index -lt $Runs; $index++) {
        $partition = $partitions[$index]
        $order = if (($index % 2) -eq 0) { @('baseline', 'treatment') } else { @('treatment', 'baseline') }
        foreach ($condition in $order) {
            Assert-WorktreeStable $baselineInfo
            Assert-WorktreeStable $treatmentInfo
            $path = if ($condition -eq 'baseline') { $BaselinePath } else { $TreatmentPath }
            $metadata = if ($condition -eq 'baseline') { $baselineMetadata } else { $treatmentMetadata }
            $observation = Invoke-Observation -Condition $condition -Worktree $path -Partition $partition -RunNumber ($index + 1) -Seed $partition.seed -Root $OutputRoot -Order $order -Environment $environment -ConditionMetadata $metadata
            $all[$condition].Add($observation)
            Assert-WorktreeStable $baselineInfo
            Assert-WorktreeStable $treatmentInfo
        }
    }
    Assert-WorktreeStable $baselineInfo
    Assert-WorktreeStable $treatmentInfo
    $baselineSummary = Get-ConditionSummary baseline @($all.baseline) $Runs $baselineManifest $baselineMetadata
    $treatmentSummary = Get-ConditionSummary treatment @($all.treatment) $Runs $treatmentManifest $treatmentMetadata
    $decision = Get-Decision $baselineSummary $treatmentSummary
    Write-JsonFile (Join-Path $OutputRoot 'baseline-summary.json') $baselineSummary
    Write-JsonFile (Join-Path $OutputRoot 'treatment-summary.json') $treatmentSummary
    Write-JsonFile (Join-Path $OutputRoot 'summary.json') ([ordered]@{ schemaVersion = 'aerolink-api-host-reuse-measurement/v1'; environment = $environment; finalBaseline = Get-RepoInfo $BaselinePath; finalTreatment = Get-RepoInfo $TreatmentPath; baseline = $baselineSummary; treatment = $treatmentSummary; decision = $decision })
    $decision | ConvertTo-Json -Depth 20
}

Main
