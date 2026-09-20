import { useCallback, useEffect, useLayoutEffect, useRef, useState, type FormEvent } from 'react'
import { PersonName } from './People'
import { decodeRepositoryResponse } from './repositoryConfiguration'
import { projectConfigurationRepositoryPath } from './routing'
import { useLatestRequest } from './useLatestRequest'

export type CodeSourceSnapshot = {
  id: string; projectId: string; instanceBaseUrl: string; remoteProjectId: number;
  pathWithNamespace: string; commitSha: string; friendlyRef?: string;
  recordedBy: string; recordedAt: string; configurationVersion: number;
}
export type CodeSource = {
  projectId: string; releaseId: string; version: number; selectionEventId?: string;
  snapshot?: CodeSourceSnapshot; capabilities: { canSelect: boolean; sourceSelectionFrozen: boolean };
  demonstration?: { configurationId: string; configurationVersion: number; remoteProjectId: number };
}
type Preview = { reference: string; referenceKind: string; sha: string; configurationVersion: number; selectionVersion: number }
type History = { page: number; total: number; items: {
  id: string; resultingVersion: number; selectedBy: string; selectedAt: string; snapshot: CodeSourceSnapshot;
}[] }

async function readJson<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(url, options)
  const body = await response.json()
  if (!response.ok) throw new Error(body.error ?? 'Source information could not be loaded.')
  return body as T
}

/** A friendly ref is only a preview input. Confirmation always binds the exact server-resolved commit. */
export default function CodeSourcePanel({ api, projectId, releaseId, readOnly, onSource }: {
  api: string; projectId: string; releaseId: string; readOnly: boolean;
  onSource: (source: CodeSource | undefined) => void;
}) {
  // A keyed inner component prevents a previous workspace's selection or dialog surviving navigation.
  return <SourcePanel key={`${api}/${projectId}/${releaseId}`} {...{ api, projectId, releaseId, readOnly, onSource }} />
}

