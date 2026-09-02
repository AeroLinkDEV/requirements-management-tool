import { expect, test } from '@playwright/test'
import { formatEvidentiaryDateTime, formatOrdinaryDateTime, formatOrdinaryTime } from '../src/presentation'
import { login } from './auth'

/**
 * Two deliberate timestamp grammars, proven against fixed instants rather than whatever the runner's
 * clock and locale happen to be:
 *
 *   ordinary     lists and previews read `14 Nov 2024 · 09:00` — minute precision, no seconds,
 *                no browser-default commas or AM/PM, identical on every machine;
 *   evidentiary  controlled history reads `14 Nov 2024 · 09:00:37 GMT-05:00` — seconds and an
 *                explicit offset, because "which second, which clock" is part of the record.
 *
 * The pure assertions pass an explicit time zone so they hold in any runner. The browser journey pins
 * `timezoneId` the same way execution-time-is-local.spec.ts does — CI runs in UTC, where a zero offset
 * would make a broken offset display invisible — and then checks the real rendered surfaces, including
 * the exact instant surviving in the `datetime` attribute. No stored value, JSON contract, or event
 * ordering is involved; these surfaces receive timestamps and format them.
 */

// 2024-11-14 14:00:37.123 UTC is 09:00:37 in Toronto (EST) and 10:00:37 in July (EDT).
const instant = '2024-11-14T14:00:37.123Z'
const summerInstant = '2024-07-14T14:00:37.123Z'

test.describe('ordinary date/time presentation', () => {
  test('formats minute precision with a fixed grammar in an explicit zone', () => {
    expect(formatOrdinaryDateTime(instant, 'UTC')).toBe('14 Nov 2024 · 14:00')
    expect(formatOrdinaryDateTime(instant, 'America/Toronto')).toBe('14 Nov 2024 · 09:00')
    expect(formatOrdinaryDateTime(new Date(instant), 'America/Toronto')).toBe('14 Nov 2024 · 09:00')
  })

  test('drops seconds and never falls back to the browser-default locale string', () => {
    const formatted = formatOrdinaryDateTime(instant, 'America/Toronto')
    expect(formatted).not.toContain(':37')
    expect(formatted).not.toContain('AM')
    expect(formatted).not.toContain('PM')
    expect(formatted).not.toContain(',')
  })

  test('formats the time half alone for date-labelled contexts', () => {
    expect(formatOrdinaryTime(instant, 'America/Toronto')).toBe('09:00')
    expect(formatOrdinaryTime(new Date(instant), 'UTC')).toBe('14:00')
  })

  test('an unparseable value formats as empty so the surface can own its absence wording', () => {
    expect(formatOrdinaryDateTime('not a timestamp')).toBe('')
    expect(formatOrdinaryTime('not a timestamp')).toBe('')
    expect(formatEvidentiaryDateTime('not a timestamp')).toBe('')
  })
})

test.describe('evidentiary date/time presentation', () => {
  test('keeps second precision and states the explicit offset', () => {
    expect(formatEvidentiaryDateTime(instant, 'America/Toronto')).toBe('14 Nov 2024 · 09:00:37 GMT-05:00')
    expect(formatEvidentiaryDateTime(instant, 'UTC')).toBe('14 Nov 2024 · 14:00:37 GMT+00:00')
    expect(formatEvidentiaryDateTime(new Date(summerInstant), 'America/Toronto')).toBe('14 Jul 2024 · 10:00:37 GMT-04:00')
  })

  test('preserves the exact instant without rounding', () => {
    // 37 seconds and 123 milliseconds survive: an evidentiary display must never invent a new instant.
    const formatted = formatEvidentiaryDateTime('2024-11-14T14:00:37.123Z', 'UTC')
    expect(formatted).toContain('14:00:37')
    expect(new Date('2024-11-14T14:00:37.123Z').toISOString()).toBe('2024-11-14T14:00:37.123Z')
  })
})

test.describe('rendered surfaces', () => {
  test.use({ timezoneId: 'America/Toronto' })

  test('the register lists scan-friendly instants and its history keeps the exact evidence', async ({ page }) => {
    test.setTimeout(180_000)
    await page.setViewportSize({ width: 1440, height: 900 })
    await login(page)
    const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')

    await page.goto(new URL(`${root}/systems/change-requests`, page.url()).toString(), { waitUntil: 'load' })
    const rows = page.locator('[data-register-row]')
    await expect(rows.first()).toBeVisible({ timeout: 30_000 })

    // Ordinary mode: every register instant is minute-precision, fixed grammar, and carries the exact
    // underlying timestamp in the semantic attribute rather than losing it to the compact display.
    const registerTimes = page.locator('[data-register-row] time[datetime]')
    await expect(registerTimes.first()).toBeVisible()
    for (const text of await registerTimes.allTextContents()) {
      expect(text).toMatch(/^\d{2} [A-Z][a-z]{2} \d{4} · \d{2}:\d{2}$/)
    }
    const registerAttribute = await registerTimes.first().getAttribute('datetime')
    expect(registerAttribute).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/)

    // Evidentiary mode: the selected record's immutable history keeps seconds and states its offset.
    await rows.first().click()
    await page.getByRole('tab', { name: 'History' }).click()
    const historyTimes = page.locator('.inspectorBody time[datetime]')
    await expect(historyTimes.first()).toBeVisible({ timeout: 30_000 })
    const historyText = await historyTimes.first().textContent()
    expect(historyText).toMatch(/^\d{2} [A-Z][a-z]{2} \d{4} · \d{2}:\d{2}:\d{2} GMT[-+]\d{2}:\d{2}$/)
    const historyAttribute = await historyTimes.first().getAttribute('datetime')
    expect(Number.isNaN(new Date(historyAttribute!).getTime())).toBe(false)
    // The zone is pinned, so the rendered offset must be Toronto's, not the runner's guess.
    expect(historyText).toContain('GMT-05:00')
  })
})
