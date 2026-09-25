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

// The migration is merged history and cannot change, so these checks guard against it being rewritten.
// SoftwareCaseRenamePostgresQualificationTests executes the same migration, but only against its dedicated
// aerolink_722_qualify database, which no CI lane provides yet (#1122).
test('the migration contract guards System and leaves review history/body prose governed', () => {
  const migration = read('product', 'src', 'AeroLink.Infrastructure', 'Persistence', 'Migrations',
    '20260822170000_RenameSoftwareVerificationArtifactsToCases.cs')
  const authority = read('product', 'src', 'AeroLink.Infrastructure', 'Persistence',
    'SoftwareVerificationCaseMigrationAuthority.cs')

  assert.match(migration, /SYSTP/)
  assert.match(migration, /System SYSTP rows are deliberately not touched/)
  assert.match(migration, /SourceChangeRequestsJson/)
  assert.match(migration, /test_procedure_revisions/)
  assert.match(migration, /Unsectioned cases/)
  assert.match(migration, /artifact_comments/)
  assert.match(migration, /test_procedure_documents/)
  assert.match(migration, /artifact_edit_sessions/)
  assert.match(migration, /regexp_matches/)
  assert.match(migration, /HLRTP-\(\[0-9\]\+\)/)
  assert.match(migration, /Controlled high-level software test cases document for this project/)
  assert.match(migration, /'Test Procedures', 'Test Cases'/)
  assert.match(migration, /ControlledDocumentArtifact/)
  assert.match(migration, /ControlledDocument'/)
  assert.match(authority, /Pending event/)
  assert.match(authority, /\.Completed/)
  assert.match(authority, /SignatureSuperseded/)
})
