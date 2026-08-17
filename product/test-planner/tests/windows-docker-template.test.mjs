import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const wrapper = readFileSync(`${repoRoot}/product/scripts/Get-AeroLinkTestPlan.ps1`, 'utf8')

test('Windows PowerShell preserves quoted Docker label keys in Go templates', () => {
  assert.ok(wrapper.includes(String.raw`'{{ index .Config.Labels \"com.aerolink.planner.run\" }}'`))
  assert.ok(wrapper.includes(String.raw`'{{ index .Labels \"com.aerolink.planner.run\" }}'`))
  assert.ok(wrapper.includes(String.raw`'{{json (index .NetworkSettings.Ports \"5432/tcp\")}}'`))
})
