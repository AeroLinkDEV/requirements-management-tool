import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login, openNavigationGroup, selectProgram } from './auth'

/**
 * The source change request named on a requirement's revision must open that change request.
 *
 * It did not. The revision projection sends `sourceChangeRequestId`; the client read `sourceScrId`, a field
 * the server has never sent. Every read produced undefined, every link built
 * `/systems/change-requests/undefined`, and the view guard then fell through to Command Center — so a broken
 * link looked exactly like a working one that happened to land on a dashboard.
 *
 * Nothing above the browser could have caught it: the identifier is correct in the database, correct in the
 * payload, and the client's own interface type-checked cleanly while naming a field that does not exist.
 * This journey therefore asserts the thing that actually broke — that clicking the link reaches the record.
 *
 * An isolated workspace rather than the showcase, because it freezes a baseline and materializes
 * requirements, and doing that to shared demonstration data would move the ground under other journeys.
 */
const completeImpacts = JSON.stringify({ trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected', baseline: 'Not Affected', collaboration: 'Not Affected' })

test('the source change request named on a requirement revision opens that change request', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)

  const suffix = Date.now().toString().slice(-7)
  const programName = `Source Link Program ${suffix}`
  const workspace = await (await request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName, programCode: `SL${suffix}`, projectName: 'FMS Product Development',
      softwareProduct: 'Flight Management Software', initialRelease: '1.0', initialReleaseIsReleased: false,
    },
  })).json()

  const draft = await (await request.post(`${apiBase}/api/change-request-drafts`, {
    data: {
      projectId: workspace.project.id, targetReleaseId: workspace.release.id, type: 'System',
      title: 'Establish controlled airspace constraints',
      problem: 'The product baseline requires controlled FMS behaviour.',
      analysis: 'Operational and assurance needs were analysed and allocated.',
      solution: 'Introduce the approved requirement with verification criteria.',
      requirementChanges: [{
        level: 'System', kind: 'Introduce',
        targetSectionId: await firstSectionId(request, workspace.project.id),
        statement: 'The FMS shall provide controlled airspace constraints capability.',
        rationale: 'The constraint is derived from the operational need for this fixture.',
        verificationMethod: 'Test', impactDispositionJson: completeImpacts,
      }],
    },
  })).json()

  await request.post(`${apiBase}/api/change-requests/${draft.id}/submit`, { data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] } })
  await request.post(`${apiBase}/api/change-requests/${draft.id}/approve`, { data: { password: 'AeroLink!2026', meaning: 'Approved so the requirement carries a source change request.' } })

  const baseline = await (await request.post(`${apiBase}/api/baselines`, { data: { baseNumber: 'SYS-01.00', revision: 0, projectId: workspace.project.id, releaseId: workspace.release.id, name: 'Source link fixture manifest', actorId: 'cm' } })).json()
  await request.post(`${apiBase}/api/baselines/${baseline.id}/selections`, { data: { changeRequestId: draft.id, actorId: 'cm' } })
  await request.post(`${apiBase}/api/baselines/${baseline.id}/freeze`, { data: { actorId: 'cm' } })
  await request.post(`${apiBase}/api/baselines/${baseline.id}/materialize-requirements`, { data: { actorId: 'cm' } })

  // The contract the client depends on, asserted directly. This is the assertion that would have caught the
  // defect in milliseconds rather than requiring someone to click through the product and notice.
  const requirements = await (await request.get(`${apiBase}/api/enterprise-requirements/workspace?projectId=${workspace.project.id}&releaseId=${workspace.release.id}&page=1&pageSize=25`)).json()
  expect(requirements.items.length, 'the fixture materialized no requirement').toBeGreaterThan(0)
  const requirement = requirements.items[0]
  const detail = await (await request.get(`${apiBase}/api/enterprise-requirements/${requirement.id}?releaseId=${workspace.release.id}`)).json()
  const revision = detail.history[0]
  expect(revision.sourceChangeRequestId, 'the revision projection must name the source change request').toBe(draft.id)
  expect(revision.sourceChangeRequestReleaseId, 'the revision projection must name the build that owns it').toBe(workspace.release.id)

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, programName)
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()
  await expect(page.getByRole('heading', { name: /System Requirements Explorer/ })).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: new RegExp(requirement.baseNumber) }).first().click()
  const inspector = page.locator('.requirementInspector')
  await expect(inspector).toBeVisible({ timeout: 30_000 })
  await inspector.getByRole('button', { name: 'History' }).click()

  const sourceLink = inspector.getByRole('button', { name: revision.sourceScr })
  await expect(sourceLink).toBeVisible({ timeout: 30_000 })
  await sourceLink.click()

  // The literal assertion for the reported defect. `undefined` in a route is never a destination.
  await expect(page).not.toHaveURL(/change-requests\/undefined/)
  await expect(page).toHaveURL(new RegExp(`/systems/change-requests/${draft.id}$`))
  // And the record itself, so that reaching a plausible-looking wrong page cannot pass either.
  await expect(page.getByRole('heading', { name: 'Establish controlled airspace constraints' })).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.changeRequestWorkspace, .scrWorkspace, main')).toContainText(revision.sourceScr)
})
