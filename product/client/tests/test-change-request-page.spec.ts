import { expect, test, type Page, type Route } from '@playwright/test'
import { login } from './auth'

/**
 * A test change request is read on a page, the way a change request is.
 *
 * Clicking one opened a drawer over the coverage workspace headed "System test engineering decision" — the
 * assessment's view of the package rather than the package's own. There was no way to read its case, what it
 * proposes, what it was raised from, or to take away the controlled document an approver needs.
 *
 * These assert the sections the requirements change request page has, on the verification one, so the two
 * cannot drift apart again.
 */

const openRegister = async (page: Page) => {
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  await page.goto(new URL(`${root}/system-verification/change-requests`, page.url()).toString(), { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'System Test Change Requests', level: 1 })).toBeVisible({ timeout: 30_000 })
  return root
}

test('a package opens on its own page, not in a drawer', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)

  const row = page.locator('[data-register-row]').first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  const number = (await row.getAttribute('data-register-row'))!
  await row.click()

  // A page, addressed by the package — not an overlay on the page you were already on.
  await expect(page).toHaveURL(/\/system-verification\/change-requests\/[0-9a-f-]{36}$/, { timeout: 30_000 })
  await expect(page.getByText(`TEST CHANGE CONTROL / ${number}`)).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(0)
})

