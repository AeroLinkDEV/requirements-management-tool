import { expect, test } from '@playwright/test'
import { login, openNavigationGroup } from './auth'

test('software change control keeps HLR and LLR context through links, refresh, history, and released builds', async ({page}) => {
  await login(page)
  await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  await page.getByRole('link',{name:'Software Change Requests'}).click()

  await expect(page).toHaveURL(/\/software\/change-requests\?level=HLR$/)
  await expect(page.getByRole('button',{name:/^HLR High-level requirements$/})).toHaveAttribute('aria-current','page')
  await expect(page.getByRole('button',{name:'+ New HLR Change Request'})).toBeVisible()
  await expect(page.getByRole('button',{name:'+ New LLR Change Request'})).toHaveCount(0)
  await expect(page.locator('.historyContext')).toContainText('HLR area')

  const llrHistory=page.waitForResponse(response=>response.url().includes('/api/history/scrs?')&&response.url().includes('level=LowLevel'))
  const llrAssessments=page.waitForResponse(response=>response.url().includes('/api/downstream-assessments?')&&response.url().includes('targetLevel=LowLevel'))
  await page.getByRole('button',{name:/^LLR Low-level requirements$/}).click()
  await Promise.all([llrHistory,llrAssessments])
  await expect(page).toHaveURL(/\/software\/change-requests\?level=LLR$/)
  await expect(page.getByRole('button',{name:'+ New LLR Change Request'})).toBeVisible()
  await expect(page.locator('.historyContext')).toContainText('LLR area')

  const llrUrl=page.url()
  await page.reload()
  await expect(page).toHaveURL(llrUrl)
  await expect(page.getByRole('button',{name:/^LLR Low-level requirements$/})).toHaveAttribute('aria-current','page')
  await page.goBack()
  await expect(page).toHaveURL(/\/software\/change-requests\?level=HLR$/)
  await expect(page.getByRole('button',{name:/^HLR High-level requirements$/})).toHaveAttribute('aria-current','page')

  await page.getByRole('button',{name:'Back to Software Builds'}).click()
  await page.getByRole('button',{name:'Open build 1.5'}).click()
  await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  await page.getByRole('link',{name:'Software Change Requests'}).click()
  await expect(page).toHaveURL(/\/software\/change-requests\?level=HLR$/)
  await expect(page.getByRole('button',{name:/^HLR High-level requirements$/})).toHaveAttribute('aria-current','page')
  await expect(page.getByRole('button',{name:/^LLR Low-level requirements$/})).toBeVisible()
  await expect(page.getByRole('button',{name:/^\+ New (HLR|LLR) Change Request$/})).toHaveCount(0)
  // The queue offers one entry control in every state, so a released build's read-only-ness has to be
  // checked where the actions actually live: inside the drawer.
  await expect(page.locator('.downstreamQueue button',{hasText:/Take it on|No change required|Send for approval|Approve|Return/})).toHaveCount(0)
  await page.locator('.downstreamAssessment').first().getByRole('button',{name:'Open assessment'}).click()
  const released=page.getByRole('dialog',{name:/downstream impact/})
  await expect(released).toBeVisible({timeout:30_000})
  await expect(released.locator('.drawerDecisionActions button')).toHaveCount(0)
})
