import { createContext, useContext, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

const StationDocument = createContext<Document | null>(null);

/** DOM-bound effects follow the displayed surface; the simulation remains in the bench owner. */
export const useFmsStationDocument = () => useContext(StationDocument) ?? document;

/**
 * The portal container never changes identity. Moving it preserves the component tree and unfinished
 * instructor/CDU drafts, while the explicit document context rebinds destination-bound effects.
 */
export function FmsStationSurface({ targetWindow, className, children }: {
  targetWindow?: Window | null; className?: string; children: ReactNode;
}) {
  const anchor = useRef<HTMLDivElement>(null);
  const [container] = useState(() => document.createElement("div"));
  let destination = document;
  try { if (targetWindow && !targetWindow.closed) destination = targetWindow.document; }
  catch { /* A navigated child is returned by its pagehide handler; keep the owner usable in the meantime. */ }
  const detached = destination !== document;

  useLayoutEffect(() => {
    const outlet = detached ? destination.querySelector<HTMLElement>("[data-fms-station-outlet]") : anchor.current;
    if (!outlet) return;
    container.className = className ?? "";
    container.dataset.fmsStationSurface = "";
    container.dataset.location = detached ? "child" : "docked";
    // Flatten the extra portal element into the existing cockpit grid when docked.
    container.style.display = detached ? "" : "contents";
    outlet.appendChild(container);
    destination.querySelector("[data-fms-station-loading]")?.remove();
    return () => { container.remove(); };
  }, [container, destination, detached, className]);

  return <>
    <div ref={anchor} style={{ display: "contents" }} />
    {createPortal(<StationDocument.Provider value={destination}>{children}</StationDocument.Provider>, container)}
  </>;
}
