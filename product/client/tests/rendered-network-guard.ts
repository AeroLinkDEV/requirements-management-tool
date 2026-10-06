import { createServer, request, type ClientRequest, type IncomingHttpHeaders } from 'node:http'
import type { AddressInfo, Socket } from 'node:net'
import type { WebSocketRoute } from '@playwright/test'
import WebSocket from 'ws'

// Connection-scoped (hop-by-hop) response headers describe the upstream connection, not the browser's.
// Relaying a recovery's `Connection: close` would make the browser open a new loopback connection (#986).
function withoutHopByHop(headers: IncomingHttpHeaders) {
  const relayed = { ...headers }
  const named = String(headers.connection ?? '').split(',').map(token => token.trim().toLowerCase()).filter(Boolean)
  for (const name of ['connection', 'keep-alive', ...named]) delete relayed[name]
  return relayed
}

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
  const recovered: string[] = []
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
  const trackSocket = (socket: Socket) => {
    // Node's HTTP agent can reuse an upstream socket for many fixture assets.
    // Own one close subscription per socket rather than one per request.
    if (sockets.has(socket)) return
    sockets.add(socket)
    socket.once('close', () => sockets.delete(socket))
  }
  let closed = false
  const server = createServer((incoming, outgoing) => {
    let url: URL
    try { url = new URL(incoming.url!) } catch { outgoing.writeHead(400).end(); return }
    if (url.protocol !== 'http:' || !allowed(url.href)) { record(url.href); outgoing.writeHead(403).end(); return }
    const headers = { ...incoming.headers }
    delete headers['proxy-authorization']
    delete headers['proxy-connection']
    const method = incoming.method ?? 'GET'
    const label = `${method} ${url.pathname}`
    const started = Date.now()
    let bodyBytes = 0
    incoming.on('data', (chunk: Buffer) => { bodyBytes += chunk.length })
    let downstreamGone = false
    let settled = false
    let retried = false
    let upstream: ClientRequest
    // An upstream failure is never silent (#1494): the page gets a 502, or a broken connection
    // instead of a body cut short, and the fixture fails naming the cause.
    const fail = (error: NodeJS.ErrnoException, phase: string, reused: boolean) => {
      if (settled || downstreamGone || closed) return
      settled = true
      failures.push(`${label}: ${error.code ?? 'error'} (${error.message}) ${phase} on a ${reused ? 'reused' : 'new'} socket after ${Date.now() - started} ms${retried ? ', after one retry' : ''}.`)
      if (outgoing.headersSent) outgoing.destroy()
      else outgoing.writeHead(502).end()
    }
    const send = (fresh: boolean) => {
      // A retry takes its own connection, so it cannot land on another stale pooled socket.
      const attempt = request(url, fresh ? { method, headers: { ...headers, connection: 'close' }, agent: false } : { method, headers }, response => {
        response.on('error', error => fail(error, 'during the response body', attempt.reusedSocket))
        outgoing.writeHead(response.statusCode!, withoutHopByHop(response.headers))
        response.pipe(outgoing)
      })
      upstream = attempt
      // A pooled socket has read earlier responses; only bytes beyond this mark belong to this attempt.
      let bytesReadBefore = -1
      attempt.on('socket', socket => { bytesReadBefore = socket.bytesRead; trackSocket(socket) })
      attempt.on('error', (error: NodeJS.ErrnoException) => {
        if (attempt.res) { fail(error, 'during the response body', attempt.reusedSocket); return }
        // Node's documented keep-alive race (#1494): a stalled server's keep-alive timer closes a pooled connection
        // that already carries a request. The guard cannot know whether the server began processing it, so it
        // replays only what is safe either way: a bodyless GET/HEAD (idempotent) with zero response bytes, once.
        // That matches Node's documented retry for this race and the resend Chromium makes on its own connections.
        const responseBytes = attempt.socket && bytesReadBefore >= 0 ? attempt.socket.bytesRead - bytesReadBefore : -1
        if (!retried && !fresh && attempt.reusedSocket && error.code === 'ECONNRESET' && (method === 'GET' || method === 'HEAD')
          && responseBytes === 0 && incoming.readableEnded && bodyBytes === 0 && !downstreamGone && !closed && !outgoing.headersSent) {
          retried = true
          const recovery = `${label}: ECONNRESET (${error.message}) on a ${attempt.reusedSocket ? 'reused' : 'new'} socket after ${Date.now() - started} ms; retried once on a new connection`
          recovered.push(recovery)
          // One line per recovery, so job logs can count them.
          process.stderr.write(`[rendered-network-guard] recovered ${recovery}\n`)
          send(true).end()
          return
        }
        fail(error, `before the response headers (${Math.max(responseBytes, 0)} bytes received)`, attempt.reusedSocket)
      })
      return attempt
    }
    outgoing.on('close', () => {
      if (!outgoing.writableFinished) downstreamGone = true
      upstream.destroy()
    })
    incoming.pipe(send(false))
  })
  server.on('connection', trackSocket)
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
    allowed, record, unexpected, failures, recovered,
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
