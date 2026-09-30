import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { NEUTRAL_RGB, obstacleColour, obstaclesWithin, parseDof, type Obstacle } from '../src/fmsCdu/obstacles'
import { ABSOLUTE_RGB, CAUTION_RGB, DANGER_RGB } from '../src/fmsCdu/terrainAwareness'
import { OBSTACLE_DATA_URL, createObstacleLayer, drawObstacles, type ObstacleCesium } from '../src/fmsCdu/otwObstacles'

// Brief C (Sean's out-the-window upgrade, 29 September): the FAA Digital Obstacle File, downloaded once, as an
// extract of unchanged records near the bench's US areas (public/fms-cdu/obstacles, with its provenance README).
const EXTRACT = 'public/fms-cdu/obstacles/dof-bench-extract.csv'
const text = () => readFileSync(EXTRACT, 'latin1')
const HEADER = 'OAS,VERIFIED STATUS,COUNTRY,STATE,CITY,LATDEC,LONDEC,DMSLAT,DMSLON,TYPE,QUANTITY,AGL,AMSL,LIGHTING,ACCURACY,MARKING,FAA STUDY,ACTION,JDATE'
const GREENWICH = '09-021145,O,US,CT,GREENWICH       ,41.005065,-73.648312,41 00 18.23N,073 38 53.92W,TOWER             ,1,00084,00137,N, 2C,N,2011ANE01884OE,A,2012268 '
const OAKDALE = '09-000279,O,US,CT,OAKDALE         ,41.417500,-72.198134,41 25 03.00N,072 11 53.28W,TOWER             ,1,01090,01399,H, 1A,N,2022ANE00687OE,C,2023003'

test('the bench extract is the recorded one: its SHA-256 and record count, every record read', () => {
  expect(createHash('sha256').update(readFileSync(EXTRACT)).digest('hex')).toBe('ff1ff3950f1972fd66ed17e7b539efc37f52f9801241dee333886ca4c215ca8a')
  const { obstacles, errors } = parseDof(text())
  expect(errors).toEqual([])
  expect(obstacles).toHaveLength(11973)
  expect(readFileSync('public/fms-cdu/obstacles/README.md', 'utf8')).toContain('ff1ff3950f1972fd66ed17e7b539efc37f52f9801241dee333886ca4c215ca8a')
})

test('a DOF record is read field by field: type, position, AGL and AMSL, lighting, accuracy', () => {
  const { obstacles } = parseDof(`${HEADER}\n${GREENWICH}\n${OAKDALE}\n`)
  expect(obstacles[0]).toEqual({
    oas: '09-021145', verified: 'O', position: { lat: 41.005065, lon: -73.648312 }, type: 'TOWER', quantity: 1,
    aglFt: 84, amslFt: 137, lighting: 'N', accuracy: '2C', marking: 'N',
  })
  expect(obstacles[1]).toMatchObject({ oas: '09-000279', aglFt: 1090, amslFt: 1399, lighting: 'H' })
})

test('a file without the published header is refused whole; an unreadable record is skipped with the reason', () => {
  expect(parseDof(`OAS,SOMETHING\n${GREENWICH}\n`)).toEqual({ obstacles: [], errors: ['not a DOF CSV file (header differs)'] })
  const broken = GREENWICH.replace('41.005065', 'north')
  const noHeight = OAKDALE.replace('01090', 'tall')
  const result = parseDof(`${HEADER}\n${broken}\n${noHeight}\n${GREENWICH}\n`)
  expect(result.obstacles.map(o => o.oas)).toEqual(['09-021145'])
  expect(result.errors).toEqual(['record 1: position not readable', 'record 2 (09-000279): heights not readable'])
})

test('a query by bounds gives the obstacles inside the box', () => {
  const { obstacles } = parseDof(text())
  // Around 87N (N40 50.8 W072 28.0), 5 NM or so.
  const near87n = obstaclesWithin(obstacles, { south: 40.76, west: -72.58, north: 40.93, east: -72.35 })
  expect(near87n.length).toBeGreaterThan(0)
  for (const o of near87n) {
    expect(o.position.lat).toBeGreaterThanOrEqual(40.76)
    expect(o.position.lon).toBeLessThanOrEqual(-72.35)
  }
  expect(obstaclesWithin(obstacles, { south: 0, west: 0, north: 1, east: 1 })).toEqual([])
})

