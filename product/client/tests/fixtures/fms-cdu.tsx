import { createRoot } from 'react-dom/client'
import '../../src/index.css'
import FmsCduTestBench from '../../src/fmsCdu/FmsCduTestBench'
import type { ImagerySource } from '../../src/fmsCdu/groundImagery'
import type { TerrainSource } from '../../src/fmsCdu/terrainTiles'

// The out-the-window view's terrain, made here instead of fetched: every tile is one smooth 900 m hill, Terrarium
// encoded (height = R × 256 + G + B / 256 − 32768). `?terrain=off` answers as an installation with the relay off.
// `?hill=1300` raises the hill (metres) above the aircraft, for the terrain colouring.
const hillMetres = Number(new URLSearchParams(window.location.search).get('hill') ?? 900)
const hill = (() => {
  let tile: Promise<Blob> | null = null
  return () => tile ??= new Promise<Blob>(resolve => {
    const canvas = Object.assign(document.createElement('canvas'), { width: 256, height: 256 })
    const pen = canvas.getContext('2d')!, image = pen.createImageData(256, 256)
    for (let y = 0; y < 256; y++) for (let x = 0; x < 256; x++) {
      const code = hillMetres * Math.exp(-((x - 128) ** 2 + (y - 128) ** 2) / 3000) + 32768, i = (y * 256 + x) * 4
      image.data[i] = Math.floor(code / 256); image.data[i + 1] = Math.floor(code % 256); image.data[i + 3] = 255
    }
    pen.putImageData(image, 0, 0)
    canvas.toBlob(blob => resolve(blob!), 'image/png')
  })
})()

const terrainOff = new URLSearchParams(window.location.search).get('terrain') === 'off'
const terrain: TerrainSource = async () => terrainOff
  ? new Response(JSON.stringify({ code: 'terrain_relay_disabled' }), { status: 404, headers: { 'content-type': 'application/json' } })
  : new Response(await hill(), { status: 200, headers: { 'content-type': 'image/png' } })

// The ground imagery, made here too: every tile a checkerboard of two colours no relief or sky uses (teal and
// violet), so a rendered test can tell imagery on the ground. `?imagery=off` answers as an installation with the relay
// off, `none` as outside the coverage (404), `blank` as the service's white filler tile.
const tileImage = (paint: (pen: CanvasRenderingContext2D) => void) => new Promise<Blob>(resolve => {
  const canvas = Object.assign(document.createElement('canvas'), { width: 256, height: 256 })
  paint(canvas.getContext('2d')!)
  canvas.toBlob(blob => resolve(blob!), 'image/png')
})
const checker = (() => {
  let tile: Promise<Blob> | null = null
  return () => tile ??= tileImage(pen => {
    for (let y = 0; y < 8; y++) for (let x = 0; x < 8; x++) { pen.fillStyle = (x + y) % 2 ? '#1fb5a8' : '#7a3fd0'; pen.fillRect(x * 32, y * 32, 32, 32) }
  })
})()
const blank = (() => { let tile: Promise<Blob> | null = null; return () => tile ??= tileImage(pen => { pen.fillStyle = '#ffffff'; pen.fillRect(0, 0, 256, 256) }) })()
const imageryMode = new URLSearchParams(window.location.search).get('imagery')
const imagery: ImagerySource = async () =>
  imageryMode === 'off' ? new Response(JSON.stringify({ code: 'imagery_relay_disabled' }), { status: 404, headers: { 'content-type': 'application/json' } })
    : imageryMode === 'none' ? new Response(null, { status: 404 })
      : new Response(await (imageryMode === 'blank' ? blank() : checker()), { status: 200, headers: { 'content-type': 'image/png' } })

createRoot(document.getElementById('root')!).render(<FmsCduTestBench terrain={terrain} imagery={imagery} />)
