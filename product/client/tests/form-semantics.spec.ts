import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, layoutSettled, login, selectProgram, surfacePainted } from './auth'

/**
 * Every control on a controlled-authoring form is programmatically what it looks like.
 *
 * The reported defect was that controls "lack associated for/id, aria-label, or aria-labelledby". That did not
 * reproduce — these forms label by wrapping, which is a valid association — so the value here is not the
 * original claim but the absence of any machine-checkable rule at all. A visual review proves one build; the
 * accessible name of a field is a property of the markup and nothing was measuring it.
 *
 * What this asserts, per form, over the controls a person can actually reach:
 *
 *  - exactly one accessible name, non-empty and concise;
 *  - clicking the visible label moves focus to the control it labels;
 *  - `required` and invalid states exist programmatically, not only as colour;
 *  - a group of related choices carries group semantics rather than being loose radios;
 *  - keyboard focus order follows the order the controls appear in;
 *  - no axe violation in the categories that describe form semantics.
 *
 * Retired workflows are deliberately absent. Problem Reports, Baselines and Product Versions route to Not
 * Found on purpose, and a test that reached for them would be asserting the product still has surfaces it
 * intentionally removed.
 */

/** The accessible-name rules. A name that is missing, duplicated or a paragraph is each a different defect. */
const NAME_MINIMUM = 1
const NAME_MAXIMUM = 120

type ControlReport = {
  unnamed: string[]
  verbose: string[]
  requiredWithoutSemantics: string[]
  looseChoices: string[]
  focusOrder: string[]
  controls: number
}

/**
 * Reads the semantics of every reachable control.
 *
 * Runs in the page because an accessible name is computed from rendered markup — attribute-by-attribute
 * checking from the test side would re-implement the algorithm and get it wrong in exactly the cases that
 * matter, such as a label that wraps its input.
 */
const auditForm = ({ minimum, maximum }: { minimum: number; maximum: number }): ControlReport => {
  const visible = (element: Element) => {
    if (element.getAttribute('aria-hidden') === 'true' || element.getAttribute('tabindex') === '-1') return false
    const box = element.getBoundingClientRect()
    if (box.width === 0 || box.height === 0) return false
    const style = getComputedStyle(element)
    return style.visibility !== 'hidden' && style.opacity !== '0'
  }

  /**
   * The accessible name, following the parts of the computation these forms actually use: an explicit
   * `aria-label`, an `aria-labelledby` reference, an explicit `for`, or a wrapping label.
   */
  const accessibleName = (element: Element): string => {
    const aria = element.getAttribute('aria-label')
    if (aria?.trim()) return aria.trim()
    const referenced = element.getAttribute('aria-labelledby')
    if (referenced) {
      const text = referenced
        .split(/\s+/)
        .map(id => document.getElementById(id)?.textContent?.trim() ?? '')
        .filter(Boolean)
        .join(' ')
      if (text) return text
    }
    const id = element.getAttribute('id')
    if (id) {
      const explicit = document.querySelector(`label[for="${CSS.escape(id)}"]`)
      if (explicit?.textContent?.trim()) return explicit.textContent.trim()
    }
    const wrapping = element.closest('label')
    if (wrapping) {
      // The label's own text, with the values of any controls inside it removed, is the field's name.
      const clone = wrapping.cloneNode(true) as HTMLElement
      for (const nested of clone.querySelectorAll('input, select, textarea, button')) nested.remove()
      const text = clone.textContent?.trim() ?? ''
      if (text) return text
    }
    // A button names itself by its content.
    if (element.tagName === 'BUTTON') return (element.textContent ?? '').trim()
    return ''
  }

  const describe = (element: Element) => {
    const tag = element.tagName.toLowerCase()
    const type = element.getAttribute('type')
    const name = element.getAttribute('name') ?? element.getAttribute('id') ?? ''
    return `${tag}${type ? `[type=${type}]` : ''}${name ? ` name="${name}"` : ''}`
  }

  const controls = [...document.querySelectorAll('input:not([type=hidden]), select, textarea')]
    .filter(visible) as HTMLElement[]

  const unnamed: string[] = []
  const verbose: string[] = []
  const requiredWithoutSemantics: string[] = []

  for (const control of controls) {
    const name = accessibleName(control)
    if (name.length < minimum) unnamed.push(describe(control))
    else if (name.length > maximum) verbose.push(`${describe(control)} — ${name.length} characters`)

    // A field the form will refuse to submit without must say so in the markup, not only in a colour or an
    // asterisk. `required` or `aria-required` both count; neither present is the defect.
    const marked = control.hasAttribute('required') || control.getAttribute('aria-required') === 'true'
    const looksRequired = (control.closest('label')?.textContent ?? '').includes('*')
    if (looksRequired && !marked) requiredWithoutSemantics.push(describe(control))

    const hint = control.closest('label')?.querySelector(':scope > small, :scope > .hint, :scope > p')
    if (hint && (!hint.id || !(control.getAttribute('aria-describedby') ?? '').split(/\s+/).includes(hint.id))) {
      verbose.push(`${describe(control)} includes help text in its name instead of an accessible description`)
    }
  }

  // Radios sharing a name are one question, and a question needs a group. Checkbox sets are left alone: a
  // single independent checkbox is common and legitimate here.
  const looseChoices: string[] = []
  const radioGroups = new Map<string, HTMLElement[]>()
  for (const radio of controls.filter(x => x.getAttribute('type') === 'radio')) {
    const group = radio.getAttribute('name') ?? ''
    if (!group) continue
    radioGroups.set(group, [...(radioGroups.get(group) ?? []), radio])
  }
  for (const [group, radios] of radioGroups) {
    if (radios.length < 2) continue
    const grouped = radios[0].closest('fieldset, [role=radiogroup], [role=group]')
    if (!grouped) looseChoices.push(`radio group "${group}" of ${radios.length} has no fieldset or role`)
    else if (grouped.tagName === 'FIELDSET' && !grouped.querySelector('legend')) {
      looseChoices.push(`fieldset for "${group}" has no legend`)
    }
  }

  // Focus order: the tabbable controls in document order must not be reordered by a positive tabindex, which
  // is the usual way a visible order and a keyboard order come apart.
  const focusOrder = [...document.querySelectorAll('[tabindex]')]
    .filter(element => Number(element.getAttribute('tabindex')) > 0)
    .map(element => `${describe(element)} has tabindex ${element.getAttribute('tabindex')}`)

  return { unnamed, verbose, requiredWithoutSemantics, looseChoices, focusOrder, controls: controls.length }
}

