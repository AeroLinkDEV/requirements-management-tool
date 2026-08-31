import { useEffect, useState } from 'react'
import { ControlledArtifactInspector } from './ControlledArtifactExplorer'
import ExactArtifactLink from './ExactArtifactLink'
import { PersonName } from './People'
import { stateLabel } from './presentation'
import { traceProvenanceLabel } from './tracePresentation'
import './RequirementsWorkspace.css'

type TraceNode = {
  id: string
  kind: string
  projectId?: string | null
  buildId?: string | null
  displayNumber: string
  title?: string | null
  state?: string | null
  revision?: number | null
  level?: string | null
  buildVersion?: string | null
  artifactId?: string | null
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
  projectId?: string
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
  releaseId?: string
  projectId?: string
  type?: string
  artifactKind?: string
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
  api, id, kind, projectId, releaseId, registerType, href, artifactHref, digitalThreadHref, onClose, onOpen,
}: {
  api: string
  id: string
  kind: 'ChangeRequest' | 'TestChangeRequest'
  projectId: string
  releaseId: string
  registerType?: string
  href: string
  artifactHref?: (node: TraceNode) => string | undefined
  digitalThreadHref?: string
  onClose: () => void
  onOpen: (id: string) => void
}) {
  const [detail, setDetail] = useState<ChangeDetail>()
  const [trace, setTrace] = useState<Trace>()
  const [comments, setComments] = useState<Comment[]>([])
  const [discussionFailure, setDiscussionFailure] = useState(false)
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
    setDiscussionFailure(false)
    setTab('overview')
    const detailUrl = kind === 'ChangeRequest'
      ? `${api}/api/change-requests/${id}`
      : `${api}/api/test-change-reviews/${id}/case-changes`
    const fallbackUrl = kind === 'TestChangeRequest'
      ? `${api}/api/test-change-reviews/${id}/procedure-changes`
      : undefined
    type DiscussionResult = { comments: Comment[]; failed: boolean }
    const discussionPromise: Promise<DiscussionResult> = kind === 'ChangeRequest'
      ? fetch(`${api}/api/change-requests/${id}/review-comments`)
          .then(async response => {
            if (!response.ok) return { comments: [], failed: true }
            try {
              return {
                comments: (await response.json() as { cycles?: { comments?: Comment[] }[] }).cycles?.flatMap(x => x.comments ?? []) ?? [],
                failed: false,
              }
            } catch {
              return { comments: [], failed: true }
            }
          })
          .catch(() => ({ comments: [], failed: true }))
      : Promise.resolve({ comments: [], failed: false })
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
      discussionPromise,
    ]).then(([nextDetail, nextTrace, discussion]) => {
      if (!active) return
      const detailReleaseId = kind === 'ChangeRequest' ? nextDetail.targetReleaseId : nextDetail.releaseId
      const detailType = kind === 'ChangeRequest' ? nextDetail.type : nextDetail.artifactKind
      const traceRootId = nextTrace?.rootArtifactId ?? nextTrace?.rootChangeRequestId
      const traceRoot = traceRootId ? nextTrace?.nodes.find(node => node.id === traceRootId) : undefined
      const scopeMatches = nextDetail.projectId === projectId
        && detailReleaseId === releaseId
        && (!registerType || detailType === registerType)
        && (!nextTrace || (
          nextTrace.projectId === projectId
          && nextTrace.rootArtifactId === id
          && nextTrace.rootArtifactKind === (kind === 'ChangeRequest' ? 'ChangeRequest' : 'TestChangeRequest')
          && traceRoot?.projectId === projectId
          && traceRoot.buildId === releaseId
          && traceRoot.kind === (kind === 'ChangeRequest' ? 'ChangeRequest' : 'TestChangeRequest')
        ))
      if (!scopeMatches) {
        setFailure('This controlled record is outside the current Project, build, or register.')
        return
      }
      setDetail(nextDetail)
      setTrace(nextTrace)
      setComments(discussion.comments)
      setDiscussionFailure(discussion.failed)
    }).catch(() => { if (active) setFailure('This controlled record could not be loaded in the current Project.')
    }).finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [api, id, kind, projectId, releaseId, registerType])

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
    ariaLabel={`${detail.displayNumber} detail`}
    displayNumber={<ExactArtifactLink href={href} onOpen={() => onOpen(id)}>{detail.displayNumber}</ExactArtifactLink>}
    subtitle={`${stateLabel(detail.state)} · exact revision ${detail.revision}`}
    closeLabel="Close change request inspector"
    onClose={onClose}
    tabs={tabs}
    activeTab={tab}
    onTab={setTab}
  >
    {tab === 'overview' && <div className="inspectorBody">
      <ExactArtifactLink className="impactLaunch" href={href} onOpen={() => onOpen(id)}>Open change request →</ExactArtifactLink>
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
      {trace?.state && <p className="traceStateLine"><span>Trace status</span><b>{stateLabel(trace.state.upstream)}</b><i>upstream</i><b>{stateLabel(trace.state.downstream)}</b><i>downstream</i><b>{stateLabel(trace.state.overall)}</b><i>overall</i></p>}
      {trace?.state?.warnings?.map(warning => <p className="inspectorNote warn" key={warning}>{warning}</p>)}
      <h3>Upstream</h3>
      {upstream.length ? upstream.map(edge => { const otherId = edge.toId === rootId ? edge.fromId : edge.toId; const otherKind = edge.toId === rootId ? edge.fromKind : edge.toKind; const node = nodeById.get(otherId); return <TraceEdgeCard key={`${edge.fromId}-${edge.toId}-${edge.relation}`} edge={edge} node={node} otherKind={otherKind} href={node ? artifactHref?.(node) : undefined} /> }) : <div className="traceEmpty"><span>No immediate upstream relationship is recorded.</span></div>}
      <h3>Downstream / verification impact</h3>
      {downstream.length ? downstream.map(edge => { const otherId = edge.fromId === rootId ? edge.toId : edge.fromId; const otherKind = edge.fromId === rootId ? edge.toKind : edge.fromKind; const node = nodeById.get(otherId); return <TraceEdgeCard key={`${edge.fromId}-${edge.toId}-${edge.relation}`} edge={edge} node={node} otherKind={otherKind} href={node ? artifactHref?.(node) : undefined} /> }) : <div className="traceEmpty"><span>No immediate downstream relationship or verification impact is recorded.</span></div>}
      {digitalThreadHref && <ExactArtifactLink className="openDigitalThread" href={digitalThreadHref}>Open Digital Thread →</ExactArtifactLink>}
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
      {discussionFailure ? <p className="inspectorNote warn">Discussion is unavailable for this controlled record. No discussion content has been inferred.</p> : kind === 'ChangeRequest' && comments.length ? comments.map(comment => <article key={comment.id} className={comment.state.toLowerCase()}><div><b><PersonName userName={comment.authorId} /></b><span>{new Date(comment.createdAt).toLocaleString()}</span></div><p>{comment.body}</p>{comment.disposition && <small>Disposition: {comment.disposition}</small>}<footer><i>{comment.state}</i></footer></article>) : <div className="traceEmpty"><span>{kind === 'TestChangeRequest' ? 'TCR discussion is available on the full controlled package. No separate client-side discussion has been inferred here.' : 'No review discussion is recorded for this change request.'}</span></div>}
    </div>}
  </ControlledArtifactInspector>
}

function TraceEdgeCard({ edge, node, otherKind, href }: { edge: TraceEdge; node?: TraceNode; otherKind: string; href?: string }) {
  return <article className="traceRelation"><div className="traceRequirementHead"><ExactArtifactLink href={href}>{node ? nodeLabel(node) : 'Exact connected artifact'}</ExactArtifactLink><span>{node?.kind ?? otherKind}</span></div><p>{node?.title || 'Exact connected controlled artifact'}</p>{node?.state && <small>{stateLabel(node.state)}{node.level ? ` · ${node.level}` : ''}{node.buildVersion ? ` · Build ${node.buildVersion}` : ''}</small>}<div className="traceProvenance">{edge.provenance.map((fact, index) => <span key={`${fact.kind}-${index}`}><b>{traceProvenanceLabel(fact.kind)}</b>{fact.isLive === false ? ' · Historical evidence' : ''}{fact.rationale ? ` · ${fact.rationale}` : ''}{fact.status ? ` · ${fact.status}` : ''}</span>)}</div></article>
}
