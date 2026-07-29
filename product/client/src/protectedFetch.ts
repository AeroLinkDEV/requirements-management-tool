const nativeFetch = window.fetch.bind(window)
let csrfToken: Promise<string> | undefined
const unsafe = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

const address = (input: RequestInfo | URL) =>
  typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url

const canonical = (input: RequestInfo | URL) => new URL(address(input), window.location.origin)
const csrfExempt = (url: URL) =>
  url.pathname === '/api/auth/login' || url.pathname === '/api/setup/bootstrap'
const isApi = (url: URL) => url.pathname.startsWith('/api/')
const routeBuildContext = () => {
  const match = window.location.pathname.match(/^\/programs\/[^/]+\/projects\/[^/]+\/releases\/([^/]+)/i)
  return match ? decodeURIComponent(match[1]) : undefined
}

const loadCsrf = async (requestUrl: URL) => {
  const endpoint = new URL('/api/auth/csrf', requestUrl)
  const response = await nativeFetch(endpoint, { credentials: 'include' })
  if (!response.ok) throw new Error('Unable to establish a protected browser session.')
  return (await response.json() as { token: string }).token
}

const csrfFor = (requestUrl: URL) => {
  csrfToken ??= loadCsrf(requestUrl).catch(error => {
    csrfToken = undefined
    throw error
  })
  return csrfToken
}

export function clearMutationSession() {
  csrfToken = undefined
}

export async function refreshMutationSession(apiUrl: string | URL = window.location.origin) {
  clearMutationSession()
  await csrfFor(new URL(apiUrl, window.location.origin))
}

async function antiforgeryFailure(response: Response) {
  if (response.status !== 400 || !response.headers.get('content-type')?.includes('json')) return false
  const body = await response.clone().json().catch(() => undefined) as { code?: string } | undefined
  return body?.code === 'antiforgery_validation_failed'
}

export function installProtectedFetch() {
  window.fetch = async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = canonical(input)
    const method = (init.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase()
    const next: RequestInit = { ...init, credentials: init.credentials ?? 'include' }
    const retryInput = input instanceof Request ? input.clone() : input
    const headers = new Headers(init.headers ?? (input instanceof Request ? input.headers : undefined))
    const buildContext = isApi(url) ? routeBuildContext() : undefined
    if (buildContext) headers.set('X-AeroLink-Build-Context', buildContext)

    if (unsafe.has(method) && isApi(url) && !csrfExempt(url)) {
      headers.set('X-AeroLink-CSRF', await csrfFor(url))
    }
    if ([...headers].length) next.headers = headers

    let response = await nativeFetch(input, next)

    // An antiforgery rejection proves the controlled operation did not run. Re-establish once and retry the
    // exact request; arbitrary failures are never retried because a non-idempotent action might have committed.
    if (unsafe.has(method) && isApi(url) && !csrfExempt(url) && await antiforgeryFailure(response)) {
      clearMutationSession()
      const headers = new Headers(next.headers)
      headers.set('X-AeroLink-CSRF', await csrfFor(url))
      response = await nativeFetch(retryInput, { ...next, headers })
    }

    if (url.pathname === '/api/auth/login' && response.ok) {
      // The login response establishes a new cookie. Do not make protected controls actionable until the
      // matching browser mutation token is ready for that session.
      await refreshMutationSession(url)
    } else if (url.pathname === '/api/auth/logout' || response.status === 401) {
      clearMutationSession()
    } else if (url.pathname === '/api/auth/password' && response.ok) {
      clearMutationSession()
    }

    return response
  }
}