/** Every rule this file enforces, reported together so one run names every offending form. */
async function inspect(page: Page, where: string, failures: string[]) {
  await surfacePainted(page)
  // The page shell and headings paint before the authoring form's asynchronous data has loaded. Waiting for
  // the first reachable field makes the guard below observe the form itself instead of racing that second
  // render on slower CI workers.
  await page.locator('main input:not([type=hidden]), main select, main textarea').first()
    .waitFor({ state: 'visible', timeout: 30_000 })
  await layoutSettled(page)
  const report = await page.evaluate(auditForm, { minimum: NAME_MINIMUM, maximum: NAME_MAXIMUM })

  // A form with no controls means the surface did not open, and an audit of nothing passes everything.
  expect(report.controls, `${where}: expected controls to audit`).toBeGreaterThan(0)

  for (const control of report.unnamed) failures.push(`${where}: ${control} has no accessible name`)
  for (const control of report.verbose) failures.push(`${where}: ${control} name is not concise`)
  for (const control of report.requiredWithoutSemantics) {
    failures.push(`${where}: ${control} is marked required visually but not programmatically`)
  }
  for (const problem of report.looseChoices) failures.push(`${where}: ${problem}`)
  for (const problem of report.focusOrder) failures.push(`${where}: ${problem}`)

  // axe covers the parts of the computation this file does not re-implement, restricted to the rules that
  // describe form semantics — a full axe pass would also report colour, which the contrast spec measures
  // directly and in both densities.
  const axe = await new AxeBuilder({ page })
    .withRules([
      'label',
      'form-field-multiple-labels',
      'select-name',
      'aria-input-field-name',
      'aria-toggle-field-name',
      'aria-required-attr',
      'aria-valid-attr-value',
      'duplicate-id-active',
    ])
    .analyze()
  for (const violation of axe.violations) {
    failures.push(`${where}: axe ${violation.id} — ${violation.nodes.length} node(s): ${violation.help}`)
  }
}

