import { expect, renderedTest as test } from './isolated-client-test'

// #1545 primary owner: HDG removes the engineering flown-path overlay, not the managed route/ND.
// #1504 separately owns computer vantage wiring; its HDG check covers only the other marker.
for (const side of [1, 2] as const) for (const independent of [false, true]) {
  test(`FMS ${side} ${independent ? 'INDEPENDENT' : 'SYNC'}: HDG withdraws its flown leg while keeping the active route`, async ({ page }, testInfo) => {
    await page.clock.setFixedTime(new Date('2026-09-27T14:00:00Z'))
    await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
    await page.goto('/tests/fixtures/fms-cdu.html')
    await expect(page.locator('.fmsCdu')).toBeVisible()
    if (independent) {
      await page.getByRole('tab', { name: 'Dual FMS and radios', exact: true }).click()
      await page.getByRole('region', { name: 'Dual computers and radio devices' }).getByRole('button', { name: 'Fail cross-talk link' }).click()
    }
    await page.getByLabel('FMS guidance source', { exact: true }).selectOption(String(side))
    await page.getByLabel('CDU inspected', { exact: true }).selectOption(String(side))
    const lower = page.getByRole('radiogroup', { name: 'Lower display' })
    const map = page.getByRole('img', { name: /^Navigation map/ })
    const ownPath = page.locator('.fmsMap path.active')
    await lower.getByText('Engineering map', { exact: true }).click()
    await expect(map).toHaveAttribute('aria-label', new RegExp(`FMS ${side} inspected and guiding, .*LNAV mode, active waypoint MUN`))
    await expect(ownPath).toHaveCount(1)
    await page.getByLabel('Selected heading').fill('180')
    await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
    await expect(map).toHaveAttribute('aria-label', new RegExp(`FMS ${side} inspected and guiding, .*HDG mode, active waypoint MUN`))
    await expect(ownPath).toHaveCount(0)
    await expect(page.locator('.fmsMap g.activeWpt')).toHaveText('MUN')
    await page.locator('.fmsMap').screenshot({ path: testInfo.outputPath('heading-managed-waypoint-no-flown-leg.png') })

    // The selected unit still publishes its executed route and active waypoint for the managed ND.
    await lower.getByText('ND', { exact: true }).click()
    await expect(page.getByTestId('nd-route')).toHaveCount(1)
    await expect(page.getByTestId('nd-active-wpt')).toContainText('MUN')
    await lower.getByText('Engineering map', { exact: true }).click()
    await page.getByLabel('CDU inspected', { exact: true }).selectOption(String(side === 1 ? 2 : 1))
    await expect(page.getByTestId('guiding-label')).toHaveText(`FMS ${side} guiding, HDG mode`)
    await expect(page.getByTestId('guiding-leg')).toHaveCount(0)
    // Both computers observe the one AFCS's HDG selection, even with independent managed routes.
    await expect(map).toHaveAttribute('aria-label', /HDG mode, active waypoint MUN/)
    await expect(ownPath).toHaveCount(0)

    await page.getByLabel('CDU inspected', { exact: true }).selectOption(String(side))
    await page.getByRole('button', { name: 'LNAV', exact: true }).click()
    await page.getByRole('button', { name: 'Fly', exact: true }).click()
    await expect(map).toHaveAttribute('aria-label', /LNAV mode, active waypoint MUN/)
    await page.getByRole('button', { name: 'Pause', exact: true }).click()
    await expect(ownPath).toHaveCount(1)
  })
}
