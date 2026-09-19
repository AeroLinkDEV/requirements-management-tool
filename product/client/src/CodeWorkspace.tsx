import { useState } from 'react'
import { ControlledArtifactExplorerHeader, ControlledArtifactInspector } from './ControlledArtifactExplorer'
import CodeSourcePanel, { type CodeSource } from './CodeSourcePanel'
import CodeTraceabilityCenter from './CodeTraceabilityCenter'
import CodeLinkPicker from './CodeLinkPicker'
import CodeRelationshipList from './CodeRelationshipList'
import { stateLabel } from './presentation'
import { codeQuery, mergeRequestState, useCodeRead, type CodePage, type CodeRelationship, type InspectedMergeRequest,
  type MergeRequest, type MetadataObservation, type RegisteredMergeRequest, type TreeEntry, type TreePage } from './codeWorkspaceData'
import './RequirementsWorkspace.css'
import './CodeWorkspace.css'

export type CodeWorkspacePage = 'mergeRequests' | 'explorer'
type Props = { api: string; projectId: string; releaseId: string; readOnly: boolean; page: CodeWorkspacePage;
  onBack: () => void; onPage: (page: CodeWorkspacePage) => void }

export default function CodeWorkspace(props: Props) {
  return <ScopedCodeWorkspace key={`${props.api}/${props.projectId}/${props.releaseId}/${props.page}`} {...props} />
}

function ScopedCodeWorkspace({ api, projectId, releaseId, readOnly, page, onBack, onPage }: Props) {
  const [source, setSource] = useState<CodeSource>()
  const currentSource = source?.projectId === projectId && source.releaseId === releaseId ? source : undefined
  return <main className="codeWorkspace">
    <ControlledArtifactExplorerHeader back={{ label: 'Command Center', onClick: onBack }} eyebrow="CODE"
      title={page === 'mergeRequests' ? 'Merge Requests' : 'Code Explorer'} />
    <nav className="codeWorkspaceTabs" aria-label="Code pages">
      <button aria-current={page === 'mergeRequests' ? 'page' : undefined} onClick={() => onPage('mergeRequests')}>Merge Requests</button>
      <button aria-current={page === 'explorer' ? 'page' : undefined} onClick={() => onPage('explorer')}>Code Explorer</button>
    </nav>
    <p className="codeBoundary">GitLab owns source and merge review. Recorded relationships provide context; accepted implementation evidence is a separate engineering decision.</p>
    <CodeSourcePanel {...{ api, projectId, releaseId, readOnly }} onSource={setSource} />
    {page === 'mergeRequests' ? <MergeRequestRegister {...{ api, projectId, releaseId }} readOnly={readOnly || !currentSource?.capabilities?.canSelect} />
      : <SourceExplorer key={currentSource?.selectionEventId ?? 'unselected'} {...{ api, projectId, releaseId }} readOnly={readOnly || !currentSource?.capabilities?.canSelect} source={currentSource} />}
    <details className="codeEvidenceSection"><summary>Implementation evidence and build gate</summary>
      <CodeTraceabilityCenter {...{ api, projectId, releaseId, readOnly, onBack }} embedded />
    </details>
  </main>
}

