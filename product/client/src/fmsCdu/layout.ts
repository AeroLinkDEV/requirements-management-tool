import { useEffect, useState } from "react";
import type { CduFunction } from "./variants";

type Rect = { x: number; y: number; w: number; h: number };
export type CduLayout = {
  image: { w: number; h: number; pxPerMm: number };
  screen: Rect & { lines: number; columns: number };
  keys: (Rect & { id: string; kind: "rect" | "round" | "lsk"; role: string })[];
  annunciators: (Rect & { id: string })[];
};

export type CduKeyEvent = { keyId: string; fn: CduFunction; held: boolean; at: Date };

export const CDU_ASSETS = `${import.meta.env.BASE_URL}fms-cdu/`;

/** Loads the rendered panel's geometry. The images and this file come from product/tools/AeroLink.FmsCduModel. */
export function useCduLayout() {
  const [layout, setLayout] = useState<CduLayout | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let live = true;
    fetch(`${CDU_ASSETS}layout.json`)
      .then(response => { if (!response.ok) throw new Error(String(response.status)); return response.json() as Promise<CduLayout>; })
      .then(value => { if (live) setLayout(value); })
      .catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, []);
  return { layout, failed };
}

