# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: managed-documentation-center-picker-continuation.spec.ts >> the build picker freezes membership across pages and links the exact selected build
- Location: tests\managed-documentation-center-picker-continuation.spec.ts:40:1

# Error details

```
Error: {"error":"The document steward and responsible owner must be an active authorized member or delegate in this Program."}

expect(received).toBeTruthy()

Received: false
```

# Test source

```ts
  1   | // Checkpoint C browser journey for #1040: the Documentation Center build relationship picker must freeze
  2   | // candidate membership across "Load more records" continuations, show builds committed later only after a
  3   | // fresh traversal, and link the exact selected build with its canonical deep link.
  4   | //
  5   | // Each attempt owns its Program/Project/document (created through the API as the administrator), so
  6   | // repetitions of this journey are isolated from each other and from the shared showcase fixture. The
  7   | // candidate builds are inserted into the attempt's disposable run database through the same guarded schema
  8   | // and allocator trigger the API host installed at startup; Guid text is UPPERCASE to match how
  9   | // Microsoft.Data.Sqlite persists Guid properties.
  10  | import { expect, test } from '@playwright/test'
  11  | import { DatabaseSync } from 'node:sqlite'
  12  | import { tmpdir } from 'node:os'
  13  | import { join } from 'node:path'
  14  | import { createHash } from 'node:crypto'
  15  | import { apiBase, apiLogin, login } from './auth'
  16  | import { browserStoragePath } from '../scripts/browser-storage.mjs'
  17  | 
  18  | function browserDatabasePath(): string {
  19  |   const runId = process.env.AEROLINK_E2E_RUN_ID
  20  |   if (!runId) throw new Error('AEROLINK_E2E_RUN_ID is not set for this Playwright run.')
  21  |   return join(browserStoragePath(runId), 'aerolink.db')
  22  | }
  23  | 
  24  | function seedInWorkBuild(projectId: string, version: string): string {
  25  |   // Microsoft.Data.Sqlite persists Guid properties as UPPERCASE text; the fixture must match that
  26  |   // representation exactly, or the seeded rows live in a parallel project identity.
  27  |   const releaseId = crypto.randomUUID().toUpperCase()
  28  |   const database = new DatabaseSync(browserDatabasePath())
  29  |   try {
  30  |     database.exec('PRAGMA busy_timeout = 10000')
  31  |     database
  32  |       .prepare('INSERT INTO software_releases ("Id", "ProjectId", "Version", "IsReleased") VALUES (?, ?, ?, 0)')
  33  |       .run(releaseId, projectId.toUpperCase(), version)
  34  |   } finally {
  35  |     database.close()
  36  |   }
  37  |   return releaseId
  38  | }
  39  | 
  40  | test('the build picker freezes membership across pages and links the exact selected build', async ({ page, request }) => {
  41  |   test.setTimeout(360_000)
  42  |   await apiLogin(request, 'admin')
  43  | 
  44  |   // A test-owned Program/Project/document per attempt keeps repetitions and the shared showcase isolated.
  45  |   const attempt = Date.now().toString(36) + Math.floor(Math.random() * 1296).toString(36)
  46  |   const programCode = `PCK${attempt.toUpperCase()}`
  47  |   const acronym = `PCK${attempt.slice(-4).toUpperCase()}`
  48  |   const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, {
  49  |     data: {
  50  |       programName: `Picker Continuation ${attempt}`,
  51  |       programCode,
  52  |       projectName: `Picker Continuation Project ${attempt}`,
  53  |       softwareProduct: 'Software',
  54  |       initialRelease: '1.0',
  55  |       initialReleaseIsReleased: false,
  56  |     },
  57  |   })
  58  |   expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  59  |   const workspace = await workspaceResponse.json() as {
  60  |     program: { id: string }
  61  |     project: { id: string }
  62  |   }
  63  |   const programId = workspace.program.id
  64  |   const projectId = workspace.project.id
  65  | 
  66  |   const meResponse = await request.get(`${apiBase}/api/auth/me`)
  67  |   expect(meResponse.ok(), await meResponse.text()).toBeTruthy()
  68  |   const adminUserId = (await meResponse.json() as { id: string }).id
  69  |   const grantResponse = await request.post(`${apiBase}/api/admin/users/${adminUserId}/memberships`, {
  70  |     data: { programId, role: 'Engineer' },
  71  |   })
  72  |   expect(grantResponse.ok(), await grantResponse.text()).toBeTruthy()
  73  | 
  74  |   const documentResponse = await request.post(`${apiBase}/api/managed-documents`, {
  75  |     data: {
  76  |       projectId,
  77  |       acronym,
  78  |       documentType: 'Software Configuration Management Plan',
  79  |       title: `Picker continuation ${attempt}`,
  80  |       ownerId: 'admin',
  81  |       formalChangeSummary: 'Picker continuation journey fixture.',
  82  |       operationKey: crypto.randomUUID(),
  83  |     },
  84  |   })
> 85  |   expect(documentResponse.ok(), await documentResponse.text()).toBeTruthy()
      |                                                                ^ Error: {"error":"The document steward and responsible owner must be an active authorized member or delegate in this Program."}
  86  | 
  87  |   // Fifty-five extra in-work builds bring the picker to 56 candidates (default page size 50), so the
  88  |   // dialog exercises a real continuation. They are inserted directly into the attempt's disposable run
  89  |   // database, through the same guarded schema and allocator trigger the API host installed at startup.
  90  |   for (let index = 1; index <= 55; index++) {
  91  |     seedInWorkBuild(projectId, `2.${String(index).padStart(2, '0')}`)
  92  |   }
  93  | 
  94  |   await login(page, 'admin', { openProject: false })
  95  |   await page.goto(`/programs/${programId}/projects/${projectId}/documentation-center`)
  96  |   await expect(page.getByRole('heading', { name: 'Documentation Center' })).toBeVisible()
  97  |   await page.getByRole('button', { name: new RegExp(`^${acronym} `) }).click()
  98  |   await expect(page.getByRole('heading', { name: `Picker continuation ${attempt}` })).toBeVisible()
  99  |   await page.getByRole('button', { name: 'links', exact: true }).click()
  100 |   await page.getByRole('button', { name: '+ Link artifact' }).click()
  101 |   await page.getByLabel('Artifact type').selectOption({ label: 'Build' })
  102 | 
  103 |   // Page one: placeholder option + exactly 50 candidates. Wait for a known build option first so the
  104 |   // dialog's initial artifact-type load cannot clobber the Build list after the count assertion.
  105 |   // Option elements have no layout box, so presence is asserted with counts, never visibility.
  106 |   const options = page.locator('select[name="artifactId"] option')
  107 |   await expect(page.locator('select[name="artifactId"] option', { hasText: 'BUILD-1.0' })).toHaveCount(1)
  108 |   await expect(options).toHaveCount(51)
  109 |   await expect(page.getByRole('button', { name: 'Load more records' })).toBeVisible()
  110 | 
  111 |   // A build committed after page one must stay outside this traversal's continuation.
  112 |   seedInWorkBuild(projectId, '9.9')
  113 |   await expect(page.getByRole('button', { name: 'Load more records' })).toBeVisible()
  114 |   await page.getByRole('button', { name: 'Load more records' }).click()
  115 |   await expect(options).toHaveCount(57) // placeholder + all 56 candidates bound at page one
  116 |   await expect(page.locator('select[name="artifactId"] option', { hasText: 'BUILD-9.9' })).toHaveCount(0)
  117 | 
  118 |   // Select a build that exists only on page two and link it.
  119 |   const selectedReleaseId = await page.$eval('select[name="artifactId"]', (select) => {
  120 |     const element = select as HTMLSelectElement
  121 |     const option = Array.from(element.options).find((candidate) => candidate.textContent?.startsWith('BUILD-2.55'))
  122 |     return option ? option.value : null
  123 |   })
  124 |   expect(selectedReleaseId).toBeTruthy()
  125 |   await page.selectOption('select[name="artifactId"]', selectedReleaseId!)
  126 |   await page.getByRole('button', { name: 'Link canonical artifact' }).click()
  127 |   await expect(page.getByText('The canonical lifecycle relationship was linked')).toBeVisible()
  128 | 
  129 |   // The saved relationship carries the exact selected Release identity and its canonical deep link.
  130 |   await page.getByRole('button', { name: 'links', exact: true }).click()
  131 |   const row = page.locator('.mdLinks > div').filter({
  132 |     has: page.locator('span').filter({ hasText: /^Release ·/ }),
  133 |   })
  134 |   await expect(row).toBeVisible()
  135 |   const href = await row.getByRole('link').getAttribute('href')
  136 |   expect(href).toMatch(new RegExp(`/releases/${selectedReleaseId}/command-center$`))
  137 | 
  138 |   // A fresh traversal re-establishes the boundary and now shows the late build.
  139 |   seedInWorkBuild(projectId, '2.56')
  140 |   await page.getByRole('button', { name: '+ Link artifact' }).click()
  141 |   await page.getByLabel('Artifact type').selectOption({ label: 'Build' })
  142 |   await expect(options).toHaveCount(51) // fresh page one over 58 candidates
  143 |   await page.getByRole('button', { name: 'Load more records' }).click()
  144 |   await expect(options).toHaveCount(59) // placeholder + all 58, now including the formerly late BUILD-9.9
  145 |   await expect(page.locator('select[name="artifactId"] option', { hasText: 'BUILD-9.9' })).toHaveCount(1)
  146 | })
  147 | 
```