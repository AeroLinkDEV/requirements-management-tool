import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const wrapper = readFileSync(`${repoRoot}/product/scripts/Get-AeroLinkTestPlan.ps1`, 'utf8')

const ownedResourceStart = wrapper.indexOf('function Get-DockerOwnedResource {')
const ownedResourceEnd = wrapper.indexOf('function Remove-DockerOwnedResource {', ownedResourceStart)
const ownedResource = wrapper.slice(ownedResourceStart, ownedResourceEnd)

test('Windows PowerShell preserves quoted Docker label keys in Go templates', () => {
  assert.ok(wrapper.includes(String.raw`'{{ index .Config.Labels \"com.aerolink.planner.run\" }}'`))
  assert.ok(wrapper.includes(String.raw`'{{ index .Labels \"com.aerolink.planner.run\" }}'`))
  assert.ok(wrapper.includes(String.raw`'{{json (index .NetworkSettings.Ports \"5432/tcp\")}}'`))
})

test('Docker ownership checks prove existence before formatting labels', () => {
  assert.notEqual(ownedResourceStart, -1)
  assert.notEqual(ownedResourceEnd, -1)

  const containerProbe = `@('inspect', $Name)`
  const volumeProbe = `@('volume', 'inspect', $Name)`
  const containerLabel = String.raw`@('inspect', '--format', '{{ index .Config.Labels \"com.aerolink.planner.run\" }}', $Name)`
  const volumeLabel = String.raw`@('volume', 'inspect', '--format', '{{ index .Labels \"com.aerolink.planner.run\" }}', $Name)`

  assert.ok(ownedResource.includes(containerProbe))
  assert.ok(ownedResource.includes(volumeProbe))
  assert.ok(ownedResource.includes(containerLabel))
  assert.ok(ownedResource.includes(volumeLabel))
  assert.ok(ownedResource.indexOf(containerProbe) < ownedResource.indexOf(containerLabel))
  assert.ok(ownedResource.indexOf(volumeProbe) < ownedResource.indexOf(volumeLabel))

  assert.ok(ownedResource.includes('Error: No such object:'))
  assert.ok(ownedResource.includes('error: no such object:'))
  assert.ok(ownedResource.includes('no such volume'))
  assert.equal(ownedResource.includes('template parsing error'), false)
  assert.equal(ownedResource.includes('map has no entry for key "Config"'), false)
})
