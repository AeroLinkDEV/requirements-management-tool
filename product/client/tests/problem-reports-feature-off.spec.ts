import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

/**
 * #1196 (DEC-136): a feature that is off does not appear anywhere for that project. With Problem Reports off,
 * the editors that raise changes and test work show no Problem Report picker; the same editors show it where
 * Problem Reports is on.
 */
test('editors show no Problem Report picker in a project without Problem Reports', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const workspaceFor = async (label: string, enabled: string[]) => {
    const suffix = `${Date.now()}`.slice(-7)
    const created = await request.post(`${apiBase}/api/workspaces`, { data: {
      programName: `${label} ${suffix}`, programCode: `PF${suffix}`, projectName: `${label} Project`,
      softwareProduct: `${label} Product`, initialRelease: '1.0', initialReleaseIsReleased: false,
    } })
    expect(created.ok(), await created.text()).toBeTruthy()
    const workspace = await created.json() as { program: { id: string }; project: { id: string }; release: { id: string } }
    const features = await request.put(`${apiBase}/api/projects/${workspace.project.id}/features`, { data: {
      expectedVersion: 0, reason: 'Problem Report picker qualification', enabled,
    } })
    expect(features.ok(), await features.text()).toBeTruthy()
    return `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  }

  await login(page, 'admin', { openProject: false })
  const off = await workspaceFor('Reports off', ['TeamWork', 'Requirements', 'Verification', 'Release'])
  await page.goto(`${off}/systems/change-requests/new`)
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
  await expect(page.getByLabel('Title', { exact: true })).toBeVisible()
  await expect(page.locator('.problemReportPicker')).toHaveCount(0)
  await page.goto(`${off}/system-verification/change-requests/new`)
  await expect(page.locator('[data-tcr-editor]')).toBeVisible()
  await expect(page.locator('[data-tcr-editor]').getByLabel('Title', { exact: true })).toBeVisible()
  await expect(page.locator('.problemReportPicker')).toHaveCount(0)

  // The same editors still offer the picker where Problem Reports is on.
  const on = await workspaceFor('Reports on', ['TeamWork', 'Requirements', 'Verification', 'ProblemReports', 'Release'])
  await page.goto(`${on}/systems/change-requests/new`)
  await expect(page.locator('.problemReportPicker').first()).toBeVisible()
  await page.goto(`${on}/system-verification/change-requests/new`)
  await expect(page.locator('[data-tcr-editor] .problemReportPicker').first()).toBeVisible()
})
