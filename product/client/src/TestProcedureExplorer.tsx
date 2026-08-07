import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { PersonName } from './People'
import { apiRequest, operationError } from './apiClient'
import { stateLabel } from './presentation'
import type { TestDiscipline } from './TestResultsWorkspace'
// The requirements explorer's stylesheet, imported rather than copied. Browsing a controlled artifact is the
// same job whichever discipline owns it, so the inspector is literally the same one.
import './RequirementsWorkspace.css'
import './TestProcedureExplorer.css'

type Procedure = {
  id: string
  revisionId: string
  displayNumber: string
  title: string
  state: string
  requirementCount: number
  ownerId: string
  objective?: string
  preconditions?: string
  steps?: string
  expectedResult?: string
}
type Revision = {
  id: string; displayNumber: string; revision: number; state: string; authorId: string; createdAt: string
  objective: string; preconditions: string; steps: string; expectedResult: string
  drivenBy: { changeRequest: string; package: string; subjectDisplayNumber: string; action: string }[]
  covers: string[]
}
type History = { id: string; baseNumber: string; title: string; ownerId: string; createdAt: string; revisions: Revision[] }
type Page = { page: number; pageSize: number; totalCount: number; totalPages: number; items: Procedure[] }
type Comment = {
  id: string; body: string; state: string; createdBy: string; createdAt: string; disposition?: string
}

type Tab = 'details' | 'trace' | 'history' | 'discussion'

const scopeOf = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'system' : discipline === 'HighLevelSoftware' ? 'highLevel' : 'lowLevel'
const disciplineLabel = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'System' : discipline === 'HighLevelSoftware' ? 'Software HLR' : 'Software LLR'

/**
 * Every controlled test procedure in this build, browsed the way requirements are browsed.
 *
 * The requirements explorer answers "what does this artifact say, what does it trace to, what happened to it,
 * and what has anybody said about it". Those are the same four questions asked of a procedure, so this is that
 * component's inspector rather than a second one that resembles it — same tabs, same stylesheet, same order.
 *
 * The trace runs the other way, and that is the one real difference: a requirement's trace shows what derives
 * from it, while a procedure's shows the requirements that drive it. A procedure exists because something has
 * to be verified.
 */
