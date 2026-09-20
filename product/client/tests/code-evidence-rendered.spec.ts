import { expect, test, type Page } from '@playwright/test'

async function acceptanceFixture(page: Page, repositoryAvailable = true) {
  await page.route('**/api/code-traceability?**', route => route.fulfill({ json: {
    campaignBaselineId: 'baseline-one', build: { version: '1.6', readOnly: false }, evaluationState: 'Evaluated',
    summary: { required: 1, mapped: 0, missing: 1, percent: 0 }, requirements: [{ artifactId: 'a', revisionId: 'r1',
      displayNumber: 'LLR-00001.01', statement: 'Retain the valid route.', evidence: { selectorVersion: 3,
        supersededLegacyRecordId: 'legacy-one', state: 'Invalidated', countsAsImplementation: false, contributions: [] } }],
  } }))
  await page.route('**/api/projects/project-one/repository', route => route.fulfill({ json: { canManage: false,
    repository: repositoryAvailable ? { projectId: 'project-one', mode: 'ConnectNow', status: 'Verified', version: 7 } : null } }))
  await page.route('**/code/source?**', route => route.fulfill({ json: { projectId: 'project-one', releaseId: 'release-one', version: 4,
    selectionEventId: 'event-one', snapshot: { id: 'snapshot-one', commitSha: 'a'.repeat(40) } } }))
  await page.route('**/code/relationships?**', route => route.fulfill({ json: { page: 1, total: 3, items: [
    { id: 'mr-one', relationshipKind: 'MergeRequest', version: 2, isActive: true, meaning: 'Implements', mergeRequestIid: 3 },
    { id: 'file-one', relationshipKind: 'File', version: 5, isActive: true, meaning: 'Implements', path: 'src/route.c', sourceSnapshotId: 'snapshot-one' },
    { id: 'context-only', relationshipKind: 'MergeRequest', version: 1, isActive: true, meaning: 'RelatedContext', mergeRequestIid: 8 },
  ] } }))
  await page.goto('/tests/fixtures/code-evidence.html?editable')
  await page.getByRole('button', { name: 'Replace evidence decision' }).click()
}

test('acceptance binds baseline, selector and source identities with multiple verified contributions', async ({ page }) => {
  let body: Record<string, unknown> | undefined
  await page.route('**/code/evidence', async route => { body = route.request().postDataJSON(); await route.fulfill({ status: 201, json: {} }) })
  await page.route('**/code/source/snapshot-one/tree?**', route => {
    const query = new URL(route.request().url()).searchParams
    expect(query.get('commit')).toBe('a'.repeat(40)); expect(query.get('path')).toBe('src')
    return route.fulfill({ json: { observation: { succeeded: true, value: { commitSha: 'a'.repeat(40),
      entries: query.get('cursor') ? [{ path: 'src/route.c', kind: 'Blob' }] : [], nextCursor: query.get('cursor') ? undefined : 'next-page' } } } })
  })
  await acceptanceFixture(page)
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: 'Select merge request' }).nth(1)).toBeDisabled()
  await dialog.getByRole('button', { name: 'Select merge request' }).first().click()
  await dialog.getByRole('button', { name: 'Verify file', exact: true }).click()
  await expect(dialog.getByRole('button', { name: 'Use verified file' })).toBeDisabled()
  await dialog.getByRole('button', { name: 'Next file page' }).click()
  await dialog.getByRole('button', { name: 'Use verified file' }).click()
  await expect(dialog.getByRole('heading', { name: '2 selected contributions' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Accept evidence decision', exact: true }).click()
  await expect(dialog).toHaveCount(0)
  expect(body).toMatchObject({ expectedBaselineId: 'baseline-one', expectedSelectorVersion: 3, expectedLegacyRecordId: 'legacy-one',
    expectedSourceSelectionVersion: 4, expectedSourceSnapshotId: 'snapshot-one', expectedSourceSelectionEventId: 'event-one', expectedConfigurationVersion: 7,
    contributions: [{ kind: 'MergeRequest', relationshipId: 'mr-one', expectedRelationshipVersion: 2 },
      { kind: 'File', relationshipId: 'file-one', expectedRelationshipVersion: 5, parentPath: 'src', cursor: 'next-page', pageSize: 50 }] })
  expect(JSON.stringify(body)).not.toContain('mergeCommitSha')
})

test('no-code replacement works without GitLab and sends no source expectations', async ({ page }) => {
  let body: Record<string, unknown> | undefined
  await page.route('**/code/evidence', async route => { body = route.request().postDataJSON(); await route.fulfill({ status: 201, json: {} }) })
  await acceptanceFixture(page, false)
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('radio', { name: 'No code change required' }).check()
  await dialog.getByRole('textbox', { name: 'No-code rationale' }).fill('Existing certified implementation is unchanged.')
  await dialog.getByRole('button', { name: 'Accept evidence decision', exact: true }).click()
  await expect(dialog).toHaveCount(0)
  expect(body).toMatchObject({ disposition: 'NoCodeChangeRequired', expectedBaselineId: 'baseline-one', contributions: [], noCodeChangeRationale: 'Existing certified implementation is unchanged.' })
  expect(body).not.toHaveProperty('expectedSourceSnapshotId')
  expect(body).not.toHaveProperty('expectedConfigurationVersion')
})

test('source drift failure cannot verify a file or silently accept evidence', async ({ page }) => {
  let posted = false
  await page.route('**/code/evidence', route => { posted = true; return route.fulfill({ status: 409, json: { error: 'The selected source changed. Refresh before accepting.' } }) })
  await page.route('**/code/source/snapshot-one/tree?**', route => route.fulfill({ status: 409, json: { error: 'Repository changed.' } }))
  await acceptanceFixture(page)
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: 'Verify file', exact: true }).click()
  await expect(dialog.getByRole('alert')).toHaveText('Repository changed.')
  await expect(dialog.getByRole('button', { name: 'Use verified file' })).toBeDisabled()
  await expect(dialog.getByRole('button', { name: 'Accept evidence decision', exact: true })).toBeDisabled()
  expect(posted).toBe(false)
  await dialog.getByRole('button', { name: 'Cancel file selection' }).click()
  await dialog.getByRole('button', { name: 'Select merge request' }).first().click()
  await dialog.getByRole('button', { name: 'Accept evidence decision', exact: true }).click()
  await expect(dialog.getByRole('alert')).toContainText('selected source changed')
  await expect(dialog).toBeVisible()
})

