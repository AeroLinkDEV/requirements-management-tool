import { expect, logicTest as test } from './isolated-client-test'
import {
  FT, TILE_PIXELS, WATER, ancestorOf, blendAircraft, cameraPose, decodeTerrarium, pixelMetres, rampColour, routeHeights,
  sampleHeights, shadeTile, tileLatitude, type AircraftSample,
} from '../src/fmsCdu/outTheWindow'

// The pure half of the out-the-window view (outTheWindow.ts): the terrain decode and the ground colouring drawn from
// it, and the camera that follows the flight model. The Cesium half is proved rendered (fms-out-the-window-rendered).

const terrarium = (metres: number) => { const code = metres + 32768; return [Math.floor(code / 256), Math.floor(code % 256), Math.round((code % 1) * 256), 255] }
const grid = (height: (x: number, y: number) => number) => {
  const heights = new Float32Array(TILE_PIXELS * TILE_PIXELS)
  for (let y = 0; y < TILE_PIXELS; y++) for (let x = 0; x < TILE_PIXELS; x++) heights[y * TILE_PIXELS + x] = height(x, y)
  return heights
}
const pixel = (rgba: Uint8ClampedArray, x: number, y: number) => Array.from(rgba.slice((y * TILE_PIXELS + x) * 4, (y * TILE_PIXELS + x) * 4 + 3))
const level: AircraftSample = { position: { lat: 45.5, lon: -73.7 }, altitude: 3000, heading: 90, pitch: 0, bank: 0 }

test('Terrarium pixels decode to metres, below sea level included', () => {
  const heights = decodeTerrarium([...terrarium(0), ...terrarium(1234.5), ...terrarium(-50), ...terrarium(8848)])
  expect(Array.from(heights)).toEqual([0, 1234.5, -50, 8848])
})

test('a tile deeper than the source reads its ancestor, at the right corner and span', () => {
  expect(ancestorOf(12, 2410, 2918, 13)).toEqual({ z: 12, x: 2410, y: 2918, offsetX: 0, offsetY: 0, span: 1 })
  // Level 15 is four times finer than 13: tile (9643, 11675) is the fourth column and second row of (2410, 2918).
  expect(ancestorOf(15, 9643, 11673, 13)).toEqual({ z: 13, x: 2410, y: 2918, offsetX: 0.75, offsetY: 0.25, span: 0.25 })
})

test('heights are sampled bilinearly from the part of the tile asked for, and the sea is level', () => {
  const ramp = grid(x => x * 10 - 100)
  const whole = sampleHeights(ramp, 0, 0, 1, 3)
  expect(Array.from(whole.slice(0, 3))).toEqual([0, 1175, 2450])
  // The right half of the same tile: from the middle column to the last.
  const right = sampleHeights(ramp, 0.5, 0, 0.5, 2)
  expect(right[0]).toBeCloseTo(1175, 3)
  expect(right[1]).toBeCloseTo(2450, 3)
})

test('the ground is coloured by height, shaded by slope from the north-west, and level water is blue', () => {
  expect(rampColour(0)).toEqual([74, 112, 62])
  expect(rampColour(5000)).toEqual([236, 240, 242])
  expect(rampColour(125)).toEqual([85, 118, 66])

  // A ridge running north-south: its west face looks toward the light, its east face away.
  const ridge = shadeTile(grid(x => 500 - Math.abs(x - 128) * 4), 20)
  const west = pixel(ridge, 100, 128), east = pixel(ridge, 156, 128)
  expect(west[0]).toBeGreaterThan(east[0])
  expect(west[1]).toBeGreaterThan(east[1])

  // A lake: a perfectly level surface above sea level, among hills.
  const lake = shadeTile(grid((x, y) => (Math.hypot(x - 128, y - 128) < 40 ? 200 : 200 + Math.hypot(x - 128, y - 128) - 40)), 20)
  const water = pixel(lake, 128, 128)
  expect(water[2]).toBeGreaterThan(water[1])
  // The water colour, evenly lit: each channel is the same fraction of it.
  const lit = water.map((value, index) => value / WATER[index])
  expect(Math.max(...lit) - Math.min(...lit)).toBeLessThan(0.03)
  // Sea level is not taken for a lake: a flat tile at 0 m is lowland green.
  expect(pixel(shadeTile(grid(() => 0), 20), 128, 128)[1]).toBeGreaterThan(pixel(shadeTile(grid(() => 0), 20), 128, 128)[2])
})

