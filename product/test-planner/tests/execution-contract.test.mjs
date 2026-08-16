import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const wrapperPath = join(repoRoot, 'product/scripts/Get-AeroLinkTestPlan.ps1')
const wrapper = readFileSync(wrapperPath, 'utf8')
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
  assert.match(gate, /Get-NetTCPConnection/)
  assert.match(gate, /Get-CimInstance Win32_Process/)
  assert.match(gate, /apiOwnershipIntent/)
  assert.match(gate, /postgres:17/)
  assert.match(gate, /finally\s*\{/)
  assert.doesNotMatch(gate, /Get-FreeLoopbackPort|hostApiPort|Start-Process|Stop-Process|containerStarted|volumeCreated/)
  assert.match(gate, /WaitForExit\(10000\)/)
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
