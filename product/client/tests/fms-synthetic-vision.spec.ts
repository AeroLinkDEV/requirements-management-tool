import { expect, logicTest as test } from './isolated-client-test'
import { horizonRow, renderSyntheticVision, svsColour, type HeightAt, type SvsEye, type SvsView } from '../src/fmsCdu/syntheticVision'

// Synthetic vision on the PFD (syntheticVision.ts): terrain drawn behind the attitude indicator, conformal with the
// PFD's pitch scale, from a height lookup.

// The PFD's attitude geometry: 6 px per degree of pitch at the boresight.
const view: SvsView = { width: 120, height: 120, centreX: 60, centreY: 60, focal: 6 / Math.tan(Math.PI / 180), range: 40_000 }
const eye: SvsEye = { lat: 45.5, lon: -73.7, altitude: 1000, heading: 0, pitch: 0 }
const draw = (heightAt: HeightAt, at: Partial<SvsEye> = {}) => {
  const out = new Uint8ClampedArray(view.width * view.height * 4)
  const samples = renderSyntheticVision(out, view, { ...eye, ...at }, heightAt)
  const pixel = (x: number, y: number) => Array.from(out.slice((y * view.width + x) * 4, (y * view.width + x) * 4 + 4))
  // Sky is blue above all; the ground's ramp is green or brown, never bluest.
  const isSky = (x: number, y: number) => { const [r, g, b] = pixel(x, y); return b > r && b > g }
  return { out, samples, pixel, isSky }
}
const flat: HeightAt = () => 0

test('over flat ground in level flight the horizon is at the boresight, sky above and ground below', () => {
  const { isSky, pixel } = draw(flat)
  expect(horizonRow(view, 0)).toBe(60)
  expect(isSky(60, 40)).toBe(true)
  expect(isSky(60, 75)).toBe(false)
  expect(isSky(5, 75)).toBe(false)
  // Every pixel is drawn, opaque.
  for (let y = 0; y < view.height; y += 7) expect(pixel(33, y)[3]).toBe(255)
})

test('the picture is conformal with the pitch scale: nose up 5° puts the horizon 30 px down', () => {
  expect(horizonRow(view, 5) - horizonRow(view, 0)).toBeCloseTo(6 * 5, 0)
  const { isSky } = draw(flat, { pitch: 5 })
  expect(isSky(60, 80)).toBe(true)
  expect(isSky(60, 100)).toBe(false)
})

test('a ridge ahead rises above the horizon by its angle, and hides what is behind it', () => {
  // 500 m above the aircraft at 10 km: atan(0.05) ≈ 2.9°, so its crest is about 17 px above the horizon.
  const ridge: HeightAt = lat => ((lat - eye.lat) * 110_540 > 10_000 ? 1500 : 0)
  const { isSky, pixel } = draw(ridge)
  expect(isSky(60, 60 - 12)).toBe(false)
  expect(isSky(60, 60 - 22)).toBe(true)
  // Its face is the ridge's brown, not the lowland green in front of it.
  const [r, g] = pixel(60, 60 - 6)
  expect(r).toBeGreaterThan(g)
  const [nearR, nearG] = pixel(60, 110)
  expect(nearG).toBeGreaterThan(nearR)
})

test('the terrain follows the heading: a hill to the east is ahead only when flying east', () => {
  const east: HeightAt = (_lat, lon) => ((lon - eye.lon) * 111_320 * Math.cos((eye.lat * Math.PI) / 180) > 5_000 ? 2500 : 0)
  expect(draw(east, { heading: 0 }).isSky(60, 30)).toBe(true)
  expect(draw(east, { heading: 90 }).isSky(60, 30)).toBe(false)
})

test('missing height data draws as sea level, and a march stops early once the column is full', () => {
  const none = draw(() => null)
  expect(none.isSky(60, 40)).toBe(true)
  expect(none.isSky(60, 90)).toBe(false)
  // Diving at a wall, every column fills at the first ridge and stops marching.
  const wall = draw(() => 5000, { pitch: -10 })
  expect(wall.samples).toBeLessThan(view.width * 5)
})

test('the ground colour runs from lowland green through brown to grey rock', () => {
  const [r0, g0] = svsColour(0)
  expect(g0).toBeGreaterThan(r0)
  const [r1, g1] = svsColour(1500)
  expect(r1).toBeGreaterThan(g1)
  const rock = svsColour(3000)
  expect(Math.max(...rock) - Math.min(...rock)).toBeLessThan(30)
})
