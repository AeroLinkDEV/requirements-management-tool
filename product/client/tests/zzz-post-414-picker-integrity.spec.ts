import { expect, test, type APIRequestContext, type Locator, type Page, type Route } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram } from './auth'

/**
 * Post-#414 picker integrity.
 *
 * Two late inline review comments against the exact merged #414 head:
 *
 * A. TestingCoverageWorkspace.load wrote effectiveBaseline before the load-ticket check, so a delayed
 *    build-context response from a previously displayed release could overwrite the active build's
 *    effective baseline and feed the wrong requirement picker.
 * B. The multi-select requirement pickers serialized every selected requirement revision ID into the
 *    ids query parameter. At roughly 200+ UUID selections the GET request line exceeds the server
 *    limit (Kestrel default 8192 bytes), the failed response was silently ignored, and the picker froze.
 *
 * These journeys force both failures deterministically against disposable data: the stale response is
 * held and released last, and the volume fixture genuinely selects past the former request-line bound.
 */

const impacts = JSON.stringify({
  trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
  baseline: 'Not Affected', collaboration: 'Not Affected',
})

async function introduceRequirements(
  request: APIRequestContext,
  projectId: string,
  releaseId: string,
  count: number,
  suffix: string,
) {
  const sections = await (await request.get(
    `${apiBase}/api/authoring/sections?projectId=${projectId}&level=System`,
  )).json()
  const sectionId = (sections as { id: string }[])[0]?.id
  expect(sectionId).toBeTruthy()
  const requirementChanges = Array.from({ length: count }, (_, index) => ({
    level: 'System', kind: 'Introduce',
    statement: `The picker integrity product shall satisfy requirement ${index + 1}.`,
    rationale: 'Picker integrity volume.',
    verificationMethod: 'Test',
    impactDispositionJson: impacts,
    targetSectionId: sectionId,
  }))
  const created = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId,
    targetReleaseId: releaseId,
    type: 'System',
    title: `Picker integrity change ${suffix}`,
    problem: 'Picker reachability must hold at realistic scale.',
    analysis: 'The picker must stay bounded and truthful.',
    solution: 'Introduce requirements and author a procedure for the last one.',
    requirementChanges,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const draft = await created.json()
  const submitted = await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: {
    expectedVersion: draft.version,
    approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }],
    mode: 'Sequential',
  } })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: {
    password: 'AeroLink!2026',
    meaning: 'Approved for picker-integrity journey verification.',
  } })
  expect(approved.ok(), await approved.text()).toBeTruthy()
  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: `SW-${String(suffix).padStart(2, '0')}.00`, revision: 0,
    projectId, releaseId, predecessorBaselineId: null, name: 'Picker integrity baseline',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const [path, data] of [
    ['selections', { changeRequestId: draft.id }],
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy()
  }
  return { draft, baseline }
}

/** The impact subject for the requirement whose statement names the given ordinal. */
async function impactSubject(
  request: APIRequestContext,
  releaseId: string,
  ordinal: number,
) {
  const impactItems = await (await request.get(
    `${apiBase}/api/releases/${releaseId}/verification-impact`,
  )).json() as { subjectDisplayNumber: string; subjectStatement?: string }[]
  const subject = impactItems.find(entry => entry.subjectStatement?.includes(`requirement ${ordinal}.`))
  expect(subject, `impact subject for requirement ${ordinal}`).toBeTruthy()
  return subject!.subjectDisplayNumber
}

/** The baseline's requirements in the picker's deterministic BaseNumber order. */
async function orderedBaselineRequirements(
  request: APIRequestContext,
  projectId: string,
  baselineId: string,
) {
  const pages = []
  for (let page = 1; page <= 2; page++) {
    const body = await (await request.get(
      `${apiBase}/api/requirements?projectId=${projectId}&scope=System&baselineId=${baselineId}&page=${page}&pageSize=200`,
    )).json()
    pages.push(...(body as { items: { displayNumber: string }[] }).items)
  }
  return pages.map(item => item.displayNumber)
}

