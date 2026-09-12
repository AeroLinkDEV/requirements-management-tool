import { useCallback, useLayoutEffect, useState } from "react"
import type { RefObject } from "react"

/** Where a detail panel sits. `auto` picks the side with less linked content; the rest are explicit. */
export type PanelDock = "auto" | "left" | "right" | "bottom"
export type ResolvedDock = Exclude<PanelDock, "auto">

export const PANEL_FALLBACK_WIDTH = 300 + 16 + 14
export const PANEL_FALLBACK_HEIGHT = 150 + 18 + 16

type FrameInset = { left?: number; right?: number; bottom?: number }

/**
 * Measure the inspector's real constraint and recover to the other dock axis only when the selected record
 * cannot fit. Each candidate is measured under its own CSS; no candidate borrows the current dock's size.
 * Recovery is bounded once per selection/preference situation to prevent dock/zoom/measurement loops.
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
