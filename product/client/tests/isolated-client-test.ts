import { expect, test as base } from '@playwright/test'

export { expect }

export const logicTest = base.extend({
  browser: async ({ browserName: _browserName }, _provide) => { throw new Error('A logic test must not launch a browser.') },
  request: async ({ baseURL: _baseURL }, _provide) => { throw new Error('A logic test must not use an API request context.') },
})

export const renderedTest = base.extend({
  request: async ({ baseURL: _baseURL }, _provide) => { throw new Error('A rendered fixture must not use an API request context.') },
  context: async ({ context, baseURL }, provide) => {
    expect(baseURL, 'rendered fixtures require an isolated client origin').toBeTruthy()
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
    await provide(context)
    expect(unexpected, 'rendered fixture attempted API or external network access').toEqual([])
  },
})
