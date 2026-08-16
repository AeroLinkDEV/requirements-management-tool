import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const wrapperPath = join(repoRoot, 'product/scripts/Get-AeroLinkTestPlan.ps1')
const wrapper = readFileSync(wrapperPath, 'utf8')
const backupContractPath = join(repoRoot, 'product/scripts/AeroLinkBackupVerification.Tests.ps1')
const backupContract = readFileSync(backupContractPath, 'utf8')
const backupVerifier = readFileSync(join(repoRoot, 'product/scripts/Verify-AeroLinkBackup.ps1'), 'utf8')
const scriptContractNames = [
  'AeroLinkEvidenceStore.Tests.ps1',
  'AeroLinkBackupVerification.Tests.ps1',
  'AeroLinkRestoreContract.Tests.ps1',
  'AeroLinkMigrationPosture.Tests.ps1',
  'AeroLinkRemoteDemo.Tests.ps1',
  'AeroLinkRemoteDemoRecovery.Tests.ps1',
  'Get-AeroLinkTestPlan.Tests.ps1',
]
const ownedProcessProject = join(repoRoot, 'product/test-planner/tools/OwnedProcess/OwnedProcess.csproj')
const ownedProcessSource = readFileSync(join(repoRoot, 'product/test-planner/tools/OwnedProcess/Program.cs'), 'utf8')

test('wrapper exposes bounded non-authoritative timing and CI execution accounting', () => {
  assert.match(wrapper, /schemaVersion\s*=\s*1/)
  assert.match(wrapper, /status\s*=\s*\$executionStatus/)
  assert.match(wrapper, /authoritative\s*=\s*\$false/)
  assert.match(wrapper, /selectedCiJobs\s*=\s*\$selected/)
  assert.match(wrapper, /executedCiJobs\s*=\s*\$executed/)
  assert.match(wrapper, /ciOnlyJobs\s*=\s*\$ciOnly/)
  assert.match(wrapper, /totalMs\s*=\s*\$totalMs/)
  assert.match(wrapper, /Get-PersistentEvidenceFingerprint/)
  assert.match(wrapper, /persistentEvidenceRootTouched\s*=\s*\$persistentEvidenceRootTouched/)
  assert.match(wrapper, /elapsedMs\s*=\s*\[int64\]\$watch\.ElapsedMilliseconds/)
  assert.match(wrapper, /StartNew\(\)/)
})

test('Full mode reaches script contracts and the isolated PostgreSQL boundary only when CI selects them', () => {
  const full = wrapper.slice(wrapper.indexOf('function Invoke-FullPlan'), wrapper.lastIndexOf('if ($DryRun)'))
  assert.match(full, /selectedCiJobs\s+-contains\s+'script-contracts'/)
  assert.match(full, /Invoke-ScriptContractSuite/)
  assert.match(full, /selectedCiJobs\s+-contains\s+'postgresql-smoke'/)
  assert.match(full, /Invoke-DisposablePostgreSqlGate/)
  assert.match(full, /Get-DisposableDockerCommand/)
  assert.match(wrapper, /function Get-DisposableDockerCommand/)
  assert.doesNotMatch(full, /Start-Postgres|54329/)
})

