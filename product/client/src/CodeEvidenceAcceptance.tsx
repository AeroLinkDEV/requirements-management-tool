import { useEffect, useRef, useState, type FormEvent } from 'react'
import type { CodeSource } from './CodeSourcePanel'
import { codeQuery, useCodeRead, type CodePage, type CodeRelationship, type MetadataObservation, type TreePage } from './codeWorkspaceData'
import { decodeRepositoryResponse } from './repositoryConfiguration'

export type EvidenceRequirement = { artifactId: string; revisionId: string; displayNumber: string; statement: string;
  mapping?: { id: string }; evidence?: { selectorVersion: number; supersededLegacyRecordId?: string } }
type Contribution = { kind: string; relationshipId: string; expectedRelationshipVersion: number;
  parentPath?: string; cursor?: string; pageSize?: number; label: string }
type Props = { api: string; projectId: string; releaseId: string; baselineId: string;
  requirement: EvidenceRequirement; onClose: () => void; onSaved: () => void }

export default function CodeEvidenceAcceptance({ api, projectId, releaseId, baselineId, requirement, onClose, onSaved }: Props) {
  const dialog = useRef<HTMLDialogElement>(null)
  const pending = useRef<AbortController | null>(null)
  const [disposition, setDisposition] = useState('GitLabContributions')
  const [contributions, setContributions] = useState<Contribution[]>([])
  const [page, setPage] = useState(1)
  const [file, setFile] = useState<CodeRelationship>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const base = `${api}/api/projects/${projectId}`
  const source = useCodeRead<CodeSource>(`${base}/code/source?${codeQuery({ releaseId })}`)
  const config = useCodeRead<unknown>(`${base}/repository`)
  const repository = decodeRepositoryResponse(config.value)?.repository
  const relationships = useCodeRead<CodePage<CodeRelationship>>(disposition === 'GitLabContributions'
    ? `${base}/code/relationships?${codeQuery({ releaseId, targetKind: 'RequirementRevision', targetId: requirement.revisionId, page, pageSize: 25 })}` : undefined)
  const selectedSource = source.value?.projectId === projectId && source.value.releaseId === releaseId ? source.value : undefined
  const gitLabReady = repository?.projectId === projectId && repository.status === 'Verified'
    && !!selectedSource?.snapshot && !!selectedSource.selectionEventId
  useEffect(() => {
    const element = dialog.current!
    element.showModal()
    return () => { pending.current?.abort(); element.close() }
  }, [])
  const add = (row: CodeRelationship, proof?: { parentPath: string; cursor?: string; pageSize: number }) => {
    setContributions(items => items.some(item => item.kind === row.relationshipKind && item.relationshipId === row.id) || items.length >= 50
      ? items : [...items, { kind: row.relationshipKind, relationshipId: row.id, expectedRelationshipVersion: row.version,
        label: row.relationshipKind === 'File' ? row.path ?? 'File' : `!${row.mergeRequestIid} · ${row.mergeRequestTitleSnapshot ?? 'Merge request'}`, ...proof }])
    setFile(undefined)
  }
  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (busy || (disposition === 'GitLabContributions' && (!gitLabReady || !contributions.length))) return
    const form = new FormData(event.currentTarget)
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError('')
    try {
      const response = await fetch(`${base}/code/evidence`, { method: 'POST', signal: controller.signal,
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
          releaseId, expectedBaselineId: baselineId, requirementArtifactId: requirement.artifactId,
          requirementRevisionId: requirement.revisionId, disposition,
          expectedSelectorVersion: requirement.evidence?.selectorVersion ?? 0,
          expectedLegacyRecordId: requirement.mapping?.id ?? requirement.evidence?.supersededLegacyRecordId ?? null,
          ...(disposition === 'GitLabContributions' ? {
            expectedConfigurationVersion: repository!.version,
            expectedSourceSelectionEventId: selectedSource!.selectionEventId,
            expectedSourceSnapshotId: selectedSource!.snapshot!.id,
            expectedSourceSelectionVersion: selectedSource!.version,
            contributions: contributions.map(({ label: _label, ...item }) => item), noCodeChangeRationale: null,
          } : { contributions: [], noCodeChangeRationale: form.get('rationale') }),
        }) })
      const result = await response.json()
      if (!response.ok) throw new Error(result.error ?? 'Evidence could not be accepted. Close and refresh before retrying.')
      if (!controller.signal.aborted) onSaved()
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Evidence acceptance failed. Refresh to check its status.')
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }
  return <dialog className="codeLinkDialog" ref={dialog} onCancel={event => { event.preventDefault(); onClose() }} aria-labelledby="acceptEvidenceTitle">
    <form onSubmit={save}>
      <header><h2 id="acceptEvidenceTitle">Accept implementation evidence</h2><button type="button" onClick={onClose} aria-label="Close evidence decision">×</button></header>
      <b>{requirement.displayNumber}</b><p>{requirement.statement}</p>
      {(requirement.mapping || requirement.evidence) && <p>This decision explicitly replaces the selected evidence. Earlier records remain in history.</p>}
      <fieldset disabled={busy}><legend>Decision</legend>
        <label><input type="radio" checked={disposition === 'GitLabContributions'} onChange={() => setDisposition('GitLabContributions')} /> GitLab contributions</label>
        <label><input type="radio" checked={disposition === 'NoCodeChangeRequired'} onChange={() => { setDisposition('NoCodeChangeRequired'); setFile(undefined) }} /> No code change required</label>
      </fieldset>
      {disposition === 'NoCodeChangeRequired' ? <label>No-code rationale<textarea name="rationale" required disabled={busy} /></label> : <>
        {selectedSource?.snapshot && <p>Selected source: <code>{selectedSource.snapshot.commitSha}</code></p>}
        {!gitLabReady && <p role="status">Select a build source and verify the project repository before accepting GitLab contributions.</p>}
        {(source.error || config.error || relationships.error) && <p role="alert">{source.error || config.error || relationships.error}</p>}
        <p>Choose implementation relationships for this exact revision. Acceptance checks current GitLab merge results and exact files again.</p>
        {relationships.loading && <p role="status">Loading relationships…</p>}
        {relationships.value?.items.map(row => {
          const selected = contributions.some(item => item.kind === row.relationshipKind && item.relationshipId === row.id)
          const eligible = row.isActive && row.meaning === 'Implements' && (row.relationshipKind === 'MergeRequest'
            || (row.relationshipKind === 'File' && row.sourceSnapshotId === selectedSource?.snapshot?.id))
          return <div key={`${row.relationshipKind}/${row.id}`}><span>{row.relationshipKind === 'File' ? row.path : `!${row.mergeRequestIid} · ${row.mergeRequestTitleSnapshot ?? 'Merge request'}`} · {row.meaning}</span>
            <button type="button" disabled={busy || !gitLabReady || !eligible || selected || contributions.length >= 50}
              onClick={() => row.relationshipKind === 'File' ? setFile(row) : add(row)}>{selected ? 'Selected' : row.relationshipKind === 'File' ? 'Verify file' : 'Select merge request'}</button></div>
        })}
        {relationships.value?.total === 0 && <p>No implementation relationships recorded. Add links from Code before accepting evidence.</p>}
        <div className="codePagination"><button type="button" disabled={busy || page === 1} onClick={() => setPage(value => value - 1)}>Previous relationships</button>
          <span>Page {page} · {relationships.value?.total ?? '…'} relationships</span><button type="button" disabled={busy || !relationships.value || page * 25 >= relationships.value.total} onClick={() => setPage(value => value + 1)}>Next relationships</button></div>
        {file && selectedSource?.snapshot && <FileProof key={file.id} {...{ base }} source={selectedSource} row={file} onSelect={proof => add(file, proof)} onCancel={() => setFile(undefined)} />}
        <section aria-label="Selected contributions"><h3>{contributions.length} selected contributions</h3>{contributions.map(item => <div key={`${item.kind}/${item.relationshipId}`}>
          <span>{item.label}</span><button type="button" disabled={busy} aria-label={`Remove ${item.label}`} onClick={() => setContributions(items => items.filter(candidate => candidate !== item))}>Remove</button></div>)}</section>
      </>}
      {error && <p role="alert">{error}</p>}
      <button className="primaryAction" disabled={busy || (disposition === 'GitLabContributions' && (!gitLabReady || !contributions.length))}>{busy ? 'Checking evidence…' : 'Accept evidence decision'}</button>
    </form>
  </dialog>
}

