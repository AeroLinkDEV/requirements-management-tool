import { expect, renderedTest as test, type Page } from './isolated-client-test'

// #1545 corrected primary owner: C-6 managed route visibility is independent of AFCS coupling.
// #1504 owns the separate guiding marker and inspected-computer wiring. Geometry here identifies
// the actual CF course; the still-present hover join (#1561) cannot satisfy its endpoint oracle.
const open = async (page: Page) => {
  await page.clock.setFixedTime(new Date('2026-09-29T15:00:00Z'))
  await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('radiogroup', { name: 'Lower display' }).getByText('Engineering map', { exact: true }).click()
}
const key = (page: Page, id: string) => page.getByTestId('fms-cdu-inspected').locator(`.fmsCduKey[data-key="${id}"]`).click()
const map = (page: Page) => page.getByRole('img', { name: /^Navigation map/ })

/** Actual solid-magenta path into the active waypoint, not an unrelated path or join. */
async function managedLeg(page: Page, ident: string, course?: number) {
  await expect(page.locator('.fmsMap g.activeWpt')).toHaveText(ident)
  const witness = await page.locator('.fmsMap').evaluate(svg => {
    const symbol = svg.querySelector('g.activeWpt')!
    const end = (symbol.getAttribute('transform') ?? '').match(/translate\(([-\d.]+),([-\d.]+)\)/)!.slice(1).map(Number)
    return [...svg.querySelectorAll('path.active')].flatMap(p => {
      const points = (p.getAttribute('d') ?? '').match(/[-\d.]+,[-\d.]+/g)?.map(s => s.split(',').map(Number)) ?? []
      if (points.length !== 2 || Math.hypot(points[1][0] - end[0], points[1][1] - end[1]) > 0.15) return []
      const dx = points[1][0] - points[0][0], dy = points[1][1] - points[0][1]
      const style = getComputedStyle(p)
      return [{ course: (Math.atan2(dx, -dy) * 180 / Math.PI + 360) % 360, length: Math.hypot(dx, dy), stroke: style.stroke, dash: style.strokeDasharray }]
    })
  })
  await test.info().attach(`managed-${ident}-${course ?? 'route'}`, {
    body: JSON.stringify({ ident, expectedCourse: course ?? null, map: await map(page).getAttribute('aria-label'), witness }),
    contentType: 'application/json',
  })
  const candidate = course === undefined ? witness[0] : witness.find(w => Math.abs(w.course - course) < 0.5 && Math.abs(w.length - 150) < 3)
  expect(candidate, `solid managed leg into ${ident}${course === undefined ? '' : ` on ${course}° true, 30 NM at 20 NM range`}`).toBeDefined()
  expect(candidate!.stroke).toBe('rgb(255, 92, 240)')
  expect(candidate!.dash).toBe('none')
}

test('HDG with NAV armed retains the inspected active managed leg in solid magenta', async ({ page }, info) => {
  await open(page)
  await page.getByLabel('Selected heading').fill('180')
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await page.getByRole('button', { name: 'LNAV', exact: true }).click()
  await expect(page.getByRole('button', { name: 'LNAV', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(map(page)).toHaveAttribute('aria-label', /FMS 1 inspected and guiding, .*HDG mode, active waypoint MUN/)
  await expect(page.getByTestId('fma-roll')).toHaveText('HDG')
  await managedLeg(page, 'MUN')
  await page.locator('.fmsMap').screenshot({ path: info.outputPath('hdg-armed-managed-route.png') })
})

test('87N crew-vector CF inbound to TDN remains magenta in HDG with NAV armed and after NAV capture', async ({ page }, info) => {
  await open(page)
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('050')
  const scenarios = page.getByRole('region', { name: 'Scenarios' })
  const scenario = { id: 'managed-cf', title: 'CF crew vector', objective: 'Inspect the executed CF route', maxSeconds: 2, start: '87n-offshore-sar', steps: [
    // As the library crew-vector case does, establish one second of receiver/flight observations before ACTIVATE.
    { when: { kind: 'time', seconds: 1 }, action: { kind: 'expectAircraft', minAltitude: 490, maxAltitude: 510 } },
  ] }
  await scenarios.getByLabel('Scenario file').setInputFiles({ name: 'managed-cf.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(scenario)) })
  await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(scenarios.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
  for (const id of ['F2_2', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC', 'LEGS']) await key(page, id)
  for (const id of 'DELETE') await key(page, id)
  for (const id of ['LSK1L', 'EXEC']) await key(page, id)
  await page.getByLabel('Selected heading').fill('230')
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await page.getByRole('button', { name: 'LNAV', exact: true }).click()
  // Manual crew-vector operation leaves NAV armed under heading control until integration captures it.
  await expect(map(page)).toHaveAttribute('aria-label', /HDG mode, active waypoint TDN/)
  await expect(page.getByTestId('fma-roll')).toHaveText('HDG')
  await expect(page.getByTestId('fma-roll-armed')).toHaveText('NAV')
  await managedLeg(page, 'TDN', 230)
  await page.locator('.fmsMap').screenshot({ path: info.outputPath('tdn-cf-hdg.png') })
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(map(page)).toHaveAttribute('aria-label', /LNAV mode, active waypoint TDN/)
  await expect(page.getByTestId('fma-roll')).toHaveText('NAV')
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await managedLeg(page, 'TDN', 230)
  await page.locator('.fmsMap').screenshot({ path: info.outputPath('tdn-cf-lnav.png') })
})

test('LNAV control keeps the active managed route and coupling mode', async ({ page }, info) => {
  await open(page)
  await expect(map(page)).toHaveAttribute('aria-label', /LNAV mode, active waypoint MUN/)
  await expect(page.getByTestId('fma-roll')).toHaveText('NAV')
  await managedLeg(page, 'MUN')
  await page.locator('.fmsMap').screenshot({ path: info.outputPath('lnav-managed-route.png') })
})

test('SYNC FMS2 observer inspected while FMS1 guides HDG retains its own managed route', async ({ page }, info) => {
  await open(page)
  await expect(page.getByLabel('FMS guidance source', { exact: true })).toHaveValue('1')
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await page.getByRole('button', { name: 'LNAV', exact: true }).click()
  await page.getByLabel('CDU inspected', { exact: true }).selectOption('2')
  await expect(page.getByTestId('map-computer')).toHaveText('FMS 2 · INSPECTED')
  await expect(map(page)).toHaveAttribute('aria-label', /FMS 2 inspected, .*HDG mode, active waypoint MUN; FMS 1 guiding, HDG mode/)
  await managedLeg(page, 'MUN')
  await page.locator('.fmsMap').screenshot({ path: info.outputPath('fms2-observer-managed-route.png') })
})
