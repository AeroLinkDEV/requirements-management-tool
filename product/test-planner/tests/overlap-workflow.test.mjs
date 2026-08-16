import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const workflow = readFileSync(fileURLToPath(new URL('../../../.github/workflows/pr-overlap.yml', import.meta.url)), 'utf8')

test('overlap workflow remains trusted-base, bounded, advisory and artifact-producing', () => {
  assert.match(workflow, /pull_request_target:\s*\n\s+types:/)
  assert.match(workflow, /permissions:\s*\n\s+contents: read\s*\n\s+pull-requests: write/)
  assert.match(workflow, /ref: \$\{\{ github\.event\.repository\.default_branch \}\}/)
  assert.doesNotMatch(workflow, /ref:\s*\$\{\{\s*github\.event\.pull_request\.head\.sha\s*\}\}/)
  assert.match(workflow, /GITHUB_TOKEN:\s+\$\{\{ secrets\.GITHUB_TOKEN \}\}/)
  assert.match(workflow, /continue-on-error: true/)
  assert.match(workflow, /name: pr-overlap-report-\$\{\{ github\.event\.pull_request\.number \}\}/)
  assert.match(workflow, /\"version\":2/)
  assert.match(workflow, /maxLabelsPerPullRequest/)
})
