import { expect, test } from '@playwright/test'
import { apiBase, login } from './auth'

// #711. The screen has to be honest about three things at once: what the project declared, what AeroLink
// recommends and on what basis, and that AeroLink has assessed no conformity to anything. A relaxation then
// has to be refused unless somebody with the right assurance authority approves it — which an Administrator
// is deliberately not, however much the rest of the product lets them do.
test('Assurance policy states its basis, refuses an unauthorised relaxation, and records an approved one', async ({ page }) => {
  await login(page, 'admin', { openProject: false })
  const suffix = Date.now().toString(36)
  const created = await page.request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Assurance UI ${suffix}`,
    programCode: `AU${suffix}`,
    projectName: `Assurance UI Project ${suffix}`,
    softwareProduct: 'Assurance UI Software',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(created.ok(), await created.text()).toBeTruthy()
  const workspace = await created.json() as { program: { id: string }; project: { id: string; name: string } }
  const slug = workspace.project.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')

  // Three approvers on this Program, so the authority rules can be exercised rather than described:
  // an Administrator who is nothing else, a Configuration Manager, and the Software Quality Analyst.
  const users = await page.request.get(`${apiBase}/api/admin/users`)
  expect(users.ok(), await users.text()).toBeTruthy()
  const accounts = await users.json() as { id: string; userName: string }[]
  for (const [userName, role] of [
    ['engineer.demo', 'Administrator'],
    ['cm.fms', 'ConfigurationManager'],
    ['systems.reviewer', 'SoftwareQualityAnalyst'],
  ]) {
    const account = accounts.find(user => user.userName === userName)
    expect(account, `the seeded ${userName} account must exist`).toBeTruthy()
    const grant = await page.request.post(`${apiBase}/api/admin/users/${account!.id}/memberships`, {
      data: { programId: workspace.program.id, role },
    })
    expect(grant.ok(), await grant.text()).toBeTruthy()
  }

  await page.goto(`/projects/${slug}/configuration/assurance`)
  await expect(page.getByRole('heading', { name: 'Assurance policy', level: 2 })).toBeVisible()

  // The notice #711 requires, and the claim boundary beside it.
  await expect(page.locator('.assuranceMappingNotice')).toContainText(
    'No certification-derived recommendation mapping has been approved for this installation.')
  await expect(page.locator('.assuranceMappingNotice')).toContainText('AeroLink project-policy defaults')
  await expect(page.getByText('AeroLink has not assessed conformity to any certification standard.')).toBeVisible()

  // Nothing on the page may claim conformity, compliance, or a passed certification.
  const panel = (await page.locator('.projectConfigurationPanel').innerText()).toLowerCase()
  for (const forbidden of ['compliant', 'compliance', 'certification passed', 'do-178']) expect(panel).not.toContain(forbidden)

  // Every lever states its basis, the kind of basis it is, and the seam that enforces it.
  const coverage = page.locator('.assuranceLever').filter({ hasText: 'Requirement coverage before release' })
  await expect(coverage).toContainText('AeroLink rule')
  await expect(coverage).toContainText('ReleaseReadinessService')
  await expect(coverage).toContainText('it is an AeroLink rule')

  // The declared level is metadata: recording it changes no recommendation.
  const recommendations = await page.locator('.assuranceLeverFacts').allInnerTexts()
  await page.getByLabel('Declared assurance level').selectOption('LevelB')
  await page.getByPlaceholder('Why is this policy changing?').fill('Declare the project posture for the pilot build')
  await page.getByRole('button', { name: 'Record policy' }).click()
  await expect(page.getByRole('status')).toContainText('Assurance policy version 1 recorded')
  await expect(page.getByLabel('Declared assurance level')).toHaveValue('LevelB')
  expect(await page.locator('.assuranceLeverFacts').allInnerTexts()).toEqual(recommendations)

  // Relaxing a lever demands a governed deviation, and an Administrator cannot approve one.
  await coverage.getByLabel('This project').selectOption('NotRequired')
  const deviation = coverage.locator('.assuranceDeviationForm')
  await expect(deviation).toBeVisible()
  await deviation.getByLabel('Rationale')
    .fill('The customer runs the coverage campaign for this build under its own procedure.')
  await deviation.getByLabel('Approving authority (user name)').fill('engineer.demo')
  await page.getByPlaceholder('Why is this policy changing?').fill('Relax coverage for the pilot build')
  await page.getByRole('button', { name: 'Record policy' }).click()
  await expect(page.getByRole('alert')).toContainText('carries no assurance authority')

  // Nor can a Configuration Manager, who may prepare and record the deviation but not authorise it.
  await deviation.getByLabel('Approving authority (user name)').fill('cm.fms')
  await page.getByRole('button', { name: 'Record policy' }).click()
  await expect(page.getByRole('alert')).toContainText('does not hold Software Quality Analyst authority')

  // The Software Quality Analyst can, and the record says who approved it and on what authority.
  await deviation.getByLabel('Approving authority (user name)').fill('systems.reviewer')
  await page.getByRole('button', { name: 'Record policy' }).click()
  await expect(page.getByRole('status')).toContainText('Assurance policy version 2 recorded')

  const deviations = page.locator('.assuranceDeviations')
  await expect(deviations).toContainText('Requirement coverage before release')
  await expect(deviations).toContainText('Verification')
  await expect(deviations).toContainText('systems.reviewer')
  await expect(deviations).toContainText('Software Quality Analyst')
  await expect(deviations).toContainText('In force')

  await expect(page.locator('.assuranceHistory')).toContainText('Relax coverage for the pilot build')
  await expect(page.locator('.assuranceAuthority')).toContainText('Prohibited')

  // The ladder is structural and untouched by any of this.
  await page.goto(`/projects/${slug}/configuration`)
  await expect(page.locator('.ladderRow')).toHaveCount(3)
})
