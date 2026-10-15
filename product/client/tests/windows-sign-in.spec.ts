import { expect, logicTest as test } from './isolated-client-test'
import { setStaySignedOut, shouldStartWindowsSignIn, staySignedOut } from '../src/windowsSignIn'

// #1615 guard: with Windows sign-in on, arriving without a session signs in with Windows, but signing out must
// keep this tab signed out until the person explicitly signs in again; and a refusal must not loop.

test('sign-out keeps the tab out of automatic Windows sign-in until an explicit sign-in clears it', () => {
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
  } finally {
    scope.window = previous
  }
})
