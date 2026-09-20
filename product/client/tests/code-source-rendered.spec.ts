import { expect, test } from '@playwright/test'

const sha = 'a'.repeat(40)
test('source confirmation sends the previewed commit and observed versions, then refreshes the selection', async ({ page }) => {
  let version = 3
  let command: Record<string, unknown> | undefined
  await page.route('**/api/projects/project-one/**', async route => {
    const url = new URL(route.request().url())
    if (url.pathname.endsWith('/repository')) return route.fulfill({ json: { canManage: true, repository: {
      projectId: 'project-one', mode: 'ConnectNow', status: 'Verified', version: 7,
    } } })
    if (url.pathname.endsWith('/repository/commit')) {
      expect(url.searchParams.get('referenceKind')).toBe('Tag')
      return route.fulfill({ json: { configurationVersion: 7, observation: { succeeded: true, value: { sha, referenceKind: 'Tag' } } } })
    }
    if (route.request().method() === 'POST') {
      command = route.request().postDataJSON(); version++
      return route.fulfill({ json: { version, commitSha: sha } })
    }
    return route.fulfill({ json: { projectId: 'project-one', releaseId: url.searchParams.get('releaseId'), version,
      snapshot: version === 4 ? { id: 'snapshot-a', commitSha: sha, pathWithNamespace: 'demo/source', friendlyRef: 'v1.5' } : null,
      capabilities: { canSelect: true, sourceSelectionFrozen: false } } })
  })
  await page.goto('/tests/fixtures/code-source.html')
  await page.getByRole('button', { name: 'Select source', exact: true }).click()
  await page.getByLabel('GitLab branch, tag, or full commit').fill('v1.5')
  await page.getByLabel('Reference type').selectOption('Tag')
  await page.getByRole('button', { name: 'Preview exact commit' }).click()
  await expect(page.getByText(sha, { exact: true })).toBeVisible()
  expect(command).toBeUndefined()
  await page.getByRole('button', { name: 'Confirm source selection' }).click()
  await expect(page.getByText('Selection 4', { exact: false })).toBeVisible()
  expect(command).toEqual({ releaseId: 'release-a', reference: 'v1.5', referenceKind: 'Tag', previewSha: sha,
    expectedConfigurationVersion: 7, expectedSelectionVersion: 3 })
})

test('a delayed preview cannot appear or be confirmed after changing builds', async ({ page }) => {
  let releasePreview!: () => void
  const pending = new Promise<void>(resolve => { releasePreview = resolve })
  let previewStarted!: () => void
  const started = new Promise<void>(resolve => { previewStarted = resolve })
  await page.route('**/api/projects/project-one/**', async route => {
    const url = new URL(route.request().url())
    if (url.pathname.endsWith('/repository')) return route.fulfill({ json: { canManage: true, repository: {
      projectId: 'project-one', mode: 'ConnectNow', status: 'Verified', version: 7,
    } } })
    if (url.pathname.endsWith('/repository/commit')) {
      previewStarted(); await pending
      return route.fulfill({ json: { configurationVersion: 7, observation: { succeeded: true, value: { sha, referenceKind: 'Branch' } } } })
    }
    return route.fulfill({ json: { projectId: 'project-one', releaseId: url.searchParams.get('releaseId'), version: 0,
      capabilities: { canSelect: url.searchParams.get('releaseId') === 'release-a', sourceSelectionFrozen: false } } })
  })
  await page.goto('/tests/fixtures/code-source.html')
  await page.getByRole('button', { name: 'Select source', exact: true }).click()
  await page.getByLabel('GitLab branch, tag, or full commit').fill('main')
  await page.getByRole('button', { name: 'Preview exact commit' }).click()
  await started
  await page.getByRole('button', { name: 'Switch build' }).click()
  releasePreview()
  await expect(page.getByText('No source selected for this build.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Confirm source selection' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Select source', exact: true })).toHaveCount(0)
  await expect(page.getByText(sha, { exact: true })).toHaveCount(0)
})

test('changing builds clears the source held by the parent while the new selection loads', async ({ page }) => {
  let releaseNewBuild!: () => void
  const pending = new Promise<void>(resolve => { releaseNewBuild = resolve })
  await page.route('**/api/projects/project-one/code/source?**', async route => {
    const releaseId = new URL(route.request().url()).searchParams.get('releaseId')
    if (releaseId === 'release-b') await pending
    await route.fulfill({ json: { projectId: 'project-one', releaseId, version: 1,
      snapshot: { id: 'snapshot-a', commitSha: sha, pathWithNamespace: 'demo/source' },
      capabilities: { canSelect: false, sourceSelectionFrozen: false } } })
  })
  await page.goto('/tests/fixtures/code-source.html')
  await expect(page.getByLabel('Parent source')).toHaveText('release-a')
  await page.getByRole('button', { name: 'Switch build' }).click()
  await expect(page.getByLabel('Parent source')).toHaveText('No parent source')
  releaseNewBuild()
  await expect(page.getByLabel('Parent source')).toHaveText('release-b')
})