async function openSystemAssessment(page: Page, displayNumber: string) {
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Test Change Requests' }).click()
  const row = page.locator('.downstreamAssessment').filter({ hasText: displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await row.getByRole('button', { name: 'Open assessment' }).click()
  const assessment = page.getByRole('dialog', { name: /test impact/ })
  await expect(assessment).toBeVisible({ timeout: 30_000 })
  return { row, assessment }
}

async function decideNewProcedure(page: Page, assessment: Locator, subject: string) {
  const item = assessment.locator('.decisionList li').filter({ hasText: subject }).first()
  await item.getByRole('button', { name: 'Decide' }).click()
  const decide = page.getByRole('dialog', { name: /Decide / })
  await decide.getByLabel('Decision').selectOption('NewProcedureRequired')
  await decide.getByLabel('Rationale').fill('A new procedure must be written for this requirement.')
  await decide.getByRole('button', { name: 'Record decision' }).click()
  await expect(decide).toHaveCount(0, { timeout: 30_000 })
}

test('a stale released-build context cannot overwrite the active build effective baseline', async ({ page, request }) => {
  test.setTimeout(420_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const created = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Stale Context ${suffix}`,
    programCode: `SC${suffix}`,
    projectName: 'Stale Context Project',
    softwareProduct: 'Stale Context Product',
    initialRelease: '1.5',
    initialReleaseIsReleased: true,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json()
  const releasedId = workspace.release.id as string
  const successor = await request.post(`${apiBase}/api/releases`, { data: {
    projectId: workspace.project.id,
    version: '1.6',
    predecessorReleaseId: releasedId,
  } })
  expect(successor.ok(), await successor.text()).toBeTruthy()
  const activeReleaseId = (await successor.json()).id as string

  const { draft, baseline } = await introduceRequirements(
    request, workspace.project.id, activeReleaseId, 20, suffix)
  const activeContext = await (await request.get(
    `${apiBase}/api/build-context?projectId=${workspace.project.id}&releaseId=${activeReleaseId}`,
  )).json()
  const activeBaselineId = activeContext.effectiveBaselineId as string
  expect(activeBaselineId).toBeTruthy()
  const ordered = await orderedBaselineRequirements(request, workspace.project.id, baseline.id)
  const subject = await impactSubject(request, activeReleaseId, 20)

  // The TCR and the NewProcedureRequired decision exist before the race, so the quick-authoring picker
  // is reachable afterwards without mutating shared state.
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, `Stale Context ${suffix}`)
  const { assessment } = await openSystemAssessment(page, draft.displayNumber)
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })
  await decideNewProcedure(page, assessment, subject)
  // The workspace stays mounted across the release switch, so close the assessment before the race.
  await assessment.getByRole('button', { name: /Close test assessment/ }).click()
  await expect(assessment).toHaveCount(0)

  // Hold the released build's build-context response so it can only land AFTER the active build loads.
  // The release switch must keep the workspace component mounted: browser Back/Forward between two
  // same-view URLs changes only the release id through the app's popstate handler, which is the path a
  // stale in-flight load can actually write through.
  const held: Route[] = []
  const staleBaselineId = '11111111-1111-1111-1111-111111111111'
  await page.route('**/api/build-context**', async route => {
    const url = new URL(route.request().url())
    if (url.searchParams.get('releaseId') === releasedId) { held.push(route); return }
    await route.continue()
  })
  const coveragePath = (releaseId: string) =>
    `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${releaseId}/system-verification/coverage`
  const switchRelease = (releaseId: string) => page.evaluate((url) => {
    history.pushState({}, '', url)
    window.dispatchEvent(new PopStateEvent('popstate'))
  }, coveragePath(releaseId))

  // Move to the released build through SPA history (same view, same mounted workspace) and hold its
  // build-context response.
  await switchRelease(releasedId)
  await expect.poll(() => held.length).toBeGreaterThan(0)

  // Switch back to the active build through SPA history and let its load finish completely.
  await switchRelease(activeReleaseId)
  const activeRow = page.locator('.downstreamAssessment').filter({ hasText: draft.displayNumber }).first()
  await expect(activeRow).toBeVisible({ timeout: 30_000 })

  // Release the stale response LAST, with a distinct fake baseline so the assertion cannot pass by luck.
  for (const route of held) {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ effectiveBaselineId: staleBaselineId }) })
  }

  // The active build's requirement picker must still query the active build's exact baseline.
  let pickerUrl = ''
  page.on('request', req => {
    if (req.method() === 'GET' && req.url().includes('/api/requirements?')) pickerUrl = req.url()
  })
  await activeRow.getByRole('button', { name: 'Open assessment' }).click()
  const reopened = page.getByRole('dialog', { name: /test impact/ })
  const decided = reopened.locator('.decisionList li').filter({ hasText: subject }).first()
  await decided.getByRole('button', { name: 'Author the procedure' }).click()
  const authoring = page.getByRole('dialog', { name: 'Propose a test procedure' })
  await expect.poll(() => pickerUrl).not.toBe('')
  expect(new URL(pickerUrl).searchParams.get('baselineId')).toBe(activeBaselineId)
  // Only the active build's requirements are offered.
  await expect(authoring.locator('select[name="requirement"] option').first()).toBeVisible({ timeout: 30_000 })
  await expect(authoring.locator('select[name="requirement"] option')).toHaveCount(20, { timeout: 30_000 })
  await expect(authoring.locator('select[name="requirement"] option').first()).toContainText(ordered[0])
})

test('a multi-page requirement selection keeps the picker request line bounded and stays fully editable', async ({ page, request }) => {
  test.setTimeout(600_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Bounded Selection ${suffix}`,
    programCode: `BS${suffix}`,
    projectName: 'Bounded Selection Project',
    softwareProduct: 'Bounded Selection Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const { draft, baseline } = await introduceRequirements(
    request, workspace.project.id, workspace.release.id, 300, suffix)
  const ordered = await orderedBaselineRequirements(request, workspace.project.id, baseline.id)
  expect(ordered).toHaveLength(300)
  const subject = await impactSubject(request, workspace.release.id, 300)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, `Bounded Selection ${suffix}`)
  const { assessment } = await openSystemAssessment(page, draft.displayNumber)
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })
  await decideNewProcedure(page, assessment, subject)

  const decided = assessment.locator('.decisionList li').filter({ hasText: subject }).first()
  await decided.getByRole('button', { name: 'Author the procedure' }).click()
  const authoring = page.getByRole('dialog', { name: 'Propose a test procedure' })
  const select = authoring.locator('select[name="requirement"]')
  await expect(select.locator('option').first()).toBeVisible({ timeout: 30_000 })
  await expect(authoring).toContainText('300 requirements in scope', { timeout: 30_000 })

  const requestUrls: string[] = []
  const responseStatuses: number[] = []
  page.on('request', req => {
    if (req.method() === 'GET' && req.url().includes('/api/requirements?')) requestUrls.push(req.url())
  })
  page.on('response', res => {
    if (res.url().includes('/api/requirements?')) responseStatuses.push(res.status())
  })

  // Select every requirement across all pages (300), keeping the seeded last requirement selected.
  let currentPage = 1
  while (true) {
    const values = await select.locator('option').evaluateAll(
      options => options.map(option => (option as HTMLOptionElement).value).filter(Boolean))
    const selected = await select.evaluate(
      element => Array.from((element as HTMLSelectElement).selectedOptions).map(option => option.value))
    await select.selectOption([...new Set([...selected, ...values])])
    const next = authoring.getByRole('button', { name: 'Next' })
    if (!(await next.isEnabled())) break
    await next.click()
    await expect(select.locator('option').filter({ hasText: ordered[currentPage * 50] }))
      .toHaveCount(1, { timeout: 15_000 })
    currentPage++
  }
  const selectedCount = await select.evaluate(
    element => Array.from((element as HTMLSelectElement).selectedOptions).length)
  expect(selectedCount).toBe(300)

  // A search that excludes the whole selection must keep the request line bounded: the complete selected
  // ID set must never be serialized into the URL, and a rejected request must not silently freeze the UI.
  requestUrls.length = 0
  responseStatuses.length = 0
  await authoring.getByRole('textbox', { name: 'Search requirements' }).fill('zz-no-such-requirement-zz')
  await expect(authoring).toContainText('0 matching requirements.', { timeout: 15_000 })
  await expect(authoring).toContainText('300 current selections are kept visible independently of the search.', { timeout: 15_000 })
  await expect(select.locator('option')).toHaveCount(300, { timeout: 15_000 })
  expect(await select.evaluate(
    element => Array.from((element as HTMLSelectElement).selectedOptions).length)).toBe(300)
  expect(requestUrls.some(url => url.includes('ids='))).toBe(false)
  expect(responseStatuses.some(status => status === 414 || status === 431)).toBe(false)

  // Returning to the full universe keeps every selection represented and editable.
  await authoring.getByRole('textbox', { name: 'Search requirements' }).fill('')
  await expect(authoring).toContainText('300 requirements in scope', { timeout: 15_000 })
  await expect(select.locator('option')).toHaveCount(300, { timeout: 15_000 })

  await authoring.getByLabel('Title').fill(`Picker integrity procedure ${suffix}`)
  await authoring.getByLabel('Objective').fill('Verify the picker integrity requirements.')
  await authoring.getByLabel('Preconditions').fill('The picker integrity configuration is available.')
  await authoring.getByLabel('Steps').fill('1. Load the configuration. 2. Exercise the behavior.')
  await authoring.getByLabel('Expected result').fill('The expected behavior is observed.')
  await authoring.getByLabel('Why it is needed').fill('Nothing covers the picker integrity requirements yet.')
  await authoring.getByRole('button', { name: 'Propose procedure' }).click()
  await expect(authoring).toHaveCount(0, { timeout: 30_000 })

  // The exact intended set reaches authoritative server validation and persists.
  const reviews = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`,
  )).json()
  const tcr = (reviews as { items: { id: string }[] }).items[0]
  const payload = await (await request.get(
    `${apiBase}/api/test-change-reviews/${tcr.id}/procedure-changes`,
  )).json()
  const proposed = (payload as { procedureChanges: { kind: string; drivingRequirementRevisionIds: string[] }[] })
    .procedureChanges.find(change => change.kind === 'Introduce')
  expect(proposed).toBeTruthy()
  expect(proposed!.drivingRequirementRevisionIds.length).toBe(300)
})

test('the TCR driving-requirement picker stays bounded with a large selection', async ({ page, request }) => {
  test.setTimeout(600_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Driving Bounded ${suffix}`,
    programCode: `DB${suffix}`,
    projectName: 'Driving Bounded Project',
    softwareProduct: 'Driving Bounded Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const { draft, baseline } = await introduceRequirements(
    request, workspace.project.id, workspace.release.id, 220, suffix)
  const ordered = await orderedBaselineRequirements(request, workspace.project.id, baseline.id)
  expect(ordered).toHaveLength(220)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, `Driving Bounded ${suffix}`)
  const { assessment } = await openSystemAssessment(page, draft.displayNumber)
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })

  // Open the TCR workspace's Introduce drawer: its governed candidate universe is the 220 requirements
  // this package's source change introduced.
  await assessment.getByRole('button', { name: /^SYSTCR-\d{6}\.\d{2}$/ }).click()
  const workspaceDrawer = page.getByRole('dialog', { name: /procedure decisions/ })
  await workspaceDrawer.getByRole('button', { name: 'Propose a procedure change' }).click()
  const drawer = page.getByRole('dialog', { name: 'Propose a procedure change' })
  const fieldset = drawer.locator('fieldset.drivingRequirements').last()
  await expect(fieldset).toContainText('220 governed requirements in scope', { timeout: 30_000 })

  const requestUrls: string[] = []
  const responseStatuses: number[] = []
  page.on('request', req => {
    if (req.method() === 'GET' && req.url().includes('/requirement-candidates?')) requestUrls.push(req.url())
  })
  page.on('response', res => {
    if (res.url().includes('/requirement-candidates?')) responseStatuses.push(res.status())
  })

  // Check every governed requirement across all pages: far past the former request-line bound.
  let currentPage = 1
  while (true) {
    const boxes = fieldset.locator('input[type="checkbox"]')
    const count = await boxes.count()
    for (let i = 0; i < count; i++) {
      if (!(await boxes.nth(i).isChecked())) await boxes.nth(i).check()
    }
    const next = fieldset.getByRole('button', { name: 'Next' })
    if (!(await next.isEnabled())) break
    await next.click()
    await expect(fieldset).toContainText(ordered[currentPage * 50], { timeout: 15_000 })
    currentPage++
  }
  expect(await fieldset.locator('input[type="checkbox"]:checked').count()).toBe(220)

  requestUrls.length = 0
  responseStatuses.length = 0
  await drawer.getByRole('textbox', { name: 'Search requirements' }).fill('zz-no-such-requirement-zz')
  await expect(drawer).toContainText('0 matching governed requirements.', { timeout: 15_000 })
  await expect(drawer).toContainText('220 current selections are kept visible independently of the search.', { timeout: 15_000 })
  await expect(fieldset.locator('input[type="checkbox"]')).toHaveCount(220, { timeout: 15_000 })
  expect(await fieldset.locator('input[type="checkbox"]:checked').count()).toBe(220)
  expect(requestUrls.some(url => url.includes('ids='))).toBe(false)
  expect(responseStatuses.some(status => status === 414 || status === 431)).toBe(false)

  // A real Introduce decision with the full exact selection is accepted and persists.
  await drawer.getByLabel('Title').fill(`Driving bounded procedure ${suffix}`)
  await drawer.getByLabel('Objective').fill('Verify the driving bounded requirements.')
  await drawer.getByLabel('Preconditions').fill('The configuration is available.')
  await drawer.getByLabel('Steps').fill('1. Load. 2. Exercise.')
  await drawer.getByLabel('Expected result').fill('The expected behavior is observed.')
  await drawer.getByLabel('Why this procedure work is required').fill('Nothing covers these requirements yet.')
  await drawer.getByRole('button', { name: 'Propose decision' }).click()
  await expect(drawer).toHaveCount(0, { timeout: 30_000 })

  const reviews = await (await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`,
  )).json()
  const tcr = (reviews as { items: { id: string }[] }).items[0]
  const payload = await (await request.get(
    `${apiBase}/api/test-change-reviews/${tcr.id}/procedure-changes`,
  )).json()
  const proposed = (payload as { procedureChanges: { kind: string; drivingRequirementRevisionIds: string[] }[] })
    .procedureChanges.find(change => change.kind === 'Introduce')
  expect(proposed).toBeTruthy()
  expect(proposed!.drivingRequirementRevisionIds.length).toBe(220)
})

test('a failed picker response shows a visible error and recovers instead of silently freezing', async ({ page, request }) => {
  test.setTimeout(360_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Picker Failure ${suffix}`,
    programCode: `PF${suffix}`,
    projectName: 'Picker Failure Project',
    softwareProduct: 'Picker Failure Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const { draft } = await introduceRequirements(
    request, workspace.project.id, workspace.release.id, 5, suffix)
  const subject = await impactSubject(request, workspace.release.id, 5)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, `Picker Failure ${suffix}`)
  const { assessment } = await openSystemAssessment(page, draft.displayNumber)
  await assessment.getByRole('button', { name: 'SYSTCR required', exact: true }).click()
  await expect(assessment).toContainText('SYSTCR Created', { timeout: 30_000 })
  await decideNewProcedure(page, assessment, subject)

  const decided = assessment.locator('.decisionList li').filter({ hasText: subject }).first()
  await decided.getByRole('button', { name: 'Author the procedure' }).click()
  const authoring = page.getByRole('dialog', { name: 'Propose a test procedure' })
  await expect(authoring.locator('select[name="requirement"] option').first()).toBeVisible({ timeout: 30_000 })

  // Force the next requirement search to fail; the failure must be visible, not a silent freeze.
  await page.route('**/api/requirements?**', async route => {
    await route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"forced failure"}' })
  })
  await authoring.getByRole('textbox', { name: 'Search requirements' }).fill('zz-forced-failure-zz')
  await expect(authoring.getByRole('alert')).toContainText('could not be loaded', { timeout: 15_000 })

  // Retry recovers coherently once the server responds again.
  await page.unroute('**/api/requirements?**')
  await authoring.getByRole('textbox', { name: 'Search requirements' }).fill('')
  await expect(authoring.getByRole('alert')).toHaveCount(0, { timeout: 15_000 })
  await expect(authoring.locator('select[name="requirement"] option').first()).toBeVisible({ timeout: 15_000 })
})
