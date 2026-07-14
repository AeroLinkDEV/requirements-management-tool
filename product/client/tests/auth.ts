import { expect } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'
export const apiBase=process.env.AEROLINK_E2E_API_BASE??'http://127.0.0.1:5082'

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
