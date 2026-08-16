import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const wrapperPath = join(repoRoot, 'product/scripts/Get-AeroLinkTestPlan.ps1')
const wrapper = readFileSync(wrapperPath, 'utf8')

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
  assert.match(gate, /'--publish', "127\.0\.0\.1:\$\{hostPostgreSqlPort\}:5432"/)
  assert.match(gate, /'--volume', "\$\{volumeName\}:\/var\/lib\/postgresql\/data"/)
  assert.match(gate, /postgres:17/)
  assert.match(gate, /finally\s*\{/)
  assert.match(gate, /Start-Process -FilePath 'dotnet' -ArgumentList @\(\$apiDll/)
  assert.match(gate, /WaitForExit\(5000\)/)
  assert.match(gate, /cleanupErrors/)
  assert.match(gate, /Config\.Labels.*com\.aerolink\.planner\.run/)
  assert.match(gate, /docker rm --force|rm --force \$containerName/)
  assert.match(gate, /docker volume rm --force|volume rm --force \$volumeName/)
  assert.match(wrapper, /Docker is unavailable.*not-proven/)
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
