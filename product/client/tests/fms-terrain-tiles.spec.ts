import { expect, logicTest as test } from './isolated-client-test'
import { TERRAIN_TILE_CAPACITY, TerrainTiles, tileOf, type TerrainSource } from '../src/fmsCdu/terrainTiles'

// The bench's shared height tiles (terrainTiles.ts): fetched once, decoded once, sampled by point for the PFD's
// synthetic vision, and the source's status reported so a display can say when there is no terrain.

// Tiles travel as raw Terrarium RGBA here; the decoder only has to hand the bytes back.
const rawDecoder = async (image: Blob) => new Uint8Array(await image.arrayBuffer())
const tile = (metres: (x: number, y: number) => number) => {
  const rgba = new Uint8Array(256 * 256 * 4)
  for (let y = 0; y < 256; y++) for (let x = 0; x < 256; x++) {
    const code = metres(x, y) + 32768, i = (y * 256 + x) * 4
    rgba[i] = Math.floor(code / 256); rgba[i + 1] = Math.floor(code % 256); rgba[i + 2] = Math.round((code % 1) * 256); rgba[i + 3] = 255
  }
  return new Response(rgba, { status: 200, headers: { 'content-type': 'image/png' } })
}
const settle = () => new Promise(resolve => setTimeout(resolve, 0))

test('a point maps to its Web Mercator tile and the fraction across it', () => {
  expect(tileOf(0, 0, 1)).toMatchObject({ x: 1, y: 1, fx: 0, fy: 0 })
  const montreal = tileOf(45.5, -73.7, 11)
  expect(montreal).toMatchObject({ x: 604, y: 732 })
  expect(montreal.fx).toBeGreaterThanOrEqual(0)
  expect(montreal.fx).toBeLessThan(1)
})

test('heights are fetched once per tile, sampled bilinearly, and null until the tile arrives', async () => {
  const asked: string[] = []
  const source: TerrainSource = async (z, x, y) => { asked.push(`${z}/${x}/${y}`); return tile(x => x * 2) }
  const tiles = new TerrainTiles(source, rawDecoder)
  let changes = 0
  tiles.subscribe(() => { changes++ })

  expect(tiles.heightAt(45.5, -73.7, 11)).toBeNull()
  expect(tiles.heightAt(45.5, -73.7, 11)).toBeNull()
  await tiles.load(11, 604, 732)
  await settle()
  const { fx } = tileOf(45.5, -73.7, 11)
  expect(tiles.heightAt(45.5, -73.7, 11)).toBeCloseTo(fx * 256 * 2, 3)
  expect(asked).toEqual(['11/604/732'])
  expect(tiles.status).toBe('live')
  expect(changes).toBeGreaterThan(0)
})

test('an installation with the relay off says so, and that status sticks', async () => {
  let off = true
  const source: TerrainSource = async () => off
    ? new Response(JSON.stringify({ code: 'terrain_relay_disabled' }), { status: 404, headers: { 'content-type': 'application/json' } })
    : tile(() => 100)
  const tiles = new TerrainTiles(source, rawDecoder)
  expect(await tiles.load(5, 9, 11)).toBeNull()
  expect(tiles.status).toBe('off')
  off = false
  await tiles.load(5, 9, 12)
  expect(tiles.status).toBe('off')
})

test('a failing source is unreachable, a missing tile is not a failure, and a later tile brings it back', async () => {
  let mode: 'fail' | 'missing' | 'ok' = 'fail'
  const source: TerrainSource = async () => {
    if (mode === 'fail') throw new TypeError('network')
    return mode === 'missing' ? new Response(null, { status: 404 }) : tile(() => 50)
  }
  const tiles = new TerrainTiles(source, rawDecoder)
  expect(await tiles.load(5, 1, 1)).toBeNull()
  expect(tiles.status).toBe('unreachable')
  mode = 'missing'
  expect(await tiles.load(5, 1, 2)).toBeNull()
  expect(tiles.status).toBe('unreachable')
  mode = 'ok'
  expect((await tiles.load(5, 1, 3))?.[0]).toBe(50)
  expect(tiles.status).toBe('live')
})

test('the tiles kept are bounded: the least recently used are dropped and fetched again when needed', async () => {
  const asked: string[] = []
  const source: TerrainSource = async (z, x, y) => { asked.push(`${z}/${x}/${y}`); return tile(() => x) }
  const tiles = new TerrainTiles(source, rawDecoder, 2)
  await tiles.load(9, 1, 1)
  await tiles.load(9, 2, 1)
  // Using the first again makes the second the least recently used.
  await tiles.load(9, 1, 1)
  await tiles.load(9, 3, 1)
  expect(tiles.size).toBe(2)
  expect(asked).toEqual(['9/1/1', '9/2/1', '9/3/1'])
  // The first is still held; the second was dropped and is fetched again.
  expect((await tiles.load(9, 1, 1))?.[0]).toBe(1)
  expect((await tiles.load(9, 2, 1))?.[0]).toBe(2)
  expect(asked).toEqual(['9/1/1', '9/2/1', '9/3/1', '9/2/1'])
  expect(tiles.size).toBe(2)
})

test('a point read counts as a use, and a tile still loading is never dropped', async () => {
  const asked: string[] = []
  let release: (() => void) | null = null
  const source: TerrainSource = async (z, x, y) => {
    asked.push(`${z}/${x}/${y}`)
    if (x === 9) await new Promise<void>(resolve => { release = resolve })
    return tile(() => x)
  }
  const tiles = new TerrainTiles(source, rawDecoder, 2)
  await tiles.load(11, 604, 732)
  await tiles.load(11, 605, 732)
  // Reading a height from the first makes it recent: the second goes when a third arrives.
  expect(tiles.heightAt(45.5, -73.7, 11)).not.toBeNull()
  await tiles.load(11, 606, 732)
  await tiles.load(11, 604, 732)
  expect(asked.filter(key => key === '11/604/732')).toHaveLength(1)
  // A slow tile, the oldest when the capacity is exceeded, is never dropped while it loads: settled tiles go instead,
  // and once it arrives it is held, not fetched again.
  const slow = tiles.load(11, 9, 732)
  await tiles.load(11, 607, 732)
  await tiles.load(11, 608, 732)
  expect(tiles.size).toBe(2)
  release!()
  expect((await slow)?.[0]).toBe(9)
  await settle()
  const again = tiles.load(11, 9, 732)
  expect(asked.filter(key => key === '11/9/732'), 'fetched once').toHaveLength(1)
  expect((await again)?.[0]).toBe(9)
})

test('by default a few hundred tiles are kept (about 100 MB of heights), not every tile ever passed over', () => {
  expect(TERRAIN_TILE_CAPACITY).toBe(384)
  expect((TERRAIN_TILE_CAPACITY * 256 * 256 * 4) / 1e6).toBeLessThan(110)
})
