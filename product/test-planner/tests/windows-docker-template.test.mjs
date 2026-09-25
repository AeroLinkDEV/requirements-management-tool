import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const wrapper = readFileSync(`${repoRoot}/product/scripts/Get-AeroLinkTestPlan.ps1`, 'utf8')

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

test('The published port is read from inspect JSON, not a template', () => {
  const gate = wrapper.slice(wrapper.indexOf('function Invoke-DisposablePostgreSqlGate'))
  assert.match(gate, /'inspect-port-mapping'/)
  assert.match(gate, /\$containerRecords\[0\]\.NetworkSettings\.Ports\.'5432\/tcp'/)
})
