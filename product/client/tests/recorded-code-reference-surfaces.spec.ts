import { expect, test, type Page } from '@playwright/test'

const projectId = '11111111-1111-4111-8111-111111111111'
const releaseId = '22222222-2222-4222-8222-222222222222'
const rootId = 'root-change-request'
const otherId = '33333333-3333-4333-8333-333333333333'
const scenarios = ['recorded-reference-null', 'recorded-reference-object'] as const

function exactReference() {
  return {
    id: '44444444-4444-4444-8444-444444444444', relationshipKind: 'MergeRequest', version: 1,
    isActive: true, releaseId, releaseVersion: '1.6', meaning: 'RelatedContext',
    recordedBy: 'reviewer', recordedAt: '2026-09-21T12:00:00Z', targetKind: 'ChangeRequestRevision',
    targetIdentityId: otherId, targetRevisionNumber: 1,
    targetStableIdentity: `ChangeRequestRevision:${otherId}`, targetDisplaySnapshot: 'SRCR-00039.00',
    instanceBaseUrl: 'https://gitlab.example', remoteProjectId: 42, repositoryPathSnapshot: 'aerolink/source',
    mergeRequestUrlSnapshot: 'https://gitlab.example/aerolink/source/-/merge_requests/12',
    mergeRequestIid: 12, mergeRequestId: 1200, mergeRequestTitleSnapshot: 'Stored source record',
  }
}

function rawReferences(scenario: typeof scenarios[number]) {
  return scenario === 'recorded-reference-null' ? [null] : exactReference()
}

async function mockInspectorApi(page: Page, references: unknown) {
  await page.route('**/api/change-requests/root-change-request', route => route.fulfill({ json: {
    id: rootId, displayNumber: 'SRCR-ROOT.00', baseNumber: 'SRCR-ROOT', revision: 0,
    title: 'A change request', state: 'Draft', projectId, targetReleaseId: releaseId,
  } }))
  await page.route('**/api/change-requests/root-change-request/review-comments', route => route.fulfill({ json: { cycles: [] } }))
  await page.route('**/api/change-requests/root-change-request/trace?directOnly=true', route => route.fulfill({ json: {
    projectId, rootChangeRequestId: rootId, rootArtifactId: rootId, rootArtifactKind: 'ChangeRequest',
    nodes: [
      { id: rootId, kind: 'ChangeRequest', projectId, buildId: releaseId, displayNumber: 'SRCR-ROOT.00' },
      { id: otherId, kind: 'ChangeRequest', projectId, buildId: releaseId, displayNumber: 'SRCR-00039.00', recordedCodeReferences: references },
    ],
    edges: [{ fromId: rootId, fromKind: 'ChangeRequest', toId: otherId, toKind: 'ChangeRequest',
      relation: 'Upstream', provenance: [] }],
  } }))
}

for (const scenario of scenarios) {
  test(`change network refuses ${scenario} reference collection`, async ({ page }) => {
    const pageErrors: string[] = []
    page.on('pageerror', error => pageErrors.push(error.message))
    await page.goto(`/tests/fixtures/change-network.html?case=${scenario}`)
    const reference = page.locator('.dtCanvasNode.is-selected .recordedCodeReference')
    await expect(reference).toHaveCount(1)
    await expect(reference.getByRole('status')).toContainText('cannot be safely interpreted')
    await expect(reference.getByRole('link')).toHaveCount(0)
    expect(pageErrors).toEqual([])
  })

  test(`inside-change view refuses ${scenario} reference collection in both render locations`, async ({ page }) => {
    const pageErrors: string[] = []
    page.on('pageerror', error => pageErrors.push(error.message))
    await page.goto(`/tests/fixtures/inside-change.html?case=${scenario}`)
    const references = page.locator('.recordedCodeReference')
    // The open register card and selected-record panel consume the same API-projected collection.
    await expect(references).toHaveCount(2)
    await expect(references.getByRole('status')).toHaveCount(2)
    await expect(references.first().getByRole('status')).toContainText('cannot be safely interpreted')
    await expect(page.getByRole('link', { name: 'Open stored GitLab reference ↗' })).toHaveCount(0)
    expect(pageErrors).toEqual([])
  })

  test(`change request inspector refuses ${scenario} reference collection`, async ({ page }) => {
    const pageErrors: string[] = []
    page.on('pageerror', error => pageErrors.push(error.message))
    await mockInspectorApi(page, rawReferences(scenario))
    await page.goto(`/tests/fixtures/recorded-code-reference-inspector.html?case=${scenario}`)
    await page.getByRole('tab', { name: 'Trace & impact' }).click()
    const reference = page.locator('.recordedCodeReference')
    await expect(reference).toHaveCount(1)
    await expect(reference.getByRole('status')).toContainText('cannot be safely interpreted')
    await expect(reference.getByRole('link')).toHaveCount(0)
    expect(pageErrors).toEqual([])
  })
}
