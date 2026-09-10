import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

test('initial upstream authoring blocks Deferred with a popup and persists multiple exact approved revisions', async ({ page, request }, testInfo) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const shelvedDraft = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId, targetReleaseId: showcase.activeReleaseId, type: 'System',
    title: 'Deferred selector qualification', problem: 'P', analysis: 'A', solution: 'S', requirementChanges: [],
  } })
  expect(shelvedDraft.ok(), await shelvedDraft.text()).toBeTruthy()
  const shelvedId = (await shelvedDraft.json()).id as string
  const shelved = await request.post(`${apiBase}/api/change-requests/${shelvedId}/defer`, { data: { reason: 'Explicit test setup for the deferred selector boundary.' } })
  expect(shelved.ok(), await shelved.text()).toBeTruthy()
  await login(page, 'admin', { openProject: false })
  const endpoint = `${apiBase}/api/authoring/upstream-change-requests?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&type=Software&softwareLevel=HighLevel`
  const response = await page.request.get(`${endpoint}&includeEarlierBuilds=true&limit=100`)
  expect(response.ok(), await response.text()).toBeTruthy()
  const data = await response.json() as { candidates: { id: string; displayNumber: string; state: string; earlierBuild: boolean; selectable: boolean }[] }
  const deferred = data.candidates.find(candidate => candidate.state === 'Deferred')
  expect(deferred, 'The real defer workflow supplies a visible but ineligible CR').toBeTruthy()
  const earlier = data.candidates.find(candidate => candidate.selectable && candidate.earlierBuild)
  const current = data.candidates.find(candidate => candidate.selectable && !candidate.earlierBuild)
  expect(earlier).toBeTruthy()
  expect(current).toBeTruthy()
  const root = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  await page.goto(`${root}/software/change-requests/new?level=HLR`)
  await expect(page.getByRole('heading', { name: 'Create HLR Change Request' })).toBeVisible()
  const picker = page.getByLabel('Upstream change requests', { exact: true })
  await picker.getByLabel('Include earlier builds').check()
  await picker.getByLabel('Find a direct parent').fill(deferred!.displayNumber.split('.')[0])
  const dialogPromise = page.waitForEvent('dialog')
  const click = picker.getByRole('button', { name: new RegExp(deferred!.displayNumber.replace('.', '\\.')) }).click()
  const dialog = await dialogPromise
  expect(dialog.type()).toBe('alert')
  expect(dialog.message()).toContain(deferred!.displayNumber)
  expect(dialog.message()).toContain('Reassign this CR to the current build')
  await dialog.accept()
  await click
  await expect(picker.locator('.upstreamDraftRow')).toHaveCount(0)
  for (const candidate of [earlier!, current!]) {
    await picker.getByLabel('Find a direct parent').fill(candidate.displayNumber.split('.')[0])
    await picker.getByRole('button', { name: new RegExp(candidate.displayNumber.replace('.', '\\.')) }).click()
    await picker.getByLabel(`Rationale for ${candidate.displayNumber}`, { exact: true }).fill('This exact approved decision remains applicable to the selected build.')
  }
  await expect(picker.locator('.upstreamDraftRow')).toHaveCount(2)
  await page.getByLabel('Title', { exact: true }).fill('Issue 1006 initial multi-parent authoring')
  await page.screenshot({ path: testInfo.outputPath('initial-exact-upstream-parents.png'), fullPage: true })
  const savedPromise = page.waitForResponse(response => response.url().endsWith('/api/change-request-drafts') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Save HLRCR Draft', exact: true }).click()
  const saved = await savedPromise
  expect(saved.ok(), await saved.text()).toBeTruthy()
  const created = await saved.json() as { id: string }
  const detail = await page.request.get(`${apiBase}/api/change-requests/${created.id}`)
  expect(detail.ok(), await detail.text()).toBeTruthy()
  const body = await detail.json() as { upstream: { upstreamChangeRequestId: string }[] }
  expect(body.upstream.map(link => link.upstreamChangeRequestId).sort()).toEqual([earlier!.id, current!.id].sort())
  await page.getByRole('button', { name: 'Check out & edit', exact: true }).click()
  const editPicker = page.getByLabel('Upstream change requests', { exact: true })
  await expect(editPicker.locator('.upstreamDraftRow')).toHaveCount(2)
  for (const candidate of [earlier!, current!]) {
    await expect(editPicker.getByLabel(`Rationale for ${candidate.displayNumber}`, { exact: true }))
      .toHaveValue('This exact approved decision remains applicable to the selected build.')
  }
  await editPicker.getByLabel('Find a direct parent').fill(deferred!.displayNumber.split('.')[0])
  const editDialogPromise = page.waitForEvent('dialog')
  const editClick = editPicker.getByRole('button', { name: new RegExp(deferred!.displayNumber.replace('.', '\\.')) }).click()
  const editDialog = await editDialogPromise
  expect(editDialog.message()).toContain('Reassign this CR to the current build')
  await editDialog.accept()
  await editClick
  await expect(editPicker.locator('.upstreamDraftRow')).toHaveCount(2)
  await page.getByRole('button', { name: 'Discard checkout', exact: true }).click()
})
