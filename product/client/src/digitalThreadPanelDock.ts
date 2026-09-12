import { useCallback, useLayoutEffect, useState } from "react"
import type { RefObject } from "react"

/** Where a detail panel sits. `auto` picks the side with less linked content; the rest are explicit. */
export type PanelDock = "auto" | "left" | "right" | "bottom"
export type ResolvedDock = Exclude<PanelDock, "auto">

export const PANEL_FALLBACK_WIDTH = 300 + 16 + 14
export const PANEL_FALLBACK_HEIGHT = 150 + 18 + 16

type FrameInset = { left?: number; right?: number; bottom?: number }


/**
 * Where a detail panel may rest, given that it must never cover a directly linked record.
 *
 * #880 §6.6 is a shared-canvas guarantee, and the canonical prototype's `checks.js` exercises it in the change
 * network as well as the artifact thread: for every dock mode, the selected record **and every direct link**
 * must be inside the panel-free frame. Since §10.1 stopped the board zooming out past the legibility floor to
 * make room, a side dock can no longer always leave enough width — and the answer is that the panel moves, not
 * that the record is hidden. A hidden linked record satisfies "not underneath the panel" only by making it not
 * present, which is the same failure wearing a different face.
 *
 * This lives in one place rather than three because it was wired into one view first and the other two kept
 * the defect. A fourth view that renders the panel gets the behaviour by using this hook, rather than by
 * remembering to reimplement it.
 *
 * `situation` is what the shortfall was observed for — the selection and the reader's preference. The flag is
 * resolved against it at render rather than cleared by an effect, because clearing it in an effect does not
 * work: child effects run before parent effects, so the reset lands *after* the canvas has already reported
 * the shortfall in the same commit and silently undoes it.
 *
 * #1022 narrows that guarantee to the record the reader actually selected, and makes the recovery two-axis:
 * a bottom dock takes height away from every lane window, while a side dock takes width. When the selected
 * record cannot be held at the preferred dock, the panel moves to the *other axis* once — bottom to a side,
 * a side to bottom — which is the placement that can restore the space it lost. Escalation is bounded to one
 * step per situation, so a board that cannot satisfy either axis cannot start a dock/zoom cycle.
 */
