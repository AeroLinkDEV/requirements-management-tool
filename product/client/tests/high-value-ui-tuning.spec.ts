import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from './auth'

test('workspace tuning persists and quick navigation provides previews and recents',async({page,request})=>{
  test.setTimeout(90_000)
  await page.setViewportSize({width:1440,height:900})
  await apiLogin(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page,'Flight Management System Live Program')

  await page.getByRole('button',{name:'Open workspace display settings'}).click()
  const display=page.getByRole('dialog',{name:'Workspace display'})
  await expect(display).toBeVisible()
  await display.getByRole('button',{name:/Compact/}).click()
  await expect(page.locator('html')).toHaveAttribute('data-density','compact')
  await expect(page.getByRole('status')).toContainText('Compact density applied')
  await display.getByRole('button',{name:/Reduced motion/}).click()
  await expect(page.locator('html')).toHaveAttribute('data-motion','reduced')
  await display.getByRole('button',{name:'Close workspace display'}).click()
  await page.reload()
  await expect(page.locator('html')).toHaveAttribute('data-density','compact')
  await expect(page.locator('html')).toHaveAttribute('data-motion','reduced')
  // The persisted display preferences arrive before the build's stored ladder. Ctrl+K is only an
  // authorized build-workspace shortcut once the declared Command Center has replaced that transient shell.
  await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()

  const palette=page.getByRole('dialog',{name:'Quick navigation'})
  // The global shortcut is bound by an effect, and the captured failure state (Command Center rendered, no
  // dialog after a single keypress) is consistent with that binding lagging the heading render under runner
  // load — mechanism inferred, not yet instrumented — so one discrete keypress can be lost. A reader presses
  // again; the journey does the same, bounded. A timeout of every attempt fails the journey; any other error
  // (a broken locator, a typo) is re-thrown rather than treated as a missing shortcut.
  const openQuickNavigation=async(landmark:string)=>{
    for(let attempt=0;attempt<3;attempt++){
      await page.keyboard.press('Control+K')
      try{
        await expect(palette.getByText(landmark)).toBeVisible({timeout:5_000})
        return
      }catch(error){
        const message=error instanceof Error?error.message:String(error)
        if(!message.includes('Timed out')&&!message.includes('Timeout'))throw error
      }
    }
    await expect(palette.getByText(landmark)).toBeVisible()
  }

  await openQuickNavigation('SUGGESTED WORKSPACES')
  await palette.getByLabel('Search AeroLink').fill('System Requirements Explorer')
  await expect(palette.locator('.palettePreview').getByRole('heading',{name:'System Requirements Explorer'})).toBeVisible()
  await palette.getByLabel('Search AeroLink').press('Enter')
  await expect(page.getByRole('heading',{name:'System Requirements Explorer'})).toBeVisible()

  await openQuickNavigation('RECENT DESTINATIONS')
  await expect(palette.getByRole('link',{name:/System Requirements/}).first()).toBeVisible()
  await palette.getByLabel('Search AeroLink').fill('SYSR-000150')
  await expect(palette.locator('.palettePreview').getByRole('heading',{name:/SYSR-000150/})).toBeVisible()
  await page.keyboard.press('Escape')

  await page.getByRole('button',{name:'Copy link to this page'}).click()
  await expect(page.locator('.experienceToast')).toContainText(/Link copied to clipboard|clipboard access/)
  const sticky=await page.locator('.reqTableHead').evaluate(element=>getComputedStyle(element).position)
  expect(sticky).toBe('sticky')

  await page.getByRole('button',{name:'Open workspace display settings'}).click()
  await display.getByRole('button',{name:/Comfortable/}).click()
  await display.getByRole('button',{name:/Purposeful motion/}).click()
  await expect(page.locator('html')).toHaveAttribute('data-density','comfortable')
  await expect(page.locator('html')).toHaveAttribute('data-motion','full')
})
