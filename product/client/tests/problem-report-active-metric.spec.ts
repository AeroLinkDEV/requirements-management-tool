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

  const create = async (title: string) => {
    const response = await request.post(`${apiBase}/api/problem-reports`, { data: {
      projectId, releaseId, title, problem: `${title} requires an attributable lifecycle decision.`,
    } })
    expect(response.ok(), await response.text()).toBeTruthy()
    return await response.json()
  }
  await create(`Active engineering work ${suffix}`)
  const terminal = await create(`Accepted risk history ${suffix}`)
  const ready = await request.post(`${apiBase}/api/problem-reports/${terminal.id}/ready-for-sccb`, {
    data: { expectedVersion: terminal.version },
  })
  expect(ready.ok(), await ready.text()).toBeTruthy()
  const opened = await request.post(`${apiBase}/api/problem-reports/${terminal.id}/sccb/open`, {
    data: { expectedVersion: (await ready.json()).version },
  })
  expect(opened.ok(), await opened.text()).toBeTruthy()
  const dispositioned = await request.post(`${apiBase}/api/problem-reports/${terminal.id}/disposition`, {
    data: {
      expectedVersion: (await opened.json()).version,
      disposition: 'AcceptedRisk',
      rationale: 'The independent decision retains the record without active corrective work.',
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
  expect(openContract.definition).toContain('retained active legacy stages')

  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${projectId}/releases/${releaseId}`
  await page.goto(`${root}/enterprise-control`)
  await page.getByRole('button', { name: 'Assurance', exact: true }).click()
  const portfolioMetric = page.locator('.assuranceMetrics article').filter({ hasText: 'open problem reports' })
  await expect(portfolioMetric).toContainText('1', { timeout: 30_000 })

  await page.goto(`${root}/problem-reports`)
  const centerMetric = page.locator('.prMetrics article').filter({ hasText: 'Open work' })
  await expect(centerMetric).toContainText('1', { timeout: 30_000 })
  await expect(page.locator('.prList').getByText(`Accepted risk history ${suffix}`)).toBeVisible()
})
