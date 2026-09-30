import { shadeTile } from "./outTheWindow";

/**
 * Shades ground tiles for the out-the-window view (outTheWindow.ts `shadeTile`) off the page's main thread, in a
 * worker: shading a tile takes a few milliseconds, and the view asks for them in bursts of dozens, which on the main
 * thread froze the page for hundreds of milliseconds at a time. Where a worker cannot be started (no Worker, or the
 * browser refuses it), the tile is shaded in place, as before.
 */
export type ReliefShader = { shade: (heights: Float32Array, cellMetres: number) => Promise<Uint8ClampedArray<ArrayBuffer>>; dispose: () => void };

/** Shades on the calling thread: the fallback, and what tests use. */
export const inlineReliefShader = (): ReliefShader => ({ shade: async (heights, cellMetres) => shadeTile(heights, cellMetres), dispose: () => {} });

export function workerReliefShader(start: () => Worker = () => new Worker(new URL("./reliefWorker.ts", import.meta.url), { type: "module" })): ReliefShader {
  let worker: Worker;
  try {
    worker = start();
  } catch {
    return inlineReliefShader();
  }
  let next = 0, failed = false;
  const pending = new Map<number, { heights: Float32Array; cellMetres: number; resolve: (rgba: Uint8ClampedArray<ArrayBuffer>) => void }>();
  worker.onmessage = ({ data }: MessageEvent<{ id: number; rgba: Uint8ClampedArray<ArrayBuffer> }>) => {
    pending.get(data.id)?.resolve(data.rgba);
    pending.delete(data.id);
  };
  // A worker that fails (it could not load, or threw) is given up: what it had and everything after is shaded here.
  worker.onerror = event => {
    event.preventDefault();
    failed = true;
    worker.terminate();
    for (const job of pending.values()) job.resolve(shadeTile(job.heights, job.cellMetres));
    pending.clear();
  };
  return {
    shade: (heights, cellMetres) => {
      if (failed) return Promise.resolve(shadeTile(heights, cellMetres));
      return new Promise(resolve => {
        const id = next++;
        pending.set(id, { heights, cellMetres, resolve });
        // A copy goes to the worker, so the shared height tile (terrainTiles.ts) stays usable here.
        const copy = heights.slice();
        worker.postMessage({ id, heights: copy, cellMetres }, [copy.buffer]);
      });
    },
    dispose: () => { worker.terminate(); pending.clear(); },
  };
}