test('a seeded software Procedure package uses the shared shell, exact origin, workflow, and Procedure library', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await page.route('**/api/projects/*/configuration', async route => {
    const response = await route.fetch()
    const configuration = await response.json()
    configuration.effectiveSteps = configuration.effectiveSteps.map((step: { catalogueEntry: string }) => ({
      ...step,
      enabledArtifactKinds: step.catalogueEntry === 'System' ? ['Procedure'] : ['Case', 'Procedure'],
    }))
    await route.fulfill({ response, json: configuration })
  })
  await login(page)
  const current = new URL(page.url())
  const parts = current.pathname.split('/').filter(Boolean)
  const root = '/' + parts.slice(0, 6).join('/')
  const projectId = parts[3]
  const releaseId = parts[5]
  const packageId = '72500000-0000-0000-0000-000000000001'
  const procedureSearchUrls: string[] = []
  const listItem = {
    id: packageId, baseNumber: 'HLRTPCR-000725', revision: 0,
    displayNumber: 'HLRTPCR-000725.00', title: 'Procedure package from exact Case change',
    state: 'Draft', authorId: 'admin', targetReleaseId: releaseId,
    discipline: 'HighLevelSoftware', artifactKind: 'Procedure',
    artifactLabel: 'High-level software Procedure', artifactCount: 0, revisionCount: 1,
    updatedAt: '2026-08-23T12:00:00Z',
  }
  const detail = {
    ...listItem, projectId, releaseId, problem: 'A Case change needs a controlled Procedure.',
    analysis: 'The exact Case change found new verification work.',
    solution: 'Author and approve the Procedure in its own package.',
    problemRich: '{"blocks":[{"type":"paragraph","text":"A Case change needs a controlled Procedure."}]}',
    analysisRich: '{"blocks":[{"type":"paragraph","text":"The exact Case change found new verification work."}]}',
    solutionRich: '{"blocks":[{"type":"paragraph","text":"Author and approve the Procedure in its own package."}]}',
    version: 1, caseContractVersion: 2, artifactLevel: 'HighLevel', procedureLevel: 'HighLevel',
    sourceChangeRequestNumber: '', originKind: 'CaseChange',
    originReferenceId: '72500000-0000-0000-0000-000000000010',
    originDisplayLabel: 'Case change', originDisplayIdentity: 'HLRTC-000738.01',
    originDisplayTitle: 'Case change: update flight guidance',
    artifactChanges: [], procedureChanges: [], coveredChangeRequests: [],
    capabilities: { canProposeArtifactChange: true, canWithdrawArtifactChange: true, canRevise: false },
    reviewCycle: {
      sequence: 1, mode: 'Sequential', state: 'InReview',
      workflowName: 'High-level Software Procedure Review', workflowVersion: 2,
      steps: [{ position: 0, stageName: 'Procedure approval', authority: 'Procedure Approver',
        approverId: 'admin', approverName: 'Administrator', state: 'Active' }],
    },
  }
  const fulfill = (route: Route, body: unknown, status = 200) =>
    route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
  await page.route('**/api/history/test-change-requests*', route => fulfill(route, {
    items: [listItem], totalCount: 1, totalPages: 1, page: 1, pageSize: 50,
  }))
  await page.route('**/api/releases/*/test-change-reviews*', route => fulfill(route, { items: [detail] }))
  await page.route('**/api/test-change-reviews/' + packageId + '/case-changes',
    route => fulfill(route, { error: 'Procedure route required' }, 404))
  await page.route('**/api/test-change-reviews/' + packageId + '/procedure-changes',
    route => fulfill(route, detail))
  await page.route('**/api/signatures*', route => fulfill(route, []))
  await page.route('**/api/controlled-editing/status*',
    route => fulfill(route, { editable: true, locked: false, mine: false }))
  await page.route('**/api/controlled-editing/checkout', route => fulfill(route, {
    id: '72500000-0000-0000-0000-000000000002', version: 1, userName: 'admin',
    openedAt: '2026-08-23T12:00:00Z', lastActivityAt: '2026-08-23T12:00:00Z',
    expiresAt: '2026-08-23T13:00:00Z', resumed: false,
    draftJson: JSON.stringify({ packageVersion: 1, title: detail.title, problem: detail.problem,
      analysis: detail.analysis, solution: detail.solution, problemRich: detail.problemRich,
      analysisRich: detail.analysisRich, solutionRich: detail.solutionRich, procedureChanges: [] }),
  }))
  await page.route('**/api/test-procedures?*', async route => {
    procedureSearchUrls.push(route.request().url())
    await fulfill(route, { page: 1, pageSize: 8, totalCount: 1, totalPages: 1, items: [
      { id: '72500000-0000-0000-0000-000000000020', displayNumber: 'HLRTP-000700.00',
        title: 'Existing HLR controlled Procedure', revision: 0, level: 'HighLevel',
        state: 'Approved' },
    ] })
  })

  await page.goto(current.origin + root + '/software-verification/hlr/change-requests?kind=Procedure', { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Software Procedure Change Requests', level: 1 })).toBeVisible()
  await page.locator('[data-register-row]').first().click()
  await expect(page.getByRole('heading', { name: 'Procedure impact', level: 2 })).toBeVisible()
  await expect(page.getByText('Case change', { exact: true })).toBeVisible()
  await expect(page.getByText('HLRTC-000738.01', { exact: true })).toBeVisible()
  await expect(page.getByText('Case change: update flight guidance', { exact: false })).toBeVisible()
  await expect(page.getByText('Procedure approval', { exact: false })).toBeVisible()
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  await page.getByRole('button', { name: 'Modify existing' }).click()
  await page.getByLabel('Find controlled procedure 1').fill('existing HLR')
  await expect.poll(() => procedureSearchUrls.some(url => url.includes('search=existing%20HLR'))).toBeTruthy()
  expect(procedureSearchUrls.some(url => url.includes('artifactKind=Procedure') && url.includes('search=existing%20HLR'))).toBeTruthy()
  expect(procedureSearchUrls.every(url => !url.includes('/api/test-cases'))).toBeTruthy()
  await expect(page.getByText('HLRTP-000700.00', { exact: true })).toBeVisible()
  await expect(page.getByText('Controlled test procedure authoring', { exact: true })).toBeVisible()

  // Procedure work starts from an exact Case change/assessment, and the shared creator now supplies that
  // origin without changing the Case package path.
  let procedureCreateBody: Record<string, unknown> | undefined
  await page.route('**/api/releases/*/test-change-request-sources*', async route => {
    const url = new URL(route.request().url())
    if (url.searchParams.get('artifactKind') !== 'Procedure') return route.continue()
    await fulfill(route, [{ sourceKind: 'CaseChange', sourceId: '72500000-0000-0000-0000-000000000010',
      displayNumber: 'LLRTC-000738.01', title: 'LLR Case change origin', state: 'Approved', selectable: true }])
  })
  await page.route('**/api/releases/*/test-change-requests', async route => {
    procedureCreateBody = route.request().postDataJSON() as Record<string, unknown>
    await fulfill(route, { id: packageId, displayNumber: 'LLRTPCR-000725.00' }, 201)
  })
  await page.goto(current.origin + root + '/software-verification/llr/change-requests?kind=Procedure', { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Software Procedure Change Requests', level: 1 })).toBeVisible()
  await page.goto(current.origin + root + '/software-verification/llr/change-requests/new?kind=Procedure', { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'Create LLR Test Procedure Change Request', level: 1 })).toBeVisible()
  await expect(page.getByText('Eligible LLR Case origins for this Procedure package')).toBeVisible()
  await page.locator('label').filter({ hasText: 'LLRTC-000738.01' }).getByRole('radio').check()
  const editor = page.locator('[data-tcr-editor]')
  await editor.getByLabel('Title').fill('LLR Procedure package from exact Case origin')
  for (const field of ['Problem', 'Analysis', 'Solution'])
    await editor.getByLabel(field).fill(`${field} for an exact LLR Procedure package.`)
  await page.getByRole('button', { name: 'Raise LLRTPCR' }).click()
  await expect.poll(() => procedureCreateBody).toBeTruthy()
  expect(procedureCreateBody?.artifactKind).toBe('Procedure')
  expect(procedureCreateBody?.caseChangeIds).toEqual(['72500000-0000-0000-0000-000000000010'])
  expect(procedureCreateBody).not.toHaveProperty('changeRequestIds')
  expect(procedureCreateBody).not.toHaveProperty('problemReportIds')
})

test('the page carries the same sections the requirements change request page carries', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)
  await page.locator('[data-register-row]').first().click()
  await expect(page.getByText(/TEST CHANGE CONTROL \//)).toBeVisible({ timeout: 30_000 })

  for (const section of ['Change case', 'Raised from', 'Supporting files', 'Procedure impact', 'Control status']) {
    await expect(page.getByRole('heading', { name: section, level: 2 })).toBeVisible()
  }
  // The change case is Problem-Analysis-Solution here as it is there, not a paragraph of prose.
  for (const part of ['Problem', 'Analysis', 'Solution']) {
    await expect(page.locator('.pasView article').filter({ hasText: part })).toBeVisible()
  }
  // Allocation and state are two separate answers, as on the requirements page.
  const control = page.locator('.controlStatusCard')
  await expect(control.getByText('Allocation')).toBeVisible()
  await expect(control.getByText('State', { exact: true })).toBeVisible()
  // The review-cycle rail stays present even when a historical package has no cycle evidence to show.
  // That keeps the page structure aligned without inventing a workflow the record never entered.
  await expect(page.getByRole('heading', { name: /Review cycle(?: \d+)?/, level: 2 })).toBeVisible()
})

test('check out and edit uses the same full-page two-stage authoring flow as a requirement change', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)
  await page.getByLabel('Lifecycle state filter').selectOption('Draft')
  await page.locator('[data-register-row]').first().click()
  const exactUrl = page.url()

  await page.getByRole('button', { name: 'Check out & edit' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByRole('navigation', { name: 'Checked-out authoring progress' })).toBeVisible()
  await expect(page.getByRole('link', { name: /Change case/ })).toBeVisible()
  await expect(page.getByRole('link', { name: /Procedure changes/ })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Discard checkout' })).toBeVisible()
  // Visible was never the question. Nothing has been edited since checkout, and in that state Save greyed
  // because there were no unsaved changes and check-in greyed because the working copy matched the snapshot —
  // so the only way out of an accidental checkout was Discard, which throws work away rather than handing the
  // lock back. Save is now available whenever a working copy is held.
  const save = page.getByRole('button', { name: 'Save', exact: true })
  await expect(save).toBeEnabled({ timeout: 30_000 })
  await save.click()
  // Still available after an explicit save, which is the moment it used to disappear.
  await expect(save).toBeEnabled({ timeout: 30_000 })

  // And a check-in that genuinely cannot proceed says why. The implication is the assertion: whenever the
  // control is unavailable a reason is on screen, which is false on the behaviour this replaces because
  // nothing was ever rendered beside a greyed button.
  const checkIn = page.getByRole('button', { name: 'Save & check in' })
  await expect(checkIn).toBeVisible()
  if (!(await checkIn.isEnabled())) await expect(page.locator('.checkInBlockedReason')).toBeVisible()
  await expect(page).toHaveURL(exactUrl)
  await page.getByRole('button', { name: 'Discard checkout' }).click()
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toBeVisible()
})

test('the shared authoring page checks in and reopens the persisted test change case', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)
  await page.getByLabel('Lifecycle state filter').selectOption('Draft')
  await page.locator('[data-register-row]').first().click()

  const title = `Verification parity check-in ${Date.now()}`
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  await page.getByLabel('Title').fill(title)
  await expect(page.getByRole('button', { name: 'Save & check in' })).toBeEnabled({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Save & check in' }).click()

  await expect(page.getByRole('heading', { name: title, level: 1 })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: title, level: 1 })).toBeVisible({ timeout: 30_000 })
})

