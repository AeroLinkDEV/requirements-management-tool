import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = join(here, '..', '..', '..')
const read = (...parts) => readFileSync(join(repoRoot, ...parts), 'utf8')

// Most of #722's Case contract is now proved by running it:
// - the neutral API fields and their pre-Case aliases are asserted in the hosted API tests that call each
//   route (VerificationProgramIsolation, TestProcedureAuthoring, VerificationImpact, BuildTestSet,
//   AuthoringTracedImpact, ProcedureManifestEffectivity, the registers, ShowcaseSeed and
//   ManualTestChangeRequest);
// - the client vocabulary is asserted in artifact-acronym-presentation.spec.ts;
// - superseded signatures are asserted in test-procedure-explorer.spec.ts;
// - Case routes are asserted in routing-contract.spec.ts.
// Only what no behaviour test can say stays here as source text (#1128).
test('controlled Case document labels are byte-exact and software surfaces never hard-code the Procedure API', () => {
  const presentation = read('product', 'client', 'src', 'presentation.ts')
  assert.match(presentation, /HighLevelTestCases: 'HLR Test Case Document \(HLRTD\)'/)
  assert.match(presentation, /LowLevelTestCases: 'LLR Test Case Document \(LLRTD\)'/)
  assert.match(presentation, /HLRTD: documentTypeLabels\.HighLevelTestCases/)
  assert.match(presentation, /LLRTD: documentTypeLabels\.LowLevelTestCases/)

  // Software surfaces reach their API root through verificationArtifactApiRoot, so a Case is never sent to
  // the System Procedure collection.
  for (const file of ['TestProcedureExplorer.tsx', 'TestingCoverageWorkspace.tsx', 'TestResultsWorkspace.tsx',
    'TestChangeRequestPage.tsx', 'TestChangeRequestWorkspace.tsx', 'TestChangeRequestEditor.tsx']) {
    assert.doesNotMatch(read('product', 'client', 'src', file), /\/api\/test-procedures/, file)
  }
})

// The #722 migration and its authority are proved by executing them:
// SoftwareCaseRenamePostgresQualificationTests upgrades an exact pre-rename database and asserts that System
// SYSTP rows are untouched, Cases, documents, comments, notifications and watermarks are relabelled, and every
// superseded signature leaves pending and completed evidence. Since #1151 it runs in the PostgreSQL job for any
// persistence or migration change, so the source-text copy of that contract is retired (#1128, #1122).
