import { useCallback, useEffect, useRef, useState } from "react";
import { flushSync } from "react-dom";
import { mirrorStationStyles } from "./stationStyles";

export type StationSurfaceId = "outside" | "cockpit" | "instructor";
type SurfaceWindows = Partial<Record<StationSurfaceId, Window>>;
type SurfaceErrors = Partial<Record<StationSurfaceId, string>>;
type Placement = { left: number; top: number; width: number; height: number };
type OwnedWindow = { window: Window; dispose: () => void; savePlacement: () => void; openedFrom: HTMLElement | null };
const TITLES: Record<StationSurfaceId, string> = { outside: "Out the window", cockpit: "Cockpit", instructor: "Instructor station" };
const DEFAULT_SIZE: Record<StationSurfaceId, { width: number; height: number }> = {
  outside: { width: 1200, height: 800 }, cockpit: { width: 1440, height: 900 }, instructor: { width: 720, height: 900 },
};
const placementKey = (scope: string, id: StationSurfaceId) => `aerolink.fmsCdu.station.${encodeURIComponent(scope)}.${id}`;
const validPlacement = (value: unknown): value is Placement => {
  if (!value || typeof value !== "object") return false;
  const item = value as Placement;
  return [item.left, item.top, item.width, item.height].every(Number.isFinite)
    && Math.abs(item.left) <= 100_000 && Math.abs(item.top) <= 100_000
    && item.width >= 320 && item.width <= 16_000 && item.height >= 240 && item.height <= 16_000;
};

