import { expect, test } from '@playwright/test'
import { programRoleLabel } from '../src/presentation'
import { authorityLabel, authorityRoleLabels, baseRoleAuthorities, leadershipAuthorities } from '../src/workflowAuthorities'

/**
 * #1016 S02. A configured review stage names the authority that must sign it, and the review-setup panel
 * showed that authority as the stored enum name — `SystemEngineer`, `SystemEngineeringLead`.
 *
 * The formatter existed. The problem was that there were two of them. `programRoleLabel` knew sixteen names,
 * `authorityLabel` knows twenty-one, and both fall back to returning the key unchanged — so a name only the
 * second one carried rendered raw while looking exactly like a name that had been formatted correctly. That
 * is the failure this file exists to stop coming back: a missing entry is invisible at the call site.
 */

test('every authority a configured stage can require has a readable label', () => {
  // The authorities a stage is actually authored against. If someone adds one of these without a label,
  // this fails here rather than silently rendering a database value to a reviewer.
  //
  // The assertion is map membership, not `label !== stored`. `Airworthiness` is one word and is already
  // how a person says it, so it legitimately maps to itself — an earlier draft of this test read that
  // correct entry as a missing one. What actually has to hold is that the fallback never fires for a
  // supported authority, and that no stored camelCase reaches a reader.
  for (const authority of [...baseRoleAuthorities, ...leadershipAuthorities]) {
    expect(Object.keys(authorityRoleLabels), `${authority} has no entry, so it would render raw`)
      .toContain(authority)
    expect(authorityLabel(authority)).not.toMatch(/[a-z][A-Z]/)
  }
})

test('the five names that used to render raw are covered', () => {
  // Regression for the exact gap: these were absent from the old programRoleLabel map, so the review-setup
  // panel showed them as stored enum names.
  const previouslyUncovered = {
    SystemTestEngineer: 'System Test Engineer',
    SoftwareTestEngineer: 'Software Test Engineer',
    ProjectEngineer: 'Project Engineer',
    SystemTestLead: 'System Test Lead',
    SoftwareTestLead: 'Software Test Lead',
  } as const

  for (const [stored, readable] of Object.entries(previouslyUncovered)) {
    expect(programRoleLabel(stored), `${stored} still renders raw`).toBe(readable)
  }
})

test('the two vocabularies cover the same names, and differ only where the server differs', () => {
  // These are two vocabularies on purpose, and an earlier attempt at this correction collapsed them — which
  // was wrong. The server keeps the same split: a person's held Program role reads "Software Quality
  // Analyst" (AssuranceAuthorityPolicy, IdentityRecords, ApiSupport), while the authority a review stage
  // requires reads "Software Quality Assurance" (ReviewWorkflow, ApprovalConfigurationEndpoints,
  // WorkflowEndpoints). Merging them would have made the picker disagree with the alerts the server writes.
  //
  // So the invariant is not "same wording". It is "same coverage": neither map may silently fall through on
  // a name the other one knows, because the fallback returns the key and a gap is invisible at the call site.
  for (const stored of Object.keys(authorityRoleLabels)) {
    expect(programRoleLabel(stored), `${stored} leaks a stored enum name`).not.toMatch(/[a-z][A-Z]/)
  }

  // The one deliberate wording difference, pinned in both directions so neither drifts alone.
  expect(programRoleLabel('SoftwareQualityAnalyst')).toBe('Software Quality Analyst')
  expect(authorityLabel('SoftwareQualityAnalyst')).toBe('Software Quality Assurance')
})

test('an unknown or retired authority is shown exactly as stored, not guessed', () => {
  // A value this vocabulary does not know is historical or not ours to interpret. Returning it unchanged is
  // the truthful outcome: inventing a modern equivalent would misreport who actually holds the authority,
  // and it must not become grantable by acquiring a friendly label.
  expect(programRoleLabel('RetiredDisciplineLead')).toBe('RetiredDisciplineLead')
  expect(authorityLabel('')).toBe('')
})
