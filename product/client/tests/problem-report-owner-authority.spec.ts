import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, selectProgram, showcaseSeed, writeRichField } from './auth'

test('Problem Report ownership offers only accountable Program authority and the new owner can work immediately', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const stamp = Date.now()
  const title = `Accountable owner browser ${stamp}`
  const workaround = `Controlled handoff confirmed ${stamp}`
  const temporaryPassword = 'AeroLink!2026'
  const createProgramMember = async (userName: string, displayName: string, programId: string, role: string) => {
    const account = await request.post(`${apiBase}/api/admin/users`, { data: {
      userName,
      displayName,
      email: `${userName}@example.test`,
      temporaryPassword,
    } })
    expect(account.ok(), await account.text()).toBeTruthy()
    const accountId = (await account.json()).id as string
    const membership = await request.post(`${apiBase}/api/admin/users/${accountId}/memberships`, { data: {
      programId,
      role,
    } })
    expect(membership.ok(), await membership.text()).toBeTruthy()
    return accountId
  }
  const firstUseLogin = async (userName: string) => {
    const rotatedPassword = `Qualified-Owner!${stamp}`
    await page.goto('/')
    await page.getByLabel('Username').fill(userName)
    await page.getByLabel('Password').fill(temporaryPassword)
    await page.getByRole('button', { name: /Sign in securely/ }).click()
    await expect(page.getByRole('heading', { name: 'Replace temporary password' })).toBeVisible()
    await page.getByLabel('Temporary password').fill(temporaryPassword)
    await page.getByLabel('New password', { exact: true }).fill(rotatedPassword)
    await page.getByLabel('Confirm new password').fill(rotatedPassword)
    await page.getByRole('button', { name: /Change password securely/ }).click()
    await page.getByLabel('Username').fill(userName)
    await page.getByLabel('Password').fill(rotatedPassword)
    await page.getByRole('button', { name: /Sign in securely/ }).click()
    await expect(page.getByRole('heading', { name: 'Projects' })).toBeVisible()
  }

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
  await createProgramMember(outsiderName, outsiderDisplay, otherProgramId, 'Engineer')
  const eligibleName = `system.owner.${stamp}`
  const eligibleDisplay = `Accountable Systems Owner ${stamp}`
  const eligibleId = await createProgramMember(eligibleName, eligibleDisplay, showcase.programId, 'SystemEngineer')
  const recoveryName = `software.owner.${stamp}`
  const recoveryDisplay = `Recovery Software Owner ${stamp}`
  await createProgramMember(recoveryName, recoveryDisplay, showcase.programId, 'SoftwareEngineer')

  const created = await request.post(`${apiBase}/api/problem-reports`, { data: {
    category: 'CodeFunctional', projectId: showcase.projectId,
    releaseId: showcase.activeReleaseId,
    title,
    problem: 'The responsible engineer must be accountable inside the report Program.',
  } })
  expect(created.ok(), await created.text()).toBeTruthy()

  await login(page)
  await page.getByRole('link', { name: 'Problem Reports' }).click()
  await page.getByLabel('Search').fill(title)
  await page.locator('.prList').getByText(title).click()
  await page.locator('.prAdmin').getByText('Reassign or change target build').click()
  const picker = page.locator('.prAdmin').getByLabel('Assigned user')

  await picker.fill(outsiderDisplay)
  await expect(page.locator(`.personSuggestions button[data-user-name="${outsiderName}"]`)).toHaveCount(0)
  await picker.fill('Marcus Hale')
  await expect(page.locator('.personSuggestions button[data-user-name="quality.analyst"]')).toHaveCount(0)

  await picker.fill(eligibleDisplay)
  const eligibleOwner = page.locator(`.personSuggestions button[data-user-name="${eligibleName}"]`)
  await expect(eligibleOwner).toBeVisible({ timeout: 30_000 })
  await eligibleOwner.click()
  await page.getByRole('button', { name: 'Reassign', exact: true }).click()
  // The panel names the person now, not the account they sign in with — this owner is created by the test
  // and is in no client-side registry, which is exactly the case #776 was about. The handle stays
  // reachable in the title for anyone reconciling against the identity provider, so both are asserted.
  await expect(page.locator('.prIdentity').getByText(eligibleDisplay)).toBeVisible({ timeout: 30_000 })
  await expect(page.locator(`.prIdentity .personName[title="${eligibleName}"]`)).toBeVisible({ timeout: 30_000 })
  const reportUrl = page.url()

  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByLabel('Username')).toBeVisible({ timeout: 30_000 })
  await firstUseLogin(eligibleName)
  await selectProgram(page, 'Flight Management System Live Program')
  await expect(page).toHaveURL(new RegExp(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/command-center$`))
  await page.goto(reportUrl)
  await expect(page.getByRole('heading', { name: title })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  const editor = page.getByRole('dialog', { name: /^Edit PR-/ })
  await expect(editor).toBeVisible({ timeout: 30_000 })
  await writeRichField(editor, 'Workaround', workaround)
  await editor.getByRole('button', { name: 'Check in' }).click()
  await expect(page.getByText(workaround)).toBeVisible({ timeout: 30_000 })

  // Membership loss does not rewrite the assigned identity. It surfaces an actionable exception and only
  // explicit Program supervision receives the recovery control.
  const revoked = await request.delete(`${apiBase}/api/admin/users/${eligibleId}/memberships/${showcase.programId}/SystemEngineer`)
  expect(revoked.ok(), await revoked.text()).toBeTruthy()

  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByLabel('Username')).toBeVisible({ timeout: 30_000 })
  await login(page, 'engineering.manager')
  await page.goto(reportUrl)
  await expect(page.getByText('Owner no longer authorized')).toBeVisible({ timeout: 30_000 })
  await expect(page.locator('.prIdentity').getByText(eligibleDisplay)).toBeVisible()
  await page.locator('.prAdmin').getByText('Reassign or change target build').click()
  const recoveryPicker = page.locator('.prAdmin').getByLabel('Assigned user')
  await recoveryPicker.fill(recoveryDisplay)
  const recoveryOwner = page.locator(`.personSuggestions button[data-user-name="${recoveryName}"]`)
  await expect(recoveryOwner).toBeVisible({ timeout: 30_000 })
  await recoveryOwner.click()
  await page.getByRole('button', { name: 'Reassign', exact: true }).click()
  await expect(page.getByText('Owner no longer authorized')).toHaveCount(0)
  await expect(page.locator('.prIdentity').getByText(recoveryDisplay)).toBeVisible({ timeout: 30_000 })
  await expect(page.locator(`.prIdentity .personName[title="${recoveryName}"]`)).toBeVisible({ timeout: 30_000 })
})
