import { expect } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
export const apiBase=process.env.AEROLINK_E2E_API_BASE??'http://127.0.0.1:5082'

export type ShowcaseSeed = {
  programId: string
  projectId: string
  activeReleaseId: string
  releasedBaselineId: string
}

let cachedShowcase: ShowcaseSeed | undefined

export async function login(page:Page,userName='admin',options:{openProject?:boolean}={}){
  await page.goto('/')
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill('AeroLink!2026')
  await page.getByRole('button',{name:/Sign in securely/}).click()
  await expect(page.getByRole('heading',{name:/Create your first program|Projects/})).toBeVisible()
  if(options.openProject!==false&&await page.getByRole('heading',{name:'Projects'}).count()){
    await page.getByRole('link',{name:'Open FMS Product Development'}).click()
    await expect(page.getByRole('heading',{name:'Software Builds'})).toBeVisible()
    await page.getByRole('button',{name:'Open build 1.6'}).click()
    await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()
  }
}
export async function apiLogin(request:APIRequestContext,userName='admin'){
  const response=await request.post(`${apiBase}/api/auth/login`,{data:{userName,password:'AeroLink!2026'}})
  expect(response.ok(),await response.text()).toBeTruthy()
}
export async function showcaseSeed(request:APIRequestContext){
  if(cachedShowcase)return cachedShowcase
  const prepared=process.env.AEROLINK_SHOWCASE_SEED
  if(prepared){cachedShowcase=JSON.parse(prepared) as ShowcaseSeed;return cachedShowcase}
  await apiLogin(request)
  const response=await request.post(`${apiBase}/api/showcase/seed`,{timeout:120_000})
  const body=await response.text()
  expect(response.ok(),body).toBeTruthy()
  cachedShowcase=JSON.parse(body) as ShowcaseSeed
  return cachedShowcase
}
export async function selectProgram(page:Page,label:string){
  const response=await page.request.get(`${apiBase}/api/workspaces`)
  const body=await response.text()
  expect(response.ok(),body).toBeTruthy()
  const workspaces=JSON.parse(body) as {
    program:{id:string;name:string};
    projects:{project:{id:string};releases:{id:string;isReleased:boolean}[]}[]
  }[]
  const workspace=workspaces.find(item=>item.program.name===label)
  expect(workspace,`Program context "${label}"`).toBeTruthy()
  const project=workspace!.projects[0]
  const release=project?.releases.find(item=>!item.isReleased)??project?.releases[0]
  expect(project&&release,`Build workspace for "${label}"`).toBeTruthy()
  await page.goto(`/programs/${workspace!.program.id}/projects/${project.project.id}/releases/${release!.id}/command-center`)
  await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()
}
export async function openNavigationGroup(page:Page,name:string){
  const currentName:{[key:string]:string}={
    'SYSTEMS ENGINEERING':'ENGINEERING',
    'SOFTWARE ENGINEERING':'ENGINEERING',
    'VERIFICATION':'ASSURANCE',
    'RELEASE & CONFIGURATION':'RELEASE',
  }
  const group=page.locator('.navGroup').filter({has:page.locator('summary').filter({hasText:currentName[name]??name})})
  if(await group.getAttribute('open')===null)await group.locator('summary').click()
  const engineeringScope=name==='SOFTWARE ENGINEERING'?'Software':name==='SYSTEMS ENGINEERING'?'System':''
  if(engineeringScope){
    const scopeButton=group.getByRole('group',{name:'Engineering scope'}).getByRole('button',{name:engineeringScope})
    if(await scopeButton.getAttribute('aria-pressed')!=='true')await scopeButton.click()
  }
}

export async function openNewSystemChangeRequest(page:Page){
  await openNavigationGroup(page,'SYSTEMS ENGINEERING')
  await page.getByRole('link',{name:'System Change Requests'}).click()
  await page.getByRole('button',{name:'+ New System Change Request'}).click()
}

export async function openNewSoftwareChangeRequest(page:Page,level:'HLR'|'LLR'='HLR'){
  await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  await page.getByRole('link',{name:'Software Change Requests'}).click()
  await page.getByRole('button',{name:'+ New Software Change Request'}).click()
  await page.getByRole('button',{name:new RegExp(`^${level} change request`)}).click()
}
