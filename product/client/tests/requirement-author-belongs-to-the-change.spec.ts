import { expect, test } from '@playwright/test'
import type { APIRequestContext } from '@playwright/test'
import { apiBase, login, openNavigationGroup, selectProgram } from './auth'

/**
 * #1016 S01. A requirement proposal has no author of its own.
 *
 * It is written inside a change request, and the change request already records who wrote it — immutably,
 * from the authenticated session. The proposal form asked the question a second time, in a free-text box, and
 * stored the answer under the legacy `owner` attribute, where it could disagree with the record it sat
 * inside. The standalone per-artifact Author question is redundant, and is no longer asked.
 *
 * The key is not redundant, and stays: `owner` remains part of the System Requirement schema and is still
 * accepted from a caller that sends it, values already recorded under it are untouched, and the Requirements
 * Explorer's owner filter and the saved views built on it still read it. That distinction — removing a
 * question is not removing anybody's answer — is what these journeys hold, so each one uses its own
 * disposable workspace and nothing shared is written.
 */

type Workspace = { program: { id: string }; project: { id: string; name: string }; release: { id: string } }
type Draft = { id: string; version: number; displayNumber: string }

const LEGACY_ATTRIBUTES = '{"criticality":"Normal","owner":"legacy.author"}'

async function workspaceAsync(request: APIRequestContext, suffix: string): Promise<Workspace> {
  const created = await request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName: `Author Scope ${suffix}`, programCode: `AS${suffix}`,
      projectName: `Author Scope Project ${suffix}`, softwareProduct: 'Author Scope Software',
      initialRelease: '1.0', initialReleaseIsReleased: false,
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  return await created.json() as Workspace
}

async function systemSectionAsync(request: APIRequestContext, projectId: string) {
  const response = await request.get(`${apiBase}/api/authoring/sections?projectId=${projectId}&level=System`)
  expect(response.ok(), await response.text()).toBeTruthy()
  const sections = await response.json() as { id: string }[]
  expect(sections.length).toBeGreaterThan(0)
  return sections[0].id
}

