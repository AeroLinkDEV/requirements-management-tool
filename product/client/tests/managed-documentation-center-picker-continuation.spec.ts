// Checkpoint C browser journey for #1040: the Documentation Center build relationship picker must freeze
// candidate membership across "Load more records" continuations, show builds committed later only after a
// fresh traversal, and link the exact selected build with its canonical deep link.
//
// Each attempt owns its Program/Project/document (created through the API as the administrator), so
// repetitions of this journey are isolated from each other and from the shared showcase fixture. The
// candidate builds are inserted into the attempt's disposable run database through the same guarded schema
// and allocator trigger the API host installed at startup; Guid text is UPPERCASE to match how
// Microsoft.Data.Sqlite persists Guid properties.
import { expect, test } from '@playwright/test'
import { DatabaseSync } from 'node:sqlite'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createHash } from 'node:crypto'
import { apiBase, apiLogin, login } from './auth'
import { browserStoragePath } from '../scripts/browser-storage.mjs'

function browserDatabasePath(): string {
  const runId = process.env.AEROLINK_E2E_RUN_ID
  if (!runId) throw new Error('AEROLINK_E2E_RUN_ID is not set for this Playwright run.')
  return join(browserStoragePath(runId), 'aerolink.db')
}

function seedInWorkBuild(projectId: string, version: string): string {
  // Microsoft.Data.Sqlite persists Guid properties as UPPERCASE text; the fixture must match that
  // representation exactly, or the seeded rows live in a parallel project identity.
  const releaseId = crypto.randomUUID().toUpperCase()
  const database = new DatabaseSync(browserDatabasePath())
  try {
    database.exec('PRAGMA busy_timeout = 10000')
    database
      .prepare('INSERT INTO software_releases ("Id", "ProjectId", "Version", "IsReleased") VALUES (?, ?, ?, 0)')
      .run(releaseId, projectId.toUpperCase(), version)
  } finally {
    database.close()
  }
  return releaseId
}

