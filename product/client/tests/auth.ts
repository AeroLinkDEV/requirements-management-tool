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

export async function login(page:Page,userName='admin'){
  await page.goto('/')
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill('AeroLink!2026')
  await page.getByRole('button',{name:/Sign in securely/}).click()
  await expect(page.getByRole('heading',{name:/Create your first program|Command Center/})).toBeVisible()
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
  const selector=page.locator('.program > select:not(.releaseSelector)')
  const activeProgram=page.locator('.activeProgram')
  await expect.poll(async()=>{
    if(await selector.count())return (await selector.locator('option').allTextContents()).includes(label)
    return (await activeProgram.textContent())?.trim()===label
  },{message:`Wait for Program context "${label}"`,timeout:15_000}).toBe(true)
  if(await selector.count())await selector.selectOption({label})
  else await expect(activeProgram).toHaveText(label)
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
