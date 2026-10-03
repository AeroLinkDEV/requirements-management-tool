import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

// Authoring gate: only the browser can prove that the selected File, mapping and original preview remain
// usable after a real committed response is lost. Hosted API tests own ledger/password/authority details.
test.afterEach(async ({ page }) => { await page.unrouteAll({ behavior: 'wait' }) })

async function openImport(page: Page, request: APIRequestContext) {
  await apiLogin(request)
  const code = `R${Date.now()}`
  const response = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: 'Import recovery', programCode: code, projectName: 'Import recovery', softwareProduct: 'Recovery',
    initialRelease: '0.01', initialReleaseIsReleased: false,
  } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const workspace = await response.json() as { program: { id: string }, project: { id: string }, release: { id: string } }
  await login(page, 'admin', { openProject: false })
  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/problem-reports`)
  await page.getByRole('button', { name: 'Import…' }).click()
  const panel = page.getByRole('region', { name: 'Import Problem Reports' })
  await panel.getByLabel('Source system').fill('Jira')
  await panel.getByLabel('Export file (.csv or .xlsx)').setInputFiles({ name: 'reviewed.csv', mimeType: 'text/csv',
    buffer: Buffer.from('Key,Summary,Description,Status,Category\nJIRA-RECOVERY,Original,Original problem,Closed,CodeFunctional\n') })
  await panel.getByRole('button', { name: 'Read the file' }).click()
  await panel.getByLabel('Map Closed').selectOption('ClosedInSource')
  await panel.getByRole('button', { name: 'Preview again' }).click()
  await expect(panel.locator('caption')).toHaveText('1 will be created · 0 will be skipped')
  await panel.getByLabel('Confirm with your password').fill('AeroLink!2026')
  return { panel, projectId: workspace.project.id }
}

function field(body: string, name: string) {
  const result = body.match(new RegExp(`name="${name}"\\r\\n\\r\\n([^\\r]*)`))
  expect(result, `${name} is present in the actual multipart request`).not.toBeNull()
  return result![1]
}

for (const loss of ['abort', 'truncated JSON', 'HTML', 'unexpected JSON'] as const) {
test(`a committed import with ${loss} can recover its original receipt after a rejected password retry`, async ({ page, request }) => {
  const { panel, projectId } = await openImport(page, request)
  const tuples: Array<{ operationId: string, previewHash: string, mapping: string, fileName: string }> = []
  const receipts: unknown[] = []
  await page.route('**/api/problem-reports/import/commit', async route => {
    const body = route.request().postDataBuffer()!.toString('utf8')
    tuples.push({ operationId: field(body, 'operationId'), previewHash: field(body, 'previewHash'),
      mapping: field(body, 'mapping'), fileName: body.match(/filename="([^"]+)"/)![1] })
    const response = await route.fetch()
    if (!response.ok()) { await route.fulfill({ response }); return }
    expect(response.ok(), await response.text()).toBeTruthy()
    receipts.push(await response.json())
    if (receipts.length > 1) await route.fulfill({ response })
    else if (loss === 'abort') await route.abort('failed') // Server commit and receipt are already durable.
    else if (loss === 'truncated JSON') await route.fulfill({ response, body: '{"batchId":' })
    else if (loss === 'HTML') await route.fulfill({ response, contentType: 'text/html', body: '<html>Receipt unavailable</html>' })
    else await route.fulfill({ response, body: '{}' })
  })
  await panel.getByRole('button', { name: 'Sign and import 1 Problem Report', exact: true }).click()
  await expect(panel.getByRole('alert')).toContainText('The result of this import could not be confirmed.')
  await expect(panel.getByText(/Retry this same import/)).toBeVisible()
  await expect(panel.getByRole('button', { name: 'Preview again' })).toBeDisabled()
  expect(await panel.getByLabel('Export file (.csv or .xlsx)').evaluate((input: HTMLInputElement) => input.files?.[0]?.name)).toBe('reviewed.csv')
  await expect(panel.getByLabel('Source system')).toHaveValue('Jira')
  await expect(panel.getByLabel('Map Closed')).toHaveValue('ClosedInSource')
  await expect(panel.getByLabel('Confirm with your password')).toHaveValue('AeroLink!2026')
  await panel.getByLabel('Confirm with your password').fill('wrong password')
  const rejected = page.waitForResponse(response => response.url().endsWith('/api/problem-reports/import/commit') && response.status() === 401)
  await panel.getByRole('button', { name: 'Sign and import 1 Problem Report', exact: true }).click()
  await rejected
  await expect(panel.getByRole('alert')).toHaveText('Electronic signature confirmation failed.')
  await expect(panel.getByText(/Retry this same import/)).toBeVisible()
  await expect(panel.getByRole('button', { name: 'Preview again' })).toBeDisabled()
  await panel.getByLabel('Confirm with your password').fill('AeroLink!2026')
  const completed = page.waitForResponse(response => response.url().endsWith('/api/problem-reports/import/commit') && response.status() === 200)
  await panel.getByRole('button', { name: 'Sign and import 1 Problem Report', exact: true }).click()
  await completed
  await expect(panel.getByRole('status')).toHaveText('Imported 1 Problem Reports; 0 rows were skipped with their reasons.')
  expect(tuples).toHaveLength(3)
  expect(tuples[1]).toEqual(tuples[0])
  expect(tuples[2]).toEqual(tuples[0])
  expect(receipts[1]).toEqual(receipts[0]) // Includes batch ID, original report IDs/display numbers and counts.
  const batches = await page.request.get(`${apiBase}/api/problem-reports/import/batches?projectId=${projectId}`)
  expect(batches.ok(), await batches.text()).toBeTruthy()
  expect(await batches.json()).toMatchObject([{ created: 1, skipped: 0, hasImportSignature: true }])
})
}

test('changing the import mapping after uncertainty starts a new operation', async ({ page, request }) => {
  const { panel } = await openImport(page, request)
  const operations: string[] = []
  await page.route('**/api/problem-reports/import/commit', async route => {
    operations.push(field(route.request().postDataBuffer()!.toString('utf8'), 'operationId'))
    if (operations.length === 1) await route.abort('failed') // This case owns intent renewal, not publication.
    else await route.fulfill({ response: await route.fetch() })
  })
  await panel.getByRole('button', { name: 'Sign and import 1 Problem Report', exact: true }).click()
  await expect(panel.getByText(/Retry this same import/)).toBeVisible()
  await panel.getByLabel('Source system').fill('Issue tracker')
  await expect(panel.getByText(/Retry this same import/)).toHaveCount(0)
  await panel.getByRole('button', { name: 'Preview again' }).click()
  await expect(panel.locator('caption')).toHaveText('1 will be created · 0 will be skipped')
  await panel.getByRole('button', { name: 'Sign and import 1 Problem Report', exact: true }).click()
  await expect(panel.getByRole('status')).toHaveText('Imported 1 Problem Reports; 0 rows were skipped with their reasons.')
  expect(operations).toHaveLength(2)
  expect(operations[1]).not.toBe(operations[0])
})
