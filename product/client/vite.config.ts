import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { cpSync, createReadStream, statSync } from 'node:fs'
import { extname, join, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * The CesiumJS engine (the FMS Test Bench's out-the-window view) loads its web workers and some data files at run
 * time from CESIUM_BASE_URL, `/cesium/`, rather than through the bundle. A build copies them to dist/cesium so the
 * server that serves the client serves them too: nothing comes from a CDN (DEC-047), and the document's
 * `worker-src 'self'` admits them. The development server answers the same paths from node_modules.
 *
 * The bench imports `@cesium/engine`, not the `cesium` package: that one also brings Cesium's widgets, whose
 * Knockout evaluates a string as script when it loads, which the document's `script-src 'self'` refuses.
 */
const cesiumEngine = fileURLToPath(new URL('./node_modules/@cesium/engine/', import.meta.url))
const cesiumFolders: Record<string, string> = {
  Workers: join(cesiumEngine, 'Build', 'Workers'),
  ThirdParty: join(cesiumEngine, 'Build', 'ThirdParty'),
  Assets: join(cesiumEngine, 'Source', 'Assets'),
}
const cesiumTypes: Record<string, string> = {
  '.js': 'text/javascript', '.json': 'application/json', '.png': 'image/png', '.jpg': 'image/jpeg', '.wasm': 'application/wasm',
  '.xml': 'application/xml', '.ktx2': 'image/ktx2',
}

function cesiumRuntimeFiles(): Plugin {
  let outDir = 'dist', building = false
  return {
    name: 'aerolink-cesium-runtime-files',
    configResolved(config) { outDir = resolve(config.root, config.build.outDir); building = config.command === 'build' },
    configureServer(server) {
      server.middlewares.use('/cesium', (request, response, next) => {
        const [folder, ...rest] = decodeURIComponent((request.url ?? '').split('?')[0]).replace(/^\/+/, '').split('/')
        const root = cesiumFolders[folder]
        const file = root ? resolve(root, ...rest) : ''
        if (!root || !file.startsWith(root + sep) || !statSync(file, { throwIfNoEntry: false })?.isFile()) return next()
        response.setHeader('Content-Type', cesiumTypes[extname(file)] ?? 'application/octet-stream')
        createReadStream(file).pipe(response)
      })
    },
    // The development server also calls this as it closes; only a build has a dist to copy into.
    closeBundle() {
      if (!building) return
      for (const [folder, source] of Object.entries(cesiumFolders)) {
        cpSync(source, join(outDir, 'cesium', folder), { recursive: true })
      }
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), cesiumRuntimeFiles()],
  resolve: {
    // Only the Cesium engine imports meshoptimizer, and its WebAssembly cannot compile under the document's policy
    // (see the stub for why the view does not need it).
    alias: [{ find: /^meshoptimizer$/, replacement: fileURLToPath(new URL('./src/fmsCdu/meshoptUnavailable.ts', import.meta.url)) }],
  },
  server: {
    watch: {
      // Playwright writes traces, screenshots, videos and reports into test-results/ and
      // playwright-report/ inside this tree while the dev server is serving the browser journeys.
      // On Windows chokidar hits EBUSY on a file Playwright still holds and takes the dev server
      // down mid-sweep, which reports every remaining journey as connection-refused — a wall of
      // failures that says nothing about the code under test. The watcher churn is also a measured
      // share of sweep wall clock. Ignored explicitly rather than moved: the journeys legitimately
      // run against this root, and a rerun artifact directory belongs to no module graph. The same
      // globs cover blob-report/, which sharded runs merge from.
      ignored: ['**/test-results/**', '**/playwright-report/**', '**/blob-report/**'],
    },
  },
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
