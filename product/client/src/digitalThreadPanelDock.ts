import { useCallback, useEffect, useState } from "react"
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
  const [narrowFor, setNarrowFor] = useState<string | null>(null)
  const [panelElement, setPanelElement] = useState<HTMLElement | null>(null)
  const [measuredInset, setMeasuredInset] = useState<FrameInset | null>(null)
  const dock: ResolvedDock = narrowFor === situation ? "bottom" : preferred

  // The canvas and panel are siblings in each view. Measure their rendered rectangles instead of reserving a
  // guessed 300x150 box: selected cards and relationship lists can grow, and the free frame must follow them.
  useEffect(() => {
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
    // Bottom keeps the full width, so it is the placement that can hold a wide directed story.
    dock,
    reportNeedsRoom: useCallback(() => setNarrowFor(situation), [situation]),
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
