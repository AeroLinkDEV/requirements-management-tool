import { useEffect, useState } from 'react'
import { ControlledArtifactInspector } from './ControlledArtifactExplorer'
import { PersonName } from './People'
import { stateLabel } from './presentation'
import './RequirementsWorkspace.css'

type TraceNode = {
  id: string
  kind: string
  displayNumber: string
  title?: string | null
  state?: string | null
  revision?: number | null
  level?: string | null
}
type Provenance = {
  kind: string
  rationale?: string | null
  actorId?: string | null
  status?: string | null
  isLive?: boolean
  buildVersion?: string | null
  upstreamBuildVersion?: string | null
}
type TraceEdge = {
  fromId: string
  fromKind: string
  toId: string
  toKind: string
  relation: string
  provenance: Provenance[]
}
type Trace = {
  rootChangeRequestId: string
  rootArtifactId?: string | null
  rootArtifactKind?: string | null
  nodes: TraceNode[]
  edges: TraceEdge[]
  state?: { upstream: string; downstream: string; overall: string; warnings: string[] }
}
type ChangeDetail = {
  id: string
  displayNumber: string
  baseNumber: string
  revision: number
  title: string
  problem?: string
  analysis?: string
  solution?: string
  state: string
  authorId?: string
  updatedAt?: string
  createdAt?: string
  targetReleaseId?: string
  requirementChanges?: { id: string; displayNumber: string; revision: number; kind: string; title?: string; statement?: string; rationale?: string }[]
  reviewCycles?: { id: string; sequence: number; state: string; startedAt: string; completedAt?: string; steps?: { stageName?: string; state: string; approverName?: string; decidedAt?: string }[] }[]
  audit?: { eventType: string; actorId: string; detail: string; occurredAt: string }[]
  reviewCycle?: { id: string; sequence: number; state: string; steps?: { stageName?: string; state: string; approverName?: string; decidedAt?: string }[] }
  artifactChanges?: { id: string; displayNumber: string; revision: number; kind: string; title?: string; statement?: string; rationale?: string }[]
  procedureChanges?: { id: string; displayNumber: string; revision: number; kind: string; title?: string; statement?: string; rationale?: string }[]
  originKind?: string
  originDisplayIdentity?: string
  originDisplayTitle?: string
  sourceChangeRequestNumber?: string
}
type Comment = { id: string; authorId: string; body: string; state: string; createdAt: string; disposition?: string }

const tabs = [
  { id: 'overview', label: 'Overview' },
  { id: 'trace', label: 'Trace & impact' },
  { id: 'history', label: 'History' },
  { id: 'discussion', label: 'Discussion' },
]

const nodeLabel = (node: TraceNode) => `${node.displayNumber}${node.revision == null ? '' : ` · revision ${node.revision}`}`

