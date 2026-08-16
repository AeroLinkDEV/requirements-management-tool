[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$HarnessPath
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $HarnessPath 'product/tools/measure-api-host-reuse.ps1'
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
    throw "Measurement harness not found: $script"
}

$source = [IO.File]::ReadAllText($script)
$oldCleanup = @'
            try {
                if ($shard.job) {
                    $cleanup = Stop-JobContainedProcess -Launch $shard.job -Terminate:(!$shard.process.HasExited) -TimeoutMilliseconds 5000
'@
$newCleanup = @'
            $processExitedBeforeCleanup = $false
            try {
                $processExitedBeforeCleanup = $shard.process.HasExited
                if ($processExitedBeforeCleanup) {
                    $shard.process.WaitForExit()
                    $shard.exitCode = [int]$shard.process.ExitCode
                } elseif ($shard.timedOut) {
                    $shard.exitCode = 124
                }
            } catch {
                $shard.exitCode = $null
                $exitCaptureError = "exit-code capture failed before cleanup: $($_.Exception.Message)"
                $shard.waitError = if ($shard.waitError) { "$($shard.waitError); $exitCaptureError" } else { $exitCaptureError }
            }
            try {
                if ($shard.job) {
                    $cleanup = Stop-JobContainedProcess -Launch $shard.job -Terminate:(!$processExitedBeforeCleanup) -TimeoutMilliseconds 5000
'@
$oldTail = @'
            $shard.endedAt = [DateTimeOffset]::UtcNow
            try { $shard.exitCode = $shard.process.ExitCode } catch { $shard.exitCode = $null }
            $shard.wallMs = ($shard.endedAt - $shard.shardStartedAt).TotalMilliseconds
'@
$newTail = @'
            $shard.endedAt = [DateTimeOffset]::UtcNow
            $shard.wallMs = ($shard.endedAt - $shard.shardStartedAt).TotalMilliseconds
'@

if (($source.Split($oldCleanup).Count - 1) -ne 1) {
    throw 'Expected cleanup anchor exactly once in merged harness.'
}
if (($source.Split($oldTail).Count - 1) -ne 1) {
    throw 'Expected post-cleanup ExitCode anchor exactly once in merged harness.'
}

$source = $source.Replace($oldCleanup, $newCleanup).Replace($oldTail, $newTail)
[IO.File]::WriteAllText($script, $source, [Text.UTF8Encoding]::new($false))

$patched = [IO.File]::ReadAllText($script)
$capture = $patched.IndexOf('$shard.exitCode = [int]$shard.process.ExitCode', [StringComparison]::Ordinal)
$cleanup = $patched.IndexOf('Stop-JobContainedProcess -Launch $shard.job -Terminate:(!$processExitedBeforeCleanup)', [StringComparison]::Ordinal)
if ($capture -lt 0 -or $cleanup -lt 0 -or $capture -ge $cleanup) {
    throw 'Observer patch did not place exit-code capture before native cleanup.'
}
if ($patched.Contains('try { $shard.exitCode = $shard.process.ExitCode } catch { $shard.exitCode = $null }')) {
    throw 'Old post-cleanup exit-code capture still exists.'
}

Write-Host 'Patched measurement observer to capture ExitCode before native handle cleanup.'