/** One owned child per slot; opening a slot must be called directly from its user's click. */
export function useFmsStationWindows({ scopeKey }: { scopeKey: string }) {
  // Names identify this mounted bench, not another tab belonging to the same signed-in user.
  const [identity] = useState(() => [...crypto.getRandomValues(new Uint32Array(4))].map(part => part.toString(16).padStart(8, "0")).join(""));
  const owned = useRef(new Map<StationSurfaceId, OwnedWindow>());
  const mounted = useRef(false);
  const [windows, setWindows] = useState<SurfaceWindows>({});
  const [opening, setOpening] = useState<StationSurfaceId[]>([]);
  const [errors, setErrors] = useState<SurfaceErrors>({});

  const returnSurface = useCallback((id: StationSurfaceId) => {
    const entry = owned.current.get(id);
    if (!entry) return;
    owned.current.delete(id);
    entry.savePlacement();
    const focused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (mounted.current) {
      // Commit the stable surface's adoption back into the owner before closing its document. This also
      // rescues that same node during a native child's pagehide, retaining uncontrolled form state.
      flushSync(() => {
        setWindows(previous => { const next = { ...previous }; delete next[id]; return next; });
        setOpening(previous => previous.filter(item => item !== id));
      });
    }
    entry.dispose();
    if (!entry.window.closed) entry.window.close();
    if (mounted.current) {
      const focusable = (node: HTMLElement | null): node is HTMLElement => !!node && node !== document.body
        && node !== document.documentElement && node.isConnected && !node.matches(":disabled") && node.getClientRects().length > 0;
      const fallback = document.querySelector<HTMLElement>("[data-fms-station-return-focus]");
      const restore = focusable(focused) ? focused : focusable(entry.openedFrom) ? entry.openedFrom : fallback;
      restore?.focus();
    }
  }, []);
  const returnAll = useCallback(() => {
    for (const id of [...owned.current.keys()]) returnSurface(id);
  }, [returnSurface]);

  useEffect(() => {
    mounted.current = true;
    setWindows({});
    setOpening([]);
    setErrors({});
    const ownerGone = () => returnAll();
    window.addEventListener("pagehide", ownerGone);
    // pagehide is the primary close signal; this also detects children closed without delivering it.
    const check = window.setInterval(() => {
      for (const [id, entry] of owned.current) if (entry.window.closed) returnSurface(id);
    }, 500);
    return () => {
      mounted.current = false;
      window.removeEventListener("pagehide", ownerGone);
      window.clearInterval(check);
      returnAll();
    };
  }, [returnAll, returnSurface, scopeKey]);

  const openSurface = useCallback((id: StationSurfaceId) => {
    const existing = owned.current.get(id);
    if (existing && !existing.window.closed) { existing.window.focus(); return true; }
    if (existing) returnSurface(id);
    const openedFrom = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    let placement: Placement | null = null;
    try { const saved: unknown = JSON.parse(window.localStorage.getItem(placementKey(scopeKey, id)) ?? "null"); if (validPlacement(saved)) placement = saved; } catch { /* optional preference */ }
    const size = placement ?? DEFAULT_SIZE[id];
    const features = `popup=yes,width=${Math.round(size.width)},height=${Math.round(size.height)}${placement ? `,left=${Math.round(placement.left)},top=${Math.round(placement.top)}` : ""}`;
    // Keep the actual popup attempt synchronous; stylesheet readiness must not consume the gesture first.
    let child: Window | null = null;
    try { child = window.open("", `aerolink-fms-${identity}-${id}`, features); }
    catch { /* A browser policy may throw instead of returning null for a refused popup. */ }
    if (!child || child.closed) {
      setErrors(previous => ({ ...previous, [id]: "The browser blocked this station window. Allow pop-ups for AeroLink, then try this step again." }));
      return false;
    }
    setErrors(previous => { const next = { ...previous }; delete next[id]; return next; });
    setOpening(previous => [...previous.filter(item => item !== id), id]);

    const doc = child.document;
    doc.title = `AeroLink FMS · ${TITLES[id]}`;
    doc.body.dataset.fmsStationChild = id;
    Object.assign(doc.body.style, { margin: "0", minWidth: "0", height: "100vh", display: "flex", flexDirection: "column" });
    const viewport = doc.createElement("meta"); viewport.name = "viewport"; viewport.content = "width=device-width, initial-scale=1"; doc.head.appendChild(viewport);
    const header = doc.createElement("header"); header.className = "fmsStationWindowHeader";
    Object.assign(header.style, { display: "flex", flex: "0 0 auto", alignItems: "center", gap: "12px", padding: "8px 12px" });
    const title = doc.createElement("strong"); title.textContent = TITLES[id];
    const back = doc.createElement("button"); back.type = "button"; back.textContent = "Return to bench";
    const manual = doc.createElement("span"); manual.textContent = "Move this window to your screen. Use the browser's full-screen command if wanted.";
    const outlet = doc.createElement("main"); outlet.className = "fmsStationWindowOutlet"; outlet.dataset.fmsStationOutlet = "";
    Object.assign(outlet.style, { flex: "1 1 auto", minHeight: "0", padding: "0" });
    const loading = doc.createElement("p"); loading.dataset.fmsStationLoading = ""; loading.textContent = "Opening station window… The panel remains in the bench until its styles are ready.";
    outlet.appendChild(loading); header.append(title, back, manual); doc.body.append(header, outlet);

    const savePlacement = () => {
      try {
        const value = { left: child.screenX, top: child.screenY, width: child.innerWidth, height: child.innerHeight };
        if (validPlacement(value)) window.localStorage.setItem(placementKey(scopeKey, id), JSON.stringify(value));
      } catch { /* Browsers may ignore placement or restrict access after a child navigates. */ }
    };
    const closed = () => returnSurface(id);
    back.addEventListener("click", closed);
    child.addEventListener("pagehide", closed);
    child.addEventListener("blur", savePlacement);
    let styles: ReturnType<typeof mirrorStationStyles> | null = null;
    const entry: OwnedWindow = {
      window: child, savePlacement, openedFrom,
      dispose: () => {
        back.removeEventListener("click", closed);
        child.removeEventListener("pagehide", closed);
        child.removeEventListener("blur", savePlacement);
        styles?.dispose();
      },
    };
    owned.current.set(id, entry);
    const failed = (error: Error) => {
      if (owned.current.get(id) !== entry) return;
      setErrors(previous => ({ ...previous, [id]: error.message }));
      returnSurface(id);
    };
    try {
      styles = mirrorStationStyles(document, doc, failed);
      void styles.ready.then(() => {
        if (!mounted.current || owned.current.get(id) !== entry || child.closed) return;
        setWindows(previous => ({ ...previous, [id]: child }));
        setOpening(previous => previous.filter(item => item !== id));
        child.focus();
      }).catch(error => failed(error instanceof Error ? error : new Error(String(error))));
    } catch (error) { failed(error instanceof Error ? error : new Error(String(error))); }
    return true;
  }, [identity, returnSurface, scopeKey]);

  return { windows, opening, errors, openSurface, returnSurface, returnAll };
}
