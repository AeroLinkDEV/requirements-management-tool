import { expect, test } from '@playwright/test'
import { login, showcaseSeed } from './auth'

test('HOME currency refreshes passively and loses its current claim when the status read fails', async ({ page, request }, testInfo) => {
  await showcaseSeed(request)
  await page.clock.install()
  let state = 'Current'
  let unavailable = true
  let reads = 0
  await page.route('**/health/identity', route => {
    reads++
    expect(route.request().method()).toBe('GET')
    return route.fulfill({ status: unavailable ? 503 : 200, contentType: 'application/json', body: JSON.stringify({
      sourceShortSha: 'abc12345', mode: 'HOME-PRODUCTION',
      instance: { label: 'HOME CANONICAL', classification: 'HomeCanonical' },
      mainCurrency: { state, checkedAtUtc: new Date().toISOString(), remoteSha: 'b'.repeat(40) },
    }) })
  })
  await login(page, 'admin')
  const badge = page.getByTestId('instance-badge')
  const currency = badge.getByTestId('main-currency')
  await expect(badge).toHaveCount(0)
  unavailable = false
  await page.clock.fastForward(61_000)
  await expect(currency).toContainText('Current main')
  // The source SHA and check age moved into the disclosure under the summary (#1048): open it and read
  // the same facts from their accessible surface instead of the closed chip.
  await badge.locator('summary').click()
  const details = badge.getByTestId('instance-details')
  await expect(details).toContainText('abc12345')
  await expect(details).toContainText(/checked \d+m ago/)
  const firstReads = reads
  state = 'UpdateAvailable'
  await page.clock.fastForward(61_000)
  await expect(currency).toContainText('Main update available')
  expect(reads).toBeGreaterThan(firstReads)
  await expect(badge.getByTestId('instance-label')).toHaveText('HOME')
  await page.setViewportSize({ width: 1280, height: 900 })
  const box = await badge.boundingBox()
  expect(box).not.toBeNull()
  expect(box!.x + box!.width).toBeLessThanOrEqual(1280)
  expect(await badge.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('home-main-currency.png') })
  unavailable = true
  await page.clock.fastForward(61_000)
  await expect(currency).toContainText('Main unverified')
  await expect(currency).not.toContainText('Current main')
})

/**
 * #925 P2 — the instance badge names the installation without shouting deployment vocabulary.
 *
 * A declared instance label may carry its classification as a suffix ("HOME CANONICAL"). Routine pages
 * show the plain installation name; the full declared label, classification, source, database and
 * snapshot facts stay in the operator tooltip. Nothing here reclassifies or renames the installation —
 * the payload below is exactly the shape /health/identity returns, replayed so the proof does not
 * depend on which installation the build happens to run against.
 */

test('the badge shows the installation name and keeps the declaration in the tooltip', async ({ page, request }) => {
  await showcaseSeed(request)
  // The declaration spells the classification as one PascalCase word (`HomeCanonical`) while the label
  // spaces it — the payload is the production shape, not the display words.
  await page.route('**/health/identity', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      sourceShortSha: 'abc1234',
      mode: 'HOME-PRODUCTION',
      instance: { label: 'HOME CANONICAL', classification: 'HomeCanonical', snapshot: null },
      database: { name: 'aerolink' },
    }),
  }))
  await login(page, 'admin')

  const badge = page.getByTestId('instance-badge')
  await expect(badge.getByTestId('instance-label')).toHaveText('HOME')
  await expect(badge.getByTestId('main-currency')).toContainText('Main unverified')
  // The visible closed summary names the plain installation; the declaration stays in the disclosure.
  await expect(badge.getByTestId('instance-summary')).not.toContainText('CANONICAL')
  await badge.locator('summary').click()
  const details = badge.getByTestId('instance-details')
  await expect(details).toContainText('HOME CANONICAL (HomeCanonical)')
  await expect(details).toContainText('aerolink')
  await expect(badge).toHaveAttribute('title', /Instance: HOME CANONICAL \(HomeCanonical\)/)
  await expect(badge).toHaveAttribute('title', /Database: aerolink/)
  await expect(badge).toHaveAttribute('data-classification', 'HomeCanonical')
})

test('an undeclared installation keeps its modest label unchanged', async ({ page, request }) => {
  await showcaseSeed(request)
  await page.route('**/health/identity', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      sourceShortSha: 'abc1234',
      mode: 'UNKNOWN',
      instance: { label: 'AEROLINK', classification: 'Undeclared', snapshot: null },
      database: { name: 'aerolink' },
    }),
  }))
  await login(page, 'admin')

  const badge = page.getByTestId('instance-badge')
  await expect(badge.getByTestId('instance-summary')).toHaveText('AEROLINK')
  await expect(badge).toHaveAttribute('data-classification', 'Undeclared')
})

test('custom declared labels render verbatim under other supported classifications', async ({ page, request }) => {
  await showcaseSeed(request)
  await login(page, 'admin')
  // The explicit rule names HOME CANONICAL and nothing else: a Demo or work-laptop declaration keeps
  // its operator's own label, word for word (#925 P2 / Astra C-ASTRA-R2-F01).
  for (const declared of [
    { label: 'Customer Demo', classification: 'LocalDemo' },
    { label: 'Flight Test Local', classification: 'WorkLaptopLocal' },
  ]) {
    await page.route('**/health/identity', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        sourceShortSha: 'abc1234',
        mode: 'UNKNOWN',
        instance: { label: declared.label, classification: declared.classification, snapshot: null },
        database: { name: 'aerolink' },
      }),
    }))
    await page.reload()
    const badge = page.getByTestId('instance-badge')
    await expect(badge.getByTestId('instance-summary')).toHaveText(declared.label)
    await expect(badge).toHaveAttribute('data-classification', declared.classification)
  }
})

