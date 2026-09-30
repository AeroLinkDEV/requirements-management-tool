import { shadeTile } from "./outTheWindow";

/**
 * Shades the out-the-window view's ground tiles away from the page's main thread (reliefShader.ts): the heights come
 * in, the RGBA tile goes back, both transferred rather than copied.
 */
type Request = { id: number; heights: Float32Array; cellMetres: number };

const scope = self as unknown as { onmessage: ((event: MessageEvent<Request>) => void) | null; postMessage: (message: unknown, transfer: Transferable[]) => void };
scope.onmessage = ({ data }) => {
  const rgba = shadeTile(data.heights, data.cellMetres);
  scope.postMessage({ id: data.id, rgba }, [rgba.buffer]);
};
