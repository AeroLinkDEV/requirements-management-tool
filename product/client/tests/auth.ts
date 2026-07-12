import { expect } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'

export async function login(page:Page,userName='admin'){
  await page.goto('/')
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill('AeroLink!2026')
  await page.getByRole('button',{name:/Sign in securely/}).click()
  await expect(page.getByRole('heading',{name:/Create your first program|Command Center/})).toBeVisible()
}
export async function apiLogin(request:APIRequestContext,userName='admin'){
  const response=await request.post('http://127.0.0.1:5080/api/auth/login',{data:{userName,password:'AeroLink!2026'}})
  expect(response.ok(),await response.text()).toBeTruthy()
}
