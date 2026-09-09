import { expect, test as base } from '@playwright/test'

export { expect }

const apiRequestContextError = 'A rendered fixture must not use an API request context.'
const apiRequestContextDiagnostic = 'rendered fixture used a forbidden API request context'

type RenderedFixtureState = {
  apiRequestViolations: string[]
}

export const logicTest = base.extend({
  browser: async ({ browserName: _browserName }, _provide) => { throw new Error('A logic test must not launch a browser.') },
  request: async ({ baseURL: _baseURL }, _provide) => { throw new Error('A logic test must not use an API request context.') },
})

export const renderedTest = base.extend<RenderedFixtureState>({
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
  context: async ({ context, baseURL, apiRequestViolations }, provide) => {
    expect(baseURL, 'rendered fixtures require an isolated client origin').toBeTruthy()
    // Derived from the active Playwright config: Full uses its own client port and Fast may override 5188.
    const origin = new URL(baseURL!).origin
    const unexpected: string[] = []
    const allowed = (url: string) => {
      const parsed = new URL(url)
      return ['data:', 'blob:'].includes(parsed.protocol)
        || (parsed.origin === origin && !/^\/api(?:\/|$)/i.test(parsed.pathname))
    }
    // Observe even fulfilled/mocked requests. Abort unhandled external/API access
    // before it reaches a service, and fail even if the component swallows the error.
    context.on('request', request => {
      if (!allowed(request.url())) unexpected.push(request.url())
    })
    await context.route('**/*', route => allowed(route.request().url()) ? route.continue() : route.abort())
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
    expect(unexpected, 'rendered fixture attempted API or external network access').toEqual([])
  },
})
