import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, showcaseSeed } from './auth'

const completeImpacts = JSON.stringify({
  trace: 'Not Affected',
  verification: 'Not Affected',
  documents: 'Not Affected',
  baseline: 'Not Affected',
  collaboration: 'Not Affected',
})

const disciplines = [
  { type: 'System', route: 'systems', level: 'System' },
  { type: 'Software', route: 'software', level: 'HighLevel' },
] as const

test('administrator actions work identically for another authors System and Software changes', async ({ page, request, playwright }) => {
  test.setTimeout(120_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const author = await playwright.request.newContext()
  await apiLogin(author, 'systems.author')
  const records: { route: string; draft: any; approved: any }[] = []

  for (const discipline of disciplines) {
    const create = async (title: string) => {
      const response = await author.post(`${apiBase}/api/scr-drafts`, { data: {
        projectId: showcase.projectId,
        targetReleaseId: showcase.activeReleaseId,
        type: discipline.type,
        title,
        problem: 'The governed recovery action is not yet represented.',
        analysis: 'An administrator with Project access must act without becoming the author.',
        solution: 'Apply one server-authoritative author-or-administrator rule.',
        requirementChanges: [{
          level: discipline.level,
          kind: 'Introduce',
          targetSectionId: await firstSectionId(author, showcase.projectId, discipline.level),
          statement: `The ${discipline.type.toLowerCase()} change shall retain original authorship during administrator recovery.`,
          rationale: 'Controlled recovery must remain attributable.',
          verificationMethod: 'Inspection',
          impactDispositionJson: completeImpacts,
        }],
      } })
      expect(response.ok(), await response.text()).toBeTruthy()
      return response.json()
    }

    const draft = await create(`Administrator ${discipline.type} Draft`)
    const approved = await create(`Administrator ${discipline.type} revision`)
    const submitted = await author.post(`${apiBase}/api/scrs/${approved.id}/submit`, { data: {
      expectedVersion: approved.version,
      approvers: [{ userId: 'admin', name: 'Caller supplied name ignored' }],
      mode: 'Sequential',
    } })
    expect(submitted.ok(), await submitted.text()).toBeTruthy()
    const approval = await request.post(`${apiBase}/api/scrs/${approved.id}/approve`, { data: {
      password: 'AeroLink!2026',
      meaning: 'Approved for administrator recovery journey coverage.',
    } })
    expect(approval.ok(), await approval.text()).toBeTruthy()
    records.push({ route: discipline.route, draft, approved })
  }
  await author.dispose()

  await login(page)
  const releaseRoot = `/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}`
  for (const record of records) {
    await page.goto(`${releaseRoot}/${record.route}/change-requests/${record.draft.id}`)
    for (const action of ['Check out & edit', 'Defer', 'Configure & Submit Review', 'Attach'])
      await expect(page.getByRole('button', { name: action, exact: true })).toBeVisible()

    await page.getByLabel('Label').fill('Administrator recovery evidence')
    await page.getByLabel('Description').fill('Uploaded by the actual acting administrator.')
    await page.getByLabel('File').setInputFiles({
      name: 'administrator-recovery.txt',
      mimeType: 'text/plain',
      buffer: Buffer.from('controlled administrator recovery evidence'),
    })
    await page.getByRole('button', { name: 'Attach', exact: true }).click()
    await expect(page.getByText('Stored, hashed, and attributed.')).toBeVisible()
    await expect(page.locator('.attachmentList')).toContainText('admin')

    await page.getByRole('button', { name: 'Check out & edit', exact: true }).click()
    await page.getByLabel('Title').fill(`${record.draft.title} governed`)
    await page.getByRole('button', { name: 'Save & check in' }).click()
    await expect(page.getByRole('heading', { name: `${record.draft.title} governed` })).toBeVisible()
    await expect(page.locator('.auditRow').filter({ hasText: 'Artifact Checked In' }).first()).toContainText('admin')

    page.once('dialog', dialog => dialog.accept('Administrator paused this governed package.'))
    await page.getByRole('button', { name: 'Defer', exact: true }).click()
    await expect(page.getByRole('button', { name: 'Reinstate', exact: true })).toBeVisible()
    await page.getByRole('button', { name: 'Reinstate', exact: true }).click()
    await expect(page.getByRole('button', { name: 'Check out & edit', exact: true })).toBeVisible()

    await page.goto(`${releaseRoot}/${record.route}/change-requests/${record.approved.id}`)
    await page.getByRole('button', { name: 'Revise', exact: true }).click()
    await expect(page).toHaveURL(new RegExp(`/${record.route}/change-requests/[^/]+$`))
    await expect(page.getByRole('button', { name: 'Check out & edit', exact: true })).toBeVisible()
    await expect(page.getByText('admin', { exact: true }).last()).toBeVisible()
  }
})
