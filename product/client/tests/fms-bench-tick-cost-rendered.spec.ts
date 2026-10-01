import { expect, renderedTest as test } from './isolated-client-test'

// #1349: the simulation tick re-renders the bench four times a second, frozen or flying. Rebuilding every hidden tool
// tab on each tick (the key log alone holds up to 200 entries) kept the main thread busy enough that, on a loaded host,
// one pointer press on the CDU waited seconds behind the ticks. A hidden tab panel is no longer rebuilt by a tick: no
// render reaches it, so its DOM is untouched (before, React re-applied every controlled input's attributes each tick).
test('a simulation tick leaves hidden tool tabs alone and still updates the shown one', async ({ page }) => {
  await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  // Fill the key log, then leave it hidden behind the Scenarios tab.
  for (const id of ['A', 'B', 'C', 'D', 'E']) await page.locator(`.fmsCduKey[data-key="${id}"]`).click()
  await page.getByRole('tab', { name: 'Scenarios', exact: true }).click()
  const mutations = await page.evaluate(() => new Promise<Record<string, number>>(resolve => {
    const counts: Record<string, number> = {}
    const panels = Array.from(document.querySelectorAll<HTMLElement>('[role="tabpanel"]'))
    const observers = panels.map(panel => {
      const observer = new MutationObserver(list => { counts[panel.id] = (counts[panel.id] ?? 0) + list.length })
      observer.observe(panel, { subtree: true, childList: true, attributes: true, characterData: true })
      counts[panel.id] = 0
      return observer
    })
    // Six ticks of the 250 ms simulation clock.
    setTimeout(() => { observers.forEach(observer => observer.disconnect()); resolve(counts) }, 1500)
  }))
  for (const id of ['fms-bench-tab-conditions', 'fms-bench-tab-dual', 'fms-bench-tab-navdata', 'fms-bench-tab-lighting'])
    expect(mutations[id], `${id} changed while hidden`).toBe(0)
  // Shown again, a panel is current: the key log has every press.
  await page.getByRole('tab', { name: 'Lighting and keys', exact: true }).click()
  await expect(page.locator('.fmsBenchLog h2 small')).toHaveText('5')
  await page.locator('.fmsCduKey[data-key="F"]').click()
  await expect(page.locator('.fmsBenchLog h2 small')).toHaveText('6')
})

// The CDU screen redraws a line only when one of its cells changes. A value that changes between ticks with no key
// pressed must still repaint: the TIMER page's UTC seconds and its countdown, read from the drawn cells (the screen's
// aria-label is computed separately and would not show a stale drawing).
test('a CDU line whose value changes between ticks is redrawn: the TIMER page\'s UTC seconds and countdown', async ({ page }) => {
  await page.addInitScript(() => { try { window.localStorage.clear() } catch { /* private mode */ } })
  await page.goto('/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
  const key = (id: string) => page.locator(`.fmsCduKey[data-key="${id}"]`).click()
  const drawn = async (line: number) => ((await page.locator('.fmsCduScreen .cduLine').nth(line).textContent()) ?? '').replace(/ /g, ' ')
  await key('INIT_REF')
  await expect(page.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /TIMER>/)
  await key('LSK5R')
  await expect(page.locator('.fmsCduScreen')).toHaveAttribute('aria-label', /^\s*TIMER/)
  // A one-minute countdown.
  await key('1')
  await key('LSK1R')
  const utc = await drawn(4)
  const countdown = await drawn(2)
  expect(utc).toMatch(/\d{4}:\d{2}Z/)
  expect(countdown).toMatch(/0[01]:\d{2}/)
  await expect.poll(() => drawn(4), { timeout: 5_000 }).not.toBe(utc)
  await expect.poll(() => drawn(2), { timeout: 5_000 }).not.toBe(countdown)
})
