import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, login } from './auth'

/**
 * Bringing in a program that already exists in another requirements tool.
 *
 * The page's job is to make an imported baseline impossible to mistake for one this product built. These
 * walk the five gates a person actually walks, and check the two statements that carry that weight: what
 * accepting asserts, and what it explicitly does not.
 */
test.describe('imported baselines', () => {
  const digest = 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678abcdef0123456789abcdef01'

  async function openImports(page: import('@playwright/test').Page) {
    await login(page, 'admin', { openProject: false })
    if (await page.getByRole('heading', { name: 'Projects' }).count()) {
      await page.getByRole('link', { name: 'Open FMS Product Development' }).click()
      await expect(page.getByRole('heading', { name: 'Software Builds' })).toBeVisible()
    }
    // Reached from Software Builds rather than from inside a build, because an import creates a build
    // rather than belonging to one.
    await page.getByRole('button', { name: 'Imported baselines' }).click()
    await expect(page.getByRole('heading', { name: 'Imported baselines' })).toBeVisible()
  }

  test('the page states what an import is, and what it never claims', async ({ page }) => {
    await openImports(page)

    // The distinction the whole feature exists to hold, said on the page rather than left to be inferred.
    await expect(page.getByText(/is not a change request/)).toBeVisible()
    await expect(page.getByText(/externally sourced baseline/)).toBeVisible()
  })

  test('a source identifier the source retired is answerable, and says it joins nothing', async ({ page, request }) => {
    await apiLogin(request)
    const workspaces = await (await request.get(`${apiBase}/api/workspaces`)).json()
    const projectId = workspaces[0].projects[0].project.id

    const created = await request.post(`${apiBase}/api/baseline-imports`, {
      data: {
        projectId,
        sourceSystem: 'IBM Rational DOORS',
        sourceSystemVersion: '9.6.1.13',
        sourceBaselineName: 'FMS Sys Req v4.2',
        sourceBaselineDate: '2026-06-30T00:00:00Z',
        extractFileName: 'FMS_SYSTEM_REQUIREMENTS.reqifz',
        extractSha256: digest,
        extractSizeBytes: 43842112,
        carries: ['Requirements'],
        extractedBy: 'm.chen',
        extractedAt: '2026-07-14T09:12:00Z',
      },
    })
    expect(created.ok(), await created.text()).toBeTruthy()
    const importId = (await created.json()).id

    await request.post(`${apiBase}/api/baseline-imports/${importId}/analysis`)
    await request.post(`${apiBase}/api/baseline-imports/${importId}/mapping`, {
      data: { mappingJson: '{"modules":{"FMS_System_Requirements":"System"}}' },
    })
    const recorded = await request.post(`${apiBase}/api/baseline-imports/${importId}/source-records`, {
      data: {
        records: [
          {
            sourceModule: 'FMS_System_Requirements', sourceObjectKey: '1234',
            sourceIdentifier: 'SYS-01234', inImportedBaseline: true,
            history: [{
              sourceBaselineName: 'V0.9',
              statement: 'The FMS shall annunciate a navigation source disagreement.',
              changedBy: 'a.okafor', changedAt: '2025-01-22T00:00:00Z', sourceChangeReference: 'DOORS CR-1402',
            }],
          },
          {
            sourceModule: 'FMS_System_Requirements', sourceObjectKey: '1233',
            sourceIdentifier: 'SYS-01233', inImportedBaseline: false, history: [],
          },
        ],
      },
    })
    expect(recorded.ok(), await recorded.text()).toBeTruthy()

    await openImports(page)

    // The question this record exists to answer: somebody holding a drawing that cites a retired identifier
    // should get an answer rather than an empty result they read as the tool having lost it.
    await page.getByRole('textbox',{name:'Source identifier'}).fill('SYS-01233')
    await page.getByRole('button', { name: 'Look it up' }).click()
    await expect(page.getByText('SYS-01233')).toBeVisible()
    await expect(page.getByText(/the source retired it earlier/)).toBeVisible()
    await expect(page.getByText(/nothing originates from it/)).toBeVisible()

    // Source history is shown as reported by the source, attributed to it, and claimed by nobody here.
    await page.getByRole('textbox',{name:'Source identifier'}).fill('SYS-01234')
    await page.getByRole('button', { name: 'Look it up' }).click()
    await expect(page.getByText('DOORS CR-1402')).toBeVisible()

    // The import itself shows what it holds, with the two kinds of record kept apart.
    await page.getByRole('button', { name: /FMS Sys Req v4.2/ }).click()
    await expect(page.getByText('In the imported baseline')).toBeVisible()
    await expect(page.getByText('Retired before this baseline')).toBeVisible()
    await expect(page.getByText(/does .*not.* assert|were not/)).toBeVisible()
  })
})
