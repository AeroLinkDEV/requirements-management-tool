import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
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
  assert.match(ownedProcessSource, /SpacePathSelfTest/)
  execFileSync('dotnet', ['build', ownedProcessProject, '--configuration', 'Release'], { cwd: repoRoot, stdio: 'ignore' })
  execFileSync('dotnet', ['run', '--project', ownedProcessProject, '--configuration', 'Release', '--no-build', '--no-restore', '--', '--self-test-space-path'], { cwd: repoRoot, stdio: 'ignore' })
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
