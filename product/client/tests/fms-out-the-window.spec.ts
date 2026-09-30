import { expect, logicTest as test } from './isolated-client-test'
import {
  AIRCRAFT_PARTS, CHASE_ABOVE, CHASE_BEHIND, FT, HOVER_LOOK_DOWN, TILE_PIXELS, WATER, ancestorOf, blendAircraft, cameraPose, decodeTerrarium, pixelMetres, rampColour, routeHeights,
  RELIEF_MAX_ZOOM, sampleHeights, shadeTile, tileLatitude, type AircraftSample,
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
  // Close enough that the 15.5 m aircraft spans a good part of the view: 55 m back along the heading.
  const metresBack = (level.position.lon - chase.longitude) * 111_320 * Math.cos((level.position.lat * Math.PI) / 180)
  expect(Math.abs(metresBack - CHASE_BEHIND)).toBeLessThan(0.5)
  expect(chase.height - 3000 * FT).toBeCloseTo(CHASE_ABOVE, 6)
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

test('the chase aircraft is a helicopter: a 14.6 m rotor disc over the cabin, a tail rotor on the boom, skids beneath', () => {
  const part = (name: string) => AIRCRAFT_PARTS.find(entry => entry.name === name)!
  const rotor = part('main rotor'), cabin = part('cabin'), boom = part('tail boom'), tailRotor = part('tail rotor')
  expect(rotor.size[0] * 2).toBeCloseTo(14.6, 6)
  expect(rotor.size[1]).toBe(rotor.size[0])
  // The main rotor turns about the reference point (the mast), clear of every part under its disc; the rotors alone are
  // translucent. (The fin, aft of the disc, may stand higher.)
  expect(rotor.offset.slice(0, 2)).toEqual([0, 0])
  for (const entry of AIRCRAFT_PARTS.filter(other => other !== rotor && Math.hypot(other.offset[0], other.offset[1]) < rotor.size[0])) {
    const top = entry.offset[2] + (entry.shape === 'box' ? entry.size[2] / 2 : entry.size[2])
    expect(top, entry.name).toBeLessThan(rotor.offset[2])
  }
  expect(AIRCRAFT_PARTS.filter(entry => entry.alpha !== undefined).map(entry => entry.name).sort()).toEqual(['main rotor', 'tail rotor'])
  // The tail rotor is at the aft end of the boom, beyond the rotor disc, turning in the vertical plane.
  expect(tailRotor.offset[0]).toBeLessThan(boom.offset[0] - boom.size[0] + 0.5)
  expect(-tailRotor.offset[0]).toBeGreaterThan(rotor.size[0])
  expect(tailRotor.size[1]).toBeLessThan(tailRotor.size[2] / 10)
  // The skids are the lowest parts, below the cabin, and every part but the tail rotor mirrors across the centreline.
  const skids = AIRCRAFT_PARTS.filter(entry => entry.name.endsWith('skid'))
  expect(skids).toHaveLength(2)
  for (const skid of skids) expect(skid.offset[2]).toBeLessThan(cabin.offset[2] - cabin.size[2])
  for (const entry of AIRCRAFT_PARTS.filter(other => other !== tailRotor)) {
    const mirrored = AIRCRAFT_PARTS.some(other => other.offset[1] === -entry.offset[1] && other.offset[0] === entry.offset[0] && other.size.every((value, i) => value === entry.size[i]))
    expect(entry.offset[1] === 0 || mirrored, entry.name).toBe(true)
  }
})

test('the cameras follow the heading, not the track: a crab or a sideways drift looks along the nose', () => {
  // Heading 090 while the ground track is anything else: the air data carries no track at all here.
  const crabbed = { ...level, heading: 60 }
  expect(cameraPose(crabbed, 'cockpit', 'hud').heading).toBeCloseTo((60 * Math.PI) / 180, 9)
  const chase = cameraPose(crabbed, 'chase', 'hud')
  expect(chase.heading).toBeCloseTo((60 * Math.PI) / 180, 9)
  // The chase camera sits behind along the heading (south-west of the aircraft for 060°).
  expect(chase.latitude).toBeLessThan(crabbed.position.lat)
  expect(chase.longitude).toBeLessThan(crabbed.position.lon)
  // In the local frame (a degree of longitude is cos(latitude) of a degree of latitude), exactly back along 060°.
  const north = crabbed.position.lat - chase.latitude
  const east = (crabbed.position.lon - chase.longitude) * Math.cos((crabbed.position.lat * Math.PI) / 180)
  expect((Math.atan2(east, north) * 180) / Math.PI).toBeCloseTo(60, 6)
})

test('with the hover data shown, the cockpit camera looks further down; the flag survives blending between ticks', () => {
  const cruising = cameraPose(level, 'cockpit', 'hud'), hovering = cameraPose({ ...level, hoverData: true }, 'cockpit', 'hud')
  expect((cruising.pitch - hovering.pitch) * (180 / Math.PI)).toBeCloseTo(HOVER_LOOK_DOWN, 9)
  expect(hovering.pitch).toBeLessThan(cameraPose({ ...level, hoverData: true }, 'cockpit', 'panel').pitch)
  // It changes nothing outside the cockpit.
  expect(cameraPose({ ...level, hoverData: true }, 'chase', 'hud')).toEqual(cameraPose(level, 'chase', 'hud'))
  expect(blendAircraft(level, { ...level, hoverData: true }, 0.4).hoverData).toBe(true)
  expect(blendAircraft({ ...level, hoverData: true }, level, 0.4).hoverData).toBeUndefined()
})

// The shading as it was first written: the reference the faster shadeTile must reproduce (to the colour table's
// whole-metre rounding). Kept here verbatim, so the test does not share the code it checks.
function referenceShade(heights: Float32Array, cellMetres: number) {
  const out = new Uint8ClampedArray(TILE_PIXELS * TILE_PIXELS * 4)
  const at = (x: number, y: number) => heights[Math.min(TILE_PIXELS - 1, Math.max(0, y)) * TILE_PIXELS + Math.min(TILE_PIXELS - 1, Math.max(0, x))]
  const azimuth = (315 * Math.PI) / 180, zenith = (45 * Math.PI) / 180
  for (let y = 0; y < TILE_PIXELS; y++) for (let x = 0; x < TILE_PIXELS; x++) {
    const h = at(x, y)
    const dzdx = (at(x + 1, y) - at(x - 1, y)) / (2 * cellMetres), dzdy = (at(x, y + 1) - at(x, y - 1)) / (2 * cellMetres)
    let flat = true
    for (let dy = -2; dy <= 2 && flat; dy++) for (let dx = -2; dx <= 2; dx++) if (Math.abs(at(x + dx, y + dy) - h) > 0.05) { flat = false; break }
    const slope = Math.atan(Math.hypot(dzdx, dzdy)), aspect = Math.atan2(dzdy, -dzdx)
    const light = Math.cos(zenith) * Math.cos(slope) + Math.sin(zenith) * Math.sin(slope) * Math.cos(azimuth - Math.PI / 2 - aspect)
    const shade = 0.55 + 0.55 * Math.max(0, light)
    const base = flat && h > 0 ? WATER : rampColour(h)
    const i = (y * TILE_PIXELS + x) * 4
    out[i] = Math.min(255, base[0] * shade); out[i + 1] = Math.min(255, base[1] * shade); out[i + 2] = Math.min(255, base[2] * shade); out[i + 3] = 255
  }
  return out
}

test('the fast shading draws what the per-pixel form drew: slopes, water, sea, snow and noise, within 2 of 255', () => {
  let seed = 7
  const noise = () => (seed = (seed * 16807) % 2147483647) / 2147483647
  const cases: [string, Float32Array, number][] = [
    ['ridge', grid(x => 500 - Math.abs(x - 128) * 4), 20],
    ['lake among hills', grid((x, y) => (Math.hypot(x - 128, y - 128) < 40 ? 200 : 200 + Math.hypot(x - 128, y - 128) - 40)), 20],
    ['sea and a shore', grid(x => (x < 100 ? -20 : (x - 100) * 3)), 30],
    ['snow above the ramp', grid((x, y) => 3000 + x * 5 + y * 2), 10],
    ['terraces a few centimetres apart', grid(x => 300 + Math.floor(x / 9) * 0.04), 5],
    ['stripes 8 cm apart', grid(x => 300 + (Math.floor(x / 3) % 2) * 0.08), 5],
    ['a level plateau with one bump', grid((x, y) => 400 + (x === 60 && y === 60 ? 0.2 : 0)), 5],
    ['rough ground', grid(() => 800 + noise() * 60), 8],
  ]
  for (const [name, heights, cell] of cases) {
    const fast = shadeTile(heights, cell), reference = referenceShade(heights, cell)
    let worst = 0
    for (let i = 0; i < fast.length; i++) worst = Math.max(worst, Math.abs(fast[i] - reference[i]))
    expect(worst, name).toBeLessThanOrEqual(2)
  }
})

test('the ground colour stops at level 14, where its pixels are already finer than the source heights', () => {
  expect(RELIEF_MAX_ZOOM).toBe(14)
  // At 45° a level-14 pixel is under 7 m, finer than the 10 m US source; level 13 (about 13.5 m) would be coarser.
  expect(pixelMetres(RELIEF_MAX_ZOOM, 45)).toBeLessThan(10)
  expect(pixelMetres(RELIEF_MAX_ZOOM - 1, 45)).toBeGreaterThan(10)
})
