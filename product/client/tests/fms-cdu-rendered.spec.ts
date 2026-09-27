import { expect, renderedTest as test, type Page } from './isolated-client-test'

// The FMS test bench is self-contained: the scripted CMA-9000 runs in the page, so this needs no backend.
// Engine rules are proved in fms-cdu-engine.spec.ts; this proves the rendered panel wires them to real
// pointer, touch-style hold and keyboard input, and that a hardware variation relabels the physical keys.
const open = async (page: Page) => {
  await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
}
const key = (page: Page, id: string) => page.locator(`.fmsCduKey[data-key="${id}"]`)
const screenLines = async (page: Page) => ((await page.locator('.fmsCduScreen').getAttribute('aria-label')) ?? '').split('\n')
const expectLine = async (page: Page, line: number, pattern: RegExp) =>
  expect.poll(async () => (await screenLines(page))[line] ?? '').toMatch(pattern)

test('keys on the rendered panel enter data, make a modification and execute it', async ({ page }) => {
  await open(page)
  await expectLine(page, 0, /^IDENT/)
  await key(page, 'RTE').click()
  await expectLine(page, 0, /^ACT RTE 1/)
  for (const letter of 'CYYZ') await key(page, letter).click()
  await expectLine(page, 13, /^CYYZ/)
  await key(page, 'LSK1R').click()
  await expectLine(page, 0, /^MOD RTE 1/)
  await expect(page.locator('.fmsCduLamp[data-lamp="EXEC_LIGHT"]')).toHaveClass(/\blit\b/)
  await key(page, 'EXEC').click()
  await expectLine(page, 0, /^ACT RTE 1/)
  await expectLine(page, 2, /CYYZ\s*$/)
  await expect(page.locator('.fmsCduLamp[data-lamp="EXEC_LIGHT"]')).not.toHaveClass(/\blit\b/)
  await expect(page.locator('.fmsBenchLog li').first()).toContainText('EXEC')
})

