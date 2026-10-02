import { expect } from '../../tests/isolated-client-test'
import { nativeTest as test } from './native-server'

test('swallowed early child requests and redirected forbidden destinations never reach receiving servers', async ({ page, nativeServer }) => {
  await page.goto('/')
  await page.evaluate(externalOrigin => {
    document.querySelector('button')!.onclick = () => {
      const child = window.open('', 'native-network-offender')! as Window & typeof globalThis
      const origin = location.origin
      const image = child.document.createElement('img')
      image.src = `${origin}/image.webp`
      child.document.body.append(image)
      const font = new child.FontFace('NativeProbe', `url(${origin}/font.woff2)`)
      child.document.fonts.add(font)
      void font.load().catch(() => {})
      // Start before Playwright receives the popup event. Swallow every refusal deliberately.
      ;(child as unknown as { deniedRequests: Promise<unknown> }).deniedRequests = Promise.all([
        `${origin}/api/direct`, `${externalOrigin}/direct`, `${origin}/redirect-api`, `${origin}/redirect-external`,
      ].map(url => child.fetch(url).catch(() => {})))
    }
  }, nativeServer.externalOrigin)
  const opened = page.waitForEvent('popup')
  await page.getByRole('button', { name: 'Open native popup', exact: true }).click()
  const child = await opened
  await expect.poll(() => child.locator('img').evaluate(image => (image as HTMLImageElement).naturalWidth), { timeout: 5000 }).toBeGreaterThan(0)
  await expect.poll(() => child.evaluate(() => [...document.fonts].map(font => font.status)), { timeout: 5000 }).toEqual(['loaded'])
  await child.evaluate(() => (window as unknown as { deniedRequests: Promise<unknown> }).deniedRequests)
  await child.evaluate(({ origin, externalOrigin }) => Promise.all([
    `${origin}/api/socket`, `${externalOrigin}/socket`, `${origin}/ws-redirect-api`, `${origin}/ws-redirect-external`,
  ].map(url => new Promise<void>(resolve => {
    const socket = new WebSocket(url.replace('http:', 'ws:'))
    socket.onerror = () => resolve()
    socket.onclose = () => resolve()
    socket.onopen = () => { socket.close(); resolve() }
  }))), nativeServer)
  // Cover the first navigation as well as subresources in an initial empty document.
  for (const url of [`${nativeServer.origin}/api/first-navigation`, `${nativeServer.externalOrigin}/first-navigation`]) {
    const nextOpened = page.waitForEvent('popup')
    await page.evaluate(url => window.open(url, '_blank'), url)
    const navigation = await nextOpened
    await navigation.waitForLoadState('domcontentloaded')
    await navigation.close()
  }
  expect(nativeServer.apiHits).toBe(0)
  expect(nativeServer.externalHits).toBe(0)
  console.log('receiving-server proof: API=0 external=0; native image and font loaded')
  await child.close()
  // The rendered fixture's teardown must fail despite every error being swallowed.
})

test('a fulfilled forbidden request still fails fixture teardown', async ({ page, nativeServer }) => {
  await page.route('**/api/mocked', route => route.fulfill({ json: { mocked: true } }))
  await page.goto('/')
  expect(await page.evaluate(() => fetch('/api/mocked').then(response => response.json()).catch(() => null))).toEqual({ mocked: true })
  expect(nativeServer.apiHits).toBe(0)
})
