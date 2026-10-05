import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, logicTest as test } from './isolated-client-test'
import { NEUTRAL_RGB, OBSTACLE_EXTRACT_SHA256, obstacleColour, obstaclesWithin, parseDof, type Obstacle } from '../src/fmsCdu/obstacles'
import { ABSOLUTE_RGB, CAUTION_RGB, DANGER_RGB } from '../src/fmsCdu/terrainAwareness'
import { OBSTACLE_DATA_URL, createObstacleLayer, drawObstacles, type ObstacleCesium } from '../src/fmsCdu/otwObstacles'

// Brief C (Sean's out-the-window upgrade, 29 September): the FAA Digital Obstacle File, downloaded once, as an
// extract of unchanged records near the bench's US areas (public/fms-cdu/obstacles, with its provenance), pinned by its SHA-256.
const EXTRACT = 'public/fms-cdu/obstacles/dof-bench-extract.csv'
const text = () => readFileSync(EXTRACT, 'latin1')
const HEADER = 'OAS,VERIFIED STATUS,COUNTRY,STATE,CITY,LATDEC,LONDEC,DMSLAT,DMSLON,TYPE,QUANTITY,AGL,AMSL,LIGHTING,ACCURACY,MARKING,FAA STUDY,ACTION,JDATE'
const GREENWICH = '09-021145,O,US,CT,GREENWICH       ,41.005065,-73.648312,41 00 18.23N,073 38 53.92W,TOWER             ,1,00084,00137,N, 2C,N,2011ANE01884OE,A,2012268 '
const OAKDALE = '09-000279,O,US,CT,OAKDALE         ,41.417500,-72.198134,41 25 03.00N,072 11 53.28W,TOWER             ,1,01090,01399,H, 1A,N,2022ANE00687OE,C,2023003'

