import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

/**
 * The #816 Personnel page: Project Leadership first, roster as membership, identity details per person.
 *
 * #848 seeds Project Leadership assignments into the FMS showcase, so the read-only contract can inspect
 * the complete seeded surface. Mutation journeys create their own disposable workspace and accounts so
 * leadership writes cannot change the state consumed by another test.
 */

type DisposableWorkspace = {
  program: { id: string }
  project: { id: string }
  release: { id: string }
}

const LEADERSHIP_POSITIONS = [
  'ProjectEngineer', 'ProgramManager', 'EngineeringManager', 'ConfigurationManager',
  'SystemEngineeringLead', 'SoftwareEngineeringLead', 'SystemTestLead', 'SoftwareTestLead',
]

async function createDisposableWorkspace(request: import('@playwright/test').APIRequestContext, suffix: string): Promise<DisposableWorkspace> {
  const created = await request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName: `Personnel isolation ${suffix}`,
      programCode: `PI${suffix}`,
      projectName: `Personnel isolation project ${suffix}`,
      softwareProduct: 'Personnel isolation software',
      initialRelease: '1.0',
      initialReleaseIsReleased: false,
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  return await created.json() as DisposableWorkspace
}

async function openPersonnel(page: import('@playwright/test').Page, workspace: DisposableWorkspace) {
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/command-center`)
  await page.getByRole('button', { name: '← Back to Software Builds' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()
}

/**
 * Activates an API-created account through AeroLink's mandatory first-use password rotation:
 * sign in with the temporary credential, rotate, return to sign-in, sign in again with the
 * new password. The product intentionally requires this lifecycle; the fixture exercises it
 * rather than bypassing it.
 */
async function activateTemporaryAccount(page: import('@playwright/test').Page, userName: string, temporaryPassword: string, permanentPassword: string) {
  // Start signed out so the login page renders.
  await page.context().clearCookies()
  await page.goto('/')
  await expect(page.getByRole('button', { name: /Sign in securely/ })).toBeVisible({ timeout: 15_000 })
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill(temporaryPassword)
  await page.getByRole('button', { name: 'Sign in securely' }).click()

  await expect(page.getByRole('heading', { name: 'Replace temporary password' })).toBeVisible({ timeout: 15_000 })
  await page.getByLabel('Temporary password').fill(temporaryPassword)
  await page.getByLabel('New password', { exact: true }).fill(permanentPassword)
  await page.getByLabel('Confirm new password').fill(permanentPassword)
  await page.getByRole('button', { name: 'Change password securely' }).click()

  // The product returns to sign-in after rotation; sign in again with the new credential.
  await expect(page.getByRole('button', { name: /Sign in securely/ })).toBeVisible({ timeout: 15_000 })
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill(permanentPassword)
  await page.getByRole('button', { name: 'Sign in securely' }).click()
  await expect(page.getByRole('heading', { name: /Projects|Software Builds/ })).toBeVisible({ timeout: 15_000 })
}

test('Project Leadership renders eight positions in stable order on the showcase', async ({ page, request }) => {
  test.setTimeout(120_000)
  await showcaseSeed(request)
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  const positions = page.locator('[data-position]')
  await expect(positions).toHaveCount(8)
  const named = await positions.evaluateAll(items => items.map(item => item.getAttribute('data-position')))
  expect(named).toEqual(LEADERSHIP_POSITIONS)
  // Every card truthfully reports its state: either a primary holder with elevated authority, or
  // Nobody assigned. The exact number of filled/vacant positions depends on the #848 seeded state.
  const cards = page.locator('.positionCard')
  for (let i = 0; i < 8; i++) {
    await expect(cards.nth(i)).toBeVisible()
  }
  await expect(page.locator('[data-assurance="SoftwareQualityAnalyst"]')).toBeVisible()
  await expect(page.locator('[data-assurance="Airworthiness"]')).toBeVisible()
})

test('a leader can be assigned from an eligible member and replaced with another eligible member', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const tag = Date.now().toString(36)
  const workspace = await createDisposableWorkspace(request, `pe-${tag}`)

  // Create two PE base-role members via the API.
  const names: string[] = []
  for (const name of ['PE First', 'PE Second']) {
    const created = await request.post(`${apiBase}/api/admin/users`, {
      data: { userName: `s3.assign.${name.replace(/\s/g, '.').toLowerCase()}.${tag}`, displayName: name, email: `s3.assign.${name.replace(/\s/g, '.').toLowerCase()}.${tag}@example.test`, temporaryPassword: 'AeroLink!2026' },
    })
    expect(created.ok(), await created.text()).toBeTruthy()
    const userId = (await created.json()).id as string
    const granted = await request.post(`${apiBase}/api/projects/${workspace.project.id}/personnel`, {
      data: { userId, roles: ['ProjectEngineer'] },
    })
    expect(granted.ok(), await granted.text()).toBeTruthy()
    names.push(name)
  }

  await openPersonnel(page, workspace)

  const engineerCard = page.locator('[data-position="ProjectEngineer"]')

  // This disposable workspace starts vacant, so the first eligible assignment exercises "Assign leader";
  // retain the replacement branch so the journey still follows the UI's current-state contract.
  const hasPrimary = await engineerCard.getByRole('button', { name: 'Replace leader' }).count()
  await engineerCard.getByRole('button', { name: hasPrimary ? 'Replace leader' : 'Assign leader' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.locator('.directoryResult', { hasText: names[0] }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText(names[0])
  await expect(engineerCard).toContainText('Elevated authority')

  // Now replace with the second PE holder.
  await engineerCard.getByRole('button', { name: 'Replace leader' }).click()
  await page.locator('.directoryResult', { hasText: names[1] }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(engineerCard).toContainText(names[1])
  await expect(engineerCard).toContainText('Elevated authority')
  await expect(engineerCard).not.toContainText(names[0])
})

test('a standing backup is named and removed, and the roster records it separately from the primary', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const tag = Date.now().toString(36)
  const workspace = await createDisposableWorkspace(request, `sel-${tag}`)

  const names: string[] = []
  for (const name of ['SEL Primary', 'SEL Deputy']) {
    const created = await request.post(`${apiBase}/api/admin/users`, {
      data: { userName: `s3.bk.${name.replace(/\s/g, '.').toLowerCase()}.${tag}`, displayName: name, email: `s3.bk.${name.replace(/\s/g, '.').toLowerCase()}.${tag}@example.test`, temporaryPassword: 'AeroLink!2026' },
    })
    expect(created.ok(), await created.text()).toBeTruthy()
    const userId = (await created.json()).id as string
    const granted = await request.post(`${apiBase}/api/projects/${workspace.project.id}/personnel`, {
      data: { userId, roles: ['SystemEngineer'] },
    })
    expect(granted.ok(), await granted.text()).toBeTruthy()
    names.push(name)
  }

  await openPersonnel(page, workspace)

  const leadCard = page.locator('[data-position="SystemEngineeringLead"]')

  // This disposable workspace starts with no leadership assignment, so the journey exercises assignment
  // and then the separate backup lifecycle without relying on a shared seeded project.
  const hasPrimary = await leadCard.getByRole('button', { name: 'Replace leader' }).count()
  await leadCard.getByRole('button', { name: hasPrimary ? 'Replace leader' : 'Assign leader' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  // Scope selections to the leadership-picker dialog's semantic listbox — not the roster.
  const eligiblePeople = page.getByRole('dialog').getByRole('listbox', { name: 'Eligible people' })
  await eligiblePeople.getByRole('option', { name: 'SEL Primary' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(leadCard).toContainText('SEL Primary')

  await leadCard.getByRole('button', { name: /Assign backup|Change backup/ }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('dialog').getByRole('listbox', { name: 'Eligible people' }).getByRole('option', { name: 'SEL Deputy' }).click()
  await page.getByRole('button', { name: 'Confirm' }).click()
  await expect(leadCard.getByText('Backup SEL Deputy')).toBeVisible()

  // The roster records the backup separately from the primary — never conflated.
  await expect(page.locator('[data-member]').filter({ hasText: 'SEL Deputy' }).getByText('Backup · System Engineering Lead')).toBeVisible()

  await leadCard.getByRole('button', { name: 'Remove backup' }).click()
  await expect(leadCard.getByText('No backup assigned')).toBeVisible()
  await expect(page.locator('[data-member]').filter({ hasText: 'SEL Deputy' }).getByText('Backup · System Engineering Lead')).toHaveCount(0)
})

test('a person is added from the directory with several base roles in one confirm', async ({ page, request }) => {
  test.setTimeout(120_000)
  await showcaseSeed(request)
  const tag = Date.now().toString(36)
  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  await page.getByRole('button', { name: '+ Add Person to Project' }).click()
  await page.getByLabel('Search the directory').fill(`avery${tag}`)
  await page.getByRole('button', { name: 'Create local person/account' }).click()
  await page.getByLabel('Display name').fill('Avery Qualification')
  await page.getByLabel('Username').fill(`avery.${tag}`)
  await page.getByLabel('Email').fill(`avery.${tag}@example.test`)
  await page.getByLabel('Temporary password').fill('AeroLink!2026')
  await page.getByRole('button', { name: 'Create account' }).click()

  await page.getByRole('checkbox', { name: 'System Engineer' }).check()
  await page.getByRole('checkbox', { name: 'Airworthiness' }).check()
  await page.getByRole('button', { name: 'Add to project' }).click()

  const row = page.locator('[data-member]').filter({ hasText: 'Avery Qualification' })
  await expect(row).toBeVisible()
  await expect(row).toContainText('System Engineer')
  await expect(row).toContainText('Airworthiness')
})

test('an administrator edits the current identity of a disposable account, and the change is scoped to now', async ({ page, request }) => {
  test.setTimeout(120_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const tag = Date.now().toString(36)
  const userName = `s3.identity.edit.${tag}`
  const displayName = `Identity Edit ${tag}`

  const created = await request.post(`${apiBase}/api/admin/users`, {
    data: { userName, displayName, email: `${userName}@example.test`, temporaryPassword: 'AeroLink!2026' },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const userId = (await created.json()).id as string
  await request.post(`${apiBase}/api/projects/${showcase.projectId}/personnel`, {
    data: { userId, roles: ['SystemEngineer'] },
  })

  await page.setViewportSize({ width: 1440, height: 900 })
  await login(page, 'admin', { openProject: false })
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  const row = page.locator('[data-member]').filter({ hasText: displayName })
  await expect(row).toBeVisible()
  await row.getByRole('button', { name: displayName, exact: true }).click()
  await expect(page.getByRole('heading', { name: displayName, level: 2 })).toBeVisible()

  await page.getByLabel('Email').fill(`${userName}.edited@example.test`)
  await page.getByLabel('Display name').fill(`Identity Edited ${tag}`)
  await page.getByRole('button', { name: 'Save identity' }).click()
  await expect(page.locator('[data-member]').filter({ hasText: `Identity Edited ${tag}` })).toBeVisible()
  await expect(page.locator('[data-member]').filter({ hasText: `${userName}.edited@example.test` })).toBeVisible()
  await expect(page.getByLabel('Username')).toHaveCount(0)
})

test('a non-admin roster manager cannot create accounts or edit global identity', async ({ page, request }) => {
  test.setTimeout(120_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request)
  const tag = Date.now().toString(36)
  const userName = `s3.roster.mgr.${tag}`
  const displayName = `Roster Manager ${tag}`

  const created = await request.post(`${apiBase}/api/admin/users`, {
    data: { userName, displayName, email: `${userName}@example.test`, temporaryPassword: 'AeroLink!2026' },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const userId = (await created.json()).id as string
  await request.post(`${apiBase}/api/projects/${showcase.projectId}/personnel`, {
    data: { userId, roles: ['ProgramManager'] },
  })

  // Elevate into the PM leadership position — base eligibility alone does not grant roster authority (#848).
  const assigned = await request.post(`${apiBase}/api/projects/${showcase.projectId}/leadership/ProgramManager/primary`, {
    data: { holderUserId: userId },
  })
  expect(assigned.ok(), await assigned.text()).toBeTruthy()

  // Verify the leadership assignment took effect before opening the browser.
  const leadership = await (await request.get(`${apiBase}/api/projects/${showcase.projectId}/leadership`)).json()
  const pmPosition = leadership.positions.find((p: { position: string }) => p.position === 'ProgramManager')
  expect(pmPosition?.primary?.person?.userName, 'PM leadership primary should be the test user').toBe(userName)

  const permanentPassword = `RosterMgr!${tag}x`

  await page.setViewportSize({ width: 1440, height: 900 })
  await page.context().clearCookies()
  await page.goto('/')

  // Activate the disposable PM leadership primary through the product's mandatory
  // first-use password rotation, then sign in with the rotated credential.
  await activateTemporaryAccount(page, userName, 'AeroLink!2026', permanentPassword)

  // The PM leadership primary has roster-management authority: they see the Add Person action.
  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds', level: 1 })).toBeVisible()
  await page.getByRole('button', { name: 'Personnel' }).click()
  await expect(page.getByRole('heading', { name: 'Project Leadership', level: 2 })).toBeVisible()

  await page.getByRole('button', { name: '+ Add Person to Project' }).click()
  await page.getByLabel('Search the directory').fill('nobody-matches-this')
  await expect(page.getByText('An AeroLink administrator must create the account before the person can be added.')).toBeVisible()
  // A non-global-admin cannot create accounts from this surface.
  await expect(page.getByRole('button', { name: 'Create local person/account' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Cancel' }).click()

  // The PM leadership primary also cannot edit another person's global identity.
  const selfRow = page.locator('[data-member]').filter({ hasText: displayName })
  await selfRow.getByRole('button', { name: displayName, exact: true }).click()
  await expect(page.getByText('Current identity (global administrator)')).toHaveCount(0)
})