function MergeRequestRegister({ api, projectId, releaseId, readOnly }: Pick<Props, 'api' | 'projectId' | 'releaseId' | 'readOnly'>) {
  const [linking, setLinking] = useState(false)
  const [mode, setMode] = useState<'linked' | 'discover'>('linked')
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [state, setState] = useState('all')
  const [refresh, setRefresh] = useState(0)
  const [selected, setSelected] = useState<{ iid: number; origin?: string; remoteProjectId?: number }>()
  const base = `${api}/api/projects/${projectId}`
  const linked = useCodeRead<CodePage<RegisteredMergeRequest>>(mode === 'linked'
    ? `${base}/code/merge-requests/register?${codeQuery({ releaseId, page, pageSize: 25 })}` : undefined, refresh)
  const discovered = useCodeRead<MetadataObservation<MergeRequest[]>>(mode === 'discover'
    ? `${base}/repository/merge-requests?${codeQuery({ search: query, state, page, pageSize: 25 })}` : undefined, refresh)
  const detail = useCodeRead<InspectedMergeRequest>(selected && mode === 'linked'
    ? `${base}/code/merge-requests/${selected.iid}?${codeQuery({ releaseId, instanceBaseUrl: selected.origin, remoteProjectId: selected.remoteProjectId })}` : undefined, refresh)
  const remoteDetail = useCodeRead<MetadataObservation<MergeRequest>>(selected && mode === 'discover'
    ? `${base}/repository/merge-requests/${selected.iid}` : undefined, refresh)
  const rows = mode === 'linked' ? linked.value?.items.map(item => ({ key: `${item.instanceBaseUrl}/${item.remoteProjectId}/${item.mergeRequestIid}`,
    iid: item.mergeRequestIid, origin: item.instanceBaseUrl, remoteProjectId: item.remoteProjectId,
    mr: item.metadataKnown ? item.metadata : undefined, count: item.relationshipCount }))
    : discovered.value?.observation.value?.map(item => ({ key: String(item.iid), iid: item.iid, mr: item,
      origin: undefined, remoteProjectId: undefined, count: undefined }))
  const error = mode === 'linked' ? linked.error : discovered.error
    ?? (discovered.value && !discovered.value.observation.succeeded ? discovered.value.observation.detail : undefined)
  const loading = mode === 'linked' ? linked.loading : discovered.loading
  const mr = mode === 'linked' ? (detail.value?.metadataKnown ? detail.value.metadata : undefined) : remoteDetail.value?.observation.value
  const next = mode === 'linked' ? !!linked.value && page * 25 < linked.value.total : !!discovered.value?.observation.nextPage
  return <section aria-label="Merge request register">
    <div className="codeCommandBar">
      <div role="group" aria-label="Merge request scope">
        <button aria-pressed={mode === 'linked'} onClick={() => { setMode('linked'); setPage(1); setSelected(undefined) }}>Linked to this build</button>
        <button aria-pressed={mode === 'discover'} onClick={() => { setMode('discover'); setPage(1); setSelected(undefined) }}>Find in GitLab</button>
      </div>
      {mode === 'discover' && <form onSubmit={event => { event.preventDefault(); setPage(1); setQuery(search); setSelected(undefined) }}>
        <input aria-label="Search GitLab merge requests" value={search} onChange={event => setSearch(event.target.value)} />
        <select aria-label="GitLab merge request state" value={state} onChange={event => { setState(event.target.value); setPage(1); setSelected(undefined) }}>
          <option value="all">All states</option><option value="opened">Open</option><option value="merged">Merged</option><option value="closed">Closed</option>
        </select><button>Search GitLab</button>
      </form>}
      <button onClick={() => setRefresh(value => value + 1)}>Refresh merge requests</button>
    </div>
    <p>{mode === 'linked' ? 'Recorded relationships for this build, including records whose GitLab metadata is unavailable.'
      : 'Live GitLab discovery. Appearing here does not mean a merge request is linked or accepted in AeroLink.'}</p>
    {error && <p role="alert">{error}</p>}
    <div className="codeRegisterLayout">
      <div>{loading ? <p role="status">Loading merge requests…</p> : <>
        <table><thead><tr><th scope="col">MR</th><th scope="col">Title</th><th scope="col">GitLab state</th><th scope="col">Recorded relationships</th></tr></thead>
          <tbody>{rows?.map(row => <tr key={row.key} aria-selected={selected?.iid === row.iid && selected.origin === row.origin && selected.remoteProjectId === row.remoteProjectId}>
            <td><button onClick={() => setSelected({ iid: row.iid, origin: row.origin, remoteProjectId: row.remoteProjectId })}>!{row.iid}</button></td>
            <td>{row.mr?.title ?? 'Metadata unavailable'}</td><td>{mergeRequestState(row.mr)}</td><td>{row.count ?? 'Not evaluated in discovery'}</td>
          </tr>)}</tbody></table>
        {rows?.length === 0 && <p>{mode === 'linked' ? 'No merge request relationship is recorded for this build.' : 'No merge requests returned for this GitLab query.'}</p>}
        <div className="codePagination"><button disabled={page <= 1} onClick={() => { setPage(value => value - 1); setSelected(undefined) }}>Previous page</button>
          <span>Page {page}{mode === 'linked' && linked.value ? ` · ${linked.value.total} recorded merge requests` : ''}</span>
          <button disabled={!next} onClick={() => { setPage(value => value + 1); setSelected(undefined) }}>Next page</button></div>
      </>}</div>
      {selected ? <ControlledArtifactInspector artifactType="GitLab merge request" displayNumber={`!${selected.iid}`}
        subtitle="GitLab metadata and recorded AeroLink context" closeLabel="Close merge request inspector"
        onClose={() => setSelected(undefined)} tabs={[{ id: 'details', label: 'Details and relationships' }]} activeTab="details" onTab={() => {}}>
        {(detail.loading || remoteDetail.loading) && <p>Loading merge request details…</p>}
        {(detail.error || remoteDetail.error) && <p role="alert">{detail.error || remoteDetail.error}</p>}
        {mr ? <><h3>{mr.title}</h3><p>{mergeRequestState(mr)}</p><p>{mr.sourceBranch} → {mr.targetBranch}</p>
          <p>{mr.approvals?.known ? `${mr.approvals.approvedBy.length} recorded GitLab approval(s)` : 'GitLab approvals unknown'}</p>
          {mr.approvals?.known && <ul>{mr.approvals.approvedBy.map(person => <li key={person.id}>{person.name} (@{person.username})</li>)}</ul>}
          <a href={mr.webUrl} target="_blank" rel="noreferrer">Open in GitLab ↗</a>
          {!readOnly && <p><button onClick={() => setLinking(true)}>Link AeroLink artifact</button></p>}
        </> : !detail.loading && !remoteDetail.loading && <p>Current GitLab metadata is unknown. Retained relationships remain readable.</p>}
        {mode === 'linked' && <CodeRelationshipList {...{ api, projectId, readOnly }} onChanged={() => setRefresh(value => value + 1)} items={[...(detail.value?.mergeRequests ?? []), ...(detail.value?.files ?? [])]} />}
      </ControlledArtifactInspector> : <aside className="codeEmptyInspector">Select a merge request to inspect its details and relationships.</aside>}
    </div>
    {linking && selected && mr && <CodeLinkPicker key={`${selected.origin}/${selected.remoteProjectId}/${selected.iid}`} {...{ api, projectId, releaseId }}
      subject={{ kind: 'MergeRequest', iid: selected.iid }} onClose={() => setLinking(false)}
      onSaved={() => { setLinking(false); setRefresh(value => value + 1) }} />}
  </section>
}

