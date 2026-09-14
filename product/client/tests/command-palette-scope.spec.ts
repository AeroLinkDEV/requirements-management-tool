import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { login } from './auth'

async function dispatchShortcut(page: Page, modifier: 'ctrlKey' | 'metaKey') {
  return page.evaluate((key) => {
    let defaultPrevented = false
    const observer = (event: KeyboardEvent) => { defaultPrevented = event.defaultPrevented }
    window.addEventListener('keydown', observer)
    const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, [key]: true, key: 'k' })
    window.dispatchEvent(event)
    window.removeEventListener('keydown', observer)
    return defaultPrevented
  }, modifier)
}

test('Ctrl/Cmd+K is inert above a build and opens quick navigation inside one', async ({ page }) => {
  await login(page, 'admin', { openProject: false })

  expect(await dispatchShortcut(page, 'ctrlKey')).toBeFalsy()
  expect(await dispatchShortcut(page, 'metaKey')).toBeFalsy()
  await expect(page.getByRole('dialog', { name: 'Quick navigation' })).toHaveCount(0)

  await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
  await expect(page.getByRole('heading', { name: 'Software Builds' })).toBeVisible()
  await page.getByRole('button', { name: 'Open build 1.6' }).click()
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()

  expect(await dispatchShortcut(page, 'ctrlKey')).toBeTruthy()
  const palette = page.getByRole('dialog', { name: 'Quick navigation' })
  await expect(palette).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(palette).toHaveCount(0)

  expect(await dispatchShortcut(page, 'metaKey')).toBeTruthy()
  await expect(palette).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(palette).toHaveCount(0)
})

const projectDocumentPath = '/programs/search-program/projects/search-project/documentation-center'

async function projectSearchShell(page: Page) {
  const requests: string[] = []
  page.on('request', request => { if (request.url().includes('/api/')) requests.push(request.url()) })
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    const json = path === '/api/auth/me'
      ? { id: 'reader', userName: 'reader', displayName: 'Reader', isAdministrator: false, programs: [] }
      : path === '/api/workspaces'
        ? [{ program: { id: 'search-program', name: 'Search Program', code: 'SEARCH' }, projects: [{
          project: { id: 'search-project', name: 'Search Project', softwareProduct: 'Search Product' },
          releases: [{ id: 'search-build', version: '1.6', isReleased: false }],
        }] }]
        : path.endsWith('/configuration')
          ? { effectiveSteps: [{ catalogueEntry: 'System', capabilities: 15 }] }
          : path === '/api/search'
            ? { items: [
              { id: 'document-id', kind: 'managed-document', identifier: 'DOC-000001.00', title: 'Project document', state: 'Draft', discipline: 'project' },
              { id: 'requirement-id', kind: 'requirement', identifier: 'SYSR-000001.00', title: 'Requirement result', state: 'Approved', discipline: 'system', level: 'System' },
              { id: 'procedure-id', kind: 'test-procedure', identifier: 'SYSTP-000001.00', title: 'Procedure result', state: 'Approved', discipline: 'systemTest', level: 'System' },
            ] }
            : []
    await route.fulfill({ json })
  })
  await page.goto(projectDocumentPath)
  await expect(page.getByRole('button', { name: /Search & navigate/ })).toBeVisible()
  return requests
}

test('project-wide search keeps mixed results and opens documents without an invented build', async ({ page }) => {
  const requests = await projectSearchShell(page)
  expect(await dispatchShortcut(page, 'ctrlKey')).toBeTruthy()
  const palette = page.getByRole('dialog', { name: 'Quick navigation' })
  await palette.getByLabel('Search AeroLink').fill('record')
  for (const identifier of ['DOC-000001.00', 'SYSR-000001.00', 'SYSTP-000001.00'])
    await expect(palette.getByRole('link', { name: new RegExp(identifier) })).toBeVisible()
  const search = new URL(requests.find(url => url.includes('/api/search?'))!)
  expect(search.searchParams.get('projectId')).toBe('search-project')
  expect(search.searchParams.has('releaseId')).toBeFalsy()
  const document = palette.getByRole('link', { name: /DOC-000001/ })
  await expect(document).toHaveAttribute('href', `${projectDocumentPath}/document-id`)
  await document.click()
  await expect(page).toHaveURL(new RegExp(`${projectDocumentPath}/document-id$`))
  await expect(palette).toHaveCount(0)
  expect(requests.some(url => url.includes('/api/dashboard?'))).toBeFalsy()
})

for (const target of ['requirement', 'procedure', 'suggested', 'recent'] as const) {
  test(`project-wide ${target} selection requires the visual build overview`, async ({ page }) => {
    if (target === 'recent') await page.addInitScript(() => {
      localStorage.setItem('aerolink-recent-destinations', JSON.stringify([{
        key: 'artifact-requirement-stale', category: 'artifact', label: 'Remembered requirement', detail: 'Prior build destination',
        view: 'requirements', discipline: 'system', artifactId: 'stale', artifactKind: 'requirement', level: 'System', icon: 'requirements',
      }]))
    })
    const requests = await projectSearchShell(page)
    await page.getByRole('button', { name: /Search & navigate/ }).click()
    const palette = page.getByRole('dialog', { name: 'Quick navigation' })
    if (target === 'requirement' || target === 'procedure') await palette.getByLabel('Search AeroLink').fill('record')
    const name = target === 'requirement' ? /SYSR-000001/ : target === 'procedure' ? /SYSTP-000001/
      : target === 'recent' ? /Remembered requirement/ : /Command Center/
    const entry = palette.getByRole('link', { name })
    await expect(entry).toContainText('Choose a build to open this')
    await expect(entry).toHaveAttribute('href', '/projects/search-project/builds')
    await entry.click()
    await expect(page).toHaveURL(/\/projects\/search-project\/builds$/)
    await expect(page.getByRole('heading', { name: 'Software Builds', exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: /Open build 1.6/i })).toBeVisible()
    expect(requests.some(url => /\/api\/(dashboard|requirements)\?/.test(url))).toBeFalsy()
  })
}
