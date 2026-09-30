import { expect, logicTest as test } from './isolated-client-test'
import { BLANK_SAMPLE_PIXELS, GroundImagery, IMAGERY_MAX_ZOOM, isBlankTile, type ImageryDecoder, type ImagerySource } from '../src/fmsCdu/groundImagery'
import {
  ABSOLUTE_BANDS_FT, ABSOLUTE_RGB, CAUTION_RGB, DANGER_RGB, RELATIVE_CAUTION_FT, RELATIVE_DANGER_FT, TERRAIN_COLOURINGS, awarenessColour,
} from '../src/fmsCdu/terrainAwareness'

// The out-the-window view's ground imagery (groundImagery.ts: the USGS orthoimagery through the server's relay, the
// United States only, relief elsewhere) and its terrain colouring (terrainAwareness.ts: relative to the aircraft, or
// absolute height bands). Tiles travel as raw bytes here; the decoder hands back a label and a sample to judge.

const sample = (rgb: [number, number, number]) => {
  const out = new Uint8Array(BLANK_SAMPLE_PIXELS * BLANK_SAMPLE_PIXELS * 4)
  for (let i = 0; i < out.length; i += 4) { out[i] = rgb[0]; out[i + 1] = rgb[1]; out[i + 2] = rgb[2]; out[i + 3] = 255 }
  return out
}
// The decoder reads the body as text: 'photo', 'white' (the service's blank filler), or 'broken'.
const decoder: ImageryDecoder<string> = async tile => {
  const kind = await tile.text()
  if (kind === 'broken') throw new Error('not an image')
  return { image: kind, sample: kind === 'white' ? sample([255, 255, 255]) : sample([60, 90, 50]) }
}
const jpeg = (body: string) => new Response(body, { status: 200, headers: { 'content-type': 'image/jpeg' } })

test('a tile with imagery comes back as its image, and the source says it is live', async () => {
  const asked: string[] = []
  const source: ImagerySource = async (z, x, y) => { asked.push(`${z}/${x}/${y}`); return jpeg('photo') }
  const imagery = new GroundImagery(source, decoder)
  expect(imagery.status).toBe('waiting')
  expect(await imagery.load(15, 9725, 11855)).toBe('photo')
  expect(asked).toEqual(['15/9725/11855'])
  expect(imagery.status).toBe('live')
})

test('no imagery (outside the coverage, the blank filler, a tile that will not decode) is null, so relief is drawn there', async () => {
  let answer: () => Response = () => new Response(null, { status: 404 })
  const imagery = new GroundImagery(async () => answer(), decoder)
  expect(await imagery.load(12, 2046, 1362)).toBeNull()
  expect(imagery.status).toBe('waiting')
  answer = () => jpeg('white')
  expect(await imagery.load(12, 1209, 1465)).toBeNull()
  answer = () => jpeg('broken')
  await expect(imagery.load(12, 1209, 1466)).resolves.toBeNull()
  answer = () => jpeg('photo')
  expect(await imagery.load(12, 1209, 1467)).toBe('photo')
})

test('deeper than the imagery is published, nothing is asked for', async () => {
  const asked: string[] = []
  const imagery = new GroundImagery(async (z, x, y) => { asked.push(`${z}/${x}/${y}`); return jpeg('photo') }, decoder)
  expect(IMAGERY_MAX_ZOOM).toBe(16)
  expect(await imagery.load(IMAGERY_MAX_ZOOM + 1, 0, 0)).toBeNull()
  expect(asked).toEqual([])
  expect(await imagery.load(IMAGERY_MAX_ZOOM, 0, 0)).toBe('photo')
})

test('an installation with imagery off says so, stops asking, and that sticks; a failing source is unreachable until tiles come', async () => {
  let mode: 'off' | 'fail' | 'ok' = 'off'
  const asked: string[] = []
  const source: ImagerySource = async (z, x, y) => {
    asked.push(`${z}/${x}/${y}`)
    if (mode === 'fail') throw new TypeError('network')
    return mode === 'off' ? new Response(JSON.stringify({ code: 'imagery_relay_disabled' }), { status: 404 }) : jpeg('photo')
  }
  const off = new GroundImagery(source, decoder)
  expect(await off.load(5, 9, 11)).toBeNull()
  expect(off.status).toBe('off')
  mode = 'ok'
  expect(await off.load(5, 9, 12)).toBeNull()
  expect(off.status).toBe('off')
  expect(asked).toEqual(['5/9/11'])

  // A tile already on its way when the relay says off does not turn it back on.
  let release: (() => void) | null = null
  const racing = new GroundImagery<string>(async (z, x) => {
    if (x === 1) return new Response(JSON.stringify({ code: 'imagery_relay_disabled' }), { status: 404 })
    await new Promise<void>(resolve => { release = resolve })
    return jpeg('photo')
  }, decoder)
  const slow = racing.load(5, 2, 0)
  expect(await racing.load(5, 1, 0)).toBeNull()
  expect(racing.status).toBe('off')
  release!()
  await slow
  expect(racing.status).toBe('off')

  mode = 'fail'
  const failing = new GroundImagery(source, decoder)
  let changes = 0
  failing.subscribe(() => { changes++ })
  expect(await failing.load(5, 9, 11)).toBeNull()
  expect(failing.status).toBe('unreachable')
  mode = 'ok'
  expect(await failing.load(5, 9, 12)).toBe('photo')
  expect(failing.status).toBe('live')
  expect(changes).toBe(2)
})

test('a blank filler tile is flat near-white; real imagery, snow included, has texture', () => {
  expect(isBlankTile(sample([255, 255, 255]))).toBe(true)
  expect(isBlankTile(sample([250, 251, 249]))).toBe(true)
  const snow = sample([245, 247, 250])
  expect(isBlankTile(snow)).toBe(false)
  const mostlyWhite = sample([255, 255, 255]); mostlyWhite[40] = 200
  expect(isBlankTile(mostlyWhite)).toBe(false)
  expect(isBlankTile([])).toBe(false)
})

test('relative colouring: red at or above 100 ft below the aircraft, amber within 500 ft, nothing lower', () => {
  expect([RELATIVE_DANGER_FT, RELATIVE_CAUTION_FT]).toEqual([100, 500])
  const at = (ground: number) => awarenessColour('relative', ground, 3000)
  expect(at(3200)).toEqual(DANGER_RGB)
  expect(at(2900)).toEqual(DANGER_RGB)
  expect(at(2899)).toEqual(CAUTION_RGB)
  expect(at(2500)).toEqual(CAUTION_RGB)
  expect(at(2499)).toBeNull()
  expect(awarenessColour('off', 3200, 3000)).toBeNull()
})

test('absolute colouring: five bands above mean sea level, each edge in the band above it', () => {
  expect(ABSOLUTE_BANDS_FT).toEqual([500, 1000, 2000, 3000])
  expect(ABSOLUTE_RGB).toHaveLength(ABSOLUTE_BANDS_FT.length + 1)
  const band = (feet: number) => ABSOLUTE_RGB.indexOf(awarenessColour('absolute', feet, 0)!)
  expect([0, 499, 500, 999, 1000, 1999, 2000, 2999, 3000, 9000].map(band)).toEqual([0, 0, 1, 1, 2, 2, 3, 3, 4, 4])
  // The aircraft's altitude does not matter in absolute mode.
  expect(awarenessColour('absolute', 1500, 100)).toEqual(awarenessColour('absolute', 1500, 9000))
  expect(TERRAIN_COLOURINGS).toEqual(['off', 'relative', 'absolute'])
})
