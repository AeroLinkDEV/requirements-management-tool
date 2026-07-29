import { expect, test } from '@playwright/test'
import { apiLogin, login, selectProgram } from './auth'

/**
 * WCAG 2.2 AA contrast, measured on rendered pixels rather than asserted in a document.
 *
 * SC 1.4.3 Contrast (Minimum) requires 4.5:1 for body text and 3:1 for large text, where large means at
 * least 24px, or at least 18.66px when bold. Disabled controls are exempt under the same criterion.
 *
 * Elements sitting on a gradient or image are reported separately rather than counted as passes: the
 * effective background cannot be resolved from computed style alone, so calling them conformant would be
 * a guess. They are listed so the number is visible instead of silently absorbed.
 */
const surfaces = [
  ['Command Center', '/command-center'],
  ['My Work', '/my-work'],
  ['Requirements Explorer', '/systems/requirements'],
  ['Change Requests', '/systems/change-requests'],
  ['Verification', '/system-verification'],
  ['Digital Thread', '/traceability'],
  ['Release Readiness', '/release-readiness'],
  ['People & Authority', '/administration'],
  ['Enterprise Control', '/enterprise-control'],
] as const

const auditContrast = () => {
  const parse = (value: string): [number, number, number, number] | null => {
    const match = value.match(/rgba?\(([^)]+)\)/)
    if (!match) return null
    const parts = match[1].split(/[,/]/).map(x => parseFloat(x.trim()))
    if (parts.length < 3 || parts.some(Number.isNaN)) return null
    return [parts[0], parts[1], parts[2], parts.length > 3 ? parts[3] : 1]
  }
  const over = (top: [number, number, number, number], bottom: [number, number, number]): [number, number, number] => {
    const a = top[3]
    return [top[0] * a + bottom[0] * (1 - a), top[1] * a + bottom[1] * (1 - a), top[2] * a + bottom[2] * (1 - a)]
  }
  const luminance = ([r, g, b]: [number, number, number]) => {
    const channel = (raw: number) => {
      const c = raw / 255
      return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
    }
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)
  }
  const ratio = (a: [number, number, number], b: [number, number, number]) => {
    const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x)
    return (light + 0.05) / (dark + 0.05)
  }

  /** Walks ancestors for the first opaque background, compositing translucent layers on the way. */
  const backgroundOf = (start: Element): { colour: [number, number, number] } | { unresolved: string } => {
    const stack: [number, number, number, number][] = []
    let node: Element | null = start
    while (node) {
      const style = getComputedStyle(node)
      if (style.backgroundImage && style.backgroundImage !== 'none') return { unresolved: style.backgroundImage.slice(0, 30) }
      const colour = parse(style.backgroundColor)
      if (colour && colour[3] > 0) {
        stack.push(colour)
        if (colour[3] === 1) {
          let base: [number, number, number] = [colour[0], colour[1], colour[2]]
          for (let i = stack.length - 2; i >= 0; i--) base = over(stack[i], base)
          return { colour: base }
        }
      }
      node = node.parentElement
    }
    let base: [number, number, number] = [255, 255, 255]
    for (let i = stack.length - 1; i >= 0; i--) base = over(stack[i], base)
    return { colour: base }
  }

  const failures: string[] = []
  const unresolved: string[] = []

  const candidates = [...document.querySelectorAll('body *')].filter(el => {
    if (el.children.length) return false
    const text = (el.textContent || '').trim()
    if (!text) return false
    const box = el.getBoundingClientRect()
    if (box.width <= 0 || box.height <= 0) return false
    const style = getComputedStyle(el)
    if (style.visibility === 'hidden' || style.opacity === '0') return false
    // SC 1.4.3 exempts inactive controls.
    const control = el.closest('button, input, select, textarea, [aria-disabled=true]') as HTMLButtonElement | null
    if (control && (control.disabled || control.getAttribute('aria-disabled') === 'true')) return false
    return true
  })

  for (const el of candidates) {
    const style = getComputedStyle(el)
    const foreground = parse(style.color)
    if (!foreground) continue
    const background = backgroundOf(el)
    const text = (el.textContent || '').trim().slice(0, 30)
    if ('unresolved' in background) { unresolved.push(`${text} on ${background.unresolved}`); continue }

    const composited = foreground[3] < 1 ? over(foreground, background.colour) : [foreground[0], foreground[1], foreground[2]] as [number, number, number]
    const size = parseFloat(style.fontSize)
    const weight = parseInt(style.fontWeight, 10) || 400
    const large = size >= 24 || (size >= 18.66 && weight >= 700)
    const required = large ? 3 : 4.5
    const measured = ratio(composited as [number, number, number], background.colour)
    if (measured + 0.005 < required) {
      const hex = (c: [number, number, number]) => '#' + c.map(v => Math.round(v).toString(16).padStart(2, '0')).join('')
      // Keyed by the colour pair, not the element: one grey used in forty places is one thing to fix.
      failures.push(`${hex(composited as [number, number, number])} on ${hex(background.colour)} — ${measured.toFixed(2)}:1, needs ${required}:1 (${size}px/${weight}) e.g. "${text}"`)
    }
  }

  return { failures: [...new Set(failures)], unresolved: [...new Set(unresolved)].length }
}

test('every surface meets WCAG 2.2 AA contrast in both densities', async ({ page, request }) => {
  test.setTimeout(360_000)
  await page.setViewportSize({ width: 1440, height: 900 })
  await apiLogin(request)
  await login(page)
  await selectProgram(page, 'Flight Management System Live Program')

  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, '')
  // colour pair -> the surfaces it fails on, so the report names things to fix rather than symptoms.
  const byPair = new Map<string, Set<string>>()
  let unresolvedTotal = 0

  for (const density of ['comfortable', 'compact'] as const) {
    await page.evaluate(value => localStorage.setItem('aerolink-density', value), density)
    await page.reload({ waitUntil: 'load' })
    await page.waitForTimeout(400)

    for (const [name, path] of surfaces) {
      await page.goto(new URL(root + path, page.url()).toString(), { waitUntil: 'load' })
      await page.waitForTimeout(1000)
      const report = await page.evaluate(auditContrast)
      unresolvedTotal += report.unresolved
      for (const failure of report.failures) {
        if (!byPair.has(failure)) byPair.set(failure, new Set())
        byPair.get(failure)!.add(`${name} [${density}]`)
      }
    }
  }

  console.log(`Contrast: ${unresolvedTotal} element(s) sat on a gradient or image and were not machine-checkable.`)
  const pairs = [...byPair.entries()].map(([pair, where]) => `${pair}  —  ${where.size} surface(s): ${[...where].slice(0, 3).join(', ')}`)
  expect(pairs, `WCAG 2.2 AA contrast failures, ${pairs.length} distinct colour pair(s):\n  ${pairs.join('\n  ')}`).toEqual([])
})