test('the build picker freezes membership across pages and links the exact selected build', async ({ page, request }) => {
  test.setTimeout(360_000)
  await apiLogin(request, 'admin')

  // A test-owned Program/Project/document per attempt keeps repetitions and the shared showcase isolated.
  // The steward is a dedicated non-administrator member (administrator status is not document-authoring
  // authority): the fixture rotates the steward's password to the shared suite password so the UI journey
  // can sign in as the responsible owner, whose relationship authority the product recognizes.
  const attempt = Date.now().toString(36) + Math.floor(Math.random() * 1296).toString(36)
  const programCode = `PCK${attempt.toUpperCase()}`.slice(0, 20)
  const acronymSuffix = Array.from({ length: 4 }, () =>
    'ABCDEFGHIJKLMNOPQRSTUVWXYZ'[Math.floor(Math.random() * 26)]).join('')
  const acronym = `PCK${acronymSuffix}`
  const stewardUserName = `picker.author.${attempt}`
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName: `Picker Continuation ${attempt}`,
      programCode,
      projectName: `Picker Continuation Project ${attempt}`,
      softwareProduct: 'Software',
      initialRelease: '1.0',
      initialReleaseIsReleased: false,
    },
  })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json() as {
    program: { id: string }
    project: { id: string }
  }
  const programId = workspace.program.id
  const projectId = workspace.project.id

  const meResponse = await request.get(`${apiBase}/api/auth/me`)
  expect(meResponse.ok(), await meResponse.text()).toBeTruthy()
  const adminUserId = (await meResponse.json() as { id: string }).id

  const authorResponse = await request.post(`${apiBase}/api/admin/users`, {
    data: {
      userName: stewardUserName,
      displayName: `Picker Author ${attempt}`,
      email: `${stewardUserName}@example.test`,
      temporaryPassword: `Picker-Author!${attempt}`,
    },
  })
  expect(authorResponse.ok(), await authorResponse.text()).toBeTruthy()
  const authorUserId = (await authorResponse.json() as { id: string }).id

  const grantResponse = await request.post(`${apiBase}/api/admin/users/${authorUserId}/memberships`, {
    data: { programId, role: 'Engineer' },
  })
  expect(grantResponse.ok(), await grantResponse.text()).toBeTruthy()

  // The steward rotates the temporary password to the shared suite password (rotation revokes all
  // sessions and swaps this request context's cookie to the steward, so the subsequent document
  // creation happens with steward identity).
  const tempLogin = await request.post(`${apiBase}/api/auth/login`, {
    data: { userName: stewardUserName, password: `Picker-Author!${attempt}` },
  })
  expect(tempLogin.ok(), await tempLogin.text()).toBeTruthy()
  const rotate = await request.post(`${apiBase}/api/auth/password`, {
    data: { currentPassword: `Picker-Author!${attempt}`, newPassword: 'AeroLink!2026' },
  })
  expect(rotate.status(), await rotate.text()).toBe(204)

  // Rotation revoked every session (including this context's cookie): sign in again as the steward.
  const stewardLogin = await request.post(`${apiBase}/api/auth/login`, {
    data: { userName: stewardUserName, password: 'AeroLink!2026' },
  })
  expect(stewardLogin.ok(), await stewardLogin.text()).toBeTruthy()

  const documentResponse = await request.post(`${apiBase}/api/managed-documents`, {
    data: {
      projectId,
      acronym,
      documentType: 'Software Configuration Management Plan',
      title: `Picker continuation ${attempt}`,
      ownerId: stewardUserName,
      formalChangeSummary: 'Picker continuation journey fixture.',
      operationKey: crypto.randomUUID(),
    },
  })
  expect(documentResponse.ok(), await documentResponse.text()).toBeTruthy()

  // Fifty-five extra in-work builds bring the picker to 56 candidates (default page size 50), so the
  // dialog exercises a real continuation. They are inserted directly into the attempt's disposable run
  // database, through the same guarded schema and allocator trigger the API host installed at startup.
  for (let index = 1; index <= 55; index++) {
    seedInWorkBuild(projectId, `2.${String(index).padStart(2, '0')}`)
  }

  await login(page, stewardUserName, { openProject: false })
  await page.goto(`/programs/${programId}/projects/${projectId}/documentation-center`)
  await expect(page.getByRole('heading', { name: 'Documentation Center' })).toBeVisible()
  await page.getByRole('button', { name: new RegExp(`^${acronym} `) }).click()
  await expect(page.getByRole('heading', { name: `Picker continuation ${attempt}` })).toBeVisible()
  await page.getByRole('button', { name: 'links', exact: true }).click()
  await page.getByRole('button', { name: '+ Link artifact' }).click()
  await page.getByLabel('Artifact type').selectOption({ label: 'Build' })

  // Page one: placeholder option + exactly 50 candidates. Wait for a known build option first so the
  // dialog's initial artifact-type load cannot clobber the Build list after the count assertion.
  // Option elements have no layout box, so presence is asserted with counts, never visibility.
  const options = page.locator('select[name="artifactId"] option')
  await expect(page.locator('select[name="artifactId"] option', { hasText: 'BUILD-1.0' })).toHaveCount(1)
  await expect(options).toHaveCount(51)
  await expect(page.getByRole('button', { name: 'Load more records' })).toBeVisible()

  // A build committed after page one must stay outside this traversal's continuation.
  seedInWorkBuild(projectId, '9.9')
  await expect(page.getByRole('button', { name: 'Load more records' })).toBeVisible()
  await page.getByRole('button', { name: 'Load more records' }).click()
  await expect(options).toHaveCount(57) // placeholder + all 56 candidates bound at page one
  await expect(page.locator('select[name="artifactId"] option', { hasText: 'BUILD-9.9' })).toHaveCount(0)

  // Select a build that exists only on page two and link it.
  const selectedReleaseId = await page.$eval('select[name="artifactId"]', (select) => {
    const element = select as HTMLSelectElement
    const option = Array.from(element.options).find((candidate) => candidate.textContent?.startsWith('BUILD-2.55'))
    return option ? option.value : null
  })
  expect(selectedReleaseId).toBeTruthy()
  await page.selectOption('select[name="artifactId"]', selectedReleaseId!)
  await page.getByRole('button', { name: 'Link canonical artifact' }).click()
  await expect(page.getByText('The canonical lifecycle relationship was linked')).toBeVisible()

  // The saved relationship carries the exact selected Release identity and its canonical deep link.
  await page.getByRole('button', { name: 'links', exact: true }).click()
  const row = page.locator('.mdLinks > div').filter({
    has: page.locator('span').filter({ hasText: /^Release ·/ }),
  })
  await expect(row).toBeVisible()
  const href = await row.getByRole('link').getAttribute('href')
  expect(href).toMatch(new RegExp(`/releases/${selectedReleaseId}/command-center$`))

  // A fresh traversal re-establishes the boundary and now shows the late build.
  seedInWorkBuild(projectId, '2.56')
  await page.getByRole('button', { name: '+ Link artifact' }).click()
  await page.getByLabel('Artifact type').selectOption({ label: 'Build' })
  await expect(options).toHaveCount(51) // fresh page one over 58 candidates
  await page.getByRole('button', { name: 'Load more records' }).click()
  await expect(options).toHaveCount(59) // placeholder + all 58, now including the formerly late BUILD-9.9
  await expect(page.locator('select[name="artifactId"] option', { hasText: 'BUILD-9.9' })).toHaveCount(1)
})
