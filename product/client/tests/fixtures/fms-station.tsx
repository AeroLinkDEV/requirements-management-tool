import { createRoot } from 'react-dom/client'
import '../../src/index.css'
import FmsCduTestBench from '../../src/fmsCdu/FmsCduTestBench'
import type { ImagerySource } from '../../src/fmsCdu/groundImagery'
import type { TerrainSource } from '../../src/fmsCdu/terrainTiles'

// Station lifecycle runs in isolation. Its real view receives declared relay-off replies without calling an API.
const terrain: TerrainSource = async () => new Response(JSON.stringify({ code: 'terrain_relay_disabled' }),
  { status: 404, headers: { 'content-type': 'application/json' } })
const imagery: ImagerySource = async () => new Response(JSON.stringify({ code: 'imagery_relay_disabled' }),
  { status: 404, headers: { 'content-type': 'application/json' } })

// The public signed-in-user prop exercises actual preference scoping; no test-only production seam.
createRoot(document.getElementById('root')!).render(<FmsCduTestBench userName="station-rendered-user" terrain={terrain} imagery={imagery} />)
