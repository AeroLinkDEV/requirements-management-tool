import { expect, renderedTest as test } from './isolated-client-test'

// Offline station lifecycle owner under preventive client isolation. The built-host owner separately
// proves shipped styles, real WebGL/workers and inherited CSP in every child.

test('fixed station presets move one shared cockpit and instructor draft, return on close and remember only intent', async ({ page, context }, testInfo) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/tests/fixtures/fms-station.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await expect(page.getByText('For separate browser windows, switch to Cockpit view.', { exact: true })).toBeVisible()
  // Browser extensions can add their own stylesheet metadata to the app document. This disabled
  // synthetic link exercises that presence without installing an extension or making external requests.
  await page.evaluate(() => {
    const extension = document.createElement('link')
    extension.rel = 'stylesheet'
    extension.disabled = true
    extension.type = 'application/x-extension-fixture'
    extension.href = 'chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/content.css'
    document.head.appendChild(extension)
  })
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await expect(page.locator('.fmsStationDock > summary')).toHaveText('Station windows')
  await expect(page.locator('.fmsStationDock > summary')).toBeVisible()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  const ios = page.getByRole('region', { name: 'Instructor station', exact: true })
  await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  await ios.getByRole('button', { name: 'Record', exact: true }).click()
  await ios.getByLabel('Recording name').fill('Keep across station windows')
  await page.keyboard.press('Escape')
  const right = page.locator('.fmsBenchCduStation[data-side="2"]')
  await right.locator('.fmsCdu').focus()
  await page.keyboard.type('stays')
  const disclosure = page.locator('.fmsStationDock > summary')
  await disclosure.click()
  const controls = page.getByRole('region', { name: 'Station arrangement', exact: true })
  await controls.getByLabel('Fixed station arrangement').selectOption('two')
  const twoOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
  const outsideTwo = await twoOpened
  await expect(outsideTwo.getByRole('region', { name: 'Out-the-window view' })).toBeVisible()
  await expect(outsideTwo.locator('head link[href^="chrome-extension:"]')).toHaveCount(0)
  await page.evaluate(() => {
    const extension = document.createElement('link')
    extension.rel = 'stylesheet'
    extension.disabled = true
    extension.type = 'application/x-extension-fixture'
    extension.href = 'moz-extension://extension-fixture/content.css'
    const authored = document.createElement('style')
    authored.textContent = '.fmsStationWindowHeader { --station-extension-control: 17px; }'
    document.head.append(extension, authored)
  })
  await expect.poll(() => outsideTwo.locator('.fmsStationWindowHeader').evaluate(header =>
    getComputedStyle(header).getPropertyValue('--station-extension-control').trim())).toBe('17px')
  await expect(outsideTwo.locator('head link[href^="moz-extension:"]')).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toBeVisible()
  await expect(page.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  await expect(right.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYS\s*$/)
  await disclosure.click()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await expect(ios.getByLabel('Recording name')).toHaveValue('Keep across station windows')
  await page.keyboard.press('Escape')
  // The modal makes its child header inert; the owner's live dock can still return this surface.
  await outsideTwo.getByRole('button', { name: 'View status and sources', exact: true }).click()
  await expect(outsideTwo.getByRole('dialog', { name: 'View status and sources', exact: true })).toBeVisible()
  await page.getByRole('region', { name: 'Out the window dock', exact: true }).getByRole('button', { name: 'Return Out the window', exact: true }).click()
  await expect.poll(() => outsideTwo.isClosed()).toBe(true)
  await expect(page.getByRole('region', { name: 'Out-the-window view' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toHaveCount(0)
  await expect(page.getByRole('dialog', { name: 'View status and sources', exact: true })).not.toBeVisible()
  await expect(page.getByRole('button', { name: 'View status and sources', exact: true })).not.toBeFocused()

  await disclosure.click()
  // Ignoring extension metadata must not admit an ordinary foreign application stylesheet.
  await page.evaluate(() => {
    const foreign = document.createElement('link')
    foreign.rel = 'stylesheet'
    foreign.disabled = true
    foreign.type = 'application/x-extension-fixture'
    foreign.href = 'https://example.invalid/station.css'
    foreign.dataset.stationForeignFixture = ''
    document.head.appendChild(foreign)
  })
  await controls.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
  await expect(controls.getByRole('status')).toContainText('The station stylesheet must come from this AeroLink installation.')
  await expect(page.getByRole('region', { name: 'Out-the-window view' })).toBeVisible()
  await expect(page.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  expect(context.pages()).toHaveLength(1)
  await page.locator('link[data-station-foreign-fixture]').evaluate(link => link.remove())
  await controls.getByLabel('Fixed station arrangement').selectOption('three')
  const threeOutsideOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
  const outsideThree = await threeOutsideOpened
  await expect(controls.getByRole('button', { name: 'Open cockpit in a window', exact: true })).toBeEnabled()
  const cockpitOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open cockpit in a window', exact: true }).click()
  const cockpit = await cockpitOpened
  await cockpit.setViewportSize({ width: 1440, height: 900 })
  await expect(cockpit.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  await expect(page.locator('.fmsCdu')).toHaveCount(0)
  const childRight = cockpit.locator('.fmsBenchCduStation[data-side="2"]')
  await expect(childRight.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYS\s*$/)
  await childRight.locator('.fmsCdu').focus()
  await cockpit.keyboard.type('q')
  await expect(childRight.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYSQ\s*$/)
  await expect(cockpit.getByLabel('FMS guidance source')).toHaveValue('1')
  await expect(ios.getByLabel('Recording name')).toHaveValue('Keep across station windows')
  await cockpit.keyboard.press('Escape')
  await expect(ios).toBeVisible()
  expect((await cockpit.locator('.efisPfd').boundingBox())!.width).toBeGreaterThanOrEqual(280)
  expect((await childRight.locator('.fmsCdu').boundingBox())!.width).toBeGreaterThanOrEqual(320)
  try {
    await expect.poll(() => childRight.locator('.fmsCduImage').evaluate(image => (image as HTMLImageElement).naturalWidth),
      { message: 'the physical faceplate image loads in the destination document' }).toBeGreaterThan(0)
  } finally {
    await testInfo.attach('station-faceplate-state', { contentType: 'application/json', body: JSON.stringify(await cockpit.locator('.fmsCduImage').evaluateAll(images => images.map(node => {
      const image = node as HTMLImageElement
      return { src: image.src, currentSrc: image.currentSrc, complete: image.complete, naturalWidth: image.naturalWidth,
        connected: image.isConnected, documentMatches: image.ownerDocument === document, readyState: document.readyState,
        visibility: document.visibilityState, focused: document.hasFocus(), fonts: document.fonts.status,
        baseURI: image.ownerDocument.baseURI, location: image.ownerDocument.location.href }
    }))) })
  }
  await cockpit.bringToFront()
  await cockpit.screenshot({ path: testInfo.outputPath('station-cockpit-child.png'), fullPage: true })

  await controls.getByLabel('Instructor apart').check()
  await expect(cockpit.locator('.fmsCdu')).toHaveCount(2)
  const instructorOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open instructor station in a window', exact: true }).click()
  const instructor = await instructorOpened
  const childIos = instructor.getByRole('region', { name: 'Instructor station', exact: true })
  await expect(childIos.getByLabel('Recording name')).toHaveValue('Keep across station windows')
  await childIos.getByLabel('Recording name').fill('Changed in instructor window')
  await expect(childRight.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYSQ\s*$/)
  await instructor.bringToFront()
  await instructor.screenshot({ path: testInfo.outputPath('station-instructor-child.png'), fullPage: true })
  await instructor.keyboard.press('Escape')
  await expect.poll(() => instructor.isClosed()).toBe(true)
  await expect(ios.getByLabel('Recording name')).toHaveValue('Changed in instructor window')
  await expect(page.getByRole('button', { name: 'Instructor station', exact: true })).toBeFocused()

  // A pending CLR hold belongs to its current document and must not fire after that cockpit is returned.
  await childRight.locator('.fmsCdu').focus()
  await cockpit.keyboard.down('Backspace')
  await cockpit.close()
  await page.keyboard.up('Backspace')
  await expect.poll(() => cockpit.isClosed()).toBe(true)
  await expect(right.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYSQ\s*$/)
  await page.waitForTimeout(1100)
  await expect(right.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYSQ\s*$/)
  await page.getByRole('button', { name: 'Engineering view', exact: true }).click()
  await expect.poll(() => outsideThree.isClosed()).toBe(true)
  await expect(page.locator('.fmsCdu')).toHaveCount(1)
  await expect(page.getByLabel('Recording name')).toHaveValue('Changed in instructor window')
  await expect(page.getByRole('button', { name: 'Stop recording', exact: true })).toBeEnabled()

  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await disclosure.click()
  const leavingOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
  const leavingChild = await leavingOpened
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toBeVisible()
  await leavingChild.getByRole('button', { name: 'View status and sources', exact: true }).click()
  await expect(leavingChild.getByRole('dialog', { name: 'View status and sources', exact: true })).toBeVisible()
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect.poll(() => leavingChild.isClosed()).toBe(true)
  await page.goto('/tests/fixtures/fms-station.html')
  await expect(page.getByRole('button', { name: 'Engineering view', exact: true })).toBeVisible()
  await disclosure.click()
  await expect(controls.getByLabel('Fixed station arrangement')).toHaveValue('three')
  await expect(controls.getByLabel('Instructor apart')).toBeChecked()
  expect(context.pages()).toHaveLength(1)
  await expect(controls.getByRole('list', { name: 'Live station dock' })).not.toContainText('In another window')
  await expect(controls.getByRole('button', { name: 'Open outside view in a window', exact: true })).toBeEnabled()
})

test('a blocked fixed station popup leaves the real panels here and a visible recovery explanation', async ({ page, context }) => {
  await page.addInitScript(() => { window.open = () => null })
  await page.goto('/tests/fixtures/fms-station.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await page.locator('.fmsStationDock > summary').click()
  const controls = page.getByRole('region', { name: 'Station arrangement', exact: true })
  await controls.getByLabel('Fixed station arrangement').selectOption('two')
  await controls.getByRole('button', { name: 'Open outside view in a window', exact: true }).click()
  await expect(controls.getByRole('status')).toContainText(/blocked/i)
  await expect(page.getByRole('region', { name: 'Out-the-window view' })).toBeVisible()
  await expect(page.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toHaveCount(0)
  expect(context.pages()).toHaveLength(1)
})
