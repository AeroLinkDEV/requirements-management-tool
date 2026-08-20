import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

test('quality assurance and the Problem Report center display the same active-work count', async ({ page, request }) => {
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `PR active metric ${suffix}`,
    programCode: `PRA${suffix}`,
    projectName: 'Metric contract project',
    softwareProduct: 'Metric contract product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const projectId = workspace.project.id as string
  const releaseId = workspace.release.id as string
  const usersResponse = await request.get(`${apiBase}/api/admin/users`)
  expect(usersResponse.ok(), await usersResponse.text()).toBeTruthy()
  const systemsLead = (await usersResponse.json() as { id: string; userName: string }[])
    .find(person => person.userName === 'systems.lead')!
  const membership = await request.post(`${apiBase}/api/admin/users/${systemsLead.id}/memberships`, { data: {
    programId: workspace.program.id,
    role: 'ProjectEngineer',
  } })
  expect(membership.ok(), await membership.text()).toBeTruthy()

  const create = async (title: string) => {
    const response = await request.post(`${apiBase}/api/problem-reports`, { data: {
      projectId, releaseId, title, problem: `${title} requires an attributable lifecycle decision.`,
    } })
    expect(response.ok(), await response.text()).toBeTruthy()
    return await response.json()
  }
  await create(`Active engineering work ${suffix}`)
  const terminal = await create(`Rejected history ${suffix}`)
  const ready = await request.post(`${apiBase}/api/problem-reports/${terminal.id}/ready-for-sccb`, {
    data: { expectedVersion: terminal.version },
  })
  expect(ready.ok(), await ready.text()).toBeTruthy()
  await apiLogin(request, 'systems.lead')
  const opened = await request.post(`${apiBase}/api/problem-reports/${terminal.id}/transition`, {
    data: { expectedVersion: (await ready.json()).version, targetState: 'Open' },
  })
  expect(opened.ok(), await opened.text()).toBeTruthy()
  await apiLogin(request)
  const dispositioned = await request.post(`${apiBase}/api/problem-reports/${terminal.id}/transition`, {
    data: {
      expectedVersion: (await opened.json()).version,
      targetState: 'Rejected',
      rationale: 'The independent decision rejects the record without active corrective work.',
    },
  })
  expect(dispositioned.ok(), await dispositioned.text()).toBeTruthy()

  const dashboard = await (await request.get(
    `${apiBase}/api/problem-reports/dashboard?projectId=${projectId}`)).json()
  const contracts = await (await request.get(
    `${apiBase}/api/quality/metric-contracts?projectId=${projectId}`)).json()
  const openContract = contracts.contracts.find((item: { key: string }) => item.key === 'open_problem_reports')
  expect(dashboard.summary.active).toBe(1)
  expect(openContract.value).toBe(1)
  expect(openContract.definition).toContain('Draft, Ready for SCCB, Open, Implementing, Verifying')

  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${releaseId}`
  await page.goto(`${root}/enterprise-control`)
  await page.getByRole('button', { name: 'Assurance', exact: true }).click()
  const portfolioMetric = page.locator('.assuranceMetrics article').filter({ hasText: 'open problem reports' })
  await expect(portfolioMetric).toContainText('1', { timeout: 30_000 })

  await page.goto(`${root}/problem-reports`)
  const centerMetric = page.locator('.prMetrics article').filter({ hasText: 'Open work' })
  await expect(centerMetric).toContainText('1', { timeout: 30_000 })
  await expect(page.locator('.prList').getByText(`Rejected history ${suffix}`)).toBeVisible()
})