export function usePanelDock(
  preferred: ResolvedDock,
  situation: string,
  canvasHostRef?: RefObject<HTMLDivElement | null>,
): {
  dock: ResolvedDock
  reportNeedsRoom: () => void
  panelRef: (element: HTMLElement | null) => void
  frameInset?: FrameInset
} {
  const [escalatedFor, setEscalatedFor] = useState<string | null>(null)
  const [escalatedDock, setEscalatedDock] = useState<ResolvedDock | null>(null)
  const [panelElement, setPanelElement] = useState<HTMLElement | null>(null)
  const [measuredInset, setMeasuredInset] = useState<FrameInset | null>(null)
  /**
   * The recovery placement, chosen by measurement rather than by assuming the opposite axis helps.
   *
   * A bottom panel spends height; a side panel spends width. Whichever costs a *smaller share* of its own axis
   * is the one that leaves the board more usable, which is what the canvas needs when it reports that the
   * selected record does not fit. The panel's own measured size is used when available, so a tall relationship
   * list and a short one do not get the same answer.
   */
  const chooseRecovery = useCallback((current: ResolvedDock): ResolvedDock => {
    const canvas = canvasHostRef?.current?.querySelector<HTMLElement>(".dtCanvas")
    const canvasRect = canvas?.getBoundingClientRect()
    if (!canvasRect || !panelElement || canvasRect.width < 1 || canvasRect.height < 1) {
      return current === "bottom" ? "right" : "bottom"
    }
    const cardRect = canvas?.querySelector<HTMLElement>(".dtCanvasNode.is-selected")?.getBoundingClientRect()
    if (!cardRect) return current
    const toolbar = canvas?.querySelector<HTMLElement>(".dtCanvasControls")?.getBoundingClientRect()
    const heading = canvas?.querySelector<HTMLElement>(".dtCanvasLaneHead")
    const headingOffset = heading ? Math.max(0, -parseFloat(getComputedStyle(heading).top) || 0) : 0
    const top = Math.max(40, Math.ceil((toolbar?.bottom ?? canvasRect.top + 38) - canvasRect.top + headingOffset + 8))
    // Each candidate is measured with its own CSS in the same containing block. The inert, invisible clone
    // exists only during this synchronous measurement, and is removed before a frame can be displayed.
    const measureCandidate = (candidate: ResolvedDock) => {
      const probe = panelElement.cloneNode(true) as HTMLElement
      probe.className = probe.className.replace(/Panel-(bottom|left|right)/g, `Panel-${candidate}`)
      probe.inert = true
      probe.setAttribute("aria-hidden", "true")
      probe.style.visibility = "hidden"
      probe.style.pointerEvents = "none"
      probe.removeAttribute("id")
      probe.querySelectorAll("[id]").forEach(node => node.removeAttribute("id"))
      panelElement.parentElement!.appendChild(probe)
      try {
        const rect = probe.getBoundingClientRect()
        const width = canvasRect.width - (candidate === "bottom" ? 0 : candidate === "left"
          ? rect.right - canvasRect.left + 12 : canvasRect.right - rect.left + 12)
        const height = canvasRect.height - top - 64 - (candidate === "bottom" ? canvasRect.bottom - rect.top + 12 : 0)
        return { fits: cardRect.width + 24 <= width && cardRect.height + 24 <= height,
          room: Math.min(width / cardRect.width, height / cardRect.height) }
      } finally { probe.remove() }
    }
    const opposite = current === "bottom" ? "right" : "bottom"
    const own = measureCandidate(current)
    const other = measureCandidate(opposite)
    return own.fits ? current : other.fits || other.room > own.room ? opposite : current
  }, [canvasHostRef, panelElement])

  /** The one placement, measured once per situation: repeated reports cannot walk through more placements. */
  const dock: ResolvedDock = escalatedFor === situation && escalatedDock ? escalatedDock : preferred

  // The canvas and panel are siblings in each view. Measure their rendered rectangles instead of reserving a
  // guessed 300x150 box: selected cards and relationship lists can grow, and the free frame must follow them.
  useLayoutEffect(() => {
    if (!panelElement || !canvasHostRef) {
      setMeasuredInset(null)
      return undefined
    }
    const measure = () => {
      const canvas = canvasHostRef.current?.querySelector<HTMLElement>(".dtCanvas")
      if (!canvas) return
      const canvasRect = canvas.getBoundingClientRect()
      const panelRect = panelElement.getBoundingClientRect()
      if (canvasRect.width < 1 || canvasRect.height < 1 || panelRect.width < 1 || panelRect.height < 1) return
      const next: FrameInset = dock === "bottom"
        ? { bottom: Math.ceil(canvasRect.bottom - panelRect.top + 12) }
        : dock === "left"
          ? { left: Math.ceil(panelRect.right - canvasRect.left + 12) }
          : { right: Math.ceil(canvasRect.right - panelRect.left + 12) }
      setMeasuredInset(previous =>
        previous && Object.keys(next).every(key => previous[key as keyof FrameInset] === next[key as keyof FrameInset])
          ? previous
          : next,
      )
    }
    measure()
    const observer = typeof ResizeObserver === "function" ? new ResizeObserver(measure) : null
    observer?.observe(panelElement)
    if (canvasHostRef.current) observer?.observe(canvasHostRef.current)
    window.addEventListener("resize", measure)
    const timer = window.setTimeout(measure, 50)
    return () => {
      window.clearTimeout(timer)
      observer?.disconnect()
      window.removeEventListener("resize", measure)
    }
  }, [canvasHostRef, dock, panelElement])

  return {
    dock,
    /**
     * Bounded per situation, not for the lifetime of the hook.
     *
     * Repeated reports for the same situation are idempotent (the state already holds it), while a genuinely
     * new situation replaces it and becomes eligible for its own supported recovery. Retaining the first
     * situation forever silently denied every later selection its fallback.
     */
    reportNeedsRoom: useCallback(() => {
      setEscalatedFor(current => {
        if (current === situation) return current
        setEscalatedDock(chooseRecovery(preferred))
        return situation
      })
    }, [chooseRecovery, preferred, situation]),
    panelRef: useCallback((element: HTMLElement | null) => setPanelElement(element), []),
    frameInset: panelElement
      ? measuredInset ?? (dock === "bottom"
        ? { bottom: PANEL_FALLBACK_HEIGHT }
        : dock === "left"
          ? { left: PANEL_FALLBACK_WIDTH }
          : { right: PANEL_FALLBACK_WIDTH })
      : undefined,
  }
}