test('disposable PostgreSQL commands are uniquely labeled, loopback-bound, and owner-checked before cleanup', () => {
  const gate = wrapper.slice(wrapper.indexOf('function Invoke-DisposablePostgreSqlGate'))
  assert.match(gate, /NewGuid/)
  assert.match(gate, /volume', 'create', '--label'/)
  assert.match(gate, /'--name', \$containerName/)
  assert.match(gate, /'--label', "\$labelKey=\$runId"/)
  assert.match(gate, /'--publish', '127\.0\.0\.1::5432'/)
  assert.match(gate, /NetworkSettings\.Ports/)
  assert.match(gate, /HostIp -ne '127\.0\.0\.1'/)
  assert.match(gate, /HostPort -notmatch '\^\[1-9\]/)
  assert.doesNotMatch(gate, /'--env'/)
  assert.match(gate, /Get-RestrictedSecretFile/)
  assert.match(wrapper, /SetAccessRuleProtection/)
  assert.match(gate, /127\.0\.0\.1:0/)
  assert.match(wrapper, /Get-NetTCPConnection/)
  assert.match(gate, /Get-BoundedListenerConnections/)
  assert.match(gate, /Get-CimInstance Win32_Process/)
  assert.match(gate, /apiOwnershipIntent/)
  assert.match(gate, /postgres:17/)
  assert.match(gate, /finally\s*\{/)
  assert.doesNotMatch(gate, /Get-FreeLoopbackPort|hostApiPort|Start-Process|Stop-Process|containerStarted|volumeCreated/)
  assert.match(gate, /WaitForExit\(10000\)/)
  assert.match(gate, /\$helper\.ExitCode -ne 0/)
  assert.match(gate, /Invoke-SafeApiRequest/)
  assert.ok(gate.indexOf('if (-not $listenerOwned)') < gate.indexOf('Invoke-SafeApiRequest'))
  assert.ok(gate.indexOf('$containerIntent = $true') < gate.indexOf("'start-container'"))
  assert.match(gate, /cleanupErrors/)
  assert.match(wrapper, /Config\.Labels.*com\.aerolink\.planner\.run/)
  assert.match(gate, /Remove-DockerOwnedResource/)
  assert.match(gate, /secretFileIntent/)
  assert.match(wrapper, /Docker is unavailable.*not-proven/)
})

test('owned process boundary uses suspended job assignment and proves a real space-path argv fixture', () => {
  assert.match(ownedProcessSource, /CreateSuspended/)
  assert.match(ownedProcessSource, /AssignProcessToJobObject/)
  assert.match(ownedProcessSource, /ResumeThread/)
  assert.match(ownedProcessSource, /JobObjectLimitKillOnJobClose/)
  assert.match(ownedProcessSource, /TerminateJobObject/)
  assert.match(ownedProcessSource, /QueryJobProcessCount/)
  assert.match(ownedProcessSource, /handles=closed/)
  assert.match(ownedProcessSource, /StringBuilder\(Quote\(executable\)/)
  assert.match(ownedProcessSource, /SafeFileHandle\(childRead, false\)/)
  assert.match(ownedProcessSource, /SpacePathSelfTest/)
  execFileSync('dotnet', ['build', ownedProcessProject, '--configuration', 'Release'], { cwd: repoRoot, stdio: 'ignore' })
  execFileSync('dotnet', ['run', '--project', ownedProcessProject, '--configuration', 'Release', '--no-build', '--no-restore', '--', '--self-test-space-path'], { cwd: repoRoot, stdio: 'ignore' })
})

test('owned process boundary kills late descendants before bounded pipe capture and exercises native fault paths', () => {
  assert.match(ownedProcessSource, /DrainJobAfterRootExit/)
  assert.match(ownedProcessSource, /capture\.Wait\(TimeSpan\.FromSeconds\(5\)\)/)
  assert.match(ownedProcessSource, /CLEANUP\|handles=failed/)
  execFileSync('dotnet', ['run', '--project', ownedProcessProject, '--configuration', 'Release', '--no-build', '--no-restore', '--', '--self-test-late-child'], { cwd: repoRoot, stdio: 'ignore' })
  for (const fault of ['create-job', 'set-job', 'close-job-create', 'create-pipe', 'set-handle', 'close-child-write', 'assign', 'resume', 'terminate-process', 'wait', 'exit-code', 'process-times', 'process-id', 'terminate-job', 'query-job', 'close-child-read-final', 'close-thread', 'close-process', 'close-job', 'capture-timeout', 'cancel-capture']) {
    execFileSync('dotnet', ['run', '--project', ownedProcessProject, '--configuration', 'Release', '--no-build', '--no-restore', '--', '--self-test-fault', fault], { cwd: repoRoot, stdio: 'ignore' })
  }
})

test('owned process boundary authenticates natural exits and controlled stop', () => {
  execFileSync('dotnet', ['run', '--project', ownedProcessProject, '--configuration', 'Release', '--no-build', '--no-restore', '--', '--self-test-exit-codes'], { cwd: repoRoot, stdio: 'ignore' })
  const fixture = mkdtempSync(join(tmpdir(), 'aerolink-owned-stop-'))
  const status = join(fixture, 'status.log')
  const stdout = join(fixture, 'stdout.log')
  const stderr = join(fixture, 'stderr.log')
  const env = join(fixture, 'environment.env')
  try {
    writeFileSync(env, 'AEROLINK_STOP_TEST=1\r\n')
    execFileSync('dotnet', [
      'run', '--project', ownedProcessProject, '--configuration', 'Release', '--no-build', '--no-restore', '--',
      '--executable', process.env.ComSpec ?? 'cmd.exe', '--arg', '/d', '--arg', '/c', '--arg', 'ping -n 31 127.0.0.1 > nul',
      '--status-file', status, '--stdout-file', stdout, '--stderr-file', stderr, '--env-file', env,
    ], { cwd: repoRoot, input: 'stop\n', stdio: ['pipe', 'ignore', 'ignore'], timeout: 15000 })
    const statusText = readFileSync(status, 'utf8')
    assert.match(statusText, /^STOPPED\|.*\|jobCount=0$/m)
    assert.match(statusText, /^CLEANUP\|handles=closed$/m)
  } finally {
    rmSync(fixture, { recursive: true, force: true })
  }
})

test('Docker ownership boundary distinguishes real absence from arbitrary and daemon errors', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'aerolink-fake-docker-'))
  const fakeScript = join(fixture, 'fake-docker.ps1')
  const fakeCommand = join(fixture, 'docker.cmd')
  const harness = join(fixture, 'harness.ps1')
  const state = join(fixture, 'state')
  const fake = String.raw`param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
$kind = if ($Arguments -contains 'volume') { 'volume' } else { 'container' }
$mode = [string]$env:FAKE_DOCKER_MODE
$stateFile = Join-Path $env:FAKE_DOCKER_STATE ($kind + '.state')
if ($Arguments -contains 'rm') { Set-Content -LiteralPath $stateFile -Value 'absent'; exit 0 }
if ($mode -eq 'daemon') { [Console]::Error.WriteLine('Cannot connect to the Docker daemon'); exit 1 }
if ($mode -eq 'arbitrary') { [Console]::Error.WriteLine('object not found'); exit 1 }
if ($mode -eq 'mismatch') { Write-Output 'other-run'; exit 0 }
if (-not (Test-Path -LiteralPath $stateFile) -or (Get-Content -LiteralPath $stateFile -Raw).Trim() -eq 'absent') {
  if ($kind -eq 'volume') { [Console]::Error.WriteLine('Error response from daemon: no such volume: fixture') }
  else { [Console]::Error.WriteLine('Error: No such object: fixture') }
  exit 1
}
Write-Output 'run-id'
exit 0
`
  const command = '@echo off\r\npwsh -NoProfile -File "%~dp0fake-docker.ps1" %*\r\nexit /b %ERRORLEVEL%\r\n'
  const harnessText = String.raw`$source = '${wrapperPath.replaceAll("'", "''")}'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
foreach ($name in @('Invoke-CheckedDocker', 'Get-DockerOwnedResource', 'Remove-DockerOwnedResource')) {
  $node = $ast.Find({ param($candidate) $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $candidate.Name -eq $name }, $true)
  if ($null -eq $node) { throw "missing function $name" }
  . ([scriptblock]::Create($node.Extent.Text))
}
$docker = '${fakeCommand.replaceAll("'", "''")}'
$env:FAKE_DOCKER_STATE = '${state.replaceAll("'", "''")}'
New-Item -ItemType Directory -Path $env:FAKE_DOCKER_STATE -Force | Out-Null
function Assert-Absent([string]$Kind) {
  if ($null -ne (Get-DockerOwnedResource -Docker $docker -Kind $Kind -Name 'fixture')) { throw "expected absent $Kind" }
}
$env:FAKE_DOCKER_MODE = 'absent-container'; Assert-Absent 'container'
$env:FAKE_DOCKER_MODE = 'absent-volume'; Assert-Absent 'volume'
foreach ($kind in @('container', 'volume')) {
  $env:FAKE_DOCKER_MODE = 'daemon'; try { Get-DockerOwnedResource -Docker $docker -Kind $kind -Name 'fixture'; throw 'daemon ambiguity accepted' } catch { if ($_.Exception.Message -notmatch 'ownership could not be verified') { throw } }
  $env:FAKE_DOCKER_MODE = 'arbitrary'; try { Get-DockerOwnedResource -Docker $docker -Kind $kind -Name 'fixture'; throw 'arbitrary error accepted' } catch { if ($_.Exception.Message -notmatch 'ownership could not be verified') { throw } }
}
Set-Content -LiteralPath (Join-Path $env:FAKE_DOCKER_STATE 'container.state') -Value 'present'
Set-Content -LiteralPath (Join-Path $env:FAKE_DOCKER_STATE 'volume.state') -Value 'present'
$errors = [System.Collections.Generic.List[string]]::new()
$env:FAKE_DOCKER_MODE = 'owner'; Remove-DockerOwnedResource -Docker $docker -Kind container -Name 'fixture' -RunId 'run-id' -CleanupErrors $errors
if ($errors.Count -ne 0) { throw 'owner-matched container cleanup failed' }
$env:FAKE_DOCKER_MODE = 'owner'; Remove-DockerOwnedResource -Docker $docker -Kind volume -Name 'fixture' -RunId 'run-id' -CleanupErrors $errors
if ($errors.Count -ne 0) { throw 'owner-matched volume cleanup failed' }
$env:FAKE_DOCKER_MODE = 'absent-volume'; Remove-DockerOwnedResource -Docker $docker -Kind volume -Name 'fixture' -RunId 'run-id' -CleanupErrors $errors
if ($errors.Count -ne 0) { throw 'partial-create absent volume cleanup was not accepted' }
Set-Content -LiteralPath (Join-Path $env:FAKE_DOCKER_STATE 'volume.state') -Value 'present'
$env:FAKE_DOCKER_MODE = 'mismatch'; Remove-DockerOwnedResource -Docker $docker -Kind volume -Name 'fixture' -RunId 'run-id' -CleanupErrors $errors
if ($errors.Count -ne 1) { throw 'owner mismatch was not recorded' }
`
  try {
    writeFileSync(fakeScript, fake)
    writeFileSync(fakeCommand, command)
    writeFileSync(harness, harnessText)
    execFileSync('pwsh', ['-NoProfile', '-File', harness], { cwd: repoRoot, stdio: 'ignore' })
  } finally {
    rmSync(fixture, { recursive: true, force: true })
  }
})

test('Docker volume ownership recognizes current missing-volume wording without masking failures', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'aerolink-fake-volume-wording-'))
  const fakeScript = join(fixture, 'fake-docker.ps1')
  const fakeCommand = join(fixture, 'docker.cmd')
  const harness = join(fixture, 'harness.ps1')
  const fake = String.raw`param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
switch ($env:FAKE_DOCKER_MODE) {
  'real-missing' { [Console]::Error.WriteLine('Error response from daemon: get fixture: no such volume'); exit 1 }
  'real-missing-suffix' { [Console]::Error.WriteLine('Error response from daemon: get fixture: no such volume: fixture'); exit 1 }
  'legacy-missing' { [Console]::Error.WriteLine('Error response from daemon: no such volume: fixture'); exit 1 }
  'daemon' { [Console]::Error.WriteLine('Cannot connect to the Docker daemon'); exit 1 }
  'permission' { [Console]::Error.WriteLine('Error response from daemon: permission denied while inspecting fixture'); exit 1 }
  'wrong-get-name' { [Console]::Error.WriteLine('Error response from daemon: get other: no such volume'); exit 1 }
  'wrong-suffix-name' { [Console]::Error.WriteLine('Error response from daemon: get fixture: no such volume: other'); exit 1 }
  'malformed' { [Console]::Error.WriteLine('Error response from daemon: get fixture: no such volume permission denied'); exit 1 }
  'bare' { [Console]::Error.WriteLine('no such volume: fixture'); exit 1 }
  'multiline' { [Console]::Error.WriteLine('Error response from daemon: get fixture: no such volume' + [Environment]::NewLine + 'permission denied'); exit 1 }
}
Write-Output 'run-id'; exit 0
`
  const command = '@echo off\r\npwsh -NoProfile -File "%~dp0fake-docker.ps1" %*\r\nexit /b %ERRORLEVEL%\r\n'
  const harnessText = String.raw`$source = '${wrapperPath.replaceAll("'", "''")}'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
$node = $ast.Find({ param($candidate) $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $candidate.Name -eq 'Get-DockerOwnedResource' }, $true)
. ([scriptblock]::Create($node.Extent.Text))
$docker = '${fakeCommand.replaceAll("'", "''")}'
foreach ($mode in @('real-missing', 'real-missing-suffix', 'legacy-missing')) {
  $env:FAKE_DOCKER_MODE = $mode
  if ($null -ne (Get-DockerOwnedResource -Docker $docker -Kind volume -Name 'fixture')) { throw "missing volume form $mode was not treated as absent" }
}
foreach ($mode in @('daemon', 'permission', 'wrong-get-name', 'wrong-suffix-name', 'malformed', 'bare', 'multiline')) {
  $env:FAKE_DOCKER_MODE = $mode
  try { Get-DockerOwnedResource -Docker $docker -Kind volume -Name 'fixture'; throw "unsafe volume diagnostic $mode was accepted" } catch { if ($_.Exception.Message -notmatch 'ownership could not be verified') { throw } }
}
`
  try {
    writeFileSync(fakeScript, fake)
    writeFileSync(fakeCommand, command)
    writeFileSync(harness, harnessText)
    execFileSync('pwsh', ['-NoProfile', '-File', harness], { cwd: repoRoot, stdio: 'ignore' })
  } finally {
    rmSync(fixture, { recursive: true, force: true })
  }
})

