import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed } from './auth'

const completedImpacts = JSON.stringify({
  trace: 'Not Affected',
  verification: 'Affected',
  documents: 'Not Affected',
  baseline: 'Affected',
  collaboration: 'Not Affected',
})

test('modified requirement coverage stays suspect until an exact approved procedure is reconfirmed', async ({ page, request }) => {
  test.setTimeout(120_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)

  const releasedCoverageResponse = await request.get(
    `${apiBase}/api/verification-coverage?projectId=${showcase.projectId}&baselineId=${showcase.releasedBaselineId}`,
  )
  expect(releasedCoverageResponse.ok(), await releasedCoverageResponse.text()).toBeTruthy()
  const releasedCoverage = await releasedCoverageResponse.json()
  const original = releasedCoverage.items.find((item: any) =>
    item.displayNumber.startsWith('SYSR-')
    && item.coveredBy.some((procedure: any) => procedure.state === 'Approved' && !procedure.isSuspect),
  )
  expect(original).toBeTruthy()
  const approvedProcedure = original.coveredBy.find(
    (procedure: any) => procedure.state === 'Approved' && !procedure.isSuspect,
  )
  const separator = original.displayNumber.lastIndexOf('.')
  const baseNumber = original.displayNumber.slice(0, separator)
  const revision = Number(original.displayNumber.slice(separator + 1)) + 1

  const draftResponse = await request.post(`${apiBase}/api/scr-drafts`, { data: {
    projectId: showcase.projectId,
    targetReleaseId: showcase.activeReleaseId,
    type: 'System',
    title: `Reconfirm exact coverage ${Date.now()}`,
    problem: 'The controlled requirement wording needs a precise clarification.',
    analysis: 'Existing procedure applicability must be re-evaluated against the new exact revision.',
    solution: 'Modify the requirement and record an attributable verification decision.',
    requirementChanges: [{
      baseNumber,
      revision,
      level: 'System',
      kind: 'Modify',
      statement: `${original.statement} The response shall remain deterministic.`,
      rationale: 'Clarify the deterministic response without changing the controlled verification method.',
      verificationMethod: 'Test',
      impactDispositionJson: completedImpacts,
    }],
  } })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const draft = await draftResponse.json()
  const submitted = await request.post(`${apiBase}/api/scrs/${draft.id}/submit`, {
    data: { approvers: [{ userId: 'admin', name: 'AeroLink Administrator' }] },
  })
  expect(submitted.ok(), await submitted.text()).toBeTruthy()
  const approved = await request.post(`${apiBase}/api/scrs/${draft.id}/approve`, {
    data: { password: 'AeroLink!2026', meaning: 'Approved for suspect-coverage journey verification.' },
  })
  expect(approved.ok(), await approved.text()).toBeTruthy()

  const suffix = Date.now().toString().slice(-8)
  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: `SWBL-${suffix}`,
    revision: 0,
    projectId: showcase.projectId,
    releaseId: showcase.activeReleaseId,
    predecessorBaselineId: showcase.releasedBaselineId,
    name: `Suspect coverage ${suffix}`,
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const [path, data] of [
    ['selections', { scrId: draft.id }],
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy()
  }

  const materializedCoverageResponse = await request.get(
    `${apiBase}/api/verification-coverage?projectId=${showcase.projectId}&baselineId=${baseline.id}`,
  )
  expect(materializedCoverageResponse.ok(), await materializedCoverageResponse.text()).toBeTruthy()
  const materializedCoverage = await materializedCoverageResponse.json()
  const changed = materializedCoverage.items.find((item: any) => item.displayNumber === `${baseNumber}.${String(revision).padStart(2, '0')}`)
  expect(changed.covered).toBe(false)
  expect(changed.coveredBy).toEqual(expect.arrayContaining([
    expect.objectContaining({
      procedureId: approvedProcedure.procedureId,
      revisionId: approvedProcedure.revisionId,
      state: 'Approved',
      isSuspect: true,
      coverageState: 'Suspect',
    }),
  ]))

  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'VERIFICATION')
  await page.getByRole('link', { name: 'System Verification' }).click()
  await page.getByLabel('Materialized baseline').selectOption(baseline.id)
  await page.getByRole('button', { name: /Requirement coverage/ }).click()
  const coverageRow = page.locator('.coverageRow').filter({ hasText: `${baseNumber}.${String(revision).padStart(2, '0')}` })
  await expect(coverageRow.getByText('Suspect', { exact: true })).toBeVisible()
  const selectedProcedureLink = coverageRow.locator('small').filter({ hasText: approvedProcedure.displayNumber })
  await expect(selectedProcedureLink).toContainText('Suspect applicability')

  await coverageRow.getByRole('button', { name: /Resolve in Change impact/ }).click()
  const impactRow = page.locator('.impactRow').filter({ hasText: `${baseNumber}.${String(revision).padStart(2, '0')}` })
  await expect(impactRow).toBeVisible()
  await impactRow.getByRole('button', { name: /Record decision/ }).click()
  const decision = page.getByRole('dialog', { name: 'Record verification decision' })
  await decision.getByLabel('Decision').selectOption('ProcedureCoverageConfirmed')
  await decision.getByLabel('Covering procedure').selectOption(approvedProcedure.procedureId)
  await decision.getByLabel('Rationale').fill('The exact approved procedure still exercises the clarified deterministic response.')
  await decision.getByRole('button', { name: 'Record decision' }).click()

  await expect(impactRow.getByText('Coverage confirmed')).toBeVisible()
  await page.getByRole('button', { name: /Requirement coverage/ }).click()
  await expect(coverageRow.getByText('Verified', { exact: true })).toBeVisible()
  await expect(selectedProcedureLink).not.toContainText('Suspect applicability')

  const finalCoverageResponse = await request.get(
    `${apiBase}/api/verification-coverage?projectId=${showcase.projectId}&baselineId=${baseline.id}`,
  )
  const finalCoverage = await finalCoverageResponse.json()
  const finalChanged = finalCoverage.items.find((item: any) => item.revisionId === changed.revisionId)
  expect(finalChanged.covered).toBe(true)
  expect(finalChanged.coveredBy).toEqual(expect.arrayContaining([
    expect.objectContaining({
      procedureId: approvedProcedure.procedureId,
      revisionId: approvedProcedure.revisionId,
      state: 'Approved',
      isSuspect: false,
      coverageState: 'Confirmed',
    }),
  ]))
})
