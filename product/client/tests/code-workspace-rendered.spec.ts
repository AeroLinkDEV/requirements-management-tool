import { expect, test, type Page } from '@playwright/test'

const sha = 'a'.repeat(40)
async function mockWorkspace(page: Page) {
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url())
    const path = url.pathname
    let body: unknown = {}
    if (path.endsWith('/code/source')) body = { projectId: 'project-one', releaseId: 'release-one', version: 1,
      selectionEventId: 'selection-one', capabilities: { canSelect: true, sourceSelectionFrozen: false }, snapshot: {
        id: 'source-one', instanceBaseUrl: 'https://gitlab.example', pathWithNamespace: 'demo/fms', commitSha: sha, friendlyRef: 'main',
      } }
    else if (path.endsWith('/repository')) body = { canManage: true, repository: { projectId: 'project-one', mode: 'ConnectNow', status: 'Verified', version: 7 } }
    else if (path.endsWith('/code/targets')) body = { page: Number(url.searchParams.get('page')), pageSize: 25, total: 26,
      items: [{ exactIdentityId: `${url.searchParams.get('targetKind')}-${url.searchParams.get('page')}`, ownerId: 'owner-one',
        display: url.searchParams.get('page') === '2' ? 'LLR-00002.00' : 'LLR-00001.00', title: 'Retain flight plan state', lifecycle: 'Active', available: true }] }
    else if (path.endsWith('/merge-requests/register')) body = { page: Number(url.searchParams.get('page')), pageSize: 25, total: 26,
      items: url.searchParams.get('page') === '2' ? [{ instanceBaseUrl: 'https://gitlab.example', remoteProjectId: 17,
        mergeRequestIid: 9, relationshipCount: 1, metadataKnown: false }] : [{ instanceBaseUrl: 'https://gitlab.example', remoteProjectId: 17,
        mergeRequestIid: 3, relationshipCount: 2, metadataKnown: true, metadata: { iid: 3, title: 'Retain valid flight plan', state: 'merged', draft: false,
          webUrl: 'https://gitlab.example/demo/fms/-/merge_requests/3' } }] }
    else if (path.endsWith('/code/merge-requests/3')) body = { metadataKnown: true,
      metadata: { iid: 3, title: 'Retain valid flight plan', state: 'merged', draft: false, sourceBranch: 'retain-state', targetBranch: 'main',
        webUrl: 'https://gitlab.example/demo/fms/-/merge_requests/3', approvals: { known: false, approvedBy: [], detail: 'Unavailable' } },
      mergeRequests: [{ id: 'edge-one', relationshipKind: 'MergeRequest', version: 4, isActive: true, capabilities: { canWithdraw: true, canReAdd: false }, targetKind: 'ChangeRequestRevision',
        targetDisplaySnapshot: 'LLRCR-00001.00', meaning: 'Addresses', recordedBy: 'engineer', recordedAt: '2026-09-19T12:00:00Z' }], files: [] }
    else if (path.endsWith('/code/source/source-one/tree')) {
      expect(url.searchParams.get('commit')).toBe(sha)
      expect(url.pathname).toContain('/code/source/source-one/tree')
      body = { configurationVersion: 1, observation: { succeeded: true, completeness: 'Complete', value: { commitSha: sha,
        entries: url.searchParams.get('path') === 'src' ? [{ path: 'src/route.c', name: 'route.c', kind: 'Blob', mode: '100644' }]
          : [{ path: 'src', name: 'src', kind: 'Tree' }, { path: 'README.md', name: 'README.md', kind: 'Blob', mode: '100644' }] } } }
    } else if (path.endsWith('/code/relationships')) {
      expect(url.searchParams.get('sourceSnapshotId')).toBe('source-one')
      expect(url.searchParams.get('path')).toBe('src/route.c')
      body = { page: 1, pageSize: 25, total: 0, items: [] }
    } else if (path.endsWith('/code-traceability')) body = { build: { version: '1.6', readOnly: false }, evaluationState: 'WaitingForPrerequisite',
      sourceOfTruth: 'GitLab owns source code.', summary: null, requirements: [], waiting: { detail: 'Waiting for a materialized baseline.', action: 'Materialize requirements first.', recordedCount: 0 } }
    await route.fulfill({ json: body })
  })
}

test('linked register pages recorded identities and keeps unknown approvals explicit', async ({ page }, testInfo) => {
  await mockWorkspace(page)
  await page.goto('/tests/fixtures/code-workspace.html')
  await page.getByRole('button', { name: '!3', exact: true }).click()
  await expect(page.getByText('GitLab approvals unknown')).toBeVisible()
  await expect(page.getByText('LLRCR-00001.00')).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('merge-request-register.png'), fullPage: true })
  await page.getByRole('button', { name: 'Next page', exact: true }).click()
  await expect(page.getByRole('button', { name: '!9', exact: true })).toBeVisible()
  await expect(page.getByText('Metadata unavailable', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '!3', exact: true })).toHaveCount(0)
})