test('persistent evidence fingerprint distinguishes absent, empty, and changed disposable roots', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'aerolink-fingerprint-'))
  const harness = join(fixture, 'harness.ps1')
  const empty = join(fixture, 'empty')
  const nonempty = join(fixture, 'nonempty')
  const absent = join(fixture, 'absent')
  const created = join(fixture, 'created-after-fingerprint')
  const removed = join(fixture, 'removed-after-fingerprint')
  const content = join(fixture, 'content-change')
  const metadata = join(fixture, 'metadata-change')
  const structure = join(fixture, 'structure-change')
  for (const root of [empty, nonempty, removed, content, metadata, structure]) mkdirSync(root)
  writeFileSync(join(nonempty, 'sentinel.bin'), Buffer.from([0, 1, 2, 3, 255]))
  writeFileSync(join(content, 'sentinel.txt'), 'before')
  writeFileSync(join(metadata, 'sentinel.txt'), 'stable')
  const harnessText = String.raw`$source = '${wrapperPath.replaceAll("'", "''")}'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
$node = $ast.Find({ param($candidate) $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $candidate.Name -eq 'Get-PersistentEvidenceFingerprint' }, $true)
if ($null -eq $node) { throw 'missing fingerprint function' }
. ([scriptblock]::Create($node.Extent.Text))
function Assert-Unchanged([string]$Root, [string]$ExpectedMarker) {
  $before = Get-PersistentEvidenceFingerprint -Root $Root
  $after = Get-PersistentEvidenceFingerprint -Root $Root
  $beforeValues = @($before)
  if ($null -eq $before -or $beforeValues.Count -lt 1 -or ([string]$beforeValues[0]) -notlike $ExpectedMarker) { throw "invalid fingerprint marker for $Root" }
  if ($null -ne (Compare-Object -ReferenceObject $before -DifferenceObject $after)) { throw "unchanged root differed for $Root" }
}
function Assert-Changed($Before, [string]$Root) {
  $after = Get-PersistentEvidenceFingerprint -Root $Root
  if ($null -eq (Compare-Object -ReferenceObject $Before -DifferenceObject $after)) { throw "changed root was accepted for $Root" }
}
Assert-Unchanged '${absent.replaceAll("'", "''")}' '<absent>'
Assert-Unchanged '${empty.replaceAll("'", "''")}' '<root>|D|*'
Assert-Unchanged '${nonempty.replaceAll("'", "''")}' '<root>|D|*'
$before = Get-PersistentEvidenceFingerprint -Root '${created.replaceAll("'", "''")}'
New-Item -ItemType Directory -Path '${created.replaceAll("'", "''")}' | Out-Null
Assert-Changed $before '${created.replaceAll("'", "''")}'
$before = Get-PersistentEvidenceFingerprint -Root '${removed.replaceAll("'", "''")}'
Remove-Item -LiteralPath '${removed.replaceAll("'", "''")}' -Force
Assert-Changed $before '${removed.replaceAll("'", "''")}'
$before = Get-PersistentEvidenceFingerprint -Root '${content.replaceAll("'", "''")}'
[IO.File]::WriteAllText((Join-Path '${content.replaceAll("'", "''")}' 'sentinel.txt'), 'after!')
Assert-Changed $before '${content.replaceAll("'", "''")}'
$before = Get-PersistentEvidenceFingerprint -Root '${metadata.replaceAll("'", "''")}'
$metadataItem = Get-Item -LiteralPath (Join-Path '${metadata.replaceAll("'", "''")}' 'sentinel.txt')
$metadataItem.LastWriteTimeUtc = $metadataItem.LastWriteTimeUtc.AddMinutes(-5)
Assert-Changed $before '${metadata.replaceAll("'", "''")}'
$before = Get-PersistentEvidenceFingerprint -Root '${structure.replaceAll("'", "''")}'
New-Item -ItemType Directory -Path (Join-Path '${structure.replaceAll("'", "''")}' 'child') | Out-Null
Assert-Changed $before '${structure.replaceAll("'", "''")}'
function Get-FileHash { throw 'simulated hash uncertainty' }
try { Get-PersistentEvidenceFingerprint -Root '${nonempty.replaceAll("'", "''")}' | Out-Null; throw 'hash uncertainty was accepted' } catch { if ($_.Exception.Message -notmatch 'simulated hash uncertainty') { throw } }
function Get-ChildItem { throw 'simulated read uncertainty' }
try { Get-PersistentEvidenceFingerprint -Root '${empty.replaceAll("'", "''")}' | Out-Null; throw 'read uncertainty was accepted' } catch { if ($_.Exception.Message -notmatch 'simulated read uncertainty') { throw } }
`
  try {
    writeFileSync(harness, harnessText)
    execFileSync('pwsh', ['-NoProfile', '-File', harness], { cwd: repoRoot, stdio: 'pipe' })
  } finally {
    rmSync(fixture, { recursive: true, force: true })
  }
})

