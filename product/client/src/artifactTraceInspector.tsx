import type { ArtifactThread, ArtifactThreadNode } from './artifactThreadContract'
import { traceRelationLabelFor } from './tracePresentation'
import { stateLabel } from './presentation'
import { artifactTraceGroups } from './artifactTraceInspectorModel'
import { TraceGroup, TraceRelation } from './TraceInspector'

export function ArtifactTraceRelations({ thread, hrefFor, excludedRecords = 0, omitDirectKinds = [] }: { thread: ArtifactThread; hrefFor?: (node: ArtifactThreadNode) => string | undefined; excludedRecords?: number; omitDirectKinds?: ArtifactThreadNode['kind'][] }) {
  const { nodes, incoming, outgoing, upstream, downstream } = artifactTraceGroups(thread)
  const shownIncoming = incoming.filter(edge => !omitDirectKinds.includes(nodes.get(edge.fromId)!.kind))
  const shownOutgoing = outgoing.filter(edge => !omitDirectKinds.includes(nodes.get(edge.toId)!.kind))
  const row = (node: ArtifactThreadNode, relation: string, suspect = false) => <TraceRelation
    key={`${node.kind}:${node.id}:${relation}`} label={node.displayNumber || 'Unnumbered record'} href={hrefFor?.(node)}
    title={node.title} attention={suspect}
    detail={`${relation} · ${node.kind}${node.state ? ` · ${stateLabel(node.state)}` : ''}${suspect ? ' · Suspect relationship' : ''}`}>
    {node.outcome && <small>Recorded outcome: {stateLabel(node.outcome)}</small>}
    {node.evidence.length > 0 && <ul aria-label="Recorded evidence">{node.evidence.map(file => <li key={file.id}>
      {file.fileName} · SHA-256 {file.sha256}
    </li>)}</ul>}
  </TraceRelation>
  return <>
    {excludedRecords > 0 && <p className="inspectorNote">{excludedRecords} historically connected records are outside this exact build trace. Open the complete Digital Thread to investigate historical context.</p>}
    <TraceGroup title="Upstream context" count={shownIncoming.length} empty="No additional direct upstream context is recorded in this exact scope.">
      {shownIncoming.map(edge => row(nodes.get(edge.fromId)!, traceRelationLabelFor(edge.relation, true), edge.isSuspect))}
    </TraceGroup>
    <TraceGroup title="Downstream context" count={shownOutgoing.length} empty="No additional direct downstream context is recorded in this exact scope.">
      {shownOutgoing.map(edge => row(nodes.get(edge.toId)!, traceRelationLabelFor(edge.relation, false), edge.isSuspect))}
    </TraceGroup>
    {([['upstream', upstream], ['downstream', downstream]] as const).map(([direction, context]) =>
      <TraceGroup key={direction} title={`Further ${direction} context`} count={context.indirect.length} empty={`No additional ${direction} context is recorded in this exact scope.`}>
        {context.indirect.map(node => row(node, `Indirect ${direction} context · ${context.distance.get(node.id)} hops`))}
      </TraceGroup>)}
    {!thread.verification.isApplicable && <p className="inspectorNote">{thread.verification.reason}</p>}
  </>
}