test('HOME CANONICAL in a non-production mode keeps the plain label but never gains a currency claim', async ({ page }) => {
  await page.route('**/health/identity', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      mode: 'UNKNOWN',
      mainCurrency: null,
      instance: { label: 'HOME CANONICAL', classification: 'HomeCanonical', snapshot: null },
    }),
  }))
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge.getByTestId('instance-label')).toHaveText('HOME')
  await expect(badge.getByTestId('main-currency')).toHaveCount(0)
  await badge.locator('summary').click()
  const details = badge.getByTestId('instance-details')
  await expect(details).toContainText('HOME CANONICAL (HomeCanonical)')
  await expect(details).not.toContainText('Main currency')
})

test('fields the server does not supply stay absent from the disclosure', async ({ page }) => {
  // The non-loopback shape in RuntimeIdentity.cs omits source and database diagnostics; the badge must
  // present exactly the supplied facts and nothing inferred.
  await page.route('**/health/identity', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      mode: 'HOME-PRODUCTION',
      mainCurrency: { state: 'Unverified', checkedAtUtc: null, remoteSha: null },
      instance: { label: 'HOME CANONICAL', classification: 'HomeCanonical', snapshot: null },
      database: { name: null },
    }),
  }))
  await login(page, 'admin', { openProject: false })
  const badge = page.getByTestId('instance-badge')
  await expect(badge.getByTestId('main-currency')).toContainText('Main unverified')
  await badge.locator('summary').click()
  const details = badge.getByTestId('instance-details')
  await expect(details).toContainText('HOME CANONICAL (HomeCanonical)')
  await expect(details).toContainText('Main unverified')
  await expect(details).not.toContainText('Database')
  await expect(details).not.toContainText('Source')
  await expect(details).not.toContainText('Last check')
  await expect(details).not.toContainText('remote main')
})

test('the Source row shows the full supplied SHA, falls back to the short form, and stays absent when neither is supplied', async ({ page }) => {
  // Distinguishable hashes so a wrong-row match cannot pass: source (a…), remote main (c…), snapshot
  // provenance (d…) are all different 40-character values.
  const fullSha = 'a'.repeat(40)
  const shortSha = 'b1c2d3e4'
  const remoteSha = 'c'.repeat(40)
  const snapshotSha = 'd'.repeat(40)
  const cases = [
    { name: 'full supplied', payload: { sourceSha: fullSha, sourceShortSha: shortSha }, expected: `Source${fullSha}` },
    { name: 'short only', payload: { sourceShortSha: shortSha }, expected: `Source${shortSha}` },
    { name: 'absent or redacted', payload: {}, expected: null },
  ]
  for (const { name, payload, expected } of cases) {
    await page.route('**/health/identity', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        mode: 'HOME-PRODUCTION',
        mainCurrency: { state: 'Current', checkedAtUtc: new Date().toISOString(), remoteSha },
        instance: {
          label: 'HOME CANONICAL',
          classification: 'HomeCanonical',
          snapshot: { sourceLabel: 'ELSEWHERE', sourceSha: snapshotSha, createdAtUtc: new Date().toISOString() },
        },
        ...payload,
      }),
    }))
    await page.goto('/')
    // An authenticated session from an earlier case goes straight to Projects; a first visit signs in.
    const username = page.getByLabel('Username')
    if (await username.isVisible().catch(() => false)) {
      await username.fill('admin')
      await page.getByLabel('Password').fill('AeroLink!2026')
      await page.getByRole('button', { name: /Sign in securely/ }).click()
    }
    const badge = page.getByTestId('instance-badge')
    await expect(badge).toBeVisible()
    await badge.locator('summary').click()
    const panel = badge.getByTestId('instance-details')
    await expect(panel).toBeVisible()
    const sourceRow = panel.locator(':scope > div').filter({ hasText: /^Source/ })
    if (expected === null) {
      await expect(sourceRow, `${name}: no Source row may be invented`).toHaveCount(0)
    } else {
      // Exact row text: the field heading plus exactly the supplied value.
      await expect(sourceRow, `${name}: the Source row must carry the exact supplied identity`).toHaveText(expected)
    }
    // The remote-main fact is separate from the running source and stays distinct.
    if (expected !== null) {
      await expect(panel.locator(':scope > div').filter({ hasText: /^Last observed remote main/ })).toHaveText(`Last observed remote main${remoteSha}`)
    }
  }
})

test.describe('touch access to the installation disclosure', () => {
  test.use({ hasTouch: true })

  test('the disclosure opens from a touch activation, not only from hover', async ({ page }) => {
    await page.route('**/health/identity', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        sourceShortSha: 'abc1234',
        mode: 'HOME-PRODUCTION',
        instance: { label: 'HOME CANONICAL', classification: 'HomeCanonical', snapshot: null },
        database: { name: 'aerolink' },
      }),
    }))
    await login(page, 'admin', { openProject: false })
    const badge = page.getByTestId('instance-badge')
    await expect(badge).toBeVisible()
    const summary = badge.getByTestId('instance-summary')
    const box = await summary.boundingBox()
    expect(box, 'the summary has a tappable area').not.toBeNull()
    await expect(badge.getByTestId('instance-details')).toBeHidden()
    await page.touchscreen.tap(box!.x + box!.width / 2, box!.y + box!.height / 2)
    await expect(badge.getByTestId('instance-details')).toBeVisible()
    await expect(badge.getByTestId('instance-details')).toContainText('HOME CANONICAL (HomeCanonical)')
  })
})
