import {expect,test} from '@playwright/test'
import {login,openNavigationGroup,selectProgram} from './auth'

test('enterprise control exposes governed product-line reuse and evidence-based operations assurance',async({page})=>{
 await login(page);await selectProgram(page,'Flight Management System Live Program');await openNavigationGroup(page,'ADMINISTRATION');await page.getByRole('link',{name:/Enterprise Control/}).click();
 await page.getByRole('button',{name:'Product line'}).click();await expect(page.getByRole('heading',{name:'Configuration intelligence'})).toBeVisible();await expect(page.getByText('Guidance computation core')).toBeVisible();await expect(page.getByText('Released FMS 1.5 configuration')).toBeVisible();await expect(page.getByText('FMS 1.6 accepts the round-robin guidance capability')).not.toBeVisible();await expect(page.getByText('CCS-00001 · Propagate round-robin guidance')).toBeVisible();
 await page.getByRole('button',{name:'Assurance'}).click();await expect(page.getByRole('heading',{name:'Production assurance'})).toBeVisible();await expect(page.getByText('NOT QUALIFIED')).toBeVisible();await expect(page.getByText('Production workload is not yet qualified')).toBeVisible();await expect(page.getByText(/permission scoped/).first()).toBeVisible();
})