export default function ChangeRequestInspector({
  api, id, kind, href, onClose, onOpen,
}: {
  api: string
  id: string
  kind: 'ChangeRequest' | 'TestChangeRequest'
  href: string
  onClose: () => void
  onOpen: (id: string) => void
}) {
  const [detail, setDetail] = useState<ChangeDetail>()
  const [trace, setTrace] = useState<Trace>()
  const [comments, setComments] = useState<Comment[]>([])
  const [tab, setTab] = useState('overview')
  const [loading, setLoading] = useState(true)
  const [failure, setFailure] = useState('')

  useEffect(() => {
    let active = true
    setLoading(true)
    setFailure('')
    setDetail(undefined)
    setTrace(undefined)
    setComments([])
    setTab('overview')
    const detailUrl = kind === 'ChangeRequest'
      ? `${api}/api/change-requests/${id}`
      : `${api}/api/test-change-reviews/${id}/case-changes`
    const fallbackUrl = kind === 'TestChangeRequest'
      ? `${api}/api/test-change-reviews/${id}/procedure-changes`
      : undefined
    Promise.all([
      fetch(detailUrl).then(async response => {
        if (response.ok) return response.json() as Promise<ChangeDetail>
        if (fallbackUrl && response.status === 404) {
          const fallback = await fetch(fallbackUrl)
          if (fallback.ok) return fallback.json() as Promise<ChangeDetail>
        }
        throw new Error('detail')
      }),
      fetch(`${api}/${kind === 'ChangeRequest' ? `api/change-requests/${id}/trace` : `api/test-change-reviews/${id}/trace`}`)
        .then(response => response.ok ? response.json() as Promise<Trace> : undefined)
        .catch(() => undefined),
      kind === 'ChangeRequest'
        ? fetch(`${api}/api/change-requests/${id}/review-comments`)
            .then(async response => response.ok ? (await response.json() as { cycles?: { comments?: Comment[] }[] }).cycles?.flatMap(x => x.comments ?? []) ?? [] : [])
            .catch(() => [])
        : Promise.resolve([]),
    ]).then(([nextDetail, nextTrace, nextComments]) => {
      if (!active) return
      setDetail(nextDetail)
      setTrace(nextTrace)
      setComments(nextComments)
    }).catch(() => { if (active) setFailure('This controlled record could not be loaded in the current Project.')
    }).finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [api, id, kind])

  if (loading) return <aside className="requirementInspector" aria-label="Change request detail"><div className="inspectorTop"><div><span>CONTROLLED RECORD</span><h2>Loading…</h2></div><button type="button" className="inspectorClose" aria-label="Close change request inspector" onClick={onClose}>×</button></div><div className="inspectorBody"><p>Loading controlled record…</p></div></aside>
  if (failure || !detail) return <aside className="requirementInspector" aria-label="Unavailable change request detail"><div className="inspectorTop"><div><span>CONTROLLED RECORD</span><h2>Unavailable</h2></div><button type="button" className="inspectorClose" aria-label="Close change request inspector" onClick={onClose}>×</button></div><div className="inspectorBody"><p className="inspectorNote warn">{failure || 'The controlled record is unavailable.'}</p></div></aside>

  const changes = detail.requirementChanges ?? detail.artifactChanges ?? detail.procedureChanges ?? []
  const rootId = trace?.rootArtifactId || trace?.rootChangeRequestId || id
  const isUpstream = (edge: TraceEdge) => edge.relation === 'Upstream'
    ? edge.fromId === rootId
    : edge.toId === rootId
  const isDownstream = (edge: TraceEdge) => edge.relation === 'Upstream'
    ? edge.toId === rootId
    : edge.fromId === rootId
  const upstream = trace?.edges.filter(isUpstream) ?? []
  const downstream = trace?.edges.filter(isDownstream) ?? []
  const nodeById = new Map((trace?.nodes ?? []).map(node => [node.id, node]))
  const cycle = detail.reviewCycles?.at(-1) ?? detail.reviewCycle

  return <ControlledArtifactInspector
    artifactType={kind === 'ChangeRequest' ? 'CHANGE REQUEST' : 'TEST CHANGE REQUEST'}
    displayNumber={detail.displayNumber}
    subtitle={`${stateLabel(detail.state)} · exact revision ${detail.revision}`}
    closeLabel="Close change request inspector"
    onClose={onClose}
    tabs={tabs}
    activeTab={tab}
    onTab={setTab}
  >
    {tab === 'overview' && <div className="inspectorBody">
      <a className="impactLaunch" href={href} onClick={event => {
        if (event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) {
          event.preventDefault(); onOpen(id)
        }
      }}>Open change request →</a>
      <p className="changeBoundaryNote">Exact controlled revision {detail.displayNumber}. Opening the record uses its Project and build authorization.</p>
      <h3>Title</h3><div className="richRequirement">{detail.title || 'Not written up yet'}</div>
      <dl>
        <div><dt>State</dt><dd>{stateLabel(detail.state)}</dd></div>
        <div><dt>Revision</dt><dd>{detail.displayNumber} · {detail.revision}</dd></div>
        {detail.authorId && <div><dt>Author</dt><dd><PersonName userName={detail.authorId} /></dd></div>}
        {detail.originKind && <div><dt>Origin</dt><dd>{detail.originKind}{detail.originDisplayIdentity ? ` · ${detail.originDisplayIdentity}` : ''}</dd></div>}
      </dl>
      <h3>Proposed controlled changes</h3>
      {changes.length ? changes.map(change => <article className="traceRelation" key={change.id}><b>{change.displayNumber} · {change.kind}</b><p>{change.title || change.statement || 'Controlled change proposal'}</p>{change.rationale && <small>Rationale: {change.rationale}</small>}</article>) : <div className="traceEmpty"><span>No proposed controlled changes are recorded.</span></div>}
    </div>}

    {tab === 'trace' && <div className="inspectorBody traceInspector">
      {trace?.state && <div className="traceSummary"><article><b>{stateLabel(trace.state.upstream)}</b><span>upstream</span></article><article><b>{stateLabel(trace.state.downstream)}</b><span>downstream</span></article><article><b>{stateLabel(trace.state.overall)}</b><span>overall</span></article></div>}
      {trace?.state?.warnings?.map(warning => <p className="inspectorNote warn" key={warning}>{warning}</p>)}
      <h3>Upstream</h3>
      {upstream.length ? upstream.map(edge => { const otherId = edge.toId === rootId ? edge.fromId : edge.toId; const otherKind = edge.toId === rootId ? edge.fromKind : edge.toKind; const node = nodeById.get(otherId); return <TraceEdgeCard key={`${edge.fromId}-${edge.toId}-${edge.relation}`} edge={edge} node={node} otherKind={otherKind} /> }) : <div className="traceEmpty"><span>No upstream change request is recorded.</span></div>}
      <h3>Downstream / verification impact</h3>
      {downstream.length ? downstream.map(edge => { const otherId = edge.fromId === rootId ? edge.toId : edge.fromId; const otherKind = edge.fromId === rootId ? edge.toKind : edge.fromKind; const node = nodeById.get(otherId); return <TraceEdgeCard key={`${edge.fromId}-${edge.toId}-${edge.relation}`} edge={edge} node={node} otherKind={otherKind} /> }) : <div className="traceEmpty"><span>No downstream change request or verification impact is recorded.</span></div>}
      {!trace && <p className="inspectorNote warn">The server did not expose a trace projection for this exact record. No client-side relationship has been inferred.</p>}
    </div>}

    {tab === 'history' && <div className="inspectorBody">
      <div className="traceRevisionIdentity"><b>{detail.displayNumber}</b><span>Exact controlled revision {detail.revision}</span></div>
      {detail.reviewCycles?.map(item => <article className="revisionCard" key={item.id}><div><b>Review cycle {item.sequence}</b><i>{item.state}</i></div><small>{new Date(item.startedAt).toLocaleString()}{item.completedAt ? ` · completed ${new Date(item.completedAt).toLocaleString()}` : ''}</small>{item.steps?.map(step => <p key={`${item.id}-${step.stageName}`}><b>{step.stageName || 'Review step'}</b> · {step.state}{step.approverName ? ` · ${step.approverName}` : ''}</p>)}</article>)}
      {detail.audit?.map(item => <article className="revisionCard" key={`${item.eventType}-${item.occurredAt}`}><div><b>{item.eventType}</b><i>{new Date(item.occurredAt).toLocaleString()}</i></div><p>{item.detail}</p><small>Recorded by <PersonName userName={item.actorId} /></small></article>)}
      {!detail.reviewCycles?.length && !detail.audit?.length && cycle && <article className="revisionCard"><div><b>Review cycle {cycle.sequence}</b><i>{cycle.state}</i></div><p>Review evidence is held by the controlled review cycle.</p></article>}
      {!detail.reviewCycles?.length && !detail.audit?.length && !cycle && <div className="traceEmpty"><span>No additional immutable history is recorded for this revision.</span></div>}
    </div>}

    {tab === 'discussion' && <div className="inspectorBody discussionPane">
      {kind === 'ChangeRequest' && comments.length ? comments.map(comment => <article key={comment.id} className={comment.state.toLowerCase()}><div><b><PersonName userName={comment.authorId} /></b><span>{new Date(comment.createdAt).toLocaleString()}</span></div><p>{comment.body}</p>{comment.disposition && <small>Disposition: {comment.disposition}</small>}<footer><i>{comment.state}</i></footer></article>) : <div className="traceEmpty"><span>{kind === 'TestChangeRequest' ? 'TCR discussion is available on the full controlled package. No separate client-side discussion has been inferred here.' : 'No review discussion is recorded for this change request.'}</span></div>}
    </div>}
  </ControlledArtifactInspector>
}

function TraceEdgeCard({ edge, node, otherKind }: { edge: TraceEdge; node?: TraceNode; otherKind: string }) {
  return <article className="traceRelation"><div className="traceRequirementHead"><b>{node ? nodeLabel(node) : 'Exact connected artifact'}</b><span>{node?.kind ?? otherKind}</span></div><p>{node?.title || 'Exact connected controlled artifact'}</p>{node?.state && <small>{stateLabel(node.state)}{node.level ? ` · ${node.level}` : ''}</small>}<div className="traceProvenance">{edge.provenance.map((fact, index) => <span key={`${fact.kind}-${index}`}><b>{fact.kind === 'AssessmentDerived' ? 'AssessmentDerived' : fact.kind === 'AuthorStated' ? 'AuthorStated' : fact.kind}</b>{fact.isLive === false ? ' · historical' : ' · live'}{fact.rationale ? ` · ${fact.rationale}` : ''}{fact.status ? ` · ${fact.status}` : ''}</span>)}</div></article>
}
