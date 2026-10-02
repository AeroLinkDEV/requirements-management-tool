import { expect, test as base } from '@playwright/test'
import { createRenderedNetworkGuard } from './rendered-network-guard'

export { expect }

const apiRequestContextError = 'A rendered fixture must not use an API request context.'
const apiRequestContextDiagnostic = 'rendered fixture used a forbidden API request context'

type RenderedFixtureState = {
  apiRequestViolations: string[]
  networkGuard: Awaited<ReturnType<typeof createRenderedNetworkGuard>>
}

export const logicTest = base.extend({
  browser: async ({ browserName: _browserName }, _provide) => { throw new Error('A logic test must not launch a browser.') },
  request: async ({ baseURL: _baseURL }, _provide) => { throw new Error('A logic test must not use an API request context.') },
})

export const renderedTest = base.extend<RenderedFixtureState>({
  networkGuard: async ({ baseURL }, provide) => {
    expect(baseURL, 'rendered fixtures require an isolated client origin').toBeTruthy()
    const guard = await createRenderedNetworkGuard(baseURL!)
    try { await provide(guard) } finally {
      await guard.close()
      expect(guard.unexpected, 'rendered fixture attempted API or external network access').toEqual([])
      expect(guard.failures, 'rendered fixture WebSocket transport failed').toEqual([])
    }
  },
  proxy: async ({ networkGuard }, provide) => { await provide(networkGuard.proxy) },
  apiRequestViolations: async ({ baseURL: _baseURL }, provide) => {
    const violations: string[] = []
    await provide(violations)
    expect(violations, apiRequestContextDiagnostic).toEqual([])
  },
  request: async ({ baseURL: _baseURL, apiRequestViolations }, _provide) => {
    apiRequestViolations.push('request fixture')
    throw new Error(apiRequestContextError)
  },
  // `page.request` is an APIRequestContext that bypasses browser routes and request events.
  page: async ({ page, apiRequestViolations }, provide) => {
    Object.defineProperty(page, 'request', {
      configurable: true,
      get: () => {
        apiRequestViolations.push('page.request')
        throw new Error(apiRequestContextError)
      },
    })
    await provide(page)
  },
  context: async ({ context, networkGuard, apiRequestViolations }, provide) => {
    await context.routeWebSocket('**/*', socket => networkGuard.connectWebSocket(socket))
    // Observe even fulfilled/mocked requests, which never reach the preventive proxy.
    // The proxy is configured before context creation and covers initial popup requests.
    context.on('request', request => {
      if (!networkGuard.allowed(request.url())) networkGuard.record(request.url())
    })
    // Playwright exposes the same APIRequestContext through page.request and context.request. Replace it
    // with a guard that permits only the internal dispose path and rejects every network method.
    const rawRequest = context.request
    Object.defineProperty(context, 'request', {
      configurable: true,
      get: () => new Proxy(rawRequest, {
        get: (target, property) => {
          if (property === 'dispose') return target.dispose.bind(target)
          apiRequestViolations.push(`context.request.${String(property)}`)
          throw new Error(apiRequestContextError)
        },
      }),
    })
    await provide(context)
  },
})