function SourceExplorer({ api, projectId, releaseId, source, readOnly }: Pick<Props, 'api' | 'projectId' | 'releaseId' | 'readOnly'> & { source?: CodeSource }) {
  const [linking, setLinking] = useState(false)
  const [path, setPath] = useState('')
  const [cursors, setCursors] = useState<string[]>([''])
  const [selected, setSelected] = useState<TreeEntry>()
  const [linkPage, setLinkPage] = useState(1)
  const [refresh, setRefresh] = useState(0)
  const snapshot = source?.snapshot
  const base = `${api}/api/projects/${projectId}`
  const tree = useCodeRead<MetadataObservation<TreePage>>(snapshot
    ? `${base}/code/source/${snapshot.id}/tree?${codeQuery({ commit: snapshot.commitSha, path, cursor: cursors.at(-1), pageSize: 25 })}` : undefined, refresh)
  const links = useCodeRead<CodePage<CodeRelationship>>(snapshot && selected
    ? `${base}/code/relationships?${codeQuery({ releaseId, relationshipKind: 'File', sourceSnapshotId: snapshot.id, path: selected.path, page: linkPage, pageSize: 25 })}` : undefined, refresh)
  if (!snapshot) return <section className="codeEmptySource"><h2>Choose a build source to browse files</h2>
    <p>Every directory is read at the selected exact commit. Unlinked files remain visible.</p></section>
  const changePath = (next: string) => { setPath(next); setCursors(['']); setSelected(undefined) }
  const entries = tree.value?.observation.value?.entries
  const nextCursor = tree.value?.observation.value?.nextCursor
  const selectedFileObserved = tree.value?.observation.succeeded === true
    && tree.value.observation.value?.commitSha === snapshot.commitSha
    && entries?.some(entry => entry.kind === 'Blob' && entry.path === selected?.path)
  return <section aria-label="Repository files">
    <div className="codeCommandBar"><nav aria-label="Repository directory"><button onClick={() => changePath('')}>Repository root</button>
      {path.split('/').filter(Boolean).map((segment, index, parts) => <button key={parts.slice(0, index + 1).join('/')}
        onClick={() => changePath(parts.slice(0, index + 1).join('/'))}>{segment}</button>)}</nav>
      <button onClick={() => setRefresh(value => value + 1)}>Refresh directory</button></div>
    {tree.error && <p role="alert">{tree.error}</p>}
    {tree.value && !tree.value.observation.succeeded && <p role="alert">{tree.value.observation.detail}</p>}
    <div className="codeRegisterLayout"><div>
      {tree.loading ? <p role="status">Loading directory…</p> : <ul className="codeTree">{entries?.map(entry => <li key={entry.path}>
        <button disabled={entry.kind !== 'Tree' && entry.kind !== 'Blob'} aria-pressed={selected?.path === entry.path}
          onClick={() => { if (entry.kind === 'Tree') changePath(entry.path); else { setSelected(entry); setLinkPage(1) } }}>
          <span aria-hidden="true">{entry.kind === 'Tree' ? '▸' : '▤'}</span> {entry.name}
        </button><small>{entry.kind === 'Link' || entry.kind === 'Commit' ? 'External target · not followed' : stateLabel(entry.kind)}</small>
      </li>)}</ul>}
      {entries?.length === 0 && <p>No entries returned in this directory page.</p>}
      <div className="codePagination"><button disabled={cursors.length <= 1} onClick={() => { setCursors(value => value.slice(0, -1)); setSelected(undefined) }}>Previous directory page</button>
        <span>Directory page {cursors.length}</span><button disabled={!nextCursor} onClick={() => { setCursors(value => [...value, nextCursor!]); setSelected(undefined) }}>Next directory page</button></div>
    </div>{selected ? <ControlledArtifactInspector artifactType="Repository file" displayNumber={selected.name}
      subtitle={selected.path} closeLabel="Close file inspector" onClose={() => setSelected(undefined)}
      tabs={[{ id: 'relationships', label: 'Recorded relationships' }]} activeTab="relationships" onTab={() => {}}>
      <small>EXACT SOURCE</small><code>{snapshot.commitSha}</code>
      <p><a href={`${snapshot.instanceBaseUrl}/${snapshot.pathWithNamespace.split('/').map(encodeURIComponent).join('/')}/-/blob/${snapshot.commitSha}/${selected.path.split('/').map(encodeURIComponent).join('/')}`}
        target="_blank" rel="noreferrer">Open exact file in GitLab ↗</a></p>
      {!readOnly && selectedFileObserved && <button onClick={() => setLinking(true)}>Link AeroLink artifact</button>}
      {links.loading && <p>Loading recorded relationships…</p>}{links.error && <p role="alert">{links.error}</p>}
      {links.value && <CodeRelationshipList {...{ api, projectId, readOnly }} onChanged={() => setRefresh(value => value + 1)} items={links.value.items} empty="No relationship recorded for this file." />}
      {links.value && links.value.total > 25 && <div className="codePagination">
        <button disabled={linkPage <= 1} onClick={() => setLinkPage(value => value - 1)}>Previous relationship page</button>
        <span>Page {linkPage} · {links.value.total} relationships</span>
        <button disabled={linkPage * 25 >= links.value.total} onClick={() => setLinkPage(value => value + 1)}>Next relationship page</button>
      </div>}
    </ControlledArtifactInspector> : <aside className="codeEmptyInspector">Select a file to inspect recorded relationships. Source content opens in GitLab.</aside>}</div>
    {linking && selected && source && selectedFileObserved && <CodeLinkPicker key={selected.path} {...{ api, projectId, releaseId }}
      subject={{ kind: 'File', path: selected.path, parentPath: path, cursor: cursors.at(-1), source }}
      onClose={() => setLinking(false)} onSaved={() => { setLinking(false); setRefresh(value => value + 1) }} />}
  </section>
}
