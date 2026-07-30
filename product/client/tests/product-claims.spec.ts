import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { apiLogin, login, selectProgram , surfacePainted } from './auth'

/**
 * The product must never appear to claim certification or tool qualification.
 *
 * This is the first boundary in SCOPE_AND_BOUNDARIES.md and the first question the software quality group
 * asks, and until this spec existed it was enforced only by everyone remembering. It was already broken:
 * the Assurance surface rendered a status badge reading, in isolation and at 32px, **QUALIFIED**. It was
 * describing a workload scale target — the word came from `WorkloadQualificationEvidence` — but nothing on
 * screen said so, and a reader in an aerospace review does not supply that context charitably.
 *
 * Nothing seeded the record, so the badge always read NOT QUALIFIED in practice. That is not a defence: the
 * record is writable through `POST /api/operations/qualification-runs`, so the claim was one self-reported
 * request away from appearing, on the screen most likely to be shown to the people it would mislead.
 *
 * Wording, not vocabulary, is what this checks. "Workload qualification evidence" is fine, because the noun
 * it qualifies is right there. A bare "QUALIFIED" is not.
 */

/**
 * Standalone words that read as a claim about the tool when nothing else is in the element.
 *
 * The negations are here on purpose. "NOT QUALIFIED" is not a false claim, but as a bare status it still
 * asserts that being *qualified* is a state this product reports on — which invites exactly the reading the
 * boundary forbids, and puts the product one seeded record away from displaying the other half of the pair.
 * The vocabulary is what has to go, not one value of it. It is also the only way this check can be exercised:
 * nothing seeds the evidence record, so the affirmative label never renders in a test run.
 */
const forbiddenAlone = [
  /^(not\s+)?qualified$/i,
  /^(not\s+)?certified$/i,
  /^(not\s+)?compliant$/i,
  /^do-178[a-c]?$/i,
  /^arp4754a?$/i,
]

/** Phrases that are a claim however they are surrounded. */
const forbiddenAnywhere = [
  /\btool[- ]qualified\b/i,
  /\bis certified\b/i,
  /\bcertification (?:achieved|approved|granted)\b/i,
  /\bdo-178[a-c]? (?:compliant|certified|qualified)\b/i,
  /\barp4754a? (?:compliant|certified|qualified)\b/i,
]

async function claimsOn(page: Page) {
  return page.evaluate(
    ({ alone, anywhere }) => {
      const aloneExpressions = alone.map(source => new RegExp(source, 'i'))
      const anywhereExpressions = anywhere.map(source => new RegExp(source, 'i'))
      const visible = (element: Element) => {
        const box = element.getBoundingClientRect()
        return box.width > 0 && box.height > 0
      }
      const found: string[] = []
      for (const element of document.querySelectorAll('body *')) {
        if (!visible(element)) continue
        // Only the element's own text, so a parent is not blamed for a child's words.
        const own = [...element.childNodes]
          .filter(node => node.nodeType === Node.TEXT_NODE)
          .map(node => (node.textContent || '').trim())
          .join(' ')
          .trim()
        if (!own) continue
        if (aloneExpressions.some(expression => expression.test(own))) found.push(`"${own}" stands alone`)
        const whole = (element.textContent || '').trim()
        for (const expression of anywhereExpressions) {
          if (expression.test(whole)) found.push(`"${whole.slice(0, 80)}" matches ${expression}`)
        }
      }
      return [...new Set(found)]
    },
    { alone: forbiddenAlone.map(expression => expression.source), anywhere: forbiddenAnywhere.map(expression => expression.source) },
  )
}

test('no surface presents a certification or tool-qualification claim', async ({ page, request }) => {
  test.setTimeout(300_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')

  const links = page.locator('nav[aria-label="Primary navigation"] a[href]')
  await expect(links.first()).toBeAttached({ timeout: 30_000 })
  const routes = (
    await links.evaluateAll(nodes => nodes.map(node => (node as HTMLAnchorElement).getAttribute('href') ?? ''))
  ).filter(Boolean)

  const violations: string[] = []
  for (const route of routes) {
    await page.goto(route, { waitUntil: 'load' })
    await surfacePainted(page)
    for (const claim of await claimsOn(page)) violations.push(`${route.replace(/^.*\/releases\/[^/]+/, '')}: ${claim}`)
  }

  // Enterprise Control keeps its strongest wording behind tabs, and a tab nobody opened is a surface nobody
  // audited — which is how the badge survived. Open each one.
  const enterprise = routes.find(route => route.includes('/enterprise-control'))
  if (enterprise) {
    await page.goto(enterprise, { waitUntil: 'load' })
    for (const tab of ['Assurance', 'Qualification']) {
      await page.getByRole('button', { name: tab, exact: true }).click()
      await page.waitForTimeout(700)
      for (const claim of await claimsOn(page)) violations.push(`enterprise-control/${tab}: ${claim}`)
    }
  }

  expect(violations, `Surfaces presenting a claim the product does not make:\n  ${violations.join('\n  ')}`).toEqual([])
})

test('the assurance surface states the boundary rather than leaving it to be inferred', async ({ page, request }) => {
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')

  const links = page.locator('nav[aria-label="Primary navigation"] a[href]')
  await expect(links.first()).toBeAttached({ timeout: 30_000 })
  const enterprise = (
    await links.evaluateAll(nodes => nodes.map(node => (node as HTMLAnchorElement).getAttribute('href') ?? ''))
  ).find(href => href.includes('/enterprise-control'))
  expect(enterprise, 'Enterprise Control should be reachable from the navigation').toBeTruthy()

  await page.goto(enterprise!, { waitUntil: 'load' })
  await page.getByRole('button', { name: 'Assurance', exact: true }).click()

  // Saying it on the page is worth more than not saying the wrong thing. This is the screen where the scale
  // numbers live, so it is the screen where the misreading was available.
  await expect(page.getByText(/tool-qualification or certification claim/i)).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText(/WORKLOAD TARGET (NOT )?MET/)).toBeVisible()
})