/** Clicking the visible label must put focus in the control it names. */
async function labelActivatesControl(page: Page, labelText: string, where: string, failures: string[]) {
  const label = page.locator('label').filter({ hasText: labelText }).first()
  if (await label.count() === 0) return
  const control = label.locator('input, select, textarea').first()
  if (await control.count() === 0) return
  await label.click({ position: { x: 4, y: 4 } })
  const focused = await page.evaluate(() => {
    const active = document.activeElement
    return active ? `${active.tagName.toLowerCase()}:${active.getAttribute('name') ?? ''}` : ''
  })
  const expected = await control.evaluate(node => `${node.tagName.toLowerCase()}:${node.getAttribute('name') ?? ''}`)
  if (focused !== expected) {
    failures.push(`${where}: clicking the "${labelText}" label focused ${focused || 'nothing'}, not ${expected}`)
  }
}

test('every controlled authoring form is programmatically what it looks like', async ({ page, request }) => {
  test.setTimeout(300_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const failures: string[] = []

  // System, HLR and LLR authoring, which the issue requires to be covered equivalently rather than sampled.
  for (const [where, path] of [
    ['System change request', '/systems/change-requests/new'],
    ['Software change request (HLR)', '/software/change-requests/new?level=HLR'],
    ['Software change request (LLR)', '/software/change-requests/new?level=LLR'],
  ] as const) {
    await page.goto(new URL(root + path, page.url()).toString(), { waitUntil: 'load' })
    await inspect(page, where, failures)
    for (const label of ['Title', 'Problem', 'Analysis', 'Solution']) {
      await labelActivatesControl(page, label, where, failures)
    }
    await page.getByRole('button', { name: /^\+ Introduce/ }).click()
    await inspect(page, `${where} with a dynamic requirement proposal`, failures)
  }

  expect(failures, `Form semantics defects:\n  ${failures.join('\n  ')}`).toEqual([])
})

test('verification, review and administration forms carry the same semantics', async ({ page, request }) => {
  test.setTimeout(300_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  const failures: string[] = []

  // Administration is a form-heavy surface that authoring tests never reach.
  await page.goto(new URL(root + '/administration', page.url()).toString(), { waitUntil: 'load' })
  await inspect(page, 'People & Authority', failures)

  // Review procedures: the form that defines who has to sign a change, and the only place in the product with a
  // real radio group. It opens from the list rather than from a route, so it is opened the way an
  // administrator opens it — auditing the list would have audited a surface with no controls on it, which the
  // guard in `inspect` refuses precisely so this cannot pass by looking at nothing.
  await page.goto(new URL(root + '/review-workflows', page.url()).toString(), { waitUntil: 'load' })
  await surfacePainted(page)
  await page.getByRole('button', { name: /Record a procedure|Revise procedure/ }).first().click()
  await expect(page.locator('.workflowModal')).toBeVisible({ timeout: 30_000 })
  await inspect(page, 'Review procedure', failures)
  await labelActivatesControl(page, 'Name', 'Review procedure', failures)

  // Verification's own forms open from the page rather than from a route, so they are opened the way a
  // verification engineer opens them. A dialog is where required fields and choice groups actually live.
  await page.goto(new URL(root + '/system-verification/results', page.url()).toString(), { waitUntil: 'load' })
  await surfacePainted(page)
  await layoutSettled(page)
  const record = page.getByRole('button', { name: /^Record (result|retest)$/ }).first()
  if (await record.count() > 0) {
    await record.click()
    await expect(page.locator('.recordResultModal')).toBeVisible({ timeout: 30_000 })
    await inspect(page, 'Record verification result', failures)
    for (const label of ['Outcome', 'Configuration under test', 'Evidence reference']) {
      await labelActivatesControl(page, label, 'Record verification result', failures)
    }
  }

  // The coverage page offers no form of its own: a procedure is introduced by a test change request, so
  // nothing on this page writes one.
  await page.goto(new URL(root + '/system-verification/coverage', page.url()).toString(), { waitUntil: 'load' })
  await surfacePainted(page)
  await layoutSettled(page)
  await expect(page.getByRole('button', { name: '+ New test procedure' })).toHaveCount(0)

  // The procedure-authoring form is still audited, through the only door it has.
  //
  // It used to be reached by pressing a control in the library, and that control is gone. Reaching it now
  // needs a decision that asked for a procedure, on a package, in a build whose requirements are materialised
  // — because a procedure binds to an exact revision and there is nothing to bind to otherwise. So the state
  // is built rather than hoped for, in a Program of its own: the showcase build has not materialised its
  // requirements, and mutating shared showcase packages to make one form auditable would put this test in
  // the way of every other journey reading the same page.
  //
  // What is emphatically not done here is adding a route, a flag or a shortcut that opens this dialog
  // without a package behind it. A form reached by a door the product does not have is not the form.
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Form Semantics ${suffix}`,
    programCode: `FS${suffix}`,
    projectName: 'Form Semantics Verification',
    softwareProduct: 'Form Semantics Product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()

  const impacts = JSON.stringify({
    trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
    baseline: 'Not Affected', collaboration: 'Not Affected',
  })
  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: workspace.project.id,
    targetReleaseId: workspace.release.id,
    type: 'System',
    title: 'Exact target for procedure authoring semantics',
    problem: 'The authoring form needs a materialised revision to bind to.',
    analysis: 'A procedure names the exact requirement revision it verifies.',
    solution: 'Introduce one exact revision.',
    requirementChanges: [{
      level: 'System', kind: 'Introduce',
      targetSectionId: await firstSectionId(request, workspace.project.id),
      statement: 'The product shall expose an exact verification target.',
      rationale: 'Form semantics qualification.',
      verificationMethod: 'Test',
      impactDispositionJson: impacts,
    }],
  } })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const changeRequest = await draftResponse.json()
  for (const [path, data] of [
    [`change-requests/${changeRequest.id}/submit`, { approvers: [{ userId: 'admin', name: 'Ignored' }] }],
    [`change-requests/${changeRequest.id}/approve`, { password: 'AeroLink!2026', meaning: 'Approved for exact verification applicability.' }],
  ] as const) {
    const response = await request.post(`${apiBase}/api/${path}`, { data })
    expect(response.ok(), await response.text()).toBeTruthy()
  }

  const baselineResponse = await request.post(`${apiBase}/api/baselines`, { data: {
    baseNumber: `SW-98.${suffix.slice(-2)}`, revision: 0,
    projectId: workspace.project.id, releaseId: workspace.release.id,
    name: 'Form semantics materialized software build',
  } })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const [path, data] of [
    ['selections', { changeRequestId: changeRequest.id }],
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), await response.text()).toBeTruthy()
  }

  // The approved requirement change raised a package. It concludes that test work is required, and one of its
  // decisions asks for a new procedure — which is exactly the state that offers "Author the procedure".
  const reviewsResponse = await request.get(`${apiBase}/api/releases/${workspace.release.id}/test-change-reviews`)
  expect(reviewsResponse.ok(), await reviewsResponse.text()).toBeTruthy()
  const review = (await reviewsResponse.json()).items.find((item: { discipline: string }) => item.discipline === 'System')
  expect(review, 'the approved requirement change raised no System test change request').toBeTruthy()
  const concluded = await request.post(`${apiBase}/api/test-change-reviews/${review.id}/conclusion`,
    { data: { testChangeRequired: true } })
  expect(concluded.ok(), await concluded.text()).toBeTruthy()

  const impactResponse = await request.get(`${apiBase}/api/releases/${workspace.release.id}/verification-impact`)
  expect(impactResponse.ok(), await impactResponse.text()).toBeTruthy()
  const items = (await impactResponse.json())
    .filter((item: { testChangeReviewId: string }) => item.testChangeReviewId === review.id)
  expect(items.length, 'the package carries no decision to answer').toBeGreaterThan(0)
  const resolved = await request.post(`${apiBase}/api/verification-impact/${items[0].id}/resolve`, { data: {
    outcome: 'NewProcedureRequired',
    rationale: 'No procedure exists for this behavior yet; one must be written.',
  } })
  expect(resolved.ok(), await resolved.text()).toBeTruthy()

  await page.goto(
    `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}/system-verification/coverage`,
    { waitUntil: 'load' })
  await surfacePainted(page)
  await layoutSettled(page)
  const assessment = page.locator('.downstreamAssessment').filter({ hasText: /SYSTCR-/ }).first()
  await expect(assessment).toBeVisible({ timeout: 30_000 })
  await assessment.getByRole('button', { name: 'Open assessment' }).click()
  const drawer = page.getByRole('dialog', { name: /test impact/ })
  await expect(drawer).toBeVisible({ timeout: 30_000 })
  await drawer.getByRole('button', { name: 'Author the procedure' }).first().click()

  const authoring = page.getByRole('dialog', { name: 'Propose a test procedure' })
  await expect(authoring).toBeVisible({ timeout: 30_000 })
  await inspect(page, 'Propose a test procedure', failures)
  for (const label of ['Title', 'Objective', 'Preconditions', 'Steps', 'Expected result',
    'Requirements it verifies', 'Why it is needed']) {
    await labelActivatesControl(page, label, 'Propose a test procedure', failures)
  }
  // The requirement selector is the one control whose name is not its own text, and the one most easily left
  // nameless — it carries help text inside the same label.
  await expect(authoring.getByLabel('Requirements it verifies')).toBeVisible()

  // The requirement the decision named is already chosen, so the link is not left to memory. That is also why
  // this form cannot be made to fail its own "say what you verify" guard by hand: the selector is `required`
  // and arrives populated, so the browser refuses the submit before the guard is reached. A refusal that does
  // happen is the server's, and what matters for semantics is that it is announced rather than only painted.
  expect(await authoring.getByLabel('Requirements it verifies')
    .evaluate(node => (node as HTMLSelectElement).selectedOptions.length),
  'the decision named a requirement, so the selector must arrive with it chosen').toBeGreaterThan(0)
  await authoring.getByLabel('Title').fill('Semantics probe')
  await authoring.getByLabel('Objective').fill('Probe the authoring form semantics.')
  await authoring.getByLabel('Preconditions').fill('None.')
  await authoring.getByLabel('Steps').fill('Exercise the exact requirement.')
  await authoring.getByLabel('Expected result').fill('The required behavior is observed.')
  await authoring.getByLabel('Why it is needed').fill('Nothing in this build covers the new requirement.')

  await page.route('**/procedure-changes', route => route.request().method() === 'POST'
    ? route.fulfill({
      status: 400,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'The selected exact requirement changed. Refresh and choose it again.' }),
    })
    : route.continue())
  await authoring.getByRole('button', { name: 'Propose procedure' }).click()
  const refusal = authoring.getByRole('alert')
  await expect(refusal).toContainText('The selected exact requirement changed')
  // Announced, not merely painted, and the engineer's input is still there to correct.
  await expect(refusal).toHaveAttribute('aria-live', 'assertive')
  await expect(authoring.getByLabel('Title')).toHaveValue('Semantics probe')
  await inspect(page, 'Propose a test procedure refused', failures)
  await page.unroute('**/procedure-changes')

  // Closing and reopening the controlled workspace leaves the semantics as they were.
  await authoring.getByRole('button', { name: 'Cancel' }).click()
  await expect(authoring).toHaveCount(0)
  await drawer.getByRole('button', { name: 'Author the procedure' }).first().click()
  await expect(authoring).toBeVisible({ timeout: 30_000 })
  await inspect(page, 'Propose a test procedure reopened', failures)
  await labelActivatesControl(page, 'Title', 'Propose a test procedure reopened', failures)

  // And it can be completed through the real workflow, which is the last thing a form has to do.
  await authoring.getByLabel('Title').fill('Semantics probe')
  await authoring.getByLabel('Objective').fill('Probe the authoring form semantics.')
  await authoring.getByLabel('Preconditions').fill('None.')
  await authoring.getByLabel('Steps').fill('Exercise the exact requirement.')
  await authoring.getByLabel('Expected result').fill('The required behavior is observed.')
  await authoring.getByLabel('Why it is needed').fill('Nothing in this build covers the new requirement.')
  await authoring.getByRole('button', { name: 'Propose procedure' }).click()
  await expect(authoring).toHaveCount(0, { timeout: 30_000 })
  await expect(page.getByRole('status')).toContainText(/Proposed on SYSTCR-|Proposed on the test change request/)

  expect(failures, `Form semantics defects:\n  ${failures.join('\n  ')}`).toEqual([])
})
