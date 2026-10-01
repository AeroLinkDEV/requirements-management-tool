import { createServer } from 'node:http'
import { readFile } from 'node:fs/promises'
import type { AddressInfo } from 'node:net'
import { renderedTest } from '../../tests/isolated-client-test'
import { WebSocketServer } from 'ws'

type NativeServer = { origin: string; externalOrigin: string; apiHits: number; externalHits: number; afterCloseHits: number; wsMessages: number; protocols: string[] }

export const nativeTest = renderedTest.extend<{ nativeServer: NativeServer }>({
  nativeServer: async ({ browserName: _browserName }, provide) => {
    const image = await readFile(new URL('../../public/fms-cdu/panel.webp', import.meta.url))
    const font = await readFile(new URL('../../node_modules/@fontsource/dm-sans/files/dm-sans-latin-400-normal.woff2', import.meta.url))
    const state: NativeServer = { origin: '', externalOrigin: '', apiHits: 0, externalHits: 0, afterCloseHits: 0, wsMessages: 0, protocols: [] }
    const external = createServer((_request, response) => {
      state.externalHits++
      response.setHeader('Access-Control-Allow-Origin', '*')
      response.end('This receiver must remain untouched by isolated browser requests.')
    })
    external.on('upgrade', () => { state.externalHits++ })
    const externalSockets = new WebSocketServer({ server: external })
    await new Promise<void>(resolve => external.listen(0, '127.0.0.1', resolve))
    state.externalOrigin = `http://127.0.0.1:${(external.address() as AddressInfo).port}`
    const server = createServer((request, response) => {
      if (/^\/api(?:\/|$)/i.test(request.url!)) state.apiHits++
      if (request.url === '/after-close') state.afterCloseHits++
      if (request.url === '/redirect-api' || request.url === '/redirect-external') {
        response.writeHead(302, { location: request.url === '/redirect-api' ? '/api/redirect' : `${state.externalOrigin}/redirect` })
        response.end()
        return
      }
      if (request.url === '/image.webp') {
        response.setHeader('Content-Type', 'image/webp')
        response.end(image)
      } else if (request.url === '/font.woff2') {
        response.setHeader('Content-Type', 'font/woff2')
        response.end(font)
      } else if (/^\/api(?:\/|$)/i.test(request.url!)) {
        response.setHeader('Content-Type', 'application/json')
        response.end('{"reachable":true}')
      } else {
        response.setHeader('Content-Type', 'text/html')
        response.end('<!doctype html><title>Native isolation control</title><button>Open native popup</button>')
      }
    })
    server.on('upgrade', (request, socket) => {
      if (/^\/api(?:\/|$)/i.test(request.url!)) state.apiHits++
      if (request.url === '/ws-redirect-api' || request.url === '/ws-redirect-external') {
        socket.end(`HTTP/1.1 302 Found\r\nLocation: ${request.url === '/ws-redirect-api' ? `${state.origin}/api/socket-redirect` : `${state.externalOrigin}/socket-redirect`}\r\nConnection: close\r\n\r\n`)
      }
    })
    const fixtureSockets = new WebSocketServer({ noServer: true })
    server.on('upgrade', (request, socket, head) => {
      if (!request.url!.startsWith('/ws-redirect-')) fixtureSockets.handleUpgrade(request, socket, head, client => fixtureSockets.emit('connection', client))
    })
    fixtureSockets.on('connection', client => {
      state.protocols.push(client.protocol)
      client.on('message', (message, binary) => { state.wsMessages++; client.send(message, { binary }) })
    })
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve))
    state.origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
    try { await provide(state) } finally {
      for (const receiver of [fixtureSockets, externalSockets]) {
        for (const client of receiver.clients) client.terminate()
        await new Promise<void>(resolve => receiver.close(() => resolve()))
      }
      for (const receiver of [server, external]) {
        receiver.closeAllConnections()
        await new Promise<void>((resolve, reject) => receiver.close(error => error ? reject(error) : resolve()))
      }
    }
  },
  baseURL: async ({ nativeServer }, provide) => { await provide(nativeServer.origin) },
})
