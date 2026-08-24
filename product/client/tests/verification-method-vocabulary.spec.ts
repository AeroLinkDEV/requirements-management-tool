import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { apiBase, login } from './auth'

/**
 * #701: verification method used to be free text, so one project could hold "Test", "test" and "Testing" on
 * requirements that meant the same thing. These journeys prove the shape end to end — a project declares its
 * permitted methods, authoring offers exactly those, review refuses anything else by name, and a stored value
 * that does not match is reported rather than quietly corrected.
 */

type Workspace = {
  program: { id: string }
  project: { id: string; name: string }
  release: { id: string }
}

type Draft = { id: string; version: number; displayNumber: string }

async function createWorkspaceAsync(request: APIRequestContext, suffix: string): Promise<Workspace> {
  const created = await request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName: `Vocabulary UI ${suffix}`,
      programCode: `VU${suffix}`,
      projectName: `Vocabulary UI Project ${suffix}`,
      softwareProduct: 'Vocabulary UI Software',
      initialRelease: '1.0',
      initialReleaseIsReleased: false,
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  return await created.json() as Workspace
}

async function systemSectionAsync(request: APIRequestContext, projectId: string): Promise<string> {
  const response = await request.get(`${apiBase}/api/authoring/sections?projectId=${projectId}&level=System`)
  expect(response.ok(), await response.text()).toBeTruthy()
  const sections = await response.json() as { id: string }[]
  expect(sections.length, 'the new project has a System requirements document section').toBeGreaterThan(0)
  return sections[0].id
}

