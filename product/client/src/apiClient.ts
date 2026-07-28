export type ApiErrorBody = {
  code?: string
  error?: string
  message?: string
  [key: string]: unknown
}

export class ApiError extends Error {
  readonly status: number
  readonly code?: string
  readonly details?: ApiErrorBody

  constructor(message: string, status = 0, code?: string, details?: ApiErrorBody) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.details = details
  }
}

const safeFallback = (status: number) => {
  if (status === 401) return 'Your session has ended. Sign in and try again.'
  if (status === 403) return 'Your account does not have authority for this action.'
  if (status === 404) return 'The controlled record is no longer available.'
  if (status === 409) return 'This record changed in another session. Review the current version and try again.'
  if (status === 413) return 'The submitted content is larger than this installation allows.'
  if (status === 429) return 'AeroLink is receiving too many requests. Wait briefly and try again.'
  if (status >= 500) return 'AeroLink could not complete the operation. No success was recorded.'
  return 'AeroLink could not complete the operation.'
}

async function errorBody(response: Response): Promise<ApiErrorBody | undefined> {
  const contentType = response.headers.get('content-type') ?? ''
  if (!contentType.includes('json')) return undefined
  const value = await response.clone().json().catch(() => undefined)
  return value && typeof value === 'object' ? value as ApiErrorBody : undefined
}

export async function apiRequest<T = undefined>(
  input: RequestInfo | URL,
  init: RequestInit = {},
): Promise<T> {
  let response: Response
  try {
    response = await fetch(input, init)
  } catch {
    throw new ApiError(
      'AeroLink could not reach its local service. Your input has been preserved; try again when the connection is restored.',
    )
  }

  if (!response.ok) {
    const body = await errorBody(response)
    throw new ApiError(
      body?.error?.toString() || body?.message?.toString() || safeFallback(response.status),
      response.status,
      body?.code?.toString(),
      body,
    )
  }

  if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T
  const contentType = response.headers.get('content-type') ?? ''
  if (!contentType.includes('json')) return await response.text() as T
  return await response.json() as T
}

export function operationError(error: unknown, fallback: string) {
  if (error instanceof ApiError && error.message) return error.message
  if (error instanceof Error && error.message) return error.message
  return fallback
}

export function recordClientOperationFailure(operation: string, error: unknown) {
  const status = error instanceof ApiError ? error.status : 0
  const code = error instanceof ApiError ? error.code : undefined
  // Operation identifiers and transport outcomes are safe diagnostics. Never log request bodies,
  // credentials, controlled content, or server response payloads here.
  console.error('AeroLink operation failed', { operation, status, code })
}
