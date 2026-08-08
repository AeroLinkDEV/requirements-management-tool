import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { apiBase, apiLogin, login, selectProgram, showcaseSeed , surfacePainted } from './auth'

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
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')

  // Asked for rather than hardcoded. The list below this was a fixed set of fifteen names, so an account added
  // to the showcase after it was written was never looked for — the check would keep passing while the surface
  // it was meant to guard printed a handle nobody had listed. The signed-in account is excluded because naming
  // it is what the footer is for.
  const accounts = await request.get(`${apiBase}/api/admin/users`)
  expect(accounts.ok(), await accounts.text()).toBeTruthy()
  const provisioned = ((await accounts.json()) as { userName: string }[])
    .map(x => x.userName)
    .filter(x => x !== 'admin')
  expect(provisioned.length, 'the showcase should provision accounts to check for').toBeGreaterThan(5)
  const checked = [...new Set([...seededAccounts, ...provisioned])]

  const links = page.locator('nav[aria-label="Primary navigation"] a[href]')
  await expect(links.first()).toBeAttached({ timeout: 30_000 })
  const routes = (
    await links.evaluateAll(nodes => nodes.map(node => (node as HTMLAnchorElement).getAttribute('href') ?? ''))
  ).filter(Boolean)

  const offenders: string[] = []
  for (const route of routes) {
    await page.goto(route, { waitUntil: 'load' })
    await surfacePainted(page)
    for (const hit of await accountsOnScreen(page, checked)) {
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
  const releaseId = releaseRoot.match(/\/releases\/([^/]+)/)?.[1]
  const listed = await request.get(`${apiBase}/api/change-requests?projectId=${projectId}&releaseId=${releaseId}&page=1&pageSize=5`)
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

  for (const hit of await accountsOnScreen(page, checked)) {
    offenders.push(`change request detail: ${hit}`)
  }

  // Everything above sees only what a route renders first, and most of this product's attribution is one click
  // further in: the job engine, the concurrency ledger, the procedure list, the evidence trail. A tab nobody
  // opened is a surface nobody audited, which is how the original handles survived a sweep of every route.
  const sweepTabs = async (surface: string, tabs: string[]) => {
    for (const tab of tabs) {
      // Labels carry counts, so this matches on a substring rather than the whole accessible name.
      const button = page.getByRole('button', { name: tab }).first()
      if (await button.count() === 0) continue
      await button.click()
      await surfacePainted(page)
      for (const hit of await accountsOnScreen(page, checked)) offenders.push(`${surface} › ${tab}: ${hit}`)
    }
  }

  await page.goto(`${releaseRoot}/enterprise-control`, { waitUntil: 'load' })
  await surfacePainted(page)
  await page.getByRole('button', { name: 'Job engine' }).first().click()
  // The job engine names whoever created a job, so a job is provisioned here rather than hoped for — an empty
  // table would let this pass by finding nothing to look at. Queued through the control an operator uses.
  await page.getByRole('button', { name: 'Generate controlled export' }).first().click()
  await expect(page.locator('.jobTable article').first()).toBeVisible({ timeout: 30_000 })
  await sweepTabs('System Operations', [
    'Operations', 'Content vault', 'Redlines', 'Query builder', 'Job engine',
    'Product line', 'Assurance', 'Qualification',
  ])

  // The Explorer is swept too: procedure authorship moved there with the library, and "written by" is exactly
  // the kind of place an account handle leaks out in place of a person's name.
  for (const [surface, path] of [
    ['Change Requests', 'coverage'],
    ['Test Procedure Explorer', 'procedures'],
    ['Test Results', 'results'],
  ] as const) {
    await page.goto(`${releaseRoot}/system-verification/${path}`, { waitUntil: 'load' })
    await surfacePainted(page)
    for (const hit of await accountsOnScreen(page, checked)) offenders.push(`${surface}: ${hit}`)
  }

  // A dialog is a surface too, and the ones that distribute or decide work are precisely where a person is
  // named. Opened if the showcase has an item to open it on; skipped rather than failed if it does not, because
  // this test is about attribution and not about the queue having content.
  const assign = page.getByRole('button', { name: /^Assign/ }).first()
  if (await assign.count() > 0) {
    await assign.click()
    const dialog = page.getByRole('dialog')
    await expect(dialog.first()).toBeVisible({ timeout: 30_000 })
    for (const hit of await accountsOnScreen(page, checked)) offenders.push(`assign dialog: ${hit}`)
    await dialog.first().getByRole('button', { name: 'Cancel' }).click()
  }

  expect(offenders, `Surfaces naming a person by their account:\n  ${offenders.join('\n  ')}`).toEqual([])
})
