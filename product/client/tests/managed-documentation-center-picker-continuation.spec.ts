// Checkpoint C browser journey for #1040: the Documentation Center build relationship picker must freeze
// candidate membership across "Load more records" continuations, show builds committed later only after a
// fresh traversal, and link the exact selected build with its canonical deep link. The fixture drives the
// disposable browser SQLite database directly (through the same guarded schema the API host installed).
import { expect, test } from '@playwright/test'
import { DatabaseSync } from 'node:sqlite'
import { createHash } from 'node:crypto'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'
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
  const showcase = await showcaseSeed(request)
  await apiLogin(request, 'software.author')

  // Fifty-five extra in-work builds bring the picker to 57 candidates (default page size 50), so the
  // dialog exercises a real continuation. They are inserted directly into the disposable run database,
  // through the same guarded schema and allocator trigger the API host installed at startup.
  for (let index = 1; index <= 55; index++) {
    seedInWorkBuild(showcase.projectId, `2.${String(index).padStart(2, '0')}`)
  }

  const documentsResponse = await request.get(`${apiBase}/api/managed-documents?projectId=${showcase.projectId}`)
  expect(documentsResponse.ok(), await documentsResponse.text()).toBeTruthy()
  const document = (await documentsResponse.json() as { items: { id: string; acronym: string }[] })
    .items.find((item) => item.acronym === 'SDP')
  expect(document).toBeTruthy()

  await login(page, 'software.author', { openProject: false })
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center`)
  await expect(page.getByRole('heading', { name: 'Documentation Center' })).toBeVisible()
  await page.getByRole('button', { name: /SDP SDP-000001/ }).click()
  await expect(page.getByRole('heading', { name: 'FMS Software Development Plan' })).toBeVisible()
  await page.getByRole('button', { name: 'links', exact: true }).click()
  await page.getByRole('button', { name: '+ Link artifact' }).click()
  await page.getByLabel('Artifact type').selectOption({ label: 'Build' })

  // Page one: placeholder option + exactly 50 candidates. Wait for a known build option first so the
  // dialog's initial artifact-type load cannot clobber the Build list after the count assertion.
  // Option elements have no layout box, so presence is asserted with counts, never visibility.
  const options = page.locator('select[name="artifactId"] option')
  await expect(page.locator('select[name="artifactId"] option', { hasText: 'BUILD-1.5' })).toHaveCount(1)
  await expect(options).toHaveCount(51)
  await expect(page.getByRole('button', { name: 'Load more records' })).toBeVisible()

  // A build committed after page one must stay outside this traversal's continuation.
  seedInWorkBuild(showcase.projectId, '9.9')
  await expect(page.getByRole('button', { name: 'Load more records' })).toBeVisible()
  await page.getByRole('button', { name: 'Load more records' }).click()
  await expect(options).toHaveCount(58) // placeholder + all 57 candidates bound at page one
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
  await page.getByRole('button', { name: 'Links' }).click()
  const row = page.locator('.mdLinks > div').filter({
    has: page.locator('span').filter({ hasText: /^Release ·/ }),
  })
  await expect(row).toBeVisible()
  const href = await row.getByRole('link').getAttribute('href')
  expect(href).toMatch(new RegExp(`/releases/${selectedReleaseId}/command-center$`))

  // A fresh traversal re-establishes the boundary and now shows the late build.
  seedInWorkBuild(showcase.projectId, '2.56')
  await page.getByRole('button', { name: '+ Link artifact' }).click()
  await page.getByLabel('Artifact type').selectOption({ label: 'Build' })
  await expect(options).toHaveCount(51) // fresh page one over 59 candidates
  await page.getByRole('button', { name: 'Load more records' }).click()
  await expect(options).toHaveCount(60) // placeholder + 59, now including the formerly late BUILD-9.9
  await expect(page.locator('select[name="artifactId"] option', { hasText: 'BUILD-9.9' })).toHaveCount(1)
})
