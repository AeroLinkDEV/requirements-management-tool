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
