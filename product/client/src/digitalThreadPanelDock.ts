import { useCallback, useState } from "react"

/** Where a detail panel sits. `auto` picks the side with less linked content; the rest are explicit. */
export type PanelDock = "auto" | "left" | "right" | "bottom"
export type ResolvedDock = Exclude<PanelDock, "auto">


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
): { dock: ResolvedDock; reportNeedsRoom: () => void } {
  const [narrowFor, setNarrowFor] = useState<string | null>(null)
  return {
    // Bottom keeps the full width, so it is the placement that can hold a wide one-hop set.
    dock: narrowFor === situation ? "bottom" : preferred,
    reportNeedsRoom: useCallback(() => setNarrowFor(situation), [situation]),
  }
}
