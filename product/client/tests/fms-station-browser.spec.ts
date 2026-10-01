import { expect, test as browserTest } from '@playwright/test'

// Full-browser owner: Chromium's intercepted initial about:blank popups stall images/fonts in the rendered
// tier. Keep that tier's isolation guard intact. These offline fixtures use only this client origin, and the
// built-host owner separately proves shipped styles, real WebGL/workers and inherited CSP in every child.
const test = browserTest.extend<{ stationRequests: void }>({
  stationRequests: [async ({ context, baseURL }, provide) => {
    const origin = new URL(baseURL!).origin
    const unexpected: string[] = []
    context.on('request', request => {
      const url = new URL(request.url())
      if (!['data:', 'blob:'].includes(url.protocol)
        && (url.origin !== origin || /^\/api(?:\/|$)/i.test(url.pathname))) {
        unexpected.push(`${url.origin}${url.pathname}`)
      }
    })
    await provide()
    expect(unexpected, 'station fixture attempted API or external network access').toEqual([])
  }, { auto: true }],
})

test('fixed station presets move one shared cockpit and instructor draft, return on close and remember only intent', async ({ page, context }, testInfo) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/tests/fixtures/fms-station.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
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
  await controls.getByRole('button', { name: 'Restore station — open Out the window', exact: true }).click()
  const outsideTwo = await twoOpened
  await expect(outsideTwo.getByRole('region', { name: 'Out-the-window view' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toBeVisible()
  await expect(page.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  await expect(right.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /STAYS\s*$/)
  await disclosure.click()
  await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
  await expect(ios.getByLabel('Recording name')).toHaveValue('Keep across station windows')
  await page.keyboard.press('Escape')
  await outsideTwo.getByRole('button', { name: 'Return to bench', exact: true }).click()
  await expect.poll(() => outsideTwo.isClosed()).toBe(true)
  await expect(page.getByRole('region', { name: 'Out-the-window view' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toHaveCount(0)

  await disclosure.click()
  await controls.getByLabel('Fixed station arrangement').selectOption('three')
  const threeOutsideOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Restore station — open Out the window', exact: true }).click()
  const outsideThree = await threeOutsideOpened
  await expect(controls.getByRole('button', { name: 'Restore station — open Cockpit', exact: true })).toBeEnabled()
  const cockpitOpened = page.waitForEvent('popup')
  await controls.getByRole('button', { name: 'Restore station — open Cockpit', exact: true }).click()
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
  await controls.getByRole('button', { name: 'Restore station — open Instructor station', exact: true }).click()
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
  await controls.getByRole('button', { name: 'Restore station — open Out the window', exact: true }).click()
  const leavingChild = await leavingOpened
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toBeVisible()
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect.poll(() => leavingChild.isClosed()).toBe(true)
  await page.goto('/tests/fixtures/fms-station.html')
  await expect(page.getByRole('button', { name: 'Engineering view', exact: true })).toBeVisible()
  await disclosure.click()
  await expect(controls.getByLabel('Fixed station arrangement')).toHaveValue('three')
  await expect(controls.getByLabel('Instructor apart')).toBeChecked()
  expect(context.pages()).toHaveLength(1)
  await expect(controls.getByRole('list', { name: 'Live station dock' })).not.toContainText('In another window')
  await expect(controls.getByRole('button', { name: 'Restore station — open Out the window', exact: true })).toBeEnabled()
})

test('a blocked fixed station popup leaves the real panels here and a visible recovery explanation', async ({ page, context }) => {
  await page.addInitScript(() => { window.open = () => null })
  await page.goto('/tests/fixtures/fms-station.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
  await page.locator('.fmsStationDock > summary').click()
  const controls = page.getByRole('region', { name: 'Station arrangement', exact: true })
  await controls.getByLabel('Fixed station arrangement').selectOption('two')
  await controls.getByRole('button', { name: 'Restore station — open Out the window', exact: true }).click()
  await expect(controls.getByRole('status')).toContainText(/blocked/i)
  await expect(page.getByRole('region', { name: 'Out-the-window view' })).toBeVisible()
  await expect(page.locator('.fmsBenchCduStation .fmsCdu')).toHaveCount(2)
  await expect(page.getByRole('region', { name: 'Out the window dock' })).toHaveCount(0)
  expect(context.pages()).toHaveLength(1)
})
