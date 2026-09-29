import { createRoot } from 'react-dom/client'
import '../../src/index.css'
import FmsCduTestBench from '../../src/fmsCdu/FmsCduTestBench'
import type { TerrainSource } from '../../src/fmsCdu/FmsOutTheWindow'

// The out-the-window view's terrain, made here instead of fetched: every tile is one smooth 900 m hill, Terrarium
// encoded (height = R × 256 + G + B / 256 − 32768). `?terrain=off` answers as an installation with the relay off.
const hill = (() => {
  let tile: Promise<Blob> | null = null
  return () => tile ??= new Promise<Blob>(resolve => {
    const canvas = Object.assign(document.createElement('canvas'), { width: 256, height: 256 })
    const pen = canvas.getContext('2d')!, image = pen.createImageData(256, 256)
    for (let y = 0; y < 256; y++) for (let x = 0; x < 256; x++) {
      const code = 900 * Math.exp(-((x - 128) ** 2 + (y - 128) ** 2) / 3000) + 32768, i = (y * 256 + x) * 4
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

createRoot(document.getElementById('root')!).render(<FmsCduTestBench terrain={terrain} />)