test('listener cleanup treats an expected no-result as empty but fails on query errors', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'aerolink-listener-query-'))
  const harness = join(fixture, 'harness.ps1')
  const harnessText = String.raw`$source = '${wrapperPath.replaceAll("'", "''")}'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
$node = $ast.Find({ param($candidate) $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $candidate.Name -eq 'Get-BoundedListenerConnections' }, $true)
. ([scriptblock]::Create($node.Extent.Text))
function Get-NetTCPConnection {
  if ($env:FAKE_LISTENER_MODE -eq 'empty') { Write-Error 'No MSFT_NetTCPConnection objects found with property LocalPort' -ErrorAction Stop; return }
  if ($env:FAKE_LISTENER_MODE -eq 'failure') { Write-Error 'Access denied querying listener state' -ErrorAction Stop; return }
  [pscustomobject]@{ LocalPort = 49152; LocalAddress = '127.0.0.1'; OwningProcess = 42 }
}
$env:FAKE_LISTENER_MODE = 'empty'
if (@(Get-BoundedListenerConnections -Port 49152).Count -ne 0) { throw 'expected no-result listener query to be empty' }
$env:FAKE_LISTENER_MODE = 'failure'
try { Get-BoundedListenerConnections -Port 49152; throw 'listener query failure was accepted' } catch { if ($_.Exception.Message -notmatch 'bounded listener query failed') { throw } }
$env:FAKE_LISTENER_MODE = 'one'
if (@(Get-BoundedListenerConnections -Port 49152).Count -ne 1) { throw 'listener result was not returned' }
`
  try {
    writeFileSync(harness, harnessText)
    execFileSync('pwsh', ['-NoProfile', '-File', harness], { cwd: repoRoot, stdio: 'ignore' })
  } finally {
    rmSync(fixture, { recursive: true, force: true })
  }
})