test('the controlled publication is offered from the package', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)
  await page.locator('[data-register-row]').first().click()
  await expect(page.getByText(/TEST CHANGE CONTROL \//)).toBeVisible({ timeout: 30_000 })

  // An approver reading a package outside the product needed the document a change request has always had.
  await expect(page.getByText('Professional controlled publication')).toBeVisible()
  const docx = page.getByRole('link', { name: 'Download DOCX' })
  await expect(docx).toBeVisible()
  await expect(docx).toHaveAttribute('href', /\/api\/test-change-reviews\/[0-9a-f-]{36}\/download\?format=docx/)
  await expect(page.getByRole('link', { name: 'Download PDF' })).toBeVisible()
})

test('a draft package can be put away and taken back off the shelf', async ({ page }) => {
  test.setTimeout(180_000)
  await page.setViewportSize({ width: 1600, height: 900 })
  await login(page)
  await openRegister(page)

  // A Draft, because that is the state deferral is offered from.
  await page.getByLabel('Lifecycle state filter').selectOption('Draft')
  const row = page.locator('[data-register-row]').first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.click()
  await expect(page.getByText(/TEST CHANGE CONTROL \//)).toBeVisible({ timeout: 30_000 })

  page.once('dialog', dialog => dialog.accept('Dropped from this build.'))
  await page.getByRole('button', { name: 'Defer' }).click()
  await expect(page.locator('.controlStatusCard').getByText('Deferred')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('Put away because: Dropped from this build.')).toBeVisible()

  await page.getByRole('button', { name: 'Reinstate' }).click()
  // Off the shelf and back to where it was, which for a Draft is a Draft.
  await expect(page.locator('.controlStatusCard').getByText('Draft')).toBeVisible({ timeout: 30_000 })
})
