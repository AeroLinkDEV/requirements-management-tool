import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { apiBase, apiLogin, login, selectProgram, showcaseSeed } from './auth'

/**
 * No surface prints the account somebody signs in with where it means to name a person.
 *
 * `PeopleRegistry` had mapped every seeded account to a name, a role and a portrait since it was written, and
 * two surfaces used it. Everywhere else — audit histories, approval steps, evidence provenance, lock holders,
 * discussion authors — rendered `cm.fms` and `assurance.reviewer` at the reader. An audit trail exists to
 * record who did something; a login handle is a worse answer to that than a name.
 *
 * A sweep like that comes undone the moment somebody adds a surface, so this is the thing that keeps it done.
 * It checks the seeded accounts by name rather than guessing at a shape like `word.word`, which would trip
 * over file names, versions and hostnames.
 */
const seededAccounts = [
  'systems.lead',
  'systems.author',
  'systems.reviewer',
  'lead.reviewer',
  'test.engineer',
  'test.author',
  'verification.engineer',
  'assurance.reviewer',
  'manager.reviewer',
  'engineering.manager',
  'program.manager',
  'release.manager',
  'cm.fms',
  'software.lead',
  'software.author',
]

/**
 * Where an account name is the right answer, and must not be replaced.
 *
 * The signed-in user's own footer names their account because that is what it is for — telling you which
 * credentials this window is using. People & Authority administers accounts, so it shows them by definition.
 * A search field naturally echoes what was typed into it.
 */
const legitimate = ['.appNavigation footer', '.identityPage', '.personPicker', 'input', 'textarea', 'option']

async function accountsOnScreen(page: Page, accounts: string[]) {
  return page.evaluate(
    ({ accounts, legitimate }) => {
      const excluded = new Set<Element>()
      for (const selector of legitimate) {
        for (const root of document.querySelectorAll(selector)) {
          excluded.add(root)
          for (const child of root.querySelectorAll('*')) excluded.add(child)
        }
      }
      const found: string[] = []
      for (const element of document.querySelectorAll('body *')) {
        if (excluded.has(element)) continue
        const box = element.getBoundingClientRect()
        if (box.width === 0 || box.height === 0) continue
        // Only the element's own text, so one offending leaf is not reported as every ancestor too.
        const own = [...element.childNodes]
          .filter(node => node.nodeType === Node.TEXT_NODE)
          .map(node => (node.textContent || '').trim())
          .join(' ')
        if (!own) continue
        for (const account of accounts) {
          // Word-bounded: `software.author` must not match inside a longer identifier or a URL.
          if (new RegExp(`(^|[^\\w.])${account.replace('.', '\\.')}($|[^\\w.])`).test(own)) {
            found.push(`${account} in <${element.tagName.toLowerCase()}> "${own.slice(0, 60)}"`)
          }
        }
      }
      return [...new Set(found)]
    },
    { accounts, legitimate },
  )
}

test('no surface names a person by their account', async ({ page, request }) => {
  test.setTimeout(300_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  const { projectId } = await showcaseSeed(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')

  const links = page.locator('nav[aria-label="Primary navigation"] a[href]')
  await expect(links.first()).toBeAttached({ timeout: 30_000 })
  const routes = (
    await links.evaluateAll(nodes => nodes.map(node => (node as HTMLAnchorElement).getAttribute('href') ?? ''))
  ).filter(Boolean)

  const offenders: string[] = []
  for (const route of routes) {
    await page.goto(route, { waitUntil: 'load' })
    await page.waitForTimeout(900)
    for (const hit of await accountsOnScreen(page, seededAccounts)) {
      offenders.push(`${route.replace(/^.*\/releases\/[^/]+/, '')}: ${hit}`)
    }
  }

  // A change request detail page is where most of these appeared: the audit history, the author, the approval
  // steps. It is reached by asking the API for a change request rather than by clicking a row, because a
  // surface with no rows renders none of the text this is looking for — and a check that passes because it
  // found nothing to look at is worse than no check. The assertion below proves it is looking at something.
  // A change request lives under the release, not under the discipline: the navigation link is
  // `…/systems/change-requests`, and the record itself is `…/change-requests/{id}`.
  const releaseRoot = routes[0].replace(/\/[^/]+$/, '')
  const listed = await request.get(`${apiBase}/api/scrs?projectId=${projectId}&page=1&pageSize=5`)
  expect(listed.ok(), await listed.text()).toBeTruthy()
  const body = (await listed.json()) as { items?: { id: string }[] } | { id: string }[]
  const items = Array.isArray(body) ? body : (body.items ?? [])
  expect(items.length, 'FMSLIVE should have change requests to inspect').toBeGreaterThan(0)

  await page.goto(`${releaseRoot}/change-requests/${items[0].id}`, { waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: /Audit history/ })).toBeVisible({ timeout: 30_000 })
  const auditRows = page.locator('.auditRow')
  expect(await auditRows.count(), 'the audit history should have rows, or this proves nothing').toBeGreaterThan(0)
  // Every audit row names an actor, so at least one person's name must be on this page.
  const auditText = await page.locator('.auditRow').first().innerText()
  expect(auditText, 'the audit row should name a person').toMatch(/[A-Z][a-z]+ [A-Z][a-z]+/)

  for (const hit of await accountsOnScreen(page, seededAccounts)) {
    offenders.push(`change request detail: ${hit}`)
  }

  expect(offenders, `Surfaces naming a person by their account:\n  ${offenders.join('\n  ')}`).toEqual([])
})
