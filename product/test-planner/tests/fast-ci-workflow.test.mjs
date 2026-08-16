import { test } from 'node:test'
import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'

const repoRoot = fileURLToPath(new URL('../../../', import.meta.url))
const manifestPath = join(repoRoot, 'product/test-planner/fast-ci-manifest.json')
const workflowPath = join(repoRoot, '.github/workflows/fast-pr-feedback.yml')
const fullWorkflowPath = join(repoRoot, '.github/workflows/ci.yml')
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
const workflow = readFileSync(workflowPath, 'utf8')
const fullWorkflow = readFileSync(fullWorkflowPath, 'utf8')

test('Fast phase 1 is explicitly advisory, versioned and bounded', () => {
  assert.equal(manifest.schemaVersion, 1)
  assert.equal(manifest.id, 'aerolink-fast-ci/v1')
  assert.equal(manifest.authoritative, false)
  assert.equal(manifest.targetMs, 240000)
  assert.equal(manifest.safety.persistentPostgreSql, 'forbidden')
  assert.equal(manifest.safety.persistentEvidenceRoot, 'forbidden')
  assert.match(manifest.safety.mergeAuthority, /existing Product quality gate remains the only merge authority/i)

  assert.match(workflow, /^name: Fast PR feedback \(advisory\)$/m)
  assert.match(workflow, /pull_request:/)
  assert.doesNotMatch(workflow, /^\s*push:/m)
  assert.match(workflow, /group: fast-pr-/)
  assert.match(workflow, /cancel-in-progress: true/)
  assert.match(workflow, /fast-ci-manifest\.json/)
  assert.match(workflow, /Fast feedback is advisory/i)
  assert.doesNotMatch(workflow, /Report what this run validated/)
  assert.match(fullWorkflow, /Report what this run validated/)
})

test('Fast backend manifest names only reviewed source-controlled smoke classes', () => {
  assert.equal(manifest.backend.domainProject, 'product/tests/AeroLink.Domain.Tests/AeroLink.Domain.Tests.csproj')
  assert.deepEqual(manifest.backend.infrastructureClasses, [
    'ArtifactScopingTests',
    'BaselinePersistenceTests',
    'ConcurrencyTests',
    'IdentityPersistenceTests',
    'MigrationRegistrationTests',
  ])
  assert.deepEqual(manifest.backend.apiClasses, ['SharedHostIsolationTests'])

  for (const className of manifest.backend.infrastructureClasses) {
    assert.equal(
      existsSync(join(repoRoot, `product/tests/AeroLink.Infrastructure.Tests/${className}.cs`)),
      true,
      `Fast Infrastructure class is not source-controlled: ${className}`,
    )
  }
  for (const className of manifest.backend.apiClasses) {
    assert.equal(
      existsSync(join(repoRoot, `product/tests/AeroLink.Api.Tests/${className}.cs`)),
      true,
      `Fast API class is not source-controlled: ${className}`,
    )
  }
})

test('Fast client manifest stays lint/typecheck-only and Full retains heavyweight evidence', () => {
  assert.deepEqual(manifest.client.commands, ['npm ci', 'npm run lint', 'npm run typecheck'])
  assert.equal(manifest.client.workingDirectory, 'product/client')
  assert.doesNotMatch(JSON.stringify(manifest.client), /playwright|test:smoke|test:production/i)

  const fullOnly = manifest.fullOnlyEvidence.join('\n')
  for (const expected of ['complete API suite', 'complete infrastructure suite', 'PostgreSQL', 'production-browser', 'full browser']) {
    assert.match(fullOnly, new RegExp(expected, 'i'))
  }
})

test('Fast workflow contains no persistent-database or persistent-evidence escape hatch', () => {
  assert.doesNotMatch(workflow, /54329/)
  assert.doesNotMatch(workflow, /product[\\/]\.local/)
  assert.doesNotMatch(workflow, /ConnectionStrings__AeroLink|Database__Provider|postgres:17/i)
  assert.doesNotMatch(workflow, /docker\s+(run|compose)|Start-Postgres/i)
  assert.match(workflow, /persistent PostgreSQL and product\/.local are forbidden/i)
})
