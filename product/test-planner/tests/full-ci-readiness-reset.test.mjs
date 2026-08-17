import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const workflowPath = fileURLToPath(new URL('../../../.github/workflows/reset-full-ci-readiness.yml', import.meta.url))
const workflow = readFileSync(workflowPath, 'utf8')

test('readiness reset runs only from trusted pull_request_target synchronize events', () => {
  assert.match(workflow, /pull_request_target:\s*\n\s*types:\s*\[synchronize\]/)
  assert.match(workflow, /contains\(github\.event\.pull_request\.labels\.\*\.name, 'ready-for-full-ci'\)/)
  assert.doesNotMatch(workflow, /actions\/checkout|git\s+(?:checkout|switch|clone)|github\.event\.pull_request\.head/)
})

test('readiness reset has only the bounded label-write permissions it requires', () => {
  assert.match(workflow, /permissions:\s*\n\s*contents:\s*read\s*\n\s*issues:\s*write\s*\n\s*pull-requests:\s*write/)
  assert.doesNotMatch(workflow, /contents:\s*write|actions:\s*write/)
  assert.match(workflow, /--method DELETE/)
  assert.match(workflow, /issues\/\$\{PR_NUMBER\}\/labels\/ready-for-full-ci/)
  assert.match(workflow, /GH_TOKEN:\s*\$\{\{ github\.token \}\}/)
})

test('a changed SHA is explicitly required to request Full validation again', () => {
  assert.match(workflow, /the new SHA must request Full validation again/)
  assert.doesNotMatch(workflow, /ready-for-full-ci.*--method (?:POST|PUT)/s)
})
