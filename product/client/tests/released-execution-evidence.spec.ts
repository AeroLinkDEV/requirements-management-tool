import { expect, test, type Route } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, showcaseSeed } from './auth'

type Workspace = {
  program: { id: string }
  projects: {
    project: { id: string }
    releases: { id: string; version: string; isReleased: boolean }[]
  }[]
}

type TestProcedureRow = {
  displayNumber: string
  latestExecutionId?: string | null
  hasEvidence: boolean
}

type TestSet = {
  discipline: 'System' | 'HighLevelSoftware' | 'LowLevelSoftware'
  procedures: TestProcedureRow[]
}

/**
 * #423 — a released execution stays readable but cannot acquire another ordinary evidence relationship.
 *
 * The API regressions prove the real persistence/history path, including existing evidence linked before
 * release. The stable showcase does not itself seed an evidence file on its released execution, so this UI
 * journey augments the two read responses with one already-linked evidence row. That keeps the browser proof
 * focused on its responsibility: render immutable released history while exposing no mutation control.
 */
test('released test results keep evidence history readable and expose no evidence mutation', async ({ page, request }) => {
  test.setTimeout(180_000)
  const seed = await showcaseSeed(request)
  await apiLogin(request)

  const workspacesResponse = await request.get(`${apiBase}/api/workspaces`)
  expect(workspacesResponse.ok(), await workspacesResponse.text()).toBeTruthy()
  const workspaces = await workspacesResponse.json() as Workspace[]
  const workspace = workspaces.find(item => item.program.id === seed.programId)
  const project = workspace?.projects.find(item => item.project.id === seed.projectId)
  const released = project?.releases.find(item => item.isReleased)
  expect(released, 'the showcase has a released predecessor build').toBeTruthy()

  const setResponse = await request.get(`${apiBase}/api/releases/${released!.id}/test-sets`)
  expect(setResponse.ok(), await setResponse.text()).toBeTruthy()
  const sets = await setResponse.json() as TestSet[]
  const set = sets.find(item => item.procedures.some(procedure => procedure.latestExecutionId))
  const procedure = set?.procedures.find(item => item.latestExecutionId)
  expect(set && procedure, 'the released showcase keeps a readable execution').toBeTruthy()
  const executionId = procedure!.latestExecutionId!

  const augmentTestSets = async (route: Route) => {
    const response = await route.fetch()
    const body = await response.json() as TestSet[]
    for (const candidate of body) {
      const exact = candidate.procedures.find(item => item.displayNumber === procedure!.displayNumber)
      if (exact) exact.hasEvidence = true
    }
    await route.fulfill({ response, json: body })
  }
  const augmentExecutionHistory = async (route: Route) => {
    const response = await route.fetch()
    const body = await response.json() as ({ id: string; evidence: unknown[] }[])
    const exact = body.find(item => item.id === executionId)
    expect(exact, 'the released execution remains in history').toBeTruthy()
    exact!.evidence = [{
      id: '00000000-0000-0000-0000-000000423001',
      originalFileName: 'released-existing.txt',
      sha256: 'a'.repeat(64),
      size: 42,
      uploadedAt: '2026-08-09T12:00:00Z',
    }]
    await route.fulfill({ response, json: body })
  }
  await page.route(`**/api/releases/${released!.id}/test-sets`, augmentTestSets)
  await page.route('**/api/test-executions?**', augmentExecutionHistory)

  await login(page, 'admin', { openProject: false })
  await page.goto(
    `/programs/${seed.programId}/projects/${seed.projectId}/releases/${released!.id}/command-center`,
  )
  await expect(page.getByLabel(`Active build ${released!.version}`)).toContainText('Released')
  await openNavigationGroup(page, 'ASSURANCE')
  const linkName = set!.discipline === 'System'
    ? 'System Test Results'
    : set!.discipline === 'HighLevelSoftware'
      ? 'Software HLR Test Results'
      : 'Software LLR Test Results'
  await page.getByRole('link', { name: linkName }).click()
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })

  const row = page.locator('.testSetRow').filter({ hasText: procedure!.displayNumber }).first()
  await expect(row).toBeVisible({ timeout: 30_000 })
  await expect(row.getByRole('button', { name: /Record result|Record retest/ })).toHaveCount(0)
  await expect(row.getByLabel(new RegExp(`Attach evidence for ${procedure!.displayNumber.replace(/\./g, '\\.')}`)))
    .toHaveCount(0)
  await expect(row.getByRole('button', { name: 'Remove' })).toHaveCount(0)

  await row.getByRole('button', { name: 'Runs' }).click()
  const history = row.locator('.runList')
  await expect(history.locator('li').first()).toBeVisible()
  await expect(history).toContainText('released-existing.txt')
  await expect(history).toContainText('evidence file')
})
