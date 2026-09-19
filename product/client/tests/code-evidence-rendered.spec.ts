import { expect, test } from '@playwright/test'

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
