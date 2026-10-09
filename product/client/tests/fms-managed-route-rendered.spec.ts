import { expect, renderedTest as test, type Page } from './isolated-client-test'
import { MAP_CF_EXTENT_NM, MAP_DEFAULT_RANGE_NM, MAP_RADIUS_UNITS } from '../src/fmsCdu/mapDrawing'

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
  const extent = MAP_CF_EXTENT_NM * MAP_RADIUS_UNITS / MAP_DEFAULT_RANGE_NM
  const candidate = course === undefined ? witness[0] : witness.find(w => Math.abs(w.course - course) < 0.5 && Math.abs(w.length - extent) < 3)
  expect(candidate, `solid managed leg into ${ident}${course === undefined ? '' : ` on ${course}° true, ${MAP_CF_EXTENT_NM} NM at ${MAP_DEFAULT_RANGE_NM} NM range`}`).toBeDefined()
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

for (const side of [1, 2]) test(`FMS${side} committed JN join has no unflown CF extension`, async ({ page }, info) => {
  await open(page)
  await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('050')
  const scenarios = page.getByRole('region', { name: 'Scenarios' })
  const scenario = { id: `committed-join-${side}`, title: 'Committed joining path', objective: 'Inspect JN before it is sequenced', maxSeconds: 2, start: '87n-offshore-sar', steps: [
    { when: { kind: 'time', seconds: 1 }, action: { kind: 'expectAircraft', minAltitude: 490, maxAltitude: 510 } },
  ] }
  await scenarios.getByLabel('Scenario file').setInputFiles({ name: 'committed-join.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(scenario)) })
  await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
  await expect(scenarios.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
  await page.getByLabel('FMS guidance source', { exact: true }).selectOption(String(side))
  await page.getByLabel('CDU inspected', { exact: true }).selectOption(String(side))
  if (side === 2) {
    // Source handover needs an actual selected-side input/computation before its hover entry.
    await page.getByRole('button', { name: 'Fly', exact: true }).click()
    await page.clock.runFor(250)
    await page.getByRole('button', { name: 'Pause', exact: true }).click()
  }
  for (const id of ['F2_2', 'LSK1R', 'LSK4L', 'LSK6R', 'EXEC']) await key(page, id)
  await expect(page.locator('.fmsMap g.activeWpt')).toHaveText('JN')
  await expect(page.getByTestId('hover-join')).toHaveAttribute('class', 'active')
  await expect(page.getByTestId('fma-roll')).toHaveText('NAV')
  const paths = await page.locator('.fmsMap path.active:not([data-testid="hover-join"])').evaluateAll(nodes => nodes.map(node => {
    const points = (node.getAttribute('d') ?? '').match(/[-\d.]+,[-\d.]+/g)?.map(s => s.split(',').map(Number)) ?? []
    return { d: node.getAttribute('d'), length: points.length === 2 ? Math.hypot(points[1][0] - points[0][0], points[1][1] - points[0][1]) : null }
  }))
  await info.attach('committed-join-geometry', { body: JSON.stringify({ side, paths, join: await page.getByTestId('hover-join').getAttribute('d'), map: await map(page).getAttribute('aria-label') }), contentType: 'application/json' })
  // #1561 separately owns removing this short chord. This row rejects the unflown CF extent.
  expect(paths).toHaveLength(1)
  expect(paths[0].length).not.toBeNull()
  expect(paths[0].length!).toBeLessThan(MAP_CF_EXTENT_NM * MAP_RADIUS_UNITS / MAP_DEFAULT_RANGE_NM / 2)
  await page.locator('.fmsMap').screenshot({ path: info.outputPath(`fms${side}-committed-join.png`) })
})

for (const side of [1, 2]) test(`FMS${side} managed offset remains drawn through HDG and NAV capture`, async ({ page }, info) => {
  await open(page)
  await page.getByLabel('FMS guidance source', { exact: true }).selectOption(String(side))
  await page.getByLabel('CDU inspected', { exact: true }).selectOption(String(side))
  for (const id of ['PROG', 'PREV', 'R', '0', 'DOT', '5', 'LSK1L', 'EXEC']) await key(page, id)
  const offset = page.locator('.fmsMap path.offset')
  await expect(offset).toHaveCount(1)
  const pathBefore = await offset.getAttribute('d')
  expect(pathBefore).toMatch(/^M[-\d.]+,[-\d.]+L[-\d.]+,[-\d.]+$/)
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await expect(page.getByTestId('fma-roll')).toHaveText('HDG')
  await expect(offset).toHaveCount(1)
  await expect(offset).toHaveAttribute('d', pathBefore!)
  await page.getByRole('button', { name: 'LNAV', exact: true }).click()
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(page.getByTestId('fma-roll')).toHaveText('NAV')
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  await expect(offset).toHaveCount(1)
  const pathCaptured = await offset.getAttribute('d')
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await expect(page.getByTestId('fma-roll')).toHaveText('HDG')
  await expect(offset).toHaveAttribute('d', pathCaptured!)
  // The other computer's actual-flown marker is still hidden while the selected driver is HDG.
  await page.getByLabel('CDU inspected', { exact: true }).selectOption(String(3 - side))
  await expect(page.getByTestId('guiding-leg')).toHaveCount(0)
  await expect(page.locator('.fmsMap path.offset')).toHaveCount(1)
  await info.attach('managed-offset-transition', { body: JSON.stringify({ side, pathBefore, pathCaptured, final: await map(page).getAttribute('aria-label') }), contentType: 'application/json' })
  await page.locator('.fmsMap').screenshot({ path: info.outputPath(`fms${side}-hdg-offset.png`) })
})

for (const pattern of ['HOLD', 'SAR'] as const) test(`${pattern} managed pattern segment remains unchanged when paused HDG is selected`, async ({ page }, info) => {
  test.setTimeout(120_000)
  await open(page)
  if (pattern === 'SAR') {
    await page.getByRole('combobox', { name: 'Hardware variation' }).selectOption('050')
    const scenarios = page.getByRole('region', { name: 'Scenarios' })
    const scenario = { id: 'managed-sar', title: 'Search pattern display', objective: 'Inspect the active search segment', maxSeconds: 2, start: '87n-offshore-sar', steps: [
      { when: { kind: 'time', seconds: 1 }, action: { kind: 'expectAircraft', minAltitude: 490, maxAltitude: 510 } },
    ] }
    await scenarios.getByLabel('Scenario file').setInputFiles({ name: 'managed-sar.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(scenario)) })
    await scenarios.getByRole('button', { name: 'Run the scenario' }).click()
    await expect(scenarios.getByRole('status').filter({ hasText: /^PASS/ })).toBeVisible()
    for (const id of ['F2_2', 'LSK4L', 'LSK6R', 'EXEC']) await key(page, id)
  } else {
    for (const id of ['F2_4', 'LSK2L', 'EXEC']) await key(page, id)
    // Existing instructor Jump reaches RDG and enters its executed hold. A real frame below
    // still computes the actual hold geometry before the preservation snapshot.
    await page.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
    await expect(page.locator('.fmsMap g.activeWpt')).toHaveText('RDG')
    await page.getByRole('button', { name: 'Jump to next waypoint', exact: true }).click()
    await expect(page.getByTestId('fms-cdu-inspected')).toContainText('IN PROGRESS')
  }
  await page.getByLabel('Simulation rate').selectOption('64')
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await expect(map(page)).toHaveAttribute('aria-label', new RegExp(`${pattern} mode`), { timeout: 95_000 })
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  const snapshot = () => page.locator('.fmsMap').evaluate(svg => ({
    active: [...svg.querySelectorAll('path.active')].map(p => ({ path: p.getAttribute('d'), stroke: getComputedStyle(p).stroke, dash: getComputedStyle(p).strokeDasharray })),
    pattern: [...svg.querySelectorAll('path.hold,path.sar')].map(p => ({ kind: p.getAttribute('class'), path: p.getAttribute('d') })),
    waypoint: svg.querySelector('g.activeWpt')?.textContent,
  }))
  const before = await snapshot()
  expect(before.pattern.some(p => p.kind === pattern.toLowerCase())).toBe(true)
  // SAR has a straight current segment; hold turns legitimately have no straight active segment.
  if (pattern === 'SAR') expect(before.active).toHaveLength(1)
  await page.getByRole('button', { name: 'HDG SEL', exact: true }).click()
  await expect(map(page)).toHaveAttribute('aria-label', /HDG mode/)
  await expect(page.getByTestId('fma-roll')).toHaveText('HDG')
  const after = await snapshot()
  await info.attach('managed-pattern-transition', { body: JSON.stringify({ pattern, before, after }), contentType: 'application/json' })
  expect(after).toEqual(before)
  await page.locator('.fmsMap').screenshot({ path: info.outputPath(`${pattern.toLowerCase()}-hdg-managed-pattern.png`) })
})