function FileProof({ base, source, row, onSelect, onCancel }: { base: string; source: CodeSource; row: CodeRelationship;
  onSelect: (proof: { parentPath: string; cursor?: string; pageSize: number }) => void; onCancel: () => void }) {
  const [cursors, setCursors] = useState<string[]>([])
  const parentPath = row.path?.includes('/') ? row.path.slice(0, row.path.lastIndexOf('/')) : ''
  const cursor = cursors.at(-1)
  const tree = useCodeRead<MetadataObservation<TreePage>>(`${base}/code/source/${source.snapshot!.id}/tree?${codeQuery({ commit: source.snapshot!.commitSha, path: parentPath, cursor, pageSize: 50 })}`)
  const observed = tree.value?.observation.succeeded && tree.value.observation.value?.commitSha === source.snapshot!.commitSha
  const found = observed && tree.value?.observation.value?.entries.some(entry => entry.path === row.path && entry.kind === 'Blob')
  const next = observed ? tree.value?.observation.value?.nextCursor : undefined
  return <section aria-label="Verify exact file"><h3>{row.path}</h3>
    {tree.loading && <p role="status">Checking exact source file…</p>}
    {tree.error && <p role="alert">{tree.error}</p>}
    {tree.value && <p>{found ? 'Exact file observed at the selected source.' : observed ? 'File not present on this page. Inspect the next page if available.' : tree.value.observation.detail}</p>}
    <button type="button" disabled={!found} onClick={() => onSelect({ parentPath, cursor, pageSize: 50 })}>Use verified file</button>
    <button type="button" disabled={!next || cursors.includes(next)} onClick={() => { if (next) setCursors(items => [...items, next]) }}>Next file page</button>
    <button type="button" onClick={onCancel}>Cancel file selection</button>
  </section>
}
