import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, showcaseSeed } from './auth'

type Workspace = {
  program: { id: string }
  projects: {
    project: { id: string }
    releases: { id: string; version: string; isReleased: boolean }[]
  }[]
}

type TestSet = {
  discipline: 'System' | 'HighLevelSoftware' | 'LowLevelSoftware'
  procedures: {
    displayNumber: string
    latestExecutionId?: string | null
    hasEvidence: boolean
  }[]
}

/**
 * #423 — a released execution stays readable but cannot acquire another ordinary evidence relationship.
 *
 * The API regressions prove the headerless/server-authority path. This journey proves the user-facing side of
 * the same invariant: a released workspace exposes the existing run/evidence history and no attach/result/test-
 * set mutation control, even to an administrator.
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
  const set = sets.find(item => item.procedures.some(procedure =>
    procedure.latestExecutionId && procedure.hasEvidence))
  const procedure = set?.procedures.find(item => item.latestExecutionId && item.hasEvidence)
  expect(set && procedure, 'the released showcase keeps a run with linked evidence').toBeTruthy()

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
  await expect(history).toContainText(/evidence file/)
})
