import { useEffect, useRef, useState, type ReactNode } from 'react'
import { ControlledArtifactExplorerHeader, ControlledArtifactExplorerLayout, ControlledArtifactInspector, ControlledArtifactInspectorEmpty } from './ControlledArtifactExplorer'
import CodeSourcePanel, { type CodeSource } from './CodeSourcePanel'
import CodeTraceabilityCenter from './CodeTraceabilityCenter'
import CodeLinkPicker from './CodeLinkPicker'
import CodeRelationshipList from './CodeRelationshipList'
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

type ArtifactLinkContext = { fixedTarget?: { kind: string; id: string }; onLinked?: () => void }
function useLinkReturnFocus(linking: boolean, subjectKey: string) {
  const opener = useRef<HTMLButtonElement>(null)
  const wasLinking = useRef(false)
  const restore = useRef(false)
  const previousSubject = useRef(subjectKey)
  useEffect(() => {
    if (previousSubject.current !== subjectKey) {
      restore.current = false
      wasLinking.current = false
      previousSubject.current = subjectKey
    }
    if (wasLinking.current && !linking) restore.current = true
    wasLinking.current = linking
    // Saving refreshes metadata and temporarily removes the opener. Restore when it returns.
    const focused = document.activeElement
    if (!linking && restore.current && focused instanceof HTMLElement && focused.isConnected
      && focused !== document.body && focused !== document.documentElement && focused !== opener.current) {
      restore.current = false
    }
    if (!linking && restore.current && opener.current) {
      opener.current.focus()
      restore.current = false
    }
  })
  return opener
}
function CodeRegisterFrame({ resizableKey, children, inspector }: {
  resizableKey: string
  children: ReactNode
  inspector: ReactNode
}) {
  return <ControlledArtifactExplorerLayout inspecting resizableKey={resizableKey} className="codeRegisterLayout">
    <div className="codeRegisterPanel">{children}</div>
    {inspector}
  </ControlledArtifactExplorerLayout>
}

export function MergeRequestRegister({ api, projectId, releaseId, readOnly, fixedTarget, onLinked }: Pick<Props, 'api' | 'projectId' | 'releaseId' | 'readOnly'> & ArtifactLinkContext) {
  const [linking, setLinking] = useState(false)
  const [mode, setMode] = useState<'linked' | 'discover'>('linked')
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [state, setState] = useState('all')
  const [refresh, setRefresh] = useState(0)
  const [selected, setSelected] = useState<{ iid: number; origin?: string; remoteProjectId?: number }>()
  const linkButton = useLinkReturnFocus(linking, `${projectId}/${releaseId}/${mode}/${selected?.origin}/${selected?.remoteProjectId}/${selected?.iid}`)
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
      : 'GitLab discovery. Appearing here does not mean a merge request is linked or accepted in AeroLink.'}</p>
    {(mode === 'linked' ? linked.value?.metadataCheckedAt : discovered.value?.checkedAt) && <p>GitLab metadata checked {new Date((mode === 'linked' ? linked.value!.metadataCheckedAt : discovered.value!.checkedAt)!).toLocaleString()} · observations may be reused for up to 15 seconds.</p>}
    {error && <p role="alert">{error}</p>}
    <CodeRegisterFrame resizableKey="code-merge-request-register" inspector={selected ? <ControlledArtifactInspector artifactType="GitLab merge request" displayNumber={`!${selected.iid}`}
      subtitle="GitLab metadata and recorded AeroLink context" closeLabel="Close merge request inspector"
      onClose={() => setSelected(undefined)} tabs={[{ id: 'details', label: 'Details and relationships' }]} activeTab="details" onTab={() => {}}>
      {(detail.loading || remoteDetail.loading) && <p>Loading merge request details…</p>}
      {(detail.error || remoteDetail.error) && <p role="alert">{detail.error || remoteDetail.error}</p>}
      {mr ? <><h3>{mr.title}</h3><p>{mergeRequestState(mr)}</p><p>{mr.sourceBranch} → {mr.targetBranch}</p>
        {(detail.value?.metadataCheckedAt || remoteDetail.value?.checkedAt) && <p>Details checked {new Date((detail.value?.metadataCheckedAt || remoteDetail.value?.checkedAt)!).toLocaleString()}</p>}
        <p>{mr.approvals?.known ? `${mr.approvals.approvedBy.length} recorded GitLab approval(s)` : 'GitLab approvals unknown'}</p>
        {mr.approvals?.known && <ul>{mr.approvals.approvedBy.map(person => <li key={person.id}>{person.name} (@{person.username})</li>)}</ul>}
        <a href={mr.webUrl} target="_blank" rel="noreferrer">Open in GitLab ↗</a>
        {!readOnly && <p><button ref={linkButton} onClick={() => setLinking(true)}>Link AeroLink artifact</button></p>}
      </> : !detail.loading && !remoteDetail.loading && <p>Current GitLab metadata is unknown. Retained relationships remain readable.</p>}
      {mode === 'linked' && <CodeRelationshipList {...{ api, projectId, readOnly }} onChanged={() => setRefresh(value => value + 1)} items={[...(detail.value?.mergeRequests ?? []), ...(detail.value?.files ?? [])]} />}
    </ControlledArtifactInspector> : <ControlledArtifactInspectorEmpty title="merge request"
      description="Select a merge request to inspect GitLab metadata and recorded AeroLink relationships." />}>
      {loading ? <p role="status">Loading merge requests…</p> : <>
        <table><thead><tr><th scope="col">MR</th><th scope="col">Title</th><th scope="col">GitLab state</th><th scope="col">Recorded relationships</th></tr></thead>
          <tbody>{rows?.map(row => <tr key={row.key} aria-selected={selected?.iid === row.iid && selected.origin === row.origin && selected.remoteProjectId === row.remoteProjectId}>
            <td><button onClick={() => setSelected({ iid: row.iid, origin: row.origin, remoteProjectId: row.remoteProjectId })}>!{row.iid}</button></td>
            <td>{row.mr ? <a href={row.mr.webUrl} target="_blank" rel="noreferrer">{row.mr.title} ↗</a> : 'Metadata unavailable'}</td><td>{mergeRequestState(row.mr)}</td><td>{row.count ?? 'Not evaluated in discovery'}</td>
          </tr>)}</tbody></table>
        {rows?.length === 0 && <p>{mode === 'linked' ? 'No merge request relationship is recorded for this build.' : 'No merge requests returned for this GitLab query.'}</p>}
        <div className="codePagination"><button disabled={page <= 1} onClick={() => { setPage(value => value - 1); setSelected(undefined) }}>Previous page</button>
          <span>Page {page}{mode === 'linked' && linked.value ? ` · ${linked.value.total} recorded merge requests` : ''}</span>
          <button disabled={!next} onClick={() => { setPage(value => value + 1); setSelected(undefined) }}>Next page</button></div>
      </>}</CodeRegisterFrame>
    {linking && selected && mr && <CodeLinkPicker key={`${selected.origin}/${selected.remoteProjectId}/${selected.iid}`} {...{ api, projectId, releaseId }}
      subject={{ kind: 'MergeRequest', iid: selected.iid }} fixedTarget={fixedTarget} onClose={() => setLinking(false)}
      onSaved={() => { setLinking(false); setRefresh(value => value + 1); onLinked?.() }} />}
  </section>
}

