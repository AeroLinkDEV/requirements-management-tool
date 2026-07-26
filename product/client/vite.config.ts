import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    // One stylesheet, even though the code is split into fourteen chunks.
    //
    // Splitting the CSS as well takes about two thirds off the first stylesheet, and it was measured doing
    // exactly that. It is not enabled because a chunk's stylesheet is appended when the chunk loads, so the
    // order of two on-demand stylesheets depends on which page the reader opened first — and this client has
    // pairs that share a class name and are told apart only by which one loaded last. The reversals against
    // the always-loaded stylesheets were found and fixed by specificity instead of order, which is what makes
    // the fourteen chunks safe; the remaining chunk-against-chunk pairs are not, and a build that renders
    // differently depending on where somebody navigated first is not worth 190 kB.
    //
    // This flag only affects `vite build`. The dev server always injects a module's CSS when the module
    // evaluates, so development already behaves like a split build — the stricter case, and the one the
    // browser journeys run against.
    cssCodeSplit: false,
  },
})
