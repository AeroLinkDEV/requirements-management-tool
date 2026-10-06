import { expect, test, type Locator } from '@playwright/test'
import { apiLogin, login, selectProgram } from '../auth'

// The fixture bench has no sticky application header. This owner proves native focus scrolling in
// the shipped shell; being focused alone does not prove that a station action is visible.
test('station keyboard focus clears the normal shell header after page scrolling', async ({ page, request }, info) => {
  test.setTimeout(60_000)
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')
  const path = await page.locator('nav[aria-label="Primary navigation"] a[href$="/fms-test-bench"]').first().getAttribute('href')
  expect(path).toBeTruthy()
  await page.goto(path!)
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.getByRole('button', { name: 'Open workspace display settings', exact: true }).click()
  const settings = page.getByRole('dialog', { name: 'Workspace display', exact: true })
  await settings.getByRole('group', { name: 'Information density' }).getByRole('button', { name: /Compact/ }).click()
  await settings.getByRole('button', { name: 'Close workspace display', exact: true }).click()
  await page.setViewportSize({ width: 761, height: 900 })
  const summary = page.locator('.fmsStationDock > summary')
  const panel = page.getByRole('region', { name: 'Station arrangement', exact: true })
  const checkbox = panel.getByLabel('Instructor apart')
  const open = panel.getByRole('button', { name: 'Open outside view in a window', exact: true })
  const clear = async (target: Locator) => {
    await expect(target).toBeFocused()
    await expect.poll(() => target.evaluate(element => {
      const box = element.getBoundingClientRect()
      const header = document.querySelector('.contextBar')!.getBoundingClientRect()
      const css = getComputedStyle(element)
      const ring = Math.max(0, parseFloat(css.outlineWidth) + parseFloat(css.outlineOffset))
      const hits = [box.top + 1, box.bottom - 1].every(y => {
        const hit = document.elementFromPoint(box.left + box.width / 2, y)
        return !!hit && (hit === element || element.contains(hit))
      })
      return box.top - ring >= Math.max(0, header.bottom) && box.bottom + ring <= innerHeight && hits
    }), { message: 'keyboard-focused station control and its focus ring clear the real sticky header' }).toBe(true)
  }
  await summary.focus()
  await page.keyboard.press('Enter')
  await panel.getByLabel('Fixed station arrangement').selectOption('two')
  await summary.focus()
  await page.keyboard.press('Tab')
  await page.keyboard.press('Tab')
  await page.keyboard.press('Tab')
  await clear(open)

  // Ordinary wheel scrolling can leave the previous target inside the viewport but beneath the header.
  // Browser focus must account for that covered viewport strip when navigating back from Open.
  const before = await checkbox.boundingBox()
  const header = await page.locator('.contextBar').boundingBox()
  const scroll = await page.evaluate(() => scrollY)
  const delta = before!.y - header!.height / 2
  await page.mouse.move(740, 850)
  await page.mouse.wheel(0, delta)
  await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(scroll + delta - 10)
  await page.keyboard.press('Shift+Tab')
  await clear(checkbox)
  await expect(panel.locator('.fmsStationOwnerNotice')).toBeVisible()
  await page.screenshot({ path: info.outputPath('station-keyboard-normal-761.png'), fullPage: false })
})
