import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram } from './auth'

/**
 * What the author typed is what the product holds.
 *
 * These come from someone using the product for an evening rather than from a test plan, and both are the
 * kind of defect a feature test walks straight past: the change case *could* be filled in, a proposal *could*
 * be added, every existing journey passed. The product was still unusable for writing a sentence.
 */

async function createWorkspace(request: APIRequestContext, prefix: string) {
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const programName = `${prefix} ${suffix}`
  const response = await request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName,
      programCode: `IF${suffix}`,
      projectName: 'Input Fidelity',
      softwareProduct: 'Input Fidelity Software',
      initialRelease: '1.0',
      initialReleaseIsReleased: false,
    },
  })
  expect(response.ok(), await response.text()).toBeTruthy()
  return programName
}

async function openNewSystemScr(page: Page, programName: string) {
  await login(page)
  await page.locator('.program > select:not(.releaseSelector)').selectOption({ label: programName })
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'New System SCR' }).click()
}

test('the change case keeps every space the author types', async ({ page, request }) => {
  const programName = await createWorkspace(request, 'Input Fidelity')
  await openNewSystemScr(page, programName)

  // Typed with the keyboard, not filled, because the defect was in the round trip on each keystroke: the
  // field's value was normalised and written back between one character and the next. `fill` sets the value
  // in one go and would have passed against the broken build.
  const problem = page.getByRole('textbox', { name: 'Problem' })
  await problem.click()
  await page.keyboard.type('Oceanic waypoint sequencing is wrong')

  await expect(problem, 'every space between words must survive being typed').toHaveValue(
    'Oceanic waypoint sequencing is wrong',
  )

  // A space in the middle is the common case; a trailing one is what actually broke, because trimming the
  // value removed the separator before the next word could arrive.
  await page.keyboard.type(' ')
  await expect(problem).toHaveValue('Oceanic waypoint sequencing is wrong ')
  await page.keyboard.type('today')
  await expect(problem).toHaveValue('Oceanic waypoint sequencing is wrong today')

  // The other two fields share the component, so they share the defect.
  const analysis = page.getByRole('textbox', { name: 'Analysis' })
  await analysis.click()
  await page.keyboard.type('Two options were considered')
  await expect(analysis).toHaveValue('Two options were considered')
})

test('the System explorer never lists software requirements, whatever else is filtered', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()

  await expect(page.getByRole('heading', { name: /System Requirements Explorer/ })).toBeVisible({ timeout: 30_000 })
  await page.waitForTimeout(1500)

  const identifiers = async () =>
    (await page.locator('.reqTable, .reqList, main').first().innerText())
      .split('\n')
      .map(line => line.trim())
      .filter(line => /^(SYSR|HLR|LLR)-\d{6}/.test(line))

  const onArrival = await identifiers()
  expect(onArrival.length, 'the explorer should have listed some requirements').toBeGreaterThan(0)
  expect(
    onArrival.filter(id => !id.startsWith('SYSR')),
    'the System explorer listed software requirements on arrival',
  ).toEqual([])

  // The rail must not offer documents this explorer cannot show a single requirement from.
  const rail = page.locator('.specRail')
  await expect(rail).toBeVisible()
  const railText = await rail.innerText()
  expect(railText, 'the System explorer offered the software specifications').not.toMatch(/HLRD-|LLRD-/)
  expect(railText).toMatch(/SYSRD-/)

  // Selecting a specification is the path that broke it: it cleared the level filter, and an empty level
  // means no level constraint at all rather than "the one this explorer is for". The scope is not a filter
  // somebody chose, so nothing may clear it.
  await rail.getByRole('button', { name: /SYSRD-/ }).click()
  await page.waitForTimeout(1500)
  const afterSpecification = await identifiers()
  expect(afterSpecification.length, 'selecting the system specification should still list requirements').toBeGreaterThan(0)
  expect(
    afterSpecification.filter(id => !id.startsWith('SYSR')),
    'selecting a specification dropped the System scope',
  ).toEqual([])
})
