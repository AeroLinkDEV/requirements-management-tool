import { spawnSync } from 'node:child_process'

/**
 * #986: Chromium on the hosted Windows runner intermittently fails a loopback load with net::ERR_NO_BUFFER_SPACE
 * (WSAENOBUFS), about once per hundred browser jobs. The runner's socket state at that moment is the evidence
 * nobody has, and a passing retry skips the diagnostic upload. So on a failed attempt this prints a one-line
 * summary of the host's TCP state straight into the job log, which is always retained. Counts and process names
 * only: no remote endpoints. It changes no retry, timeout or assertion.
 */
export const SNAPSHOT_SCRIPT = String.raw`
$ErrorActionPreference = 'SilentlyContinue'
$tcp = @(Get-NetTCPConnection)
$setting = Get-NetTCPSetting -SettingName Internet
$start = [int]$setting.DynamicPortRangeStartPort
$count = [int]$setting.DynamicPortRangeNumberOfPorts
$states = [ordered]@{}
$tcp | Group-Object State | Sort-Object Count -Descending | ForEach-Object { $states[[string]$_.Name] = $_.Count }
$ephemeral = @($tcp | Where-Object { $_.LocalPort -ge $start -and $_.LocalPort -lt ($start + $count) }).Count
$owners = [ordered]@{}
$tcp | Where-Object { $_.State -ne 'Listen' -and $_.OwningProcess -gt 0 } | Group-Object OwningProcess |
  Sort-Object Count -Descending | Select-Object -First 6 | ForEach-Object {
    $name = (Get-Process -Id ([int]$_.Name)).ProcessName
    $owners["$name#$($_.Name)"] = $_.Count
  }
[ordered]@{
  total = $tcp.Count; states = $states; dynamicPorts = [ordered]@{ start = $start; count = $count; inUse = $ephemeral }
  udpEndpoints = @(Get-NetUDPEndpoint).Count; topOwners = $owners
} | ConvertTo-Json -Compress -Depth 4
`

const runPowerShell = script => {
  // Windows PowerShell 5.1 owns the NetTCPIP cmdlets; a PowerShell 7 parent's module path would shadow them.
  const env = { ...process.env }
  delete env.PSModulePath
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8', timeout: 20_000, windowsHide: true, env,
  })
  if (result.error) return { error: result.error.message }
  if (result.status !== 0) return { error: `exit ${result.status}` }
  try { return JSON.parse(result.stdout.trim()) } catch { return { error: 'unparseable snapshot' } }
}

export default class SocketSnapshotReporter {
  constructor({ enabled = process.env.CI === 'true' && process.platform === 'win32', limit = 5, capture = runPowerShell, log = console.log } = {}) {
    this.enabled = enabled
    this.remaining = limit
    this.capture = capture
    this.log = log
  }

  onTestEnd(test, result) {
    if (!this.enabled || this.remaining <= 0) return
    if (result.status === 'passed' || result.status === 'skipped') return
    this.remaining -= 1
    const started = Date.now()
    const snapshot = this.capture(SNAPSHOT_SCRIPT)
    const title = test.titlePath().filter(Boolean).slice(-2).join(' › ')
    this.log(`[socket-snapshot] ${title} (attempt ${result.retry + 1}, ${result.status}, ${Date.now() - started} ms): ${JSON.stringify(snapshot)}`)
  }

  printsToStdio() { return false }
}
