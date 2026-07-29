import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup, openNewSystemChangeRequest, selectProgram } from './auth'

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
  await selectProgram(page, programName)
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await openNewSystemChangeRequest(page)
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

test('modifying a requirement shows the approved wording beside the proposed wording', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await openNewSystemChangeRequest(page)

  // Introduce has nothing to compare against, so the read-only field must not be there.
  await page.getByRole('button', { name: '+ Introduce System requirement' }).click()
  await expect(page.getByLabel('Change type')).toHaveValue('Introduce')
  await expect(page.getByRole('textbox', { name: 'Existing requirement wording' })).toHaveCount(0)
  await expect(page.getByRole('textbox', { name: 'Requirement statement' })).toBeVisible()

  // A proposal's kind used to be fixed once added, on the reasoning that the kind decides what the rest of the
  // form means. It does — which is an argument for re-deriving the identity when it changes, not for making the
  // author delete the card and start again. Switched in place here, on the proposal that already exists.
  await page.getByLabel('Change type').selectOption('Modify')
  const search = page.getByRole('textbox', { name: /Find controlled requirement/ }).last()
  await expect(search).toBeVisible({ timeout: 30_000 })
  await search.fill('SYSR-000001')
  const candidate = page.locator('.proposalLookupResults button').first()
  await expect(candidate).toBeVisible({ timeout: 30_000 })
  await candidate.click()

  // Both fields, and the reader can tell which is which: the approved text is not editable, the proposal is.
  const existing = page.getByRole('textbox', { name: 'Existing requirement wording' })
  const modified = page.getByRole('textbox', { name: 'Modified requirement wording' })
  await expect(existing).toBeVisible({ timeout: 30_000 })
  await expect(modified).toBeVisible()
  await expect(existing).not.toBeEditable()
  await expect(modified).toBeEditable()
  await expect(existing).not.toHaveValue(/Loading the approved wording/)

  // The proposal starts as a copy of the approved text, and editing it must not disturb the original — that
  // is the whole point of showing them together.
  const approved = await existing.inputValue()
  expect(approved.length).toBeGreaterThan(10)
  expect(await modified.inputValue()).toBe(approved)
  await modified.fill(`${approved} The sequencing shall additionally be configurable.`)
  expect(await existing.inputValue()).toBe(approved)

  // Criticality is no longer asked of the author, and Owner is called Author.
  const proposal = page.locator('.controlledEditor').first()
  await expect(page.getByLabel('Criticality')).toHaveCount(0)
  await expect(proposal.getByRole('textbox', { name: 'Owner' })).toHaveCount(0)
  await expect(proposal.getByRole('textbox', { name: 'Author' })).toBeVisible()
})

test('a specification section filters to the requirements it holds', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'SYSTEMS ENGINEERING')
  await page.getByRole('link', { name: 'System Requirements Explorer' }).click()
  await expect(page.getByRole('heading', { name: /System Requirements Explorer/ })).toBeVisible({ timeout: 30_000 })

  const rail = page.locator('.specRail')
  await rail.getByRole('button', { name: /SYSRD-/ }).click()
  await page.waitForTimeout(1200)

  // The reported total, not a digit-scrape of the whole summary block — that also swallowed the page numbers
  // beside it and produced a number belonging to nothing.
  const total = async () =>
    Number(((await page.locator('.resultSummary b').first().innerText()).match(/[\d,]+/)?.[0] ?? '0').replace(/,/g, ''))
  const beforeFilter = await total()
  expect(beforeFilter, 'the specification should list its requirements').toBeGreaterThan(0)

  // The heading reported a count and could not be pressed, so a reader could see that a section held forty
  // requirements and had no way to reach them.
  const sections = page.locator('.sectionTree button')
  await expect(sections.first()).toBeVisible({ timeout: 30_000 })
  const sectionCount = Number((await sections.first().innerText()).match(/(\d+)\s*$/)?.[1] ?? '0')
  expect(sectionCount, 'the section should report how many requirements it holds').toBeGreaterThan(0)

  await sections.first().click()
  await page.waitForTimeout(1500)
  await expect(sections.first()).toHaveAttribute('aria-pressed', 'true')

  const afterFilter = await total()
  expect(afterFilter, 'selecting a section must narrow the list').toBeLessThan(beforeFilter)
  expect(afterFilter, 'and it should show exactly the number the heading promised').toBe(sectionCount)

  // Pressing it again returns to the whole specification, so the control is a toggle rather than a trap.
  await sections.first().click()
  await page.waitForTimeout(1500)
  expect(await total()).toBe(beforeFilter)
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