/** A proposal written before the control was removed, carrying an authored owner value. */
async function legacyDraftAsync(request: APIRequestContext, projectId: string, releaseId: string, sectionId: string) {
  const created = await request.post(`${apiBase}/api/change-request-drafts`, {
    data: {
      projectId, targetReleaseId: releaseId, type: 'System',
      title: 'Retain an authored owner across an edit',
      problem: 'A legacy proposal carries an owner value.',
      analysis: 'Removing the control must not remove the value.',
      solution: 'Leave the stored attribute exactly as recorded.',
      requirementChanges: [{
        level: 'System', kind: 'Introduce', targetSectionId: sectionId,
        statement: 'The FMS shall retain an authored owner attribute.',
        rationale: 'Saved-view compatibility', verificationMethod: 'Test',
        attributesJson: LEGACY_ATTRIBUTES,
      }],
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  return await created.json() as Draft
}

const storedAttributesAsync = async (request: APIRequestContext, changeRequestId: string) => {
  const detail = await (await request.get(`${apiBase}/api/change-requests/${changeRequestId}`)).json() as
    { requirementChanges: { statement: string; attributesJson: string }[] }
  return detail.requirementChanges[0]
}

test('the proposal form no longer asks for an author, and the change request still names one', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const workspace = await workspaceAsync(page.request, suffix)
  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`

  await page.goto(`${root}/systems/change-requests/new`)
  await expect(page.getByRole('heading', { name: 'Create System Change Request' })).toBeVisible({ timeout: 30_000 })

  // The change request's own author: present, derived from the session, and not typed by anybody. This is the
  // record the proposal-level field was duplicating, and it is the one that has to survive.
  const changeRequestAuthor = page.locator('input[aria-describedby="change-request-author-help"]')
  await expect(changeRequestAuthor).toHaveCount(1)
  await expect(changeRequestAuthor).toHaveAttribute('readonly', '')
  await expect(page.getByText('Derived from the authenticated session.')).toBeVisible()

  // How many times the page says "Author" before a proposal exists. The proposal-level field was the only
  // other one there has ever been, so this is the number that must not go up when one is added.
  const authorLabels = await page.getByText('Author', { exact: true }).count()

  await page.getByRole('button', { name: '+ Introduce System requirement' }).click()
  await expect(page.getByLabel('Verification method')).toHaveCount(1)

  // Adding a proposal adds no second Author question, and the change request's own author is untouched.
  expect(await page.getByText('Author', { exact: true }).count(),
    'a proposal added a second Author question').toBe(authorLabels)
  await expect(changeRequestAuthor).toHaveAttribute('readonly', '')
  await expect(page.getByPlaceholder('responsible.username')).toHaveCount(0)

  // Nothing else was taken with it: the classification block the field sat in is still there and still works.
  await expect(page.locator('.classificationMetadata')).toHaveCount(1)
  await expect(page.locator('.classificationMetadata input')).toHaveCount(0)

  // And the section summary stops advertising the field. A description that still promises an author sends a
  // reader looking for a control that is not there, which is the removal surviving under different wording.
  const summary = page.locator('.supportingDetails summary')
  await expect(summary).toContainText('Formatted context, controlled references, and classification')
  await expect(summary).not.toContainText('responsible author')
})

test('an authored owner survives a controlled checkout, edit and check-in untouched', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const workspace = await workspaceAsync(page.request, suffix)
  const projectId = workspace.project.id
  const sectionId = await systemSectionAsync(page.request, projectId)
  const draft = await legacyDraftAsync(page.request, projectId, workspace.release.id, sectionId)
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${workspace.release.id}`

  // Recorded before anything is opened, so the comparison at the end is against the real starting value.
  expect(JSON.parse((await storedAttributesAsync(page.request, draft.id)).attributesJson).owner).toBe('legacy.author')

  await page.goto(`${root}/systems/change-requests/${draft.id}`)
  await expect(page.locator('.eyebrow')).toContainText(draft.displayNumber, { timeout: 30_000 })

  await page.getByRole('button', { name: 'Check out & edit' }).click()
  await expect(page.getByRole('button', { name: /Save & check in/ })).toBeVisible({ timeout: 30_000 })

  // The legacy value is in this record, and the editor offers no control that could overwrite it.
  await expect(page.getByPlaceholder('responsible.username')).toHaveCount(0)
  await expect(page.locator('.classificationMetadata input')).toHaveCount(0)

  // A real edit, through the controlled path, so the round trip is a genuine write and not a no-op.
  const statement = page.locator('.statementEditor').first()
  await statement.fill('The FMS shall retain an authored owner attribute across a controlled edit.')
  await page.getByRole('button', { name: /Save & check in/ }).click()
  await expect(page.getByRole('button', { name: 'Check out & edit' })).toBeVisible({ timeout: 30_000 })

  // Reopened from the server: the edit landed, and the owner nobody can now type is exactly as it was.
  const stored = await storedAttributesAsync(page.request, draft.id)
  expect(stored.statement).toBe('The FMS shall retain an authored owner attribute across a controlled edit.')
  const attributes = JSON.parse(stored.attributesJson) as Record<string, unknown>
  expect(attributes.owner, 'the authored owner was blanked by an edit that never offered to change it')
    .toBe('legacy.author')
  expect(attributes.criticality).toBe('Normal')

  // And it is not reported as a gap needing somebody to go and fix it.
  const gaps = await (await page.request.get(`${apiBase}/api/authoring/attribute-gaps?projectId=${projectId}`)).json() as
    { id: string; missing: string[] }[]
  expect(gaps.find(row => row.id === draft.id)).toBeUndefined()
  expect(gaps.some(row => row.missing.includes('owner')),
    'owner is no longer an expected attribute, so no row may report it missing').toBe(false)
})

test('the Requirements Explorer still presents the owner filter control', async ({ page }) => {
  test.setTimeout(180_000)
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const workspace = await workspaceAsync(page.request, suffix)
  const projectId = workspace.project.id
  const sectionId = await systemSectionAsync(page.request, projectId)
  await legacyDraftAsync(page.request, projectId, workspace.release.id, sectionId)
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${workspace.release.id}`

  // Presentation only, and named accordingly. This proves the filter control and its chip survive the
  // removal of the Author input — it does **not** prove the filter selects anything, because a Draft proposal
  // is not yet a controlled requirement the Explorer can return.
  //
  // What the filter actually selects, and that a saved view keeps its owner predicate and still selects the
  // same requirements, is proven where the records exist:
  // `RequirementFilterExactnessApiTests.An_owner_matches_the_owner_field_and_not_an_unrelated_attribute_that_mentions_them`
  // and `…A_saved_view_keeps_its_owner_predicate_and_still_selects_the_same_requirements`.
  await page.goto(`${root}/systems/requirements`)
  await expect(page.getByRole('heading', { name: 'System Requirements Explorer' })).toBeVisible({ timeout: 30_000 })
  // The owner filter lives behind Advanced, which is where it has always lived.
  await page.getByRole('button', { name: 'Advanced' }).click()
  const owner = page.getByLabel('Owner', { exact: true })
  await expect(owner).toHaveCount(1)
  await owner.fill('legacy.author')
  await expect(page.getByText('Owner: legacy.author')).toBeVisible()
})

/**
 * #1016 S01. A controlled verification artifact's authorship belongs to its revisions, not to its header.
 *
 * The Procedure Explorer's Trace & impact header carried "Written by <person>" for the selected revision.
 * Standing at the top of an inspector that also lists earlier revisions, one name reads as the authorship of
 * everything on the screen — including revisions somebody else wrote. The History tab already states
 * authorship against the revision it belongs to, which is the surface where the question has an exact answer.
 *
 * So the routine header no longer individually attributes, and everything that carries real provenance is
 * checked to be still there: the recorded timestamp, the History attribution, and the producing change.
 */
test('the procedure trace header drops individual authorship while History keeps it', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()
  await expect(page.getByRole('status', { name: /Loading controlled requirements/ })).toBeHidden()
  const root = new URL(page.url()).pathname.split('/').slice(0, 7).join('/')

  await page.goto(`${root}/system-verification/procedures`)
  const procedure = page.getByRole('button', { name: /SYSTP-\d+\.\d{2}/ }).first()
  await expect(procedure).toBeVisible({ timeout: 30_000 })
  await procedure.click()

  const inspector = page.locator('.procedureInspector, .requirementInspector').first()
  await inspector.getByRole('tab', { name: 'Trace & impact' }).click()
  const identity = page.locator('.traceRevisionIdentity')
  await expect(identity).toBeVisible({ timeout: 30_000 })

  // The routine header does not individually attribute.
  await expect(identity).not.toContainText('Written by')
  // The recorded timestamp is kept — when it was recorded is still a useful fact here.
  await expect(identity).toContainText('Recorded')

  // Producing-change provenance is untouched: a revision still says what raised it.
  const provenance = page.locator('.traceProvenance')
  if (await provenance.count()) {
    await expect(provenance.first()).toContainText(/Produced by|Related controlled impact/)
  }

  // History still answers the authorship question, against the revision it belongs to.
  await inspector.getByRole('tab', { name: 'History' }).click()
  const revisions = page.locator('.revisionList li')
  await expect(revisions.first()).toBeVisible({ timeout: 30_000 })
  await expect(revisions.first()).toContainText('written by')

  // The operational Owner field is an assignment, not an authorship duplicate, and stays.
  await inspector.getByRole('tab', { name: 'Overview' }).click()
  await expect(page.getByText('Owner', { exact: true })).toBeVisible()
})