export default function TestProcedureExplorer({ api, projectId, releaseId, discipline, buildName, released }: {
  api: string; projectId: string; releaseId: string; discipline: TestDiscipline; buildName: string
  released: boolean
}) {
  const [data, setData] = useState<Page>()
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [selectedId, setSelectedId] = useState('')
  const [tab, setTab] = useState<Tab>('details')
  const [history, setHistory] = useState<History>()
  const [comments, setComments] = useState<Comment[]>([])
  const [error, setError] = useState('')

  const scope = scopeOf(discipline)
  // A page at a time, at the requirements explorer's own default. A build holds hundreds of procedures, and
  // the reader is looking for one of them.
  const load = useCallback(async () => {
    setError('')
    try {
      const response = await fetch(
        `${api}/api/test-procedures?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}` +
        `&search=${encodeURIComponent(query)}&page=${page}&pageSize=25`)
      if (!response.ok) throw new Error(String(response.status))
      setData(await response.json())
    } catch (problem) { setError(operationError(problem, 'The procedure library could not be loaded.')) }
  }, [api, projectId, releaseId, scope, query, page])
  useEffect(() => { void load() }, [load])

  const procedures = data?.items ?? []
  // Keyed on the page rather than the derived array, so the identity stays stable between renders. The
  // history and discussion effects watch this object, and a fresh one every render would refetch forever.
  const selected = useMemo(() => data?.items.find(x => x.id === selectedId), [data, selectedId])

  // Loaded when the tab is opened rather than with the list. A reader browsing forty procedures does not need
  // forty revision histories fetched on their behalf.
  useEffect(() => {
    if (!selected || tab !== 'history') return
    let active = true
    void (async () => {
      try {
        const response = await fetch(`${api}/api/test-procedures/${selected.id}/history?releaseId=${releaseId}`)
        if (response.ok && active) setHistory(await response.json())
      } catch { if (active) setHistory(undefined) }
    })()
    return () => { active = false }
  }, [api, releaseId, selected, tab])

  const loadComments = useCallback(async (procedureId: string) => {
    try {
      const response = await fetch(`${api}/api/test-procedures/${procedureId}/comments`)
      if (response.ok) setComments(await response.json())
    } catch { setComments([]) }
  }, [api])
  // On selection rather than on opening the tab, because the tab wears the count. A number fetched only once
  // somebody looks is a number that is wrong until they do.
  useEffect(() => {
    if (!selected) return
    setComments([])
    void loadComments(selected.id)
  }, [loadComments, selected])

  const addComment = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!selected) return
    const form = event.currentTarget
    const body = String(new FormData(form).get('body'))
    const mentions = [...body.matchAll(/@([a-z0-9._-]+)/gi)].map(match => match[1])
    setError('')
    try {
      await apiRequest(`${api}/api/test-procedures/${selected.id}/comments`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ revisionId: selected.revisionId, body, mentions }),
      })
      form.reset()
      await loadComments(selected.id)
    } catch (problem) { setError(operationError(problem, 'The comment could not be added.')) }
  }

  // The resolve route reads ArtifactComments by identifier alone, so a procedure comment settles through the
  // same endpoint a requirement comment does rather than a second one that behaves almost the same.
  const resolveComment = async (id: string) => {
    if (!selected) return
    const disposition = window.prompt('Disposition or resolution rationale (optional):') ?? ''
    setError('')
    try {
      await apiRequest(`${api}/api/enterprise-requirements/comments/${id}/resolve`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ disposition }),
      })
      await loadComments(selected.id)
    } catch (problem) { setError(operationError(problem, 'The comment could not be resolved.')) }
  }

  const open = (procedure: Procedure) => { setSelectedId(procedure.id); setTab('details'); setHistory(undefined) }

  // A workspace is its own <main>: the shell supplies the navigation and context bar, not the landmark.
  return <main className="procedureExplorer">
    <header className="procedureExplorerHead">
      <div>
        <p className="eyebrow">VERIFICATION / {disciplineLabel(discipline).toUpperCase()}</p>
        <h1>Test Procedure Explorer</h1>
        <p>Every controlled {disciplineLabel(discipline)} procedure {buildName} carries.</p>
      </div>
    </header>
    {error && <div className="workspaceError" role="alert">{error}</div>}

    <label className="procedureFind">Find a procedure
      <input value={query} onChange={event => { setQuery(event.target.value); setPage(1) }}
        placeholder="Number or title" />
    </label>

    <div className="procedureExplorerSplit">
      <section className="procedureList" aria-label="Test procedures">
        {procedures.length === 0
          ? <p className="procedureEmpty">No {disciplineLabel(discipline)} procedures match.</p>
          : procedures.map(procedure => (
            <button type="button" key={procedure.id}
              className={`procedureRow ${procedure.id === selectedId ? 'selected' : ''}`}
              aria-pressed={procedure.id === selectedId}
              onClick={() => open(procedure)}>
              <b>{procedure.displayNumber}</b>
              <span>{procedure.title}</span>
              <small>{procedure.state} · verifies {procedure.requirementCount}</small>
            </button>
          ))}
        <div className="pager">
          <button disabled={(data?.page ?? 1) <= 1} onClick={() => setPage(x => x - 1)}>← Previous</button>
          <span>
            {(data?.totalCount ?? 0) > 0
              ? `${((data?.page ?? 1) - 1) * (data?.pageSize ?? 25) + 1}–` +
                `${Math.min((data?.page ?? 1) * (data?.pageSize ?? 25), data?.totalCount ?? 0)} ` +
                `of ${(data?.totalCount ?? 0).toLocaleString()}`
              : '0 procedures'}
          </span>
          <button disabled={(data?.page ?? 1) >= (data?.totalPages ?? 1)}
            onClick={() => setPage(x => x + 1)}>Next →</button>
        </div>
      </section>

      {selected && (
        <aside className="requirementInspector" aria-label={`${selected.displayNumber} detail`}>
          <div className="inspectorTop">
            <div>
              <b>{selected.displayNumber}</b>
              <span>{selected.title}</span>
            </div>
            <button type="button" className="inspectorClose" onClick={() => setSelectedId('')}
              aria-label="Close procedure detail">×</button>
          </div>
          <div className="inspectorTabs">
            <button className={tab === 'details' ? 'active' : ''} onClick={() => setTab('details')}>Overview</button>
            <button className={tab === 'trace' ? 'active' : ''} onClick={() => setTab('trace')}>Trace &amp; impact</button>
            <button className={tab === 'history' ? 'active' : ''} onClick={() => setTab('history')}>History</button>
            <button className={tab === 'discussion' ? 'active' : ''} onClick={() => setTab('discussion')}>
              Discussion <span>{comments.length}</span>
            </button>
          </div>

          {tab === 'details' && (
            <div className="inspectorBody">
              <dl className="procedureCase">
                <dt>Objective</dt><dd>{selected.objective || 'Not recorded'}</dd>
                <dt>Preconditions</dt><dd>{selected.preconditions || 'None'}</dd>
                <dt>Steps</dt><dd>{selected.steps || 'Not recorded'}</dd>
                <dt>Expected result</dt><dd>{selected.expectedResult || 'Not recorded'}</dd>
                <dt>State</dt><dd>{selected.state}</dd>
                <dt>Owner</dt><dd><PersonName userName={selected.ownerId} /></dd>
              </dl>
            </div>
          )}

          {tab === 'trace' && (
            <div className="inspectorBody">
              {/* The other direction from a requirement's trace: a requirement shows what derives from it, a
                  procedure shows what it exists to verify. */}
              <p className="inspectorNote">
                This procedure verifies {selected.requirementCount} requirement{selected.requirementCount === 1 ? '' : 's'}.
              </p>
              {selected.requirementCount === 0 && (
                <p className="inspectorNote warn">
                  Nothing is verified by this procedure. Either it has not been linked yet, or the requirement it
                  was written against has been retired.
                </p>
              )}
            </div>
          )}

          {tab === 'history' && (
            <div className="inspectorBody">
              {history
                ? <ul className="revisionList">{history.revisions.map(revision => (
                  <li key={revision.id}>
                    <b>{revision.displayNumber}</b>
                    <span>{revision.state} · written by <PersonName userName={revision.authorId} /></span>
                    {revision.drivenBy.length > 0 && (
                      <span className="revisionDriver">
                        {revision.drivenBy.map(driver => `${driver.package} · ${driver.changeRequest}`).join(', ')}
                      </span>
                    )}
                  </li>))}</ul>
                : <p className="inspectorNote">Loading history…</p>}
            </div>
          )}

          {tab === 'discussion' && (
            <div className="inspectorBody discussionPane">
              {!released ? <form onSubmit={addComment}>
                <textarea name="body" required
                  placeholder="Add an attributable comment. Use @username to mention someone." />
                <div className="commentFoot"><button>Add comment</button></div>
              </form> : <div className="traceEmpty">
                <span>Discussion is read-only in released {buildName}.</span>
              </div>}
              {comments.map(comment => (
                <article key={comment.id} className={comment.state.toLowerCase()}>
                  <div>
                    <b><PersonName userName={comment.createdBy} /></b>
                    <span>{new Date(comment.createdAt).toLocaleString()}</span>
                  </div>
                  <p>{comment.body}</p>
                  {comment.disposition && <small>Disposition: {comment.disposition}</small>}
                  <footer>
                    <i>{stateLabel(comment.state)}</i>
                    {comment.state === 'Open' && !released && (
                      <button onClick={() => void resolveComment(comment.id)}>Resolve / disposition</button>
                    )}
                  </footer>
                </article>
              ))}
            </div>
          )}
        </aside>
      )}
    </div>
  </main>
}