export function SourceExplorer({ api, projectId, releaseId, source, readOnly, fixedTarget, onLinked }: Pick<Props, 'api' | 'projectId' | 'releaseId' | 'readOnly'> & { source?: CodeSource } & ArtifactLinkContext) {
  const [linking, setLinking] = useState(false)
  const [linkedOnly, setLinkedOnly] = useState(false)
  const [filePage, setFilePage] = useState(1)
  const [fileSearch, setFileSearch] = useState('')
  const [fileQuery, setFileQuery] = useState('')
  const [path, setPath] = useState('')
  const [cursors, setCursors] = useState<string[]>([''])
  const [selected, setSelected] = useState<TreeEntry>()
  const [linkPage, setLinkPage] = useState(1)
  const [refresh, setRefresh] = useState(0)
  const snapshot = source?.snapshot
  const linkButton = useLinkReturnFocus(linking, `${projectId}/${releaseId}/${source?.selectionEventId}/${path}/${selected?.path}/${linkedOnly}`)
  const base = `${api}/api/projects/${projectId}`
  const tree = useCodeRead<MetadataObservation<TreePage>>(snapshot && !linkedOnly
    ? `${base}/code/source/${snapshot.id}/tree?${codeQuery({ commit: snapshot.commitSha, path, cursor: cursors.at(-1), pageSize: 25 })}` : undefined, refresh)
  const linkedFiles = useCodeRead<CodePage<{ path: string; relationshipCount: number }>>(snapshot && linkedOnly
    ? `${base}/code/files?${codeQuery({ releaseId, sourceSnapshotId: snapshot.id, search: fileQuery, page: filePage, pageSize: 25 })}` : undefined, refresh)
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
    <div className="codeCommandBar"><label><input type="checkbox" checked={linkedOnly} onChange={event => {
      setLinkedOnly(event.target.checked); setFilePage(1); setSelected(undefined); setLinking(false)
    }} /> Linked files only</label>
      {linkedOnly && <form onSubmit={event => { event.preventDefault(); setFileQuery(fileSearch); setFilePage(1); setSelected(undefined) }}>
        <input aria-label="Search linked file paths" value={fileSearch} maxLength={200} onChange={event => setFileSearch(event.target.value)} /><button>Search paths</button>
      </form>}</div>
    {linkedOnly && <p>Recorded active links at this source snapshot, across all directories. This is not a measure of repository coverage.</p>}
    {!linkedOnly && <>
    {tree.value?.checkedAt && <p>Directory metadata checked {new Date(tree.value.checkedAt).toLocaleString()} · exact selected source.</p>}
    <div className="codeCommandBar"><nav aria-label="Repository directory"><button onClick={() => changePath('')}>Repository root</button>
      {path.split('/').filter(Boolean).map((segment, index, parts) => <button key={parts.slice(0, index + 1).join('/')}
        onClick={() => changePath(parts.slice(0, index + 1).join('/'))}>{segment}</button>)}</nav>
      <button onClick={() => setRefresh(value => value + 1)}>Refresh directory</button></div></>}
    {tree.error && <p role="alert">{tree.error}</p>}
    {tree.value && !tree.value.observation.succeeded && <p role="alert">{tree.value.observation.detail}</p>}
    {linkedFiles.error && <p role="alert">{linkedFiles.error}</p>}
    <CodeRegisterFrame resizableKey="code-source-explorer" inspector={selected ? <ControlledArtifactInspector artifactType="Repository file" displayNumber={selected.name}
      subtitle={selected.path} closeLabel="Close file inspector" onClose={() => setSelected(undefined)}
      tabs={[{ id: 'relationships', label: 'Recorded relationships' }]} activeTab="relationships" onTab={() => {}}>
      <small>EXACT SOURCE</small><code>{snapshot.commitSha}</code>
      <p><a href={`${snapshot.instanceBaseUrl}/${snapshot.pathWithNamespace.split('/').map(encodeURIComponent).join('/')}/-/blob/${snapshot.commitSha}/${selected.path.split('/').map(encodeURIComponent).join('/')}`}
        target="_blank" rel="noreferrer">Open exact file in GitLab ↗</a></p>
      {!readOnly && selectedFileObserved && <button ref={linkButton} onClick={() => setLinking(true)}>Link AeroLink artifact</button>}
      {links.loading && <p>Loading recorded relationships…</p>}{links.error && <p role="alert">{links.error}</p>}
      {links.value && <CodeRelationshipList {...{ api, projectId, readOnly }} onChanged={() => setRefresh(value => value + 1)} items={links.value.items} empty="No LLR link or other AeroLink relationship is recorded for this file." />}
      {links.value && links.value.total > 25 && <div className="codePagination">
        <button disabled={linkPage <= 1} onClick={() => setLinkPage(value => value - 1)}>Previous relationship page</button>
        <span>Page {linkPage} · {links.value.total} relationships</span>
        <button disabled={linkPage * 25 >= links.value.total} onClick={() => setLinkPage(value => value + 1)}>Next relationship page</button>
      </div>}
    </ControlledArtifactInspector> : <ControlledArtifactInspectorEmpty title="file"
      description="Select a file to inspect its exact source and recorded relationships." />}>
      {linkedOnly ? <>{linkedFiles.loading ? <p role="status">Loading linked files…</p> : <ul className="codeTree">{linkedFiles.value?.items.map(file => <li key={file.path}>
        <button aria-pressed={selected?.path === file.path} onClick={() => { setSelected({ path: file.path, name: file.path.split('/').at(-1)!, kind: 'Blob' }); setLinkPage(1) }}>{file.path}</button>
        <small>{file.relationshipCount} relationship(s)</small></li>)}</ul>}
        {linkedFiles.value?.items.length === 0 && <p>No matching linked file at this source snapshot.</p>}
        <div className="codePagination"><button disabled={filePage <= 1} onClick={() => { setFilePage(value => value - 1); setSelected(undefined) }}>Previous linked files</button>
          <span>Page {filePage}{linkedFiles.value ? ` · ${linkedFiles.value.total} linked files` : ''}</span>
          <button disabled={!linkedFiles.value || filePage * 25 >= linkedFiles.value.total} onClick={() => { setFilePage(value => value + 1); setSelected(undefined) }}>Next linked files</button></div>
      </> : <>{tree.loading ? <p role="status">Loading directory…</p> : <ul className="codeTree">{entries?.map(entry => <li key={entry.path}>
        <button disabled={entry.kind !== 'Tree' && entry.kind !== 'Blob'} aria-pressed={selected?.path === entry.path}
          onClick={() => { if (entry.kind === 'Tree') changePath(entry.path); else { setSelected(entry); setLinkPage(1) } }}>
          <span aria-hidden="true">{entry.kind === 'Tree' ? '▸' : '▤'}</span> {entry.name}
        </button><small>{entry.kind === 'Link' || entry.kind === 'Commit' ? 'External target · not followed' : entry.kind === 'Blob' ? 'File' : 'Directory'}</small>
      </li>)}</ul>}
      {entries?.length === 0 && <p>No entries returned in this directory page.</p>}
      <div className="codePagination"><button disabled={cursors.length <= 1} onClick={() => { setCursors(value => value.slice(0, -1)); setSelected(undefined) }}>Previous directory page</button>
        <span>Directory page {cursors.length}</span><button disabled={!nextCursor} onClick={() => { setCursors(value => [...value, nextCursor!]); setSelected(undefined) }}>Next directory page</button></div></>}
    </CodeRegisterFrame>
    {linking && selected && source && selectedFileObserved && <CodeLinkPicker key={selected.path} {...{ api, projectId, releaseId }}
      subject={{ kind: 'File', path: selected.path, parentPath: path, cursor: cursors.at(-1), source }} fixedTarget={fixedTarget}
      onClose={() => setLinking(false)} onSaved={() => { setLinking(false); setRefresh(value => value + 1); onLinked?.() }} />}
  </section>
}