test('an obstacle is coloured as the terrain colouring colours its top: relative, absolute, or neutral', () => {
  const tower = { amslFt: 1399 } as Obstacle
  // Relative (terrainAwareness.ts): red at or above 100 ft below the aircraft, amber within 500 ft, else neutral.
  expect(obstacleColour(tower, 1499, 'relative')).toEqual(DANGER_RGB)
  expect(obstacleColour(tower, 1500, 'relative')).toEqual(CAUTION_RGB)
  expect(obstacleColour(tower, 1899, 'relative')).toEqual(CAUTION_RGB)
  expect(obstacleColour(tower, 1900, 'relative')).toEqual(NEUTRAL_RGB)
  expect(obstacleColour(tower, 1000, 'relative')).toEqual(DANGER_RGB)
  // Absolute: the shared AMSL bands.
  expect([499, 500, 999, 1000, 1999, 2000, 2999, 3000].map(amslFt => obstacleColour({ amslFt } as Obstacle, 0, 'absolute')))
    .toEqual([ABSOLUTE_RGB[0], ABSOLUTE_RGB[1], ABSOLUTE_RGB[1], ABSOLUTE_RGB[2], ABSOLUTE_RGB[2], ABSOLUTE_RGB[3], ABSOLUTE_RGB[3], ABSOLUTE_RGB[4]])
  // Off: neutral whatever the height.
  expect(obstacleColour(tower, 1400, 'off')).toEqual(NEUTRAL_RGB)
})

/** A stand-in for the parts of Cesium the layer uses, recording what is drawn. */
function fakeCesium() {
  const added: unknown[] = [], removed: unknown[] = []
  let renders = 0
  class PolylineCollection { items: { positions: unknown[]; width: number; material?: unknown }[] = []; add(o: { positions: unknown[]; width: number }) { const item = { ...o, material: undefined as unknown }; this.items.push(item); return item } }
  class PointPrimitiveCollection { items: { position: unknown; pixelSize: number; color: unknown }[] = []; add(o: { position: unknown; pixelSize: number; color: unknown }) { const item = { ...o }; this.items.push(item); return item } }
  const Cesium: ObstacleCesium = {
    PolylineCollection, PointPrimitiveCollection,
    Cartesian3: { fromDegrees: (lon, lat, height) => ({ lon, lat, height }) },
    Color: { fromBytes: (r, g, b) => `rgb(${r},${g},${b})` },
    Material: { fromType: (_type, uniforms) => uniforms.color },
  }
  const scene = { primitives: { add<T>(p: T) { added.push(p); return p }, remove(p: unknown) { removed.push(p); return true } }, requestRender: () => { renders += 1 } }
  return { Cesium, scene, added, removed, renders: () => renders }
}

test('the scene layer draws each obstacle from its base to its top at true height, and recolours by clearance', () => {
  const { obstacles } = parseDof(`${HEADER}\n${GREENWICH}\n${OAKDALE}\n`)
  const { Cesium, scene, added, removed, renders } = fakeCesium()
  const layer = drawObstacles(Cesium, scene, obstacles)
  expect(added).toHaveLength(2)
  const line = (layer.lines as unknown as { items: { positions: { height: number }[] }[] }).items[1]
  // Oakdale: its top 1,399 ft AMSL, its base 1,090 ft below (309 ft), in metres.
  expect(line.positions[0].height).toBeCloseTo(309 * 0.3048, 6)
  expect(line.positions[1].height).toBeCloseTo(1399 * 0.3048, 6)
  // At 1,450 ft the Oakdale tower (1,399 ft) is red; Greenwich (137 ft) stays neutral.
  layer.update(1450, 'relative')
  const points = (layer.tops as unknown as { items: { color: string }[] }).items
  expect(points.map(p => p.color)).toEqual([`rgb(${NEUTRAL_RGB})`, `rgb(${DANGER_RGB})`])
  expect(renders()).toBe(1)
  // A change of less than 10 ft recolours nothing; a new mode does.
  layer.update(1455, 'relative')
  expect(renders()).toBe(1)
  layer.update(1455, 'absolute')
  expect(points.map(p => p.color)).toEqual([`rgb(${ABSOLUTE_RGB[0]})`, `rgb(${ABSOLUTE_RGB[2]})`])
  layer.destroy()
  expect(removed).toHaveLength(2)
})

test('createObstacleLayer fetches the bench extract, applies the latest update once drawn, and reports a failed load', async () => {
  const { Cesium, scene } = fakeCesium()
  const urls: string[] = []
  const layer = createObstacleLayer(Cesium, scene, async url => { urls.push(url); return `${HEADER}\n${OAKDALE}\n` })
  layer.update(1450, 'relative')
  expect(await layer.ready).toEqual({ drawn: 1 })
  expect(urls).toEqual([OBSTACLE_DATA_URL])
  expect(layer.count).toBe(1)
  const failed = createObstacleLayer(Cesium, scene, async () => { throw new Error('404 Not Found') })
  expect(await failed.ready).toEqual({ failed: 'obstacle data not loaded: 404 Not Found' })
  expect(failed.count).toBe(0)
})
