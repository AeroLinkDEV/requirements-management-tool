import { expect, test } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
import { apiBase, apiLogin, login, openNavigationGroup } from './auth'

async function createWorkspace(request:APIRequestContext,prefix:string){
  await apiLogin(request)
  const suffix=Date.now().toString().slice(-7),programName=`${prefix} ${suffix}`
  const response=await request.post(`${apiBase}/api/workspaces`,{data:{programName,programCode:`CA${suffix}`,projectName:'Controlled Product',softwareProduct:'Controlled Product Software',initialRelease:'1.0',initialReleaseIsReleased:false}})
  expect(response.ok(),await response.text()).toBeTruthy()
  return programName
}

async function selectProgram(page:Page,programName:string){
  await login(page)
  await page.locator('.program > select:not(.releaseSelector)').selectOption({label:programName})
}

async function openPageFromPalette(page:Page,label:string){
  await page.getByRole('button',{name:/Search & navigate/}).click()
  const palette=page.getByRole('dialog',{name:'Quick navigation'})
  await palette.getByPlaceholder(/Search pages/).fill(label)
  await palette.getByRole('link',{name:new RegExp(label)}).click()
}

test('System proposal identity and stages are controlled from the first screen', async ({page,request})=>{
  const programName=await createWorkspace(request,'System Authoring')
  await selectProgram(page,programName)
  await openNavigationGroup(page,'SYSTEMS ENGINEERING')
  await page.getByRole('link',{name:'New System SCR'}).click()

  const stages=page.getByRole('navigation',{name:'Change authoring progress'})
  await expect(stages).toBeVisible()
  await expect(stages.getByText('Change case',{exact:true})).toBeVisible()
  await expect(stages.getByText('Requirement changes',{exact:true})).toBeVisible()
  await expect(stages.getByText('Impact & readiness',{exact:true})).toBeVisible()
  await expect(page.getByLabel('Identifier')).toHaveValue(/^SYSR-\d{6}$/)
  await expect(page.getByRole('textbox',{name:'Level',exact:true})).toHaveValue('System')
  await expect(page.getByLabel('Change type')).toHaveValue('Introduce')
  await expect(page.getByLabel('Identifier')).not.toBeEditable()
  await expect(page.getByLabel('Revision')).not.toBeEditable()
  await expect(page.getByRole('textbox',{name:'Level',exact:true})).not.toBeEditable()
  await expect(page.getByLabel('Change type')).not.toBeEditable()
})

test('Software Draft closes impacts before an explicitly selected reviewer signs',async ({page,request})=>{
  const programName=await createWorkspace(request,'Software Authoring')
  await selectProgram(page,programName)
  await openPageFromPalette(page,'New Software SWCR')

  await expect(page.getByLabel('Identifier')).toHaveValue(/^HLR-\d{6}$/)
  await expect(page.getByRole('textbox',{name:'Level',exact:true})).toHaveValue('Software HLR')
  await expect(page.getByText('SWR-000001')).toHaveCount(0)
  await page.getByLabel('Title').fill('Control software authoring readiness')
  await page.getByLabel('Problem').fill('The authoring handoff needs explicit readiness gates.')
  await page.getByLabel('Analysis',{exact:true}).fill('Identity, content, impacts, and review authority must remain attributable.')
  await page.getByLabel('Solution').fill('Use one staged controlled proposal experience.')
  await page.getByLabel('Requirement statement').fill('The software shall require explicit review readiness decisions.')
  await page.getByRole('button',{name:'Save SWCR Draft'}).click()

  await expect(page.getByRole('heading',{name:'Control software authoring readiness'})).toBeVisible()
  await expect(page.getByRole('button',{name:'Configure & Submit Review'})).toHaveCount(0)
  await page.getByRole('button',{name:'Check out & edit'}).click()
  const impacts=page.locator('.editorColumns aside select')
  await expect(impacts).toHaveCount(5)
  for(let index=0;index<5;index++)await impacts.nth(index).selectOption('Not Affected')
  await page.getByRole('button',{name:'Save & check in'}).click()

  await page.getByRole('button',{name:'Configure & Submit Review'}).click()
  await expect(page.getByText('No reviewers selected')).toBeVisible()
  await expect(page.getByRole('button',{name:'Submit for Review'})).toBeDisabled()
  await page.getByRole('button',{name:'+ Add approver'}).click()
  await page.getByLabel('Approver 1 search').fill('AeroLink Administrator')
  await page.getByRole('button',{name:/AeroLink Administrator.*Administrator/}).click()
  await page.getByRole('button',{name:'Submit for Review'}).click()
  await expect(page.getByText('InReview',{exact:true}).first()).toBeVisible()
  await page.getByRole('button',{name:'Review & electronically approve'}).click()
  await page.getByLabel('Re-enter your password').fill('AeroLink!2026')
  await page.getByRole('button',{name:'Sign & approve'}).click()
  await expect(page.getByText('Approved',{exact:true}).first()).toBeVisible()
})
