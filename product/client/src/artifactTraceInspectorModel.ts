import type { ArtifactThread, ArtifactThreadNode } from './artifactThreadContract'
import { parseArtifactThread } from './artifactThreadContract'
import type { ExactTraceArtifact } from './routing'
export function parseInspectorThread(value: unknown, expectedRevisionId: string) {
  const result = parseArtifactThread(value)
  if (result.ok && result.thread.focalId !== expectedRevisionId)
    return { ok: false as const, reason: 'The trace response does not identify the selected exact revision.' }
  return result
}
export function artifactTraceIdentity(node: ArtifactThreadNode): ExactTraceArtifact | undefined {
  if (node.kind === 'Requirement') return { id: node.id, artifactId: node.artifactId, kind: 'RequirementRevision', level: node.level }
  if (node.kind === 'Case' || node.kind === 'Procedure') return node.artifactId
    ? { id: node.artifactId, revisionId: node.id, kind: node.kind === 'Case' ? 'TestCase' : 'TestProcedure', level: node.level } : undefined
  if (node.kind === 'Execution') return { id: node.id, kind: 'TestExecution' }
  // Change requests in this graph do not carry verification discipline metadata. Keep the existing exact
  // router's supported compatibility behavior; do not infer revision identity from the display label here.
  if (node.kind === 'ChangeRequest' || node.kind === 'TestChangeRequest' || node.kind === 'ProblemReport')
    return { id: node.id, kind: node.kind, displayNumber: node.displayNumber, level: node.level }
  return undefined
}

/** Direct rows come only from edges incident on the selected exact node. Further context is explicitly indirect. */
export function artifactTraceGroups(thread: ArtifactThread) {
  const nodes = new Map(thread.nodes.map(node => [node.id, node]))
  const incoming = thread.edges.filter(edge => edge.toId === thread.focalId)
  const outgoing = thread.edges.filter(edge => edge.fromId === thread.focalId)
  const walk = (reverse: boolean) => {
    const adjacency = new Map<string, string[]>()
    for (const edge of thread.edges) {
      const from = reverse ? edge.toId : edge.fromId
      const to = reverse ? edge.fromId : edge.toId
      adjacency.set(from, [...(adjacency.get(from) ?? []), to])
    }
    const distance = new Map([[thread.focalId, 0]])
    const queue = [thread.focalId]
    for (let i = 0; i < queue.length; i++) {
      for (const next of adjacency.get(queue[i]) ?? []) {
        if (distance.has(next)) continue
        distance.set(next, distance.get(queue[i])! + 1)
        queue.push(next)
      }
    }
    return { distance, indirect: thread.nodes.filter(node => (distance.get(node.id) ?? 0) > 1) }
  }
  return { nodes, incoming, outgoing, upstream: walk(true), downstream: walk(false) }
}