function SourcePanel({ api, projectId, releaseId, readOnly, onSource }: {
  api: string; projectId: string; releaseId: string; readOnly: boolean;
  onSource: (source: CodeSource | undefined) => void;
}) {
  const [source, setSource] = useState<CodeSource>()
  const [reference, setReference] = useState('')
  const [referenceKind, setReferenceKind] = useState('Auto')
  const [preview, setPreview] = useState<Preview>()
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [history, setHistory] = useState<History>()
  const [showHistory, setShowHistory] = useState(false)
  const { begin: beginLoad } = useLatestRequest()
  const commandRequest = useLatestRequest()
  const historyRequest = useLatestRequest()
  const notifySource = useRef(onSource)
  notifySource.current = onSource
  const base = `${api}/api/projects/${encodeURIComponent(projectId)}`
  useLayoutEffect(() => () => notifySource.current(undefined), [])
  const load = useCallback(async (signal?: AbortSignal) => {
    const current = beginLoad()
    setSource(undefined); setPreview(undefined); notifySource.current(undefined)
    try {
      const value = await readJson<CodeSource>(`${base}/code/source?releaseId=${encodeURIComponent(releaseId)}`, { signal })
      if (!current() || signal?.aborted) return
      if (value.projectId !== projectId || value.releaseId !== releaseId) throw new Error('Source belongs to another workspace.')
      setSource(value); notifySource.current(value)
    } catch (failure) {
      if (current() && !signal?.aborted) setError(failure instanceof Error ? failure.message : 'Source unavailable.')
    }
  }, [base, projectId, releaseId, beginLoad])
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort() }, [load])
  const canSelect = !readOnly && source?.capabilities?.canSelect === true

  const resolve = async (event: FormEvent) => {
    event.preventDefault()
    if (!source || !canSelect) return
    const current = commandRequest.begin()
    setBusy(true); setError(''); setPreview(undefined)
    try {
      const config = decodeRepositoryResponse(await readJson<unknown>(`${base}/repository`))
      if (!current()) return
      if (!config?.repository || config.repository.projectId !== projectId || config.repository.status !== 'Verified')
        throw new Error('Verify this project’s GitLab repository before selecting source.')
      const query = new URLSearchParams({ reference: reference.trim(), referenceKind })
      const result = await readJson<{ configurationVersion: number; observation: {
        succeeded: boolean; detail: string; value?: { sha: string; referenceKind: string };
      } }>(`${base}/repository/commit?${query}`)
      if (!current()) return
      if (result.configurationVersion !== config.repository.version) throw new Error('Repository configuration changed. Preview again before confirming.')
      const observation = result.observation
      if (!observation.succeeded || !observation.value) throw new Error(observation.detail || 'GitLab could not resolve this reference.')
      setPreview({ reference: reference.trim(), referenceKind: observation.value.referenceKind,
        sha: observation.value.sha, configurationVersion: config.repository.version, selectionVersion: source.version })
    } catch (failure) { if (current()) setError(failure instanceof Error ? failure.message : 'Source preview failed.') }
    finally { if (current()) setBusy(false) }
  }

  const confirm = async () => {
    if (!preview || !source || !canSelect) return
    const current = commandRequest.begin()
    setBusy(true); setError('')
    try {
      await readJson(`${base}/code/source`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ releaseId, reference: preview.reference, referenceKind: preview.referenceKind,
          previewSha: preview.sha, expectedConfigurationVersion: preview.configurationVersion,
          expectedSelectionVersion: preview.selectionVersion }) })
      if (!current()) return
      setEditing(false); setPreview(undefined); setHistory(undefined); setShowHistory(false)
      await load()
    } catch (failure) {
      if (current()) { setPreview(undefined); setError(failure instanceof Error ? failure.message : 'Source selection failed.'); await load() }
    } finally { if (current()) setBusy(false) }
  }

  const readHistory = async (page: number) => {
    const current = historyRequest.begin()
    setShowHistory(true); setHistory(undefined); setError('')
    try {
      const value = await readJson<History>(`${base}/code/source/history?${new URLSearchParams({ releaseId, page: String(page), pageSize: '10' })}`)
      if (current()) setHistory(value)
    } catch (failure) { if (current()) setError(failure instanceof Error ? failure.message : 'Source history unavailable.') }
  }

  return <section className="codeSourcePanel" aria-label="Build source">
    <div><h2>Build source</h2>{source?.snapshot ? <>
      <p>{source.snapshot.pathWithNamespace} · {source.snapshot.friendlyRef || 'Exact commit'}</p>
      <code>{source.snapshot.commitSha}</code>
      <p>Selection {source.version} · Branch and tag movement does not change this selection.</p>
    </> : <p>{source ? 'No source selected for this build.' : 'Loading selected source…'}</p>}</div>
    <div className="codeSourceActions">
      {canSelect && <button onClick={() => { setEditing(true); setPreview(undefined); setError('') }}>Select source</button>}
      <button onClick={() => void readHistory(1)}>Source history</button>
      <button disabled={busy} onClick={() => { setError(''); void load() }}>Refresh source</button>
      <a href={projectConfigurationRepositoryPath(projectId)}>Repository configuration</a>
    </div>
    {source?.capabilities?.sourceSelectionFrozen && <p>Source selection is frozen for this build.</p>}
    {error && <p role="alert">{error}</p>}
    {editing && canSelect && <form onSubmit={resolve} className="codeSourceForm">
      <label>GitLab branch, tag, or full commit<input value={reference} required disabled={busy}
        onChange={event => { setReference(event.target.value); setPreview(undefined) }} /></label>
      <label>Reference type<select value={referenceKind} disabled={busy}
        onChange={event => { setReferenceKind(event.target.value); setPreview(undefined) }}>
        <option value="Auto">Detect</option><option value="Branch">Branch</option><option value="Tag">Tag</option><option value="Commit">Commit</option>
      </select></label>
      <button disabled={busy || !reference.trim()}>Preview exact commit</button>
      <button type="button" disabled={busy} onClick={() => { setEditing(false); setPreview(undefined) }}>Cancel</button>
      {preview && <div><p>Select this exact commit for the current build?</p><code>{preview.sha}</code>
        <p>Changing source requires GitLab implementation evidence to be confirmed again. Earlier decisions remain in history.</p>
        <button type="button" disabled={busy} onClick={() => void confirm()}>Confirm source selection</button></div>}
    </form>}
    {showHistory && <section aria-label="Source selection history"><h3>Source selection history</h3>
      <button onClick={() => { historyRequest.invalidate(); setShowHistory(false) }}>Close history</button>
      {!history ? <p>Loading history…</p> : <>
        {history.items.length === 0 && <p>No source selection has been recorded.</p>}
        <ol>{history.items.map(item => <li key={item.id}>Selection {item.resultingVersion} · <code>{item.snapshot.commitSha}</code>
          <p>{item.snapshot.friendlyRef} · <PersonName userName={item.selectedBy} /> · {new Date(item.selectedAt).toLocaleString()}</p></li>)}</ol>
        <button disabled={history.page <= 1} onClick={() => void readHistory(history.page - 1)}>Previous history page</button>
        <span>Page {history.page}</span>
        <button disabled={history.page * 10 >= history.total} onClick={() => void readHistory(history.page + 1)}>Next history page</button>
      </>}
    </section>}
  </section>
}
