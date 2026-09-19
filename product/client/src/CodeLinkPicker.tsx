import { useEffect, useRef, useState, type FormEvent } from 'react'
import { codeQuery, useCodeRead, type CodePage } from './codeWorkspaceData'
import { decodeRepositoryResponse } from './repositoryConfiguration'
import type { CodeSource } from './CodeSourcePanel'

type Target = { exactIdentityId: string; ownerId: string; revision?: number; display: string; title: string;
  lifecycle: string; eventType?: string; occurredAt?: string; available: boolean }
export type CodeLinkSubject = { kind: 'MergeRequest'; iid: number } | { kind: 'File'; path: string;
  parentPath: string; cursor?: string; source: CodeSource }
type Props = { api: string; projectId: string; releaseId: string; subject: CodeLinkSubject;
  fixedTarget?: { kind: string; id: string }; onClose: () => void; onSaved: () => void }

/** One picker for all exact controlled targets. Switching kind/search/page clears the selection. */
export default function CodeLinkPicker({ api, projectId, releaseId, subject, fixedTarget, onClose, onSaved }: Props) {
  const dialog = useRef<HTMLDialogElement>(null)
  const pending = useRef<AbortController | null>(null)
  const [kind, setKind] = useState(fixedTarget?.kind ?? 'RequirementRevision')
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<Target | undefined>(fixedTarget ? { exactIdentityId: fixedTarget.id,
    ownerId: '', display: 'Current exact artifact', title: '', lifecycle: '', available: true } : undefined)
  const [meaning, setMeaning] = useState('RelatedContext')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const base = `${api}/api/projects/${projectId}`
  const targets = useCodeRead<CodePage<Target>>(fixedTarget ? undefined : `${base}/code/targets?${codeQuery({ releaseId, targetKind: kind, search: query, page, pageSize: 25 })}`)
  const configuration = useCodeRead<unknown>(`${base}/repository`)
  const repository = decodeRepositoryResponse(configuration.value)?.repository
  useEffect(() => {
    const element = dialog.current!
    element.showModal()
    return () => { pending.current?.abort(); element.close() }
  }, [])
  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!selected || !repository || repository.status !== 'Verified' || busy) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError('')
    const form = new FormData(event.currentTarget)
    const body = { releaseId, targetKind: kind, targetId: selected.exactIdentityId, meaning,
      expectedConfigurationVersion: repository.version,
      ...(subject.kind === 'MergeRequest' ? { mergeRequestIid: subject.iid } : {
        sourceSnapshotId: subject.source.snapshot!.id, sourceSelectionEventId: subject.source.selectionEventId,
        commitSha: subject.source.snapshot!.commitSha, path: subject.path, parentPath: subject.parentPath,
        cursor: subject.cursor || null, pageSize: 25,
        startLine: form.get('startLine') ? Number(form.get('startLine')) : null,
        endLine: form.get('endLine') ? Number(form.get('endLine')) : null,
        mergeRequestIid: form.get('mergeRequestIid') ? Number(form.get('mergeRequestIid')) : null,
      }) }
    try {
      const response = await fetch(`${base}/code/relationships/${subject.kind === 'MergeRequest' ? 'merge-requests' : 'files'}`,
        { method: 'POST', signal: controller.signal, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
      const result = await response.json()
      if (!response.ok) throw new Error(result.error ?? 'The relationship could not be recorded. Refresh and try again.')
      if (!controller.signal.aborted) onSaved()
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Relationship unavailable.')
    } finally { if (!controller.signal.aborted) setBusy(false) }
  }
  return <dialog ref={dialog} className="codeLinkDialog" aria-labelledby="code-link-title" onCancel={onClose}>
    <header><h2 id="code-link-title">Link {subject.kind === 'MergeRequest' ? `merge request !${subject.iid}` : subject.path}</h2>
      <button type="button" onClick={onClose} aria-label="Close link picker">Close</button></header>
    <p>A relationship records context. It does not accept implementation evidence or approve a build.</p>
    {fixedTarget ? <p>This relationship targets the exact artifact revision or snapshot opened in the originating Code tab.</p> : <><label>Artifact type<select value={kind} disabled={busy} onChange={event => { setKind(event.target.value); setPage(1); setSelected(undefined); setQuery(''); setSearch('') }}>
      <option value="RequirementRevision">Requirement revision</option><option value="RequirementProposal">Requirement proposal</option>
      <option value="ChangeRequestRevision">Change request revision</option><option value="ProblemReportRevision">Problem Report snapshot</option>
    </select></label>
    {kind === 'ProblemReportRevision' && <p>Recorded historical snapshots. Search by PR number; choose the event and time explicitly.</p>}
    <form className="codeCommandBar" onSubmit={event => { event.preventDefault(); setQuery(search); setPage(1); setSelected(undefined) }}>
      <input aria-label="Search link targets" value={search} maxLength={200} onChange={event => setSearch(event.target.value)} disabled={busy} />
      <button disabled={busy}>Search targets</button></form>
    {targets.loading && <p role="status">Loading exact targets…</p>}{targets.error && <p role="alert">{targets.error}</p>}
    <fieldset disabled={busy}><legend>Choose one exact target</legend>
      <div className="codeTargetRows">{targets.value?.items.map(item => <label key={item.exactIdentityId}>
        <input type="radio" name="codeTarget" checked={selected?.exactIdentityId === item.exactIdentityId} disabled={!item.available}
          onChange={() => setSelected(item)} /><span><b>{item.display}</b><small>{item.title}</small>
          <small>{item.lifecycle}{item.eventType ? ` · ${item.eventType}` : ''}{item.occurredAt ? ` · ${new Date(item.occurredAt).toLocaleString()}` : ''}
            {!item.available && ' · Stored snapshot unavailable'}</small></span></label>)}</div>
      {targets.value?.items.length === 0 && <p>No matching target in this scope.</p>}
    </fieldset>
    <div className="codePagination"><button disabled={busy || page <= 1} onClick={() => { setPage(value => value - 1); setSelected(undefined) }}>Previous targets</button>
      <span>Page {page}{targets.value ? ` · ${targets.value.total} targets` : ''}</span>
      <button disabled={busy || !targets.value || page * 25 >= targets.value.total} onClick={() => { setPage(value => value + 1); setSelected(undefined) }}>Next targets</button></div></>}
    <form onSubmit={save}><label>Relationship<select value={meaning} disabled={busy} onChange={event => setMeaning(event.target.value)}>
      <option value="RelatedContext">Related context</option><option value="Implements">Implements</option><option value="Addresses">Addresses</option>
    </select></label>
      {subject.kind === 'File' && <fieldset disabled={busy}><legend>Optional file context</legend>
        <label>First line<input name="startLine" type="number" min="1" step="1" /></label>
        <label>Last line<input name="endLine" type="number" min="1" step="1" /></label>
        <label>Associated GitLab MR number<input name="mergeRequestIid" type="number" min="1" step="1" /></label></fieldset>}
      {error && <p role="alert">{error}</p>}{configuration.error && <p role="alert">{configuration.error}</p>}
      {!configuration.loading && repository?.status !== 'Verified' && <p>Verify the project repository before recording a link.</p>}
      <button disabled={busy || !selected || targets.loading || repository?.status !== 'Verified'}>{busy ? 'Recording…' : 'Record relationship'}</button>
    </form>
  </dialog>
}
