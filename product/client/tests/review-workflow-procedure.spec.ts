import { expect, test } from '@playwright/test'
import { apiBase, login, openNavigationGroup } from './auth'

/**
 * A recorded review procedure is only worth having if a team can actually record one, see it, and revise it
 * without losing the version an earlier review was judged against.
 */
test('a team records its review procedure, puts it in force, and revises it without losing the prior version', async ({ page }) => {
  test.setTimeout(60_000)
  await login(page)

  // Its own workspace. Putting a procedure in force changes what a valid submission looks like for every
  // change request in that project, so doing it in the shared one would silently invalidate other journeys.
  const suffix = Date.now().toString().slice(-7)
  const programName = `Review Procedure ${suffix}`
  const created = await page.request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName, programCode: `RP${suffix}`, projectName: 'Procedure Project',
      softwareProduct: 'Procedure Product', initialRelease: '1.0', initialReleaseIsReleased: false,
    },
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  await page.reload()
  await page.locator('.program > select:not(.releaseSelector)').selectOption({ label: programName })

  await openNavigationGroup(page, 'ADMINISTRATION')
  await page.getByRole('link', { name: /Review Workflows/ }).click()
  await expect(page.getByRole('heading', { name: 'Review Workflows' })).toBeVisible()

  // With nothing recorded, authors keep free approver selection. Introducing workflows must not turn "we
  // have not written our procedure down yet" into "you cannot submit a change request".
  const systems = page.locator('.workflowCard').first()
  await expect(systems.getByText('No procedure is recorded')).toBeVisible()

  await systems.getByRole('button', { name: 'Record a procedure' }).click()
  const composer = page.locator('.workflowModal')
  await composer.getByLabel('Name', { exact: true }).fill('System change board')
  await composer.getByLabel('Stage 1 name').fill('Peer engineering')
  await composer.getByLabel('Stage 1 signed by').selectOption('Reviewer')
  await composer.getByRole('button', { name: 'Add a stage' }).click()
  await composer.getByLabel('Stage 3 name').fill('Change board')
  await composer.getByLabel('Stage 3 signed by').selectOption('Approver')
  await composer.getByRole('button', { name: 'Save as draft' }).click()

  // Nothing changes for authors until somebody puts it in force. Recording a procedure and adopting it are
  // separate decisions.
  await expect(page.getByText(/recorded as a draft/)).toBeVisible()
  await systems.getByRole('group', { name: /version/ }).or(systems.locator('.workflowHistory')).first().click()
  await systems.getByRole('button', { name: 'Put in force' }).click()

  await expect(systems.getByText('System change board v1')).toBeVisible()
  await expect(systems.getByText('Peer engineering')).toBeVisible()
  await expect(systems.getByText('Signed by a Approver')).toBeVisible()

  // Revising produces the next version. The one in force stays retained, because a recorded approval has to
  // remain explainable by the rules it was actually judged against.
  await systems.getByRole('button', { name: 'Revise procedure' }).click()
  await composer.getByLabel('Stage 3 name').fill('Configuration management')
  await composer.getByLabel('Stage 3 signed by').selectOption('ConfigurationManager')
  await composer.getByRole('button', { name: 'Save as draft' }).click()

  await systems.locator('.workflowHistory summary').click()
  await expect(systems.getByText(/System change board/).first()).toBeVisible()
  await expect(systems.locator('.workflowHistory li')).toHaveCount(1)
})
