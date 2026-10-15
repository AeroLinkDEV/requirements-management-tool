import { expect, logicTest as test } from './isolated-client-test'
import { recordWindowsAttempt, setStaySignedOut, shouldStartWindowsSignIn, staySignedOut, takeWindowsAttempt } from '../src/windowsSignIn'

// #1615 guard: with Windows sign-in on, arriving without a session signs in with Windows, but signing out must
// keep this tab signed out until the person explicitly signs in again; and neither a refusal nor a browser that
// cannot finish Negotiate may loop, so the password break-glass stays reachable.

test('automatic Windows sign-in stops after a sign-out, an outcome page or one failed attempt', () => {
  const values = new Map<string, string>()
  const sessionStorage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value) },
    removeItem: (key: string) => { values.delete(key) },
  }
  const scope = globalThis as unknown as { window?: unknown }
  const previous = scope.window
  scope.window = { sessionStorage }
  try {
    const on = { windowsSignIn: true }
    expect(shouldStartWindowsSignIn(on, '', staySignedOut())).toBe(true)
    expect(shouldStartWindowsSignIn({ windowsSignIn: false }, '', staySignedOut())).toBe(false)

    setStaySignedOut(true) // what Sign out does
    expect(shouldStartWindowsSignIn(on, '', staySignedOut())).toBe(false)

    setStaySignedOut(false) // what clicking "Sign in with Windows" or a password sign-in does
    expect(shouldStartWindowsSignIn(on, '', staySignedOut())).toBe(true)

    // Back from a refusal or mid-link: show the outcome instead of trying again.
    expect(shouldStartWindowsSignIn(on, '?windows=refused&code=no_email', staySignedOut())).toBe(false)
    expect(shouldStartWindowsSignIn(on, '?windows=link', staySignedOut())).toBe(false)
    expect(shouldStartWindowsSignIn(on, '?windows=password', staySignedOut())).toBe(false)

    // An automatic attempt that comes back without a session is not repeated on the next arrival, only after it.
    recordWindowsAttempt()
    expect(shouldStartWindowsSignIn(on, '', staySignedOut(), takeWindowsAttempt())).toBe(false)
    expect(shouldStartWindowsSignIn(on, '', staySignedOut(), takeWindowsAttempt())).toBe(true)
  } finally {
    scope.window = previous
  }
})
