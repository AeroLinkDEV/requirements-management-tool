import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { apiBase, apiLogin, login, openNewSoftwareChangeRequest, openNewSystemChangeRequest, openNavigationGroup, selectProgram as enterProgram } from './auth'

async function createWorkspace(request:APIRequestContext,prefix:string){
  await apiLogin(request)
  const suffix=Date.now().toString().slice(-7),programName=`${prefix} ${suffix}`
  const response=await request.post(`${apiBase}/api/workspaces`,{data:{programName,programCode:`CA${suffix}`,projectName:'Controlled Product',softwareProduct:'Controlled Product Software',initialRelease:'1.0',initialReleaseIsReleased:false}})
  expect(response.ok(),await response.text()).toBeTruthy()
  return programName
}

async function selectProgram(page:Page,programName:string){
  await login(page, 'admin', { openProject: false })
  await enterProgram(page,programName)
}

test('System proposal identity and stages are controlled from the first screen', async ({page,request})=>{
  const programName=await createWorkspace(request,'System Authoring')
  await selectProgram(page,programName)
  await openNavigationGroup(page,'SYSTEMS ENGINEERING')
  await openNewSystemChangeRequest(page)

  const stages=page.getByRole('navigation',{name:'Change authoring progress'})
  await expect(stages).toBeVisible()
  await expect(stages.getByText('Change case',{exact:true})).toBeVisible()
  await expect(stages.getByText('Requirement changes',{exact:true})).toBeVisible()
  await expect(stages.getByText('Impact & readiness',{exact:true})).toHaveCount(0)

  // Nothing is assumed about what this change does. The editor used to pre-seed an Introduce proposal, which
  // decided that before the author had said — and because it arrived with an identifier already allocated it
  // counted as identity-locked, so the first proposal on every new change request could not be changed either.
  await expect(page.getByText('Choose the first requirement change')).toBeVisible()
  await expect(page.getByLabel('Identifier')).toHaveCount(0)
  await page.getByRole('button',{name:'+ Introduce System requirement'}).click()

  await expect(page.getByLabel('Identifier')).toHaveValue('Provisional — assigned at check-in')
  await expect(page.getByRole('textbox',{name:'Level',exact:true})).toHaveValue('System')
  await expect(page.getByLabel('Change type')).toHaveValue('Introduce')
  await expect(page.getByLabel('Identifier')).not.toBeEditable()
  await expect(page.getByLabel('Revision')).not.toBeEditable()
  await expect(page.getByRole('textbox',{name:'Level',exact:true})).not.toBeEditable()

  // Change type is the one thing in that row the author decides, so it is the one thing that is editable.
  // Switching to Modify re-issues the identity, because a modification names a requirement that already
  // exists rather than being allocated a fresh number — which is what makes the repository lookup appear.
  await page.getByLabel('Change type').selectOption('Modify')
  await expect(page.getByLabel('Identifier')).toHaveValue('Awaiting controlled selection')
  await expect(page.getByText('Select the requirement to modify')).toBeVisible()
  await page.getByLabel('Change type').selectOption('Introduce')
  await expect(page.getByLabel('Identifier')).toHaveValue('Provisional — assigned at check-in')
})

test('Software Draft keeps downstream impact with consuming engineers before an explicitly selected reviewer signs',async ({page,request})=>{
  test.setTimeout(120_000)
  const programName=await createWorkspace(request,'Software Authoring')
  await selectProgram(page,programName)
  await openNewSoftwareChangeRequest(page,'HLR')

  await page.getByRole('button',{name:'+ Introduce HLR'}).click()
  await expect(page.getByLabel('Identifier')).toHaveValue('Provisional — assigned at check-in')
  await expect(page.getByRole('textbox',{name:'Level',exact:true})).toHaveValue('Software HLR')
  await expect(page.getByText('SWR-000001')).toHaveCount(0)
  await page.getByLabel('Title').fill('Control software authoring readiness')
  await page.getByLabel('Problem').fill('The authoring handoff needs explicit readiness gates.')
  await page.getByLabel('Analysis',{exact:true}).fill('Identity, content, impacts, and review authority must remain attributable.')
  await page.getByLabel('Solution').fill('Use one staged controlled proposal experience.')
  await page.getByLabel('Requirement statement').fill('The software shall require explicit review readiness decisions.')
  // A new requirement must be given a place in the document before it can be sent for review.
  await page.getByLabel('Section for proposal 1').selectOption({ index: 1 })
  await page.getByRole('textbox',{name:'Author',exact:true}).fill('software.author')
  await page.getByRole('button',{name:'Save SWCR Draft'}).click()

  await expect(page.getByRole('heading',{name:'Control software authoring readiness'})).toBeVisible()
  await expect(page.getByRole('button',{name:'Configure & Submit Review'})).toBeVisible()
  await page.getByRole('button',{name:'Check out & edit'}).click()
  await expect(page.getByRole('textbox',{name:'Author',exact:true})).toHaveValue('software.author')
  await expect(page.getByText('Known downstream context',{exact:true})).toBeVisible()
  await expect(page.locator('.editorColumns aside select')).toHaveCount(0)
  const checkIn=page.getByRole('button',{name:'Save & check in'})
  await expect(checkIn).toBeEnabled({timeout:30_000})
  await checkIn.click()

  await page.getByRole('button',{name:'Configure & Submit Review'}).click()
  await expect(page.getByText('No reviewers selected')).toBeVisible()
  await expect(page.getByRole('button',{name:'Submit for Review'})).toBeDisabled()
  await page.getByRole('button',{name:'+ Add approver'}).click()
  await page.getByLabel('Approver 1 search').fill('AeroLink Administrator')
  await page.getByRole('button',{name:/AeroLink Administrator.*Administrator/}).click()
  await page.getByRole('button',{name:'Submit for Review'}).click()
  // The lifecycle state is spelled for a reader; the raw enum stays available to tooling as data-state.
  await expect(page.getByText('In review',{exact:true}).first()).toBeVisible()
  await expect(page.locator('[data-state="InReview"]').first()).toBeVisible()
  await page.getByRole('button',{name:'Review & electronically approve'}).click()
  await page.getByLabel('Re-enter your password').fill('AeroLink!2026')
  await page.getByRole('button',{name:'Sign & approve'}).click()
  await expect(page.getByText('Approved',{exact:true}).first()).toBeVisible()
})
