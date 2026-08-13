import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

test('retained connector work is reachable by direct Project URL and fails closed without losing recovery context', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request, 'software.author')
  const listResponse = await request.get(`${apiBase}/api/managed-documents?projectId=${showcase.projectId}`)
  expect(listResponse.ok(), await listResponse.text()).toBeTruthy()
  const document = (await listResponse.json()).items.find((item: { inWorkRevision?: string }) => item.inWorkRevision)
  expect(document).toBeTruthy()
  const detailResponse = await request.get(`${apiBase}/api/managed-documents/${document.id}`)
  expect(detailResponse.ok(), await detailResponse.text()).toBeTruthy()
  const revision = (await detailResponse.json()).revisions.find((item: { state: string }) => ['Draft', 'Returned'].includes(item.state))
  expect(revision).toBeTruthy()

  const workspaceId = crypto.randomUUID()
  const recoveryCalls: { path: string; workspaceId: string }[] = []
  await page.route(`**/api/managed-documents/revisions/${revision.id}/recovery**`, async route => {
    const requestBody = route.request().postDataJSON() as { workspaceId: string }
    recoveryCalls.push({ path: new URL(route.request().url()).pathname, workspaceId: requestBody.workspaceId })
    await route.fulfill({
      status: 409,
      contentType: 'application/problem+json',
      body: JSON.stringify({
        code: 'document_recovery_source_changed',
        error: 'The controlled source changed. Preserve or export the retained local copy.',
      }),
    })
  })

  await login(page, 'software.author', { openProject: false })
  const recoveryUrl = `/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${document.id}?recoveryWorkspaceId=${workspaceId}&recoveryRevisionId=${revision.id}`
  await page.goto(recoveryUrl)
  await expect(page.getByText('Retained desktop workspace')).toBeVisible()
  await expect(page.getByText(/Browser reauthentication is required/)).toBeVisible()
  await page.reload()
  await expect(page.getByRole('button', { name: 'Reauthorize and resume' })).toBeVisible()

  await page.getByRole('button', { name: 'Reauthorize and resume' }).click()
  await expect(page.getByRole('alert')).toContainText('Preserve or export the retained local copy')
  expect(recoveryCalls).toEqual([{ path: `/api/managed-documents/revisions/${revision.id}/recovery`, workspaceId }])

  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${document.id}`)
  await expect(page.getByText('Retained desktop workspace')).toHaveCount(0)
  await page.goBack()
  await expect(page.getByText('Retained desktop workspace')).toBeVisible()
  await page.getByRole('button', { name: 'Discard retained workspace' }).click()
  await expect(page.getByRole('alert')).toContainText('Preserve or export the retained local copy')
  expect(recoveryCalls[1]).toEqual({ path: `/api/managed-documents/revisions/${revision.id}/recovery/discard`, workspaceId })
})
