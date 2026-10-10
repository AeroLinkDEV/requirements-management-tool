import { readFile } from 'node:fs/promises'
import { expect, renderedTest as test } from './isolated-client-test'
import type { Locator, Page } from '@playwright/test'
import { installPausedClock } from './paused-clock'

// The bench hands each CDU key event to the recorder with its held flag (#1505): a key pressed on the bench panel or
// on cockpit CDU 1 is recorded at its own tick, a held CLR is recorded held, and the saved scenario replays it as one.
// The recorder and runner rules are proved in fms-scenario-recorded-keys.spec.ts; this proves the bench wiring.
const START = new Date('2026-09-27T14:00:00Z')
type Station = { cdu: Locator; scenarios: Locator; showInstructor: () => Promise<void>; showCdu: () => Promise<void> }

const rows: { name: string; setUp: (page: Page) => Promise<Station> }[] = [
  {
    name: 'the bench panel',
    setUp: async page => {
      const scenarios = page.getByRole('region', { name: 'Scenarios' })
      return { cdu: page.locator('.fmsCdu'), scenarios, showInstructor: async () => {}, showCdu: async () => {} }
    },
  },
  {
    name: 'cockpit CDU 1',
    setUp: async page => {
      await page.getByRole('button', { name: 'Cockpit view', exact: true }).click()
      const ios = page.getByRole('region', { name: 'Instructor station', exact: true })
      const showInstructor = async () => {
        await page.getByRole('button', { name: 'Instructor station', exact: true }).click()
        await ios.getByRole('tab', { name: 'Scenarios', exact: true }).click()
      }
      const showCdu = async () => { await page.keyboard.press('Escape'); await expect(ios).toBeHidden() }
      return { cdu: page.locator('.fmsBenchCduStation[data-side="1"] .fmsCdu'), scenarios: ios, showInstructor, showCdu }
    },
  },
]

for (const row of rows) {
  test(`keys on ${row.name} are recorded at their own ticks and a held CLR is recorded and replayed held`, async ({ page }) => {
    await installPausedClock(page, START)
    await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
    await page.goto('/tests/fixtures/fms-cdu.html')
    await expect(page.locator('.fmsCdu').first()).toBeVisible()
    const station = await row.setUp(page)
    const scratchpad = async () => (((await station.cdu.locator('.fmsCduScreen').getAttribute('aria-label')) ?? '').split('\n')[13] ?? '').trim()

    await station.showInstructor()
    await station.scenarios.getByRole('button', { name: 'Record', exact: true }).click()
    await station.scenarios.getByLabel('Recording name').fill('Held CLR')
    await station.showCdu()
    await station.cdu.locator('.fmsCduKey[data-key="A"]').click()
    await page.clock.runFor(250)
    await station.cdu.locator('.fmsCduKey[data-key="B"]').click()
    await expect.poll(scratchpad).toBe('AB')
    // CLR held for its full second clears the whole scratchpad live. The hold starts between bench ticks, so its
    // timer fires between ticks too (at 1.35 s, recorded on the 1.25 s tick) rather than tying with one.
    await page.clock.runFor(100)
    await station.cdu.focus()
    await page.keyboard.down('Backspace')
    await page.clock.runFor(1100)
    await page.keyboard.up('Backspace')
    await expect.poll(scratchpad).toBe('')

    await station.showInstructor()
    await station.scenarios.getByRole('button', { name: 'Stop recording' }).click()
    const [saved] = await Promise.all([page.waitForEvent('download'), station.scenarios.getByRole('button', { name: 'Save as JSON' }).click()])
    const recorded = JSON.parse(await readFile(await saved.path(), 'utf8')) as { steps: { when: { kind: string; seconds?: number }; action: Record<string, unknown> }[] }
    await saved.saveAs(test.info().outputPath('held-clr-recorded.json'))
    const keys = recorded.steps.filter(step => step.action.kind === 'keys')
    expect(keys.map(step => step.action)).toEqual([{ kind: 'keys', keys: ['CHAR_A'] }, { kind: 'keys', keys: ['CHAR_B'] }, { kind: 'keys', keys: ['CLR'], held: true }])
    // A and B were pressed a bench tick apart, so they replay a tick apart.
    expect(keys.map(step => step.when.seconds ?? 0)).toEqual([0, 0.25, 1.25])

    // Replayed on a restarted bench, the held CLR clears both characters, as it did live (a plain CLR would leave A).
    await station.scenarios.getByRole('button', { name: 'Run the scenario' }).click()
    // The replay types A and B first, so the blank scratchpad at the end is the held CLR's work, not an idle run.
    await page.clock.runFor(250)
    await expect.poll(scratchpad).toBe('AB')
    await page.clock.runFor(1750)
    await expect(station.scenarios.getByRole('status').filter({ hasText: /^NO CHECKS/ })).toBeVisible()
    await station.showCdu()
    expect(await scratchpad()).toBe('')
  })
}
