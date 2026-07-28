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
  const signedInUnknown = new URL(page.url()).pathname

  expect(signedOut).toBe('/')
  expect(signedInUnknown).toBe('/')
})
