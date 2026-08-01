import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { apiLogin, layoutSettled, login, selectProgram, surfacePainted } from './auth'

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

  // The other half: authoring a procedure, which is a longer form and the only one on the coverage page.
  await page.goto(new URL(root + '/system-verification/coverage', page.url()).toString(), { waitUntil: 'load' })
  await surfacePainted(page)
  await layoutSettled(page)
  await page.getByRole('button', { name: '+ New test procedure' }).click()
  await expect(page.getByRole('dialog', { name: 'Create a test procedure' })).toBeVisible({ timeout: 30_000 })
  await inspect(page, 'Create a test procedure', failures)
  for (const label of ['Title', 'Objective', 'Expected result']) {
    await labelActivatesControl(page, label, 'Create a test procedure', failures)
  }

  expect(failures, `Form semantics defects:\n  ${failures.join('\n  ')}`).toEqual([])
})
