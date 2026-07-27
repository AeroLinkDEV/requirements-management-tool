import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import './DesignSystem.css'
import './Density.css'
import WorkspaceErrorBoundary from './WorkspaceErrorBoundary.tsx'

const nativeFetch = window.fetch.bind(window)
let csrfToken: Promise<string> | undefined
const unsafe = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])
const address = (input: RequestInfo | URL) => typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url
const csrfExempt = (url: string) => url.includes('/api/auth/login') || url.includes('/api/setup/bootstrap')
const loadCsrf = async (url: string) => {
  const endpoint = new URL('/api/auth/csrf', url).toString()
  const response = await nativeFetch(endpoint, { credentials: 'include' })
  if (!response.ok) throw new Error('Unable to establish a protected browser session.')
  return (await response.json() as { token: string }).token
}

/**
 * Caches the token, not the attempt.
 *
 * `csrfToken ??= loadCsrf(url)` stored the promise, so a single failed fetch became a *rejected* promise that
 * `??=` would never replace — it is neither null nor undefined. Every later mutation in that tab then awaited
 * the same old rejection and threw before reaching the network, permanently, until the page was reloaded. One
 * momentary blip and the product stopped accepting work. The reset on line 32 below could not help: it keys
 * off a 400 response, and no request was ever sent to be answered.
 */
const csrfFor = (url: string) => {
  csrfToken ??= loadCsrf(url).catch(error => {
    csrfToken = undefined
    throw error
  })
  return csrfToken
}

window.fetch = async (input: RequestInfo | URL, init: RequestInit = {}) => {
  const url = address(input), method = (init.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase()
  const next: RequestInit = { ...init, credentials: init.credentials ?? 'include' }
  if (unsafe.has(method) && url.includes('/api/') && !csrfExempt(url)) {
    const headers = new Headers(init.headers ?? (input instanceof Request ? input.headers : undefined))
    headers.set('X-AeroLink-CSRF', await csrfFor(url))
    next.headers = headers
  }
  const response = await nativeFetch(input, next)
  if (response.status === 400 && response.headers.get('content-type')?.includes('json')) {
    const clone = response.clone(), body = await clone.json().catch(() => undefined) as { code?: string } | undefined
    if (body?.code === 'antiforgery_validation_failed') csrfToken = undefined
  }
  return response
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <WorkspaceErrorBoundary>
      <App />
    </WorkspaceErrorBoundary>
  </StrictMode>,
)
