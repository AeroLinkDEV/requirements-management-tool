// Windows sign-in from the browser's side (#1615).
//
// Windows sign-in is a top-level navigation, not a fetch: the browser has to answer the server's Negotiate
// challenge itself, and the server ends the exchange with a redirect that carries the new session cookie.
// When Windows sign-in is on, the app tries it on arrival — unless the person signed out in this tab, in which
// case they stay signed out until they ask to sign in again. Otherwise signing out would sign them straight
// back in.

const STAY_SIGNED_OUT = 'aerolink.staySignedOut'

function storage(): Storage | undefined {
  try { return window.sessionStorage } catch { return undefined }
}

export function staySignedOut(): boolean {
  try { return storage()?.getItem(STAY_SIGNED_OUT) === '1' } catch { return false }
}

export function setStaySignedOut(value: boolean) {
  try {
    if (value) storage()?.setItem(STAY_SIGNED_OUT, '1')
    else storage()?.removeItem(STAY_SIGNED_OUT)
  } catch { /* A tab without storage simply tries Windows sign-in again. */ }
}

/** The address that signs in with Windows and comes back to `returnTo`, or that starts linking an account. */
export function windowsSignInUrl(api: string, options: { returnTo?: string; link?: boolean } = {}): string {
  const query = options.link
    ? '?intent=link'
    : options.returnTo && options.returnTo.startsWith('/') && !options.returnTo.startsWith('//')
      ? `?returnTo=${encodeURIComponent(options.returnTo)}`
      : ''
  return `${api}/api/auth/windows${query}`
}

const ATTEMPT_PENDING = 'aerolink.windowsAttemptPending'

/** Marks that this tab is about to try Windows sign-in on its own. */
export function recordWindowsAttempt() {
  try { storage()?.setItem(ATTEMPT_PENDING, '1') } catch { /* see setStaySignedOut */ }
}

/**
 * Whether the previous arrival tried Windows sign-in on its own and came back without a session, clearing the
 * mark either way. A browser that cannot finish Negotiate (an off-domain machine, a cancelled prompt, a refused
 * cookie) therefore lands on the sign-in page the second time instead of being sent round again.
 */
export function takeWindowsAttempt(): boolean {
  try {
    const pending = storage()?.getItem(ATTEMPT_PENDING) === '1'
    storage()?.removeItem(ATTEMPT_PENDING)
    return pending
  } catch { return false }
}

/**
 * Whether arriving without a session should go straight to Windows sign-in. Never after a sign-out in this
 * tab, never twice in a row, and never while the page is reporting the outcome of a Windows sign-in (or was
 * sent to the password form), which would loop on a refusal.
 */
export function shouldStartWindowsSignIn(status: { windowsSignIn?: boolean }, search: string, signedOut: boolean, attemptPending = false): boolean {
  if (!status.windowsSignIn || signedOut || attemptPending) return false
  return !new URLSearchParams(search).has('windows')
}

/** What the sign-in page says after Windows sign-in redirected back to it. */
export function windowsOutcome(search: string): { kind: 'link' } | { kind: 'refused'; message: string } | undefined {
  const params = new URLSearchParams(search)
  const outcome = params.get('windows')
  if (outcome === 'link') return { kind: 'link' }
  if (outcome === 'refused') return { kind: 'refused', message: windowsRefusalMessage(params.get('code') ?? '') }
  return undefined
}

export function windowsRefusalMessage(code: string): string {
  switch (code) {
    case 'foreign_domain': return 'Your Windows account belongs to a domain that is not allowed to sign in to AeroLink.'
    case 'machine_account': return 'Computer and managed service accounts cannot sign in to AeroLink. Sign in with your own domain account.'
    case 'builtin_account': return 'Local, built-in and shared Windows accounts cannot sign in to AeroLink. Sign in with your own domain account.'
    case 'no_email': return 'Your Windows account has no email address in the directory, so AeroLink cannot create an account for it. Ask IT to add one.'
    case 'not_in_directory': return 'AeroLink could not find your Windows account in the directory.'
    case 'directory_unavailable': return 'AeroLink could not reach the directory to confirm your Windows account. Try again shortly.'
    case 'setup_incomplete': return 'AeroLink has not been set up yet. The administrator must finish first-install setup before anyone signs in with Windows.'
    case 'account_disabled': return 'Your AeroLink account is disabled. Ask an AeroLink administrator to enable it.'
    case 'account_locked': return 'Your AeroLink account is locked. Ask an AeroLink administrator to release it.'
    case 'account_name_in_use': return 'Another AeroLink account already uses your Windows account name. Ask an AeroLink administrator to resolve it.'
    default: return 'Windows sign-in was refused.'
  }
}
