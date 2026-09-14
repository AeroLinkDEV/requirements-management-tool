import { expect, test } from '@playwright/test'
import { contentPositionsForNodes, geometryFor, MEASURED_CARD_GAP, planReveal, type CanvasNode } from '../src/digitalThreadGeometry'

const geometry = geometryFor(1)
const nodes: CanvasNode[] = [
  { id: 'source', lane: 0, row: 1 },
  ...Array.from({ length: 8 }, (_, row) => ({ id: `background-${row}`, lane: 1, row })),
  { id: 'linked-a', lane: 1, row: 12 }, { id: 'linked-b', lane: 1, row: 13 },
]
const input = { nodes, geometry, laneOffsets: [0, 0], subjectId: 'source',
  storyIds: new Set(['source', 'linked-a', 'linked-b']), windowByLane: new Map([[1, { top: 0, bottom: 500 }]]),
  frozenLanes: new Set<number>(), bandHeight: 500 }

test('ordinary reveal fits two foreground cards over unchanged background with a populated pair check', () => {
  const plan = planReveal(input)
  const final = contentPositionsForNodes(nodes, geometry, undefined, plan.deltas)
  const canonical = contentPositionsForNodes(nodes, geometry)
  const moved = ['linked-a', 'linked-b'].map(id => ({ id, top: final.get(id)! }))
  expect(moved).toHaveLength(2)
  expect(plan.deltas.has(moved[0].id) && plan.deltas.has(moved[1].id)).toBe(true)
  expect(Math.abs(moved[0].top - moved[1].top)).toBeGreaterThanOrEqual(geometry.cardHeight + MEASURED_CARD_GAP)
  for (const node of nodes.filter(n => n.id.startsWith('background'))) {
    expect(final.get(node.id)).toBe(canonical.get(node.id))
    expect(plan.deltas.has(node.id)).toBe(false)
  }
  for (const card of moved) expect.soft(card.top + geometry.cardHeight, `${card.id} fits`).toBeLessThanOrEqual(500)
  expect(moved.some(card => nodes.filter(n => n.id.startsWith('background')).some(n =>
    card.top < canonical.get(n.id)! + geometry.cardHeight && canonical.get(n.id)! < card.top + geometry.cardHeight)),
    'positively demonstrated allowed background overlap').toBe(true)
})

test('retained growth protects an actual foreground neighbor without treating background as obstacles', () => {
  const measuredHeights = new Map([['linked-a', 180]])
  const grownBase = contentPositionsForNodes(nodes, geometry, measuredHeights)
  const existing = new Map([['linked-a', 100 - grownBase.get('linked-a')!], ['linked-b', 320 - grownBase.get('linked-b')!]])
  const plan = planReveal({ ...input, measuredHeights, existing, frozenLanes: new Set([1]) })
  const final = contentPositionsForNodes(nodes, geometry, measuredHeights, plan.deltas)
  expect(final.get('linked-b')! - final.get('linked-a')!).toBeGreaterThanOrEqual(180 + MEASURED_CARD_GAP)
  expect.soft(final.get('linked-a'), 'retained foreground can cover background').toBe(100)
  expect.soft(final.get('linked-b')).toBe(320)
  expect([...planReveal({ ...input, measuredHeights, existing: plan.deltas, frozenLanes: new Set([1]) }).deltas]).toEqual([...plan.deltas])
})

test('ordinary-row zero-delta selected promotion needs the post-tray window', () => {
  const base = contentPositionsForNodes(nodes, geometry)
  const subjectId = 'background-4'
  const plan = planReveal({ ...input, subjectId, storyIds: new Set([subjectId]),
    windowByLane: new Map([[1, { top: 0, bottom: 300 }]]), existing: new Map([[subjectId, 0]]) })
  expect(base.get(subjectId)!).toBeGreaterThan(300)
  expect(contentPositionsForNodes(nodes, geometry, undefined, plan.deltas).get(subjectId)! + geometry.cardHeight).toBeLessThanOrEqual(300)
})

test('a clipped linked foreground card becomes fully usable while the hovered source stays fixed', () => {
  const subjectId = 'source', linked = 'background-2'
  const base = contentPositionsForNodes(nodes, geometry)
  const top = base.get(linked)! + 20
  const plan = planReveal({ ...input, subjectId, storyIds: new Set([subjectId, linked]), windowByLane: new Map([[1, { top, bottom: top + 240 }]]) })
  const final = contentPositionsForNodes(nodes, geometry, undefined, plan.deltas)
  expect(final.get(subjectId)).toBe(base.get(subjectId))
  expect(final.get(linked)!).toBeGreaterThanOrEqual(top)
  expect(final.get(linked)! + geometry.cardHeight).toBeLessThanOrEqual(top + 240)
})

test('oversized first candidate cannot strand a later fitting foreground card', () => {
  const sparse = nodes.filter(n => !n.id.startsWith('background'))
  const measuredHeights = new Map([['linked-a', 900]])
  const plan = planReveal({ ...input, nodes: sparse, measuredHeights })
  const final = contentPositionsForNodes(sparse, geometry, measuredHeights, plan.deltas)
  expect(final.get('linked-b')! + geometry.cardHeight).toBeLessThanOrEqual(500)
})