test('new evidence shows individual contributions and invalidation without a fabricated legacy mapping', async ({ page }) => {
  await page.route('**/api/code-traceability?**', route => route.fulfill({ json: {
    build: { version: '1.6', readOnly: true }, sourceOfTruth: 'GitLab is the source of truth for source code.',
    evaluationState: 'Evaluated', demonstrationScope: false,
    summary: { required: 2, mapped: 1, missing: 1, percent: 50, gateComplete: false }, requirements: [
      { artifactId: 'a', revisionId: 'r1', displayNumber: 'LLR-00001.01', statement: 'Retain the valid route.', mapping: null,
        evidence: { evidenceSetId: 'set-one', selectorVersion: 1, state: 'Accepted', countsAsImplementation: true,
          disposition: 'GitLabContributions', recordedBy: 'engineer', recordedAt: '2026-09-19T16:00:00Z', contributions: [
            { id: 'c1', kind: 'MergeRequest', repositoryPath: 'demo/source', mergeRequestIid: 3,
              mergeRequestTitle: 'Retain valid route', mergeRequestUrl: 'https://gitlab.example/demo/source/-/merge_requests/3',
              commitSha: 'a'.repeat(40), mergeResultSha: 'b'.repeat(40), mergeResultKind: 'MergeCommit' },
            { id: 'c2', kind: 'File', repositoryPath: 'demo/source', path: 'src/route.c', commitSha: 'a'.repeat(40), startLine: 3, endLine: 12 },
          ] } },
      { artifactId: 'b', revisionId: 'r2', displayNumber: 'LLR-00002.00', statement: 'Keep the crew informed.', mapping: null,
        evidence: { evidenceSetId: 'set-two', selectorVersion: 2, state: 'Invalidated', countsAsImplementation: false,
          disposition: 'NoCodeChangeRequired', noCodeChangeRationale: 'Previously covered by existing code.',
          invalidationRationale: 'Candidate taken back.', contributions: [] } },
    ],
  } }))
  await page.goto('/tests/fixtures/code-evidence.html')
  await expect(page.getByRole('heading', { name: '1 of 2 exact requirement revisions mapped' })).toBeVisible()
  const accepted = page.locator('.codeRecords article').filter({ hasText: 'LLR-00001.01' })
  await expect(accepted.locator('.mergeEvidence')).toHaveCount(2)
  await expect(accepted.getByRole('link', { name: '!3 · Retain valid route' })).toHaveAttribute('href', /merge_requests\/3$/)
  await expect(accepted).toContainText('src/route.c')
  await expect(accepted).toContainText('Lines 3–12')
  const invalidated = page.locator('.codeRecords article').filter({ hasText: 'LLR-00002.00' })
  await expect(invalidated).toHaveClass('missing')
  await expect(invalidated).toContainText('Invalidated')
  await expect(invalidated).toContainText('no longer satisfies the build gate')
  await expect(invalidated).toContainText('Candidate taken back.')
  await expect(page.getByRole('button', { name: '+ Record code mapping' })).toHaveCount(0)
})
