import { createServer, request } from 'node:http'
import type { AddressInfo, Socket } from 'node:net'
import type { WebSocketRoute } from '@playwright/test'
import WebSocket from 'ws'

// A native per-context proxy avoids #1441's split owner/child Fetch event pairing.
// It permits only the configured HTTP fixture origin, including each redirect hop.
// Playwright's supported proxy option routes Chromium loopback requests through it.
export async function createRenderedNetworkGuard(baseURL: string) {
  const client = new URL(baseURL)
  const origin = client.origin
  if (client.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(client.hostname)) {
    throw new Error('Rendered fixtures require a loopback HTTP client origin.')
  }
  const unexpected: string[] = []
  const failures: string[] = []
  const webSockets = new Set<WebSocket>()
  const allowed = (value: string) => {
    const url = new URL(value)
    return ['data:', 'blob:'].includes(url.protocol)
      || (url.origin === origin && !/^\/api(?:\/|$)/i.test(url.pathname))
  }
  const record = (value: string) => {
    const url = new URL(value)
    unexpected.push(`${url.origin}${url.pathname}`)
  }
  const sockets = new Set<Socket>()
  let closed = false
  const server = createServer((incoming, outgoing) => {
    let url: URL
    try { url = new URL(incoming.url!) } catch { outgoing.writeHead(400).end(); return }
    if (url.protocol !== 'http:' || !allowed(url.href)) { record(url.href); outgoing.writeHead(403).end(); return }
    const headers = { ...incoming.headers }
    delete headers['proxy-authorization']
    delete headers['proxy-connection']
    const upstream = request(url, { method: incoming.method, headers }, response => {
      outgoing.writeHead(response.statusCode!, response.headers)
      response.pipe(outgoing)
    })
    upstream.on('socket', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)) })
    upstream.on('error', () => { if (!outgoing.headersSent) outgoing.writeHead(502); outgoing.end() })
    outgoing.on('close', () => upstream.destroy())
    incoming.pipe(upstream)
  })
  server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)) })
  // The allowed client uses HTTP. Never establish an opaque TLS tunnel to any service.
  server.on('connect', (incoming, socket) => {
    unexpected.push(`CONNECT ${incoming.url}`)
    socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n')
  })
  // WebSockets use the path-checked public routing bridge below; no opaque/native upgrade escapes it.
  server.on('upgrade', (incoming, socket) => {
    unexpected.push(`HTTP upgrade ${incoming.url}`)
    socket.end('HTTP/1.1 403 Forbidden\r\n\r\n')
  })
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject)
    server.listen(0, '127.0.0.1', () => { server.off('error', reject); resolve() })
  })
  return {
    allowed, record, unexpected, failures,
    proxy: { server: `http://127.0.0.1:${(server.address() as AddressInfo).port}` },
    async connectWebSocket(route: WebSocketRoute) {
      const destination = new URL(route.url())
      const httpDestination = new URL(destination)
      if (httpDestination.protocol === 'ws:') httpDestination.protocol = 'http:'
      if (closed || destination.protocol !== 'ws:' || destination.username || destination.password || !allowed(httpDestination.href)) {
        record(destination.href)
        await route.close({ code: 1008, reason: 'Rendered fixture network isolation' })
        return
      }
      // Chromium tunnels ws:// through CONNECT, which cannot enforce the /api path policy.
      // Bridge only this validated destination through supported message APIs; redirects stay disabled.
      const upstream = new WebSocket(destination, route.protocols(), { followRedirects: false, origin })
      webSockets.add(upstream)
      let accepted = false
      upstream.on('open', () => {
        // A server can send immediately after open; accept negotiation before forwarding any message.
        accepted = upstream.protocol === (route.protocols()[0] ?? '')
        if (!accepted) {
          failures.push('WebSocket subprotocol negotiation differs from the routed socket.')
          upstream.terminate()
        }
      })
      route.onMessage(message => { if (accepted && upstream.readyState === WebSocket.OPEN) upstream.send(message) })
      route.onClose((code, reason) => {
        try { upstream.close(code, reason) } catch { upstream.terminate() }
      })
      upstream.on('message', (message, binary) => {
        if (accepted) route.send(binary ? Buffer.concat(Array.isArray(message) ? message : [Buffer.from(message as ArrayBuffer)]) : message.toString())
      })
      upstream.on('close', (code, reason) => { webSockets.delete(upstream); void route.close({ code, reason: reason.toString() }) })
      upstream.on('error', () => {
        if (!closed) failures.push('Allowed WebSocket transport failed.')
        void route.close({ code: 1006, reason: 'WebSocket transport failed' })
      })
      const opened = await new Promise<boolean>(resolve => {
        upstream.once('open', () => resolve(true))
        upstream.once('error', () => resolve(false))
        upstream.once('close', () => resolve(false))
      })
      // Playwright's supported routed socket advertises the first offered protocol.
      // Refuse mismatched real negotiation instead of reporting a false protocol to the page.
      if (opened && !accepted) {
        await route.close({ code: 1002, reason: 'Unsupported subprotocol negotiation' })
      }
    },
    async close() {
      if (closed) return
      closed = true
      const webSocketsClosed = [...webSockets].map(socket => new Promise<void>(resolve => {
        if (socket.readyState === WebSocket.CLOSED) resolve()
        else socket.once('close', () => resolve())
      }))
      for (const socket of webSockets) socket.terminate()
      for (const socket of sockets) socket.destroy()
      await Promise.all(webSocketsClosed)
      await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()))
    },
  }
}