test('a held key shows the pressed render, and CLR held for a second clears the whole scratchpad', async ({ page }) => {
  await open(page)
  for (const letter of 'ABC') await key(page, letter).click()
  const clr = key(page, 'CLR')
  const box = (await clr.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down()
  await expect(clr).toHaveClass(/\bpressed\b/)
  expect(await clr.evaluate(element => getComputedStyle(element).backgroundImage)).toContain('pressed.webp')
  await expectLine(page, 13, /^\s*$/)
  await page.mouse.up()
  await expect(clr).not.toHaveClass(/\bpressed\b/)
  await expectLine(page, 13, /^\s*$/)

  for (const letter of 'ABC') await key(page, letter).click()
  await clr.click()
  await expectLine(page, 13, /^AB\s*$/)
})

test('choosing a hardware variation relabels the same physical keys and annunciators', async ({ page }) => {
  await open(page)
  await expect(key(page, 'F2_2')).toHaveAttribute('aria-label', 'FUEL')
  await expect(page.locator('.fmsCduLamp').nth(5)).toHaveText('GSM')
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('030/430')
  await expect(key(page, 'F2_2')).toHaveAttribute('aria-label', 'TPDR')
  await expect(page.locator('.fmsCduLamp').nth(5)).toHaveText('TX1')
  await key(page, 'F2_2').click()
  await expectLine(page, 0, /^RADIO\s+2\/2$/)
})

test('the physical keyboard drives the focused panel', async ({ page }) => {
  await open(page)
  await page.locator('.fmsCdu').focus()
  await page.keyboard.type('cyyz')
  await expectLine(page, 13, /^CYYZ/)
  await page.keyboard.press('Backspace')
  await expectLine(page, 13, /^CYY\s*$/)
  await page.keyboard.press('Backspace')
  await page.keyboard.press('Backspace')
  await page.keyboard.press('Backspace')
  await key(page, 'RTE').click()
  await page.keyboard.press('F1')
  await expectLine(page, 13, /^CYOW/)
})

test('an alert raised from the bench lights MSG until CLR on the panel acknowledges it', async ({ page }) => {
  await open(page)
  const msg = page.locator('.fmsCduLamp[data-lamp="MSG"]')
  await expect(msg).not.toHaveClass(/\blit\b/)
  await page.getByLabel('Alert message to raise').fill('unable rnp')
  await page.getByRole('button', { name: 'Raise alert' }).click()
  await expect(msg).toHaveClass(/\blit\b/)
  await expectLine(page, 13, /^UNABLE RNP/)
  await key(page, 'CLR').click()
  await expect(msg).not.toHaveClass(/\blit\b/)
})

test('every physical key on the rendered panel can be clicked and reaches the simulation', async ({ page }) => {
  await open(page)
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('050')
  const keys = page.locator('.fmsCduKey')
  const count = await keys.count()
  expect(count).toBe(68)
  for (let i = 0; i < count; i += 1) await keys.nth(i).click()
  await expect(page.locator('.fmsBenchLog h2 small')).toHaveText(String(count))
})

test('conditions from the bench light the panel annunciators, and FMS failure blanks the display', async ({ page }) => {
  await open(page)
  const lamp = (code: string) => page.locator(`.fmsCduLamp[data-lamp="${code}"]`)
  await page.getByLabel('GPS lost (dead reckoning)').check()
  await expect(lamp('POS')).toHaveClass(/\blit\b/)
  await expectLine(page, 13, /^GPS NAV LOST/)
  await page.getByLabel('Subsystem request').check()
  await expect(lamp('MENU_LIGHT')).toHaveClass(/\blit\b/)

  await page.getByLabel('FMS failure').check()
  await expect(lamp('FAIL')).toHaveClass(/\blit\b/)
  await expect(lamp('POS')).not.toHaveClass(/\blit\b/)
  await expect.poll(async () => (await screenLines(page)).join('').trim()).toBe('')
  await key(page, 'RTE').click()
  await expect.poll(async () => (await screenLines(page)).join('').trim()).toBe('')
  await page.getByLabel('FMS failure').uncheck()
  await expectLine(page, 0, /^IDENT/)
})

test('a library alert and a sequenced waypoint reach the panel', async ({ page }) => {
  await open(page)
  await page.getByRole('combobox', { name: 'Alert from the manual' }).selectOption('TIMER ALARM')
  await page.getByRole('button', { name: 'Raise', exact: true }).click()
  await expectLine(page, 13, /^TIMER ALARM/)
  await expect(page.locator('.fmsCduLamp[data-lamp="MSG"]')).toHaveClass(/\blit\b/)
  await expect(page.locator('.fmsBench')).toContainText('Active waypoint MUN')
  await page.getByRole('button', { name: 'Sequence to next waypoint' }).click()
  await expect(page.locator('.fmsBench')).toContainText('Active waypoint RDG')
  await key(page, 'LEGS').click()
  await expectLine(page, 2, /^RDG/)
})

test('NVG lighting backlights the legends green and holds the display in the NVG range; ambient light moves it', async ({ page }) => {
  await open(page)
  const panel = page.locator('.fmsCdu')
  const luminance = async () => Number(await panel.getAttribute('data-luminance'))
  const day = await luminance()
  await page.getByText('NVG', { exact: true }).click()
  await expect(panel).toHaveClass(/\bmode-nvg\b/)
  expect(await luminance()).toBeLessThanOrEqual(3)
  const legend = await key(page, 'A').evaluate(element => getComputedStyle(element).color)
  expect(legend).toBe('rgb(140, 245, 106)')
  const dim = await luminance()
  await page.getByRole('slider', { name: 'Ambient light' }).fill('100')
  await expect.poll(luminance).toBeGreaterThan(dim)
  expect(await luminance()).toBeLessThanOrEqual(3)
  await page.getByText('Day', { exact: true }).click()
  await expect.poll(luminance).toBe(day)
})
