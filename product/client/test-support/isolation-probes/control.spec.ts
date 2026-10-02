import { expect } from "../../tests/isolated-client-test"
import { nativeTest as test } from './native-server'
import { hmrTest } from './vite-server'

test("ordinary rendered control does not use an API request context", async ({ page, context, nativeServer }) => {
  expect(page).toBeTruthy()
  expect(context).toBeTruthy()
  await page.goto('/')
  await page.evaluate(() => {
    document.querySelector('button')!.onclick = () => {
      const child = window.open('', 'native-isolation-control')! as Window & typeof globalThis
      const image = child.document.createElement('img')
      image.src = new URL('/image.webp', location.href).href
      child.document.body.append(image)
      const font = new child.FontFace('NativeProbe', `url(${new URL('/font.woff2', location.href).href})`)
      child.document.fonts.add(font)
      void font.load().catch(() => {})
    }
  })
  const opened = page.waitForEvent('popup')
  await page.getByRole('button', { name: 'Open native popup', exact: true }).click()
  const child = await opened
  await expect.poll(() => child.locator('img').evaluate(image => (image as HTMLImageElement).naturalWidth), { timeout: 5000 }).toBeGreaterThan(0)
  await expect.poll(() => child.evaluate(() => [...document.fonts].map(font => font.status)), { timeout: 5000 }).toEqual(['loaded'])
  const roundTrip = await child.evaluate(origin => new Promise<{ protocol: string; text: string; bytes: number[] }>((resolve, reject) => {
    const socket = new WebSocket(`${origin.replace('http:', 'ws:')}/hmr-echo`, 'vite-hmr')
    socket.binaryType = 'arraybuffer'
    let text = ''
    socket.onopen = () => socket.send('real fixture round trip')
    socket.onerror = () => reject(new Error('Native WebSocket receiver failed'))
    socket.onmessage = event => {
      if (typeof event.data === 'string') { text = event.data; socket.send(new Uint8Array([11, 22, 33])) }
      else { resolve({ protocol: socket.protocol, text, bytes: [...new Uint8Array(event.data)] }); socket.close() }
    }
  }), nativeServer.origin)
  expect(roundTrip).toEqual({ protocol: 'vite-hmr', text: 'real fixture round trip', bytes: [11, 22, 33] })
  expect(nativeServer.protocols).toEqual(['vite-hmr'])
  expect(nativeServer.wsMessages).toBe(2)
  await child.close()
})

test('an unavailable isolation proxy never falls back to a direct client connection', async ({ page, networkGuard, nativeServer }) => {
  await page.goto('/')
  await networkGuard.close()
  expect(await page.evaluate(() => fetch('/after-close').then(() => true, () => false))).toBe(false)
  expect(nativeServer.afterCloseHits).toBe(0)
})

hmrTest('a real Vite file change reaches the isolated browser through its negotiated HMR socket', async ({ page, hmrServer }) => {
  await page.goto('/')
  await expect(page.locator('output')).toHaveText('before')
  // The file watcher and Vite's real update message must both participate.
  await hmrServer.update()
  await expect(page.locator('output')).toHaveText('after')
})
