export const auditSurface = (minimum: number) => {
  const visible = (el: Element) => {
    // Closed details can retain descendant geometry in Chromium. A box alone does
    // not mean the text is painted; check skipped content and CSS visibility too.
    if (!el.checkVisibility({ visibilityProperty: true })) return false
    const box = el.getBoundingClientRect()
    return box.width > 0 && box.height > 0
  }
  const label = (el: Element) => (el.textContent || '').trim().slice(0, 26)
  // Narrow scaled-canvas exception documented by the owning design-system spec.
  const scaled = (el: Element) => el.closest('.dtCanvasScene') !== null
  const readable = [...document.querySelectorAll('body *')]
    .filter(el => visible(el) && !el.children.length && (el.textContent || '').trim().length > 0)
  const tiny = readable
    .filter(el => !scaled(el) && parseFloat(getComputedStyle(el).fontSize) < minimum)
    .map(el => `${label(el)} @ ${getComputedStyle(el).fontSize}`)
  // Browser-default chrome: grey background, square corners, no author styling.
  const unstyled = [...document.querySelectorAll('button')]
    .filter(el => visible(el) && getComputedStyle(el).backgroundColor === 'rgb(239, 239, 239)')
    .map(label)
  // WCAG 2.2 SC 2.5.8 Target Size (Minimum): 24x24 CSS pixels, which compact must not trade away.
  const smallTargets = [...document.querySelectorAll('button, a[href], select, input:not([type=hidden])')]
    .filter(el => {
      if (!visible(el) || (el as HTMLButtonElement).disabled) return false
      // Same narrow exception, same reason: a target inside the scaled canvas is measured in scene units the
      // reader can zoom, not in fixed CSS pixels. Everything outside it still owes 24x24.
      if (scaled(el)) return false
      const box = el.getBoundingClientRect()
      return box.height < 24 || box.width < 24
    })
    .map(el => `${el.tagName.toLowerCase()} "${label(el)}" ${Math.round(el.getBoundingClientRect().width)}x${Math.round(el.getBoundingClientRect().height)}`)
  return {
    tiny: [...new Set(tiny)],
    unstyled: [...new Set(unstyled)],
    smallTargets: [...new Set(smallTargets)],
    overflow: document.documentElement.scrollWidth > window.innerWidth + 1,
    crashed: (document.querySelector('body')?.textContent || '').trim().length < 40,
    contentHeight: document.documentElement.scrollHeight,
  }
}