test('the bench extract is the recorded one: its SHA-256 and record count, every record read', () => {
  expect(createHash('sha256').update(readFileSync(EXTRACT)).digest('hex')).toBe(OBSTACLE_EXTRACT_SHA256)
  const { obstacles, errors } = parseDof(text())
  expect(errors).toEqual([])
  expect(obstacles).toHaveLength(11973)
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
  const added: unknown[] = [], removed: unknown[] = [], created: Primitive[] = [], sceneErrors: unknown[] = []
  let renders = 0
  const postRender = new Set<() => void>(), renderError = new Set<(_scene: unknown, error: unknown) => void>()
  type LineOptions = { positions: { height: number }[]; width: number; arcType: number }
  class PolylineGeometry { constructor(readonly options: LineOptions) {} }
  class GeometryInstance { constructor(readonly options: { id: number; geometry: PolylineGeometry; attributes: { color: Uint8Array } }) {} }
  class Primitive {
    ready = false
    show: boolean
    /** As Cesium's Primitive: a failed asynchronous build is thrown from every update after it. */
    error: unknown = undefined
    destroyed = false
    readonly attributes = new Map<number, { color: Uint8Array; boundingSphere: object | undefined }>()
    constructor(readonly options: { geometryInstances: GeometryInstance[]; show: boolean }) {
      created.push(this)
      this.show = options.show
      for (const instance of options.geometryInstances) {
        const positions = instance.options.geometry.options.positions
        this.attributes.set(instance.options.id, { color: instance.options.attributes.color,
          boundingSphere: positions[0].height === positions[1].height ? undefined : { radius: 1 } })
      }
    }
    getGeometryInstanceAttributes(id: number) {
      if (!this.ready) throw new Error('geometry still loading')
      return this.attributes.get(id)
    }
    update() { if (this.error !== undefined) throw this.error }
    isDestroyed() { return this.destroyed }
    destroy() { this.destroyed = true }
  }
  class PointPrimitiveCollection { show = true; items: { position: unknown; pixelSize: number; color: unknown }[] = []; add(o: { position: unknown; pixelSize: number; color: unknown }) { const item = { ...o }; this.items.push(item); return item } }
  const bytes = (color: string) => new Uint8Array([...color.slice(4, -1).split(',').map(Number), 255])
  const Cesium = {
    Primitive, GeometryInstance, PolylineGeometry, PointPrimitiveCollection,
    PolylineColorAppearance: Object.assign(class { constructor(readonly options: { translucent: boolean }) {} }, { VERTEX_FORMAT: 'positions' }),
    ColorGeometryInstanceAttribute: { fromColor: bytes, toValue: bytes }, ArcType: { NONE: 0 },
    Cartesian3: { fromDegrees: (lon: number, lat: number, height: number) => ({ lon, lat, height }) },
    Color: { fromBytes: (r: number, g: number, b: number) => `rgb(${r},${g},${b})` },
  } as unknown as ObstacleCesium
  const scene = {
    primitives: { add<T>(p: T) { added.push(p); return p }, remove(p: unknown) { removed.push(p); return true }, isDestroyed: () => false },
    postRender: { addEventListener(listener: () => void) { postRender.add(listener); return () => { postRender.delete(listener) } } },
    renderError: { addEventListener(listener: (_scene: unknown, error: unknown) => void) { renderError.add(listener); return () => { renderError.delete(listener) } } },
    requestRender: () => { renders += 1 },
  }
  const lines = () => created[0]
  const error = (reason: unknown) => { sceneErrors.push(reason); for (const listener of renderError) listener(scene, reason) }
  // As Scene.render: the primitives still in the scene are updated, a throw becomes a render error, and the frame ends.
  const render = () => {
    try {
      for (const primitive of added) if (!removed.includes(primitive)) (primitive as { update?(frameState: unknown): void }).update?.({})
    } catch (thrown) { error(thrown) }
    for (const listener of postRender) listener()
  }
  const complete = () => { lines().ready = true; render(); render() }
  return { Cesium, scene, added, removed, lines, render, complete, error, sceneErrors,
    listeners: () => postRender.size + renderError.size, renders: () => renders }
}

test('the scene layer draws each obstacle from its base to its top at true height, and recolours by clearance', async () => {
  const { obstacles } = parseDof(`${HEADER}\n${GREENWICH}\n${OAKDALE}\n`)
  const { Cesium, scene, added, removed, renders, lines, complete } = fakeCesium()
  const layer = drawObstacles(Cesium, scene, obstacles)
  expect(added).toHaveLength(2)
  const line = lines().options.geometryInstances[1].options.geometry.options
  // Oakdale: its top 1,399 ft AMSL, its base 1,090 ft below (309 ft), in metres.
  expect(line.positions[0].height).toBeCloseTo(309 * 0.3048, 6)
  expect(line.positions[1].height).toBeCloseTo(1399 * 0.3048, 6)
  expect(line.width).toBe(2)
  expect(line.arcType).toBe(0) // A straight vertical segment, not a surface-following arc.
  // At 1,450 ft the Oakdale tower (1,399 ft) is red; Greenwich (137 ft) stays neutral.
  layer.update(1450, 'relative')
  complete()
  expect(await layer.ready).toEqual({ drawn: 2 })
  const points = (layer.tops as unknown as { items: { color: string }[] }).items
  expect(points.map(p => p.color)).toEqual([`rgb(${NEUTRAL_RGB})`, `rgb(${DANGER_RGB})`])
  expect(Array.from(lines().attributes.get(1)!.color)).toEqual([...DANGER_RGB, 255])
  const initialRenders = renders()
  // A change of less than 10 ft recolours nothing; a new mode does.
  layer.update(1455, 'relative')
  expect(renders()).toBe(initialRenders)
  layer.update(1455, 'absolute')
  expect(points.map(p => p.color)).toEqual([`rgb(${ABSOLUTE_RGB[0]})`, `rgb(${ABSOLUTE_RGB[2]})`])
  expect(Array.from(lines().attributes.get(1)!.color)).toEqual([...ABSOLUTE_RGB[2], 255])
  layer.destroy()
  expect(removed).toHaveLength(2)
})

test('createObstacleLayer fetches the bench extract, applies the latest update once drawn, and reports a failed load', async () => {
  const { Cesium, scene, complete } = fakeCesium()
  const urls: string[] = []
  const layer = createObstacleLayer(Cesium, scene, async url => { urls.push(url); return `${HEADER}\n${OAKDALE}\n` })
  layer.update(1450, 'relative')
  await Promise.resolve() // The data arrives before Cesium prepares its geometry.
  complete()
  expect(await layer.ready).toEqual({ drawn: 1 })
  expect(urls).toEqual([OBSTACLE_DATA_URL])
  expect(layer.count).toBe(1)
  const failed = createObstacleLayer(Cesium, scene, async () => { throw new Error('404 Not Found') })
  expect(await failed.ready).toEqual({ failed: 'obstacle data not loaded: 404 Not Found' })
  expect(failed.count).toBe(0)
})

test('delayed obstacle geometry first appears with the latest colours, then reports drawn after that frame', async () => {
  const { obstacles } = parseDof(`${HEADER}\n${OAKDALE}\n`)
  const { Cesium, scene, lines, render, listeners, sceneErrors } = fakeCesium()
  const layer = drawObstacles(Cesium, scene, obstacles)
  layer.update(1450, 'relative')
  render()
  layer.update(1455, 'absolute')
  expect(layer.lines!.show).toBe(false)
  expect(layer.tops.show).toBe(false)
  expect(layer.count).toBe(0)
  lines().ready = true
  render()
  expect(layer.lines!.show).toBe(true)
  expect(layer.tops.show).toBe(true)
  expect(Array.from(lines().attributes.get(0)!.color)).toEqual([...ABSOLUTE_RGB[2], 255])
  expect(layer.count, 'prepared geometry is not a completed exposed render').toBe(0)
  layer.update(1000, 'relative')
  render()
  expect(await layer.ready).toEqual({ drawn: 1 })
  expect(Array.from(lines().attributes.get(0)!.color)).toEqual([...DANGER_RGB, 255])
  expect(layer.count).toBe(1)
  expect(listeners()).toBe(0)
  // Once drawn, an error from the lines is not hidden by the layer: the scene's render error handling sees it.
  const lost = new Error('lines lost after drawing')
  lines().error = lost
  render()
  expect(sceneErrors).toEqual([lost])
  layer.destroy()
})

test('failed or disposed pending obstacle geometry reports no drawn count, says why, and releases its listeners and resources', async () => {
  const { obstacles } = parseDof(`${HEADER}\n${OAKDALE}\n`)
  // #1492: a geometry worker whose module could not be fetched rejects with a TypeError, which reaches the page as the
  // plain object the worker posted; Cesium then throws it from the primitive's update on every frame.
  const workerError = { name: 'TypeError', message: 'Failed to fetch dynamically imported module: createPolylineGeometry.js', stack: '' }
  const failures = {
    worker: 'obstacle geometry failed: TypeError: Failed to fetch dynamically imported module: createPolylineGeometry.js',
    attributes: 'obstacle 09-000279 has no rendered geometry',
    'exposed render': 'the scene stopped rendering: renderer failed',
    destroy: 'destroyed before geometry was ready',
  }
  for (const [failure, reason] of Object.entries(failures)) {
    const fake = fakeCesium(), layer = drawObstacles(fake.Cesium, fake.scene, obstacles)
    if (failure === 'destroy') layer.destroy()
    else if (failure === 'worker') { fake.lines().error = workerError; fake.render() }
    else {
      fake.lines().ready = true // Cesium also marks FAILED primitives ready.
      if (failure === 'attributes') { fake.lines().attributes.clear(); fake.render() }
      else { fake.render(); fake.error(new Error('renderer failed')) }
    }
    expect(await layer.ready).toEqual({ failed: `obstacles not drawn: ${reason}` })
    await Promise.resolve() // Resource removal follows the engine event traversal.
    fake.render()
    // The layer's own failure stays the layer's: the scene goes on rendering without it.
    expect(fake.sceneErrors, failure).toHaveLength(failure === 'exposed render' ? 1 : 0)
    expect(layer.count).toBe(0)
    expect(fake.listeners()).toBe(0)
    expect(fake.removed).toHaveLength(2)
    layer.destroy()
    fake.complete()
    expect(layer.count).toBe(0)
    expect(fake.removed).toHaveLength(2)
  }
})

test('zero-height obstacles retain their top point without preventing other segments or an all-point layer from drawing', async () => {
  const zeroHeight = OAKDALE.replace('01090', '00000')
  for (const records of [`${GREENWICH}\n${zeroHeight}`, zeroHeight]) {
    const { obstacles, errors } = parseDof(`${HEADER}\n${records}\n`)
    expect(errors).toEqual([])
    const fake = fakeCesium(), layer = drawObstacles(fake.Cesium, fake.scene, obstacles)
    layer.update(1450, 'relative')
    if (fake.lines()) fake.lines().ready = true
    fake.render()
    fake.render()
    expect(await layer.ready).toEqual({ drawn: obstacles.length })
    expect(layer.count).toBe(obstacles.length)
    const points = (layer.tops as unknown as { items: { position: { height: number }; color: string }[] }).items
    expect(points).toHaveLength(obstacles.length)
    expect(points.at(-1)!.position.height).toBeCloseTo(1399 * 0.3048, 6)
    expect(points.at(-1)!.color).toBe(`rgb(${DANGER_RGB})`)
    layer.destroy()
    expect(fake.listeners()).toBe(0)
  }
})