async function draftAsync(request: APIRequestContext, projectId: string, releaseId: string, sectionId: string,
  title: string, verificationMethod: string): Promise<Draft> {
  const created = await request.post(`${apiBase}/api/change-request-drafts`, {
    data: {
      projectId,
      targetReleaseId: releaseId,
      type: 'System',
      title,
      problem: 'Verification declarations must be controlled',
      analysis: 'A free-text method fragments the controlled record',
      solution: 'Declare a method the project permits',
      requirementChanges: [{
        level: 'System',
        kind: 'Introduce',
        targetSectionId: sectionId,
        statement: `The FMS shall sequence oceanic waypoints for ${title}.`,
        rationale: 'Controlled verification declaration',
        verificationMethod,
      }],
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  return await created.json() as Draft
}

async function declaredMethodAsync(request: APIRequestContext, changeRequestId: string) {
  const detail = await (await request.get(`${apiBase}/api/change-requests/${changeRequestId}`)).json() as
    { state: string; version: number; requirementChanges: { verificationMethod: string }[] }
  return detail
}

const projectSlug = (name: string) => name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')

test('Project Configuration declares the vocabulary, authoring offers exactly it, and an off-vocabulary value is reported not rewritten', async ({ page }) => {
  test.setTimeout(150_000)
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const workspace = await createWorkspaceAsync(page.request, suffix)
  const projectId = workspace.project.id
  const releaseId = workspace.release.id
  const sectionId = await systemSectionAsync(page.request, projectId)

  // Written before anyone narrowed the vocabulary: exactly the fragmentation #701 exists to surface.
  const historical = await draftAsync(page.request, projectId, releaseId, sectionId, 'Historical wording', 'Testing')

  await page.goto(`/projects/${projectSlug(workspace.project.name)}/configuration`)
  await expect(page.getByRole('heading', { name: 'Project configuration', level: 1 })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: /Verification methods/ }).click()
  await expect(page.getByRole('heading', { name: 'Verification methods', level: 2 })).toBeVisible()

  // A project created today carries a persisted vocabulary, and the screen says so rather than implying one.
  await expect(page.getByText('configured for this project')).toBeVisible()
  for (const [index, expected] of ['Test', 'Analysis', 'Inspection', 'Demonstration'].entries())
    await expect(page.getByLabel(`Verification method ${index + 1}`)).toHaveValue(expected)
  await expect(page.getByLabel('Verification method 5')).toHaveCount(0)

  // The reconciliation report names the stored value the vocabulary does not permit, with its provenance.
  const report = page.locator('.relationshipEditor').filter({ hasText: 'Stored values outside the vocabulary' })
  await expect(report.locator('code', { hasText: 'Testing' })).toBeVisible()
  await expect(report.getByText('Nothing here is rewritten by this screen')).toBeVisible()

  // A programme adds its own method.
  await page.getByLabel('New verification method').fill('Similarity')
  await page.getByRole('button', { name: 'Add method' }).click()
  await expect(page.getByLabel('Verification method 5')).toHaveValue('Similarity')
  await page.getByLabel('Verification vocabulary reason')
    .fill('This programme verifies by similarity to a qualified predecessor')
  await page.getByRole('button', { name: 'Save vocabulary' }).click()
  await expect(page.getByRole('status'))
    .toContainText('Permitted verification methods saved: Test, Analysis, Inspection, Demonstration, Similarity.')

  // Reading and editing the configuration changed no controlled record.
  expect((await declaredMethodAsync(page.request, historical.id)).requirementChanges[0].verificationMethod)
    .toBe('Testing')

  // Authoring offers exactly the configured vocabulary, as a select. There is no free-text box in which
  // "test" could be introduced beside "Test" — the option list is the project's own declaration.
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${releaseId}`
  await page.goto(`${root}/systems/change-requests/new`)
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: '+ Introduce System requirement' }).click()
  const method = page.getByLabel('Verification method')
  await expect(method).toHaveCount(1)
  await expect(method.locator('option')).toHaveText(['Test', 'Analysis', 'Inspection', 'Demonstration', 'Similarity'])
  await method.selectOption('Similarity')
  await expect(method).toHaveValue('Similarity')
})

test('A canonical method reaches review while an off-vocabulary one is refused, and the controlled output carries the configured value', async ({ page }) => {
  test.setTimeout(210_000)
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const workspace = await createWorkspaceAsync(page.request, suffix)
  const projectId = workspace.project.id
  const releaseId = workspace.release.id
  const sectionId = await systemSectionAsync(page.request, projectId)

  const narrowed = await page.request.put(`${apiBase}/api/projects/${projectId}/verification-methods`, {
    data: { expectedVersion: 1, reason: 'This programme verifies by inspection only', methods: ['Inspection'] },
  })
  expect(narrowed.ok(), await narrowed.text()).toBeTruthy()

  // A near miss is refused at the authoritative transition, and the refusal names what is permitted. The
  // draft keeps saying what its author wrote; nothing is re-spelled on the way to an approver.
  const nearMiss = await draftAsync(page.request, projectId, releaseId, sectionId, 'Near miss', 'inspection')
  const refused = await page.request.post(`${apiBase}/api/change-requests/${nearMiss.id}/submit`, {
    data: {
      expectedVersion: nearMiss.version, mode: 'Sequential',
      approvers: [{ userId: 'admin', name: 'Administrator' }],
    },
  })
  expect(refused.status(), await refused.text()).toBe(400)
  expect(await refused.text()).toContain('Permitted verification methods: Inspection.')
  const afterRefusal = await declaredMethodAsync(page.request, nearMiss.id)
  expect(afterRefusal.state).toBe('Draft')
  expect(afterRefusal.version).toBe(nearMiss.version)
  expect(afterRefusal.requirementChanges[0].verificationMethod).toBe('inspection')

  // The stored near miss is reported for a deliberate correction rather than corrected behind the reader.
  const reported = await (await page.request.get(`${apiBase}/api/projects/${projectId}/verification-methods`)).json() as
    { methods: string[]; nonConforming: { value: string; changeCount: number; revisionCount: number }[] }
  expect(reported.methods).toEqual(['Inspection'])
  expect(reported.nonConforming.map(row => row.value)).toEqual(['inspection'])
  expect(reported.nonConforming[0].changeCount).toBe(1)
  expect(reported.nonConforming[0].revisionCount).toBe(0)

  // The exact configured spelling goes all the way through to controlled output.
  const accepted = await draftAsync(page.request, projectId, releaseId, sectionId, 'Canonical declaration', 'Inspection')
  const submitted = await page.request.post(`${apiBase}/api/change-requests/${accepted.id}/submit`, {
    data: {
      expectedVersion: accepted.version, mode: 'Sequential',
      approvers: [{ userId: 'admin', name: 'Administrator' }],
    },
  })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const inReview = await declaredMethodAsync(page.request, accepted.id)
  const approved = await page.request.post(`${apiBase}/api/change-requests/${accepted.id}/approve`, {
    data: {
      password: 'AeroLink!2026',
      meaning: 'Approve the canonical verification declaration.',
      expectedVersion: inReview.version,
    },
  })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const baseline = await (await page.request.post(`${apiBase}/api/baselines`, {
    data: {
      baseNumber: 'SW-01.00', revision: 0, projectId, releaseId,
      predecessorBaselineId: null, name: 'Vocabulary baseline',
    },
  })).json() as { id: string }
  const selected = await page.request.post(`${apiBase}/api/baselines/${baseline.id}/selections`, {
    data: { changeRequestId: accepted.id },
  })
  expect(selected.ok(), await selected.text()).toBeTruthy()
  const frozen = await page.request.post(`${apiBase}/api/baselines/${baseline.id}/freeze`, { data: {} })
  expect(frozen.ok(), await frozen.text()).toBeTruthy()
  const materialized = await page.request.post(
    `${apiBase}/api/baselines/${baseline.id}/materialize-requirements`, { data: {} })
  expect(materialized.ok(), await materialized.text()).toBeTruthy()
  const generated = await page.request.post(
    `${apiBase}/api/baselines/${baseline.id}/generate-documents`, { data: {} })
  expect(generated.ok(), await generated.text()).toBeTruthy()

  // The materialized revision carries the configured spelling, and the generated requirements document
  // renders exactly that value.
  const published = await (await page.request.get(`${apiBase}/api/baselines/${baseline.id}/swrd`)).json() as
    { requirements: { verificationMethod: string }[] }
  expect(published.requirements.length).toBeGreaterThan(0)
  expect(published.requirements.map(row => row.verificationMethod)).toEqual(
    published.requirements.map(() => 'Inspection'))

  // And the historical near miss is still exactly as its author wrote it, after all of that.
  expect((await declaredMethodAsync(page.request, nearMiss.id)).requirementChanges[0].verificationMethod)
    .toBe('inspection')
})

/**
 * #701 review finding 2: a proposal must never be created while the project has not yet said what it
 * permits. It used to be, with `verificationMethod: ""` in state while the select — whose value matched no
 * option — displayed the browser's fallback first entry. The author read a method the payload did not carry.
 */

const vocabularyRoute = '**/api/projects/*/verification-methods'

/**
 * The authoring surfaces' own blocked notice. Scoped by class because these pages already carry an unrelated
 * `role="status"` for draft state, and an unscoped role query is ambiguous.
 */
const blockedNotice = (page: Page) => page.locator('.proposalUnavailable').filter({ hasText: 'verification' })

/** Holds the vocabulary response until `release()` is called, so the loading window can be inspected. */
async function holdVocabularyAsync(page: Page) {
  let release = () => {}
  const held = new Promise<void>(resolve => { release = resolve })
  await page.route(vocabularyRoute, async (route, request) => {
    if (request.method() !== 'GET') return route.fallback()
    await held
    await route.fallback()
  })
  return { release: () => release() }
}

test('the new change request editor cannot start a proposal before the vocabulary arrives, and what it shows is what it sends', async ({ page }) => {
  test.setTimeout(150_000)
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const workspace = await createWorkspaceAsync(page.request, suffix)
  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`

  const held = await holdVocabularyAsync(page)
  const drafts: { verificationMethod: string }[][] = []
  page.on('request', event => {
    if (event.url().endsWith('/api/change-request-drafts') && event.method() === 'POST')
      drafts.push((event.postDataJSON() as { requirementChanges: { verificationMethod: string }[] }).requirementChanges)
  })

  await page.goto(`${root}/systems/change-requests/new`)
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible({ timeout: 30_000 })

  // Blocked while the vocabulary is in flight. Retirement declares no method, so it stays available.
  const introduce = page.getByRole('button', { name: '+ Introduce System requirement' })
  const modify = page.getByRole('button', { name: 'Modify existing', exact: true })
  const retire = page.getByRole('button', { name: 'Retire existing', exact: true })
  await expect(introduce).toBeDisabled()
  await expect(modify).toBeDisabled()
  await expect(retire).toBeEnabled()
  await expect(blockedNotice(page)).toContainText('Loading this project')
  // The proposal cannot be created at all, so it cannot acquire a blank method.
  await expect(page.getByLabel('Verification method')).toHaveCount(0)

  held.release()
  await expect(introduce).toBeEnabled()
  await introduce.click()

  // Displayed option and payload value are the same authoritative first configured method.
  const method = page.getByLabel('Verification method')
  await expect(method).toHaveValue('Test')
  await expect(method.locator('option:checked')).toHaveText('Test')
  await expect(page.getByText('Choose a verification method')).toHaveCount(0)

  await page.getByLabel('Title').fill(`Vocabulary race ${suffix}`)
  await page.getByLabel('Requirement statement').fill('The FMS shall sequence oceanic waypoints.')
  await page.getByRole('button', { name: /Save SRCR Draft/ }).click()
  await expect.poll(() => drafts.length).toBe(1)
  expect(drafts[0].map(change => change.verificationMethod)).toEqual(['Test'])
})

test('a checked-out draft cannot start a proposal before the vocabulary arrives, and what it shows is what it sends', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const workspace = await createWorkspaceAsync(page.request, suffix)
  const projectId = workspace.project.id
  const sectionId = await systemSectionAsync(page.request, projectId)
  const existing = await draftAsync(page.request, projectId, workspace.release.id, sectionId,
    'Checked out draft', 'Test')
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${workspace.release.id}`

  // The checked-out workspace persists a working copy through controlled-editing autosave, not through the
  // change request itself, so the autosave body is the submitted payload for this surface.
  const autosaved: string[][] = []
  page.on('request', event => {
    if (!/\/api\/controlled-editing\/sessions\/.*\/autosave$/.test(event.url())) return
    const body = event.postDataJSON() as { draftJson: string }
    const copy = JSON.parse(body.draftJson) as { requirementChanges: { verificationMethod: string }[] }
    autosaved.push(copy.requirementChanges.map(change => change.verificationMethod))
  })

  const held = await holdVocabularyAsync(page)
  await page.goto(`${root}/systems/change-requests/${existing.id}`)
  await page.getByRole('button', { name: 'Check out & edit' }).click()

  const introduce = page.getByRole('button', { name: '+ Introduce System requirement' })
  const modify = page.getByRole('button', { name: 'Modify existing', exact: true })
  const retire = page.getByRole('button', { name: 'Retire existing', exact: true })
  await expect(introduce).toBeDisabled()
  await expect(modify).toBeDisabled()
  await expect(retire).toBeEnabled()
  await expect(blockedNotice(page)).toContainText('Loading this project')
  // The draft's own proposal is already there and unchanged; no second, blank one was created.
  await expect(page.getByLabel('Verification method')).toHaveCount(1)
  await expect(page.getByLabel('Verification method')).toHaveValue('Test')

  held.release()
  await expect(introduce).toBeEnabled()
  await introduce.click()
  const added = page.getByLabel('Verification method').nth(1)
  await expect(added).toHaveValue('Test')
  await expect(added.locator('option:checked')).toHaveText('Test')

  // The autosaved payload carries the same value the screen shows — never a blank the select filled in.
  await expect.poll(() => autosaved.at(-1) ?? [], { timeout: 30_000 }).toEqual(['Test', 'Test'])
  expect(autosaved.every(copy => copy.every(method => method === 'Test'))).toBe(true)
})

test('a vocabulary that fails to load blocks verification-bearing authoring on both surfaces instead of creating a blank proposal', async ({ page }) => {
  test.setTimeout(150_000)
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const workspace = await createWorkspaceAsync(page.request, suffix)
  const projectId = workspace.project.id
  const sectionId = await systemSectionAsync(page.request, projectId)
  const existing = await draftAsync(page.request, projectId, workspace.release.id, sectionId,
    'Failure state draft', 'Test')
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${workspace.release.id}`

  await page.route(vocabularyRoute, async (route, request) => {
    if (request.method() !== 'GET') return route.fallback()
    await route.fulfill({
      status: 500,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'The permitted verification methods could not be loaded.' }),
    })
  })

  await page.goto(`${root}/systems/change-requests/new`)
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByRole('button', { name: '+ Introduce System requirement' })).toBeDisabled()
  await expect(page.getByRole('alert').filter({ hasText: 'authoring is paused' })).toBeVisible()
  await expect(page.getByLabel('Verification method')).toHaveCount(0)

  await page.goto(`${root}/systems/change-requests/${existing.id}`)
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  await expect(page.getByRole('button', { name: '+ Introduce System requirement' })).toBeDisabled()
  await expect(page.getByRole('alert').filter({ hasText: 'authoring is paused' })).toBeVisible()
  // The existing proposal still shows exactly what it stores, and no blank one joined it.
  await expect(page.getByLabel('Verification method')).toHaveCount(1)
  await expect(page.getByLabel('Verification method')).toHaveValue('Test')
})
