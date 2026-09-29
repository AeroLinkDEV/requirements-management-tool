/**
 * Stands in for the `meshoptimizer` package inside the Cesium engine (vite.config.ts aliases it here).
 *
 * meshoptimizer compiles WebAssembly as soon as it is imported, and the document's `script-src 'self'` refuses
 * WebAssembly compilation, so each import was an unhandled error in the page. Cesium uses the decoder only for
 * meshopt-compressed glTF models and 3D Tiles terrain, which the out-the-window view never loads: it builds its terrain
 * from height tiles. If something ever does ask, it fails with this reason instead of a policy error.
 */
const unavailable = () => {
  throw new Error("meshopt decoding is not available in AeroLink (its WebAssembly is refused by the page's policy).");
};

export const MeshoptDecoder = {
  supported: false,
  ready: Promise.resolve(),
  useWorkers: () => undefined,
  decodeVertexBuffer: unavailable,
  decodeIndexBuffer: unavailable,
  decodeIndexSequence: unavailable,
  decodeGltfBuffer: unavailable,
  decodeGltfBufferAsync: unavailable,
};
