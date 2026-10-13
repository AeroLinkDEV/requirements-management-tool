import { readFile } from 'node:fs/promises'
import { expect, renderedTest as test } from './isolated-client-test'

// Only the real component can detect a first-render TDZ or a CDU/peer key bypassing the submission boundary.
// The exported production journal supplies the evidence; no private unit or kernel seam is used.
test('bench initializes and both real CDU panels submit held keys to the exported journal', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(`${error.name}: ${error.message}`))
  const epoch = new Date('2026-09-29T15:00:00Z')
  await page.clock.setFixedTime(epoch)
  await page.clock.pauseAt(epoch)
  await page.goto('/tests/fixtures/fms-cdu.html?terrain=off&imagery=off')
  await expect.poll(async () => errors.length > 0 || await page.locator('.fmsCdu').count() > 0).toBe(true)
  expect(errors, 'native first render must initialize the actual bench').toEqual([])
  await expect(page.locator('.fmsCdu')).toBeVisible()
  await page.getByRole('button', { name: 'Show peer CDU', exact: true }).click()
  const one = page.getByTestId('fms-cdu-inspected')
  const two = page.getByTestId('fms-cdu-peer')
  await one.locator('[data-key="RTE"]').click()
  await two.locator('[data-key="RTE"]').click()
  const clr = two.locator('[data-key="CLR"]')
  const box = (await clr.boundingBox())!
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down()
  await page.clock.runFor(1000)
  await page.mouse.up()
  // Play/pause must preserve the same composition and journal; an initializer dependency on playing loses these keys.
  await page.getByRole('button', { name: 'Fly', exact: true }).click()
  await page.getByRole('button', { name: 'Pause', exact: true }).click()
  // A refused FMS go-around remains a real AFCS selection and its outcome is read from the exported event journal.
  await page.getByRole('button', { name: 'TOGA', exact: true }).click()
  const downloaded = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Export journal', exact: true }).click()
  const download = await downloaded
  const record = JSON.parse(await readFile((await download.path())!, 'utf8'))
  const keys = record.journal.filter((entry: { kind: string }) => entry.kind === 'cdu.key')
  expect(keys.map((entry: { submissionSeq: number; payload: unknown; target: string; source: unknown; outcome: unknown }) =>
    [entry.submissionSeq, entry.payload, entry.target, entry.source, entry.outcome])).toEqual([
    [1, { kind: 'cdu.key', side: 1, fn: 'RTE', held: false }, 'fms1', { kind: 'cdu', id: 'CDU 1', surface: 'main', side: 1 }, { status: 'accepted' }],
    [2, { kind: 'cdu.key', side: 2, fn: 'RTE', held: false }, 'fms2', { kind: 'cdu', id: 'CDU 2', surface: 'main', side: 2 }, { status: 'accepted' }],
    [3, { kind: 'cdu.key', side: 2, fn: 'CLR', held: true }, 'fms2', { kind: 'cdu', id: 'CDU 2', surface: 'main', side: 2 }, { status: 'accepted' }],
  ])
  expect(record.journal.filter((entry: { kind: string }) => entry.kind === 'ios.flightFreeze').map((entry: { payload: unknown }) => entry.payload)).toEqual([
    { kind: 'ios.flightFreeze', on: false }, { kind: 'ios.flightFreeze', on: true },
  ])
  expect(record.journal.at(-1)).toMatchObject({ submissionSeq: 6, kind: 'afcs.select', target: 'afcs',
    payload: { kind: 'afcs.select', selection: { select: 'toga' } },
    outcome: { status: 'refused', reason: 'the FMS refused the missed approach: none ahead, or it has failed' },
  })
  expect(errors).toEqual([])
})
