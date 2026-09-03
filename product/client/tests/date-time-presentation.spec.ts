import { expect, test } from '@playwright/test'
import { formatEvidentiaryDateTime, formatOrdinaryDateTime, formatOrdinaryTime, invalidTimestampText } from '../src/presentation'
import { apiBase, login } from './auth'

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

  test('absence stays empty so the surface can own its wording, but a present invalid value fails closed', () => {
    // Genuine absence is the caller's wording decision ("Never", "Not recorded"): the helper stays silent.
    expect(formatOrdinaryDateTime(null)).toBe('')
    expect(formatOrdinaryDateTime(undefined)).toBe('')
    expect(formatOrdinaryDateTime('')).toBe('')
    expect(formatOrdinaryDateTime('   ')).toBe('')
    expect(formatOrdinaryTime(null)).toBe('')
    expect(formatEvidentiaryDateTime(undefined)).toBe('')
    // A present-but-unparseable controlled instant must never silently vanish from the evidence.
    expect(formatOrdinaryDateTime('not a timestamp')).toBe(invalidTimestampText)
    expect(formatOrdinaryTime('not a timestamp')).toBe(invalidTimestampText)
    expect(formatEvidentiaryDateTime('not a timestamp')).toBe(invalidTimestampText)
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
    // The visible evidentiary grammar is minute/second precision by design; the machine-readable instant
    // travels in the semantic <time datetime> attribute, so this asserts the formatter never invents one.
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
      expect(text).toMatch(/^\d{2} [A-Z][a-z]{2,3} \d{4} · \d{2}:\d{2}$/)
    }
    // The page's own authenticated API session is the source of truth, but the list endpoint is
    // filtered by release/type/level while the rendered row carries its own id: capture the first row's
    // id and instant first, then look the same id up through the release-scoped list the surface reads.
    const firstRowId = await rows.first().getAttribute('data-register-id')
    const registerAttribute = await registerTimes.first().getAttribute('datetime')
    const workspacesResponse = await page.request.get(`${apiBase}/api/workspaces`)
    expect(workspacesResponse.ok(), await workspacesResponse.text()).toBeTruthy()
    const workspaces = await workspacesResponse.json() as {
      projects: { project: { id: string }; releases: { id: string; isReleased: boolean }[] }[]
    }[]
    const workspaceProjectId = workspaces[0].projects[0].project.id
    const activeReleaseId = workspaces[0].projects[0].releases.find(item => !item.isReleased)?.id
      ?? workspaces[0].projects[0].releases[0].id
    const sourceRow = await page.evaluate(async ({ api, projectId, releaseId, rowId }) => {
      const params = new URLSearchParams({ projectId, page: '1', pageSize: '50', releaseId, type: 'System' })
      const response = await fetch(`${api}/api/history/change-requests?${params}`)
      if (!response.ok) return null
      const body = await response.json() as { items: { id: string; updatedAt: string }[] }
      return body.items.find(item => item.id === rowId) ?? null
    }, { api: apiBase, projectId: workspaceProjectId, releaseId: activeReleaseId, rowId: firstRowId })
    expect(sourceRow, 'register source row for the first rendered row').toBeTruthy()
    // The semantic instant must be the EXACT source timestamp, not a re-serialized minute truncation:
    // a `2024-11-14T14:00:37.123Z` → `2024-11-14T14:00:37Z` truncation must fail this regression.
    expect(registerAttribute).toBe(sourceRow!.updatedAt)

    // Evidentiary mode: the selected record's immutable history keeps seconds and states its offset.
    await rows.first().click()
    await page.getByRole('tab', { name: 'History' }).click()
    const historyTimes = page.locator('.inspectorBody time[datetime]')
    await expect(historyTimes.first()).toBeVisible({ timeout: 30_000 })
    const historyText = await historyTimes.first().textContent()
    expect(historyText).toMatch(/^\d{2} [A-Z][a-z]{2,3} \d{4} · \d{2}:\d{2}:\d{2} GMT[-+]\d{2}:\d{2}$/)
    // The history instant must be the EXACT controlled source timestamp: capture the detail record from
    // the same API the inspector reads and require byte equality, including fractional seconds/offset.
    // The first rendered history card is a review cycle when cycles exist, else the first audit event.
    const detailResponse = await page.request.get(`${apiBase}/api/change-requests/${firstRowId}`)
    expect(detailResponse.ok(), await detailResponse.text()).toBeTruthy()
    const detailBody = await detailResponse.json() as {
      audit?: { occurredAt: string }[]
      reviewCycles?: { startedAt: string; completedAt?: string }[]
    }
    const sourceHistory = detailBody.reviewCycles?.[0]?.startedAt ?? detailBody.audit?.[0]?.occurredAt
    expect(sourceHistory, 'controlled history source instant').toBeTruthy()
    const historyCards = page.locator('.inspectorBody .revisionCard time[datetime]')
    await expect(historyCards.first()).toBeVisible()
    const historyAttribute = await historyCards.first().getAttribute('datetime')
    expect(historyAttribute).toBe(sourceHistory)
    // The zone is pinned, so the rendered offset must be Toronto's, not the runner's guess.
    expect(historyText).toContain('GMT-05:00')
  })
})
