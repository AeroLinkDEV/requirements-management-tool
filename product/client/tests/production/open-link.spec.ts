import { expect, test } from '@playwright/test'
import { login, selectProgram } from '../auth'

/**
 * The link an email or a Jira issue actually contains, followed the way a recipient follows it.
 *
 * Notifications emitted paths such as `/systems/change-requests/{id}`, which the client router cannot
 * resolve — every application route lives beneath `/programs/{p}/projects/{pr}/releases/{r}/`. A recipient
 * received a valid-looking link to a controlled record and landed on Not Found. The unit tests were green
 * throughout, because comparing a generated string to an expected string proves the two agree and nothing
 * about whether either one opens anything.
 *
 * This runs against the build because that is what a deployment serves, and because the redirect has to
 * survive the server's SPA fallback rather than a dev server's.
 */
test('a notification link resolves its own context and opens the exact record', async ({ page, baseURL }) => {
  test.setTimeout(120_000)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')

  // page.request carries the signed-in session; the standalone fixture does not.
  const workspaces = await (await page.request.get(`${baseURL}/api/workspaces`)).json()
  const fms = workspaces.find((x: { program: { name: string } }) => x.program.name === 'Flight Management System Live Program')
  const project = fms.projects[0].project

  // Take a real change request the way an emitter would: by its id, with no context attached.
  const found = await (await page.request.get(`${baseURL}/api/search?projectId=${project.id}&query=SCR-&limit=5`)).json()
  const scr = found.items.find((x: { kind: string }) => x.kind === 'change-request')
  expect(scr, 'the showcase must contain at least one change request').toBeTruthy()

  await page.goto(`${baseURL}/open/scr/${scr.id}`, { waitUntil: 'load' })

  // The address the reader ends on is the canonical contextual one, not the context-free one they clicked.
  await expect(page).toHaveURL(new RegExp(`/programs/[^/]+/projects/[^/]+/releases/[^/]+/(systems|software)/change-requests/${scr.id}$`))
})

test('an unknown record and an unauthenticated reader are answered identically', async ({ page, baseURL }) => {
  test.setTimeout(120_000)

  // Signed out: no session, so nothing about the artifact may be revealed — not even that it exists.
  await page.context().clearCookies()
  await page.goto(`${baseURL}/open/scr/11111111-1111-1111-1111-111111111111`, { waitUntil: 'load' })
  const signedOut = new URL(page.url()).pathname

  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  await page.goto(`${baseURL}/open/scr/11111111-1111-1111-1111-111111111111`, { waitUntil: 'load' })
  // Read the address only once the app has settled. The resolver decides where to send an unresolvable link
  // after the session is known, so sampling the path on `load` alone catches it mid-decision and returns
  // whichever answer won that run — this assertion failed in both directions before the wait was added.
  await expect(page.getByRole('heading', { name: 'Projects' })).toBeVisible({ timeout: 30_000 })
  const signedInUnknown = new URL(page.url()).pathname

  // Each reader is returned to their own starting point and told nothing about the record: signed out that is
  // the sign-in page, signed in it is the Projects portal. They stopped being the same address when the portal
  // was added, and that is right — bouncing a signed-in reader to the sign-in page would be the defect.
  //
  // What this proves is that neither reader is given a Not Found for a record they may not see. What it does
  // not prove is that an unknown record and a real record outside the reader's Programs are indistinguishable;
  // that needs a second account with no access to the record, which this fixture does not have.
  expect(signedOut).toBe('/')
  expect(signedInUnknown).toBe('/projects')
})
