import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

test('Problem Report ownership offers only accountable Program authority and the new owner can work immediately', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const title = `Accountable owner browser ${stamp}`
  const workaround = `Controlled handoff confirmed ${stamp}`

  // A real active engineer in another Program exists, but must never cross the FMS picker boundary.
  const workspace = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Other ownership Program ${stamp}`,
    programCode: `OW${String(stamp).slice(-8)}`,
    projectName: 'Other ownership Project',
    softwareProduct: 'Other product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspace.ok(), await workspace.text()).toBeTruthy()
  const otherProgramId = (await workspace.json()).program.id as string
  const outsiderName = `outside.owner.${stamp}`
  const outsiderDisplay = `Cross Program Owner ${stamp}`
  const account = await request.post(`${apiBase}/api/admin/users`, { data: {
    userName: outsiderName,
    displayName: outsiderDisplay,
    email: `${outsiderName}@example.test`,
    temporaryPassword: 'Temporary-Owner!2026',
  } })
  expect(account.ok(), await account.text()).toBeTruthy()
  const outsiderId = (await account.json()).id as string
  const membership = await request.post(`${apiBase}/api/admin/users/${outsiderId}/memberships`, { data: {
    programId: otherProgramId,
    role: 'Engineer',
  } })
  expect(membership.ok(), await membership.text()).toBeTruthy()

  const created = await request.post(`${apiBase}/api/problem-reports`, { data: {
    projectId: showcase.projectId,
    releaseId: showcase.activeReleaseId,
    title,
    problem: 'The responsible engineer must be accountable inside the report Program.',
  } })
  expect(created.ok(), await created.text()).toBeTruthy()

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await page.locator('.prList').getByText(title).click()
  await page.locator('.prAdmin').getByText('Reassign or change target build').click()
  const picker = page.locator('.prAdmin').getByLabel('Assigned user')

  await picker.fill(outsiderDisplay)
  await expect(page.locator(`.personSuggestions button[data-user-name="${outsiderName}"]`)).toHaveCount(0)
  await picker.fill('Marcus Hale')
  await expect(page.locator('.personSuggestions button[data-user-name="quality.analyst"]')).toHaveCount(0)

  await picker.fill('Systems Engineering Lead')
  const systemsLead = page.locator('.personSuggestions button[data-user-name="systems.lead"]')
  await expect(systemsLead).toBeVisible({ timeout: 30_000 })
  await systemsLead.click()
  await page.getByRole('button', { name: 'Reassign', exact: true }).click()
  await expect(page.locator('.prIdentity').getByText('Maya Patel')).toBeVisible({ timeout: 30_000 })
  const reportUrl = page.url()

  await page.getByRole('button', { name: 'Sign out' }).click()
  await login(page, 'systems.lead')
  await page.goto(reportUrl)
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await expect(editor).toBeVisible({ timeout: 30_000 })
  await editor.getByLabel('Workaround').fill(workaround)
  await editor.getByRole('button', { name: 'Check in' }).click()
  await expect(page.getByText(workaround)).toBeVisible({ timeout: 30_000 })

  // Membership loss does not rewrite the assigned identity. It surfaces an actionable exception and only
  // explicit Program supervision receives the recovery control.
  const users = await (await request.get(`${apiBase}/api/admin/users`)).json() as { id: string; userName: string }[]
  const systemsLeadId = users.find(user => user.userName === 'systems.lead')?.id
  expect(systemsLeadId).toBeTruthy()
  const revoked = await request.delete(`${apiBase}/api/admin/users/${systemsLeadId}/memberships/${showcase.programId}/SystemEngineeringLead`)
  expect(revoked.ok(), await revoked.text()).toBeTruthy()

  await page.getByRole('button', { name: 'Sign out' }).click()
  await login(page, 'program.manager')
  await page.goto(reportUrl)
  await expect(page.getByText('Owner no longer authorized')).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.prIdentity').getByText('Maya Patel')).toBeVisible()
  await page.locator('.prAdmin').getByText('Reassign or change target build').click()
  const recoveryPicker = page.locator('.prAdmin').getByLabel('Assigned user')
  await recoveryPicker.fill('Rina Shah')
  const softwareLead = page.locator('.personSuggestions button[data-user-name="software.lead"]')
  await expect(softwareLead).toBeVisible({ timeout: 30_000 })
  await softwareLead.click()
  await page.getByRole('button', { name: 'Reassign', exact: true }).click()
  await expect(page.getByText('Owner no longer authorized')).toHaveCount(0)
  await expect(page.locator('.prIdentity').getByText('Rina Shah')).toBeVisible({ timeout: 30_000 })
})