test('a pixel covers less ground further north and at a deeper zoom', () => {
  expect(pixelMetres(0, 0)).toBeCloseTo(156_543, 0)
  expect(pixelMetres(13, 45) / pixelMetres(13, 0)).toBeCloseTo(Math.SQRT1_2, 6)
  expect(pixelMetres(14, 45)).toBeCloseTo(pixelMetres(13, 45) / 2, 6)
  expect(tileLatitude(1, 0)).toBeGreaterThan(60)
  expect(tileLatitude(1, 1)).toBeLessThan(-60)
})

test('the cockpit camera sits at the aircraft, looks along the heading, pitches with the flight path and rolls with the bank', () => {
  const climbing = { ...level, pitch: 4, bank: -15 }
  const hud = cameraPose(climbing, 'cockpit', 'hud')
  expect(hud).toMatchObject({ longitude: -73.7, latitude: 45.5, height: 3000 * FT })
  expect(hud.heading).toBeCloseTo(Math.PI / 2, 9)
  expect(hud.roll).toBeCloseTo((-15 * Math.PI) / 180, 9)
  // Looking a little below the flight path, and less so through the panel layout's short window.
  expect(hud.pitch).toBeLessThan((4 * Math.PI) / 180)
  expect(cameraPose(climbing, 'cockpit', 'panel').pitch).toBeGreaterThan(hud.pitch)
  expect(cameraPose(climbing, 'cockpit', 'panel').pitch).toBeLessThan((4 * Math.PI) / 180)
})

test('the chase camera is behind and above the aircraft, and the map looks straight down', () => {
  const chase = cameraPose(level, 'chase', 'hud')
  // Heading east, so behind is west, at the same latitude.
  expect(chase.longitude).toBeLessThan(level.position.lon)
  expect(chase.latitude).toBeCloseTo(level.position.lat, 9)
  expect(chase.height).toBeGreaterThan(3000 * FT)
  expect(chase.roll).toBe(0)
  const map = cameraPose({ ...level, bank: 25 }, 'map', 'panel')
  expect(map.pitch).toBeCloseTo(-Math.PI / 2, 9)
  expect(map.roll).toBe(0)
  expect(map.height).toBeGreaterThan(30_000 * FT)
})

test('between two ticks the aircraft is blended, the short way round the compass and the date line', () => {
  const from: AircraftSample = { position: { lat: 10, lon: 179.9 }, altitude: 1000, heading: 350, pitch: 0, bank: 0 }
  const to: AircraftSample = { position: { lat: 10.2, lon: -179.9 }, altitude: 2000, heading: 10, pitch: 2, bank: 20 }
  const half = blendAircraft(from, to, 0.5)
  expect(half.position.lat).toBeCloseTo(10.1, 9)
  expect(Math.abs(Math.abs(half.position.lon) - 180)).toBeLessThan(1e-9)
  expect(half.altitude).toBe(1500)
  expect(Math.min(half.heading, 360 - half.heading)).toBeLessThan(1e-9)
  expect(half.bank).toBe(10)
  // Outside the tick interval it holds the ends instead of extrapolating.
  const after = blendAircraft(from, to, 3)
  expect(after.position.lon).toBeCloseTo(-179.9, 9)
  expect(after.heading).toBeCloseTo(10, 9)
  expect(after.altitude).toBe(2000)
  expect(blendAircraft(from, to, -1).altitude).toBe(1000)
})

test('the route line is drawn at each constraint, and holds the last altitude between them', () => {
  expect(routeHeights([null, 4000, null, 2500], 1500)).toEqual([1500 * FT, 4000 * FT, 4000 * FT, 2500 * FT])
})
