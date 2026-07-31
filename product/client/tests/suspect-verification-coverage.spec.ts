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

  // The in-work build's own baseline, rather than a throwaway second one. A release now carries exactly one
  // software build, because the build number is derived from the release version — two would collide on the
  // same name. The end state is what it always was: a materialized baseline on the in-work release.
  const baselinesResponse = await request.get(
    `${apiBase}/api/baselines?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`,
  )
  expect(baselinesResponse.ok(), await baselinesResponse.text()).toBeTruthy()
  const baseline = (await baselinesResponse.json())[0]
  expect(baseline, 'the in-work software build').toBeTruthy()
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
  const impactResponse = await request.get(
    `${apiBase}/api/releases/${showcase.activeReleaseId}/verification-impact`,
  )
  expect(impactResponse.ok(), await impactResponse.text()).toBeTruthy()
  const impactItem = (await impactResponse.json()).find(
    (item: any) => item.subjectDisplayNumber === changed.displayNumber,
  )
  expect(impactItem).toBeTruthy()
  const assignmentResponse = await request.post(
    `${apiBase}/api/verification-impact/${impactItem.id}/assign`,
    { data: { engineerId: 'admin' } },
  )
  expect(assignmentResponse.ok(), await assignmentResponse.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'ASSURANCE')
  await page.getByRole('link', { name: 'System Testing Coverage' }).click()
  await expect(page.getByRole('heading', { name: 'Testing Coverage' })).toBeVisible({ timeout: 30_000 })

  // The whole inventory, because this requirement is covered — it is the applicability of that coverage that
  // is in question, and the page opens on what has no coverage at all.
  const subject = `${baseNumber}.${String(revision).padStart(2, '0')}`
  const showAll = page.getByRole('button', { name: /Show all \d+ requirements/ })
  await expect(showAll).toBeVisible({ timeout: 30_000 })
  await showAll.click()
  const coverageRow = page.locator('.fullCoverage .coverageRow').filter({ hasText: subject })
  await expect(coverageRow.getByText('Suspect', { exact: true })).toBeVisible({ timeout: 30_000 })

  // The decision itself is made inside the package that raised it, which is where the work is queued.
  const decisionRow = page.locator('.decisionList li').filter({ hasText: subject })
  const openPackage = async () => {
    if (await decisionRow.count() > 0 && await decisionRow.first().isVisible()) return
    for (const button of await page.locator('.coverageRow').getByRole('button', { name: /decision/i }).all()) {
      await button.click()
      if (await decisionRow.count() > 0 && await decisionRow.first().isVisible()) return
    }
  }
  await openPackage()
  await expect(decisionRow.first()).toBeVisible({ timeout: 30_000 })

  const decide = async (rationale: string) => {
    await decisionRow.first().getByRole('button', { name: 'Decide' }).click()
    const dialog = page.getByRole('dialog', { name: `Decide ${subject}` })
    await dialog.getByLabel('Decision').selectOption('ProcedureCoverageConfirmed')
    await dialog.getByLabel('Covering procedure').selectOption(approvedProcedure.procedureId)
    await dialog.getByLabel('Rationale').fill(rationale)
    await dialog.getByRole('button', { name: 'Record decision' }).click()
    await expect(dialog).toHaveCount(0, { timeout: 30_000 })
  }

  await decide('The exact approved procedure still exercises the clarified deterministic response.')
  await expect(decisionRow.first()).toContainText('Coverage confirmed')

  // A decision can be reconsidered. What was decided stays in history, and the requirement returns to suspect.
  await decisionRow.first().getByRole('button', { name: /Reopen \/ change decision/ }).click()
  const reopen = page.getByRole('dialog', { name: 'Reopen verification decision' })
  await reopen.getByLabel('Reopen rationale').fill(
    'A second review must preserve the first decision while restoring the release gate.',
  )
  await reopen.getByRole('button', { name: 'Reopen decision' }).click()
  await expect(reopen).toHaveCount(0, { timeout: 30_000 })
  await expect(decisionRow.first().getByRole('button', { name: 'Decide' })).toBeVisible({ timeout: 30_000 })
  await expect(coverageRow.getByText('Suspect', { exact: true })).toBeVisible({ timeout: 30_000 })

  await decide('The repeat review confirms the same exact controlled procedure revision.')
  await expect(decisionRow.first()).toContainText('Coverage confirmed')
  await decisionRow.first().getByText(/Decision history · 3/).click()
  await expect(decisionRow.first().getByText('Decision reopened')).toBeVisible()

  await expect(coverageRow.getByText('Verified', { exact: true })).toBeVisible({ timeout: 30_000 })
  await expect(coverageRow.locator('small').filter({ hasText: approvedProcedure.displayNumber })).not.toContainText('Suspect')

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
  const finalImpactResponse = await request.get(
    `${apiBase}/api/releases/${showcase.activeReleaseId}/verification-impact`,
  )
  const finalImpact = (await finalImpactResponse.json()).find((item: any) => item.id === impactItem.id)
  expect(finalImpact.blocksBaselineApproval).toBe(false)
  expect(finalImpact.resolvedProcedure.revisionId).toBe(approvedProcedure.revisionId)
  expect(finalImpact.decisionHistory.map((entry: any) => entry.action)).toEqual([
    'Resolved',
    'Reopened',
    'Resolved',
  ])
})
