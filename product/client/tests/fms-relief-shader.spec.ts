import { expect, logicTest as test } from './isolated-client-test'
import { TILE_PIXELS, shadeTile } from '../src/fmsCdu/outTheWindow'
import { inlineReliefShader, workerReliefShader } from '../src/fmsCdu/reliefShader'

// The out-the-window view's ground tiles are shaded in a worker (reliefShader.ts, reliefWorker.ts), so a burst of new
// tiles does not freeze the page; where no worker can run they are shaded in place. The worker is stood in for here by
// an object with the same messages, answering with shadeTile as the real one does.

const heights = () => { const h = new Float32Array(TILE_PIXELS * TILE_PIXELS); for (let i = 0; i < h.length; i++) h[i] = 200 + (i % 97); return h }
type Posted = { id: number; heights: Float32Array; cellMetres: number }
// A shaded tile, or 'lost' when it does not come within a second.
const within = <T>(promise: Promise<T>) => Promise.race([promise, new Promise<'lost'>(resolve => setTimeout(() => resolve('lost'), 1000))])

class FakeWorker {
  onmessage: ((event: MessageEvent) => void) | null = null
  onerror: ((event: ErrorEvent) => void) | null = null
  posted: { message: Posted; transfer: Transferable[] }[] = []
  terminated = false
  postMessage(message: Posted, transfer: Transferable[]) { this.posted.push({ message, transfer }) }
  terminate() { this.terminated = true }
  answer(index = 0) {
    const { message } = this.posted[index]
    this.onmessage?.({ data: { id: message.id, rgba: shadeTile(message.heights, message.cellMetres) } } as MessageEvent)
  }
  fail() { this.onerror?.({ preventDefault: () => {} } as ErrorEvent) }
}

test('a tile goes to the worker as a transferred copy, and comes back shaded as shadeTile shades it', async () => {
  const worker = new FakeWorker()
  const shader = workerReliefShader(() => worker as unknown as Worker)
  const tile = heights()
  const shaded = shader.shade(tile, 12)
  expect(worker.posted).toHaveLength(1)
  const { message, transfer } = worker.posted[0]
  // A copy is transferred; the shared tile stays whole and usable here.
  expect(message.heights).not.toBe(tile)
  expect(transfer).toEqual([message.heights.buffer])
  expect(Array.from(message.heights.slice(0, 5))).toEqual(Array.from(tile.slice(0, 5)))
  expect(message.cellMetres).toBe(12)
  worker.answer()
  expect(Array.from((await shaded).slice(0, 64))).toEqual(Array.from(shadeTile(tile, 12).slice(0, 64)))
})

test('answers are matched to their tiles by id, in whatever order they come', async () => {
  const worker = new FakeWorker()
  const shader = workerReliefShader(() => worker as unknown as Worker)
  const flat = new Float32Array(TILE_PIXELS * TILE_PIXELS), hill = heights()
  const first = shader.shade(flat, 10), second = shader.shade(hill, 10)
  worker.answer(1)
  worker.answer(0)
  expect(Array.from((await first).slice(0, 4))).toEqual(Array.from(shadeTile(flat, 10).slice(0, 4)))
  expect(Array.from((await second).slice(0, 4))).toEqual(Array.from(shadeTile(hill, 10).slice(0, 4)))
})

test('a worker that fails, or cannot start, gives way to shading in place: nothing is lost', async () => {
  const worker = new FakeWorker()
  const shader = workerReliefShader(() => worker as unknown as Worker)
  const tile = heights()
  const waiting = shader.shade(tile, 10)
  worker.fail()
  expect(worker.terminated).toBe(true)
  const recovered = await within(waiting)
  expect(recovered, 'the tile in the worker when it failed').not.toBe('lost')
  expect(Array.from((recovered as Uint8ClampedArray).slice(0, 8))).toEqual(Array.from(shadeTile(tile, 10).slice(0, 8)))
  // After the failure, tiles are shaded here and the worker is not asked again.
  expect(await within(shader.shade(tile, 10)), 'a tile after the failure').not.toBe('lost')
  expect(worker.posted).toHaveLength(1)

  const refused = workerReliefShader(() => { throw new Error('worker-src refused') })
  expect(Array.from((await refused.shade(tile, 10)).slice(0, 8))).toEqual(Array.from(shadeTile(tile, 10).slice(0, 8)))
  expect(Array.from((await inlineReliefShader().shade(tile, 10)).slice(0, 8))).toEqual(Array.from(shadeTile(tile, 10).slice(0, 8)))
})
