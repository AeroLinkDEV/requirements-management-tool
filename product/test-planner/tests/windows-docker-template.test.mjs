import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const wrapper = readFileSync(`${repoRoot}/product/scripts/Get-AeroLinkTestPlan.ps1`, 'utf8')

const ownedResourceStart = wrapper.indexOf('function Get-DockerOwnedResource {')
const ownedResourceEnd = wrapper.indexOf('function Remove-DockerOwnedResource {', ownedResourceStart)
const ownedResource = wrapper.slice(ownedResourceStart, ownedResourceEnd)

/**
 * These contracts used to require the opposite of what they now require.
 *
 * They pinned Go templates carrying the label key as an embedded quoted string, escaped as \" so that
 * Windows PowerShell 5.1 would pass it through. That spelling is rejected by PowerShell 7 with
 * `unexpected "\\" in operand`, and the unescaped spelling is rejected by 5.1 with `function "com" not
 * defined`. No spelling satisfies both, so pinning either one guaranteed the gate was broken on one of the
 * two shells a developer might have — and it was the template, not the surrounding logic, that kept
 * needing to be corrected.
 *
 * What is required now is that no such template exists: ownership and the published port are read out of
 * the inspect JSON the command already returns, which carries no quoting problem and parses identically on
 * both editions.
 */
test('Docker inspect is not driven by a Go template carrying a quoted key', () => {
  assert.doesNotMatch(wrapper, /--format', '\{\{/)
  assert.equal(wrapper.includes(String.raw`\"com.aerolink.planner.run\"`), false)
  assert.equal(wrapper.includes(String.raw`\"5432/tcp\"`), false)
})

test('Docker ownership proves existence before reading a label, and reads it from the probe output', () => {
  assert.notEqual(ownedResourceStart, -1)
  assert.notEqual(ownedResourceEnd, -1)

  const containerProbe = `@('inspect', $Name)`
  const volumeProbe = `@('volume', 'inspect', $Name)`
  assert.ok(ownedResource.includes(containerProbe))
  assert.ok(ownedResource.includes(volumeProbe))

  // The absence branch runs on the probe, before anything reads a label off it.
  assert.ok(ownedResource.indexOf('$probeExitCode -ne 0') < ownedResource.indexOf('$ownerRecords'))
  // And the label comes from that same probe rather than a second call.
  assert.match(ownedResource, /\$ownerJson = \(\$probeOutput/)
  assert.match(ownedResource, /\$ownerRecords\[0\]\.Config\.Labels/)
  assert.match(ownedResource, /\$ownerRecords\[0\]\.Labels/)
  assert.match(ownedResource, /com\.aerolink\.planner\.run/)

  assert.ok(ownedResource.includes('Error: No such object:'))
  assert.ok(ownedResource.includes('error: no such object:'))
  assert.ok(ownedResource.includes('no such volume'))
})

test('A record carrying no label of ours is absent rather than an error', () => {
  // docker rm --force returns before inspect stops answering for the name. In that window inspect exits 0
  // with one record whose Labels are null, and treating that as corruption reported cleanup unproven for a
  // container that had just been removed.
  assert.match(ownedResource, /if \(\[string\]::IsNullOrWhiteSpace\(\$owner\)\) \{ return \$null \}/)
  assert.ok(ownedResource.indexOf('IsNullOrWhiteSpace($owner)) { return $null }')
    < ownedResource.indexOf('ownership label was not a single bounded value'))
})

test('The published port is read from inspect JSON, not a template', () => {
  const gate = wrapper.slice(wrapper.indexOf('function Invoke-DisposablePostgreSqlGate'))
  assert.match(gate, /'inspect-port-mapping'/)
  assert.match(gate, /\$containerRecords\[0\]\.NetworkSettings\.Ports\.'5432\/tcp'/)
})