test('explorer includes unlinked files and requests relationships for the exact snapshot and path', async ({ page }, testInfo) => {
  await mockWorkspace(page)
  await page.goto('/tests/fixtures/code-workspace.html')
  await page.getByRole('button', { name: 'Code Explorer', exact: true }).click()
  await expect(page.getByRole('button', { name: 'README.md', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'src', exact: true }).click()
  await page.getByRole('button', { name: 'route.c', exact: true }).click()
  await expect(page.getByText('No relationship recorded for this file.')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Open exact file in GitLab' })).toHaveAttribute('href', `https://gitlab.example/demo/fms/-/blob/${sha}/src/route.c`)
  await page.screenshot({ path: testInfo.outputPath('code-explorer.png'), fullPage: true })
  await page.setViewportSize({ width: 700, height: 900 })
  await expect(page.getByText('No relationship recorded for this file.')).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('code-explorer-narrow.png'), fullPage: true })
})

test('shared picker clears old selections on page and target-kind changes and submits exact identity', async ({ page }, testInfo) => {
  await mockWorkspace(page)
  let recorded: Record<string, unknown> | undefined
  await page.route('**/code/relationships/merge-requests', async route => {
    recorded = route.request().postDataJSON()
    await route.fulfill({ json: { id: 'new-link', version: 1 } })
  })
  await page.goto('/tests/fixtures/code-workspace.html')
  await page.getByRole('button', { name: '!3', exact: true }).click()
  await page.getByRole('button', { name: 'Link AeroLink artifact' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('radio', { name: /LLR-00001.00/ }).check()
  await expect(dialog.getByRole('button', { name: 'Record relationship' })).toBeEnabled()
  await dialog.getByRole('button', { name: 'Next targets' }).click()
  await expect(dialog.getByRole('button', { name: 'Record relationship' })).toBeDisabled()
  await dialog.getByRole('radio', { name: /LLR-00002.00/ }).check()
  await dialog.getByLabel('Artifact type').selectOption('RequirementProposal')
  await expect(dialog.getByRole('button', { name: 'Record relationship' })).toBeDisabled()
  await dialog.getByRole('radio', { name: /LLR-00001.00/ }).check()
  await page.screenshot({ path: testInfo.outputPath('shared-link-picker.png'), fullPage: true })
  await dialog.getByRole('button', { name: 'Record relationship' }).click()
  await expect(dialog).toHaveCount(0)
  expect(recorded).toMatchObject({ releaseId: 'release-one', targetKind: 'RequirementProposal',
    targetId: 'RequirementProposal-1', expectedConfigurationVersion: 7, meaning: 'RelatedContext', mergeRequestIid: 3 })
})

test('relationship withdrawal sends expected version and retains an attributable rationale', async ({ page }) => {
  await mockWorkspace(page)
  let recorded: Record<string, unknown> | undefined
  await page.route('**/relationships/MergeRequest/edge-one/withdraw', async route => {
    recorded = route.request().postDataJSON()
    await route.fulfill({ status: 409, json: { error: 'Relationship changed. Refresh before trying again.' } })
  })
  await page.goto('/tests/fixtures/code-workspace.html')
  await page.getByRole('button', { name: '!3', exact: true }).click()
  await page.getByRole('button', { name: 'Withdraw relationship' }).click()
  await page.getByLabel('Withdrawal rationale').fill('Superseded by the exact replacement relationship.')
  await page.getByRole('button', { name: 'Confirm withdrawal' }).click()
  await expect(page.getByRole('alert')).toHaveText('Relationship changed. Refresh before trying again.')
  expect(recorded).toEqual({ expectedVersion: 4, rationale: 'Superseded by the exact replacement relationship.' })
  await expect(page.getByText('LLRCR-00001.00', { exact: true })).toBeVisible()
})

test('missing source capabilities fail closed without losing the workspace', async ({ page }) => {
  await mockWorkspace(page)
  await page.route('**/code/source?**', route => route.fulfill({ json: { projectId: 'project-one', releaseId: 'release-one', version: 0 } }))
  await page.goto('/tests/fixtures/code-workspace.html')
  await page.getByRole('button', { name: '!3', exact: true }).click()
  await expect(page.getByText('GitLab approvals unknown')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Link AeroLink artifact' })).toHaveCount(0)
})

test('repository drift cannot decorate the selected snapshot with another repository tree', async ({ page }) => {
  await mockWorkspace(page)
  await page.route('**/code/source/*/tree?**', async route => {
    expect(new URL(route.request().url()).pathname).toContain('/code/source/source-one/tree')
    await route.fulfill({ status: 409, json: { code: 'repository_changed', error: 'Selected source belongs to a different repository configuration.' } })
  })
  await page.goto('/tests/fixtures/code-workspace.html')
  await page.getByRole('button', { name: 'Code Explorer', exact: true }).click()
  await expect(page.getByRole('alert')).toHaveText('Selected source belongs to a different repository configuration.')
  await expect(page.getByRole('button', { name: 'README.md', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Link AeroLink artifact' })).toHaveCount(0)
})

test('artifact code panel asks for exact revision and keeps server pages separate', async ({ page }) => {
  const requests: string[] = []
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url())
    requests.push(url.search)
    const target = url.searchParams.get('targetId')
    const number = Number(url.searchParams.get('page'))
    await route.fulfill({ json: { page: number, pageSize: 25, total: target === 'exact-one' ? 26 : 0,
      items: target === 'exact-one' ? [{ id: `edge-${number}`, version: 1, relationshipKind: 'File', isActive: true,
        targetKind: 'RequirementRevision', targetIdentityId: target, targetDisplaySnapshot: `LLR-0000${number}.00`,
        meaning: 'Implements', path: `src/file-${number}.c`, recordedBy: 'engineer', recordedAt: '2026-09-19T12:00:00Z',
        capabilities: { canWithdraw: false, canReAdd: false } }] : [] } })
  })
  await page.goto('/tests/fixtures/code-workspace.html?mode=artifact')
  await expect(page.getByText('src/file-1.c')).toBeVisible()
  await page.getByRole('button', { name: 'Next relationships' }).click()
  await expect(page.getByText('src/file-2.c')).toBeVisible()
  await expect(page.getByText('src/file-1.c')).toHaveCount(0)
  await page.getByRole('button', { name: 'Open another exact revision' }).click()
  await expect(page.getByText('No code relationship is recorded for this exact target in this build.')).toBeVisible()
  await expect(page.getByText('src/file-2.c')).toHaveCount(0)
  expect(requests.some(query => query.includes('targetId=exact-two') && query.includes('page=1'))).toBeTruthy()
  expect(requests.every(query => query.includes('targetKind=RequirementRevision'))).toBeTruthy()
})

test('linked-only files use a grouped server page and leave all-file browsing reachable', async ({ page }) => {
  await mockWorkspace(page)
  await page.route('**/code/files?**', async route => {
    const url = new URL(route.request().url())
    expect(url.searchParams.get('sourceSnapshotId')).toBe('source-one')
    const second = url.searchParams.get('page') === '2'
    await route.fulfill({ json: { page: second ? 2 : 1, pageSize: 25, total: 26,
      items: [{ path: second ? 'tests/fms_test.c' : 'src/route.c', relationshipCount: 2 }] } })
  })
  await page.goto('/tests/fixtures/code-workspace.html')
  await page.getByRole('button', { name: 'Code Explorer', exact: true }).click()
  await expect(page.getByRole('button', { name: 'README.md', exact: true })).toBeVisible()
  await page.getByLabel('Linked files only').check()
  await expect(page.getByRole('button', { name: 'src/route.c', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'README.md', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'Next linked files' }).click()
  await expect(page.getByRole('button', { name: 'tests/fms_test.c', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'src/route.c', exact: true })).toHaveCount(0)
  await page.getByLabel('Linked files only').uncheck()
  await expect(page.getByRole('button', { name: 'README.md', exact: true })).toBeVisible()
})

test('artifact entry reuses Code browsers and preserves its exact target for MR and file links', async ({ page }, testInfo) => {
  await mockWorkspace(page)
  const writes: Record<string, unknown>[] = []
  await page.route('**/code/relationships?**', route => route.fulfill({ json: { page: 1, total: 0, items: [] } }))
  await page.route('**/code/relationships/merge-requests', route => {
    writes.push(route.request().postDataJSON()); return route.fulfill({ status: 201, json: {} })
  })
  await page.route('**/code/relationships/files', route => {
    writes.push(route.request().postDataJSON()); return route.fulfill({ status: 201, json: {} })
  })
  await page.goto('/tests/fixtures/code-workspace.html?mode=artifact')
  await page.getByRole('button', { name: 'Add code relationship' }).click()
  await page.getByRole('button', { name: '!3', exact: true }).click()
  await page.getByRole('button', { name: 'Link AeroLink artifact' }).click()
  const picker = page.getByRole('dialog', { name: 'Link merge request !3', exact: true })
  await expect(picker.getByLabel('Artifact type')).toHaveCount(0)
  await picker.getByRole('combobox', { name: 'Relationship', exact: true }).selectOption('Implements')
  await picker.getByRole('button', { name: 'Record relationship' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(writes[0]).toMatchObject({ targetKind: 'RequirementRevision', targetId: 'exact-one', mergeRequestIid: 3, meaning: 'Implements' })
  await page.getByRole('button', { name: 'Open another exact revision' }).click()
  await page.getByRole('button', { name: 'Add code relationship' }).click()
  await page.getByRole('button', { name: 'Files at selected source', exact: true }).click()
  await page.getByRole('button', { name: 'src', exact: true }).click()
  await page.getByRole('button', { name: 'route.c', exact: true }).click()
  await page.screenshot({ path: testInfo.outputPath('artifact-file-browser.png'), fullPage: true })
  await page.getByRole('button', { name: 'Link AeroLink artifact' }).click()
  const filePicker = page.getByRole('dialog', { name: 'Link src/route.c', exact: true })
  await filePicker.getByRole('button', { name: 'Record relationship' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(writes[1]).toMatchObject({ targetKind: 'RequirementRevision', targetId: 'exact-two', sourceSnapshotId: 'source-one', commitSha: sha, path: 'src/route.c', parentPath: 'src' })
  await expect(page.getByRole('button', { name: 'Add code relationship' })).toBeFocused()
})
