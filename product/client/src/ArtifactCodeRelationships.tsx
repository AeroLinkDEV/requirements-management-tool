import { useEffect, useRef, useState } from 'react'
import CodeRelationshipList from './CodeRelationshipList'
import { MergeRequestRegister, SourceExplorer } from './CodeWorkspace'
import type { CodeSource } from './CodeSourcePanel'
import { codeQuery, useCodeRead, type CodePage, type CodeRelationship } from './codeWorkspaceData'
import './CodeWorkspace.css'

type Props = { api: string; projectId: string; releaseId: string; targetKind: string; targetId: string; readOnly?: boolean }
export default function ArtifactCodeRelationships(props: Props) {
  return <ScopedRelationships key={`${props.projectId}/${props.releaseId}/${props.targetKind}/${props.targetId}`} {...props} />
}
function ScopedRelationships({ api, projectId, releaseId, targetKind, targetId, readOnly = false }: Props) {
  const [page, setPage] = useState(1)
  const [refresh, setRefresh] = useState(0)
  const [includeWithdrawn, setIncludeWithdrawn] = useState(false)
  const [linking, setLinking] = useState(false)
  const addButton = useRef<HTMLButtonElement>(null)
  const wasLinking = useRef(false)
  useEffect(() => {
    if (!linking && wasLinking.current) addButton.current?.focus()
    wasLinking.current = linking
  }, [linking])
  const result = useCodeRead<CodePage<CodeRelationship>>(`${api}/api/projects/${projectId}/code/relationships?${codeQuery({
    releaseId, targetKind, targetId, page, pageSize: 25, includeWithdrawn: includeWithdrawn ? 'true' : 'false',
  })}`, refresh)
  return <section className="artifactCodeRelationships" aria-label="Code relationships for this exact artifact">
    <p>Relationships for this exact controlled target in the selected build. These links do not by themselves count as accepted implementation evidence.</p>
    <div className="codeCommandBar"><label><input type="checkbox" checked={includeWithdrawn}
      onChange={event => { setIncludeWithdrawn(event.target.checked); setPage(1) }} /> Include withdrawn relationships</label>
      <button onClick={() => setRefresh(value => value + 1)}>Refresh code relationships</button>
      {!readOnly && <button ref={addButton} onClick={() => setLinking(true)}>Add code relationship</button>}</div>
    {result.loading && <p role="status">Loading code relationships…</p>}{result.error && <p role="alert">{result.error}</p>}
    {result.value && <CodeRelationshipList {...{ api, projectId, readOnly }} items={result.value.items}
      onChanged={() => setRefresh(value => value + 1)} empty="No code relationship is recorded for this exact target in this build." />}
    <div className="codePagination"><button disabled={page <= 1} onClick={() => setPage(value => value - 1)}>Previous relationships</button>
      <span>Page {page}{result.value ? ` · ${result.value.total} relationships` : ''}</span>
      <button disabled={!result.value || page * 25 >= result.value.total} onClick={() => setPage(value => value + 1)}>Next relationships</button></div>
    {linking && <ArtifactLinkBrowser {...{ api, projectId, releaseId }} fixedTarget={{ kind: targetKind, id: targetId }}
      onClose={() => setLinking(false)} onLinked={() => { setLinking(false); setRefresh(value => value + 1) }} />}
  </section>
}

/** Artifact entry points share the actual Code browsers and link command, retaining their exact target. */
function ArtifactLinkBrowser({ api, projectId, releaseId, fixedTarget, onClose, onLinked }: {
  api: string; projectId: string; releaseId: string; fixedTarget: { kind: string; id: string }; onClose: () => void; onLinked: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null)
  const [view, setView] = useState<'mr' | 'files'>('mr')
  const source = useCodeRead<CodeSource>(`${api}/api/projects/${projectId}/code/source?${codeQuery({ releaseId })}`)
  const current = source.value?.projectId === projectId && source.value.releaseId === releaseId ? source.value : undefined
  const readOnly = current?.capabilities?.canSelect !== true
  useEffect(() => { const element = dialog.current!; element.showModal(); return () => element.close() }, [])
  return <dialog ref={dialog} className="codeLinkDialog codeArtifactBrowser codeWorkspace" aria-label="Choose code relationship"
    onCancel={event => { event.preventDefault(); onClose() }}>
    <header><h2>Link code to this exact artifact</h2><button onClick={onClose} aria-label="Close code browser">Close</button></header>
    <nav className="codeWorkspaceTabs" aria-label="Code relationship type"><button aria-pressed={view === 'mr'} onClick={() => setView('mr')}>Merge requests</button>
      <button aria-pressed={view === 'files'} onClick={() => setView('files')}>Files at selected source</button></nav>
    {source.error && <p role="alert">{source.error}</p>}
    {source.loading && <p role="status">Checking build permissions…</p>}
    {view === 'mr' ? <MergeRequestRegister {...{ api, projectId, releaseId, readOnly, fixedTarget, onLinked }} />
      : <SourceExplorer {...{ api, projectId, releaseId, readOnly, fixedTarget, onLinked }} source={current} />}
  </dialog>
}