function snapshotTree(root) {
  if (!existsSync(root)) return ['<absent>']
  const entries = []
  const visit = (current) => {
    for (const entry of readdirSync(current, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const absolute = join(current, entry.name)
      const name = relative(root, absolute)
      if (entry.isDirectory()) {
        entries.push(`${name}/`)
        visit(absolute)
      } else {
        const stat = lstatSync(absolute)
        const digest = entry.isFile() ? createHash('sha256').update(readFileSync(absolute)).digest('hex') : 'non-file'
        entries.push(`${name}|${stat.size}|${stat.mtimeMs}|${digest}`)
      }
    }
  }
  visit(root)
  return entries
}

test('script-contract family uses disposable verification storage and preserves product/.local', () => {
  assert.match(backupVerifier, /VerificationRoot/)
  assert.match(backupContract, /-VerificationRoot \$verificationRoot/)
  const evidenceRoot = join(repoRoot, 'product/.local')
  const before = snapshotTree(evidenceRoot)
  for (const name of scriptContractNames) {
    execFileSync('pwsh', ['-NoProfile', '-File', join(repoRoot, 'product/scripts', name)], { cwd: repoRoot, stdio: 'ignore' })
  }
  assert.deepEqual(snapshotTree(evidenceRoot), before)
})

test('wrapper failure and cleanup contracts are redacted and fail closed', () => {
  assert.match(wrapper, /function Get-SafeFailureMessage/)
  assert.match(wrapper, /sensitive details were redacted/)
  assert.match(wrapper, /\$executionError = Get-SafeFailureMessage/)
  assert.doesNotMatch(wrapper, /docker \$\(\$Arguments -join/)
  assert.doesNotMatch(wrapper, /\$executionError = \$_.Exception.Message/)
  assert.match(wrapper, /containerIntent/)
  assert.match(wrapper, /volumeIntent/)
  assert.match(wrapper, /cleanup was not proven; Full mode is non-authoritative/)
})

test('JSON dry-run reports execution as not-run without touching a service', () => {
  const output = execFileSync('pwsh', ['-NoProfile', '-File', wrapperPath, '-Paths', 'README.md', '-Mode', 'Full', '-Json', '-DryRun'], {
    cwd: repoRoot,
    encoding: 'utf8',
  })
  const result = JSON.parse(output)
  assert.equal(result.wrapper.execution.status, 'not-run')
  assert.equal(result.wrapper.execution.authoritative, false)
  assert.equal(result.wrapper.execution.timing.totalMs, 0)
  assert.equal(result.wrapper.execution.resources.persistentPostgreSqlTouched, false)
  assert.equal(result.wrapper.execution.resources.persistentEvidenceRootTouched, false)
})
