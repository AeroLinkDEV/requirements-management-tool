import { useState } from 'react'
import CodeRelationshipList from './CodeRelationshipList'
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
  const result = useCodeRead<CodePage<CodeRelationship>>(`${api}/api/projects/${projectId}/code/relationships?${codeQuery({
    releaseId, targetKind, targetId, page, pageSize: 25, includeWithdrawn: includeWithdrawn ? 'true' : 'false',
  })}`, refresh)
  return <section className="artifactCodeRelationships" aria-label="Code relationships for this exact artifact">
    <p>Relationships for this exact controlled target in the selected build. These links do not by themselves count as accepted implementation evidence.</p>
    <div className="codeCommandBar"><label><input type="checkbox" checked={includeWithdrawn}
      onChange={event => { setIncludeWithdrawn(event.target.checked); setPage(1) }} /> Include withdrawn relationships</label>
      <button onClick={() => setRefresh(value => value + 1)}>Refresh code relationships</button></div>
    {result.loading && <p role="status">Loading code relationships…</p>}{result.error && <p role="alert">{result.error}</p>}
    {result.value && <CodeRelationshipList {...{ api, projectId, readOnly }} items={result.value.items}
      onChanged={() => setRefresh(value => value + 1)} empty="No code relationship is recorded for this exact target in this build." />}
    <div className="codePagination"><button disabled={page <= 1} onClick={() => setPage(value => value - 1)}>Previous relationships</button>
      <span>Page {page}{result.value ? ` · ${result.value.total} relationships` : ''}</span>
      <button disabled={!result.value || page * 25 >= result.value.total} onClick={() => setPage(value => value + 1)}>Next relationships</button></div>
  </section>
}
